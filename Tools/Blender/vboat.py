"""Little sailboat: lofted planked hull, teak deck, cabin, spars, cross-cut sails and rope rigging.
Built in boat-local Unity space (the scene root keeps its transform, scale .39)."""
import bpy, bmesh, math
from mathutils import Vector, Matrix
import vcore as vc
import vmaterials as vm

STERN, BOW = -.78, .85
WATERLINE = -.06


def half_beam(z):
    if z >= -.1:
        t = min(1.0, (z + .1) / (BOW + .1))
        return .36 * max(0.0, 1 - t ** 2.2) ** .75
    t = (-.1 - z) / (-.1 - STERN)
    return .36 - .13 * t * t


def sheer(z):
    return .145 + .045 * max(0.0, (z - .1) / .75) ** 2 + .02 * max(0.0, (-.3 - z) / .48) ** 2


def keel(z):
    k = -.22
    if z > .35:
        k += (sheer(z) + .22) * ((z - .35) / (BOW - .35)) ** 1.8
    if z < -.4:
        k += .1 * ((-.4 - z) / (-.4 - STERN)) ** 1.5
    return k


def adapt(mat):
    g = vm.Graph.__new__(vm.Graph)
    g.mat, g.nt = mat, mat.node_tree
    g.bsdf = next(n for n in g.nt.nodes if n.type == 'BSDF_PRINCIPLED')
    return g


def xyz(g):
    sep = g.node('ShaderNodeSeparateXYZ')
    g.link(g.coords(), sep.inputs[0])
    return sep.outputs


def hull_material():
    mat = vm.wood("Mahogany hull", (.46, .2, .09), (.2, .075, .03), grain='Y', rings=6)
    g = adapt(mat)
    z = xyz(g)[2]   # Blender Z = Unity Y
    wood = g.bsdf.inputs['Base Color'].links[0].from_socket
    rough = g.bsdf.inputs['Roughness'].links[0].from_socket
    normal = g.bsdf.inputs['Normal'].links[0].from_socket
    below = g.math('LESS_THAN', z, WATERLINE)
    boot = g.math('MULTIPLY', g.math('LESS_THAN', z, WATERLINE + .028), g.math('SUBTRACT', 1.0, below))
    above = g.math('SUBTRACT', 1.0, g.math('ADD', below, boot))
    lap = g.math('FRACT', g.math('DIVIDE', g.math('SUBTRACT', z, WATERLINE + .028), .047))
    seam = g.math('MULTIPLY', g.math('LESS_THAN', lap, .07), above)
    color = g.mix(wood, (.12, .05, .02), g.math('MULTIPLY', seam, .6))
    color = g.mix(color, (.86, .8, .66), boot)
    fouling = g.noise(g.coords(), 18, 4, .55)
    red = g.ramp(fouling.outputs['Fac'], [(.3, (.24, .07, .05)), (.7, (.36, .11, .07))])
    color = g.mix(color, red, below)
    paint = g.math('SUBTRACT', 1.0, above)
    g.link(g.math('MULTIPLY_ADD', paint, g.math('SUBTRACT', .5, rough), rough), g.bsdf.inputs['Roughness'])
    g.link(color, g.bsdf.inputs['Base Color'])
    g.link(g.bump(g.math('MULTIPLY', lap, above), .5, .004, normal=normal), g.bsdf.inputs['Normal'])
    return mat


def teak_material(name="Teak deck", plank=.055):
    g = vm.Graph(name)
    x, y, _ = xyz(g)
    idx = g.math('FLOOR', g.math('DIVIDE', x, plank))
    seam_x = g.math('LESS_THAN', g.math('FRACT', g.math('DIVIDE', x, plank)), .08)
    seam_y = g.math('LESS_THAN', g.math('FRACT', g.math('DIVIDE', g.math('MULTIPLY_ADD', idx, .37, y), .46)), .012)
    seam = g.math('MAXIMUM', seam_x, seam_y)
    wn = g.node('ShaderNodeTexWhiteNoise', noise_dimensions='1D')
    g.link(idx, wn.inputs['W'])
    streak_v = g.node('ShaderNodeMapping')
    streak_v.inputs['Scale'].default_value = (60, 4, 60)
    g.link(g.coords(), streak_v.inputs['Vector'])
    streak = g.noise(streak_v.outputs[0], 1.0, 6, .6)
    base = g.ramp(streak.outputs['Fac'], [(.3, (.4, .25, .12)), (.7, (.62, .43, .24))])
    tint = g.ramp(wn.outputs['Value'], [(0, (.86, .84, .82)), (1, (1.08, 1.04, 1.0))])
    color = g.mix(g.mix(base, tint, 1.0, 'MULTIPLY'), (.05, .04, .03), seam)
    rough = g.math('MULTIPLY_ADD', seam, .3, .55)
    height = g.math('MULTIPLY_ADD', streak.outputs['Fac'], .2, g.math('MULTIPLY', seam, -1.0))
    return g.finish(color, rough, g.bump(height, .45, .002))


def paint_material(name, color, rough=.42):
    g = vm.Graph(name)
    v = g.coords()
    n = g.noise(v, 40, 5, .55)
    tint = g.ramp(n.outputs['Fac'], [(.3, tuple(c * .93 for c in color)), (.7, color)])
    return g.finish(tint, rough, g.bump(n.outputs['Fac'], .08, .001))


def sail_material(name, head, tack, clew, color=(.9, .86, .76)):
    g = vm.Graph(name)
    v = g.coords()
    leech = (vc.U(*clew) - vc.U(*head)).normalized()
    dot = g.node('ShaderNodeVectorMath', operation='DOT_PRODUCT')
    g.link(v, dot.inputs[0])
    dot.inputs[1].default_value = leech
    panel = g.math('FRACT', g.math('DIVIDE', dot.outputs['Value'], .105))
    seam = g.math('LESS_THAN', panel, .045)
    dmin = None
    for corner in (head, tack, clew):
        d = g.node('ShaderNodeVectorMath', operation='DISTANCE')
        g.link(v, d.inputs[0])
        d.inputs[1].default_value = vc.U(*corner)
        dmin = d.outputs['Value'] if dmin is None else g.math('MINIMUM', dmin, d.outputs['Value'])
    patch = g.math('LESS_THAN', dmin, .11)
    stitch = g.math('LESS_THAN', g.math('ABSOLUTE', g.math('SUBTRACT', dmin, .11)), .005)
    blot = g.noise(v, 7, 4, .55)
    tint = g.ramp(blot.outputs['Fac'], [(.3, tuple(c * .92 for c in color)), (.7, color)])
    tint = g.mix(tint, tuple(c * .9 for c in color), g.math('MULTIPLY', patch, .5))
    tint = g.mix(tint, tuple(c * .72 for c in color), g.math('MAXIMUM', seam, stitch))
    weave = g.noise(v, 900, 2, .5)
    height = g.math('ADD', g.math('MULTIPLY', g.math('MAXIMUM', seam, stitch), .6), g.math('MULTIPLY', weave.outputs['Fac'], .15))
    g.bsdf.inputs['Sheen Weight'].default_value = .4
    return g.finish(tint, .8, g.bump(height, .3, .0015))


def plate(bm, outline_zy, half_thickness, mat_index=0):
    """Extrudes a (z, y) outline across Unity X."""
    front = [bm.verts.new(vc.U(-half_thickness, y, z)) for z, y in outline_zy]
    back = [bm.verts.new(vc.U(half_thickness, y, z)) for z, y in outline_zy]
    faces = [bm.faces.new(front), bm.faces.new(list(reversed(back)))]
    n = len(outline_zy)
    for i in range(n):
        faces.append(bm.faces.new((front[i], front[(i + 1) % n], back[(i + 1) % n], back[i])))
    for f in faces:
        f.material_index = mat_index
    return faces


def sail_mesh(bm, head, tack, clew, bulge, roach, div=18):
    head, tack, clew = Vector(head), Vector(tack), Vector(clew)
    leech = (clew - head).normalized()
    away = (head - tack) - leech * (head - tack).dot(leech)
    away = -away.normalized()
    idx = {}
    for i in range(div + 1):
        for j in range(div + 1 - i):
            u, v = i / div, j / div
            w = 1 - u - v
            p = head * u + tack * v + clew * w
            p.x += bulge * 27 * u * v * w
            p += away * roach * 4 * u * w * (1 - v) ** 3
            idx[i, j] = bm.verts.new(vc.U(*p))
    for i in range(div):
        for j in range(div - i):
            bm.faces.new((idx[i, j], idx[i + 1, j], idx[i, j + 1]))
            if i + j < div - 1:
                bm.faces.new((idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1]))


def solid(ob, thickness):
    mod = ob.modifiers.new("Thick", 'SOLIDIFY')
    mod.thickness = thickness
    mod.offset = 0
    vc.apply_modifiers(ob)


def build():
    col = vc.collection("Sailboat")
    hull_mat, deck_mat = hull_material(), teak_material()
    rail_mat = vm.wood("Mahogany rail", (.4, .17, .07), (.16, .06, .025), grain='Y', rings=8)
    spar_mat = vm.wood("Spruce mast", (.72, .5, .27), (.45, .27, .12), grain='Z', rings=10)
    boom_mat = vm.wood("Spruce boom", (.72, .5, .27), (.45, .27, .12), grain='Y', rings=10)
    cabin_mat = paint_material("Cabin paint", (.86, .82, .7))
    brass_mat = vm.brass("Boat brass")
    glass_mat = vm.plain("Porthole glass", (.03, .05, .06), rough=.08)
    main_pts = ((0, 1.48, -.045), (0, .40, -.045), (0, .40, -.66))
    jib_pts = ((0, 1.40, .045), (0, .31, .65), (0, .40, .10))
    main_mat = sail_material("Mainsail cloth", *main_pts)
    jib_mat = sail_material("Jib cloth", *jib_pts)
    flag_mat = vm.linen("Ochre pennant", (.76, .34, .12), weave=600)
    parts = []

    # ---- hull + deck + transom (one closed shell)
    bm = bmesh.new()
    N, M, D = 44, 10, 10
    zs = [STERN + (BOW - STERN) * (i / N) for i in range(N + 1)]
    hull_rings, deck_rings = [], []
    for z in zs:
        b, s, k = half_beam(z), sheer(z), keel(z)
        side = []
        for m in range(M + 1):
            u = m / M
            y = s + (k - s) * u ** .9
            x = b * max(0.0, 1 - u ** 2.6) ** (1 / 1.6)
            side.append((x, y))
        ring = [vc.U(-x, y, z) for x, y in side] + [vc.U(x, y, z) for x, y in reversed(side[:-1])]
        hull_rings.append([bm.verts.new(p) for p in ring])
        deck = []
        for d in range(1, D):
            t = -1 + 2 * d / D
            deck.append(bm.verts.new(vc.U(b * t, s + .014 * (1 - t * t), z)))
        deck_rings.append([hull_rings[-1][0]] + deck + [hull_rings[-1][-1]])
    for a, b_ in zip(hull_rings, hull_rings[1:]):
        for j in range(len(a) - 1):
            bm.faces.new((a[j], a[j + 1], b_[j + 1], b_[j])).material_index = 0
    for a, b_ in zip(deck_rings, deck_rings[1:]):
        for j in range(len(a) - 1):
            bm.faces.new((a[j], b_[j], b_[j + 1], a[j + 1])).material_index = 1
    transom = hull_rings[0] + list(reversed(deck_rings[0][1:-1]))
    bm.faces.new(transom).material_index = 0
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    hull = vc.to_object("Sailboat", bm, [hull_mat, deck_mat], col=col)
    vc.finish(hull, smooth=38)
    parts.append(hull)

    # ---- keel fin + rudder (hull paint below the waterline)
    bm = bmesh.new()
    plate(bm, [(.30, -.19), (-.22, -.19), (-.17, -.31), (.13, -.31)], .016)
    plate(bm, [(-.745, .02), (-.79, .02), (-.875, -.1), (-.86, -.21), (-.79, -.23), (-.75, -.12)], .011)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    fins = vc.to_object("Keel and rudder", bm, [hull_mat], col=col)
    vc.finish(fins, bevel=.008, segments=2)
    parts.append(fins)

    # ---- cap rail along the sheer and across the transom
    bm = bmesh.new()
    port = [vc.U(-half_beam(z) - .003, sheer(z) + .006, z) for z in zs[:-1]]
    star = [vc.U(half_beam(z) + .003, sheer(z) + .006, z) for z in reversed(zs[:-1])]
    loop = port + [vc.U(0, sheer(BOW) + .006, BOW - .004)] + star
    vc.tube(bm, loop, .013, sides=8, closed=True)
    rail = vc.to_object("Cap rail", bm, [rail_mat], col=col)
    vc.finish(rail, smooth=60)
    parts.append(rail)

    # ---- cabin trunk with cambered roof, portholes and a companionway door
    bm = bmesh.new()
    cz, hx, hz, r = -.29, .125, .128, .03

    def rounded(hx_, hz_, r_):
        pts = []
        for cx, czz, a0 in ((hx_ - r_, hz_ - r_, 0), (-hx_ + r_, hz_ - r_, 90), (-hx_ + r_, -hz_ + r_, 180), (hx_ - r_, -hz_ + r_, 270)):
            for s_ in range(7):
                a = math.radians(a0 + 90 * s_ / 6)
                pts.append((cx + math.cos(a) * r_, czz + math.sin(a) * r_))
        return pts

    wall = rounded(hx, hz, r)
    lo = [bm.verts.new(vc.U(x, .13, cz + z)) for x, z in wall]
    hi = [bm.verts.new(vc.U(x, .245, cz + z)) for x, z in wall]
    for i in range(len(wall)):
        bm.faces.new((lo[i], lo[(i + 1) % len(wall)], hi[(i + 1) % len(wall)], hi[i])).material_index = 0
    roof = rounded(hx + .014, hz + .014, r + .014)
    under = [bm.verts.new(vc.U(x, .245, cz + z)) for x, z in roof]
    top = [bm.verts.new(vc.U(x, .258 + .022 * (1 - (x / (hx + .014)) ** 2), cz + z)) for x, z in roof]
    centre = bm.verts.new(vc.U(0, .28, cz))
    n = len(roof)
    for i in range(n):
        bm.faces.new((under[i], under[(i + 1) % n], top[(i + 1) % n], top[i])).material_index = 1
        bm.faces.new((top[i], top[(i + 1) % n], centre)).material_index = 1
    bm.faces.new(list(reversed(under))).material_index = 1
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    cabin = vc.to_object("Cabin", bm, [cabin_mat, deck_mat], col=col)
    vc.finish(cabin, bevel=.004, segments=2, smooth=45)
    parts.append(cabin)

    bm = bmesh.new()
    res = bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(.1, .09, .026), verts=res['verts'])
    bmesh.ops.translate(bm, vec=vc.U(0, .292, cz - .03), verts=res['verts'])
    res = bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(.085, .012, .085), verts=res['verts'])
    bmesh.ops.translate(bm, vec=vc.U(0, .19, cz + hz + .004), verts=res['verts'])
    hatch = vc.to_object("Hatch", bm, [deck_mat], col=col)
    vc.finish(hatch, bevel=.005, segments=2)
    parts.append(hatch)

    bm = bmesh.new()
    glass = bmesh.new()
    for sx in (-1, 1):
        for pz in (-.335, -.245):
            c = Vector((sx * (hx + .003), .195, pz))
            ring = [vc.U(c.x, c.y + math.cos(a) * .019, c.z + math.sin(a) * .019) for a in (2 * math.pi * i / 20 for i in range(20))]
            vc.tube(bm, ring, .0045, sides=6, closed=True)
            disc = [glass.verts.new(vc.U(c.x + sx * .0005, c.y + math.cos(a) * .016, c.z + math.sin(a) * .016))
                    for a in (2 * math.pi * i / 16 for i in range(16))]
            glass.faces.new(disc if sx < 0 else list(reversed(disc)))
    knob = bmesh.ops.create_uvsphere(bm, u_segments=10, v_segments=6, radius=.009)
    bmesh.ops.translate(bm, vec=vc.U(.028, .19, cz + hz + .014), verts=knob['verts'])
    step = [vc.U(math.cos(a) * .034, .152, math.sin(a) * .034) for a in (2 * math.pi * i / 20 for i in range(20))]
    vc.tube(bm, step, .006, sides=6, closed=True)
    brass = vc.to_object("Boat brass", bm, [brass_mat], col=col)
    vc.finish(brass, smooth=70)
    glass.normal_update()
    for f in glass.faces:   # glass faces outward (Blender X is mirrored Unity X)
        if f.normal.x * f.calc_center_median().x < 0:
            f.normal_flip()
    ports = vc.to_object("Portholes", glass, [glass_mat], col=col)
    vc.finish(ports)
    parts += [brass, ports]

    # ---- spars
    bm = bmesh.new()
    vc.lathe(bm, [(0, .13), (.026, .13), (.026, .17), (.024, .7), (.018, 1.5), (.016, 1.585), (.022, 1.59), (.02, 1.61), (0, 1.615)], 14)
    vc.tube(bm, [vc.U(-.165, 1.05, 0), vc.U(.165, 1.05, 0)], .007, sides=6, radius_fn=lambda t: 1 - .35 * abs(2 * t - 1))
    mast = vc.to_object("Mast", bm, [spar_mat], col=col)
    vc.finish(mast, smooth=50)
    bm = bmesh.new()
    vc.lathe(bm, [(0, -.01), (.016, -.01), (.02, .03), (.02, .6), (.016, .68), (0, .685)], 12,
             Matrix.Translation(vc.U(0, .35, 0)) @ Matrix.Rotation(math.radians(-90), 4, 'X'))
    tiller = [vc.U(0, .175 + .06 * t ** 1.4, -.80 + .32 * t) for t in (i / 10 for i in range(11))]
    vc.tube(bm, tiller, .011, sides=8, radius_fn=lambda t: 1 - .3 * t)
    boom = vc.to_object("Boom and tiller", bm, [boom_mat], col=col)
    vc.finish(boom, smooth=50)
    parts += [mast, boom]

    # ---- sails and pennant
    for name, pts, bulge, roach, mat in (("Mainsail", main_pts, .10, .035, main_mat), ("Jib", jib_pts, .065, .0, jib_mat)):
        bm = bmesh.new()
        sail_mesh(bm, *pts, bulge, roach)
        ob = vc.to_object(name, bm, [mat], col=col)
        solid(ob, .006)
        vc.finish(ob, smooth=60)
        parts.append(ob)
    bm = bmesh.new()
    grid = {}
    for i in range(9):
        for j in range(3):
            t, s = i / 8, j / 2
            z = -.3 * t
            top, bottom = 1.61 - .045 * t, 1.52 + .036 * t
            y = top + (bottom - top) * s
            x = .02 * math.sin(t * 7.5) * t
            grid[i, j] = bm.verts.new(vc.U(x, y, z))
    for i in range(8):
        for j in range(2):
            bm.faces.new((grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]))
    flag = vc.to_object("Pennant", bm, [flag_mat], col=col)
    solid(flag, .004)
    vc.finish(flag, smooth=60)
    parts.append(flag)

    # ---- bake one atlas, then join into a single mesh
    vc.unwrap(parts, angle=55, margin=.003)
    vc.bake_set("Sailboat", parts, 1024, ao_distance=.12, ao_into_albedo=.35)
    vc.single_material(parts, "Sailboat")
    vc.select(parts, hull)
    bpy.ops.object.join()
    hull.name = hull.data.name = "Sailboat"

    # ---- standing and running rigging (tiled rope texture)
    rope_mat = bpy.data.materials.get("Cotton rope") or vm.plain("Cotton rope", (.8, .75, .64))
    bm = bmesh.new()
    rad = .0065
    lines = [
        [(0, 1.53, .01), (0, .195, .83)],
        [(0, 1.56, -.012), (0, .17, -.765)],
        [(-.02, 1.32, 0), (-.165, 1.05, 0), (-.31, .165, -.08)],
        [(.02, 1.32, 0), (.165, 1.05, 0), (.31, .165, -.08)],
        [(0, .34, -.64), (0, .17, -.72)],
        [(.02, .40, .10), (.16, .3, .02), (.26, .17, -.03)],
    ]
    for pts in lines:
        dense = []
        for a, b_ in zip(pts, pts[1:]):
            a, b_ = Vector(a), Vector(b_)
            for s_ in range(12):
                dense.append(vc.U(*a.lerp(b_, s_ / 12)))
        dense.append(vc.U(*pts[-1]))
        vc.tube(bm, dense, rad, sides=6)
    rigging = vc.to_object("Sailboat rigging", bm, [rope_mat], col=col)
    vc.finish(rigging, smooth=70)
    return [hull, rigging]
