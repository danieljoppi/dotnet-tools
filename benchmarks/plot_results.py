#!/usr/bin/env python3
"""Render comparison charts from the BenchmarkDotNet CSV reports.

Usage:
    python3 benchmarks/plot_results.py [artifacts_dir] [out_dir]

Defaults: artifacts_dir=BenchmarkDotNet.Artifacts/results, out_dir=benchmarks/results/charts
"""
import csv
import re
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

# Palette: one accent for the class under test, neutral context bars, light chart chrome.
SURFACE = "#fcfcfb"
ACCENT = "#2a78d6"      # SnapshotTable bars
CONTEXT = "#c3c2b7"     # every other implementation
INK = "#0b0b0b"
MUTED = "#898781"
GRID = "#e1e0d9"

ARTIFACTS = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("BenchmarkDotNet.Artifacts/results")
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("benchmarks/results/charts")
OUT.mkdir(parents=True, exist_ok=True)

TIME_UNITS = {"ns": 1e-6, "us": 1e-3, "μs": 1e-3, "ms": 1.0, "s": 1e3}
SIZE_UNITS = {"B": 1e-6, "KB": 1e-3, "MB": 1.0, "GB": 1e3}


def parse_quantity(text, units):
    """'4.874 ms' / '15.33 MB' -> value in canonical unit (ms / MB)."""
    text = text.replace(",", "").strip()
    m = re.match(r"([\d.]+)\s*(\S+)", text)
    if not m:
        return None
    value, unit = float(m.group(1)), m.group(2)
    return value * units.get(unit, float("nan"))


def load(csv_name):
    path = ARTIFACTS / csv_name
    rows = []
    with path.open() as f:
        for row in csv.DictReader(f):
            rows.append(
                {
                    "method": row["Method"].strip("'"),
                    "mean_ms": parse_quantity(row["Mean"], TIME_UNITS),
                    "alloc_mb": parse_quantity(row.get("Allocated", ""), SIZE_UNITS),
                    "gen2": float(row.get("Gen2", "0") or 0),
                }
            )
    return rows


def barh(rows, value_key, title, xlabel, out_name, fmt, log=False, annotate_loh=False):
    rows = [r for r in rows if r[value_key] is not None]
    rows.sort(key=lambda r: r[value_key], reverse=True)  # smallest (best) bar on top
    labels = [r["method"] for r in rows]
    values = [r[value_key] for r in rows]
    colors = [ACCENT if "SnapshotTable" in l or "TableSnapshot" in l else CONTEXT for l in labels]

    fig, ax = plt.subplots(figsize=(9, 0.62 * len(rows) + 1.6), dpi=160)
    fig.patch.set_facecolor(SURFACE)
    ax.set_facecolor(SURFACE)
    bars = ax.barh(labels, values, color=colors, height=0.62, zorder=3)

    if log:
        ax.set_xscale("log")
    ax.xaxis.grid(True, color=GRID, linewidth=0.8, zorder=0)
    ax.set_axisbelow(True)
    for spine in ("top", "right", "left"):
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.tick_params(colors=MUTED, labelsize=9)
    for tick in ax.get_yticklabels():
        tick.set_color(INK)
        tick.set_fontsize(10)
    ax.set_xlabel(xlabel, color=MUTED, fontsize=9)
    ax.set_title(title, color=INK, fontsize=12, loc="left", pad=14, fontweight="bold")

    span = max(values)
    for bar, row in zip(bars, rows):
        label = fmt(row[value_key])
        if annotate_loh and row["gen2"] > 0:
            label += "  ⚠ LOH/Gen2"
        ax.text(
            bar.get_width() * (1.06 if log else 1.0) + (0 if log else span * 0.012),
            bar.get_y() + bar.get_height() / 2,
            label,
            va="center", ha="left", color=INK, fontsize=9,
        )
    ax.set_xlim(right=span * (2.2 if log else 1.30))
    fig.tight_layout()
    fig.savefig(OUT / out_name, facecolor=SURFACE, bbox_inches="tight")
    plt.close(fig)
    print(f"wrote {OUT / out_name}")


def ms(v):
    return f"{v:,.2f} ms" if v >= 0.01 else f"{v * 1000:,.1f} μs"


def mb(v):
    return f"{v:,.2f} MB" if v >= 0.1 else f"{v * 1000:,.0f} KB"


batch = load("DotnetTools.SnapshotCache.Benchmarks.BatchUpdateBenchmarks-report.csv")
barh(batch, "mean_ms",
     "Applying a 5,000-change batch to a 1,000,000-row table — time",
     "mean time per batch (ms) — shorter is better",
     "batch-update-time.png", ms, annotate_loh=True)
barh(batch, "alloc_mb",
     "Applying a 5,000-change batch to a 1,000,000-row table — allocation",
     "allocated per batch (MB) — shorter is better",
     "batch-update-alloc.png", mb, annotate_loh=True)

reads = load("DotnetTools.SnapshotCache.Benchmarks.ReadBenchmarks-report.csv")
barh(reads, "mean_ms",
     "10,000 random point lookups against 1,000,000 rows — time",
     "mean time per 10k lookups (ms, log scale) — shorter is better",
     "read-time.png", ms, log=True)

load_rows = load("DotnetTools.SnapshotCache.Benchmarks.InitialLoadBenchmarks-report.csv")
barh(load_rows, "mean_ms",
     "Initial full load of 1,000,000 rows — time",
     "mean time per load (ms, log scale) — shorter is better",
     "initial-load-time.png", ms, log=True)
