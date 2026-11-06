"""Check the local detector against the reviewed pixel-art examples."""

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("verify_examples", ROOT / "scripts/verify_examples.py")
examples = importlib.util.module_from_spec(spec)
spec.loader.exec_module(examples)


def main():
    reviews = json.loads((ROOT / "tests/reviewed-edges.json").read_text(encoding="utf-8"))
    expected_counts = {"active": (438, 435, 34), "cancelled": (326, 326, 8)}
    cases = []
    for name in ("active", "cancelled"):
        folder = ROOT / "examples" / name
        rgba = examples.read_rgba(folder / "before.png")
        pixels = []
        for i in range(0, len(rgba), 4):
            red, green, blue, alpha = rgba[i:i + 4]
            argb = (alpha << 24) | (red << 16) | (green << 8) | blue
            pixels.append(argb if argb < 2**31 else argb - 2**32)
        marks = [
            {"x": x, "y": y, "mode": mode}
            for mode in ("remove", "protect")
            for x, y in reviews[name][mode]
        ]
        cases.append({"name": name, "width": 128, "height": 128, "pixels": pixels, "marks": marks})

    env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    result = subprocess.run(
        ["dotnet", "run", "--project", str(ROOT / "tests/engine/Engine.csproj"),
         "--configuration", "Release", "--verbosity", "quiet"],
        input=json.dumps(cases), capture_output=True, text=True, encoding="utf-8", env=env,
    )
    if result.returncode:
        raise RuntimeError(f"Detector failed\n{result.stdout}\n{result.stderr}")
    detections = json.loads(result.stdout.strip().splitlines()[-1])

    for case, detected in zip(cases, detections, strict=True):
        name = case["name"]
        if detected["name"] != name:
            raise AssertionError("Detector returned examples in an unexpected order")
        mask = examples.read_rgba(ROOT / "examples" / name / "removed-mask.png")
        expected = {i for i in range(128 * 128) if mask[i * 4 + 3]}
        automatic = set(detected["automatic"])
        guided = set(detected["guided"])
        counts = (len(automatic), len(guided), detected["uncheckedEdges"])
        if counts != expected_counts[name]:
            raise AssertionError(f"{name}: unexpected automatic, guided, or unchecked counts {counts}")
        if not automatic <= expected or not guided <= expected or automatic | guided != expected:
            raise AssertionError(
                f"{name}: unreviewed automatic={len(automatic - expected)}, "
                f"unreviewed guided={len(guided - expected)}, missing={len(expected - (automatic | guided))}"
            )
        protected = reviews[name]["reviewed"]
        for point in protected:
            index = point["y"] * 128 + point["x"]
            actual = f'{case["pixels"][index] & 0xFFFFFFFF:08X}'
            if actual != point["argb"] or index in automatic or index in guided:
                raise AssertionError(f'{name}: reviewed {point["classification"]} pixel at '
                                     f'({point["x"]}, {point["y"]}) was changed or selected')
        for x, y in reviews[name]["protect"]:
            if y * 128 + x in automatic | guided:
                raise AssertionError(f"{name}: explicitly protected mark at ({x}, {y}) was selected")
        print(f"PASS {name}: automatic {len(automatic)}, guided {len(guided)}, "
              f"combined {len(expected)}, reviewed edge pixels protected {len(protected)}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, AssertionError, subprocess.SubprocessError) as error:
        print(f"FAIL detection: {error}", file=sys.stderr)
        sys.exit(1)
