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


# --- Steady-state memory footprint (reads results/raw/memory-footprint.csv if present) ---
def plot_memory_footprint():
    src = Path("benchmarks/results/raw/memory-footprint.csv")
    if not src.exists():
        src = Path("results/raw/memory-footprint.csv")
    if not src.exists():
        print("memory-footprint.csv not found; skipping memory chart")
        return
    LOH_COLOR = "#eb6834"  # categorical slot 6 (orange): the LOH portion
    SOH_COLOR = "#2a78d6"  # categorical slot 1 (blue): small-object heap portion
    rows_target = 10_000_000
    raw_bytes = rows_target * 16  # KeyValuePair<long,long>
    entries = []
    with src.open() as f:
        for row in csv.DictReader(f):
            if int(row["rows"]) == rows_target and row["heap_bytes"] != "FAILED":
                heap = int(row["heap_bytes"])
                loh = int(row["loh_bytes"])
                entries.append((row["structure"], (heap - loh) / 2**20, loh / 2**20, heap / 2**20))
    entries.sort(key=lambda e: e[3])
    labels = [e[0] for e in entries]
    soh = [e[1] for e in entries]
    loh = [e[2] for e in entries]
    totals = [e[3] for e in entries]

    fig, ax = plt.subplots(figsize=(9, 0.62 * len(entries) + 2.0), dpi=160)
    fig.patch.set_facecolor(SURFACE)
    ax.set_facecolor(SURFACE)
    ax.barh(labels, soh, color=SOH_COLOR, height=0.62, zorder=3, label="Small-object heap")
    ax.barh(labels, loh, left=[s + 0.8 for s in soh], color=LOH_COLOR, height=0.62, zorder=3,
            label="Large Object Heap", edgecolor=SURFACE, linewidth=1.5)
    ax.xaxis.grid(True, color=GRID, linewidth=0.8, zorder=0)
    ax.set_axisbelow(True)
    for spine in ("top", "right", "left"):
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.tick_params(colors=MUTED, labelsize=9)
    for tick in ax.get_yticklabels():
        tick.set_color(INK)
        tick.set_fontsize(10)
    ax.set_xlabel("resident MiB for 10,000,000 rows (16 B raw each) — shorter is better", color=MUTED, fontsize=9)
    ax.set_title("Steady-state memory by structure — small-object vs Large Object Heap",
                 color=INK, fontsize=12, loc="left", pad=14, fontweight="bold")
    span = max(totals)
    for y, total in enumerate(totals):
        ax.text(total + span * 0.015, y, f"{total:,.0f} MiB  ({total * 2**20 / raw_bytes:.1f}x raw)",
                va="center", ha="left", color=INK, fontsize=9)
    ax.set_xlim(right=span * 1.32)
    ax.legend(loc="lower right", frameon=False, fontsize=9, labelcolor=INK)
    fig.tight_layout()
    fig.savefig(OUT / "memory-footprint.png", facecolor=SURFACE, bbox_inches="tight")
    plt.close(fig)
    print(f"wrote {OUT / 'memory-footprint.png'}")


plot_memory_footprint()


# --- Shared-key bucket workloads (issue #6 / RESULTS.md §9) ---

RAW = Path("benchmarks/results/raw")
if not RAW.exists():
    RAW = Path("results/raw")

LOH_SAFE = ("ChunkedList", "SnapshotTable", "MultiValue")


def load_bucket_csv(csv_name, param_filter):
    """Rows from a committed bucket-benchmark CSV matching the given param values."""
    path = RAW / csv_name
    if not path.exists():
        return []
    rows = []
    with path.open() as f:
        for row in csv.DictReader(f):
            if all(row.get(k, "").strip() == v for k, v in param_filter.items()):
                rows.append(
                    {
                        "method": row["Method"].strip("'"),
                        "mean_ms": parse_quantity(row.get("Median") or row["Mean"], TIME_UNITS),
                        "alloc_mb": parse_quantity(row.get("Allocated", ""), SIZE_UNITS),
                        "gen2": float(row.get("Gen2", "0") or 0),
                    }
                )
    return rows


def parse_loh_study(txt_name, column):
    """Approach → MiB value of one column in a --bucket-loh markdown table."""
    path = RAW / txt_name
    if not path.exists():
        return {}
    header, values = [], {}
    for line in path.read_text().splitlines():
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if cells and cells[0] == "Approach":
            header = cells
        elif header and len(cells) == len(header) and not set(cells[0]) <= {"-", ":", " "}:
            row = dict(zip(header, cells))
            if column in row:
                values[row["Approach"]] = float(row[column].replace("MiB", "").strip())
    return values


def plot_bucket_loh():
    """The §9 money chart: uncompacted LOH growth after warm batches, 1M vs 10M entities."""
    small = parse_loh_study("bucket-loh-1m-10k-zipf.txt", "LOH after cycles (uncompacted)")
    large = parse_loh_study("bucket-loh-10m-10k-zipf.txt", "LOH after cycles (uncompacted)")
    if not small or not large:
        print("bucket LOH study files not found; skipping bucket LOH chart")
        return
    approaches = [a for a in small if a in large]
    y = range(len(approaches))
    fig, ax = plt.subplots(figsize=(9, 0.9 * len(approaches) + 1.8), dpi=160)
    fig.patch.set_facecolor(SURFACE)
    ax.set_facecolor(SURFACE)
    LOH_COLOR = "#eb6834"
    SAFE = "#2a78d6"
    for offset, (values, alpha, label) in enumerate(
        [(small, 0.55, "1M entities"), (large, 1.0, "10M entities")]
    ):
        vals = [values[a] for a in approaches]
        colors = [SAFE if any(t in a for t in LOH_SAFE) else LOH_COLOR for a in approaches]
        bars = ax.barh(
            [i + (0.2 if offset == 0 else -0.2) for i in y], vals, height=0.36,
            color=colors, alpha=alpha, zorder=3, label=label)
        for bar, v in zip(bars, vals):
            ax.text(bar.get_width() + max(large.values()) * 0.012,
                    bar.get_y() + bar.get_height() / 2,
                    f"{v:,.1f} MiB", va="center", ha="left", color=INK, fontsize=9)
    ax.set_yticks(list(y), approaches, color=INK, fontsize=10)
    ax.xaxis.grid(True, color=GRID, linewidth=0.8, zorder=0)
    ax.set_axisbelow(True)
    for spine in ("top", "right", "left"):
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.tick_params(colors=MUTED, labelsize=9)
    ax.set_xlabel("uncompacted LOH growth after warm batches (MiB) — Zipf buckets, K=10k — shorter is better",
                  color=MUTED, fontsize=9)
    ax.set_title("Shared-key buckets: what each representation leaves on the Large Object Heap",
                 color=INK, fontsize=12, loc="left", pad=14, fontweight="bold")
    ax.set_xlim(right=max(large.values()) * 1.30)
    ax.legend(loc="lower right", frameon=False, fontsize=9, labelcolor=INK)
    fig.tight_layout()
    fig.savefig(OUT / "bucket-loh-growth.png", facecolor=SURFACE, bbox_inches="tight")
    plt.close(fig)
    print(f"wrote {OUT / 'bucket-loh-growth.png'}")


def barh_buckets(rows, value_key, title, xlabel, out_name, fmt, log=False):
    rows = [r for r in rows if r[value_key] is not None]
    if not rows:
        print(f"no rows for {out_name}; skipping")
        return
    rows.sort(key=lambda r: r[value_key], reverse=True)
    labels = [r["method"] for r in rows]
    values = [r[value_key] for r in rows]
    colors = [ACCENT if any(t in l for t in LOH_SAFE) else CONTEXT for l in labels]
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
        ax.text(bar.get_width() * (1.06 if log else 1.0) + (0 if log else span * 0.012),
                bar.get_y() + bar.get_height() / 2, fmt(row[value_key]),
                va="center", ha="left", color=INK, fontsize=9)
    ax.set_xlim(right=span * (2.2 if log else 1.30))
    fig.tight_layout()
    fig.savefig(OUT / out_name, facecolor=SURFACE, bbox_inches="tight")
    plt.close(fig)
    print(f"wrote {OUT / out_name}")


plot_bucket_loh()

zipf = load_bucket_csv(
    "DotnetTools.SnapshotCache.Benchmarks.SharedKeyBucketBenchmarks-report.csv",
    {"N": "1000000", "K": "10000", "Skew": "Zipf"})
barh_buckets(zipf, "mean_ms",
             "Warm bucket batch, 1M entities over 10k Zipf keys — time (median)",
             "median time per batch (ms, log scale) — shorter is better",
             "bucket-batch-time.png", ms, log=True)
barh_buckets(zipf, "alloc_mb",
             "Warm bucket batch, 1M entities over 10k Zipf keys — allocation",
             "allocated per batch (MB) — shorter is better",
             "bucket-batch-alloc.png", mb)

bucket_reads = load_bucket_csv(
    "DotnetTools.SnapshotCache.Benchmarks.BucketReadBenchmarks-report.csv",
    {"N": "1000000", "K": "10000", "Skew": "Zipf"})
indexed = [r for r in bucket_reads if "x10k" in r["method"]]
barh_buckets(indexed, "mean_ms",
             "10,000 hot-weighted indexed bucket reads, Zipf — time",
             "mean time per 10k reads (ms, log scale) — shorter is better",
             "bucket-read-time.png", ms, log=True)
