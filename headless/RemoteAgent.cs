using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

/// Thrown out of a decision when the client sends a new command (reset/quit) mid-run.
public class ClientCommandException(JsonElement cmd) : Exception("client command")
{
    public JsonElement Command { get; } = cmd;
}

/// IAgent that asks an external client (Python) for every decision over a line-based JSON protocol.
/// The simulator runs on a single thread, so blocking on the reply simply pauses the game.
public class RemoteAgent(TextReader input, TextWriter output) : IAgent
{
    public Func<RunState> State;
    /// Set when the client sent a command mid-run. Exceptions thrown inside game callbacks can be swallowed by the
    /// game, so the driver also polls this flag.
    public JsonElement? PendingCommand { get; private set; }
    readonly List<CardModel> _hand = new();

    int Ask(string kind, List<object> options, Dictionary<string, object> extra = null)
    {
        if (PendingCommand is { } pc) throw new ClientCommandException(pc);
        if (options.Count == 0) throw new InvalidOperationException($"decision '{kind}' with no options");
        var msg = new Dictionary<string, object> { ["type"] = "decision", ["kind"] = kind, ["options"] = options, ["state"] = Obs.State(State()) };
        if (extra != null) foreach (var (k, v) in extra) msg[k] = v;
        output.WriteLine(JsonSerializer.Serialize(msg));
        output.Flush();
        while (true)
        {
            var line = input.ReadLine() ?? "{\"cmd\":\"quit\"}";
            var doc = JsonDocument.Parse(line).RootElement.Clone();
            if (doc.TryGetProperty("cmd", out _)) { PendingCommand = doc; throw new ClientCommandException(doc); }
            int a = doc.GetProperty("action").GetInt32();
            if (a >= 0 && a < options.Count) return a;
            output.WriteLine(JsonSerializer.Serialize(new { type = "error", message = $"action {a} out of range [0,{options.Count})" }));
            output.Flush();
        }
    }

    public int ChooseMapAction(IReadOnlyList<MapPoint> next, IReadOnlyList<CombatAction> potionActions)
    {
        var slots = State().Players[0].PotionSlots.ToList();
        var opts = next.Select(p => (object)new Dictionary<string, object> { ["action"] = "travel", ["type"] = p.PointType.ToString(), ["col"] = p.coord.col, ["row"] = p.coord.row }).ToList();
        opts.AddRange(potionActions.Select(a => (object)new Dictionary<string, object> { ["action"] = a.Discard ? "discard_potion" : "potion", ["potion"] = a.Potion!.Id.Entry, ["slot"] = slots.IndexOf(a.Potion) }));
        return Ask("map", opts);
    }

    public int ChoosePotionToReplace(PotionModel incoming, IReadOnlyList<PotionModel> held)
    {
        var opts = held.Select(p => (object)new { action = "discard_for", potion = p.Id.Entry }).ToList();
        opts.Add(new { action = "leave" });
        int i = Ask("potion_full", opts, new() { ["incoming"] = incoming.Id.Entry });
        return i < held.Count ? i : -1;
    }

    public int ChooseRelic(IReadOnlyList<RelicModel> relics, bool canSkip)
    {
        var opts = relics.Select(r => (object)new { action = "take", relic = r.Id.Entry }).ToList();
        if (canSkip) opts.Add(new { action = "skip" });
        int i = Ask("choose_relic", opts);
        return i < relics.Count ? i : -1;
    }

    public int ChooseBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles) =>
        Ask("choose_bundle", bundles.Select(b => (object)b.Select(Obs.Card).ToList()).ToList());

    public bool TakeTreasureRelic(RelicModel relic) =>
        Ask("treasure", new() { new { action = "take", relic = relic.Id.Entry }, new { action = "skip" } }) == 0;

    public CombatAction ChooseCombatAction(IReadOnlyList<CombatAction> legal)
    {
        var me = State().Players[0];
        var hand = PileType.Hand.GetPile(me).Cards.ToList();
        var enemies = me.Creature.CombatState!.Enemies.Where(e => e.IsAlive).ToList();
        var potions = me.PotionSlots.ToList();
        var opts = legal.Select(a => (object)(a.IsEndTurn ? new Dictionary<string, object> { ["action"] = "end_turn" }
            : a.Discard ? new Dictionary<string, object> { ["action"] = "discard_potion", ["potion"] = a.Potion!.Id.Entry, ["slot"] = potions.IndexOf(a.Potion) }
            : a.Card != null ? new Dictionary<string, object> { ["action"] = "play", ["card"] = a.Card.Id.Entry, ["hand_index"] = hand.IndexOf(a.Card), ["target"] = a.Target == null ? -1 : enemies.IndexOf(a.Target) }
            : new Dictionary<string, object> { ["action"] = "potion", ["potion"] = a.Potion!.Id.Entry, ["slot"] = potions.IndexOf(a.Potion), ["target"] = a.Target == null || a.Target == me.Creature ? -1 : enemies.IndexOf(a.Target) })).ToList();
        return legal[Ask("combat", opts)];
    }

    /// Multi-select is asked one card at a time: pick a card, or "done" once at least `min` are picked.
    public IEnumerable<CardModel> ChooseCards(IReadOnlyList<CardModel> options, int min, int max)
    {
        var picked = new List<CardModel>();
        max = Math.Min(max, options.Count);
        while (picked.Count < max)
        {
            var remaining = options.Where(c => !picked.Contains(c)).ToList();
            var opts = remaining.Select(Obs.Card).ToList();
            bool canStop = picked.Count >= min;
            if (canStop) opts.Add(new { action = "done" });
            int i = Ask("select_cards", opts, new() { ["min"] = min, ["max"] = max, ["picked"] = picked.Select(Obs.Card).ToList() });
            if (canStop && i == remaining.Count) break;
            picked.Add(remaining[i]);
        }
        return picked;
    }

    public int ChooseCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        var opts = options.Select(o => Obs.Card(o.Card)).ToList();
        opts.AddRange(alternatives.Select(a => (object)new { action = "alternative", id = a.OptionId }));
        opts.Add(new { action = "skip" });
        int i = Ask("card_reward", opts);
        return i < options.Count ? i : i < options.Count + alternatives.Count ? -(2 + i - options.Count) : -1;
    }

    public bool TakeReward(Reward reward)
    {
        var info = RewardInfo(reward);
        return Ask("reward", new() { new Dictionary<string, object>(info) { ["action"] = "take" }, new Dictionary<string, object> { ["action"] = "skip" } },
                   new() { ["reward"] = info }) == 0;
    }

    static Dictionary<string, object> RewardInfo(Reward r) => r switch
    {
        GoldReward => new() { ["kind"] = "gold" },
        PotionReward p => new() { ["kind"] = "potion", ["potion"] = p.Potion.Id.Entry },
        RelicReward rr => new() { ["kind"] = "relic", ["relic"] = rr.Relic?.Id.Entry },
        CardReward => new() { ["kind"] = "card" },
        _ => new() { ["kind"] = r.GetType().Name },
    };

    public int ChooseEventOption(IReadOnlyList<EventOption> options) =>
        Ask("event", options.Select(o => (object)new { key = o.TextKey, relic = o.Relic?.Id.Entry, proceed = o.IsProceed }).ToList());

    public int ChooseRestOption(IReadOnlyList<RestSiteOption> options) =>
        Ask("rest", options.Select(o => (object)new { id = o.OptionId }).ToList());

    public (int cellIndex, bool big) ChooseCrystalCell(IReadOnlyList<(int x, int y)> hidden, int divinationsLeft)
    {
        var opts = hidden.SelectMany(c => new object[] { new { x = c.x, y = c.y, tool = "small" }, new { x = c.x, y = c.y, tool = "big" } }).ToList();
        int i = Ask("crystal_sphere", opts, new() { ["divinations_left"] = divinationsLeft });
        return (i / 2, i % 2 == 1);
    }

    public MerchantEntry ChooseShopPurchase(IReadOnlyList<MerchantEntry> affordable)
    {
        var opts = affordable.Select(e => (object)new Dictionary<string, object>
        {
            ["action"] = "buy", ["cost"] = e.Cost,
            ["item"] = e switch
            {
                MerchantCardEntry c => "card:" + c.CreationResult.Card.Id.Entry,
                MerchantRelicEntry r => "relic:" + r.Model.Id.Entry,
                MerchantPotionEntry p => "potion:" + p.Model.Id.Entry,
                _ => "card_removal",
            },
        }).ToList();
        opts.Add(new Dictionary<string, object> { ["action"] = "leave" });
        int i = Ask("shop", opts);
        return i < affordable.Count ? affordable[i] : null;
    }
}
