#!/usr/bin/env bash
# Regenerates the app icon into src/Reviewer.Desktop/Assets: app.icns (macOS bundle), app.ico (Windows executable and
# installer) and app.png (the window icon). Runs on macOS, which has swift and iconutil.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
assets="$root/src/Reviewer.Desktop/Assets"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

swift "$root/eng/icons/make-icons.swift" "$work"
iconutil --convert icns "$work/app.iconset" --output "$assets/app.icns"
cp "$work/app.png" "$assets/app.png"

# An .ico is a directory of PNGs; Windows Vista and later read PNG entries at every size.
python3 - "$work/ico" "$assets/app.ico" <<'PY'
import os, struct, sys
src, dst = sys.argv[1], sys.argv[2]
sizes = sorted(int(f[:-4]) for f in os.listdir(src))
images = [open(os.path.join(src, f"{s}.png"), "rb").read() for s in sizes]
offset = 6 + 16 * len(images)
header = struct.pack("<HHH", 0, 1, len(images))
entries = b""
for s, data in zip(sizes, images):
    dim = 0 if s >= 256 else s
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
with open(dst, "wb") as f:
    f.write(header + entries + b"".join(images))
PY

echo "Wrote $assets/app.icns, app.ico and app.png"
