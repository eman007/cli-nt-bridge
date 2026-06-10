"""Backtest report rendering — a plain-text metrics table for stdout, and a
professional one-page PDF (KPI tiles + filled equity curve + underwater drawdown
+ trade-P&L histogram). PDF rendering requires matplotlib (the [report] extra)."""
from __future__ import annotations

# --- palette -----------------------------------------------------------------
INK = "#1f2937"        # near-black slate (header band, primary text)
ACCENT = "#2563eb"     # blue (equity line)
ACCENT_SOFT = "#93c5fd"
POS = "#16a34a"        # green
NEG = "#dc2626"        # red
MUTED = "#64748b"      # slate-500 (labels)
GRID = "#e5e7eb"       # light grid / tile border
PANEL = "#f8fafc"      # tile background
ZERO = "#9ca3af"

_RC = {
    "font.family": "DejaVu Sans",
    "font.size": 9,
    "axes.edgecolor": GRID,
    "axes.linewidth": 0.8,
    "axes.labelcolor": MUTED,
    "axes.titlecolor": INK,
    "axes.titlesize": 10,
    "axes.titleweight": "bold",
    "text.color": INK,
    "xtick.color": MUTED,
    "ytick.color": MUTED,
    "xtick.labelsize": 8,
    "ytick.labelsize": 8,
    "figure.facecolor": "white",
    "savefig.facecolor": "white",
}


def format_metrics_table(metrics: dict) -> str:
    if not metrics:
        return "(no metrics)"
    width = max(len(k) for k in metrics)
    return "\n".join(f"{k.ljust(width)}  {v}" for k, v in metrics.items())


def _equity_curve(trades: list) -> list:
    eq, cum = [], 0.0
    for t in trades:
        try:
            cum += float(t.get("pnl", 0))
        except (TypeError, ValueError):
            pass
        eq.append(cum)
    return eq


def assess(metrics: dict) -> str:
    """One-line plain-English read of the metrics."""
    bits = []
    net = metrics.get("netProfit")
    if net is not None:
        bits.append(("profitable" if net > 0 else "unprofitable") + f" (net {net:,.2f})")
    pf = metrics.get("profitFactor")
    if pf is not None:
        bits.append(f"profit factor {pf:.2f} " + ("(edge)" if pf > 1 else "(no edge)"))
    n = metrics.get("totalTrades")
    if n is not None:
        bits.append(f"{n} trades")
    dd = metrics.get("maxDrawdown")
    if dd is not None:
        bits.append(f"max drawdown {dd:,.2f}")
    return "; ".join(bits) if bits else "no metrics"


def compute_stats(result: dict) -> dict:
    """Derive a full stat set from a backtest result. Uses the supplied headline
    metrics where present and computes the rest (win rate, avg win/loss, equity,
    drawdown) from the per-trade P&L list."""
    metrics = result.get("metrics", {}) or {}
    trades = result.get("trades", []) or []
    pnls = []
    for t in trades:
        try:
            pnls.append(float(t.get("pnl", 0)))
        except (TypeError, ValueError):
            pass
    n = len(pnls)
    wins = [p for p in pnls if p > 0]
    losses = [p for p in pnls if p < 0]
    gross_profit = sum(wins)
    gross_loss = sum(losses)  # <= 0

    equity, cum, running_peak, drawdown, rp = [], 0.0, [], [], float("-inf")
    for p in pnls:
        cum += p
        equity.append(cum)
        rp = max(rp, cum)
        running_peak.append(rp)
        drawdown.append(cum - rp)

    return {
        "strategy": result.get("strategy") or "(strategy)",
        "net": float(metrics.get("netProfit", sum(pnls) if pnls else 0.0)),
        "trades": int(metrics.get("totalTrades", n)),
        "profit_factor": float(
            metrics.get("profitFactor", (gross_profit / abs(gross_loss)) if gross_loss else 0.0)
        ),
        "max_dd": float(metrics.get("maxDrawdown", min(drawdown) if drawdown else 0.0)),
        "win_rate": (len(wins) / n * 100.0) if n else 0.0,
        "avg_trade": (sum(pnls) / n) if n else 0.0,
        "avg_win": (gross_profit / len(wins)) if wins else 0.0,
        "avg_loss": (gross_loss / len(losses)) if losses else 0.0,
        "n_wins": len(wins),
        "n_losses": len(losses),
        "equity": equity,
        "drawdown": drawdown,
        "running_peak": running_peak,
        "pnls": pnls,
    }


# --- PDF helpers --------------------------------------------------------------
def _plt():
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        return plt
    except ImportError as e:
        raise RuntimeError(
            'PDF report needs matplotlib — install with: pip install -e ".[report]"'
        ) from e


def _cur(v) -> str:
    try:
        v = float(v)
    except (TypeError, ValueError):
        return str(v)
    sign = "-" if v < 0 else ""
    # Escape the $ so matplotlib doesn't treat "$...$" as math mode (which would
    # eat the dollar signs in any string containing two of them, e.g. "$84 / -$136").
    return f"{sign}\\${abs(v):,.0f}"


def _sign_color(v) -> str:
    try:
        return POS if float(v) >= 0 else NEG
    except (TypeError, ValueError):
        return INK


def _style_axis(ax, plt):
    from matplotlib.ticker import FuncFormatter

    for side in ("top", "right"):
        ax.spines[side].set_visible(False)
    ax.grid(True, axis="y", color=GRID, linewidth=0.8, alpha=0.9)
    ax.set_axisbelow(True)
    ax.yaxis.set_major_formatter(FuncFormatter(lambda x, _: _cur(x)))
    ax.margins(x=0.01)


def _tile(bg, x, y, w, h, label, value, color):
    from matplotlib.patches import FancyBboxPatch

    bg.add_patch(
        FancyBboxPatch(
            (x, y), w, h,
            boxstyle="round,pad=0.003,rounding_size=0.010",
            facecolor=PANEL, edgecolor=GRID, linewidth=1.0, zorder=1,
            mutation_aspect=1.0,
        )
    )
    bg.text(x + 0.018, y + h - 0.013, label.upper(), fontsize=7, color=MUTED,
            va="top", ha="left", zorder=2, fontweight="bold")
    bg.text(x + 0.018, y + 0.012, value, fontsize=13, color=color,
            va="bottom", ha="left", zorder=2, fontweight="bold")


def render_pdf(result: dict, out_path) -> str:
    """Render one professional page: header, KPI tiles, equity curve, underwater
    drawdown, trade-P&L histogram. Requires matplotlib."""
    plt = _plt()
    from matplotlib.patches import Rectangle

    out_path = str(out_path)
    s = compute_stats(result)
    x = list(range(1, len(s["equity"]) + 1))

    with plt.rc_context(_RC):
        fig = plt.figure(figsize=(8.5, 11), dpi=150)
        bg = fig.add_axes([0, 0, 1, 1])
        bg.axis("off")
        bg.set_xlim(0, 1)
        bg.set_ylim(0, 1)

        # header band
        bg.add_patch(Rectangle((0, 0.935), 1, 0.065, facecolor=INK, edgecolor="none", zorder=1))
        bg.text(0.045, 0.972, "NT8 BRIDGE", color=ACCENT_SOFT, fontsize=9.5, fontweight="bold", va="center", zorder=2)
        bg.text(0.045, 0.951, "Backtest Report", color="white", fontsize=15, fontweight="bold", va="center", zorder=2)
        bg.text(0.955, 0.962, s["strategy"], color="#e2e8f0", fontsize=11, va="center", ha="right", zorder=2)

        # hero line: net P&L + assessment
        bg.text(0.045, 0.905, "NET P&L", fontsize=8, color=MUTED, fontweight="bold", va="center")
        bg.text(0.045, 0.882, _cur(s["net"]), fontsize=22, color=_sign_color(s["net"]), fontweight="bold", va="center")
        bg.text(0.955, 0.888, assess(result.get("metrics", {}) or {}), fontsize=8.5, color=MUTED, va="center", ha="right")

        # KPI tiles: 2 rows x 3
        tiles = [
            ("Profit Factor", f"{s['profit_factor']:.2f}", POS if s["profit_factor"] > 1 else NEG),
            ("Win Rate", f"{s['win_rate']:.1f}%", INK),
            ("Trades", f"{s['trades']:,}", INK),
            ("Max Drawdown", _cur(s["max_dd"]), NEG),
            ("Avg Win / Loss", f"{_cur(s['avg_win'])} / {_cur(s['avg_loss'])}", INK),
            ("Avg Trade", _cur(s["avg_trade"]), _sign_color(s["avg_trade"])),
        ]
        gx, gy, tw, th, pad = 0.045, 0.79, 0.293, 0.058, 0.014
        for i, (lab, val, col) in enumerate(tiles):
            r, c = divmod(i, 3)
            tx = gx + c * (tw + pad)
            ty = gy - r * (th + pad)
            _tile(bg, tx, ty, tw, th, lab, val, col)

        # equity curve
        ax_eq = fig.add_axes([0.095, 0.45, 0.86, 0.21])
        _style_axis(ax_eq, plt)
        if x:
            ax_eq.fill_between(x, s["equity"], 0, color=ACCENT, alpha=0.12, linewidth=0)
            ax_eq.plot(x, s["equity"], color=ACCENT, linewidth=1.7, label="equity")
            ax_eq.plot(x, s["running_peak"], color=MUTED, linewidth=0.9, linestyle=(0, (4, 3)), alpha=0.8, label="peak")
            ax_eq.axhline(0, color=ZERO, linewidth=0.8)
            ax_eq.legend(loc="upper left", frameon=False, fontsize=7.5)
        else:
            ax_eq.text(0.5, 0.5, "no trades", ha="center", va="center", color=MUTED, transform=ax_eq.transAxes)
        ax_eq.set_title("Equity curve  (cumulative P&L)", loc="left")

        # underwater drawdown
        ax_dd = fig.add_axes([0.095, 0.275, 0.86, 0.11])
        _style_axis(ax_dd, plt)
        if x:
            ax_dd.fill_between(x, s["drawdown"], 0, color=NEG, alpha=0.22, linewidth=0)
            ax_dd.plot(x, s["drawdown"], color=NEG, linewidth=1.1)
            ax_dd.axhline(0, color=ZERO, linewidth=0.8)
        ax_dd.set_title("Drawdown  (underwater)", loc="left")
        ax_dd.set_xlabel("trade #")

        # trade P&L histogram
        ax_h = fig.add_axes([0.095, 0.065, 0.86, 0.135])
        for side in ("top", "right"):
            ax_h.spines[side].set_visible(False)
        ax_h.set_axisbelow(True)
        ax_h.grid(True, axis="y", color=GRID, linewidth=0.8)
        if s["pnls"]:
            bins = min(30, max(8, len(s["pnls"]) // 2))
            counts, edges, patches = ax_h.hist(s["pnls"], bins=bins, edgecolor="white", linewidth=0.5)
            for patch, left in zip(patches, edges[:-1]):
                patch.set_facecolor(POS if left >= 0 else NEG)
            ax_h.axvline(0, color=ZERO, linewidth=0.8)
            from matplotlib.ticker import FuncFormatter
            ax_h.xaxis.set_major_formatter(FuncFormatter(lambda v, _: _cur(v)))
        else:
            ax_h.text(0.5, 0.5, "no trades", ha="center", va="center", color=MUTED, transform=ax_h.transAxes)
        ax_h.set_title(f"Trade P&L distribution  ({s['n_wins']}W / {s['n_losses']}L)", loc="left")

        # footer
        try:
            from datetime import datetime
            stamp = datetime.now().strftime("%Y-%m-%d %H:%M")
        except Exception:
            stamp = ""
        bg.text(0.045, 0.022, "Generated by NT8 Bridge", fontsize=7, color=MUTED, va="center")
        bg.text(0.955, 0.022, stamp, fontsize=7, color=MUTED, va="center", ha="right")

        fig.savefig(out_path)
        plt.close(fig)
    return out_path


def render_batch_pdf(results: list, out_path) -> str:
    """Render a batch of runs: header, a clean summary table, and a net-P&L-by-run
    horizontal bar chart (one bar per run, colored by sign). Requires matplotlib."""
    plt = _plt()
    from matplotlib.patches import Rectangle

    out_path = str(out_path)
    rows, labels, nets = [], [], []
    for r in results:
        res = r.get("result") or {}
        m = res.get("metrics", {}) or {}
        labels.append(str(r.get("label", "")))
        try:
            nets.append(float(m.get("netProfit", 0)))
        except (TypeError, ValueError):
            nets.append(0.0)
        rows.append([
            str(r.get("label", "")),
            str(res.get("status", "")),
            _cur(m.get("netProfit", 0)) if "netProfit" in m else "-",
            f"{float(m['profitFactor']):.2f}" if "profitFactor" in m else "-",
            str(m.get("totalTrades", "-")),
            _cur(m.get("maxDrawdown", 0)) if "maxDrawdown" in m else "-",
        ])

    with plt.rc_context(_RC):
        fig = plt.figure(figsize=(8.5, 11), dpi=150)
        bg = fig.add_axes([0, 0, 1, 1])
        bg.axis("off")
        bg.set_xlim(0, 1)
        bg.set_ylim(0, 1)

        bg.add_patch(Rectangle((0, 0.935), 1, 0.065, facecolor=INK, edgecolor="none", zorder=1))
        bg.text(0.045, 0.972, "NT8 BRIDGE", color=ACCENT_SOFT, fontsize=9.5, fontweight="bold", va="center", zorder=2)
        bg.text(0.045, 0.951, "Batch Report", color="white", fontsize=15, fontweight="bold", va="center", zorder=2)
        bg.text(0.955, 0.962, f"{len(results)} runs", color="#e2e8f0", fontsize=11, va="center", ha="right", zorder=2)

        # summary table
        ax_t = fig.add_axes([0.045, 0.62, 0.91, 0.28])
        ax_t.axis("off")
        if rows:
            tbl = ax_t.table(
                cellText=rows,
                colLabels=["Run", "Status", "Net P&L", "PF", "Trades", "Max DD"],
                cellLoc="left",
                loc="upper center",
            )
            tbl.auto_set_font_size(False)
            tbl.set_fontsize(9)
            tbl.scale(1, 1.9)
            for (row, _col), cell in tbl.get_celld().items():
                cell.set_edgecolor(GRID)
                if row == 0:
                    cell.set_facecolor(INK)
                    cell.set_text_props(color="white", fontweight="bold")
                elif row % 2 == 0:
                    cell.set_facecolor(PANEL)

        # net P&L by run
        ax = fig.add_axes([0.13, 0.09, 0.82, 0.50])
        for side in ("top", "right"):
            ax.spines[side].set_visible(False)
        ax.set_axisbelow(True)
        ax.grid(True, axis="x", color=GRID, linewidth=0.8)
        if labels:
            ypos = range(len(labels))
            ax.barh(list(ypos), nets, color=[POS if v >= 0 else NEG for v in nets], height=0.6)
            ax.set_yticks(list(ypos))
            ax.set_yticklabels(labels, fontsize=8)
            ax.invert_yaxis()
            ax.axvline(0, color=ZERO, linewidth=0.8)
            from matplotlib.ticker import FuncFormatter
            ax.xaxis.set_major_formatter(FuncFormatter(lambda v, _: _cur(v)))
        ax.set_title("Net P&L by run", loc="left")

        fig.savefig(out_path)
        plt.close(fig)
    return out_path
