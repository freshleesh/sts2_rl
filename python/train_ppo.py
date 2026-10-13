"""PPO on full Act-1 runs (one shared candidate-scoring policy for every decision kind).

Usage: python3 train_ppo.py --envs 12 --steps 128 --updates 2000 --out runs/act1_ironclad
Logs one CSV row per update; checkpoints every --save-every updates.
"""
import argparse
import csv
import json
import os
import random
import queue
import threading
import sys
import time

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import featurize as fz
import model
from sts2env import Sts2Env, ShapedReward

p = argparse.ArgumentParser()
p.add_argument("--envs", type=int, default=12)
p.add_argument("--steps", type=int, default=128, help="decisions per env per update")
p.add_argument("--updates", type=int, default=2000)
p.add_argument("--epochs", type=int, default=4)
p.add_argument("--minibatch", type=int, default=256)
p.add_argument("--lr", type=float, default=1e-4)
p.add_argument("--target-kl", type=float, default=0.02, help="stop the update's epochs early once approx KL exceeds this")
p.add_argument("--floor-coef", type=float, default=0.02)
p.add_argument("--hp-coef", type=float, default=0.0, help="0 = no HP shaping (decided 2026-10-13: reward = clear/death + floors)")
p.add_argument("--gamma", type=float, default=0.999)
p.add_argument("--lam", type=float, default=0.95)
p.add_argument("--clip", type=float, default=0.2)
p.add_argument("--ent", type=float, default=0.01)
p.add_argument("--vf", type=float, default=0.5)
p.add_argument("--character", default="IRONCLAD")
p.add_argument("--act", type=int, default=1)
p.add_argument("--threads", type=int, default=6)
p.add_argument("--min-batch", type=int, default=8, help="envs to wait for (<=4 ms) before a policy step")
p.add_argument("--save-every", type=int, default=50)
p.add_argument("--out", default="runs/act1_ironclad")
p.add_argument("--resume", default=None)
p.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
args = p.parse_args()

torch.set_num_threads(args.threads)
os.makedirs(args.out, exist_ok=True)
rng = random.Random(0)
reward = ShapedReward(floor_coef=args.floor_coef, hp_coef=args.hp_coef, gamma=args.gamma)
print(f"device {args.device}", flush=True)
envs = [Sts2Env(character=args.character, stop_after_act=args.act, reward_fn=reward) for _ in range(args.envs)]
dev = torch.device(args.device)
net = model.Policy().to(dev)
print(f"game version {envs[0].game_version}", flush=True)
opt = torch.optim.Adam(net.parameters(), lr=args.lr, eps=1e-5)
start_update = 0
if args.resume:
    ck = torch.load(args.resume, map_location=dev)
    net.load_state_dict(ck["model"]); opt.load_state_dict(ck["opt"]); start_update = ck["update"]
    for g in opt.param_groups:
        g["lr"] = args.lr  # resume with the (possibly changed) learning rate


def new_seed():
    return "T%012d" % rng.randrange(10**12)


obs = [e.reset(seed=new_seed())[0] for e in envs]
ep_ret = [0.0] * len(envs)
done_eps = []  # (return, victory, floor) of episodes finished since last log
# per-env trajectory segments collected this update: lists of (feat, act, logp, val, rew, done)
traj = [[] for _ in envs]
clears_f = open(os.path.join(args.out, "clears.jsonl"), "a")


def record_clear(i, end, update):
    st = obs[i]["state"]  # last decision state before the run ended
    clears_f.write(json.dumps({"update": update + 1, "seed": end["seed"], "boss": end["reason"].split(" vs ")[-1].split(" @")[0],
                               "hp": end["hp"], "deck": [c["id"] + ("+" if c["upgraded"] else "") for c in st["deck"]],
                               "relics": st["relics"], "potions": st["potions"]}) + "\n")
    clears_f.flush()


waiting = set()  # envs whose step_async reply has not been read yet

log_f = open(os.path.join(args.out, "log.csv"), "a", newline="")
log = csv.writer(log_f)
if start_update == 0:
    log.writerow(["update", "decisions", "episodes", "clear_rate", "avg_floor", "avg_return", "pg_loss", "v_loss",
                  "entropy", "approx_kl", "decisions_per_s", "time_s", "rollout_s", "update_s"])
t0 = time.time()
total = start_update * args.envs * args.steps
batch_target = args.envs * args.steps


# One worker thread per simulator: it sends the action, waits for the reply (and resets finished runs), then
# reports to a shared queue. Threads (not select()) so this also works on Windows, where select() can't wait on pipes.
results = queue.Queue()
jobs = [queue.Queue() for _ in envs]


def worker(i):
    env = envs[i]
    while True:
        job = jobs[i].get()
        if job is None:
            return
        action, seed = job
        try:
            env.step_async(action)
            o, r, term, trunc, info = env.step_wait()
            if term or trunc:
                o = env.reset(seed=seed)[0]
            results.put((i, o, r, term or trunc, info))
        except Exception as e:  # surface simulator failures in the main thread
            results.put((i, e, 0.0, True, {}))


threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(len(envs))]
for th in threads:
    th.start()


def act_on(ids):
    """Policy step for the given (ready) envs: sample actions and hand them to the workers."""
    feats = [fz.featurize(obs[i]) for i in ids]
    with torch.no_grad():
        logits, values = net(model.collate(feats, dev))
    dist = torch.distributions.Categorical(logits=logits)
    acts = dist.sample(); logps = dist.log_prob(acts)
    acts, logps, values = acts.cpu(), logps.cpu(), values.cpu()
    for k, i in enumerate(ids):
        traj[i].append([feats[k], acts[k].item(), logps[k].item(), values[k].item(), 0.0, 0.0])
        jobs[i].put((acts[k].item(), new_seed()))
        waiting.add(i)


def absorb(i, o, r, done, info, update):
    """Store the reward of env i's last action and its next observation."""
    if isinstance(o, Exception):
        raise RuntimeError(f"simulator {i} failed") from o
    waiting.discard(i)
    traj[i][-1][4] = r; traj[i][-1][5] = float(done)
    ep_ret[i] += r
    if done:
        end = info["end"]
        done_eps.append((ep_ret[i], end["victory"], end["floor"]))
        if end["victory"]:
            record_clear(i, end, update)
        ep_ret[i] = 0.0
    obs[i] = o


for update in range(start_update, args.updates):
    # --- asynchronous rollout: whichever simulators have replied get batched together ---------------------
    t_roll = time.time()
    collected = 0
    act_on([i for i in range(len(envs)) if i not in waiting])
    while collected < batch_target:
        ready = [results.get()]
        # gather a reasonable batch before running the policy (tiny batches waste the forward pass)
        deadline = time.time() + 0.004
        while len(ready) < min(args.min_batch, len(waiting)):
            left = deadline - time.time()
            if left <= 0:
                break
            try:
                ready.append(results.get(timeout=left))
            except queue.Empty:
                break
        for i, o, r, done, info in ready:
            absorb(i, o, r, done, info, update)
            collected += 1
        if collected < batch_target:
            act_on([x[0] for x in ready])
    # finish in-flight steps so every stored transition has its reward
    while waiting:
        absorb(*results.get(), update)
    total += sum(len(t) for t in traj)

    t_roll = time.time() - t_roll
    t_upd = time.time()
    # --- GAE per env segment, bootstrapped from the value of the env's current observation ----------------
    with torch.no_grad():
        _, boot = net(model.collate([fz.featurize(o) for o in obs], dev))
        boot = boot.cpu()
    flat_feat, acts_l, logp_l, adv_l, ret_l = [], [], [], [], []
    for i, seg in enumerate(traj):
        last, nv = 0.0, boot[i].item()
        advs = [0.0] * len(seg)
        for t in reversed(range(len(seg))):
            f, a, lp, v, r, d = seg[t]
            nonterm = 1.0 - d
            delta = r + args.gamma * nv * nonterm - v
            last = delta + args.gamma * args.lam * nonterm * last
            advs[t] = last; nv = v
        for (f, a, lp, v, r, d), ad in zip(seg, advs):
            flat_feat.append(f); acts_l.append(a); logp_l.append(lp); adv_l.append(ad); ret_l.append(ad + v)
    traj = [[] for _ in envs]
    flat = {"act": torch.tensor(acts_l), "logp": torch.tensor(logp_l)}
    flat_adv, flat_ret = torch.tensor(adv_l), torch.tensor(ret_l)
    n = len(flat_feat)
    stats = []
    for _ in range(args.epochs):
        if stats and np.mean([x[3] for x in stats[-max(1, n // args.minibatch):]]) > args.target_kl:
            break  # policy already moved far enough on this batch
        perm = torch.randperm(n)
        for s in range(0, n, args.minibatch):
            idx = perm[s:s + args.minibatch]
            b = model.collate([flat_feat[i] for i in idx.tolist()], dev)
            logits, v = net(b)
            dist = torch.distributions.Categorical(logits=logits)
            old_logp = flat["logp"][idx].to(dev)
            logp = dist.log_prob(flat["act"][idx].to(dev))
            ratio = torch.exp(logp - old_logp)
            a = flat_adv[idx].to(dev); a = (a - a.mean()) / (a.std() + 1e-8)
            pg = -torch.min(ratio * a, ratio.clamp(1 - args.clip, 1 + args.clip) * a).mean()
            vl = 0.5 * (v - flat_ret[idx].to(dev)).pow(2).mean()
            ent = dist.entropy().mean()
            loss = pg + args.vf * vl - args.ent * ent
            opt.zero_grad(); loss.backward(); torch.nn.utils.clip_grad_norm_(net.parameters(), 0.5); opt.step()
            stats.append([pg.item(), vl.item(), ent.item(), (old_logp - logp).mean().item()])

    s = np.mean(stats, 0) if stats else np.zeros(4)
    t_upd = time.time() - t_upd
    el = time.time() - t0
    if done_eps:
        R, W, Fl = zip(*done_eps)
        row = [update + 1, total, len(done_eps), np.mean(W), np.mean(Fl), np.mean(R)]
    else:
        row = [update + 1, total, 0, "", "", ""]
    row += [*np.round(s, 5), round((total - start_update * args.envs * args.steps) / el, 1), round(el), round(t_roll, 1), round(t_upd, 1)]
    log.writerow(row); log_f.flush()
    print(" ".join(str(x) for x in row), flush=True)
    done_eps = []
    if (update + 1) % args.save_every == 0:
        torch.save({"model": net.state_dict(), "opt": opt.state_dict(), "update": update + 1, "args": vars(args),
                    "game_version": envs[0].game_version}, os.path.join(args.out, f"ckpt_{update + 1:05d}.pt"))
        torch.save({"model": net.state_dict(), "opt": opt.state_dict(), "update": update + 1, "args": vars(args),
                    "game_version": envs[0].game_version}, os.path.join(args.out, "latest.pt"))

for q in jobs:
    q.put(None)
for e in envs:
    e.close()
