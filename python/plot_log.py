"""Plot a training log: python3 plot_log.py runs/act1_ironclad_v1 -> <run>/progress.png"""
import sys
import pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

run = sys.argv[1]
df = pd.read_csv(f"{run}/log.csv").dropna(subset=["clear_rate"])
x = df["decisions"] / 1e6
roll = lambda c: df[c].rolling(20, min_periods=1).mean()
fig, ax = plt.subplots(1, 3, figsize=(15, 4))
ax[0].plot(x, df["clear_rate"] * 100, color="0.8", lw=0.8, label="per update")
ax[0].plot(x, roll("clear_rate") * 100, color="C0", label="moving avg (20 updates)")
ax[0].set(title="Act 1 clear rate", xlabel="decisions (millions)", ylabel="% of finished episodes")
ax[1].plot(x, df["avg_floor"], color="0.8", lw=0.8, label="per update")
ax[1].plot(x, roll("avg_floor"), color="C1", label="moving avg (20 updates)")
ax[1].axhline(17, color="k", ls="--", lw=0.8, label="act 1 boss floor (17)")
ax[1].set(title="Floor reached", xlabel="decisions (millions)", ylabel="floor")
ax[2].plot(x, df["avg_return"], color="0.8", lw=0.8, label="per update")
ax[2].plot(x, roll("avg_return"), color="C2", label="moving avg (20 updates)")
ax[2].set(title="Episode return (shaped)", xlabel="decisions (millions)", ylabel="return")
for a in ax:
    a.legend(fontsize=8); a.grid(alpha=0.3)
fig.tight_layout()
fig.savefig(f"{run}/progress.png", dpi=110)
print(f"{run}/progress.png  last: decisions={df['decisions'].iloc[-1]:,} clear={roll('clear_rate').iloc[-1]*100:.1f}% floor={roll('avg_floor').iloc[-1]:.1f}")
