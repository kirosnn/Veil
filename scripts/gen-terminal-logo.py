"""
Generate a high-quality TerminalLogo.png (512x512) for Veil Terminal,
then produce all sized PNGs and terminal.ico.

Run from the repo root:
    python scripts/gen-terminal-logo.py
"""

import math
import struct
import io
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

REPO_ROOT = Path(__file__).resolve().parent.parent
VEIL_OUT  = REPO_ROOT / "apps/desktop/Veil/Assets/Terminal"
TERM_OUT  = REPO_ROOT / "apps/desktop/VeilTerminal/Assets/Terminal"
LOGO_SIZE = 512
SIZES     = [16, 24, 32, 48, 64, 128, 256]


def make_rounded_rect_mask(size, radius, aa=4):
    """Anti-aliased rounded rectangle mask."""
    s = size * aa
    r = radius * aa
    mask = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def draw_logo(size=LOGO_SIZE):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    pad = int(size * 0.0)
    corner = int(size * 0.22)

    # Background: deep dark blue-grey with slight gradient feel
    bg_color = (14, 18, 28, 255)
    mask = make_rounded_rect_mask(size, corner)

    bg = Image.new("RGBA", (size, size), bg_color)
    img.paste(bg, (0, 0), mask)

    # Subtle inner glow border
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    bw = max(2, size // 80)
    gd.rounded_rectangle(
        [bw, bw, size - bw - 1, size - bw - 1],
        radius=corner - bw,
        outline=(88, 166, 255, 28),
        width=bw,
    )
    img = Image.alpha_composite(img, glow)
    draw = ImageDraw.Draw(img)

    # Draw ">_" symbol using thick lines (no font needed)
    cx = size // 2
    cy = size // 2

    # Scale
    unit = size / 512

    # Chevron ">" — pointing right
    # apex at 40% from left, spans 24% height
    chev_x   = int(cx - 80 * unit)
    chev_tip = int(cx - 16 * unit)
    chev_top = int(cy - 76 * unit)
    chev_bot = int(cy + 76 * unit)
    lw = max(3, int(22 * unit))

    # upper arm
    draw.line([(chev_x, chev_top), (chev_tip, cy)], fill=(88, 166, 255, 255), width=lw)
    # lower arm
    draw.line([(chev_tip, cy), (chev_x, chev_bot)], fill=(88, 166, 255, 255), width=lw)

    # Underscore "_" — to the right of chevron
    und_x1 = int(cx - 4 * unit)
    und_x2 = int(cx + 130 * unit)
    und_y  = int(cy + 62 * unit)
    lw2 = max(3, int(20 * unit))
    draw.line([(und_x1, und_y), (und_x2, und_y)], fill=(230, 237, 243, 240), width=lw2)

    # Round line caps (circles at endpoints)
    r = lw // 2
    for pt in [(chev_x, chev_top), (chev_tip, cy), (chev_x, chev_bot)]:
        draw.ellipse([pt[0]-r, pt[1]-r, pt[0]+r, pt[1]+r], fill=(88, 166, 255, 255))

    r2 = lw2 // 2
    for pt in [(und_x1, und_y), (und_x2, und_y)]:
        draw.ellipse([pt[0]-r2, pt[1]-r2, pt[0]+r2, pt[1]+r2], fill=(230, 237, 243, 240))

    # Soft glow around the ">" symbol
    glow_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd2 = ImageDraw.Draw(glow_layer)
    lw_g = lw + int(14 * unit)
    gd2.line([(chev_x, chev_top), (chev_tip, cy)], fill=(88, 166, 255, 60), width=lw_g)
    gd2.line([(chev_tip, cy), (chev_x, chev_bot)], fill=(88, 166, 255, 60), width=lw_g)
    glow_layer = glow_layer.filter(ImageFilter.GaussianBlur(int(10 * unit)))
    img = Image.alpha_composite(img, glow_layer)

    # Re-apply background mask to clip everything
    final = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    final.paste(img, (0, 0), mask)
    return final


def build_ico(images):
    header_size = 6 + 16 * len(images)
    png_blobs = []
    for img in images:
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        png_blobs.append(buf.getvalue())

    data = struct.pack("<HHH", 0, 1, len(images))
    offset = header_size
    for i, img in enumerate(images):
        w, h = img.size
        blob = png_blobs[i]
        data += struct.pack(
            "<BBBBHHII",
            w if w < 256 else 0,
            h if h < 256 else 0,
            0, 0, 1, 32,
            len(blob), offset,
        )
        offset += len(blob)
    for blob in png_blobs:
        data += blob
    return data


def main():
    print("Generating TerminalLogo.png …")
    logo = draw_logo(LOGO_SIZE)

    for out_dir in (VEIL_OUT, TERM_OUT):
        out_dir.mkdir(parents=True, exist_ok=True)
        logo_path = out_dir / "TerminalLogo.png"
        logo.save(logo_path, "PNG")
        print(f"  {logo_path}")

        resized = {s: logo.resize((s, s), Image.LANCZOS) for s in SIZES}

        for s, img in resized.items():
            p = out_dir / f"terminal_{s}.png"
            img.save(p, "PNG")
            print(f"  {p}")

        ico_path = out_dir / "terminal.ico"
        ico_bytes = build_ico([resized[s] for s in SIZES])
        ico_path.write_bytes(ico_bytes)
        print(f"  {ico_path}  ({len(ico_bytes):,} bytes)")

    print("Done.")


if __name__ == "__main__":
    main()
