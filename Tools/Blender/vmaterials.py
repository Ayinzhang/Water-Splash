"""Procedural Blender materials used only as bake sources."""
import bpy, math


class Graph:
    def __init__(self, name, metal=0.0):
        self.mat = bpy.data.materials.new(name)
        self.mat.use_nodes = True
        self.mat["metal"] = metal
        self.nt = self.mat.node_tree
        self.nt.nodes.clear()
        out = self.node('ShaderNodeOutputMaterial')
        self.bsdf = self.node('ShaderNodeBsdfPrincipled')
        self.link(self.bsdf.outputs[0], out.inputs['Surface'])
        self.bsdf.inputs['Metallic'].default_value = metal

    def node(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            if hasattr(n, k):
                setattr(n, k, v)
            else:
                n.inputs[k].default_value = v
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def coords(self, kind='Object', scale=(1, 1, 1), rotation=(0, 0, 0), location=(0, 0, 0)):
        tc = self.node('ShaderNodeTexCoord')
        mp = self.node('ShaderNodeMapping')
        mp.inputs['Scale'].default_value = scale
        mp.inputs['Rotation'].default_value = rotation
        mp.inputs['Location'].default_value = location
        self.link(tc.outputs[kind], mp.inputs['Vector'])
        return mp.outputs[0]

    def ramp(self, fac, stops):
        r = self.node('ShaderNodeValToRGB')
        els = r.color_ramp.elements
        while len(els) < len(stops):
            els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos
            e.color = col if len(col) == 4 else (*col, 1)
        self.link(fac, r.inputs['Fac'])
        return r.outputs['Color']

    def mix(self, a, b, fac, blend='MIX'):
        m = self.node('ShaderNodeMix', data_type='RGBA', blend_type=blend)
        ins = {s.identifier: s for s in m.inputs}
        for sock, val in ((ins['Factor_Float'], fac), (ins['A_Color'], a), (ins['B_Color'], b)):
            if isinstance(val, (int, float, tuple)):
                sock.default_value = val if not isinstance(val, tuple) or len(val) == 4 else (*val, 1)
            else:
                self.link(val, sock)
        return next(s for s in m.outputs if s.identifier == 'Result_Color')

    def math(self, op, a, b=0.0, c=0.0):
        n = self.node('ShaderNodeMath', operation=op)
        for i, v in enumerate((a, b, c)):
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            else:
                self.link(v, n.inputs[i])
        return n.outputs[0]

    def noise(self, vec, scale, detail=4.0, rough=0.5, distortion=0.0):
        n = self.node('ShaderNodeTexNoise')
        n.inputs['Scale'].default_value = scale
        n.inputs['Detail'].default_value = detail
        n.inputs['Roughness'].default_value = rough
        n.inputs['Distortion'].default_value = distortion
        self.link(vec, n.inputs['Vector'])
        return n

    def bump(self, height, strength, distance=0.002, normal=None):
        b = self.node('ShaderNodeBump')
        b.inputs['Strength'].default_value = strength
        b.inputs['Distance'].default_value = distance
        self.link(height, b.inputs['Height'])
        if normal is not None:
            self.link(normal, b.inputs['Normal'])
        return b.outputs['Normal']

    def finish(self, color, roughness, normal=None):
        for name, v in (('Base Color', color), ('Roughness', roughness), ('Normal', normal)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                self.bsdf.inputs[name].default_value = v
            elif isinstance(v, tuple):
                self.bsdf.inputs[name].default_value = v if len(v) == 4 else (*v, 1)
            else:
                self.link(v, self.bsdf.inputs[name])
        return self.mat


GRAIN_ROT = {'X': (0, math.pi / 2, 0), 'Y': (math.pi / 2, 0, 0), 'Z': (0, 0, 0)}


def wood(name, light, dark, grain='X', rings=9.0, rough=0.42, pore_scale=260.0, offset=(0.3, 0.22, 0.0), figure=1.0):
    """Plain-sawn wood: cylindrical rings around the grain axis seen off-centre, plus stretched pores."""
    g = Graph(name)
    v = g.coords(rotation=GRAIN_ROT[grain])
    # Mapping rotates the grain axis onto Z, then offsets the log centre so rings read as cathedral figure.
    mp = g.node('ShaderNodeMapping')
    mp.inputs['Location'].default_value = offset
    g.link(v, mp.inputs['Vector'])
    warp = g.noise(mp.outputs[0], 1.6, 3, .5)
    wv = g.node('ShaderNodeVectorMath', operation='MULTIPLY_ADD')
    g.link(warp.outputs['Color'], wv.inputs[0])
    wv.inputs[1].default_value = (0.08 * figure, 0.08 * figure, 0.0)
    g.link(mp.outputs[0], wv.inputs[2])
    wave = g.node('ShaderNodeTexWave', wave_type='RINGS', rings_direction='Z')
    wave.inputs['Scale'].default_value = rings
    wave.inputs['Distortion'].default_value = 2.5 * figure
    wave.inputs['Detail'].default_value = 3
    wave.inputs['Detail Scale'].default_value = 1.2
    g.link(wv.outputs[0], wave.inputs['Vector'])
    sharp = g.math('POWER', wave.outputs['Fac'], 1.8)
    base = g.ramp(sharp, [(0.0, light), (0.55, tuple(.5 * a + .5 * b for a, b in zip(light, dark))), (1.0, dark)])
    pv = g.node('ShaderNodeMapping')
    pv.inputs['Scale'].default_value = (pore_scale, pore_scale, pore_scale * .04)
    g.link(mp.outputs[0], pv.inputs['Vector'])
    pores = g.noise(pv.outputs[0], 1.0, 2, .6)
    pore = g.ramp(pores.outputs['Fac'], [(0.36, (1, 1, 1)), (0.62, (0, 0, 0))])
    pore_f = g.node('ShaderNodeRGBToBW')
    g.link(pore, pore_f.inputs[0])
    mottle = g.noise(v, 3.0, 5, .55)
    color = g.mix(base, dark, g.math('MULTIPLY', pore_f.outputs[0], .55))
    color = g.mix(color, g.ramp(mottle.outputs['Fac'], [(.3, (.82, .82, .82)), (.7, (1.08, 1.06, 1.0))]), 1.0, 'MULTIPLY')
    rough_v = g.math('MULTIPLY_ADD', pore_f.outputs[0], .18, rough)
    height = g.math('MULTIPLY_ADD', sharp, .25, g.math('MULTIPLY', pore_f.outputs[0], -.6))
    return g.finish(color, rough_v, g.bump(height, .35, .002))


def cork(name):
    g = Graph(name)
    v = g.coords()
    vor = g.node('ShaderNodeTexVoronoi')
    vor.inputs['Scale'].default_value = 90
    g.link(v, vor.inputs['Vector'])
    pits = g.ramp(vor.outputs['Distance'], [(0.0, (0, 0, 0)), (0.14, (1, 1, 1))])
    pit = g.node('ShaderNodeRGBToBW')
    g.link(pits, pit.inputs[0])
    fine = g.noise(v, 260, 6, .65)
    blot = g.noise(v, 9, 4, .5)
    base = g.ramp(blot.outputs['Fac'], [(.3, (.43, .27, .14)), (.7, (.66, .47, .28))])
    color = g.mix(base, (.2, .11, .05), g.math('SUBTRACT', 1.0, pit.outputs[0]))
    color = g.mix(color, g.ramp(fine.outputs['Fac'], [(.35, (.78, .74, .7)), (.65, (1.1, 1.05, 1))]), 1.0, 'MULTIPLY')
    height = g.math('MULTIPLY_ADD', fine.outputs['Fac'], .5, pit.outputs[0])
    return g.finish(color, .86, g.bump(height, .55, .004))


def felt(name, color):
    g = Graph(name)
    v = g.coords()
    n = g.noise(v, 700, 8, .7)
    tint = g.ramp(n.outputs['Fac'], [(.3, tuple(c * .75 for c in color)), (.7, tuple(min(1, c * 1.2) for c in color))])
    g.bsdf.inputs['Sheen Weight'].default_value = .8
    g.bsdf.inputs['Sheen Roughness'].default_value = .4
    return g.finish(tint, .95, g.bump(n.outputs['Fac'], .4, .001))


def brass(name, tarnish=0.35):
    g = Graph(name, metal=1.0)
    v = g.coords()
    n = g.noise(v, 22, 6, .6)
    color = g.ramp(n.outputs['Fac'], [(.35, (.86, .64, .32)), (.62, (.62, .44, .2)), (.8, (.42, .36, .2))])
    rough_v = g.math('MULTIPLY_ADD', n.outputs['Fac'], tarnish, .18)
    micro = g.noise(v, 600, 3, .5)
    return g.finish(color, rough_v, g.bump(micro.outputs['Fac'], .06, .001))


def linen(name, color=(.88, .83, .72), weave=900.0):
    g = Graph(name)
    v = g.coords()
    a = g.node('ShaderNodeTexWave', wave_type='BANDS', bands_direction='X')
    b = g.node('ShaderNodeTexWave', wave_type='BANDS', bands_direction='Z')
    for w in (a, b):
        w.inputs['Scale'].default_value = weave
        w.inputs['Distortion'].default_value = .4
        g.link(v, w.inputs['Vector'])
    blot = g.noise(v, 6, 4, .55)
    weave_h = g.math('MULTIPLY', g.math('ADD', a.outputs['Fac'], b.outputs['Fac']), .5)
    tint = g.ramp(blot.outputs['Fac'], [(.3, tuple(c * .9 for c in color)), (.7, color)])
    tint = g.mix(tint, g.ramp(weave_h, [(0, (.9, .9, .9)), (1, (1.04, 1.04, 1.04))]), 1.0, 'MULTIPLY')
    g.bsdf.inputs['Sheen Weight'].default_value = .4
    return g.finish(tint, .82, g.bump(weave_h, .25, .0008))


def cotton(name, color=(.84, .78, .66)):
    """Fibre bump along strand UVs (u along strand)."""
    g = Graph(name)
    uv = g.node('ShaderNodeUVMap')
    sep = g.node('ShaderNodeSeparateXYZ')
    g.link(uv.outputs[0], sep.inputs[0])
    fib = g.math('MULTIPLY_ADD', sep.outputs[1], 40.0, g.math('MULTIPLY', sep.outputs[0], 55.0))
    stripes = g.math('SINE', fib)
    v = g.coords()
    n = g.noise(v, 30, 4, .5)
    tint = g.ramp(n.outputs['Fac'], [(.3, tuple(c * .86 for c in color)), (.7, color)])
    tint = g.mix(tint, g.ramp(stripes, [(0, (.86, .86, .86)), (1, (1.0, 1.0, 1.0))]), 1.0, 'MULTIPLY')
    g.bsdf.inputs['Sheen Weight'].default_value = .6
    return g.finish(tint, .9, g.bump(stripes, .35, .02))


def shell(name, color=(.93, .86, .76), band=(.8, .55, .4)):
    g = Graph(name)
    v = g.coords()
    grad = g.node('ShaderNodeTexWave', wave_type='RINGS', rings_direction='SPHERICAL')
    grad.inputs['Scale'].default_value = 6
    grad.inputs['Distortion'].default_value = 1.5
    g.link(v, grad.inputs['Vector'])
    n = g.noise(v, 14, 5, .55)
    tint = g.ramp(grad.outputs['Fac'], [(0, color), (.6, color), (1, band)])
    tint = g.mix(tint, g.ramp(n.outputs['Fac'], [(.3, (.9, .9, .9)), (.7, (1.03, 1.03, 1.03))]), 1.0, 'MULTIPLY')
    return g.finish(tint, .45, g.bump(n.outputs['Fac'], .15, .002))


def stone(name, a, b, scale=4.0):
    g = Graph(name)
    v = g.coords()
    n = g.noise(v, scale, 8, .62, .3)
    speck = g.noise(v, 140, 2, .5)
    tint = g.ramp(n.outputs['Fac'], [(.25, a), (.75, b)])
    tint = g.mix(tint, g.ramp(speck.outputs['Fac'], [(.6, (1, 1, 1)), (.75, (.55, .52, .5))]), 1.0, 'MULTIPLY')
    return g.finish(tint, g.math('MULTIPLY_ADD', n.outputs['Fac'], .2, .55), g.bump(speck.outputs['Fac'], .12, .002))


def plain(name, color, rough=.5, metal=0.0):
    return Graph(name, metal).finish(color, rough)


def painted(name, image_path, rough=.75, bump_scale=420.0):
    """Paper with a painted image (UV) and fibre bump."""
    g = Graph(name)
    tex = g.node('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(image_path, check_existing=True)
    v = g.coords()
    n = g.noise(v, bump_scale, 6, .6)
    return g.finish(tex.outputs['Color'], rough, g.bump(n.outputs['Fac'], .18, .001))
