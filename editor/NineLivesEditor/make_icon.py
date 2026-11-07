"""Build editor.ico from icon-source.png (the "9E" pixel-art icon).

    python editor/NineLivesEditor/make_icon.py

icon-source.png is a 32x32 pixel-art icon shown big (each pixel is a block). The 32 px icon uses
the exact pixels; bigger sizes come from the big picture, smaller ones from the 32 px version.
Needs Pillow.
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
GRID = 32


def main():
    src = Image.open(os.path.join(HERE, "icon-source.png")).convert("RGBA")
    w = src.size[0]
    cell = w / GRID
    # The original pixels: sample the middle of each block.
    px = Image.new("RGBA", (GRID, GRID))
    for y in range(GRID):
        for x in range(GRID):
            px.putpixel((x, y), src.getpixel((int((x + 0.5) * cell), int((y + 0.5) * cell))))
    sizes = {32: px}
    for n in (16, 20, 24):
        sizes[n] = px.resize((n, n), Image.LANCZOS)
    for n in (40, 48, 64, 128, 256):
        sizes[n] = src.resize((n, n), Image.LANCZOS)
    order = sorted(sizes)
    out = os.path.join(HERE, "editor.ico")
    sizes[256].save(out, format="ICO", sizes=[(n, n) for n in order], append_images=[sizes[n] for n in order if n != 256])
    print("wrote", out, order)


if __name__ == "__main__":
    main()
