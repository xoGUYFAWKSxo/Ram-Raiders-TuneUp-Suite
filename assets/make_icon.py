"""Builds the Galactic Tune-Up app icon: the spiral galaxy from galaxy.py with the CPU knight
(knight.png) in the bottom-right corner, the same layout as the Galactic Macro icon. galaxy.py is
copied from the Galactic Macro project.

Needs Pillow and NumPy:  python make_icon.py
Writes ../TuneUpIcon.png (1024 x 1024), the TuneUpIcon.ico files (16-256 px) used by the installer,
UI and engine, ../docs/assets/TuneUpIcon.png, and the installer's wizard-*.bmp images next to this
script. The galaxy is seeded, so the output is the same every time."""
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

import galaxy

HERE = Path(__file__).parent
ROOT = HERE.parent
SIZE = 1024
RIM = (90, 230, 255)  # soft rim light so the knight's dark armour stands off the dark sky


def knight(width):
    """Returns the knight, `width` px wide, in the bottom-right of a transparent SIZE x SIZE layer."""
    k = Image.open(HERE / 'knight.png').convert('RGBA')
    k = k.crop(k.getbbox())
    k = k.resize((width, round(k.height * width / k.width)), Image.LANCZOS)
    layer = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    layer.paste(k, (SIZE - width - 44, SIZE - k.height - 60), k)
    return layer


def with_rim_and_shadow(base, fg):
    a = np.asarray(fg)[..., 3].astype(float) / 255
    size = base.size
    shadow = Image.fromarray((a * 200).astype(np.uint8), 'L').filter(ImageFilter.GaussianBlur(14))
    shadow_rgba = Image.new('RGBA', size, (0, 0, 0, 0))
    shadow_rgba.paste((0, 0, 0, 255), (8, 14), shadow)
    rim = Image.fromarray((a * 150).astype(np.uint8), 'L').filter(ImageFilter.GaussianBlur(10))
    rim_rgba = Image.new('RGBA', size, (0, 0, 0, 0))
    rim_rgba.paste(RIM + (255,), (0, 0), rim)
    out = Image.alpha_composite(base, shadow_rgba)
    out = Image.alpha_composite(out, rim_rgba)
    return Image.alpha_composite(out, fg)


def compose():
    base = galaxy.icon(SIZE)
    out = with_rim_and_shadow(base, knight(470))
    # Keep the knight and its shadow inside the icon's rounded square.
    out.putalpha(Image.fromarray(np.minimum(np.asarray(out)[..., 3], np.asarray(base)[..., 3])))
    return out


def scaled(img, px):
    small = img.resize((px, px), Image.LANCZOS)
    if px <= 48:
        small = small.filter(ImageFilter.UnsharpMask(radius=1, percent=60, threshold=0))
    return small


def wizard_images(icon_img):
    """Installer artwork: the tall welcome-page panel and the small header image, each at 100% and 200%."""
    w, h = 328, 628
    light = galaxy.galaxy(h, 0.45 * h, h / 2, h * 0.40, rot=np.deg2rad(-28))
    g = galaxy.tonemap(light, 2.2)[:, (h - w) // 2:(h + w) // 2]
    panel = galaxy.to_image(galaxy.screen(galaxy.background(w, h, seed=11), g)).convert('RGBA')
    kw = 220
    k = Image.open(HERE / 'knight.png').convert('RGBA')
    k = k.crop(k.getbbox())
    kh = round(k.height * kw / k.width)
    fg = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    fg.paste(k.resize((kw, kh), Image.LANCZOS), ((w - kw) // 2, h - kh - 40))
    panel = with_rim_and_shadow(panel, fg).convert('RGB')
    panel.save(HERE / 'wizard-large-200.bmp')
    panel.resize((w // 2, h // 2), Image.LANCZOS).save(HERE / 'wizard-large-100.bmp')
    for px, name in ((55, 'wizard-small-100.bmp'), (110, 'wizard-small-200.bmp')):
        small = Image.new('RGBA', (px, px), (255, 255, 255, 255))
        small.alpha_composite(scaled(icon_img, px))
        small.convert('RGB').save(HERE / name)


if __name__ == '__main__':
    img = compose()
    img.save(ROOT / 'TuneUpIcon.png')
    img.resize((512, 512), Image.LANCZOS).save(ROOT / 'docs' / 'assets' / 'TuneUpIcon.png', optimize=True)
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [scaled(img, s) for s in sizes]
    for ico in (ROOT / 'TuneUpIcon.ico', ROOT / 'TuneUpUI' / 'TuneUpIcon.ico', ROOT / 'TuneUpEngine' / 'TuneUpIcon.ico'):
        frames[0].save(ico, sizes=[(s, s) for s in sizes], append_images=frames[1:])
    wizard_images(img)
