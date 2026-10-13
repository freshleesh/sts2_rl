using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;

/// One combat decision: play a card (on a target), use a potion, or end the turn.
public record CombatAction(CardModel Card = null, PotionModel Potion = null, Creature Target = null, bool Discard = false)
{
    public bool IsEndTurn => Card == null && Potion == null;
    public static readonly CombatAction EndTurn = new();
}

/// Every decision the game asks of the player during a run. Indices refer to the passed lists.
public interface IAgent
{
    /// index < next.Count: travel there; otherwise potionActions[index - next.Count] (use/discard a potion first)
    int ChooseMapAction(IReadOnlyList<MapPoint> next, IReadOnlyList<CombatAction> potionActions);
    CombatAction ChooseCombatAction(IReadOnlyList<CombatAction> legal);
    IEnumerable<CardModel> ChooseCards(IReadOnlyList<CardModel> options, int min, int max);
    /// card index into options, or -1 to skip / -(2+i) for alternatives[i]
    int ChooseCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives);
    bool TakeReward(Reward reward);
    /// Potion reward with full slots: slot index to discard to make room, or -1 to leave the potion.
    int ChoosePotionToReplace(PotionModel incoming, IReadOnlyList<PotionModel> held);
    /// "Choose a relic" screens (some events/relics). -1 = skip (only when canSkip).
    int ChooseRelic(IReadOnlyList<RelicModel> relics, bool canSkip);
    /// "Choose a bundle of cards" screens.
    int ChooseBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles);
    /// Treasure room: take the chest's relic or leave it.
    bool TakeTreasureRelic(RelicModel relic);
    int ChooseEventOption(IReadOnlyList<EventOption> options);
    int ChooseRestOption(IReadOnlyList<RestSiteOption> options);
    /// Crystal Sphere minigame: which hidden cell to reveal and whether to use the big (3x3) tool.
    (int cellIndex, bool big) ChooseCrystalCell(IReadOnlyList<(int x, int y)> hidden, int divinationsLeft);
    /// entry to buy, or null to leave the shop
    MerchantEntry ChooseShopPurchase(IReadOnlyList<MerchantEntry> affordable);
}

public class RandomAgent(int seed) : IAgent
{
    readonly Random _r = new(seed);
    T Pick<T>(IReadOnlyList<T> xs) => xs[_r.Next(xs.Count)];

    public int ChooseMapAction(IReadOnlyList<MapPoint> next, IReadOnlyList<CombatAction> potionActions) => _r.Next(next.Count);
    public int ChoosePotionToReplace(PotionModel incoming, IReadOnlyList<PotionModel> held) => _r.Next(-1, held.Count);
    public int ChooseRelic(IReadOnlyList<RelicModel> relics, bool canSkip) => _r.Next(relics.Count);
    public int ChooseBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles) => _r.Next(bundles.Count);
    public bool TakeTreasureRelic(RelicModel relic) => true;
    public CombatAction ChooseCombatAction(IReadOnlyList<CombatAction> legal)
    {
        // end turn only when nothing else is playable, so random play isn't hopelessly passive
        var plays = legal.Where(a => !a.IsEndTurn && !a.Discard).ToList();
        return plays.Count > 0 ? Pick(plays) : CombatAction.EndTurn;
    }
    public IEnumerable<CardModel> ChooseCards(IReadOnlyList<CardModel> options, int min, int max)
    {
        int n = _r.Next(min, Math.Min(max, options.Count) + 1);
        return options.OrderBy(_ => _r.Next()).Take(n).ToList();
    }
    public int ChooseCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives) =>
        options.Count > 0 && _r.NextDouble() < 0.8 ? _r.Next(options.Count) : -1;
    public bool TakeReward(Reward reward) => true;
    public int ChooseEventOption(IReadOnlyList<EventOption> options) => _r.Next(options.Count);
    public int ChooseRestOption(IReadOnlyList<RestSiteOption> options) => _r.Next(options.Count);
    public (int cellIndex, bool big) ChooseCrystalCell(IReadOnlyList<(int x, int y)> hidden, int divinationsLeft) => (_r.Next(hidden.Count), _r.Next(2) == 0);
    public MerchantEntry ChooseShopPurchase(IReadOnlyList<MerchantEntry> affordable) =>
        affordable.Count > 0 && _r.NextDouble() < 0.5 ? Pick(affordable) : null;
}
