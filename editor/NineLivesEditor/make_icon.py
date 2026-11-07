"""Draw the editor's icon: "9E" in white on burnt orange (made for this project, not taken from the game).

    python editor/NineLivesEditor/make_icon.py      -> editor/NineLivesEditor/editor.ico

Each size is drawn on its own so small sizes stay sharp. Needs Pillow.
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
BG = (167, 58, 0)
FG = (246, 244, 240)
FONT = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "segoeuib.ttf")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size):
    scale = 4                                   # draw big, shrink: smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), BG + (255,))
    d = ImageDraw.Draw(img)
    text = "9E"
    # Largest font that fits with a small margin.
    margin = s * (0.08 if size >= 32 else 0.04)
    pt = s
    while pt > 4:
        font = ImageFont.truetype(FONT, int(pt))
        l, t, r, b = d.textbbox((0, 0), text, font=font)
        if r - l <= s - 2 * margin and b - t <= s - 2 * margin:
            break
        pt -= s / 64
    l, t, r, b = d.textbbox((0, 0), text, font=font)
    d.text(((s - (r - l)) / 2 - l, (s - (b - t)) / 2 - t), text, font=font, fill=FG + (255,))
    return img.resize((size, size), Image.LANCZOS)


def main():
    imgs = [draw(n) for n in SIZES]
    out = os.path.join(HERE, "editor.ico")
    imgs[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=imgs[:-1])
    imgs[-1].save(os.path.join(HERE, "editor-icon.png"))
    print("wrote", out)


if __name__ == "__main__":
    main()
