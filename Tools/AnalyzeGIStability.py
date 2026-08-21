#!/usr/bin/env python3
"""Measure stopped-camera GI convergence from GIStabilityCapture PNGs."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

import numpy as np
from PIL import Image


FRAME_PATTERN = re.compile(r"^(move_stop|view_stop)_f(\d+)\.png$", re.IGNORECASE)


def srgb_to_linear(value: np.ndarray) -> np.ndarray:
    return np.where(
        value <= 0.04045,
        value / 12.92,
        np.power((value + 0.055) / 1.055, 2.4),
    )


def load_rgb(path: Path, crop_top: float, crop_bottom: float) -> np.ndarray:
    with Image.open(path) as image:
        rgb = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    height = rgb.shape[0]
    first_row = min(height - 1, int(round(height * crop_top)))
    last_row = max(first_row + 1, height - int(round(height * crop_bottom)))
    rgb = rgb[first_row:last_row]
    return srgb_to_linear(rgb)


def luminance(rgb: np.ndarray) -> np.ndarray:
    return (
        rgb[..., 0] * 0.2126
        + rgb[..., 1] * 0.7152
        + rgb[..., 2] * 0.0722
    )


def compare(current: np.ndarray, reference: np.ndarray) -> dict[str, float]:
    if current.shape != reference.shape:
        raise ValueError(f"image shape mismatch: {current.shape} != {reference.shape}")

    current_luma = luminance(current)
    reference_luma = luminance(reference)
    log_delta = np.log2((current_luma + 0.01) / (reference_luma + 0.01))
    absolute_log_delta = np.abs(log_delta)
    rgb_delta = current - reference
    positive_log_delta = np.maximum(log_delta, 0.0)
    return {
        "mean_log2_luma": float(np.mean(log_delta)),
        "rms_log2_luma": float(np.sqrt(np.mean(log_delta * log_delta))),
        "p95_abs_log2_luma": float(np.percentile(absolute_log_delta, 95.0)),
        "p99_abs_log2_luma": float(np.percentile(absolute_log_delta, 99.0)),
        "p99_positive_log2_luma": float(np.percentile(positive_log_delta, 99.0)),
        "rms_linear_rgb": float(np.sqrt(np.mean(rgb_delta * rgb_delta))),
    }


def analyze(directory: Path, crop_top: float, crop_bottom: float) -> dict[str, object]:
    phases: dict[str, list[tuple[int, Path]]] = {}
    for path in directory.glob("*.png"):
        match = FRAME_PATTERN.match(path.name)
        if match is None:
            continue
        phases.setdefault(match.group(1).lower(), []).append((int(match.group(2)), path))

    result: dict[str, object] = {"directory": str(directory.resolve()), "phases": {}}
    for phase, frames in sorted(phases.items()):
        frames.sort(key=lambda item: item[0])
        reference_offset, reference_path = frames[-1]
        reference = load_rgb(reference_path, crop_top, crop_bottom)
        measurements = []
        for offset, path in frames:
            metrics = compare(load_rgb(path, crop_top, crop_bottom), reference)
            measurements.append({"offset": offset, "file": path.name, **metrics})
        result["phases"][phase] = {
            "reference_offset": reference_offset,
            "reference_file": reference_path.name,
            "frames": measurements,
        }
    return result


def print_table(result: dict[str, object]) -> None:
    for phase, phase_result in result["phases"].items():
        print(f"{phase} (reference f{phase_result['reference_offset']:03d})")
        print("frame  meanLog2  rmsLog2   p99Abs   p99Bright  rmsRGB")
        for frame in phase_result["frames"]:
            print(
                f"f{frame['offset']:03d}  "
                f"{frame['mean_log2_luma']:+.5f}  "
                f"{frame['rms_log2_luma']:.5f}  "
                f"{frame['p99_abs_log2_luma']:.5f}  "
                f"{frame['p99_positive_log2_luma']:.5f}  "
                f"{frame['rms_linear_rgb']:.6f}"
            )
        print()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("capture_directory", type=Path)
    parser.add_argument("--json", dest="json_path", type=Path)
    parser.add_argument(
        "--crop-top",
        type=float,
        default=0.0,
        help="Fraction of top rows to exclude, e.g. 0.18 for an animated sky.",
    )
    parser.add_argument(
        "--crop-bottom",
        type=float,
        default=0.0,
        help="Fraction of bottom rows to exclude, e.g. 0.08 for an error overlay.",
    )
    args = parser.parse_args()
    if not args.capture_directory.is_dir():
        parser.error(f"capture directory does not exist: {args.capture_directory}")
    if not 0.0 <= args.crop_top < 1.0:
        parser.error("--crop-top must be in [0, 1)")
    if not 0.0 <= args.crop_bottom < 1.0:
        parser.error("--crop-bottom must be in [0, 1)")
    if args.crop_top + args.crop_bottom >= 1.0:
        parser.error("--crop-top + --crop-bottom must be less than 1")

    result = analyze(args.capture_directory, args.crop_top, args.crop_bottom)
    if not result["phases"]:
        parser.error("no move_stop/view_stop captures were found")
    print_table(result)
    if args.json_path is not None:
        args.json_path.parent.mkdir(parents=True, exist_ok=True)
        args.json_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
