#!/usr/bin/env python3
"""Rasterize branding/logo.svg into Windows and Android icon assets."""
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path("/workspace/branding")
OUT = ROOT / "export"
WIN = Path("/workspace/src/EdgeSpan.Windows/Assets")
AND = Path("/workspace/android/app/src/main/res")
SVG = ROOT / "logo.svg"
SMALL = ROOT / "logo-small.svg"


def raster(svg: Path, size: int) -> Image.Image:
    dest = Path(f"/tmp/edgestpan-logo-{svg.stem}-{size}.png")
    subprocess.check_call(
        ["convert", "-background", "none", "-resize", f"{size}x{size}", str(svg), str(dest)]
    )
    return Image.open(dest).convert("RGBA")


def lines_svg(src: Path) -> Path:
    dest = Path(f"/tmp/{src.stem}-lines.svg")
    kept = [line for line in src.read_text().splitlines() if "<rect" not in line]
    dest.write_text("\n".join(kept) + "\n")
    return dest


def save_png(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, "PNG")


def circle_mask(size: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, size - 1, size - 1), fill=255)
    return mask


def fit_lines(svg: Path, canvas: int, art_fraction: float) -> Image.Image:
    """White line art, centered, scaled so the wide stroke span stays in art_fraction of the canvas."""
    # Strokes run from x=60 to x=684 in the 744 viewBox (see branding/logo.svg).
    span = 624 / 744
    rendered = int(round(canvas * art_fraction / span))
    mark = raster(lines_svg(svg), rendered)
    plate = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    plate.alpha_composite(mark, ((canvas - rendered) // 2, (canvas - rendered) // 2))
    return plate


def white_alpha(img: Image.Image) -> Image.Image:
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    src = img.load()
    dst = out.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = src[x, y]
            if a > 20 and r > 180 and g > 180 and b > 180:
                dst[x, y] = (255, 255, 255, a)
    return out


def main() -> None:
    master = raster(SVG, 1024)
    save_png(master, OUT / "logo-1024.png")
    save_png(master.resize((512, 512), Image.Resampling.LANCZOS), OUT / "logo-512.png")

    small64 = raster(SMALL, 64)
    small32 = small64.resize((32, 32), Image.Resampling.LANCZOS)
    small24 = raster(SMALL, 48).resize((24, 24), Image.Resampling.LANCZOS)
    small16 = small64.resize((16, 16), Image.Resampling.LANCZOS)
    save_png(small16, OUT / "logo-16.png")
    save_png(small32, OUT / "logo-32.png")

    save_png(master.resize((256, 256), Image.Resampling.LANCZOS), WIN / "icon.png")
    save_png(small32, WIN / "tray.png")

    by_size = {
        16: small16,
        24: small24,
        32: small32,
        48: master.resize((48, 48), Image.Resampling.LANCZOS),
        64: master.resize((64, 64), Image.Resampling.LANCZOS),
        128: master.resize((128, 128), Image.Resampling.LANCZOS),
        256: master.resize((256, 256), Image.Resampling.LANCZOS),
    }
    ordered = [by_size[n] for n in (16, 24, 32, 48, 64, 128, 256)]
    ordered[-1].save(
        WIN / "EdgeSpan.ico",
        format="ICO",
        sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)],
        append_images=ordered[:-1],
    )

    for folder, px in {
        "mipmap-mdpi": 48,
        "mipmap-hdpi": 72,
        "mipmap-xhdpi": 96,
        "mipmap-xxhdpi": 144,
        "mipmap-xxxhdpi": 192,
    }.items():
        tile = master.resize((px, px), Image.Resampling.LANCZOS)
        save_png(tile, AND / folder / "ic_launcher.png")
        round_icon = tile.copy()
        round_icon.putalpha(circle_mask(px))
        save_png(round_icon, AND / folder / "ic_launcher_round.png")

    # Adaptive foreground is 108dp. Keep strokes inside the center 66dp safe zone.
    foreground = fit_lines(SVG, 432, 66 / 108)
    save_png(foreground, AND / "drawable-nodpi" / "ic_launcher_foreground.png")
    save_png(white_alpha(foreground), AND / "drawable-nodpi" / "ic_launcher_monochrome.png")
    save_png(master.resize((432, 432), Image.Resampling.LANCZOS), AND / "drawable-nodpi" / "ic_brand.png")

    # Status-bar icon: white strokes only. Android tints by alpha.
    note = white_alpha(fit_lines(SMALL, 96, 0.78))
    for folder, px in {
        "drawable-mdpi": 24,
        "drawable-hdpi": 36,
        "drawable-xhdpi": 48,
        "drawable-xxhdpi": 72,
        "drawable-xxxhdpi": 96,
    }.items():
        save_png(note.resize((px, px), Image.Resampling.LANCZOS), AND / folder / "ic_stat_edgestpan.png")

    print("wrote", OUT / "logo-1024.png")


if __name__ == "__main__":
    main()
