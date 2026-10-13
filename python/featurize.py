"""Turns a decision observation (JSON dict from the simulator) into fixed-width numpy arrays.

State = token sets (deck, relics, potions, hand, draw, discard, enemies) + a global feature vector.
Actions = one feature row per legal option, with optional pointers into the hand / enemy tokens.
String ids (cards, relics, monsters, powers, event keys...) go through a hash embedding: no vocabulary file,
and ids never seen before still get a (shared-bucket) embedding.
"""
import zlib
import numpy as np

N_BUCKETS = 8192
KINDS = ["map", "combat", "select_cards", "card_reward", "reward", "event", "rest", "shop", "crystal_sphere",
         "potion_full", "choose_relic", "choose_bundle", "treasure", "end"]
ACTIONS = ["travel", "potion", "discard_potion", "end_turn", "play", "done", "alternative", "skip", "take",
           "discard_for", "leave", "buy", "card", "bundle", "event", "rest", "crystal", "other"]
CARD_TYPES = ["Attack", "Skill", "Power", "Status", "Curse", "Quest", "None"]
INTENTS = ["Attack", "Buff", "Debuff", "Defend", "Heal", "Escape", "Sleep", "Stun", "Summon", "Status",
           "CardDebuff", "DeathBlow", "Hidden", "Unknown", "None"]
MAP_TYPES = ["Monster", "Elite", "Boss", "Unknown", "RestSite", "Shop", "Treasure", "Ancient", "Unassigned"]
REWARD_KINDS = ["gold", "potion", "relic", "card"]

CARD_F = 3 + len(CARD_TYPES)            # upgraded, cost, is_x_cost, type one-hot
ENEMY_F = 6 + len(INTENTS)              # hp, max_hp, hp_frac, block, intent dmg total, n_intents, intent one-hot (multi)
GLOBAL_F = len(KINDS) + 14
OPT_F = len(ACTIONS) + len(MAP_TYPES) + len(REWARD_KINDS) + CARD_F + 8
MAX_POWERS = 12


def h(s) -> int:
    return 1 + zlib.crc32(str(s).encode()) % (N_BUCKETS - 1)  # 0 = padding


def onehot(v, vocab):
    x = np.zeros(len(vocab), np.float32)
    if v in vocab:
        x[vocab.index(v)] = 1
    return x


def card_feat(c):
    cost = c.get("cost", 0)
    return np.concatenate([[float(c.get("upgraded", False)), max(cost, 0) / 3.0, float(cost < 0)],
                           onehot(c.get("type"), CARD_TYPES)]).astype(np.float32)


def powers_tokens(powers: dict):
    items = list(powers.items())[:MAX_POWERS]
    ids = np.zeros(MAX_POWERS, np.int64); amt = np.zeros(MAX_POWERS, np.float32)
    for i, (k, v) in enumerate(items):
        ids[i] = h(k); amt[i] = np.sign(v) * np.log1p(abs(v))
    return ids, amt


def featurize(obs: dict) -> dict:
    st = obs["state"] or {}
    combat = st.get("combat")
    out = {}

    def cards(lst, key):
        out[key + "_ids"] = np.array([h(c["id"] if isinstance(c, dict) else c) for c in lst], np.int64)
        out[key + "_f"] = (np.stack([card_feat(c) for c in lst]) if lst and isinstance(lst[0], dict)
                           else np.zeros((len(lst), CARD_F), np.float32))

    cards(st.get("deck", []), "deck")
    out["relic_ids"] = np.array([h(r) for r in st.get("relics", [])], np.int64)
    out["potion_ids"] = np.array([h(p) for p in st.get("potions", []) if p], np.int64)
    cards(combat["hand"] if combat else [], "hand")
    cards(combat["draw_pile"] if combat else [], "draw")
    cards(combat["discard_pile"] if combat else [], "discard")

    enemies = combat["enemies"] if combat else []
    out["enemy_ids"] = np.array([h(e["id"]) for e in enemies], np.int64)
    ef = np.zeros((len(enemies), ENEMY_F), np.float32)
    epw_ids = np.zeros((len(enemies), MAX_POWERS), np.int64); epw_amt = np.zeros((len(enemies), MAX_POWERS), np.float32)
    for i, e in enumerate(enemies):
        dmg = sum(it.get("damage", 0) * it.get("hits", 1) for it in e["intents"])
        intents = np.zeros(len(INTENTS), np.float32)
        for it in e["intents"]:
            intents += onehot(it["type"], INTENTS)
        ef[i, :6] = [e["hp"] / 100, e["max_hp"] / 100, e["hp"] / max(e["max_hp"], 1), e["block"] / 20, dmg / 30, len(e["intents"]) / 3]
        ef[i, 6:] = intents
        epw_ids[i], epw_amt[i] = powers_tokens(e["powers"])
    out["enemy_f"], out["enemy_pw_ids"], out["enemy_pw_amt"] = ef, epw_ids, epw_amt
    out["my_pw_ids"], out["my_pw_amt"] = powers_tokens(combat["powers"] if combat else {})

    g = np.zeros(GLOBAL_F, np.float32)
    g[:len(KINDS)] = onehot(obs["kind"], KINDS)
    k = len(KINDS)
    if st:
        g[k:k + 8] = [st["hp"] / 100, st["max_hp"] / 100, st["hp"] / max(st["max_hp"], 1), st["gold"] / 300,
                      st["floor"] / 17, st["act"] / 4, len(st["deck"]) / 30, sum(p is not None for p in st["potions"]) / 3]
    if combat:
        g[k + 8:k + 14] = [combat["energy"] / 3, combat["turn"] / 10, combat["block"] / 20, len(combat["draw_pile"]) / 20,
                           len(combat["discard_pile"]) / 20, combat["exhaust_count"] / 10]
    out["global"] = g

    # --- options ---------------------------------------------------------------------------------------------
    opts = obs["options"]
    of = np.zeros((len(opts), OPT_F), np.float32)
    oid = np.zeros(len(opts), np.int64)        # main entity id of the option (card / relic / potion / event key ...)
    hand_ptr = np.full(len(opts), -1, np.int64)
    enemy_ptr = np.full(len(opts), -1, np.int64)
    bundle_ids = np.zeros((len(opts), 4), np.int64)
    A, M, R = len(ACTIONS), len(MAP_TYPES), len(REWARD_KINDS)
    for i, o in enumerate(opts):
        if isinstance(o, list):                                   # choose_bundle: a list of cards
            act = "bundle"
            for j, c in enumerate(o[:4]):
                bundle_ids[i, j] = h(c["id"])
        elif "action" in o:
            act = o["action"]
        elif "id" in o and "type" in o:                           # a bare card (card reward / select / bundle)
            act = "card"
        elif "key" in o:
            act = "event"
        elif "x" in o:
            act = "crystal"
        elif "id" in o:
            act = "rest"
        else:
            act = "other"
        of[i, :A] = onehot(act if act in ACTIONS else "other", ACTIONS)
        if isinstance(o, dict):
            if act == "travel":
                of[i, A:A + M] = onehot(o["type"], MAP_TYPES)
            if obs["kind"] == "reward" and "reward" in obs:
                of[i, A + M:A + M + R] = onehot(obs["reward"]["kind"], REWARD_KINDS)
            if act == "card":
                of[i, A + M + R:A + M + R + CARD_F] = card_feat(o)
            base = A + M + R + CARD_F
            of[i, base:base + 8] = [o.get("col", 0) / 7, o.get("row", 0) / 16, o.get("x", 0) / 11, o.get("y", 0) / 11,
                                    float(o.get("tool") == "big"), o.get("cost", 0) / 300, float(o.get("proceed", False)),
                                    float(isinstance(o.get("target"), int) and o["target"] >= 0)]
            ident = (o.get("card") or o.get("potion") or o.get("relic") or o.get("key") or o.get("item")
                     or (o.get("id") if act in ("card", "rest", "alternative") else None))
            if ident:
                oid[i] = h(ident)
            if o.get("hand_index", -1) >= 0:
                hand_ptr[i] = o["hand_index"]
            if isinstance(o.get("target"), int) and o["target"] >= 0:  # (cards carry a TargetType string instead)
                enemy_ptr[i] = o["target"]
    out.update(opt_f=of, opt_id=oid, opt_hand=hand_ptr, opt_enemy=enemy_ptr, opt_bundle=bundle_ids)
    return out
