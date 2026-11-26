#!/usr/bin/env python3
"""Turn the server/client metrics CSV files into a Markdown report and SVG charts.

No third-party packages: standard library only.

Usage:
    python metrics_report.py [--dir PATH_TO_METRICS]

Default metrics dir: ../csharp/metrics (relative to this script).
Writes report.md and *.svg into the metrics directory.
"""
import argparse
import csv
import glob
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DIR = os.path.normpath(os.path.join(HERE, "..", "csharp", "metrics"))


def read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def num(row, key, default=0.0):
    try:
        return float(row.get(key, "") or default)
    except ValueError:
        return default


def svg_bars(title, labels, series, ylabel, path, width=720, height=360, colors=None):
    """series = list of (name, values). Draws grouped bars."""
    colors = colors or ["#3b6ea5", "#c46a3a", "#4b8b5a", "#8a6bbf"]
    left, right, top, bottom = 60, 20, 40, 70
    plot_w = width - left - right
    plot_h = height - top - bottom
    max_v = max([0.001] + [v for _, vals in series for v in vals])
    n = max(1, len(labels))
    group_w = plot_w / n
    bar_w = group_w / (len(series) + 1)

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}">',
             f'<rect width="{width}" height="{height}" fill="white"/>',
             f'<text x="{width/2}" y="22" text-anchor="middle" font-family="sans-serif" font-size="15" font-weight="bold">{title}</text>']
    # axes
    parts.append(f'<line x1="{left}" y1="{top}" x2="{left}" y2="{top+plot_h}" stroke="#333"/>')
    parts.append(f'<line x1="{left}" y1="{top+plot_h}" x2="{left+plot_w}" y2="{top+plot_h}" stroke="#333"/>')
    for i in range(5):
        y = top + plot_h * i / 4
        v = max_v * (4 - i) / 4
        parts.append(f'<line x1="{left}" y1="{y:.1f}" x2="{left+plot_w}" y2="{y:.1f}" stroke="#eee"/>')
        parts.append(f'<text x="{left-6}" y="{y+4:.1f}" text-anchor="end" font-family="sans-serif" font-size="10" fill="#555">{v:.0f}</text>')
    for gi, label in enumerate(labels):
        gx = left + gi * group_w
        for si, (name, vals) in enumerate(series):
            v = vals[gi] if gi < len(vals) else 0
            h = (v / max_v) * plot_h
            x = gx + bar_w * (si + 0.5)
            y = top + plot_h - h
            parts.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{bar_w*0.8:.1f}" height="{h:.1f}" fill="{colors[si % len(colors)]}"/>')
        parts.append(f'<text x="{gx+group_w/2:.1f}" y="{top+plot_h+16}" text-anchor="middle" font-family="sans-serif" font-size="9">{label}</text>')
    # legend
    lx = left
    for si, (name, _) in enumerate(series):
        parts.append(f'<rect x="{lx}" y="{height-30}" width="12" height="12" fill="{colors[si % len(colors)]}"/>')
        parts.append(f'<text x="{lx+18}" y="{height-20}" font-family="sans-serif" font-size="11">{name}</text>')
        lx += 20 + 8 * len(name)
    parts.append(f'<text x="18" y="{top+plot_h/2}" transform="rotate(-90 18 {top+plot_h/2})" text-anchor="middle" font-family="sans-serif" font-size="11">{ylabel}</text>')
    parts.append('</svg>')
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=DEFAULT_DIR, help="metrics directory")
    args = ap.parse_args()
    mdir = os.path.abspath(args.dir)
    server = read_csv(os.path.join(mdir, "server-orders.csv"))
    # One CSV per client process (client-orders-<pid>.csv), so several clients
    # can run at the same time; merge them in file-name order.
    client_files = sorted(glob.glob(os.path.join(mdir, "client-orders*.csv")))
    client = []
    for path in client_files:
        client.extend(read_csv(path))
    if not server and not client:
        print(f"No metrics found in {mdir}. Run the server and client first.")
        return 1

    ready = [r for r in server if r.get("status") == "ready"]
    cancelled = [r for r in server if r.get("status") == "cancelled"]

    def avg(rows, key):
        vals = [num(r, key) for r in rows if (r.get(key, "") or "") not in ("", "-1")]
        return sum(vals) / len(vals) if vals else 0.0

    def mx(rows, key):
        vals = [num(r, key) for r in rows if (r.get(key, "") or "") not in ("", "-1")]
        return max(vals) if vals else 0.0

    per_worker = defaultdict(int)
    for r in server:
        w = r.get("worker_id", "")
        if w not in ("", "-1"):
            per_worker[w] += 1

    lines = []
    lines.append("# Lab 2 — Coffee Shop metrics\n")
    lines.append("Generated from `server-orders.csv` and `client-orders*.csv`.\n")
    lines.append("## Summary\n")
    lines.append("| Metric | Value |")
    lines.append("|---|---:|")
    lines.append(f"| Orders accepted (finished rows) | {len(server)} |")
    lines.append(f"| Orders ready | {len(ready)} |")
    lines.append(f"| Orders cancelled | {len(cancelled)} |")
    lines.append(f"| Avg queue wait (ms) | {avg(server, 'queue_wait_ms'):.1f} |")
    lines.append(f"| Max queue wait (ms) | {mx(server, 'queue_wait_ms'):.1f} |")
    lines.append(f"| Avg prep actual (ms) | {avg(ready, 'prep_actual_ms'):.1f} |")
    lines.append(f"| Max prep actual (ms) | {mx(ready, 'prep_actual_ms'):.1f} |")
    if client:
        lines.append(f"| Client avg accept latency (ms) | {avg(client, 'accept_latency_ms'):.1f} |")
        lines.append(f"| Client avg ready wait (ms) | {avg(client, 'wait_ms'):.1f} |")
        lines.append(f"| Client avg total (ms) | {avg(client, 'total_ms'):.1f} |")
    lines.append("")
    lines.append("## Orders per worker\n")
    lines.append("| Worker | Orders |")
    lines.append("|---:|---:|")
    for w in sorted(per_worker, key=lambda x: int(x)):
        lines.append(f"| {w} | {per_worker[w]} |")
    lines.append("")

    if server:
        labels = [r.get("order_id", "?") for r in server]
        svg_bars("Queue wait per order", labels,
                 [("queue wait (ms)", [num(r, "queue_wait_ms") for r in server])],
                 "milliseconds", os.path.join(mdir, "queue_wait.svg"))
        svg_bars("Preparation time: expected vs actual", labels,
                 [("expected (ms)", [num(r, "expected_prep_ms") for r in server]),
                  ("actual (ms)", [num(r, "prep_actual_ms") for r in server])],
                 "milliseconds", os.path.join(mdir, "prep_time.svg"))
    if per_worker:
        ws = sorted(per_worker, key=lambda x: int(x))
        svg_bars("Orders per worker", ws,
                 [("orders", [per_worker[w] for w in ws])],
                 "orders", os.path.join(mdir, "worker_orders.svg"))
    if client:
        labels = [r.get("order_id", "?") for r in client]
        svg_bars("Client-side latency per order", labels,
                 [("accept (ms)", [num(r, "accept_latency_ms") for r in client]),
                  ("ready wait (ms)", [num(r, "wait_ms") for r in client]),
                  ("total (ms)", [num(r, "total_ms") for r in client])],
                 "milliseconds", os.path.join(mdir, "client_latency.svg"))

    lines.append("## Files\n")
    for name in ["server-orders.csv"] + [os.path.basename(p) for p in client_files] + [
                 "queue_wait.svg", "prep_time.svg",
                 "worker_orders.svg", "client_latency.svg"]:
        if os.path.exists(os.path.join(mdir, name)):
            lines.append(f"- `{name}`")
    lines.append("")

    out = os.path.join(mdir, "report.md")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"Wrote {out}")
    for name in ["queue_wait.svg", "prep_time.svg", "worker_orders.svg", "client_latency.svg"]:
        if os.path.exists(os.path.join(mdir, name)):
            print(f"Wrote {os.path.join(mdir, name)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
