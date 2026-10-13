"""Headless Slay the Spire 2 environment (gymnasium-style API, no hard dependency on gymnasium).

Each Sts2Env owns one simulator process (`headless --server`) and talks JSON lines over its stdin/stdout.
The game runs in lockstep: it pauses at every decision until `step` answers it.

Observation (a dict, one per decision):
    kind     : "map" | "combat" | "select_cards" | "card_reward" | "reward" | "event" | "rest" | "shop" | "crystal_sphere"
    options  : list of legal actions (dicts describing each); the action is an index into this list
    state    : run state (hp, gold, deck, relics, potions, floor, act, and `combat` with hand/enemies/intents if in combat)
    + extra fields for some kinds (select_cards: min/max/picked, reward: reward, crystal_sphere: divinations_left)

Episodes end on victory, death, abandon, or the per-combat turn cap (`max_combat_turns`, default 100; a deck that
exhausted itself can otherwise never finish a fight).
"""
from __future__ import annotations

import json
import os
import subprocess
from typing import Any, Callable, Optional

_REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# simulator executable: $STS2_SIM, else the repo's Release build (headless.exe on Windows)
DEFAULT_BINARY = os.environ.get("STS2_SIM") or os.path.join(
    _REPO, "headless", "bin", "Release", "net9.0", "headless" + (".exe" if os.name == "nt" else ""))
# .NET runtime: $DOTNET_ROOT, else ~/.dotnet if it exists (user-local install), else the system install
DEFAULT_DOTNET_ROOT = os.environ.get("DOTNET_ROOT") or (
    os.path.expanduser("~/.dotnet") if os.path.isdir(os.path.expanduser("~/.dotnet")) else None)


class ShapedReward:
    """Terminal +1 (cleared the target act / won) or -1 (death, abandon, combat turn cap), plus potential-based
    shaping r = gamma*Phi(s') - Phi(s) with Phi = floor_coef*floor + hp_coef*hp/max_hp.

    The HP term of Phi is zeroed at every terminal, so HP shaping telescopes to zero over an episode: it only speeds
    up credit assignment ("that fight cost a lot of HP") without changing the optimal policy. Rationale: acts start by
    healing (fully at A0-1, 80% of missing HP at A2+), so HP left at the end of an act is worth ~nothing, while HP
    between fights inside an act matters. The floor term is kept at death, so dying later is less bad.
    """

    def __init__(self, floor_coef: float = 0.02, hp_coef: float = 0.3, gamma: float = 0.999):
        self.floor_coef, self.hp_coef, self.gamma = floor_coef, hp_coef, gamma

    def phi(self, floor: int, hp: int = 0, max_hp: int = 1) -> float:
        return self.floor_coef * floor + self.hp_coef * hp / max(max_hp, 1)

    def __call__(self, prev_state: dict, state: Optional[dict], end: Optional[dict]) -> float:
        before = self.phi(prev_state["floor"], prev_state["hp"], prev_state["max_hp"])
        if end is None:
            return self.gamma * self.phi(state["floor"], state["hp"], state["max_hp"]) - before
        after = self.phi(end["floor"])  # HP term zeroed at terminal
        return (1.0 if end["victory"] else -1.0) + self.gamma * after - before


default_reward = ShapedReward()


class Sts2Env:
    def __init__(self, character: str = "IRONCLAD", max_combat_turns: int = 100, stop_after_act: int = 0,
                 reward_fn: Callable[[Optional[dict], Optional[dict], Optional[dict]], float] = default_reward,
                 binary: str = DEFAULT_BINARY, dotnet_root: Optional[str] = DEFAULT_DOTNET_ROOT,
                 stderr=subprocess.DEVNULL):
        env = dict(os.environ)
        if dotnet_root:
            env["DOTNET_ROOT"] = dotnet_root
        self._p = subprocess.Popen([binary, "--server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=stderr, text=True, bufsize=1, env=env)
        ready = self._recv()
        assert ready["type"] == "ready", ready
        self.characters: list[str] = ready["characters"]
        self.game_version: str = ready.get("game_version", "?")
        self.character = character
        self.max_combat_turns = max_combat_turns
        self.stop_after_act = stop_after_act  # e.g. 1: the episode is won by beating the act 1 boss
        self.reward_fn = reward_fn
        self._obs: Optional[dict] = None

    # --- protocol -------------------------------------------------------------------------------------------
    def _send(self, msg: dict) -> None:
        self._p.stdin.write(json.dumps(msg) + "\n")
        self._p.stdin.flush()

    def _recv(self) -> dict:
        line = self._p.stdout.readline()
        if not line:
            raise RuntimeError(f"simulator exited (code {self._p.poll()})")
        msg = json.loads(line)
        if msg["type"] == "error":
            raise ValueError(msg["message"])
        return msg

    # --- gym-style API --------------------------------------------------------------------------------------
    def reset(self, seed: Optional[str | int] = None, character: Optional[str] = None,
              relics: Optional[list[str]] = None, potions: Optional[list[str]] = None,
              god_mode: bool = False) -> tuple[dict, dict]:
        """relics/potions: model ids granted at run start (testing / curricula), e.g. relics=["SCROLL_BOXES"].
        god_mode: testing only — 999 plating/regen at every combat start (like the game's AutoSlay)."""
        cmd = {"cmd": "reset", "character": (character or self.character).upper(),
               "max_combat_turns": self.max_combat_turns, "stop_after_act": self.stop_after_act, "relics": relics or [], "potions": potions or [], "god_mode": god_mode}
        if seed is not None:
            cmd["seed"] = str(seed)
        self._send(cmd)
        msg = self._recv()
        if msg["type"] == "end":  # degenerate: run ended before any decision
            self._obs = None
            return {"kind": "end", "options": [], "state": None}, {"end": msg}
        self._obs = msg
        return msg, {}

    def step(self, action: int) -> tuple[dict, float, bool, bool, dict]:
        """Returns (obs, reward, terminated, truncated, info). info["end"] holds the result when terminated."""
        self.step_async(action)
        return self.step_wait()

    # split step: send to many envs first, then collect -> the simulator processes run in parallel
    def step_async(self, action: int) -> None:
        if self._obs is None:
            raise RuntimeError("call reset() first")
        self._send({"action": int(action)})

    def fileno(self) -> int:
        """stdout pipe of the simulator, for select(): readable = the reply to step_async is (being) delivered."""
        return self._p.stdout.fileno()

    def step_wait(self) -> tuple[dict, float, bool, bool, dict]:
        prev_state = self._obs["state"]
        msg = self._recv()
        if msg["type"] == "end":
            self._obs = None
            # the turn cap counts as a loss (the deck cannot win that fight), so it is a real terminal, not a truncation
            reward = self.reward_fn(prev_state, None, msg)
            return {"kind": "end", "options": [], "state": prev_state}, reward, True, False, {"end": msg}
        self._obs = msg
        return msg, self.reward_fn(prev_state, msg["state"], None), False, False, {}

    def close(self) -> None:
        if self._p.poll() is None:
            try:
                self._send({"cmd": "quit"})
                self._p.wait(timeout=5)
            except Exception:
                self._p.kill()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass
