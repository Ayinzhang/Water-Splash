"""Shared helpers for the Bottle Voyage Blender build: axis conversion, meshes, baking and FBX export."""
import bpy, bmesh, json, math, os
import numpy as np
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(REPO, "Assets", "WaterSplash", "Art", "BottleVoyage", "Blender")
TEX = os.path.join(OUT, "Textures")
SCENE = json.load(open(os.path.join(HERE, "voyage_scene.json")))

# Unity (x, y, z) -> Blender (-x, -z, y); verified with an FBX probe (axis_forward -Z, axis_up Y, bake_space_transform).
C = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def U(x, y, z):
    return Vector((-x, -z, y))


def placed(name):
    m = SCENE["objects"][name]["m"]
    return C @ Matrix([m[0:4], m[4:8], m[8:12], m[12:16]]) @ C.transposed()


def unity_pose(position, euler_deg):
    """Blender matrix for a Unity world position + Unity euler (ZXY order), unit scale."""
    rx, ry, rz = (math.radians(a) for a in euler_deg)
    r = Matrix.Rotation(ry, 4, 'Y') @ Matrix.Rotation(rx, 4, 'X') @ Matrix.Rotation(rz, 4, 'Z')
    return C @ (Matrix.Translation(Vector(position)) @ r) @ C.transposed()


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    prefs = bpy.context.preferences.addons['cycles'].preferences
    prefs.compute_device_type = 'OPTIX'
    prefs.get_devices()
    for d in prefs.devices:
        d.use = d.type == 'OPTIX'
    scene.cycles.device = 'GPU'
    scene.render.bake.margin = 8
    world = bpy.data.worlds.new("World")
    world.color = (0.6, 0.55, 0.5)
    scene.world = world
    os.makedirs(TEX, exist_ok=True)


def collection(name):
    col = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    return col


def to_object(name, bm, materials=(), matrix=None, col=None, smooth_angle=None):
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for m in materials:
        me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    (col or bpy.context.scene.collection).objects.link(ob)
    if matrix is not None:
        ob.matrix_world = matrix
    if smooth_angle is not None:
        me.shade_smooth()
        me.set_sharp_from_angle(angle=math.radians(smooth_angle))
    return ob


def apply_modifiers(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    old = ob.data
    ob.modifiers.clear()
    ob.data = me
    name = old.name
    bpy.data.meshes.remove(old)
    me.name = name


def finish(ob, bevel=0.0, segments=3, angle=35, smooth=40):
    """Optional bevel, triangulate (so Blender bake and Unity tangents agree), smooth by angle."""
    if bevel > 0:
        mod = ob.modifiers.new("Bevel", 'BEVEL')
        mod.width = bevel
        mod.segments = segments
        mod.limit_method = 'ANGLE'
        mod.angle_limit = math.radians(angle)
        mod.harden_normals = False
    ob.modifiers.new("Tri", 'TRIANGULATE').quad_method = 'FIXED'
    apply_modifiers(ob)
    ob.data.shade_smooth()
    ob.data.set_sharp_from_angle(angle=math.radians(smooth))
    return ob


# ------------------------------------------------------------------ geometry builders

def lathe(bm, profile, sides, matrix=Matrix(), v_offset=0.0):
    """Revolve (radius, height) around Blender Z. Returns rings of verts."""
    rings = []
    for r, h in profile:
        if r < 1e-6:
            rings.append([bm.verts.new(matrix @ Vector((0, 0, h)))])
            continue
        rings.append([bm.verts.new(matrix @ Vector((math.cos(a) * r, math.sin(a) * r, h)))
                      for a in (2 * math.pi * i / sides + v_offset for i in range(sides))])
    for a, b in zip(rings, rings[1:]):
        if len(a) == 1 or len(b) == 1:
            ring, apex = (b, a[0]) if len(a) == 1 else (a, b[0])
            for i in range(sides):
                bm.faces.new((ring[i], ring[(i + 1) % sides], apex))
        else:
            for i in range(sides):
                bm.faces.new((a[i], a[(i + 1) % sides], b[(i + 1) % sides], b[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return rings


def frames(points, closed):
    """Parallel-transport frames (T, N, B) along a polyline."""
    n = len(points)
    tangents = []
    for i in range(n):
        a = points[(i - 1) % n] if closed or i > 0 else points[i]
        b = points[(i + 1) % n] if closed or i < n - 1 else points[i]
        tangents.append((b - a).normalized())
    ref = Vector((0, 0, 1)) if abs(tangents[0].z) < .9 else Vector((1, 0, 0))
    normal = (ref - tangents[0] * ref.dot(tangents[0])).normalized()
    out = []
    for i, t in enumerate(tangents):
        if i > 0:
            normal = (normal - t * normal.dot(t)).normalized()
        out.append((t, normal, t.cross(normal)))
    return out


def tube(bm, points, radius, sides=8, closed=False, tile=None, caps=True, radius_fn=None):
    """Tube along a polyline. UV u runs along length (in rope tiles), v around; matches the rope tile bake."""
    fr = frames(points, closed)
    lengths = [0.0]
    for i in range(1, len(points) + (1 if closed else 0)):
        lengths.append(lengths[-1] + (points[i % len(points)] - points[i - 1]).length)
    total = lengths[-1]
    tile = tile or 2.333 * radius
    repeats = max(1, round(total / tile)) if closed else total / tile
    uv_layer = bm.loops.layers.uv.verify()
    count = len(points) + (1 if closed else 0)
    rings = []
    for i in range(count):
        p = points[i % len(points)]
        t, nrm, b = fr[i % len(points)]
        r = radius * (radius_fn(lengths[i] / total) if radius_fn else 1)
        rings.append([bm.verts.new(p + (nrm * math.cos(2 * math.pi * j / sides) + b * math.sin(2 * math.pi * j / sides)) * r)
                      for j in range(sides)])
    if closed:  # weld the seam ring positions while keeping a UV seam
        for j in range(sides):
            rings[-1][j].co = rings[0][j].co.copy()
    for i in range(count - 1):
        for j in range(sides):
            f = bm.faces.new((rings[i][j], rings[i][(j + 1) % sides], rings[i + 1][(j + 1) % sides], rings[i + 1][j]))
            us = (lengths[i] / total * repeats, lengths[i + 1] / total * repeats)
            vs = (j / sides, (j + 1) / sides)
            for loop, (u, v) in zip(f.loops, ((us[0], vs[0]), (us[0], vs[1]), (us[1], vs[1]), (us[1], vs[0]))):
                loop[uv_layer].uv = (u, v)
            f.smooth = True
    if caps and not closed:
        for ring, end in ((rings[0], False), (rings[-1], True)):
            f = bm.faces.new(ring if end else list(reversed(ring)))
            f.smooth = True
            for loop in f.loops:
                loop[uv_layer].uv = (0.0, 0.0)
    if closed:
        bmesh.ops.remove_doubles(bm, verts=[v for r in (rings[0], rings[-1]) for v in r], dist=1e-6)
    return rings


def catmull(points, steps):
    out = []
    n = len(points)
    for i in range(n - 1):
        p0, p1, p2, p3 = points[max(i - 1, 0)], points[i], points[i + 1], points[min(i + 2, n - 1)]
        for s in range(steps):
            t = s / steps
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(points[-1])
    return out


# ------------------------------------------------------------------ baking

def select(objs, active=None):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]


def unwrap(objs, angle=60, margin=0.004):
    select(objs)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(angle), island_margin=margin, area_weight=0.0, scale_to_bounds=False)
    bpy.ops.uv.pack_islands(rotate=True, margin=margin, shape_method='CONCAVE')
    bpy.ops.object.mode_set(mode='OBJECT')


def _materials(objs):
    seen = []
    for o in objs:
        for m in o.data.materials:
            if m and m not in seen:
                seen.append(m)
    return seen


def _target(mats, image):
    for m in mats:
        nt = m.node_tree
        node = nt.nodes.get("BakeTarget") or nt.nodes.new('ShaderNodeTexImage')
        node.name = "BakeTarget"
        node.image = image
        nt.nodes.active = node


def _emission_swap(mats, on):
    for m in mats:
        nt = m.node_tree
        out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL')
        bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
        if on:
            em = nt.nodes.new('ShaderNodeEmission')
            em.name = "MetalEmit"
            v = m.get("metal", 0.0)
            em.inputs['Color'].default_value = (v, v, v, 1)
            nt.links.new(em.outputs[0], out.inputs['Surface'])
        else:
            nt.nodes.remove(nt.nodes["MetalEmit"])
            nt.links.new(bsdf.outputs[0], out.inputs['Surface'])


def pixels(image):
    a = np.empty(image.size[0] * image.size[1] * 4, np.float32)
    image.pixels.foreach_get(a)
    return a.reshape(image.size[1], image.size[0], 4)


def save(array, name, colorspace='sRGB', alpha=False):
    h, w = array.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=alpha)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(np.ascontiguousarray(array, np.float32).ravel())
    img.filepath_raw = os.path.join(TEX, name + ".png")
    img.file_format = 'PNG'
    img.save()
    return img


def bake_pass(objs, kind, size, samples, active=None, selected_to_active=False, extrusion=0.0, ray=0.0):
    w, h = size if isinstance(size, tuple) else (size, size)
    img = bpy.data.images.new("bake " + kind, w, h, alpha=False, float_buffer=True)
    img.colorspace_settings.name = 'Non-Color' if kind != 'albedo' else 'Linear Rec.709'
    mats = _materials([active] if selected_to_active else objs)
    _target(mats, img)
    if selected_to_active:
        _target(_materials(objs), img)
    select(objs + ([active] if active else []), active)
    bpy.context.scene.cycles.samples = samples
    types = {'albedo': 'DIFFUSE', 'normal': 'NORMAL', 'ao': 'AO', 'rough': 'ROUGHNESS', 'metal': 'EMIT'}
    if kind == 'metal':
        _emission_swap(_materials(objs), True)
    metals = []
    if kind == 'albedo':   # the DIFFUSE colour pass is black for metals; bake their base colour instead
        for m in _materials(objs):
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED' and not n.inputs['Metallic'].is_linked:
                    metals.append((n, n.inputs['Metallic'].default_value))
                    n.inputs['Metallic'].default_value = 0.0
    kwargs = dict(type=types[kind], margin=8, use_clear=True, use_selected_to_active=selected_to_active)
    if kind == 'albedo':
        kwargs['pass_filter'] = {'COLOR'}
    if kind == 'normal':
        kwargs['normal_space'] = 'TANGENT'
    if selected_to_active:
        kwargs.update(cage_extrusion=extrusion, max_ray_distance=ray)
    bpy.ops.object.bake(**kwargs)
    if kind == 'metal':
        _emission_swap(_materials(objs), False)
    for n, v in metals:
        n.inputs['Metallic'].default_value = v
    data = pixels(img).copy()
    bpy.data.images.remove(img)
    return data


def linear_to_srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def bake_set(label, objs, size, ao_distance=0.2, ao_into_albedo=0.3, active=None, selected_to_active=False,
             extrusion=0.0, ray=0.0):
    """Bakes albedo, tangent normal and a mask (R metal, G AO, A smoothness) for objs (or high->active)."""
    bpy.context.scene.world.light_settings.distance = ao_distance
    args = dict(active=active, selected_to_active=selected_to_active, extrusion=extrusion, ray=ray)
    albedo = bake_pass(objs, 'albedo', size, 4, **args)
    normal = bake_pass(objs, 'normal', size, 8, **args)
    ao = bake_pass(objs, 'ao', size, 256, **args)
    rough = bake_pass(objs, 'rough', size, 4, **args)
    metal = bake_pass(objs, 'metal', size, 2, **args)
    occl = ao[..., 0]
    albedo[..., :3] *= (1 - ao_into_albedo) + ao_into_albedo * occl[..., None]
    albedo[..., :3] = linear_to_srgb(albedo[..., :3])
    albedo[..., 3] = 1
    normal[..., 3] = 1
    mask = np.zeros_like(albedo)
    mask[..., 0] = metal[..., 0]
    mask[..., 1] = occl
    mask[..., 3] = 1 - rough[..., 0]
    save(albedo, label + " albedo", 'sRGB')
    save(normal, label + " normal", 'Non-Color')
    save(mask, label + " mask", 'Non-Color', alpha=True)


def single_material(objs, name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    for o in objs:
        for p in o.data.polygons:
            p.material_index = 0
        o.data.materials.clear()
        o.data.materials.append(mat)
    return mat


def export(objs, filename):
    saved = [(o, o.matrix_world.copy(), o.parent) for o in objs]
    for o in objs:
        o.parent = None
        o.matrix_world = Matrix()
    select(objs)
    bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, filename), use_selection=True, object_types={'MESH'},
                             axis_forward='-Z', axis_up='Y', apply_scale_options='FBX_SCALE_UNITS',
                             bake_space_transform=True, mesh_smooth_type='OFF', use_mesh_modifiers=True,
                             add_leaf_bones=False, bake_anim=False, path_mode='STRIP', use_custom_props=False)
    for o, m, parent in saved:
        o.parent = parent
        o.matrix_world = m
