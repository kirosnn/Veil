from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256, 512]
CIRCLE_RATIO = 0.70
SUPERSAMPLE = 16
ALPHA_CUTOFF = 8
OUTPUT_DIR = Path(__file__).resolve().parent


def generate_circle(size: int) -> Image.Image:
    scaled_size = size * SUPERSAMPLE
    diameter = round(size * CIRCLE_RATIO)
    scaled_diameter = diameter * SUPERSAMPLE
    offset = (scaled_size - scaled_diameter) / 2

    mask = Image.new("L", (scaled_size, scaled_size), 0)
    draw = ImageDraw.Draw(mask)
    draw.ellipse(
        [offset, offset, offset + scaled_diameter, offset + scaled_diameter],
        fill=255,
    )
    mask = mask.resize((size, size), Image.Resampling.LANCZOS)
    mask = mask.point(lambda value: 0 if value < ALPHA_CUTOFF else value)

    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    img.putalpha(mask)
    return img


def main():
    images = {}
    for size in SIZES:
        img = generate_circle(size)
        path = OUTPUT_DIR / f"veil_{size}.png"
        img.save(path, "PNG")
        images[size] = img
        print(f"  {path}")

    ico_sizes = [s for s in SIZES if s <= 256]
    images[max(ico_sizes)].save(
        OUTPUT_DIR / "veil.ico",
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
    )
    print(f"  {OUTPUT_DIR / 'veil.ico'}")


if __name__ == "__main__":
    main()
