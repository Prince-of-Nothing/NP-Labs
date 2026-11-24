#!/usr/bin/env python3
"""Generate the Lab 1 report charts from results.csv.

Reads the CSV appended to by the Lab1 program and writes one PNG per task:

  task1a.png, task1c.png, task2.png  elapsed time, speed-up and peak memory
                                     against the number of threads;
  task1b.png                         elapsed time and totals per configuration,
                                     so lost updates in the unsynchronised
                                     version are visible.

When a (task, workers) pair appears more than once, the last row wins, so a
fresh full run replaces older measurements in the charts.

Usage:
  python charts.py --csv ../Lab1/results.csv --out ../Lab1/charts
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

try:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except ImportError:
    sys.exit(
        "error: matplotlib is required for charts.\n"
        "  CachyOS/Linux: sudo pacman -S python-matplotlib\n"
        "  Windows:       py -3 -m pip install matplotlib"
    )

SERIES_TASKS = ("task1a", "task1c", "task2")
LABELS = {
    "task1a": "Task 1a - counting with threads (predivided file)",
    "task1c": "Task 1c - producer and consumers (bounded queue)",
    "task2": "Task 2 - many files on slow storage (whole-file buffers)",
    "task1b": "Task 1b - three ways to count (in-memory array)",
}


def load_rows(csv_path: Path) -> list[dict]:
    if not csv_path.is_file():
        sys.exit(f"error: '{csv_path}' does not exist. Run some tasks first.")
    with csv_path.open(newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        sys.exit(f"error: '{csv_path}' has no data rows.")
    return rows


def number(row: dict, key: str, default=0.0):
    value = row.get(key, "")
    if value is None or value == "":
        return default
    try:
        return float(value)
    except ValueError:
        return default


def series_rows(rows: list[dict], task: str) -> list[dict]:
    """One row per worker count for a line-chart task; the last row wins."""
    by_workers: dict[int, dict] = {}
    for row in rows:
        if row["task"] == task:
            by_workers[int(number(row, "workers"))] = row
    return [by_workers[workers] for workers in sorted(by_workers)]


def chart_series_task(rows: list[dict], task: str, out_dir: Path) -> Path | None:
    task_rows = series_rows(rows, task)
    if not task_rows:
        print(f"  {task}: no rows in the CSV, skipping.")
        return None

    workers = [int(number(row, "workers")) for row in task_rows]
    elapsed = [number(row, "elapsed_ms") for row in task_rows]
    memory = [number(row, "peak_memory_mib") for row in task_rows]
    speedup = [number(row, "speedup", default=float("nan")) for row in task_rows]

    figure, axes = plt.subplots(1, 3, figsize=(15, 4.5))
    figure.suptitle(LABELS.get(task, task))

    axes[0].plot(workers, elapsed, marker="o")
    axes[0].set_title("Execution time")
    axes[0].set_xlabel("threads")
    axes[0].set_ylabel("elapsed [ms]")
    axes[0].set_xticks(workers)

    axes[1].plot(workers, speedup, marker="o", color="tab:green")
    ideal = [min(value, max(elapsed) / min(elapsed) if min(elapsed) else value) for value in workers]
    axes[1].plot(workers, ideal, linestyle="--", color="grey", label="ideal")
    axes[1].set_title("Speed-up vs. 1 thread")
    axes[1].set_xlabel("threads")
    axes[1].set_ylabel("speed-up [x]")
    axes[1].set_xticks(workers)
    axes[1].legend()

    axes[2].plot(workers, memory, marker="o", color="tab:red")
    axes[2].set_title("Peak memory")
    axes[2].set_xlabel("threads")
    axes[2].set_ylabel("peak working set [MiB]")
    axes[2].set_xticks(workers)

    figure.tight_layout()
    destination = out_dir / f"{task}.png"
    figure.savefig(destination, dpi=150)
    plt.close(figure)
    return destination


def chart_task1b(rows: list[dict], out_dir: Path) -> Path | None:
    # Repeated runs append rows; the last row per configuration wins.
    by_config: dict[str, dict] = {}
    for row in rows:
        if row["task"] == "task1b" and row.get("correct") is not None:
            by_config[row["configuration"]] = row
    task_rows = list(by_config.values())
    if not task_rows:
        print("  task1b: no rows in the CSV, skipping.")
        return None

    labels = [
        f"{row['configuration']}\n({int(number(row, 'workers'))} threads)"
        for row in task_rows
    ]
    elapsed = [number(row, "elapsed_ms") for row in task_rows]
    negative = [number(row, "negative") for row in task_rows]
    positive = [number(row, "positive") for row in task_rows]
    zero = [number(row, "zero") for row in task_rows]

    figure, axes = plt.subplots(1, 2, figsize=(13, 5))
    figure.suptitle(LABELS["task1b"])

    axes[0].bar(labels, elapsed, color="tab:blue")
    axes[0].set_title("Execution time")
    axes[0].set_ylabel("elapsed [ms]")
    axes[0].tick_params(axis="x", labelsize=8)

    axes[1].bar(labels, negative, label="negative")
    axes[1].bar(labels, positive, bottom=negative, label="positive")
    axes[1].bar(labels, zero, bottom=[n + p for n, p in zip(negative, positive)], label="zero")
    axes[1].set_title("Totals (lost updates visible as short bars)")
    axes[1].set_ylabel("count")
    axes[1].tick_params(axis="x", labelsize=8)
    axes[1].legend()

    figure.tight_layout()
    destination = out_dir / "task1b.png"
    figure.savefig(destination, dpi=150)
    plt.close(figure)
    return destination


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate Lab 1 report charts from results.csv.")
    parser.add_argument("--csv", default="../Lab1/results.csv", help="input CSV written by the lab program")
    parser.add_argument("--out", default="../Lab1/charts", help="output directory for the PNG files")
    arguments = parser.parse_args()

    csv_path = Path(arguments.csv)
    out_dir = Path(arguments.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    rows = load_rows(csv_path)
    written = []
    for task in SERIES_TASKS:
        destination = chart_series_task(rows, task, out_dir)
        if destination:
            written.append(destination)
    destination = chart_task1b(rows, out_dir)
    if destination:
        written.append(destination)

    if not written:
        sys.exit("error: the CSV contains no rows for any known task.")
    for path in written:
        print(f"wrote {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
