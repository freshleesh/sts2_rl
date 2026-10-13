"""Play episodes with a checkpoint and summarize what the policy actually does.
Usage: python3 eval_behavior.py <ckpt> [episodes] [--greedy]"""
import collections, random, sys, os
import torch
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import featurize as fz, model
from sts2env import Sts2Env

ckpt, n = sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 30
greedy = "--greedy" in sys.argv
torch.set_num_threads(2)
net = model.Policy(); net.load_state_dict(torch.load(ckpt, map_location="cpu", weights_only=False)["model"]); net.eval()
wins = 0
C = collections.Counter(); deaths = collections.Counter(); floors = []; decks = []
picked = collections.Counter(); offered = collections.Counter(); maps = collections.Counter(); rests = collections.Counter()
with Sts2Env(stop_after_act=1) as env:
    for k in range(n):
        obs, _ = env.reset(seed=f"EVAL{k}"); done = False
        while not done:
            with torch.no_grad():
                logits, _ = net(model.collate([fz.featurize(obs)]))
            a = int(logits[0].argmax()) if greedy else int(torch.distributions.Categorical(logits=logits[0]).sample())
            kind, opts, st = obs["kind"], obs["options"], obs["state"]
            o = opts[a]
            if kind == "combat":
                cb = st["combat"]; playable = [x for x in opts if x.get("action") == "play"]
                C["combat"] += 1
                if o["action"] == "end_turn":
                    C["end_turn"] += 1
                    if playable: C["end_turn_with_playable"] += 1
                    if cb["energy"] > 0 and playable: C["end_turn_with_energy_and_playable"] += 1
                elif o["action"] == "potion": C["potion_use"] += 1
                elif o["action"] == "discard_potion": C["potion_discard"] += 1
                if any(x.get("action") == "potion" for x in opts): C["combat_with_usable_potion"] += 1
            elif kind == "card_reward":
                C["card_reward"] += 1
                for x in opts:
                    if "id" in x and "type" in x: offered[x["id"]] += 1
                if o.get("action") == "skip": C["card_reward_skip"] += 1
                elif "id" in o: picked[o["id"]] += 1
            elif kind == "map" and o.get("action") == "travel": maps[o["type"]] += 1
            elif kind == "rest": rests[o["id"]] += 1
            elif kind == "shop": C["shop_decisions"] += 1; C["shop_leave"] += o.get("action") == "leave"
            elif kind == "reward": C["reward"] += 1; C["reward_skip"] += o.get("action") == "skip"
            obs, r, te, tr, info = env.step(a); done = te or tr
            if not done: last = obs["state"]
        e = info["end"]; wins += e["victory"]; floors.append(e["floor"]); decks.append(len(last["deck"]))
        deaths[e["reason"].split(" turn")[0].replace("death in ", "").split(" @")[0]] += 1
print(f"{'greedy' if greedy else 'sampled'} policy, {n} runs: avg floor {sum(floors)/n:.1f}, clears {wins} (reached boss floor: {sum(f>=17 for f in floors)}), avg final deck {sum(decks)/n:.1f}")
print("ended by:", deaths.most_common(8))
pct = lambda a, b: f"{100*C[a]/max(C[b],1):.0f}%"
print(f"combat: end_turn {pct('end_turn','combat')} of combat decisions; ends turn with playable cards {pct('end_turn_with_playable','end_turn')}, with energy+playable {pct('end_turn_with_energy_and_playable','end_turn')}")
print(f"potions: used {C['potion_use']}, discarded {C['potion_discard']} (decisions with a usable potion: {C['combat_with_usable_potion']})")
print(f"card rewards: skip {pct('card_reward_skip','card_reward')}; picked: {picked.most_common(10)}")
print(f"rewards skipped {pct('reward_skip','reward')}; shop leave {pct('shop_leave','shop_decisions')}")
print("map choices:", maps.most_common()); print("rest choices:", rests.most_common())
