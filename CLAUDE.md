# sts2_rl — Slay the Spire 2 headless simulator + RL

Goal: an agent that plays Slay the Spire 2 well, trained by RL in a headless copy of the real game, and in the end
**watched playing the real game** (Windows, the user's beta branch + mods) through a bridge mod.

The user writes in Korean; answer in Korean, keep it short.

## Layout

| path | what |
|---|---|
| `headless/` | C# .NET 9 console that loads the game's own `sts2.dll` and runs it **without the Godot engine** |
| `headless/lib/` | game DLLs pinned by `tools/pin_game.py` (**not in git** — copyrighted game files) |
| `python/sts2env/` | gym-style Python API: one `headless --server` process per env, JSON lines over stdin/stdout |
| `python/featurize.py`, `model.py` | observation → tensors; candidate-scoring Transformer policy |
| `python/train_ppo.py` | PPO over full Act-1 runs; `plot_log.py`, `eval_behavior.py`, `random_agent.py` |
| `tools/pin_game.py` | copy the game's DLLs into `headless/lib/` (fixes the game version the sim runs) |
| `runs/` | training outputs (not in git) |

The decompiled game source is **not** in this repo (copyright). Regenerate it when needed:
`ilspycmd -p -o sts2_decomp/src -r <data_dir> <data_dir>/sts2.dll` (`dotnet tool install -g ilspycmd`; needs .NET 10 SDK).
Grep it to find game APIs — almost every fix so far came from reading it.

## Setup (Windows native; Linux works the same)

1. Install .NET SDK 9 (Harmony refuses .NET 10 at runtime; SDK 10 is fine to have too) and Python 3.11+.
2. `pip install torch --index-url https://download.pytorch.org/whl/cu128` (or the current CUDA wheel), plus `numpy pandas matplotlib`.
3. `python tools/pin_game.py` (auto-finds Steam's `Slay the Spire 2`; or pass the folder). Prints the pinned game version.
4. `dotnet build headless -c Release`
5. Smoke test: `python python/random_agent.py 60 6` → runs/s line, no assertion errors.
6. Regression (after any game update / re-pin): see "Regression check" below. **Do this before trusting a new version.**

Paths: `STS2_SIM` = simulator exe (default `headless/bin/Release/net9.0/headless[.exe]`), `DOTNET_ROOT` (default
`~/.dotnet` if present, else system install).

Windows notes: rollout uses one reader thread per simulator (Windows `select()` can't wait on pipes). Windows
Defender scanning can slow process start — exclude the repo folder if startup is slow. Use PowerShell
`Start-Process` / a second terminal instead of `nohup` for long trainings.

## Game version policy

- The sim runs whatever is in `headless/lib/` (version shown in `GAME_VERSION.json`, reported by the server's
  `ready` message and saved in checkpoints as `game_version`).
- The user **plays the Steam beta branch with many mods** on Windows. Train on the version the agent will play:
  re-pin from the beta install, rebuild, run the regression check, then retrain (checkpoints from another game
  version are not comparable).
- Mods are never loaded by the sim (ModManager is forced to Skipped).
- Steam Cloud syncs saves between machines; the sim never reads or writes saves (verified: no files touched).
  Don't launch the *game* on a machine whose installed version differs from the other one with cloud sync on.

## Simulator: how it works / gotchas (hard-won — read before changing anything)

- Boot (`Boot.cs`): `TestMode.TurnOnInternal()`, `NonInteractiveMode.AutoSlayerCheck=()=>true`, ModManager.State=Skipped
  via reflection, test settings/prefs, `ModelDb.Init/InitIds`, message/action type init.
- `GodotStubs.cs` (Harmony): every Godot native entry point is trapped to throw (otherwise a segfault with no stack).
  Stubbed: print, OS args/features, Time, SceneTree/root (frame timing), StringName/NodePath ctors, ResourceLoader,
  AssetCache, LocString (returns the loc key), Sfx/Vfx/Talk/Think cmds, Sentry, saves/replays/achievements.
- **UI singletons used without a null check** (`NGame.Instance.ScreenShake…`, `NCombatRoom.Instance…`) are rewritten in
  IL by a transpiler (`SwapInstance`): the getter becomes `ldnull` and its consumer call is replaced by pops + default.
  Faking the object with patched no-op methods does NOT work: the JIT inlines the original member body.
- Everything runs on `AsyncPump` (single-thread SynchronizationContext, like Godot's main thread). The thread-pool
  version hung under parallel load.
- The game swallows exceptions in `TaskHelper.RunSafely` → **a silent stall usually means a swallowed exception**.
  Every bench prints a count of them. Debug: `STS_FCE=1|2` (first-chance log), `STS_DUMPIL=Type::Method`,
  `STS_TRACE=1`, `STS_VERBOSE=1`.
- `LocalContext.NetId=1` must be re-set before every run (`RunManager.CleanUp` clears it).
- `RunDriver` plays full runs (Neow → map → rooms → rewards → act change → Architect victory) and asks an `IAgent`
  for every decision. Every player choice in the game goes through `PlayerChoiceSynchronizer` — grep its call sites to
  find choice points. Card selects use the game's `ICardSelector`; rewards `RewardsSet.testSelector`; relic/bundle
  screens are Harmony-hooked (test mode would always take bundle 0); Crystal Sphere minigame is captured from its
  screen; Trial "double down" abandons the run.
- After choosing an event option, wait for its task **unless** it is waiting on us (minigame) — otherwise deadlock or
  double choices.
- God mode (`STS_GOD=1`, `reset(god_mode=True)`): 999 plating/regen like the game's AutoSlay — testing only. Deaths in
  god mode are real mechanics: THE_INSATIABLE's Sandpit instakill, or a deck exhausted to nothing (Havoc spam) →
  per-combat turn cap (default 100) ends the run as a loss.
- Deterministic per seed. Speed: ~220 single combats/s/process; full random runs ~25/s on 16 procs.

### Regression check (run after any sim or game-version change)

```
SIM=headless/bin/Release/net9.0/headless          # headless.exe on Windows
STS_SWEEP=events STS_REPS=6 $SIM                  # also ancients, encounters -> "[sweep] 0 problem ..."
STS_GOD=1 STS_N=40 STS_CHAR=IRONCLAD $SIM         # each character -> no "error @", "[swallowed game exceptions] 0"
python python/random_agent.py 240 6               # -> no assertion errors
```
(set `DOTNET_ROOT` if .NET is user-local; env vars go before the exe on Linux, `$env:STS_SWEEP="events"` in PowerShell.)

## Python API

`Sts2Env(character, stop_after_act=1, max_combat_turns=100).reset(seed, relics=[], potions=[], god_mode=False)` →
`obs = {kind, options, state, ...}`; `step(option_index)` → `(obs, reward, terminated, truncated, info)`;
`info["end"]` = `{victory, act, floor, hp, reason, seed, ...}`. Decision kinds: map (travel / use or discard potion),
combat (play card→target / potion / discard potion / end turn), select_cards (one card at a time + "done"),
card_reward, reward (relics only — gold, potions with a free slot and cards are auto-taken), potion_full,
choose_relic, choose_bundle, treasure (take/skip the single relic), event, rest, shop, crystal_sphere.

## Training decisions (agreed with the user — don't silently change)

- One shared policy for all decision kinds over **full Act-1 runs** (`stop_after_act=1`), not a separate combat
  curriculum: decks/relics then come from the agent's own choices.
- Reward: **+1 act clear / −1 death, abandon or turn cap**, plus floor potential `0.02·floor` (dying later is less bad).
  **No HP term** (`--hp-coef 0`): HP shaping made the policy heal at 100% of rest sites, and end-of-act HP is worthless
  (each act starts with an Ancient heal: full at A0-1, 80% of missing HP at A2+). **No enemy-HP shaping** (user:
  would punish build-up turns).
- Relic rewards stay a decision (forced-effect relics can be bad); the option shows which relic.
- PPO: lr 1e-4, target-KL 0.02 early stop (lr 3e-4 drifted to KL 0.045), γ 0.999, λ 0.95, clip 0.2.
- Rejected for now: MCTS/search for combat (no cheap state clone — only replay; sim ~50× slower than sts_lightspeed),
  LLM / TypeSafe Jev as the player (no game knowledge, can't be trained; might revisit as a teacher).
- Not done (and can't be done fully): numeric card features — rule-changing cards (Corruption) don't reduce to numbers.
  Hash-embedded ids learn from experience; card numbers/text embeddings are an optional speed-up later.

## Status / history

- 2026-10-13, Linux, game v0.107.1 public branch: v1 run (lr 3e-4, HP shaping, reward take/skip exposed) reached
  avg floor ~10.7 / 0.5% act-1 clears after 0.5M decisions, then plateaued. Behaviour review: skipped 85% of rewards
  (reward options didn't say which relic), always healed at rest sites, ended 17% of turns with energy + playable
  cards left; deaths mostly in Act-1 normal fights. Those findings drove the decisions above. v2 config is ready but
  was never run.
- Throughput: ~750 decisions/s overall with 24 envs on an RTX 4060 laptop GPU (rollout ~1,600/s, update ~1.3 s per
  3k decisions). CPU-only updates are ~20× slower.

## Next steps

1. On Windows: pin the **beta** game, rebuild, run the regression check, fix what breaks.
2. Start v2 training: `python python/train_ppo.py --envs 24 --steps 128 --updates 3000 --min-batch 12 --out runs/act1_ironclad_v2`
   (watch `runs/<name>/log.csv`, `plot_log.py runs/<name>`, `eval_behavior.py runs/<name>/latest.pt 30 [--greedy]`,
   victories in `clears.jsonl`).
3. Bridge mod for the real game: reuse `Obs`/`RemoteAgent` inside a Harmony mod so the real game asks the trained
   policy (localhost TCP) — same observations as in training.
