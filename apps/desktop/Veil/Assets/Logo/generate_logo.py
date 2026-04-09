from pathlib import Path

from PIL import Image

SIZES = [16, 24, 32, 48, 64, 128, 256, 512]
OUTPUT_DIR = Path(__file__).resolve().parent
SOURCE_PATH = OUTPUT_DIR / "new.png"


def load_source() -> Image.Image:
    image = Image.open(SOURCE_PATH).convert("RGBA")
    width, height = image.size
    square_size = min(width, height)
    left = (width - square_size) // 2
    top = (height - square_size) // 2
    return image.crop((left, top, left + square_size, top + square_size))


def render_size(source: Image.Image, size: int) -> Image.Image:
    return source.resize((size, size), Image.Resampling.LANCZOS)


def main():
    source = load_source()
    images = {}
    for size in SIZES:
        img = render_size(source, size)
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
