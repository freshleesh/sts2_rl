using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

/// JSON-ready snapshots of the game state for the Python side (plain dictionaries/lists only).
public static class Obs
{
    public static object Card(CardModel c) => new Dictionary<string, object>
    {
        ["id"] = c.Id.Entry,
        ["upgraded"] = c.IsUpgraded,
        ["cost"] = c.EnergyCost.CostsX ? -1 : c.EnergyCost.GetWithModifiers(CostModifiers.All),
        ["type"] = c.Type.ToString(),
        ["target"] = c.TargetType.ToString(),
    };

    public static Dictionary<string, int> Powers(Creature c) =>
        c.Powers.GroupBy(p => p.Id.Entry).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

    static object Intent(AbstractIntent i, Creature owner, IEnumerable<Creature> targets)
    {
        var d = new Dictionary<string, object> { ["type"] = i.IntentType.ToString() };
        if (i is AttackIntent a)
        {
            try { d["damage"] = a.GetSingleDamage(targets, owner); d["hits"] = System.Math.Max(1, a.Repeats); }
            catch { /* some intents compute damage lazily from state that may be gone */ }
        }
        return d;
    }

    public static object Enemy(Creature e, IReadOnlyList<Creature> players) => new Dictionary<string, object>
    {
        ["id"] = e.Monster?.Id.Entry,
        ["hp"] = e.CurrentHp,
        ["max_hp"] = e.MaxHp,
        ["block"] = e.Block,
        ["powers"] = Powers(e),
        ["intents"] = e.Monster?.NextMove?.Intents?.Select(i => Intent(i, e, players)).ToList() ?? new List<object>(),
    };

    public static object State(RunState s)
    {
        var me = s.Players[0];
        var d = new Dictionary<string, object>
        {
            ["character"] = me.Character.Id.Entry,
            ["act"] = s.CurrentActIndex + 1,
            ["floor"] = s.TotalFloor,
            ["room"] = s.CurrentRoom?.GetType().Name,
            ["hp"] = me.Creature.CurrentHp,
            ["max_hp"] = me.Creature.MaxHp,
            ["gold"] = me.Gold,
            ["deck"] = me.Deck.Cards.Select(Card).ToList(),
            ["relics"] = me.Relics.Select(r => r.Id.Entry).ToList(),
            ["potions"] = me.PotionSlots.Select(p => p?.Id.Entry).ToList(),
        };
        var pcs = me.PlayerCombatState;
        var cs = me.Creature.CombatState;
        if (pcs != null && cs != null && MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
        {
            var players = cs.PlayerCreatures.ToList();
            d["combat"] = new Dictionary<string, object>
            {
                ["turn"] = pcs.TurnNumber,
                ["energy"] = pcs.Energy,
                ["block"] = me.Creature.Block,
                ["powers"] = Powers(me.Creature),
                ["hand"] = PileType.Hand.GetPile(me).Cards.Select(Card).ToList(),
                ["draw_pile"] = PileType.Draw.GetPile(me).Cards.Select(c => c.Id.Entry).OrderBy(x => x).ToList(), // order hidden from the player
                ["discard_pile"] = PileType.Discard.GetPile(me).Cards.Select(c => c.Id.Entry).ToList(),
                ["exhaust_count"] = PileType.Exhaust.GetPile(me).Cards.Count(),
                ["enemies"] = cs.Enemies.Where(e => e.IsAlive).Select(e => Enemy(e, players)).ToList(),
            };
        }
        return d;
    }
}
