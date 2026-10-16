"""Renders a face-on barred spiral galaxy (the Milky Way as seen from outside).

Made the app icon (ui/build/appicon.png, ui/build/windows/icon.ico,
cmd/engine/tray.ico, extension/src/icon.png) and the store images' backgrounds.
Needs Pillow and NumPy: python galaxy.py icon.png  (1024 x 1024, seeded, so
the same picture every time). Smaller sizes are Lanczos downscales, with a
light unsharp mask at 48 px and below."""
import numpy as np
from PIL import Image

RNG = np.random.default_rng(7)


def blur(img, sigma):
    """Gaussian blur of an HxWxC float array, via FFT."""
    h, w = img.shape[:2]
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.fftfreq(w)[None, :]
    k = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    out = np.empty_like(img)
    for c in range(img.shape[2]):
        out[..., c] = np.real(np.fft.ifft2(np.fft.fft2(img[..., c]) * k))
    return out


def splat(acc, x, y, w, color):
    """Adds points (in pixels) with weights w and an RGB color into acc."""
    h, wd = acc.shape[:2]
    H, _, _ = np.histogram2d(y, x, bins=[h, wd], range=[[0, h], [0, wd]], weights=w)
    acc += H[..., None] * np.asarray(color, float)[None, None, :]


def galaxy(size, radius, cx, cy, rot=0.0, scale_pts=1.0, bold=False):
    """Returns an HxWx3 float light map of the galaxy (not tone-mapped)."""
    R = radius
    acc = np.zeros((size, size, 3))
    dust = np.zeros((size, size, 1))
    n = lambda k: int(k * scale_pts)

    def place(px, py):
        c, s = np.cos(rot), np.sin(rot)
        return cx + px * c - py * s, cy + px * s + py * c

    # Spiral arms: logarithmic spirals starting at the ends of the bar.
    b = 0.30
    r0 = 0.20 * R
    arms = [(0.0, 1.0, 1.0), (np.pi, 1.0, 1.0), (np.pi * 0.5, 0.45, 0.7), (np.pi * 1.5, 0.45, 0.7)]
    if bold:
        arms = [(0.0, 1.0, 2.2), (np.pi, 1.0, 2.2)]
    for phase, strength, width in arms:
        tmax = np.log(1.0 / 0.20) / b
        m = n(260_000 * strength)
        t = tmax * RNG.random(m) ** 0.85
        r = r0 * np.exp(b * t)
        th = t + phase
        sig = 0.030 * R * width * (0.7 + 1.3 * r / R)
        dr = RNG.normal(0, 1, m) * sig
        ax = (r + dr) * np.cos(th) + RNG.normal(0, 0.012 * R, m)
        ay = (r + dr) * np.sin(th) + RNG.normal(0, 0.012 * R, m)
        fade = np.exp(-r / (0.55 * R)) * np.clip((R * 1.02 - r) / (0.15 * R), 0, 1)
        x, y = place(ax, ay)
        mix = np.clip(r / R, 0, 1)
        # Inner arm stars warmer, outer bluer.
        splat(acc, x, y, fade * 1.1, (0.66, 0.79, 1.0))
        # Dust lane on the inner (trailing) edge of each arm.
        dd = n(90_000 * strength)
        t2 = tmax * RNG.random(dd) ** 0.85
        r2 = r0 * np.exp(b * t2) * 0.95
        th2 = t2 + phase - 0.06
        if r2.size:
            dx = r2 * np.cos(th2) + RNG.normal(0, 0.010 * R, dd)
            dy = r2 * np.sin(th2) + RNG.normal(0, 0.010 * R, dd)
            x2, y2 = place(dx, dy)
            H, _, _ = np.histogram2d(y2, x2, bins=[size, size], range=[[0, size], [0, size]],
                                     weights=np.exp(-r2 / (0.6 * R)) * (r2 > 0.22 * R))
            dust[..., 0] += H
        # Star-forming knots: pink nebulae and blue clusters along the arms.
        k = 0 if bold else n(170 * strength)
        tk = tmax * RNG.random(k) ** 0.8
        rk = r0 * np.exp(b * tk) + RNG.normal(0, 0.02 * R, k)
        thk = tk + phase
        kx, ky = place(rk * np.cos(thk), rk * np.sin(thk))
        for j in range(k):
            pink = RNG.random() < 0.55
            col = (1.0, 0.45, 0.65) if pink else (0.65, 0.8, 1.0)
            cnt = n(300)
            spread = 0.006 * R * (1.5 if pink else 0.8)
            splat(acc, kx[j] + RNG.normal(0, spread, cnt), ky[j] + RNG.normal(0, spread, cnt),
                  np.full(cnt, 0.25 * (1 if pink else 1.4)), col)

    # Diffuse disk.
    m = n(400_000)
    r = RNG.exponential(0.33 * R, m)
    th = RNG.random(m) * 2 * np.pi
    x, y = place(r * np.cos(th), r * np.sin(th))
    splat(acc, x, y, np.full(m, 0.10) * (r < 1.05 * R), (0.75, 0.8, 1.0))

    # Bar and bulge: old, yellowish stars.
    m = n(260_000)
    bx = RNG.normal(0, 0.17 * R, m)
    by = RNG.normal(0, 0.042 * R, m)
    x, y = place(bx, by)
    splat(acc, x, y, np.full(m, 0.55), (1.0, 0.78, 0.45))
    m = n(220_000)
    rr = RNG.normal(0, 0.06 * R, (2, m))
    x, y = place(rr[0], rr[1])
    splat(acc, x, y, np.full(m, 0.35), (1.0, 0.86, 0.62))

    s = size / 1024
    light = 0.55 * blur(acc, 1.0 * s) + 0.45 * blur(acc, 4.0 * s) + 0.35 * blur(acc, 14.0 * s)
    d = blur(dust, 7.0 * s)
    d = d / (d.max() + 1e-9)
    light *= (1 - 0.55 * np.clip(d * 2.0, 0, 1))
    # Bright core glow on top.
    yy, xx = np.mgrid[0:size, 0:size]
    rc2 = ((xx - cx) ** 2 + (yy - cy) ** 2) / (0.035 * R) ** 2
    light += np.exp(-rc2)[..., None] * np.array([1.0, 0.93, 0.8]) * np.percentile(light, 99.5) * 0.8
    return light


def tonemap(light, exposure):
    v = 1 - np.exp(-exposure * light / np.percentile(light, 99.0))
    v = np.clip(v, 0, 1) ** 0.85
    g = v.mean(axis=2, keepdims=True)
    return np.clip(g + (v - g) * 1.35, 0, 1)


def background(w, h, stars=True, seed=3):
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w]
    d = np.sqrt(((xx - w / 2) / w) ** 2 + ((yy - h / 2) / h) ** 2)
    top = np.array([0.055, 0.07, 0.17])
    edge = np.array([0.012, 0.015, 0.045])
    t = np.clip(d / 0.75, 0, 1)[..., None]
    bg = top * (1 - t) + edge * t
    if stars:
        acc = np.zeros((h, w, 3))
        m = int(w * h / 900)
        H, _, _ = np.histogram2d(rng.random(m) * h, rng.random(m) * w, bins=[h, w], range=[[0, h], [0, w]],
                                 weights=rng.random(m) ** 6 * 3)
        acc += H[..., None]
        bg = bg + np.clip(blur(acc, 0.7), 0, 1) * np.array([0.85, 0.9, 1.0])
    return bg


def rounded_mask(size, radius_frac):
    """Anti-aliased rounded-square alpha (supersampled 4x)."""
    ss = 4
    S = size * ss
    r = radius_frac * S
    yy, xx = np.mgrid[0:S, 0:S] + 0.5
    qx = np.clip(np.maximum(r - xx, xx - (S - r)), 0, None)
    qy = np.clip(np.maximum(r - yy, yy - (S - r)), 0, None)
    inside = (qx ** 2 + qy ** 2) <= r ** 2
    return inside.reshape(size, ss, size, ss).mean(axis=(1, 3))


def to_image(rgb, alpha=None):
    a = (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8)
    if alpha is None:
        return Image.fromarray(a, 'RGB')
    al = (np.clip(alpha, 0, 1) * 255 + 0.5).astype(np.uint8)
    return Image.fromarray(np.dstack([a, al]), 'RGBA')


def screen(base, glow):
    return 1 - (1 - base) * (1 - glow)


def icon(size=1024, exposure=2.2, bold=False):
    light = galaxy(size, (0.47 if bold else 0.45) * size, size / 2, size / 2, rot=np.deg2rad(-28), bold=bold)
    g = tonemap(light, exposure)
    rgb = screen(background(size, size, stars=not bold, seed=11), g)
    return to_image(rgb, rounded_mask(size, 0.185))


if __name__ == '__main__':
    import sys
    icon().save(sys.argv[1])
