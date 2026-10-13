"""Copy the game's managed DLLs into headless/lib/ so the simulator builds and runs against a fixed game version
(Steam updates then can't change the rules under a training run). Game files are NOT committed to git.

Usage: python tools/pin_game.py [path-to-"Slay the Spire 2"-folder]
Without an argument the usual Steam locations (Linux / Windows) are searched.
"""
import glob
import json
import os
import shutil
import sys

CANDIDATES = [
    "~/.local/share/Steam/steamapps/common/Slay the Spire 2",
    "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2",
    "C:/Program Files/Steam/steamapps/common/Slay the Spire 2",
    "D:/SteamLibrary/steamapps/common/Slay the Spire 2",
]

game = sys.argv[1] if len(sys.argv) > 1 else next((os.path.expanduser(c) for c in CANDIDATES if os.path.isdir(os.path.expanduser(c))), None)
if not game or not os.path.isdir(game):
    sys.exit("game folder not found; pass it explicitly")
data = glob.glob(os.path.join(game, "data_sts2_*"))
if not data:
    sys.exit(f"no data_sts2_* folder in {game}")
data = data[0]
lib = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "headless", "lib")
os.makedirs(lib, exist_ok=True)
n = 0
for f in glob.glob(os.path.join(data, "*.dll")):
    # copy everything: the build takes only what sts2.dll actually needs (incl. NuGet libs like System.IO.Hashing)
    shutil.copy2(f, lib); n += 1
version = {}
for j in glob.glob(os.path.join(game, "*.json")):
    try:
        version = json.load(open(j))
        break
    except Exception:
        pass
version["pinned_from"] = data
json.dump(version, open(os.path.join(lib, "GAME_VERSION.json"), "w"), indent=2)
print(f"pinned {n} DLLs from {data} -> {os.path.normpath(lib)}  (game {version.get('version', '?')})")
print("rebuild:  dotnet build headless -c Release")
