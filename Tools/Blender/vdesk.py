"""Desk props: compass, scallop, pebbles and pencil (baked "Desk props" atlas), nautical chart and postcard
(numpy-painted "Desk paper" atlas) and the mooring rope / cotton coil (tiled "Cotton rope" texture).

All meshes are built in their Unity pivot space at real size and rest on the tabletop (Unity y = -0.11)."""
import bpy, bmesh, math
import numpy as np
from mathutils import Vector, Matrix, noise
import vcore as vc
import vmaterials as vm

TABLE = -.11
OBJ = vc.SCENE["objects"]
PEBBLES = ["Beach pebble 6", "Beach pebble 7", "Beach pebble 8", "Beach pebble 9", "Beach pebble 10"]
SHELL = .27   # Scallop keepsake scale; the small scallop reuses the mesh at .19 / .27


def pose(name, y=None):
    o = OBJ[name]
    p = list(o["pos"])
    if y is not None:
        p[1] = y
    return vc.unity_pose(p, o["rot"])


# ================================================================== numpy painting

def fbm(h, w, seed, cells=(4, 9, 23, 61), weights=(.45, .3, .17, .08)):
    rng = np.random.default_rng(seed)
    out = np.zeros((h, w), np.float32)
    for c, wt in zip(cells, weights):
        gw, gh = c + 2, max(2, int(round(c * h / w))) + 2
        g = rng.random((gh, gw)).astype(np.float32)
        y = np.linspace(0, gh - 2.001, h)
        x = np.linspace(0, gw - 2.001, w)
        y0, x0 = y.astype(int), x.astype(int)
        fy, fx = (y - y0)[:, None], (x - x0)[None, :]
        fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
        a, b = g[y0][:, x0], g[y0][:, x0 + 1]
        c_, d = g[y0 + 1][:, x0], g[y0 + 1][:, x0 + 1]
        out += wt * ((a * (1 - fx) + b * fx) * (1 - fy) + (c_ * (1 - fx) + d * fx) * fy)
    return out / sum(weights)


class Paper:
    """A sheet painted in its own metres: X to the right (Unity +x), Z up the image (Unity +z)."""

    def __init__(self, w, h, px_w, px_h, color, seed):
        self.w, self.h, self.W, self.H = w, h, px_w, px_h
        self.px = px_w / w
        xs = (np.arange(px_w) + .5) / self.px - w / 2
        zs = (np.arange(px_h) + .5) / self.px - h / 2
        self.X, self.Z = np.meshgrid(xs.astype(np.float32), zs.astype(np.float32))
        blot = fbm(px_h, px_w, seed)
        fine = fbm(px_h, px_w, seed + 1, cells=(80, 190, 420), weights=(.3, .4, .3))
        self.rgb = np.array(color, np.float32) * (.92 + .14 * blot[..., None]) * (.97 + .06 * fine[..., None])
        self.height = .6 * fine + .25 * blot
        self.ink_cover = np.zeros((px_h, px_w), np.float32)

    def window(self, x0, z0, x1, z1):
        c0 = max(0, int((x0 + self.w / 2) * self.px) - 2)
        c1 = min(self.W, int((x1 + self.w / 2) * self.px) + 3)
        r0 = max(0, int((z0 + self.h / 2) * self.px) - 2)
        r1 = min(self.H, int((z1 + self.h / 2) * self.px) + 3)
        return np.s_[r0:r1, c0:c1] if c1 > c0 and r1 > r0 else None

    def cover(self, sd):
        """Antialiased coverage from a signed distance (metres, positive inside)."""
        return np.clip(sd * self.px + .5, 0, 1)

    def paint(self, cover, color, alpha=1.0, sl=np.s_[:, :], ink=True):
        a = (np.clip(np.asarray(cover, np.float32), 0, 1) * alpha)[..., None]
        self.rgb[sl] = self.rgb[sl] * (1 - a) + np.array(color, np.float32) * a
        if ink:
            self.ink_cover[sl] = np.maximum(self.ink_cover[sl], a[..., 0])

    def line(self, pts, width, color, alpha=1.0, dash=None, closed=False):
        pts = [np.array(p, np.float32) for p in pts]
        segs = list(zip(pts, pts[1:])) + ([(pts[-1], pts[0])] if closed else [])
        lo = np.min(pts, 0) - width - .002
        hi = np.max(pts, 0) + width + .002
        sl = self.window(lo[0], lo[1], hi[0], hi[1])
        if sl is None:
            return
        X, Z = self.X[sl], self.Z[sl]
        d = np.full(X.shape, 1e9, np.float32)
        along = np.zeros(X.shape, np.float32)
        run = 0.0
        for a, b in segs:
            ab = b - a
            L = float(np.hypot(*ab)) or 1e-9
            t = np.clip(((X - a[0]) * ab[0] + (Z - a[1]) * ab[1]) / (L * L), 0, 1)
            dd = np.hypot(X - a[0] - t * ab[0], Z - a[1] - t * ab[1])
            closer = dd < d
            d = np.where(closer, dd, d)
            along = np.where(closer, run + t * L, along)
            run += L
        c = self.cover(width / 2 - d)
        if dash:
            c *= ((along / dash[0]) % 1.0) < dash[1]
        self.paint(c, color, alpha, sl)

    def circle(self, cx, cz, r, width, color, alpha=1.0):
        sl = self.window(cx - r - width, cz - r - width, cx + r + width, cz + r + width)
        if sl is None:
            return
        d = np.abs(np.hypot(self.X[sl] - cx, self.Z[sl] - cz) - r)
        self.paint(self.cover(width / 2 - d), color, alpha, sl)

    def disc(self, cx, cz, r, color, alpha=1.0, ink=True):
        sl = self.window(cx - r, cz - r, cx + r, cz + r)
        if sl is None:
            return
        d = np.hypot(self.X[sl] - cx, self.Z[sl] - cz)
        self.paint(self.cover(r - d), color, alpha, sl, ink)

    def rect(self, x0, z0, x1, z1, color, alpha=1.0, ink=True):
        sl = self.window(x0, z0, x1, z1)
        if sl is None:
            return
        X, Z = self.X[sl], self.Z[sl]
        sd = np.minimum(np.minimum(X - x0, x1 - X), np.minimum(Z - z0, z1 - Z))
        self.paint(self.cover(sd), color, alpha, sl, ink)

    def frame(self, x0, z0, x1, z1, width, color, alpha=1.0):
        self.line([(x0, z0), (x1, z0), (x1, z1), (x0, z1)], width, color, alpha, closed=True)

    def triangle(self, a, b, c, color, alpha=1.0, outline=None):
        pts = np.array((a, b, c), np.float32)
        lo, hi = pts.min(0), pts.max(0)
        sl = self.window(lo[0], lo[1], hi[0], hi[1])
        if sl is None:
            return
        X, Z = self.X[sl], self.Z[sl]
        area = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
        sgn = 1 if area > 0 else -1
        sd = np.full(X.shape, 1e9, np.float32)
        for p, q in ((a, b), (b, c), (c, a)):
            e = np.array(q) - np.array(p)
            L = float(np.hypot(*e)) or 1e-9
            sd = np.minimum(sd, sgn * (e[0] * (Z - p[1]) - e[1] * (X - p[0])) / L)
        self.paint(self.cover(sd), color, alpha, sl)
        if outline:
            self.paint(self.cover(outline[0] / 2 - np.abs(sd)) * (sd > -outline[0]), outline[1], 1.0, sl)

    def scribble(self, x0, z, x1, size, color, rng, width=.0012, alpha=.9, bold=1.0):
        """Pseudo handwriting / lettering: words of small looping strokes."""
        x = x0
        while x < x1:
            n = int(rng.integers(3, 9))
            wlen = min(n * size * .55, x1 - x)
            if wlen < size:
                break
            k = max(6, int(wlen / size * 7))
            pts = []
            for i in range(k + 1):
                t = i / k
                pts.append((x + t * wlen + .25 * size * math.sin(t * n * 6.3),
                            z + size * (.35 * math.sin(t * n * 9.4 + rng.random()) + .15 * rng.standard_normal())))
            self.line(pts, width * bold, color, alpha)
            x += wlen + size * .7

    def creases(self, xs=(), zs=(), depth=.5):
        for x in xs:
            g = np.exp(-((self.X - x) / .0025) ** 2)
            self.height -= depth * g
            self.rgb *= (1 - .06 * g)[..., None]
        for z in zs:
            g = np.exp(-((self.Z - z) / .0025) ** 2)
            self.height -= depth * g
            self.rgb *= (1 - .06 * g)[..., None]

    def age(self, rng, spots=30, edge=.06, edge_color=(.55, .38, .2)):
        edge_d = np.minimum(self.w / 2 - np.abs(self.X), self.h / 2 - np.abs(self.Z))
        self.paint(1 - np.clip(edge_d / edge, 0, 1) ** .6, edge_color, .32, ink=False)
        for _ in range(spots):
            x, z = (rng.random() - .5) * self.w, (rng.random() - .5) * self.h
            self.disc(x, z, .002 + .007 * rng.random() ** 2, edge_color, .12 + .18 * rng.random(), ink=False)

    def maps(self, gloss_paper=.1, gloss_ink=.24, strength=1.2):
        hy, hx = np.gradient(self.height)
        n = np.dstack((-hx * strength, -hy * strength, np.ones_like(hx)))
        n /= np.linalg.norm(n, axis=2, keepdims=True)
        mask = np.zeros((self.H, self.W, 4), np.float32)
        mask[..., 1] = 1
        mask[..., 3] = gloss_paper + (gloss_ink - gloss_paper) * self.ink_cover
        return np.clip(self.rgb, 0, 1), n * .5 + .5, mask


SEPIA, INK, RUST = (.27, .17, .09), (.16, .12, .1), (.56, .2, .1)


def paint_chart(W, H):
    rng = np.random.default_rng(7)
    P = Paper(.95, .66, W, H, (.87, .77, .57), 11)
    X, Z = P.X, P.Z

    def coast_r(a):
        return .15 + .035 * np.sin(a * 5) + .02 * np.cos(a * 3) + .006 * np.sin(a * 23 + 1) + .003 * np.sin(a * 41)

    phi = np.arctan2(Z, X / 1.8)
    land = np.hypot(X / 1.8, Z) < coast_r(phi)
    coast = [(math.cos(a) * coast_r(a) * 1.8, math.sin(a) * coast_r(a)) for a in np.linspace(0, 2 * math.pi, 241)]
    # sea wash, land tint, stipple and shore hatching
    P.paint(~land * 1.0, (.66, .72, .66), .14, ink=False)
    P.paint(land * 1.0, (.8, .64, .38), .35, ink=False)
    P.paint(land & (rng.random(land.shape) < .035), SEPIA, .5)
    # distance to the coast in a window (outside water-lines and inner hatching)
    sl = P.window(-.37, -.28, .37, .28)
    Xs, Zs = X[sl], Z[sl]
    d = np.full(Xs.shape, 1e9, np.float32)
    for a, b in zip(coast, coast[1:]):
        a, b = np.array(a), np.array(b)
        ab = b - a
        t = np.clip(((Xs - a[0]) * ab[0] + (Zs - a[1]) * ab[1]) / max(ab @ ab, 1e-12), 0, 1)
        d = np.minimum(d, np.hypot(Xs - a[0] - t * ab[0], Zs - a[1] - t * ab[1]))
    inside = land[sl]
    for k, off in enumerate((.006, .013, .021, .031, .043)):
        P.paint(P.cover(.0005 - np.abs(d - off)) * ~inside, SEPIA, .7 - .12 * k, sl)
    hatch = np.abs(((Xs + Zs) / .006) % 1 - .5) * .006
    P.paint(P.cover(.0006 - hatch) * inside * np.clip(1 - d / .022, 0, 1), SEPIA, .7, sl)
    P.paint(P.cover(.0017 - d), SEPIA, 1.0, sl)
    # hills inland
    for hx, hz, s in ((-.08, .04, .018), (-.02, .07, .022), (.05, .03, .016), (.12, -.02, .014), (-.14, -.04, .015), (.02, -.06, .013)):
        P.line([(hx - s, hz - s * .3), (hx - s * .3, hz + s * .45), (hx, hz + s * .2), (hx + s * .35, hz + s * .55), (hx + s, hz - s * .3)], .0012, SEPIA, .85)
    # graticule and neat-line border with alternating bars
    inner_x, inner_z = .475 - .045, .33 - .045
    within = (np.abs(X) < inner_x) & (np.abs(Z) < inner_z)
    for i in range(-3, 4):
        P.paint(P.cover(.0005 - np.abs(Z - i * .11)) * within, SEPIA, .45)
        P.paint(P.cover(.0005 - np.abs(X - i * .15)) * within, SEPIA, .45)
    P.frame(-inner_x, -inner_z, inner_x, inner_z, .0016, INK)
    P.frame(-inner_x - .009, -inner_z - .009, inner_x + .009, inner_z + .009, .0009, INK)
    band = (np.abs(X) < inner_x + .009) & (np.abs(Z) < inner_z + .009) & ~((np.abs(X) < inner_x + .0005) & (np.abs(Z) < inner_z + .0005))
    horizontal = (inner_z + .009 - np.abs(Z)) < (inner_x + .009 - np.abs(X))
    param = np.where(horizontal, X, Z)
    P.paint(band & ((np.floor(param / .025) % 2) == 0), INK, .9)
    # compass rose
    cx, cz = -.3, .155
    P.circle(cx, cz, .064, .0011, INK)
    P.circle(cx, cz, .069, .0007, INK)
    for k in range(72):
        a = k * math.pi / 36
        r0 = .064 if k % 2 else .062
        P.line([(cx + math.sin(a) * r0, cz + math.cos(a) * r0), (cx + math.sin(a) * .069, cz + math.cos(a) * .069)], .0005, INK)
    for k in sorted(range(16), key=lambda k: (k % 4 == 0, k % 2 == 0)):
        L = .078 if k % 4 == 0 else .056 if k % 2 == 0 else .036
        a = k * math.pi / 8
        dx, dz, px, pz = math.sin(a), math.cos(a), math.cos(a), -math.sin(a)
        tip = (cx + dx * L, cz + dz * L)
        wdt = L * .2
        side = lambda s: (cx + dx * L * .26 + px * wdt * s, cz + dz * L * .26 + pz * wdt * s)
        dark = RUST if k % 4 == 0 else INK
        P.triangle((cx, cz), tip, side(1), dark, .92, (.0006, INK))
        P.triangle((cx, cz), tip, side(-1), (.93, .86, .68), 1.0, (.0006, INK))
    P.disc(cx, cz, .004, INK)
    n0 = cz + .085
    P.line([(cx - .005, n0), (cx - .005, n0 + .013), (cx + .005, n0), (cx + .005, n0 + .013)], .0011, INK)
    # course, start anchor and destination cross
    course = [(-.38, -.22), (-.2, -.06), (.12, .11), (.32, .2)]
    P.line(course, .0022, RUST, .95, dash=(.014, .6))
    P.circle(-.38, -.22, .007, .0013, RUST)
    P.line([(.31, .19), (.33, .21)], .002, RUST)
    P.line([(.31, .21), (.33, .19)], .002, RUST)
    # soundings and a title cartouche
    for _ in range(90):
        x, z = (rng.random() - .5) * .8, (rng.random() - .5) * .52
        if abs(x) > inner_x - .02 or abs(z) > inner_z - .02 or np.hypot(x / 1.8, z) < .24 or np.hypot(x - cx, z - cz) < .1:
            continue
        P.scribble(x, z, x + .006 + .006 * rng.random(), .0035, SEPIA, rng, width=.0007, alpha=.75)
    x0, z0, x1, z1 = .2, -.27, .41, -.19
    P.rect(x0, z0, x1, z1, (.9, .82, .62), 1.0, ink=False)
    P.frame(x0, z0, x1, z1, .0013, INK)
    P.frame(x0 + .005, z0 + .005, x1 - .005, z1 - .005, .0006, INK)
    P.scribble(x0 + .02, -.212, x1 - .02, .008, INK, rng, width=.0016, bold=1.2)
    P.scribble(x0 + .03, -.236, x1 - .03, .004, SEPIA, rng, width=.0008)
    P.scribble(x0 + .045, -.253, x1 - .045, .004, SEPIA, rng, width=.0008)
    # folds, coffee ring, foxing and darkened edges
    P.creases(xs=(0.0,), zs=(0.0,), depth=.6)
    P.circle(.27, .02, .055, .004, (.45, .28, .12), .16)
    P.disc(.27, .02, .053, (.5, .33, .15), .05, ink=False)
    P.age(rng, spots=40)
    return P


def paint_postcard(W, H):
    rng = np.random.default_rng(3)
    P = Paper(.72, .95, W, H, (.95, .91, .82), 21)
    blue = (.14, .17, .3)
    P.line([(0, -.39), (0, .37)], .0018, (.42, .32, .22), .9)
    for z in (-.06, -.14, -.22, -.30):
        P.line([(.04, z), (.31, z)], .0013, (.42, .32, .22), .8)
    # stamp: perforated white paper, framed picture of a little boat
    sx0, sz0, sx1, sz1 = .175, .285, .315, .445
    P.rect(sx0 - .006, sz0 - .006, sx1 + .006, sz1 + .006, (.97, .95, .9), 1.0, ink=False)
    for i in range(13):
        t = i / 12
        for x, z in ((sx0 - .006 + t * (sx1 - sx0 + .012), sz0 - .006), (sx0 - .006 + t * (sx1 - sx0 + .012), sz1 + .006),
                     (sx0 - .006, sz0 - .006 + t * (sz1 - sz0 + .012)), (sx1 + .006, sz0 - .006 + t * (sz1 - sz0 + .012))):
            P.disc(x, z, .0035, (.95, .91, .82), ink=False)
    P.rect(sx0, sz0 + .055, sx1, sz1, (.66, .78, .84), 1.0)
    P.rect(sx0, sz0, sx1, sz0 + .055, (.18, .38, .55), 1.0)
    for k in range(4):
        z = sz0 + .012 + k * .011
        P.line([(sx0 + .005 + .012 * j, z + .0025 * math.sin(j * 1.7 + k)) for j in range(12)], .001, (.75, .85, .9), .8)
    P.triangle((.245, .37), (.245, .43), (.29, .372), (.97, .94, .86))
    P.triangle((.24, .37), (.24, .415), (.205, .372), (.97, .94, .86))
    P.triangle((.2, .358), (.3, .358), (.285, .343), (.45, .2, .1))
    P.disc(.29, .425, .009, (.95, .8, .45))
    P.frame(sx0, sz0, sx1, sz1, .0012, (.32, .25, .2))
    # postmark and cancellation waves
    pm = (.13, .375)
    P.circle(*pm, .05, .0022, (.14, .13, .15), .72)
    P.circle(*pm, .035, .0012, (.14, .13, .15), .72)
    P.scribble(pm[0] - .022, pm[1] + .002, pm[0] + .022, .006, (.14, .13, .15), rng, width=.0016, alpha=.7)
    for k in range(4):
        z = pm[1] - .03 + k * .02
        P.line([(.01 + .009 * j, z + .006 * math.sin(j * .9)) for j in range(13)], .0018, (.14, .13, .15), .6)
    # printed heading, address and a short handwritten note
    P.scribble(-.15, .415, .15, .011, (.3, .22, .16), rng, width=.0026)
    P.scribble(.06, -.045, .27, .008, blue, rng, width=.0013)
    P.scribble(.06, -.125, .3, .008, blue, rng, width=.0013)
    P.scribble(.06, -.205, .22, .008, blue, rng, width=.0013)
    for i, z in enumerate((.25, .19, .13, .07, .01, -.05, -.11)):
        P.scribble(-.31, z, -.04 - (.08 if i == 6 else 0), .009, blue, rng, width=.0013)
    P.line([(-.3, -.2), (-.27, -.19), (-.24, -.21), (-.2, -.185), (-.16, -.2)], .0016, blue)
    P.age(rng, spots=14, edge=.03, edge_color=(.6, .48, .32))
    return P


def paint_dial(N, R):
    """Compass card, in metres around the pivot (R = dial radius)."""
    rng = np.random.default_rng(5)
    P = Paper(2 * R, 2 * R, N, N, (.94, .9, .8), 31)
    for r, w in ((.205, .0014), (.19, .0008), (.13, .0009), (.124, .0005)):
        P.circle(0, 0, r, w, INK)
    for k in range(72):
        a = k * math.pi / 36
        r0 = .175 if k % 9 == 0 else .182 if k % 2 == 0 else .19
        P.line([(math.sin(a) * r0, math.cos(a) * r0), (math.sin(a) * .205, math.cos(a) * .205)], .0012 if k % 9 == 0 else .0008, INK)
    for k in sorted(range(16), key=lambda k: (k % 4 == 0, k % 2 == 0)):
        L = .12 if k % 4 == 0 else .085 if k % 2 == 0 else .055
        a = k * math.pi / 8
        dx, dz, px, pz = math.sin(a), math.cos(a), math.cos(a), -math.sin(a)
        tip = (dx * L, dz * L)
        side = lambda s: (dx * L * .25 + px * L * .2 * s, dz * L * .25 + pz * L * .2 * s)
        P.triangle((0, 0), tip, side(1), RUST if k == 0 else INK, .9, (.0007, INK))
        P.triangle((0, 0), tip, side(-1), (.96, .92, .82), 1.0, (.0007, INK))
    s = .011   # N E S W letters
    glyphs = {
        0: [[(-s * .6, -s), (-s * .6, s), (s * .6, -s), (s * .6, s)]],
        2: [[(s * .6, s), (-s * .6, s), (-s * .6, -s), (s * .6, -s)], [(-s * .6, 0), (s * .4, 0)]],
        4: [[(s * .6, s * .8), (0, s), (-s * .6, s * .5), (s * .6, -s * .5), (0, -s), (-s * .6, -s * .8)]],
        6: [[(-s * .8, s), (-s * .4, -s), (0, s * .3), (s * .4, -s), (s * .8, s)]],
    }
    for q, strokes in glyphs.items():
        a = q * math.pi / 4
        c = (math.sin(a) * .155, math.cos(a) * .155)
        for st in strokes:
            P.line([(c[0] + x, c[1] + z) for x, z in st], .0022, RUST if q == 0 else INK)
    P.age(rng, spots=6, edge=.02)
    return P


# ================================================================== geometry

def slab(bm, nu, nv, top, bottom, uv_top=None, uv_back=None):
    """Closed sheet: top(a, b) / bottom(a, b) return Unity-local points for a, b in [0, 1]."""
    uv = bm.loops.layers.uv.verify()
    T = [[bm.verts.new(vc.U(*top(i / nu, j / nv))) for j in range(nv + 1)] for i in range(nu + 1)]
    B = [[bm.verts.new(vc.U(*bottom(i / nu, j / nv))) for j in range(nv + 1)] for i in range(nu + 1)]
    par = {}
    for i in range(nu + 1):
        for j in range(nv + 1):
            par[T[i][j]] = par[B[i][j]] = (i / nu, j / nv)
    tops, backs = [], []
    for i in range(nu):
        for j in range(nv):
            tops.append(bm.faces.new((T[i][j], T[i][j + 1], T[i + 1][j + 1], T[i + 1][j])))
            backs.append(bm.faces.new((B[i][j], B[i + 1][j], B[i + 1][j + 1], B[i][j + 1])))
    ring = [(i, 0) for i in range(nu)] + [(nu, j) for j in range(nv)] + [(i, nv) for i in range(nu, 0, -1)] + [(0, j) for j in range(nv, 0, -1)]
    for k in range(len(ring)):
        (i0, j0), (i1, j1) = ring[k], ring[(k + 1) % len(ring)]
        backs.append(bm.faces.new((T[i0][j0], B[i0][j0], B[i1][j1], T[i1][j1])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if uv_top:
        for faces, fn in ((tops, uv_top), (backs, uv_back or uv_top)):
            for f in faces:
                for loop in f.loops:
                    loop[uv].uv = fn(*par[loop.vert])
    return tops, backs


def rect_uv(r):
    return lambda a, b: (r[0] + a * r[2], r[1] + b * r[3])


def corner_lift(x, z, corners):
    return sum(h * max(0.0, 1 - math.hypot(x - cx, z - cz) / fall) ** 2 for cx, cz, h, fall in corners)


def paper_props():
    """Chart and postcard meshes plus the painted "Desk paper" atlas (no Cycles bake needed)."""
    col = vc.collection("Desk paper")
    W, H = 2048, 1024
    chart_px, card_px = (1474, 1024), (532, 702)
    CHART = (0, 0, 1474 / W, 1.0)
    CARD = (1506 / W, 0, 532 / W, 702 / H)
    CHART_BACK = (1530 / W, 770 / H, 220 / W, 220 / H)
    CARD_BACK = (1800 / W, 770 / H, 220 / W, 220 / H)
    paint = paper = None
    albedo = np.empty((H, W, 3), np.float32)
    albedo[:] = (.9, .85, .72)
    normal = np.empty((H, W, 3), np.float32)
    normal[:] = (.5, .5, 1)
    mask = np.zeros((H, W, 4), np.float32)
    mask[..., 1], mask[..., 3] = 1, .1
    for P, (c0, r0) in ((paint_chart(*chart_px), (0, 0)), (paint_postcard(*card_px), (1506, 0))):
        a, n, m = P.maps()
        albedo[r0:r0 + P.H, c0:c0 + P.W] = a
        normal[r0:r0 + P.H, c0:c0 + P.W] = n
        mask[r0:r0 + P.H, c0:c0 + P.W] = m
    albedo[770:990, 1530:1750] = (.8, .7, .52)
    albedo[770:990, 1800:2020] = (.9, .86, .77)
    rgba = lambda a: np.dstack((a, np.ones(a.shape[:2], np.float32)))
    vc.save(rgba(albedo), "Desk paper albedo", 'sRGB')
    vc.save(rgba(normal), "Desk paper normal", 'Non-Color')
    vc.save(mask, "Desk paper mask", 'Non-Color', alpha=True)

    out = []
    mat = vm.plain("Desk paper", (.9, .85, .72), .8)
    # chart: folded sheet with two lifted corners, a slight ridge along the centre fold
    w, h, t = .95, .66, .0022
    base = TABLE - OBJ["Hand-drawn nautical chart"]["pos"][1]
    lifts = [(.475, -.33, .03, .2), (-.475, .33, .016, .15)]

    def chart_top(a, b):
        x, z = (a - .5) * w, (b - .5) * h
        y = base + t + corner_lift(x, z, lifts) + .003 * math.exp(-(x / .05) ** 2) + .0012 * math.sin(z * 11) ** 2
        return x, y, z

    bm = bmesh.new()
    slab(bm, 64, 44, chart_top, lambda a, b: (lambda p: (p[0], p[1] - t, p[2]))(chart_top(a, b)), rect_uv(CHART), rect_uv(CHART_BACK))
    ob = vc.to_object("Hand-drawn nautical chart", bm, [mat], pose("Hand-drawn nautical chart"), col)
    out.append(vc.finish(ob, smooth=60))
    # postcard: thin card with a gentle bow and one lifted corner
    w, h, t = .72, .95, .0045
    base = TABLE - OBJ["Blank voyage postcard"]["pos"][1]
    lifts = [(.36, .475, .012, .18)]

    def card_top(a, b):
        x, z = (a - .5) * w, (b - .5) * h
        return x, base + t + corner_lift(x, z, lifts) + .002 * (1 - (2 * a - 1) ** 2), z

    bm = bmesh.new()
    slab(bm, 30, 40, card_top, lambda a, b: (lambda p: (p[0], p[1] - t, p[2]))(card_top(a, b)), rect_uv(CARD), rect_uv(CARD_BACK))
    card = vc.to_object("Blank voyage postcard", bm, [mat], vc.unity_pose(OBJ["Blank voyage postcard"]["pos"], OBJ["Blank voyage postcard"]["rot"]), col)
    out.append(vc.finish(card, smooth=60))
    return out, base + t + .002


def compass(col):
    brass = vm.brass("Compass brass", tarnish=.3)
    steel = vm.plain("Needle steel", (.1, .11, .14), .32, 1.0)
    red = vm.plain("Needle red", (.6, .1, .06), .4)
    dial_mat = vm.Graph("Compass card")
    P = paint_dial(512, .23)
    img = bpy.data.images.new("Compass card", 512, 512, alpha=True)
    img.colorspace_settings.name = 'sRGB'
    img.pixels.foreach_set(np.dstack((np.clip(P.rgb, 0, 1), np.ones((512, 512), np.float32))).ravel())
    tex = dial_mat.node('ShaderNodeTexImage', extension='EXTEND')
    tex.image = img
    dial_mat.link(dial_mat.coords(scale=(-1 / .46, -1 / .46, 1), location=(.5, .5, 0)), tex.inputs['Vector'])
    fibre = dial_mat.noise(dial_mat.coords(), 500, 4, .6)
    dial = dial_mat.finish(tex.outputs['Color'], .62, dial_mat.bump(fibre.outputs['Fac'], .12, .001))

    lo = TABLE - OBJ["Pocket compass"]["pos"][1]   # -.05
    bm = bmesh.new()
    prof = [(0, lo), (.222, lo), (.24, lo + .003), (.249, lo + .01), (.252, lo + .02), (.252, .004), (.256, .008), (.26, .012),
            (.26, .024), (.256, .029), (.247, .033), (.236, .032), (.23, .027), (.228, .014)]
    rings = vc.lathe(bm, prof, 180)
    for ring in rings[6:11]:      # knurled bezel
        for i, v in enumerate(ring):
            r = Vector((v.co.x, v.co.y, 0))
            v.co += r.normalized() * .0022 * math.cos(i * 2 * math.pi / 3)
    case = vc.to_object("Compass case", bm, [brass], col=col)
    vc.finish(case, smooth=50)

    bm = bmesh.new()
    vc.lathe(bm, [(0, .014), (.1, .014), (.2285, .014)], 96)
    card = vc.to_object("Compass card", bm, [dial], col=col)
    vc.finish(card)

    bm = bmesh.new()   # needle: two thin lozenges, north red / south steel, plus a domed pivot
    a = math.radians(32)
    d, p = Vector((math.sin(a), 0, math.cos(a))), Vector((math.cos(a), 0, -math.sin(a)))
    for sign, idx in ((1, 0), (-1, 1)):
        y0, y1 = .019, .0235
        tip, l, r = d * .165 * sign, p * .021, -p * .021
        quad = [Vector((0, 0, 0)) + l, tip, r]
        bot = [bm.verts.new(vc.U(q.x, y0, q.z)) for q in quad]
        top = [bm.verts.new(vc.U(q.x, y1 - (.002 if q is tip else 0), q.z)) for q in quad]
        ridge = bm.verts.new(vc.U(d.x * .02 * sign, y1 + .0015, d.z * .02 * sign))
        for f in ((top[0], top[1], ridge), (top[1], top[2], ridge), (top[2], top[0], ridge), (bot[2], bot[1], bot[0]),
                  (top[0], bot[0], bot[1], top[1]), (top[1], bot[1], bot[2], top[2]), (top[2], bot[2], bot[0], top[0])):
            bm.faces.new(f).material_index = idx
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    needle = vc.to_object("Compass needle", bm, [red, steel], col=col)
    vc.finish(needle, smooth=30)

    bm = bmesh.new()
    vc.lathe(bm, [(0, .014), (.009, .014), (.011, .022), (.009, .028), (.005, .031), (0, .0315)], 20)
    # crown and bow ring toward the back-left
    ang = math.radians(-25)
    dirv = Vector((math.sin(ang), 0, math.cos(ang)))
    m = Matrix.Translation(vc.U(dirv.x * .25, -.016, dirv.z * .25)) @ (vc.C @ Matrix.Rotation(ang, 4, 'Y') @ Matrix.Rotation(math.radians(90), 4, 'X') @ vc.C.transposed())
    rings = vc.lathe(bm, [(0, -.002), (.017, -.002), (.017, .026), (.028, .028), (.031, .033), (.031, .05), (.028, .054), (0, .055)], 30, m)
    for ring in rings[4:6]:
        for i, v in enumerate(ring):
            c = m @ Vector((0, 0, (m.inverted() @ v.co).z))
            v.co += (v.co - c).normalized() * .0018 * math.cos(i * math.pi)
    centre = dirv * (.305 + .052)
    loop = [vc.U(centre.x + math.sin(ang + t) * .052 * 0 + (dirv.x * math.cos(t) + dirv.z * math.sin(t)) * .052,
                 lo + .0085, centre.z + (dirv.z * math.cos(t) - dirv.x * math.sin(t)) * .052) for t in (2 * math.pi * i / 40 for i in range(40))]
    vc.tube(bm, loop, .0085, sides=10, closed=True)
    hardware = vc.to_object("Compass crown", bm, [brass], col=col)
    vc.finish(hardware, smooth=60)

    parts = [case, card, needle, hardware]
    for o in parts:
        o.matrix_world = pose("Pocket compass")
    return parts


def scallop(col):
    mat = vm.Graph("Scallop shell")
    v = mat.coords(location=(0, .2 * SHELL, 0))
    rings = mat.node('ShaderNodeTexWave', wave_type='RINGS', rings_direction='SPHERICAL')
    rings.inputs['Scale'].default_value = 7 / SHELL
    rings.inputs['Distortion'].default_value = 1.2
    mat.link(v, rings.inputs['Vector'])
    radial = mat.node('ShaderNodeTexGradient', gradient_type='SPHERICAL')
    mat.link(mat.coords(scale=(1 / SHELL, 1 / SHELL, 1 / SHELL), location=(0, .2, 0)), radial.inputs['Vector'])
    n = mat.noise(mat.coords(), 60, 5, .55)
    tint = mat.ramp(rings.outputs['Fac'], [(0, (.95, .86, .74)), (.55, (.92, .78, .62)), (.85, (.78, .42, .3)), (1, (.86, .6, .45))])
    tint = mat.mix(tint, (.97, .93, .86), mat.math('POWER', radial.outputs['Fac'], 3.0))
    tint = mat.mix(tint, mat.ramp(n.outputs['Fac'], [(.3, (.9, .9, .9)), (.7, (1.04, 1.03, 1.0))]), 1.0, 'MULTIPLY')
    shell_mat = mat.finish(tint, .5, mat.bump(n.outputs['Fac'], .12, .001))

    bm = bmesh.new()
    th = 1.12

    def top(a, b):
        th_ = (2 * a - 1) * th
        s = .05 + .95 * b
        R = .98 * (1 - .06 * (th_ / th) ** 6)
        x, z = math.sin(th_) * R * s, -.2 + math.cos(th_) * R * s
        dome = .2 * (4 * s * (1 - s) + .0001) ** .55 * (1 - .35 * (th_ / th) ** 2)
        rib = .022 * s ** .7 * (.5 + .5 * math.cos(th_ * 18 / (2 * th) * 2 * math.pi))
        growth = .0025 * math.sin(s * 70) * s
        return x * SHELL, (dome + rib + growth + .004) * SHELL, z * SHELL

    def bottom(a, b):
        x, y, z = top(a, b)
        return x, max(0.0, y - .03 * SHELL), z

    slab(bm, 90, 30, top, bottom)
    ears = [(-.3, -.245), (.3, -.245), (.15, -.04), (-.15, -.04)]
    lo_ = [bm.verts.new(vc.U(x * SHELL, 0, z * SHELL)) for x, z in ears]
    hi_ = [bm.verts.new(vc.U(x * SHELL, .03 * SHELL, z * SHELL)) for x, z in ears]
    bm.faces.new(lo_)
    bm.faces.new(list(reversed(hi_)))
    for i in range(4):
        bm.faces.new((lo_[i], lo_[(i + 1) % 4], hi_[(i + 1) % 4], hi_[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    ob = vc.to_object("Ribbed scallop", bm, [shell_mat], pose("Scallop keepsake", TABLE), col)
    return vc.finish(ob, smooth=55)


def pebbles(col):
    mats = [vm.stone("Grey pebble", (.42, .41, .4), (.62, .6, .57), 5),
            vm.stone("Sand pebble", (.64, .56, .45), (.8, .74, .62), 6),
            vm.stone("Slate pebble", (.22, .23, .25), (.38, .38, .4), 4)]
    out = []
    for k, name in enumerate(PEBBLES):
        sx, sy, sz = OBJ[name]["scale"]
        bm = bmesh.new()
        bmesh.ops.create_uvsphere(bm, u_segments=40, v_segments=20, radius=1.0)
        seed = Vector((k * 3.7, k * 1.3, k * 5.1))
        for v in bm.verts:
            c = v.co.copy()
            c *= 1 + .1 * noise.noise(c * 1.1 + seed) + .03 * noise.noise(c * 3.5 + seed)
            if c.z < -.55:
                c.z = -.55 + (c.z + .55) * .35
            v.co = Vector((c.x * sx / 2, c.y * sz / 2, c.z * sy / 2))
        ob = vc.to_object(name, bm, [mats[k % 3]], pose(name), col, smooth_angle=80)
        bpy.context.view_layer.update()
        low = min((ob.matrix_world @ v.co).z for v in ob.data.vertices)
        shift = ob.matrix_world.to_3x3().inverted() @ Vector((0, 0, TABLE - low))
        ob.data.transform(Matrix.Translation(shift))
        out.append(vc.finish(ob, smooth=80))
    return out


def pencil(col, card_top):
    paint = vm.Graph("Pencil lacquer")
    n = paint.noise(paint.coords(), 300, 3, .5)
    lacquer = paint.finish((.08, .25, .17), .28, paint.bump(n.outputs['Fac'], .03, .001))
    wood = vm.wood("Pencil cedar", (.85, .66, .45), (.66, .45, .28), grain='X', rings=30)
    lead = vm.plain("Graphite", (.06, .06, .065), .35, .6)
    ferrule = vm.brass("Pencil ferrule", .2)
    eraser = vm.plain("Eraser", (.78, .45, .42), .85)
    r = .0125
    centre = card_top + r * .866
    m = Matrix.Translation(vc.U(-.23, centre, .19)) @ (vc.C @ Matrix.Rotation(math.radians(8), 4, 'Y') @ Matrix.Rotation(math.radians(-90), 4, 'Z') @ vc.C.transposed())
    # lathe axis (Blender Z) -> Unity +X after C; hexagonal body, cone tip, ferrule and eraser
    bm = bmesh.new()
    body = vc.lathe(bm, [(0, -.004), (r * .5, -.004), (r, 0), (r, .4), (r * .55, .432), (.0018, .452), (0, .456)], 6, m, math.radians(30))
    for f in bm.faces:
        h = (m.inverted() @ f.calc_center_median()).z
        f.material_index = 0 if 0 < h < .4 else 1 if h < .446 else 2
        if h <= 0:
            f.material_index = 0
    for f in bm.faces:   # the sharpened cone shows lacquer scallops at its base
        if f.material_index == 1:
            c = m.inverted() @ f.calc_center_median()
            if c.z < .405:
                f.material_index = 0
    ob = vc.to_object("Pencil", bm, [lacquer, wood, lead, ferrule, eraser], col=col)
    bm = bmesh.new()
    vc.lathe(bm, [(0, -.05), (r * .86, -.05), (r * .95, -.046), (r * .95, -.03), (r * 1.02, -.028), (r * 1.02, -.022), (r * 1.04, -.02),
                  (r * 1.04, -.012), (r * 1.02, -.01), (r * 1.02, .0), (r * .9, .001)], 24, m)
    for f in bm.faces:
        f.material_index = 4 if (m.inverted() @ f.calc_center_median()).z < -.03 else 3
    end = vc.to_object("Pencil end", bm, [lacquer, wood, lead, ferrule, eraser], col=col)
    vc.select([ob, end], ob)
    bpy.ops.object.join()
    ob.name = ob.data.name = "Pencil"
    low = min(v.co.z for v in ob.data.vertices)    # rest a flat facet on the card
    ob.data.transform(Matrix.Translation((0, 0, card_top - low)))
    vc.finish(ob, smooth=35)
    ob.matrix_world = vc.unity_pose(OBJ["Blank voyage postcard"]["pos"], OBJ["Blank voyage postcard"]["rot"])
    return ob


def ropes(col):
    mat = bpy.data.materials.get("Cotton rope") or vm.plain("Cotton rope", (.8, .75, .64))
    out = []
    rad = .012
    y = TABLE + rad * .92
    pts = []
    for i in range(190):
        t = i / 189
        a, r = t * math.pi * 2 * 3.2, .09 + t * .17
        pts.append(Vector((-1.95 + math.cos(a) * r, y, .05 + math.sin(a) * r)))
    tail = [Vector((-1.88, y, -.35)), Vector((-1.62, y, -.49)), Vector((-1.5, y, -.6)), Vector((-1.47, y, -.66))]
    bridge = vc.catmull([pts[-3], pts[-1], tail[0], tail[1], tail[2], tail[3]], 16)
    path = pts[:-1] + bridge[16:]
    # the tail crosses over the coil before running off across the desk
    for i in range(189, len(path)):
        dist = math.hypot(path[i].x + 1.95, path[i].z - .05)
        s = min(1.0, max(0.0, (.3 - dist) / .06))
        path[i] = path[i] + Vector((0, 1.9 * rad * s * s * (3 - 2 * s), 0))
    bm = bmesh.new()
    vc.tube(bm, [vc.U(*p) for p in path], rad, sides=10, radius_fn=lambda t: 1 - .2 * max(0, (t - .985) / .015))
    ob = vc.to_object("Coiled mooring rope", bm, [mat], col=col)
    out.append(vc.finish(ob, smooth=70))

    rad = .013
    pts = []
    turns, r0, r1 = 5.5, .07, .215
    for i in range(int(turns * 48) + 1):
        t = i / (turns * 48)
        a = t * turns * 2 * math.pi
        r = r0 + (r1 - r0) * t
        pts.append(Vector((math.cos(a) * r, TABLE + rad * .92, math.sin(a) * r)))
    a_end = turns * 2 * math.pi
    end_dir = Vector((-math.sin(a_end), 0, math.cos(a_end)))
    for k in range(1, 9):
        pts.append(pts[-1] + end_dir * .022 + Vector((0, 0, .002 * k)))
    bm = bmesh.new()
    vc.tube(bm, [vc.U(*p) for p in pts], rad, sides=10)
    coil = vc.to_object("Cotton coil", bm, [mat], vc.unity_pose(OBJ["Cotton coil"]["pos"], (0, 0, 0)), col)
    out.append(vc.finish(coil, smooth=70))
    return out


def build():
    col = vc.collection("Desk props")
    papers, card_top = paper_props()
    props = compass(col) + [scallop(col)] + pebbles(col) + [pencil(col, card_top)]
    vc.unwrap(props, angle=55, margin=.003)
    table = bpy.data.objects.get("Table proxy")
    if table is None:
        import vfittings
        table = vfittings.table_proxy()
    table.hide_render = False
    vc.bake_set("Desk props", props, 2048, ao_distance=.08, ao_into_albedo=.35)
    table.hide_render = True
    vc.single_material(props, "Desk props")
    compass_parts = props[:4]
    vc.select(compass_parts, compass_parts[0])
    bpy.ops.object.join()
    compass_parts[0].name = compass_parts[0].data.name = "Pocket compass"
    props = [compass_parts[0]] + props[4:]
    return papers + props + ropes(col)
