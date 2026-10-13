using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

public record RunResult(bool Victory, int Act, int Floor, int Hp, string EndedIn, string Error = null);

/// Plays one full run (map, combats, events, shops, rest sites, treasure, rewards, act changes) headless,
/// asking an IAgent for every decision. Must run inside AsyncPump.
public class RunDriver(IAgent agent, bool log = false, bool godMode = false, string character = "IRONCLAD", int maxCombatTurns = 100, int stopAfterAct = 0)
{
    public RunState State => _state;
    class TurnLimitException() : Exception("combat turn limit");
    static RunManager Rm => RunManager.Instance;
    RunState _state;
    Player Me => _state.Players[0];

    /// Card choices the game requests from inside its own async code (card rewards, Armaments, Smith, ...).
    sealed class Selector(IAgent agent) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
            Task.FromResult(agent.ChooseCards(options.ToList(), minSelect, maxSelect));
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            int i = agent.ChooseCardReward(options, alternatives);
            if (i >= 0) return new CardRewardSelection { card = options[i].Card };
            if (i <= -2) return new CardRewardSelection { alternative = alternatives[-i - 2] };
            return default;
        }
    }

    void Log(string s) { if (log) Console.WriteLine(s); }

    void InstallChoosers()
    {
        GodotStubs.RelicChooser = relics => { int i = agent.ChooseRelic(relics, canSkip: true); Log($"[relic choice] {(i < 0 ? "skip" : relics[i].Id.Entry)}"); return i; };
        GodotStubs.BundleChooser = bundles => { int i = agent.ChooseBundle(bundles); Log($"[bundle choice] {string.Join(",", bundles[i].Select(c => c.Id.Entry))}"); return i; };
    }

    static bool ManuallyUsable(PotionModel p, bool inCombat) =>
        p.PassesCustomUsabilityCheck && (p.Usage == MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.AnyTime
                                          || (inCombat && p.Usage == MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.CombatOnly));

    /// Potion actions available outside combat: use AnyTime potions on yourself, or discard any potion.
    List<CombatAction> PotionActionsOutOfCombat() =>
        Me.Potions.Where(p => ManuallyUsable(p, false) && p.IsValidTarget(Me.Creature)).Select(p => new CombatAction(Potion: p, Target: Me.Creature))
          .Concat(Me.Potions.Select(p => new CombatAction(Potion: p, Discard: true))).ToList();

    async Task DoPotionAction(CombatAction a)
    {
        if (a.Discard) { Log($"[potion] discard {a.Potion!.Id.Entry}"); await PotionCmd.Discard(a.Potion!); }
        else { Log($"[potion] use {a.Potion!.Id.Entry}"); a.Potion!.EnqueueManualUse(a.Target); await Rm.ActionExecutor.FinishedExecutingActions(); }
    }

    /// Extra relics/potions (model ids) granted right after the run starts: testing and curricula.
    public List<string> ExtraRelics = new(), ExtraPotions = new();

    async Task GrantExtras()
    {
        foreach (var id in ExtraRelics)
        {
            var relic = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == id) ?? throw new ArgumentException("unknown relic " + id);
            await RelicCmd.Obtain(relic.ToMutable(), Me);
        }
        foreach (var id in ExtraPotions)
        {
            var potion = ModelDb.AllPotions.FirstOrDefault(r => r.Id.Entry == id) ?? throw new ArgumentException("unknown potion " + id);
            await PotionCmd.TryToProcure(potion.ToMutable(), Me);
        }
    }

    public async Task<RunResult> Play(string seed, int maxSteps = 20000)
    {
        using var selectorScope = CardSelectCmd.UseSelector(new Selector(agent));
        RewardsSet.testSelector = OfferRewards;
        InstallChoosers();
        await StartRun(seed);
        try
        {
            await GrantExtras();
            for (int step = 0; step < maxSteps; step++)
            {
                await AsyncPump.Idle();
                if (agent is RemoteAgent { PendingCommand: { } pc }) throw new ClientCommandException(pc);
                // winning ends with the game killing the player inside the victory room (RunManager.WinRun)
                if (Me.Creature.IsDead) return _state.CurrentRoom?.IsVictoryRoom == true ? Result(true, "victory") : Result(false, "death");
                if (Rm.IsAbandoned) return Result(false, "abandoned");
                if (_actCleared) return Result(true, "act-clear");
                await Step();
            }
            return Result(false, "step-limit");
        }
        catch (TurnLimitException) { return Result(false, "turn-limit"); }
        catch (ClientCommandException) { throw; }
        catch (Exception e) when (e.InnerException is ClientCommandException cce) { throw cce; }
        catch (Exception e) { return Result(false, "error", e.ToString()); }
        finally { RewardsSet.testSelector = null; GodotStubs.RelicChooser = null; GodotStubs.BundleChooser = null; Rm.CleanUp(); }
    }

    /// Content sweep: start a run, force-enter one event/encounter, and play until that room is done.
    public async Task<string> PlayRoom(string seed, AbstractModel model, int maxSteps = 3000)
    {
        using var selectorScope = CardSelectCmd.UseSelector(new Selector(agent));
        RewardsSet.testSelector = OfferRewards;
        InstallChoosers();
        await StartRun(seed);
        try
        {
            await Rm.EnterRoomDebug(model is EventModel ? RoomType.Event : RoomType.Monster, model: model, showTransition: false);
            var room = _state.CurrentRoom;
            for (int step = 0; step < maxSteps; step++)
            {
                await AsyncPump.Idle();
                if (Me.Creature.IsDead) return "death";
                if (Rm.IsAbandoned) return "abandoned";
                // done once the driver would move on to the map from the room we forced (or a room it pushed)
                if (!CombatManager.Instance.IsInProgress && WouldGoToMap()) return "ok";
                await Step();
            }
            return "step-limit";
        }
        catch (TurnLimitException) { return "turn-limit"; }
        catch (Exception e) { return "error: " + e.GetType().Name + " " + (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at MegaCrit")); }
        finally { RewardsSet.testSelector = null; GodotStubs.RelicChooser = null; GodotStubs.BundleChooser = null; Rm.CleanUp(); }
    }

    bool WouldGoToMap() => _state.CurrentRoom switch
    {
        EventRoom => Rm.EventSynchronizer.GetLocalEvent() is not { IsFinished: false },
        CombatRoom c => _rewarded == c && (c.RoomType != RoomType.Boss || _bossDone == c),
        _ => false,
    };

    RunResult Result(bool won, string why, string err = null)
    {
        if (won && why == "act-clear" && _state.CurrentRoom is CombatRoom bossRoom)
            why += $" vs {bossRoom.Encounter.Id.Entry}";
        if (!won && why == "death" && _state.CurrentRoom is CombatRoom cr)
            why += $" in {cr.Encounter.Id.Entry} turn {Me.PlayerCombatState?.TurnNumber ?? _lastTurn}";
        return new(won, _state.CurrentActIndex + 1, _state.TotalFloor, Me.Creature.CurrentHp, why + " @ " + (_state.CurrentRoom?.GetType().Name ?? "-"), err);
    }

    async Task StartRun(string seed)
    {
        GodotStubs.PendingCrystalSphere = null;
        // same act roll as StartRunLobby.BeginRunLocally
        var actRng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed), "act_selection");
        var acts = ActModel.GetRandomList(actRng, UnlockState.all, isMultiplayer: false).ToList();
        var charModel = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry.Equals(character, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown character {character}; one of {string.Join(",", ModelDb.AllCharacters.Select(c => c.Id.Entry))}");
        var player = Player.CreateForNewRun(charModel, UnlockState.all, 1uL);
        _state = RunState.CreateForNewRun(new[] { player }, acts.Select(a => a.ToMutable()).ToList(), Array.Empty<ModifierModel>(), GameMode.Standard, 0, seed);
        LocalContext.NetId = 1uL;
        Rm.SetUpNewSingleplayer(_state, shouldSave: false);
        await Rm.FinalizeStartingRelics();
        Rm.Launch();
        await Rm.EnterAct(0, doTransition: false);
    }

    async Task Step()
    {
        if (CombatManager.Instance.IsInProgress) { await CombatStep(); return; }
        if (GodotStubs.PendingCrystalSphere is MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame sphere)
        {
            if (sphere.IsFinished) GodotStubs.PendingCrystalSphere = null;
            else { await CrystalStep(sphere); return; }
        }
        switch (_state.CurrentRoom)
        {
            case EventRoom when Rm.EventSynchronizer.GetLocalEvent() is { IsFinished: false } ev:
                await EventStep(ev); return;
            case RestSiteRoom rest when _restDone != rest:
                await RestStep(rest); return;
            case MerchantRoom shop when _shopDone != shop:
                await ShopStep(shop); return;
            case TreasureRoom chest when _chestDone != chest:
                await ChestStep(chest); return;
            case CombatRoom won when _rewarded != won:
                // the combat UI (NCombatUi.OnCombatWon) normally triggers this
                _rewarded = won;
                if (won.Encounter.ShouldGiveRewards) await won.OfferRoomEndRewards();
                return;
            case CombatRoom { RoomType: RoomType.Boss } boss when _bossDone != boss:
                _bossDone = boss;
                if (stopAfterAct > 0 && _state.CurrentActIndex + 1 >= stopAfterAct) { _actCleared = true; return; } // episode ends here
                Log($"[act] boss of act {_state.CurrentActIndex + 1} cleared -> next act");
                Rm.ActChangeSynchronizer.SetLocalPlayerReady();
                return;
        }
        await MapStep();
    }

    object _buffedCombat;
    bool _actCleared;
    (object, int) _loggedTurn;
    int _lastTurn;
    AbstractRoom _restDone, _shopDone, _chestDone, _bossDone, _rewarded;

    async Task MapStep()
    {
        var map = _state.Map;
        var visited = _state.VisitedMapCoords;
        IReadOnlyList<MapPoint> next = visited.Count == 0
            ? new[] { map.StartingMapPoint }
            : map.GetPoint(visited[^1])!.Children.OrderBy(p => p.coord.col).ToList();
        if (next.Count == 0) throw new InvalidOperationException($"dead end on map at {visited[^1]} in {_state.CurrentRoom?.GetType().Name}");
        var potionActions = PotionActionsOutOfCombat();
        int choice = agent.ChooseMapAction(next, potionActions);
        if (choice >= next.Count) { await DoPotionAction(potionActions[choice - next.Count]); return; }
        var dest = next[choice];
        Log($"[map] act{_state.CurrentActIndex + 1} floor{_state.TotalFloor} -> {dest.PointType} {dest.coord}  (hp {Me.Creature.CurrentHp}/{Me.Creature.MaxHp}, gold {Me.Gold}, deck {Me.Deck.Cards.Count()})");
        await Rm.EnterMapCoord(dest.coord);
    }

    async Task CombatStep()
    {
        var pcs = Me.PlayerCombatState;
        if (pcs?.Phase != PlayerTurnPhase.Play || Rm.ActionExecutor.IsRunning) return; // game still resolving
        var cs = Me.Creature.CombatState!;
        if (godMode && _buffedCombat != cs)
        {
            // test-only (same trick as the game's AutoSlay): survive everything to exercise the whole run
            _buffedCombat = cs;
            var ctx = new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext();
            await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.PlatingPower>(ctx, Me.Creature, 999m, Me.Creature, null);
            await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.RegenPower>(ctx, Me.Creature, 999m, Me.Creature, null);
        }
        var enemies = cs.HittableEnemies.ToList();
        var legal = new List<CombatAction> { CombatAction.EndTurn };
        foreach (var c in PileType.Hand.GetPile(Me).Cards)
        {
            if (!c.CanPlay()) continue;
            if (c.TargetType == TargetType.AnyEnemy) legal.AddRange(enemies.Select(e => new CombatAction(Card: c, Target: e)));
            else legal.Add(new CombatAction(Card: c));
        }
        foreach (var p in Me.Potions)
        {
            legal.Add(new CombatAction(Potion: p, Discard: true));
            if (!ManuallyUsable(p, inCombat: true)) continue;
            if (p.TargetType == TargetType.AnyEnemy) legal.AddRange(enemies.Select(e => new CombatAction(Potion: p, Target: e)));
            else if (p.TargetType is TargetType.Self or TargetType.AnyPlayer or TargetType.AnyAlly) legal.Add(new CombatAction(Potion: p, Target: Me.Creature));
        }
        if (log && _loggedTurn != (cs, pcs.TurnNumber))
        {
            _loggedTurn = (cs, pcs.TurnNumber);
            string P(MegaCrit.Sts2.Core.Entities.Creatures.Creature c) => string.Join(",", c.Powers.Select(p => $"{p.Id.Entry}{p.Amount}"));
            Log($"  [turn {pcs.TurnNumber}] me {Me.Creature.CurrentHp}/{Me.Creature.MaxHp} blk{Me.Creature.Block} [{P(Me.Creature)}] | " +
                string.Join(" ; ", cs.Enemies.Where(e => e.IsAlive).Select(e => $"{e.Monster?.Id.Entry} {e.CurrentHp}/{e.MaxHp} [{P(e)}]")) +
                $" | E{pcs.Energy} hand[{string.Join(",", PileType.Hand.GetPile(Me).Cards.Select(c => c.Id.Entry + (c.CanPlay() ? "" : "x")))}] draw{PileType.Draw.GetPile(Me).Cards.Count()} disc{PileType.Discard.GetPile(Me).Cards.Count()} exh{PileType.Exhaust.GetPile(Me).Cards.Count()}");
        }
        _lastTurn = pcs.TurnNumber;
        if (pcs.TurnNumber > maxCombatTurns) throw new TurnLimitException(); // e.g. a deck exhausted to nothing never ends
        var a = agent.ChooseCombatAction(legal);
        if (a.IsEndTurn) Rm.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(Me, pcs.TurnNumber));
        else if (a.Card != null) Rm.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(a.Card, a.Target));
        else if (a.Discard) await PotionCmd.Discard(a.Potion!);
        else a.Potion!.EnqueueManualUse(a.Target);
        await Rm.ActionExecutor.FinishedExecutingActions();
    }

    async Task CrystalStep(MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame g)
    {
        var hidden = new List<(int x, int y)>();
        for (int x = 0; x < g.GridSize.X; x++)
            for (int y = 0; y < g.GridSize.Y; y++)
                if (g.cells[x, y].IsHidden) hidden.Add((x, y));
        var (i, big) = agent.ChooseCrystalCell(hidden, g.DivinationCount);
        g.SetTool(big ? MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame.CrystalSphereToolType.Big
                      : MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame.CrystalSphereToolType.Small);
        Log($"[crystal] reveal {hidden[i]} big={big} ({g.DivinationCount} left)");
        await g.CellClicked(g.cells[hidden[i].x, hidden[i].y]);
    }

    async Task EventStep(EventModel ev)
    {
        var opts = ev.CurrentOptions.Where(o => !o.IsLocked).ToList();
        if (opts.Count == 0) throw new InvalidOperationException($"event {ev.Id.Entry} has no unlocked options");
        var o = opts[agent.ChooseEventOption(opts)];
        Log($"[event] {ev.Id.Entry}: {o.TextKey}");
        Rm.EventSynchronizer.ChooseLocalOption(ev.CurrentOptions.ToList().IndexOf(o));
        // wait for the option to resolve, unless it is waiting on us (Crystal Sphere minigame) -> main loop drives it
        var pending = Rm.EventSynchronizer.AwaitPendingOptionTasks();
        while (!pending.IsCompleted && GodotStubs.PendingCrystalSphere == null) await Task.Delay(1);
    }

    async Task RestStep(RestSiteRoom rest)
    {
        var opts = Rm.RestSiteSynchronizer.GetLocalOptions().Where(o => o.IsEnabled).ToList();
        _restDone = rest;
        if (opts.Count == 0) return;
        var o = opts[agent.ChooseRestOption(opts)];
        Log($"[rest] {o.OptionId}");
        await Rm.RestSiteSynchronizer.ChooseLocalOption(Rm.RestSiteSynchronizer.GetLocalOptions().ToList().IndexOf(o));
    }

    async Task ShopStep(MerchantRoom shop)
    {
        var inv = shop.GetLocalInventory();
        var affordable = inv.AllEntries.Where(e => e.IsStocked && e.EnoughGold).ToList();
        var buy = agent.ChooseShopPurchase(affordable);
        if (buy == null) { _shopDone = shop; return; }
        Log($"[shop] buy {buy.GetType().Name} for {buy.Cost}");
        await buy.OnTryPurchaseWrapper(inv);
    }

    async Task ChestStep(TreasureRoom chest)
    {
        _chestDone = chest;
        await chest.DoNormalRewards();
        if (Rm.TreasureRoomRelicSynchronizer.CurrentRelics is { Count: > 0 } relics) // single player: one relic (or none)
        {
            if (agent.TakeTreasureRelic(relics[0])) Rm.TreasureRoomRelicSynchronizer.PickRelicLocally(0);
            else Rm.TreasureRoomRelicSynchronizer.SkipRelicLocally();
        }
        await AsyncPump.Idle();
        await chest.DoExtraRewardsIfNeeded();
        Log("[chest] opened");
    }

    /// RewardsSet.testSelector: the agent decides which offered rewards to take.
    async Task OfferRewards(RewardsSet set)
    {
        set.ThrowInTestIfRewardsNotTaken = false;
        foreach (var r in set.Rewards.ToList())
        {
            // Gold and potions (with a free slot) are never worse to take, and cards are decided on the card
            // screen (which has its own skip) -> no decision. Relics can be bad (forced effects): agent decides.
            bool take = r switch
            {
                GoldReward or CardReward => true,
                PotionReward when Me.HasOpenPotionSlots => true,
                _ => agent.TakeReward(r),
            };
            if (!take) continue;
            if (r is PotionReward pr && !Me.HasOpenPotionSlots)
            {
                var held = Me.Potions.ToList();
                int d = agent.ChoosePotionToReplace(pr.Potion, held);
                if (d < 0) continue;
                Log($"[potion] discard {held[d].Id.Entry} for {pr.Potion.Id.Entry}");
                await PotionCmd.Discard(held[d]);
            }
            Log($"[reward] {r.GetType().Name}");
            await Rm.RewardsSetSynchronizer.SelectLocalReward(r);
        }
        if (!Rm.RewardsSetSynchronizer.IsRewardsSetCompleted(set)) Rm.RewardsSetSynchronizer.SkipLocalRewardsSet();
    }
}
