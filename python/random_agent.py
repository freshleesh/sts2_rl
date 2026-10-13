"""Plays random runs through the Python API: sanity check + throughput. Usage: python3 random_agent.py [runs] [procs]"""
import random
import sys
import time
from concurrent.futures import ProcessPoolExecutor

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from sts2env import Sts2Env


def worker(args):
    wid, runs = args
    rng = random.Random(wid)
    stats = {"runs": 0, "wins": 0, "floors": 0, "decisions": 0, "reasons": {}, "kinds": {}}
    with Sts2Env() as env:
        for k in range(runs):
            obs, info = env.reset(seed=f"PY{wid}_{k}", character=rng.choice(env.characters))
            done = False
            while not done:
                stats["kinds"][obs["kind"]] = stats["kinds"].get(obs["kind"], 0) + 1
                opts = obs["options"]
                # random, but don't end the turn while something is playable (like the C# RandomAgent)
                if obs["kind"] == "combat" and len(opts) > 1:
                    a = rng.randrange(1, len(opts))
                else:
                    a = rng.randrange(len(opts))
                obs, r, term, trunc, info = env.step(a)
                stats["decisions"] += 1
                done = term or trunc
            end = info["end"]
            stats["runs"] += 1
            stats["wins"] += end["victory"]
            stats["floors"] += end["floor"]
            reason = end["reason"].split(" @")[0].split(" in ")[0]
            stats["reasons"][reason] = stats["reasons"].get(reason, 0) + 1
            assert end["error"] is None, end["error"]
    return stats


if __name__ == "__main__":
    runs = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    procs = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    t = time.time()
    with ProcessPoolExecutor(procs) as ex:
        parts = list(ex.map(worker, [(w, runs // procs) for w in range(procs)]))
    dt = time.time() - t
    tot = {"runs": 0, "wins": 0, "floors": 0, "decisions": 0}
    reasons, kinds = {}, {}
    for p in parts:
        for k in tot:
            tot[k] += p[k]
        for k, v in p["reasons"].items():
            reasons[k] = reasons.get(k, 0) + v
        for k, v in p["kinds"].items():
            kinds[k] = kinds.get(k, 0) + v
    print(f"{tot['runs']} runs in {dt:.1f}s with {procs} procs: {tot['runs']/dt:.1f} runs/s, "
          f"{tot['decisions']/dt:.0f} decisions/s | wins {tot['wins']} | avg floor {tot['floors']/tot['runs']:.1f}")
    print("endings:", reasons)
    print("decision kinds:", kinds)
