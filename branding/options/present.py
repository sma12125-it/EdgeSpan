"""Presentation boards for the three EdgeSpan logo studies."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

OUT = Path("/opt/cursor/artifacts")
FONT = Path("/workspace/branding/fonts/PlusJakartaSans-SemiBold.ttf")
FONT_R = Path("/workspace/branding/fonts/PlusJakartaSans-Regular.ttf")
LIGHT = (244, 246, 250)
DARK = (11, 16, 32)
INK = (18, 24, 43)
PAPER = (244, 247, 255)
BLUE = (37, 86, 255)


def font(size, regular=False):
    path = FONT_R if regular else FONT
    return ImageFont.truetype(str(path), size)


def board(draw_mark, title, filename):
    w, h = 1600, 900
    img = Image.new("RGB", (w, h), LIGHT)
    d = ImageDraw.Draw(img)
    d.rectangle((800, 0, 1600, 900), fill=DARK)
    for side, color, ground in ((0, INK, LIGHT), (800, PAPER, DARK)):
        mark = draw_mark(360, color)
        img.paste(mark, (side + 220, 170), mark)
        small = draw_mark(16, color)
        medium = draw_mark(32, color)
        # Pixel-adjacent tiles so the 16px study is readable.
        tile = Image.new("RGBA", (16 * 8, 16 * 8), (0, 0, 0, 0))
        tile.paste(small.resize((16 * 8, 16 * 8), Image.Resampling.NEAREST))
        img.paste(tile, (side + 250, 620), tile)
        img.paste(medium.resize((32 * 3, 32 * 3), Image.Resampling.NEAREST), (side + 430, 636), medium.resize((32 * 3, 32 * 3), Image.Resampling.NEAREST))
        d.text((side + 250, 780), "16 px", font=font(18, True), fill=(*color[:3],) if False else color)
        d.text((side + 430, 780), "32 px", font=font(18, True), fill=color)
        chip = (230, 234, 242) if ground == LIGHT else (28, 36, 58)
        d.rounded_rectangle((side + 48, 48, side + 210, 96), radius=18, fill=chip)
        d.text((side + 68, 60), title, font=font(20), fill=color)
    img.save(OUT / filename, "PNG")


def seam(size, color):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 32
    stroke = max(2, round(2 * s))

    def frame(x, y, w, h):
        d.rounded_rectangle((x * s, y * s, (x + w) * s, (y + h) * s), radius=2.5 * s, outline=color + (255,), width=stroke)

    frame(2.5, 8.5, 12, 13)
    frame(17.5, 6.5, 12, 17)
    # Cursor sitting on the seam, optically centered a hair high.
    d.polygon([
        (11 * s, 14.15 * s), (16.4 * s, 14.15 * s), (14.7 * s, 16 * s),
        (16.4 * s, 17.85 * s), (11 * s, 17.85 * s), (13.3 * s, 16 * s),
    ], fill=color + (255,))
    return img


def exit_mark(size, color):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 32
    stroke = max(2, round(2.25 * s))
    c = color + (255,)
    # Screen with the right edge open where the pointer leaves.
    d.arc((6 * s, 7.5 * s, 23.5 * s, 25 * s), 200, 430, fill=c, width=stroke)
    d.line((9 * s, 7.5 * s, 20.2 * s, 7.5 * s), fill=c, width=stroke)
    d.line((6 * s, 11 * s, 6 * s, 21.5 * s), fill=c, width=stroke)
    d.line((9 * s, 24.5 * s, 20.2 * s, 24.5 * s), fill=c, width=stroke)
    d.line((23.5 * s, 11 * s, 23.5 * s, 14 * s), fill=c, width=stroke)
    d.line((23.5 * s, 18 * s, 23.5 * s, 21.5 * s), fill=c, width=stroke)
    d.line((18.5 * s, 16 * s, 28 * s, 16 * s), fill=c, width=stroke)
    d.line((24.2 * s, 12.4 * s, 28 * s, 16 * s), fill=c, width=stroke)
    d.line((24.2 * s, 19.6 * s, 28 * s, 16 * s), fill=c, width=stroke)
    return img


def span(size, color):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 32
    stroke = max(2, round(2.25 * s))
    c = color + (255,)
    d.line((6 * s, 8.5 * s, 6 * s, 23.5 * s), fill=c, width=stroke)
    d.line((26 * s, 8.5 * s, 26 * s, 23.5 * s), fill=c, width=stroke)
    d.line((6 * s, 16 * s, 14.2 * s, 16 * s), fill=c, width=stroke)
    d.line((17.8 * s, 16 * s, 26 * s, 16 * s), fill=c, width=stroke)
    d.line((14.2 * s, 12.6 * s, 18.6 * s, 16 * s), fill=c, width=stroke)
    d.line((14.2 * s, 19.4 * s, 18.6 * s, 16 * s), fill=c, width=stroke)
    return img


if __name__ == "__main__":
    board(seam, "01   Seam", "EdgeSpan-logo-option-1.png")
    board(exit_mark, "02   Exit", "EdgeSpan-logo-option-2.png")
    board(span, "03   Span", "EdgeSpan-logo-option-3.png")
