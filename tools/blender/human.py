"""Higher-fidelity humanoid generator (single watertight body + garment layers).

Pipeline per character:
  1. Body = union of ellipsoids / capsules (torso, limbs, 5-finger hands, face features)
  2. Voxel remesh -> light smooth -> eye sockets & mouth (boolean) -> decimate
  3. Armature (A-pose, 51 bones incl. fingers) + automatic (bone heat) weights
  4. Garments = copies of the weighted body, cropped by region, pushed along
     normals and solidified, so they inherit skin weights automatically
  5. Rigid accessories (eyes, gear) bound 100% to one bone
  6. Everything joined into one skinned mesh

Coordinates: metres, Z up, character faces -Y, built at 1.80 m then scaled.
"""
import math

import bmesh
import bpy
from mathutils import Matrix, Vector, noise

A_POSE_DEG = 42.0

# ---------------------------------------------------------------- material


def mat(name, rgb, metal=0.0, rough=0.6, sss=0.0, emit=None, alpha=1.0):
    m = bpy.data.materials.get(name)
    if m:
        return m
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    if sss:
        b.inputs["Subsurface Weight"].default_value = sss
        b.inputs["Subsurface Radius"].default_value = (1.0, 0.35, 0.2)
        b.inputs["Subsurface Scale"].default_value = 0.01
    if emit:
        b.inputs["Emission Color"].default_value = (*emit, 1)
        b.inputs["Emission Strength"].default_value = 1.5
    if alpha < 1:
        b.inputs["Alpha"].default_value = alpha
        m.blend_method = "BLEND"
    return m


# ---------------------------------------------------------------- primitives


def _link(o):
    bpy.context.scene.collection.objects.link(o)
    return o


def ellipsoid(c, r, rot=None, seg=24, name="ell"):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=seg // 2, radius=1.0)
    m = Matrix.Diagonal((*r, 1.0))
    if rot is not None:
        m = rot.to_4x4() @ m
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(c)) @ m, verts=bm.verts)
    bm.to_mesh(me)
    bm.free()
    for polygon in me.polygons:
        polygon.use_smooth = True
    return _link(bpy.data.objects.new(name, me))


def capsule(p0, p1, r0, r1, seg=16, name="cap"):
    """Tapered capsule: cone between two spheres."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r1, depth=d.length)
    q = d.to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation((p0 + p1) / 2) @ q, verts=bm.verts)
    for p, r in ((p0, r0), (p1, r1)):
        bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=seg // 2, radius=r,
                                  matrix=Matrix.Translation(p))
    bm.to_mesh(me)
    bm.free()
    return _link(bpy.data.objects.new(name, me))


def box(c, size, rot=None, name="box"):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    m = Matrix.Diagonal((*size, 1.0))
    if rot is not None:
        m = rot.to_4x4() @ m
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(c)) @ m, verts=bm.verts)
    bm.to_mesh(me)
    bm.free()
    return _link(bpy.data.objects.new(name, me))


def torus(c, R, r, rot=None, scale=(1, 1, 1), name="tor"):
    bpy.ops.mesh.primitive_torus_add(major_radius=R, minor_radius=r, major_segments=32, minor_segments=10)
    o = bpy.context.active_object
    o.name = name
    m = Matrix.Diagonal((*scale, 1.0))
    if rot is not None:
        m = rot.to_4x4() @ m
    o.data.transform(Matrix.Translation(Vector(c)) @ m)
    for polygon in o.data.polygons:
        polygon.use_smooth = True
    return o


def join(objs, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = name
    return o


def apply_mod(o, mod):
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.modifier_apply(modifier=mod.name)


def basis(u, w):
    """Rotation whose X=u, Y=w, Z=u×w."""
    u = u.normalized()
    w = (w - u * w.dot(u)).normalized()
    n = u.cross(w)
    return Matrix((u, w, n)).transposed()


# ---------------------------------------------------------------- skeleton


def skeleton(p):
    """Joint positions (1.80 m reference) for body params p."""
    sw, hw = p["shoulder"], p["hip"]
    J = {}
    J["pelvis"] = Vector((0, 0, 0.98))
    J["spine"] = Vector((0, 0, 1.10))
    J["chest"] = Vector((0, 0.005, 1.25))
    J["neck"] = Vector((0, 0.01, 1.47))
    neck_length = 0.012 if p.get("portrait") else 0
    J["head"] = Vector((0, 0.0, 1.57 - neck_length))
    J["head_top"] = Vector((0, 0.0, 1.785 - neck_length))
    a = math.radians(p.get("arm_angle", A_POSE_DEG))
    for side, sx in (("L", 1), ("R", -1)):
        dirv = Vector((sx * math.cos(a), 0.0, -math.sin(a)))
        J[f"clav.{side}"] = Vector((sx * 0.025, -0.005, 1.455))
        J[f"shoulder.{side}"] = Vector((sx * 0.185 * sw, 0.005, 1.445))
        J[f"elbow.{side}"] = J[f"shoulder.{side}"] + dirv * p.get("upper_arm_length", 0.285)
        J[f"wrist.{side}"] = J[f"elbow.{side}"] + dirv * p.get("forearm_length", 0.255)
        J[f"knuckle.{side}"] = J[f"wrist.{side}"] + dirv * 0.085
        J[f"hip.{side}"] = Vector((sx * 0.092 * hw, 0.0, 0.935))
        J[f"knee.{side}"] = Vector((sx * 0.098 * hw, -0.012, 0.515))
        J[f"ankle.{side}"] = Vector((sx * 0.102 * hw, 0.02, 0.085))
        J[f"ball.{side}"] = Vector((sx * 0.108 * hw, -0.105, 0.022))
        J[f"toe.{side}"] = Vector((sx * 0.110 * hw, -0.165, 0.02))
        # hand frame: u along arm, w forward(-Y), n = palm normal
        u = dirv
        w = Vector((0, -1, 0))
        J[f"hand_basis.{side}"] = basis(u, w)
    return J


FINGERS = {  # name: (w offset at knuckle, length, radius)
    "index": (0.026, 0.078, 0.0090),
    "middle": (0.008, 0.086, 0.0093),
    "ring": (-0.010, 0.080, 0.0088),
    "pinky": (-0.027, 0.062, 0.0078),
}


def finger_chains(J, side):
    """Returns {finger: [p0,p1,p2,p3]} joint positions."""
    B = J[f"hand_basis.{side}"]
    u, w, n = B.col[0], B.col[1], B.col[2]
    sx = 1 if side == "L" else -1
    k = J[f"knuckle.{side}"]
    out = {}
    for f, (wo, ln, _) in FINGERS.items():
        base = k + w * wo
        seg = [ln * 0.45, ln * 0.31, ln * 0.24]
        # slight natural curl toward palm (-n is palm side when palm faces down/in)
        pts = [base]
        d = u.copy()
        for i, s in enumerate(seg):
            d = (d + n * (-0.18)).normalized()
            pts.append(pts[-1] + d * s)
        out[f] = pts
    tb = J[f"wrist.{side}"] + u * 0.022 + w * 0.024 + n * (-0.012)
    td = (u * 0.55 + w * 0.75 + n * (-0.35)).normalized()
    t1 = tb + td * 0.045
    td2 = (td + u * 0.4).normalized()
    t2 = t1 + td2 * 0.034
    t3 = t2 + td2 * 0.028
    out["thumb"] = [tb, t1, t2, t3]
    return out


def bone_table(J):
    """name -> (head, tail, parent)."""
    T = {
        "hips": (J["pelvis"], J["spine"], None),
        "spine": (J["spine"], J["chest"], "hips"),
        "chest": (J["chest"], J["neck"], "spine"),
        "neck": (J["neck"], J["head"], "chest"),
        "head": (J["head"], J["head_top"], "neck"),
    }
    for s in ("L", "R"):
        T[f"shoulder.{s}"] = (J[f"clav.{s}"], J[f"shoulder.{s}"], "chest")
        T[f"upper_arm.{s}"] = (J[f"shoulder.{s}"], J[f"elbow.{s}"], f"shoulder.{s}")
        T[f"forearm.{s}"] = (J[f"elbow.{s}"], J[f"wrist.{s}"], f"upper_arm.{s}")
        T[f"hand.{s}"] = (J[f"wrist.{s}"], J[f"knuckle.{s}"], f"forearm.{s}")
        for f, pts in finger_chains(J, s).items():
            par = f"hand.{s}"
            for i in range(3):
                bn = f"{f}_{i + 1}.{s}"
                T[bn] = (pts[i], pts[i + 1], par)
                par = bn
        T[f"thigh.{s}"] = (J[f"hip.{s}"], J[f"knee.{s}"], "hips")
        T[f"shin.{s}"] = (J[f"knee.{s}"], J[f"ankle.{s}"], f"thigh.{s}")
        T[f"foot.{s}"] = (J[f"ankle.{s}"], J[f"ball.{s}"], f"shin.{s}")
        T[f"toe.{s}"] = (J[f"ball.{s}"], J[f"toe.{s}"], f"foot.{s}")
    return T


# ---------------------------------------------------------------- body sculpt

DEFAULT = dict(shoulder=1.0, hip=1.0, bulk=1.0, muscle=1.0, belly=0.0, fem=0.0, height=1.80, head=1.06,
               face_width=1.0, jaw_width=1.0, chin_width=1.0, nose_bridge=1.0, nose_width=1.0, nose_depth=1.0)


class Blobs:
    """Metaball element list (smoothly blended soft tissue) + hard union parts."""

    K = 0.575  # surface radius / element radius for a lone ball (threshold 0.6, stiffness 2)

    def __init__(self):
        self.els = []   # (center, radii, rot)
        self.hard = []  # mesh objects unioned by voxel remesh (fingers, nose, ears)

    def ell(self, c, r, rot=None):
        self.els.append((Vector(c), tuple(r), rot))

    def ball(self, c, r):
        self.ell(c, (r, r, r))

    def chain(self, p0, p1, r0, r1, comp=0.86):
        p0, p1 = Vector(p0), Vector(p1)
        n = max(2, int((p1 - p0).length / (min(r0, r1) * 0.6)))
        for i in range(n + 1):
            t = i / n
            self.ball(p0.lerp(p1, t), (r0 + (r1 - r0) * t) * comp)

    def build(self, res=0.0045):
        mb = bpy.data.metaballs.new("flesh")
        mb.resolution = res
        mb.render_resolution = res
        mb.threshold = 0.6
        o = _link(bpy.data.objects.new("flesh", mb))
        for c, r, rot in self.els:
            e = mb.elements.new(type="ELLIPSOID")
            m = max(r)
            e.co = c
            e.radius = m / self.K
            e.size_x, e.size_y, e.size_z = (x / m for x in r)
            if rot is not None:
                e.rotation = rot.to_quaternion()
        bpy.ops.object.select_all(action="DESELECT")
        bpy.context.view_layer.objects.active = o
        o.select_set(True)
        bpy.ops.object.convert(target="MESH")
        return bpy.context.active_object



# Height, lateral radius, front and back depth of the continuous Harbor head.
HEAD_PROFILE = (
    (1.576, 0.018, -0.067, 0.030), (1.594, 0.047, -0.087, 0.042),
    (1.621, 0.060, -0.090, 0.065), (1.650, 0.070, -0.091, 0.087),
    (1.679, 0.078, -0.095, 0.103), (1.707, 0.080, -0.097, 0.108),
    (1.735, 0.077, -0.087, 0.106), (1.762, 0.069, -0.073, 0.092),
    (1.783, 0.047, -0.047, 0.068), (1.803, 0.008, -0.005, 0.028),
)


def profile_section(p, z):
    i = next((i for i in range(1, len(HEAD_PROFILE) - 1) if z <= HEAD_PROFILE[i][0]),
             len(HEAD_PROFILE) - 1)
    a, b = HEAD_PROFILE[i - 1], HEAD_PROFILE[i]
    previous, following = HEAD_PROFILE[max(0, i - 2)], HEAD_PROFILE[min(i + 1, len(HEAD_PROFILE) - 1)]
    span = b[0] - a[0]
    t = min(1.0, max(0.0, (z - a[0]) / span))
    t2, t3 = t * t, t * t * t
    values = []
    for k in range(1, 4):
        da = (b[k] - previous[k]) / (b[0] - previous[0])
        db = (following[k] - a[k]) / (following[0] - a[0])
        values.append((2 * t3 - 3 * t2 + 1) * a[k] + (t3 - 2 * t2 + t) * da * span
                      + (-2 * t3 + 3 * t2) * b[k] + (t3 - t2) * db * span)
    width, front, back = values
    jaw = math.exp(-((z - 1.609) / 0.042) ** 2)
    width *= p["face_width"] * (1 + (p["jaw_width"] - 1) * jaw - p["fem"] * 0.065 * jaw)
    return width, (front + back) * 0.5, (back - front) * 0.5


def face_relief(p, x, z):
    def bump(cx, cz, w, h):
        return math.exp(-((x - cx) / w) ** 2 - ((z - cz) / h) ** 2)

    eye = abs(x) - 0.034
    socket = 0.012 * math.exp(-(eye / 0.022) ** 2 - ((z - 1.693) / 0.0135) ** 2)
    cheek = -0.006 * bump(0.046 if x > 0 else -0.046, 1.672, 0.029, 0.021)
    bridge = -0.018 * bump(0, 1.680, 0.012 * p["nose_bridge"], 0.031)
    tip = -0.015 * bump(0, 1.658, 0.013 * p["nose_width"], 0.010)
    wing = -0.009 * bump(0.010 if x > 0 else -0.010, 1.655, 0.009 * p["nose_width"], 0.007)
    lips = -0.009 * bump(0, 1.627, 0.025, 0.009)
    chin = -0.006 * bump(0, 1.600, 0.029 * p["chin_width"], 0.011)
    return socket + cheek + (bridge + tip + wing) * p["nose_depth"] + lips + chin


def portrait_head(p, hp):
    name = "portrait_head"
    rings, resolution = 57, 80
    vertices = []
    for i in range(rings):
        z = 1.576 + (1.803 - 1.576) * i / (rings - 1)
        width, center, depth = profile_section(p, z)
        for j in range(resolution):
            angle = math.tau * j / resolution
            x, front = math.sin(angle) * width, math.cos(angle)
            y = center - depth * front
            if front > 0:
                y += face_relief(p, x, z) * min(1.0, front * 2)
            vertices.append(hp((x, y, z)))
    faces = []
    for i in range(rings - 1):
        for j in range(resolution):
            a, b = i * resolution + j, i * resolution + (j + 1) % resolution
            faces.append((a, b, b + resolution, a + resolution))
    faces.append(tuple(reversed(range(resolution))))
    faces.append(tuple(range((rings - 1) * resolution, rings * resolution)))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    return _link(bpy.data.objects.new(name, mesh))


def body_parts(p, J):
    b, mu, fem = p["bulk"], p["muscle"], p["fem"]
    hw, sw = p["hip"], p["shoulder"]
    B = Blobs()
    E = B.ell
    # ---- torso
    E((0, 0.008, 0.965), (0.140 * hw * b, 0.098 * b, 0.10))  # pelvis
    for sx in (1, -1):
        E((sx * 0.064 * hw, 0.048 * b, 0.915), (0.072 * hw * b, 0.066 * b, 0.082))  # glutes
    bel = p["belly"]
    E((0, -0.004 - 0.03 * bel, 1.105), (0.122 * b + 0.015 * bel, 0.085 * b + 0.03 * bel, 0.125))  # abdomen
    E((0, 0.0, 1.265), (0.145 * sw * b, 0.098 * b, 0.135))  # ribcage
    E((0, 0.008, 1.38), (0.158 * sw * b, 0.094 * b, 0.088))  # upper chest
    E((0, 0.03, 1.445), (0.11 * sw, 0.06, 0.05), rot=None)  # traps
    for sx in (1, -1):
        E((sx * 0.085 * sw, 0.03, 1.425), (0.07 * sw, 0.045, 0.03),
          rot=Matrix.Rotation(sx * math.radians(-20), 3, "Y"))  # trap slope
        if fem > 0:
            E((sx * 0.058, -0.072 * b, 1.305), (0.058 * fem + 0.015, 0.05 * fem + 0.01, 0.055 * fem + 0.01))
        else:
            E((sx * 0.062 * sw, -0.052 * b, 1.33), (0.068 * mu * b, 0.038 * mu * b, 0.055))  # pecs
        E((sx * 0.10 * sw, 0.018, 1.25), (0.05 * mu * b, 0.07 * b, 0.10))  # lats
    B.chain((0, 0.014, 1.43), (0, 0.004, 1.60), 0.062 * (1 + 0.2 * (mu - 1)) - 0.008 * fem, 0.052 - 0.005 * fem)
    # ---- head
    hs, hc = p["head"], J["head"]
    hp = lambda c: hc + (Vector(c) - Vector((0, 0, 1.585))) * hs
    H = lambda c, r, rot=None: E(hp(c), tuple(x * hs for x in r), rot)
    if p.get("portrait"):
        B.hard.append(portrait_head(p, hp))
    else:
        H((0, 0.014, 1.705), (0.079, 0.097, 0.099))  # cranium
        H((0, -0.026, 1.648), ((0.064 - 0.004 * fem) * p["face_width"], 0.072, 0.074))  # face
        H((0, -0.035, 1.604), ((0.055 - 0.008 * fem) * p["jaw_width"], 0.05, 0.03))  # jaw
        H((0, -0.07, 1.597), ((0.024 - 0.004 * fem) * p["chin_width"], 0.02, 0.019))  # chin
        for sx in (1, -1):
            H((sx * 0.046, -0.07, 1.673), (0.02, 0.014, 0.013))  # cheekbones
        H((0, -0.083, 1.716), (0.055, 0.015, 0.012 - 0.003 * fem))  # brow
        H((0, -0.088, 1.627), (0.021, 0.012, 0.0105))  # lips
    for sx in (1, -1):
        B.hard.append(ellipsoid(hp((sx * 0.08, 0.012, 1.678)), (0.012 * hs, 0.022 * hs, 0.031 * hs),
                                rot=Matrix.Rotation(sx * math.radians(12), 3, "Z"), seg=16, name="ear"))
    if not p.get("portrait"):
        B.hard.append(capsule(hp((0, -0.094, 1.707)), hp((0, -0.114 * p["nose_depth"], 1.664)),
                              0.0075 * hs * p["nose_bridge"], 0.011 * hs * p["nose_bridge"]))
        B.hard.append(ellipsoid(hp((0, -0.106 * p["nose_depth"], 1.657)),
                                (0.017 * hs * p["nose_width"], 0.012 * hs, 0.0095 * hs), seg=16))
    # ---- arms
    for s, sx in (("L", 1), ("R", -1)):
        sh, el, wr = J[f"shoulder.{s}"], J[f"elbow.{s}"], J[f"wrist.{s}"]
        u = (el - sh).normalized()
        rot = basis(u, Vector((0, -1, 0)))
        E(sh + Vector((sx * 0.005, 0.0, -0.007)), (0.058, 0.047 * mu * b, 0.047 * mu * b), rot=rot)  # deltoid
        B.chain(sh, el, 0.047 * b, 0.037 * b)
        E(sh.lerp(el, 0.5) + Vector((0, -0.008, 0)), (0.09, 0.040 * mu * b, 0.043 * mu * b), rot=rot)  # biceps
        B.chain(el, wr, 0.038 * b, 0.025 * b)
        E(el.lerp(wr, 0.28), (0.08, 0.040 * mu * b, 0.036 * b), rot=rot)  # forearm
        # hand (hard union: keeps fingers separated)
        Bh = J[f"hand_basis.{s}"]
        k = J[f"knuckle.{s}"]
        B.hard.append(ellipsoid(wr.lerp(k, 0.55), (0.05, 0.044, 0.0165 * (1 + 0.2 * (b - 1))), rot=Bh, seg=20))
        B.hard.append(ellipsoid(wr.lerp(k, 0.2), (0.03, 0.034, 0.02), rot=Bh, seg=16))  # wrist heel
        B.hard.append(ellipsoid(wr.lerp(k, 0.3) + Bh.col[1] * 0.024 + Bh.col[2] * (-0.01), (0.026, 0.017, 0.015), rot=Bh, seg=16))
        for f, pts in finger_chains(J, s).items():
            r = FINGERS[f][2] if f != "thumb" else 0.0105
            for i in range(3):
                B.hard.append(capsule(pts[i], pts[i + 1], r * (1 - 0.1 * i), r * (0.92 - 0.1 * i), seg=12))
    # ---- legs
    for s, sx in (("L", 1), ("R", -1)):
        hp_, kn, an = J[f"hip.{s}"], J[f"knee.{s}"], J[f"ankle.{s}"]
        B.chain(hp_ + Vector((0, 0, 0.02)), kn, 0.088 * b * (1 + 0.15 * fem), 0.054 * b)
        u = (kn - hp_).normalized()
        E(hp_.lerp(kn, 0.4) + Vector((sx * 0.004, -0.01, 0)), (0.17, 0.068 * mu * b, 0.066 * mu * b), rot=basis(u, Vector((sx, 0, 0))))
        E(kn + Vector((0, -0.018, 0.0)), (0.035, 0.025, 0.035))  # kneecap
        B.chain(kn, an, 0.05 * b, 0.032)
        E(kn.lerp(an, 0.28) + Vector((0, 0.022, 0)), (0.11, 0.045 * mu * b, 0.05 * mu * b), rot=basis(an - kn, Vector((sx, 0, 0))))
        bl, to = J[f"ball.{s}"], J[f"toe.{s}"]
        E((an.x, 0.03, 0.042), (0.03, 0.036, 0.04))  # heel
        E(((an.x + bl.x) / 2, -0.04, 0.038), (0.04, 0.085, 0.032))  # foot
        E((to.x, -0.125, 0.022), (0.042, 0.042, 0.02))  # toes
    return B


def build_body(p, J, voxel=0.0042, target_faces=22000):
    B = body_parts(p, J)
    flesh = B.build(res=p.get("metaball_resolution", 0.0045))
    body = join([flesh] + B.hard, "body")
    rm = body.modifiers.new("remesh", "REMESH")
    rm.mode = "VOXEL"
    rm.voxel_size = voxel
    apply_mod(body, rm)
    sm = body.modifiers.new("smooth", "CORRECTIVE_SMOOTH")
    sm.factor = 0.5
    sm.iterations = 3
    sm.smooth_type = "SIMPLE"
    sm.use_only_smooth = True
    apply_mod(body, sm)
    if not p.get("portrait"):
        # eye sockets + mouth line
        hs, hc = p["head"], J["head"]
        hp = lambda v: hc + (Vector(v) - Vector((0, 0, 1.585))) * hs
        eye_height = 0.0096 if p.get("portrait_eye_socket") else 0.012
        cutters = [ellipsoid(hp((sx * 0.033, -0.097, 1.692)), (0.0165 * hs, 0.016 * hs, eye_height * hs), seg=16)
                   for sx in (1, -1)]
        cutters.append(ellipsoid(hp((0, -0.1005, 1.627)), (0.02 * hs, 0.008 * hs, 0.0016 * hs), seg=16))
        cut = join(cutters, "cutters")
        bo = body.modifiers.new("bool", "BOOLEAN")
        bo.operation = "DIFFERENCE"
        bo.object = cut
        bo.solver = "EXACT"
        apply_mod(body, bo)
        bpy.data.objects.remove(cut)
    dec = body.modifiers.new("dec", "DECIMATE")
    dec.ratio = min(1.0, target_faces / max(1, len(body.data.polygons)))
    apply_mod(body, dec)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.shade_smooth()
    return body


# ---------------------------------------------------------------- rig


def build_rig(J, name):
    T = bone_table(J)
    arm = bpy.data.armatures.new(name)
    rig = _link(bpy.data.objects.new(name, arm))
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    eb = {}
    for bn, (h, t, par) in T.items():
        b = arm.edit_bones.new(bn)
        b.head, b.tail = h, t
        b.roll = 0
        if par:
            b.parent = eb[par]
            b.use_connect = (T[par][1] - h).length < 1e-5
        eb[bn] = b
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def skin_auto(body, rig):
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    missing = [b.name for b in rig.data.bones if b.name not in body.vertex_groups]
    return missing


def limit_skin_weights(mesh):
    """Normalize the four weights that fit glTF's portable skin input."""
    groups = mesh.vertex_groups
    for vertex in mesh.data.vertices:
        weights = sorted(((g.group, g.weight) for g in vertex.groups), key=lambda g: g[1], reverse=True)
        total = sum(weight for _, weight in weights[:4])
        if total > 0:
            for group, weight in weights[:4]:
                groups[group].add([vertex.index], weight / total, "REPLACE")
            for group, _ in weights[4:]:
                groups[group].remove([vertex.index])


def bind_rigid(obj, bone, rig):
    vg = obj.vertex_groups.new(name=bone)
    vg.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
    obj.parent = rig
    m = obj.modifiers.new("rig", "ARMATURE")
    m.object = rig
    return obj


# ---------------------------------------------------------------- garments


def garment(body, name, material, keep, offset=0.01, thick=0.006, smooth=2, noise_amp=0.0, noise_scale=40.0,
            offset_fn=None, planes=(), min_island=40, edge_relax=8):
    """Copy the weighted body, cut clean hems along planes, keep verts where keep(co), push along normals."""
    o = body.copy()
    o.data = body.data.copy()
    o.name = name
    bpy.context.scene.collection.objects.link(o)
    o.data.materials.clear()
    o.data.materials.append(material)
    bm = bmesh.new()
    bm.from_mesh(o.data)
    for co, no in planes:  # insert exact edge loops at hem lines
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=no)
    bm.normal_update()
    kill = [v for v in bm.verts if not keep(v.co)]
    bmesh.ops.delete(bm, geom=kill, context="VERTS")
    # Bisect and vertex deletion can leave new faces with duplicate stale indices.
    bm.faces.index_update()
    # Drop small islands from the region cut.
    seen = set()
    for f in bm.faces:
        if f.index in seen or not f.is_valid:
            continue
        stack, comp = [f], []
        seen.add(f.index)
        while stack:
            g = stack.pop()
            comp.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
        if len(comp) < min_island:
            bmesh.ops.delete(bm, geom=comp, context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.normal_update()
    for v in bm.verts:
        d = offset_fn(v.co) if offset_fn else offset
        if noise_amp:
            d += noise_amp * (0.5 + 0.5 * noise.noise(v.co * noise_scale))
        v.co += v.normal * d
    # relax hem loops so region cuts read as clean seams instead of voxel stair-steps
    bnd = [v for v in bm.verts if v.is_boundary]
    for _ in range(edge_relax):
        new = {}
        for v in bnd:
            nb = [e.other_vert(v) for e in v.link_edges if e.is_boundary]
            if len(nb) == 2:
                new[v] = v.co * 0.4 + (nb[0].co + nb[1].co) * 0.3
        for v, c in new.items():
            v.co = c
    inner = [v for v in bm.verts if not v.is_boundary]
    for _ in range(smooth):
        bmesh.ops.smooth_vert(bm, verts=inner, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
    bm.to_mesh(o.data)
    bm.free()
    for poly in o.data.polygons:
        poly.material_index = 0
        poly.use_smooth = True
    if thick > 0:
        so = o.modifiers.new("solid", "SOLIDIFY")
        so.thickness = thick
        so.offset = -1.0
        so.use_even_offset = False
        so.use_rim = True
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_move_to_index(modifier=so.name, index=0)
        bpy.ops.object.modifier_apply(modifier=so.name)
    return o


# region helpers (reference-space coordinates, 1.80 m)


def in_hand(co, J):
    for s in ("L", "R"):
        if (co - J[f"wrist.{s}"]).dot((J[f"knuckle.{s}"] - J[f"wrist.{s}"]).normalized()) > -0.005 and abs(co.x) > 0.3:
            return True
    return False


def arm_t(co, J):
    """0 at shoulder … 1 at wrist along the arm; None if not on an arm."""
    s = "L" if co.x > 0 else "R"
    sh, wr = J[f"shoulder.{s}"], J[f"wrist.{s}"]
    d = wr - sh
    t = (co - sh).dot(d) / d.length_squared
    if abs(co.x) < 0.15 or t < -0.05:
        return None
    return t


def leg_z(co):
    return co.z


# ---------------------------------------------------------------- assemble


def make_character(name, params, dress, height=None):
    """dress(ctx) adds garments/accessories; ctx has body, rig, J, p, add(obj)."""
    p = dict(DEFAULT)
    p.update(params)
    J = skeleton(p)
    body = build_body(p, J, voxel=p.get("voxel_size", 0.0042), target_faces=p.get("body_faces", 22000))
    body.data.materials.append(p["skin_mat"])
    rig = build_rig(J, f"{name}_rig")
    missing = skin_auto(body, rig)
    if missing:
        print(f"[harbor] {name}: bones without weights: {missing}")
    extras = []

    class Ctx:
        pass

    ctx = Ctx()
    ctx.body, ctx.rig, ctx.J, ctx.p = body, rig, J, p
    ctx.add = extras.append
    ctx.rigid = lambda o, bone: extras.append(bind_rigid(o, bone, rig))
    hide_body = dress(ctx) or (lambda co: False)
    # delete body verts fully covered by clothing to avoid z-fighting / save polys
    bm = bmesh.new()
    bm.from_mesh(body.data)
    kill = [v for v in bm.verts if hide_body(v.co)]
    bmesh.ops.delete(bm, geom=kill, context="VERTS")
    bm.to_mesh(body.data)
    bm.free()
    for e in extras:
        if e.parent is None:
            e.parent = rig
    mesh = join([body] + extras, name)
    # keep one armature modifier
    mods = [m for m in mesh.modifiers if m.type == "ARMATURE"]
    for m in mods[1:]:
        mesh.modifiers.remove(m)
    if not mods:
        m = mesh.modifiers.new("rig", "ARMATURE")
        m.object = rig
    mesh.parent = rig
    budget = p.get("max_faces", 48000)
    if len(mesh.data.polygons) > budget:
        dec = mesh.modifiers.new("dec", "DECIMATE")
        dec.ratio = budget / len(mesh.data.polygons)
        dec.vertex_group = "head"  # keep the face dense
        dec.invert_vertex_group = True
        dec.vertex_group_factor = 10.0
        bpy.context.view_layer.objects.active = mesh
        bpy.ops.object.modifier_move_to_index(modifier=dec.name, index=0)
        bpy.ops.object.modifier_apply(modifier=dec.name)
    if p.get("portrait"):
        limit_skin_weights(mesh)
    s = p["height"] / 1.80
    if abs(s - 1) > 1e-4:
        rig.scale = (s, s, s)
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        mesh.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return rig, mesh


def eyes(ctx, iris_rgb=(0.25, 0.15, 0.08)):
    J, p = ctx.J, ctx.p
    hs, hc = p["head"], J["head"]
    hp = lambda v: hc + (Vector(v) - Vector((0, 0, 1.585))) * hs
    white = mat("eye_white", (0.85, 0.84, 0.8), rough=0.15)
    iris = mat(f"iris_{iris_rgb}", iris_rgb, rough=0.1)
    for sx in (1, -1):
        e = ellipsoid(hp((sx * 0.033, -0.0835, 1.692)), (0.0122 * hs,) * 3, seg=16, name="eye")
        e.data.materials.append(white)
        ctx.rigid(e, "head")
        i = ellipsoid(hp((sx * 0.033, -0.0948, 1.692)), (0.0054 * hs, 0.0022 * hs, 0.0054 * hs), seg=12, name="iris")
        i.data.materials.append(iris)
        ctx.rigid(i, "head")
        for dz, rz, tilt in ((0.0062, 0.0075, -1), (-0.0072, 0.005, 1)):  # upper / lower lid
            lid = ellipsoid(hp((sx * 0.033, -0.0855, 1.692 + dz)), (0.0158 * hs, 0.0125 * hs, rz * hs), seg=16,
                            rot=Matrix.Rotation(tilt * math.radians(12), 3, "X"), name="lid")
            lid.data.materials.append(p["skin_mat"])
            ctx.rigid(lid, "head")


def brows(ctx, hair_mat, thick=1.0):
    J, p = ctx.J, ctx.p
    hs, hc = p["head"], J["head"]
    hp = lambda v: hc + (Vector(v) - Vector((0, 0, 1.585))) * hs
    for sx in (1, -1):
        b = ellipsoid(hp((sx * 0.035, -0.0935, 1.713)), (0.018 * hs, 0.005 * hs, 0.0026 * hs * thick), seg=12,
                      rot=Matrix.Rotation(sx * math.radians(-8), 3, "Y"), name="brow")
        b.data.materials.append(hair_mat)
        ctx.rigid(b, "head")


def head_local(ctx, co):
    """Reference-head coordinates for a world co (undo head scaling)."""
    hs, hc = ctx.p["head"], ctx.J["head"]
    return (co - hc) / hs + Vector((0, 0, 1.585))


def hair_planes(ctx, style):
    hs, hc = ctx.p["head"], ctx.J["head"]
    w = lambda v: hc + (Vector(v) - Vector((0, 0, 1.585))) * hs
    if style == "beanie":
        return [(w((0, 0, 1.705)), Vector((0, 0, 1)))]
    return [(w((0, 0, 1.742)), Vector((0, 0, 1))), (w((0, -0.045, 1.7)), Vector((0, 1, 0)))]


def hair_region(ctx, style):
    def keep(co):
        if co.z < ctx.J["neck"].z:
            return False
        h = head_local(ctx, co)
        if abs(h.x) > 0.13 or h.z < 1.58:
            return False
        ear = abs(abs(h.x) - 0.086) < 0.02 and abs(h.z - 1.678) < 0.035 and abs(h.y - 0.008) < 0.03
        if ear and style != "long":
            return False
        if style == "beanie":
            return h.z > 1.705 - max(0, h.y) * 0.3
        if style == "helmet":
            return h.z > 1.672 - max(0.0, h.y + 0.02) * 0.6 and not (h.y < -0.06 and h.z < 1.735)
        front = h.z > 1.742 - max(0.0, h.y + 0.08) * 0.0
        side = h.y > -0.045 and h.z > 1.695
        back = h.y > 0.03 and h.z > (1.615 if style in ("short", "slick") else 1.58)
        temple = abs(h.x) > 0.07 and h.y > -0.06 and h.z > 1.70
        return front or side or back or temple
    return keep


def beard_region(ctx):
    def keep(co):
        h = head_local(ctx, co)
        if h.z > 1.66 or h.z < 1.565 or h.y > 0.0:
            return False
        if abs(h.x) < 0.028 and h.z > 1.618 and h.z < 1.64 and h.y < -0.08:
            return False  # mouth gap
        return h.y < -0.02 + (h.z - 1.60) * 0.0
    return keep
