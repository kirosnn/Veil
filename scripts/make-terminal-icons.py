"""
Generate terminal.ico (16/24/32/48/64/128/256 px) and sized PNGs
from apps/desktop/Veil/Assets/Terminal/TerminalLogo.png.

Output goes to apps/desktop/Veil/Assets/Terminal/  (and same path in VeilTerminal).
Run from the repo root:
    python scripts/make-terminal-icons.py
"""

import sys
import struct
import zlib
from pathlib import Path
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
SRC_PNG   = REPO_ROOT / "apps/desktop/Veil/Assets/Terminal/TerminalLogo.png"

VEIL_OUT     = REPO_ROOT / "apps/desktop/Veil/Assets/Terminal"
TERMINAL_OUT = REPO_ROOT / "apps/desktop/VeilTerminal/Assets/Terminal"

SIZES = [16, 24, 32, 48, 64, 128, 256]


def resize(img: Image.Image, size: int) -> Image.Image:
    return img.resize((size, size), Image.LANCZOS)


def build_ico(images: list[Image.Image]) -> bytes:
    """Build a proper multi-size .ico file from a list of RGBA PIL images."""
    ico_sizes = [img.size[0] for img in images]

    # Each entry is 16 bytes in the directory
    header_size = 6 + 16 * len(images)
    png_blobs = []
    for img in images:
        import io
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        png_blobs.append(buf.getvalue())

    # ICO header
    data = struct.pack("<HHH", 0, 1, len(images))

    offset = header_size
    for i, img in enumerate(images):
        w = img.size[0]
        h = img.size[1]
        blob = png_blobs[i]
        # width/height stored as 0 when == 256
        data += struct.pack(
            "<BBBBHHII",
            w if w < 256 else 0,
            h if h < 256 else 0,
            0,       # color count
            0,       # reserved
            1,       # planes
            32,      # bit count
            len(blob),
            offset,
        )
        offset += len(blob)

    for blob in png_blobs:
        data += blob

    return data


def main() -> None:
    if not SRC_PNG.exists():
        print(f"Source not found: {SRC_PNG}", file=sys.stderr)
        sys.exit(1)

    src = Image.open(SRC_PNG).convert("RGBA")

    resized = {s: resize(src, s) for s in SIZES}

    for out_dir in (VEIL_OUT, TERMINAL_OUT):
        out_dir.mkdir(parents=True, exist_ok=True)

        # Write individual PNGs
        for size, img in resized.items():
            img.save(out_dir / f"terminal_{size}.png")
            print(f"  {out_dir / f'terminal_{size}.png'}")

        # Write .ico with all sizes
        ico_path = out_dir / "terminal.ico"
        ico_bytes = build_ico([resized[s] for s in SIZES])
        ico_path.write_bytes(ico_bytes)
        print(f"  {ico_path}  ({len(ico_bytes):,} bytes, {len(SIZES)} sizes)")

    print("Done.")


if __name__ == "__main__":
    main()
