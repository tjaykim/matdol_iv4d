import numpy as np
from PIL import Image
from scipy import ndimage as nd

G = np.array([207, 200, 188], float)
TARGETS = [0.30, 0.25, 0.20, 0.15, 0.12, 0.09, 0.06, 0.03, 0.02, 0.01]
EXTENT_2X = 16.5

def glow_alpha(s, band):
    lo = s - 0.5; hi = s + 0.5
    a = np.zeros_like(s)
    a += TARGETS[0] * np.clip(np.minimum(hi, 0) - lo, 0, 1)
    for k, t in enumerate(TARGETS):
        e0, e1 = k * band, (k + 1) * band
        a += t * np.clip(np.minimum(hi, e1) - np.maximum(lo, e0), 0, None)
    return np.clip(a, 0, 1)

def compose(aF, objp, aS, scale):
    band = EXTENT_2X * scale / len(TARGETS)
    obj = aF >= 0.5
    d = nd.distance_transform_edt(~obj)
    s = np.where(obj, -1.0, d - 0.5)
    aG = glow_alpha(s, band)
    a_l = aG + aS * (1 - aG)
    cl_p = aG[..., None] * G
    out_a = aF + (1 - aF) * a_l
    out_p = objp + (1 - aF)[..., None] * cl_p
    rgb = np.where(out_a[..., None] > 1e-6, out_p / np.maximum(out_a, 1e-6)[..., None], 0)
    out = np.dstack([np.clip(rgb, 0, 255), np.clip(out_a * 255, 0, 255)])
    return np.rint(out).astype(np.uint8)

def help_frame(img):
    a = np.asarray(img.convert('RGBA')).astype(float)
    al = a[..., 3] / 255; C = a[..., :3]
    g0 = 128 / 255
    aF = np.where(al >= 250 / 255, 1.0, np.where(al > g0 + 1e-3, (al - g0) / (1 - g0), 0.0))
    objp = np.where((al >= 250 / 255)[..., None], C,
                    np.clip(al[..., None] * C - ((1 - aF) * g0)[..., None] * G, 0, None))
    objp = np.where((aF > 0)[..., None], objp, 0)
    return compose(aF, objp, np.zeros_like(al), 1.0)

def splash_frame(img, scale):
    a = np.asarray(img.convert('RGBA')).astype(float)
    al = a[..., 3] / 255; C = a[..., :3]; lum = C @ [0.299, 0.587, 0.114]
    dark = (lum < 70) & (al > 0.8)
    filled = nd.binary_fill_holes(dark)
    Ld = C[dark].mean(axis=0); Ldl = float(Ld @ [0.299, 0.587, 0.114]); Lc = 230.0
    d = nd.distance_transform_edt(~filled)
    edge = (~filled) & (d <= 2.5 * scale) & (al > 0.8) & (lum < Lc - 8)
    cov = np.clip((Lc - lum) / (Lc - Ldl), 0, 1)
    aF = np.where(filled, al, np.where(edge, cov * al, 0.0))
    objp = np.where(filled[..., None], C * al[..., None], np.where(edge[..., None], aF[..., None] * Ld, 0))
    shadow = (~filled) & (lum < 20) & (al < 0.5)
    aS = np.where(shadow, al, 0.0)
    return compose(aF, objp, aS, scale)

if __name__ == '__main__':
    import sys, glob, os
    assets = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..', 'Assets')
    save = dict(lossless=True, quality=100, method=6, exact=True)
    for p in sorted(glob.glob(os.path.join(assets, 'Loading', 'frame_*.webp'))) + [os.path.join(assets, 'Splash', 'still_384.webp')]:
        Image.fromarray(splash_frame(Image.open(p), 1.0), 'RGBA').save(p, **save)
    p = os.path.join(assets, 'Splash', 'still_192.webp')
    Image.fromarray(splash_frame(Image.open(p), 0.5), 'RGBA').save(p, **save)
    for p in sorted(glob.glob(os.path.join(assets, 'Help128', 'frame_*.webp'))):
        Image.fromarray(help_frame(Image.open(p)), 'RGBA').save(p, **save)
