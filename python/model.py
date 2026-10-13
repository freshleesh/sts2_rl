"""Candidate-scoring actor-critic: a small Transformer over the state's entity tokens, then each legal option is
embedded (with pointers back into the hand / enemy tokens it refers to) and scored against the pooled state."""
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

import featurize as fz

CARD_SETS = ["deck", "hand", "draw", "discard"]


def _pad(arrs, dtype, fill=0):
    n = max(1, max(len(a) for a in arrs))
    tail = arrs[0].shape[1:] if len(arrs[0].shape) > 1 else ()
    out = np.full((len(arrs), n) + tail, fill, dtype)
    mask = np.zeros((len(arrs), n), bool)
    for i, a in enumerate(arrs):
        out[i, :len(a)] = a
        mask[i, :len(a)] = True
    return torch.from_numpy(out), torch.from_numpy(mask)


def collate(feats: list[dict], device="cpu") -> dict:
    b = _collate(feats)
    return {k: v.to(device, non_blocking=True) for k, v in b.items()}


def _collate(feats: list[dict]) -> dict:
    b = {}
    for s in CARD_SETS:
        b[s + "_ids"], b[s + "_m"] = _pad([f[s + "_ids"] for f in feats], np.int64)
        b[s + "_f"], _ = _pad([f[s + "_f"] for f in feats], np.float32)
    b["relic_ids"], b["relic_m"] = _pad([f["relic_ids"] for f in feats], np.int64)
    b["potion_ids"], b["potion_m"] = _pad([f["potion_ids"] for f in feats], np.int64)
    b["enemy_ids"], b["enemy_m"] = _pad([f["enemy_ids"] for f in feats], np.int64)
    b["enemy_f"], _ = _pad([f["enemy_f"] for f in feats], np.float32)
    b["enemy_pw_ids"], _ = _pad([f["enemy_pw_ids"] for f in feats], np.int64)
    b["enemy_pw_amt"], _ = _pad([f["enemy_pw_amt"] for f in feats], np.float32)
    b["my_pw_ids"] = torch.from_numpy(np.stack([f["my_pw_ids"] for f in feats]))
    b["my_pw_amt"] = torch.from_numpy(np.stack([f["my_pw_amt"] for f in feats]))
    b["global"] = torch.from_numpy(np.stack([f["global"] for f in feats]))
    b["opt_f"], b["opt_m"] = _pad([f["opt_f"] for f in feats], np.float32)
    b["opt_id"], _ = _pad([f["opt_id"] for f in feats], np.int64)
    b["opt_hand"], _ = _pad([f["opt_hand"] for f in feats], np.int64, -1)
    b["opt_enemy"], _ = _pad([f["opt_enemy"] for f in feats], np.int64, -1)
    b["opt_bundle"], _ = _pad([f["opt_bundle"] for f in feats], np.int64)
    return b


def _gather(tokens, ptr):
    """tokens (B,N,d), ptr (B,K) with -1 = none -> (B,K,d)"""
    idx = ptr.clamp(min=0).unsqueeze(-1).expand(-1, -1, tokens.size(-1))
    return torch.gather(tokens, 1, idx) * (ptr >= 0).unsqueeze(-1)


class Policy(nn.Module):
    def __init__(self, d=128, layers=2, heads=4):
        super().__init__()
        self.emb = nn.Embedding(fz.N_BUCKETS, d, padding_idx=0)
        self.set_type = nn.Embedding(8, d)  # global, deck, relic, potion, hand, draw, discard, enemy
        self.card_f = nn.Linear(fz.CARD_F, d)
        self.enemy_f = nn.Linear(fz.ENEMY_F, d)
        self.pw_amt = nn.Linear(1, d)
        self.glob = nn.Linear(fz.GLOBAL_F, d)
        enc = nn.TransformerEncoderLayer(d, heads, 4 * d, dropout=0.0, batch_first=True, norm_first=True)
        self.encoder = nn.TransformerEncoder(enc, layers, enable_nested_tensor=False)
        self.opt_f = nn.Linear(fz.OPT_F, d)
        self.score = nn.Sequential(nn.Linear(3 * d, d), nn.ReLU(), nn.Linear(d, 1))
        self.value = nn.Sequential(nn.Linear(d, d), nn.ReLU(), nn.Linear(d, 1))

    def _powers(self, ids, amt):  # bag of powers: (..., P) -> (..., d)
        return (self.emb(ids) * torch.tanh(self.pw_amt(amt.unsqueeze(-1)))).sum(-2)

    def forward(self, b):
        T = self.set_type.weight
        g = self.glob(b["global"]) + self._powers(b["my_pw_ids"], b["my_pw_amt"]) + T[0]
        parts, masks = [g.unsqueeze(1)], [torch.ones(g.size(0), 1, dtype=torch.bool, device=g.device)]
        slices = {}
        for name, t in [("deck", 1), ("relic", 2), ("potion", 3), ("hand", 4), ("draw", 5), ("discard", 6), ("enemy", 7)]:
            x = self.emb(b[name + "_ids"]) + T[t]
            if name in CARD_SETS:
                x = x + self.card_f(b[name + "_f"])
            if name == "enemy":
                x = x + self.enemy_f(b["enemy_f"]) + self._powers(b["enemy_pw_ids"], b["enemy_pw_amt"])
            start = sum(p.size(1) for p in parts)
            slices[name] = (start, start + x.size(1))
            parts.append(x); masks.append(b[name + "_m"])
        x = torch.cat(parts, 1)
        m = torch.cat(masks, 1)
        h = self.encoder(x, src_key_padding_mask=~m)
        state = h[:, 0]
        hand = h[:, slices["hand"][0]:slices["hand"][1]]
        enemy = h[:, slices["enemy"][0]:slices["enemy"][1]]
        o = (self.opt_f(b["opt_f"]) + self.emb(b["opt_id"]) + _gather(hand, b["opt_hand"]) + _gather(enemy, b["opt_enemy"])
             + self.emb(b["opt_bundle"]).sum(-2))
        s = state.unsqueeze(1).expand_as(o)
        logits = self.score(torch.cat([s, o, s * o], -1)).squeeze(-1)
        logits = logits.masked_fill(~b["opt_m"], -1e9)
        return logits, self.value(state).squeeze(-1)
