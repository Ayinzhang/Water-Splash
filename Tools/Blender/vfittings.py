"""Rope tile texture and bottle fittings (cork, neck cord, saddle cradles)."""
import bmesh, math
from mathutils import Vector, Matrix
import vcore as vc
import vmaterials as vm

ROPE_TILE = 2.333  # tile length in rope radii (one third of a 3-strand twist)


def rope_tile():
    """Bakes a seamless 3-strand rope tile (u along rope, v around) from high-poly strands onto a cylinder."""
    col = vc.collection("Rope tile")
    strand_mat = vm.cotton("Rope strand")
    s, a = .464, .536   # strand radius and offset for a unit-radius rope
    pitch = ROPE_TILE * 3
    high = []
    for k in range(3):
        bm = bmesh.new()
        uv = bm.loops.layers.uv.verify()
        steps, sides = 360, 20
        rings = []
        for i in range(steps + 1):
            x = -ROPE_TILE + 3 * ROPE_TILE * i / steps
            phase = 2 * math.pi * (x / pitch + k / 3)
            c = Vector((x, a * math.cos(phase), a * math.sin(phase)))
            radial = Vector((0, math.cos(phase), math.sin(phase)))
            t = Vector((1, -a * math.sin(phase) * 2 * math.pi / pitch, a * math.cos(phase) * 2 * math.pi / pitch)).normalized()
            n = (radial - t * radial.dot(t)).normalized()
            b = t.cross(n)
            # strands are slightly flattened where they press together
            rings.append([bm.verts.new(c + (n * math.cos(2 * math.pi * j / sides) * 1.06 + b * math.sin(2 * math.pi * j / sides) * .94) * s)
                          for j in range(sides)])
        for i in range(steps):
            for j in range(sides):
                f = bm.faces.new((rings[i][j], rings[i][(j + 1) % sides], rings[i + 1][(j + 1) % sides], rings[i + 1][j]))
                for loop, (u, v) in zip(f.loops, ((i, j), (i, j + 1), (i + 1, j + 1), (i + 1, j))):
                    loop[uv].uv = (u / steps * 3 * ROPE_TILE, v / sides)
                f.smooth = True
        high.append(vc.to_object(f"Strand {k}", bm, [strand_mat], col=col))
    bm = bmesh.new()
    pts = [Vector((ROPE_TILE * i / 24, 0, 0)) for i in range(25)]
    vc.tube(bm, pts, .9, sides=32, tile=ROPE_TILE, caps=False)  # exactly one tile along u
    low = vc.to_object("Rope low", bm, [vm.plain("Rope low", (.8, .8, .8))], col=col)
    vc.bake_set("Cotton rope", high, (256, 512), ao_distance=.6, ao_into_albedo=.45, active=low,
                selected_to_active=True, extrusion=.2, ray=1.3)
    for o in high + [low]:
        o.hide_render = True
        o.hide_set(True)


def bottle_parts():
    """Builds cork, cradles (one baked atlas) and the neck cord (tiled rope)."""
    col = vc.collection("Bottle fittings")
    walnut = vm.wood("Walnut cradle", (.36, .2, .11), (.13, .06, .03), grain='X', rings=6)
    cork_mat = vm.cork("Cork")

    # Cork: Unity-local lathe along Y, narrow end (+Y) points into the neck.
    bm = bmesh.new()
    prof = [(0, -.12), (.2, -.12), (.236, -.118), (.252, -.112), (.26, -.1), (.262, -.09)]
    prof += [(.262 - .03 * t, -.09 + .21 * t) for t in (i / 12 for i in range(1, 13))]
    prof += [(.226, .12), (.19, .124), (0, .124)]
    vc.lathe(bm, prof, 64)
    for v in bm.verts:   # slightly irregular natural cork
        r = Vector((v.co.x, v.co.y, 0))
        if r.length > .1:
            n = math.sin(v.co.z * 60 + math.atan2(v.co.y, v.co.x) * 3) * .0015 + math.sin(math.atan2(v.co.y, v.co.x) * 7) * .002
            v.co += r.normalized() * n
    cork = vc.to_object("Cork stopper", bm, [cork_mat], vc.placed("Cork stopper"), col)
    vc.finish(cork, smooth=50)

    cradles = []
    floor = -.11 - .91
    for x, radius in ((-1.12, .74), (.55, .7242)):
        r = radius + .04   # glass outer surface is about radius + .035
        a0 = math.radians(-38)
        tip = Vector((math.cos(a0), math.sin(a0))) * r
        right = [Vector((.6, floor)), Vector((.6, floor + .03)), Vector((.585, floor + .045)), Vector((.5, floor + .05))]
        # concave cove up to the horn
        p0, p2 = Vector((.5, floor + .05)), Vector((tip.x + .07, tip.y - .05))
        p1 = Vector((.44, (p0.y + p2.y) * .5 - .04))
        right += [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in (i / 10 for i in range(1, 11))]
        right += [Vector((tip.x + .062, tip.y - .01)), Vector((tip.x + .04, tip.y + .012)), Vector((tip.x + .012, tip.y + .012))]
        outline = [Vector((-.6, floor))] + right
        outline += [Vector((math.cos(a), math.sin(a))) * r for a in (a0 + (-math.pi - 2 * a0) * i / 40 for i in range(41))]
        outline += [Vector((-p.x, p.y)) for p in reversed(right[1:])]
        bm = bmesh.new()
        front = [bm.verts.new(vc.U(p.x, p.y, -.1)) for p in outline]
        back = [bm.verts.new(vc.U(p.x, p.y, .1)) for p in outline]
        bm.faces.new(front)
        bm.faces.new(list(reversed(back)))
        n = len(outline)
        for i in range(n):
            bm.faces.new((front[i], front[(i + 1) % n], back[(i + 1) % n], back[i]))
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        name = f"Walnut saddle cradle {x:0.2f}"
        cradle = vc.to_object(name, bm, [walnut], vc.placed(name), col)
        vc.finish(cradle, bevel=.009, segments=3)
        cradles.append(cradle)

    parts = [cork] + cradles
    vc.unwrap(parts)
    table = table_proxy()
    vc.bake_set("Bottle fittings", parts, 1024, ao_distance=.25)
    table.hide_render = True
    vc.single_material(parts, "Bottle fittings")

    # Neck cord: two wraps and a small knot at the tag rope anchor (bottle-local 1.51, -.04, -.28).
    rope_mat = vm.plain("Cotton rope", (.8, .75, .64))
    bm = bmesh.new()
    rr, rad = .291, .0155   # neck glass outer radius is .275
    for dx in (-.017, .017):
        pts = [vc.U(1.51 + dx, math.cos(a) * rr, math.sin(a) * rr) for a in (2 * math.pi * i / 72 for i in range(72))]
        vc.tube(bm, pts, rad, sides=8, closed=True)
    out = Vector((0, -.04, -.28)).normalized()
    anchor = Vector((1.51, 0, 0)) + out * rr
    side = Vector((1, 0, 0))
    around = out.cross(side)
    knot = [anchor + out * (.012 + .012 * math.cos(t)) + side * .026 * math.sin(t) + around * .02 * math.sin(2 * t) for t in
            (2 * math.pi * i / 28 for i in range(28))]
    vc.tube(bm, [vc.U(*p) for p in knot], rad * .95, sides=8, closed=True)
    cord = vc.to_object("Cotton neck cord", bm, [rope_mat], vc.placed("Cotton neck cord"), col)
    return parts, [cord]


def table_proxy():
    import bpy
    bm = bmesh.new()
    bmesh.ops.create_circle(bm, cap_ends=True, radius=3.35, segments=64)
    ob = vc.to_object("Table proxy", bm, [vm.plain("Table proxy", (.3, .18, .1))], Matrix.Translation((0, 0, -.11)))
    return ob
