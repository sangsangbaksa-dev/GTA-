"""Procedural asset generator for Project Harbor (original low-poly designs).

Run headless:
    blender -b --factory-startup -P tools/blender/build_assets.py -- \
        --out game/assets [--only characters,weapons]

Props live in tools/blender/build_props.py (output goes to the Set repo).

Outputs .glb files (glTF 2.0). Conventions (see game/assets/README.md):
  * 1 unit = 1 m, Blender Z-up / -Y forward (exporter converts to glTF Y-up).
  * Characters: one skinned mesh + armature "<name>_rig" (51 bones incl. fingers), A-pose, feet at origin.
    Built by characters.py / human.py (metaball body, garment layers, auto weights).
  * Weapons: origin at grip, barrel along -Y, empties "muzzle" and "grip_off".
  * Buildings: mesh name ends with "-col" so Godot generates trimesh collision.
"""
import argparse
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import characters  # noqa: E402  (higher-fidelity humans, see human.py)

# ---------------------------------------------------------------- utilities

_MATS = {}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _MATS.clear()


def mat(name, rgb, metal=0.0, rough=0.7, alpha=1.0, emit=None):
    key = name
    if key in _MATS:
        return _MATS[key]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Roughness"].default_value = rough
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        m.blend_method = "BLEND"
    if emit:
        bsdf.inputs["Emission Color"].default_value = (*emit, 1.0)
        bsdf.inputs["Emission Strength"].default_value = 2.0
    _MATS[key] = m
    return m


def _finish(obj, material, bevel=0.0):
    if material:
        obj.data.materials.append(material)
    if bevel > 0:
        mod = obj.modifiers.new("bevel", "BEVEL")
        mod.width = bevel
        mod.segments = 2
        mod.limit_method = "ANGLE"
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=mod.name)
    return obj


def box(size, loc, material=None, rot=(0, 0, 0), bevel=0.0, name="box"):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return _finish(o, material, bevel)


def cyl(r, depth, loc, material=None, rot=(0, 0, 0), verts=12, name="cyl", r2=None):
    if r2 is None:
        bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r, depth=depth, location=loc, rotation=rot)
    else:
        bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r, radius2=r2, depth=depth, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    return _finish(o, material)


def sphere(r, loc, material=None, scale=(1, 1, 1), seg=12, name="sph"):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=max(6, seg // 2), radius=r, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.ops.object.shade_smooth()
    return _finish(o, material)


def limb(p0, p1, r, material, r_end=None, verts=10, name="limb"):
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    o = cyl(r, d.length, (p0 + p1) / 2, material, verts=verts, name=name, r2=r_end)
    o.rotation_mode = "QUATERNION"
    # cone radius1 sits at -Z, so point -Z at p0
    o.rotation_quaternion = d.to_track_quat("Z", "Y")
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
    bpy.ops.object.shade_smooth()
    return o


def join(objs, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = name
    o.data.name = name
    return o


def empty(name, loc, parent=None):
    e = bpy.data.objects.new(name, None)
    e.location = loc
    e.empty_display_size = 0.05
    bpy.context.scene.collection.objects.link(e)
    if parent:
        e.parent = parent
    return e


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_apply=True,
        export_yup=True,
        export_animations=False,
    )
    print(f"[harbor] wrote {path}")


# ---------------------------------------------------------------- palette

def P():
    """Shared materials, created per scene."""
    return dict(
        black=mat("black", (0.03, 0.03, 0.035), rough=0.6),
        gunmetal=mat("gunmetal", (0.12, 0.12, 0.13), metal=0.9, rough=0.35),
        steel=mat("steel", (0.55, 0.56, 0.58), metal=1.0, rough=0.3),
        polymer=mat("polymer", (0.08, 0.085, 0.09), rough=0.8),
        wood=mat("wood", (0.36, 0.20, 0.10), rough=0.7),
        boots=mat("boots", (0.10, 0.07, 0.05), rough=0.5),
        teal=mat("teal", (0.0, 0.45, 0.45), rough=0.6),
        concrete=mat("concrete", (0.55, 0.54, 0.52), rough=0.95),
        brick=mat("brick", (0.45, 0.18, 0.12), rough=0.9),
        glass=mat("glass", (0.25, 0.4, 0.5), metal=0.2, rough=0.05, alpha=0.45),
        trim=mat("trim", (0.85, 0.85, 0.82), rough=0.6),
        asphalt=mat("asphalt", (0.10, 0.10, 0.11), rough=0.95),
    )


# ---------------------------------------------------------------- weapons
# Origin = grip centre. Barrel points -Y. Empties: muzzle, grip_off (support hand).
R90X = (math.radians(90), 0, 0)


def _wpn_finish(name, parts, muzzle, support=None):
    o = join(parts, name)
    empty("muzzle", muzzle, o)
    if support:
        empty("grip_off", support, o)
    return o


def bx(size, loc, m, bev=0.0025, rot=(0, 0, 0)):
    return box(size, loc, m, rot=rot, bevel=bev)


def tube(r, length, y0, z, m, verts=16, x=0.0, r2=None):
    """Cylinder along Y from y0 toward -Y."""
    return cyl(r, length, (x, y0 - length / 2, z), m, rot=R90X, verts=verts, r2=r2)


def rail(parts, m, y0, y1, z, w=0.022):
    """Picatinny-style rail: base + cross teeth."""
    parts.append(bx((w, abs(y1 - y0), 0.006), (0, (y0 + y1) / 2, z), m, bev=0.001))
    n = int(abs(y1 - y0) / 0.01)
    for i in range(n):
        parts.append(box((w + 0.002, 0.005, 0.005), (0, y0 - (i + 0.5) * (y0 - y1) / n, z + 0.005), m))


def ring(R, r, loc, m, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_torus_add(major_radius=R, minor_radius=r, major_segments=20, minor_segments=8,
                                     location=loc, rotation=rot)
    o = bpy.context.active_object
    o.data.materials.append(m)
    return o


def wpn_pistol():
    """Kestrel P10 — compact polymer-frame pistol."""
    reset(); p = P()
    g, poly, blk = p["gunmetal"], p["polymer"], p["black"]
    parts = [
        bx((0.029, 0.185, 0.032), (0, -0.048, 0.076), g, bev=0.004),  # slide
        bx((0.031, 0.05, 0.004), (0, -0.07, 0.093), blk, bev=0.0),  # ejection port shadow
        bx((0.027, 0.16, 0.022), (0, -0.04, 0.049), poly, bev=0.004),  # frame
        bx((0.028, 0.048, 0.115), (0, 0.022, -0.01), poly, rot=(math.radians(-16), 0, 0), bev=0.006),  # grip
        bx((0.031, 0.05, 0.01), (0, 0.04, -0.068), blk, rot=(math.radians(-16), 0, 0), bev=0.002),  # mag base
        bx((0.006, 0.012, 0.022), (0, -0.012, 0.025), blk, rot=(math.radians(12), 0, 0), bev=0.001),  # trigger
        ring(0.019, 0.003, (0, -0.016, 0.028), poly, rot=(0, math.radians(90), 0)),  # trigger guard
        tube(0.0065, 0.012, -0.14, 0.078, blk, verts=12),  # barrel crown
        bx((0.005, 0.008, 0.008), (0, -0.132, 0.096), p["teal"], bev=0.0),  # front sight (teal dot)
        bx((0.007, 0.008, 0.008), (0.009, 0.036, 0.096), blk, bev=0.0),  # rear sight
        bx((0.007, 0.008, 0.008), (-0.009, 0.036, 0.096), blk, bev=0.0),
        bx((0.004, 0.02, 0.004), (0.016, -0.03, 0.062), g, bev=0.0),  # slide release
    ]
    for i in range(6):  # rear slide serrations
        for sx in (1, -1):
            parts.append(box((0.002, 0.003, 0.024), (sx * 0.0148, 0.02 + i * 0.006, 0.076), blk))
    for i in range(4):  # accessory rail slots under frame
        parts.append(box((0.02, 0.004, 0.004), (0, -0.09 - i * 0.01, 0.037), blk))
    for i in range(5):  # grip texture
        for sx in (1, -1):
            parts.append(box((0.002, 0.032, 0.006), (sx * 0.0145, 0.022 + i * 0.004, 0.025 - i * 0.02),
                             blk, rot=(math.radians(-16), 0, 0)))
    _wpn_finish("wpn_pistol_kestrel", parts, (0, -0.152, 0.078))


def wpn_smg():
    """Wasp SMG — boxy, top-heavy, ventilated shroud, folding stock."""
    reset(); p = P()
    g, poly, blk, st = p["gunmetal"], p["polymer"], p["black"], p["steel"]
    parts = [
        bx((0.044, 0.29, 0.06), (0, -0.08, 0.065), g, bev=0.005),  # upper receiver
        bx((0.04, 0.17, 0.03), (0, -0.04, 0.022), poly, bev=0.004),  # lower
        bx((0.046, 0.05, 0.006), (0, -0.07, 0.093), blk, bev=0.0),  # ejection port
        bx((0.034, 0.05, 0.12), (0, 0.01, -0.03), poly, rot=(math.radians(-14), 0, 0), bev=0.006),  # grip
        ring(0.02, 0.003, (0, -0.028, 0.0), poly, rot=(0, math.radians(90), 0)),
        bx((0.006, 0.012, 0.02), (0, -0.026, 0.003), blk, rot=(math.radians(12), 0, 0), bev=0.0),
        bx((0.028, 0.04, 0.2), (0, -0.115, -0.06), poly, rot=(math.radians(10), 0, 0), bev=0.004),  # mag
        bx((0.032, 0.045, 0.012), (0, -0.098, -0.162), blk, rot=(math.radians(10), 0, 0), bev=0.002),
        tube(0.022, 0.13, -0.225, 0.065, g, verts=16),  # shroud
        tube(0.011, 0.05, -0.355, 0.065, blk, verts=12),  # muzzle
        bx((0.012, 0.025, 0.012), (0.026, -0.03, 0.08), blk, bev=0.002),  # charging handle
        bx((0.04, 0.06, 0.035), (0, -0.25, 0.018), poly, bev=0.006),  # vertical foregrip block
        bx((0.03, 0.03, 0.09), (0, -0.25, -0.03), poly, rot=(math.radians(8), 0, 0), bev=0.006),
    ]
    for i in range(6):  # shroud vents
        for sx in (1, -1):
            parts.append(box((0.004, 0.012, 0.008), (sx * 0.021, -0.17 - i * 0.018, 0.065), blk))
    rail(parts, blk, -0.02, -0.21, 0.098)
    parts += [bx((0.016, 0.012, 0.022), (0, -0.2, 0.112), blk, bev=0.002), bx((0.02, 0.012, 0.018), (0, 0.0, 0.11), blk, bev=0.002)]
    for sx in (1, -1):  # folding stock rods + hinge
        parts.append(tube(0.0055, 0.22, 0.29, 0.06, st, verts=10, x=sx * 0.016))
    parts += [bx((0.05, 0.02, 0.1), (0, 0.29, 0.035), poly, bev=0.006), bx((0.046, 0.03, 0.03), (0, 0.075, 0.06), g, bev=0.004)]
    _wpn_finish("wpn_smg_wasp", parts, (0, -0.38, 0.065), (0, -0.25, -0.02))


def wpn_rifle():
    """Halberd AR — modular rifle with slotted handguard, optic, adjustable stock."""
    reset(); p = P()
    g, poly, blk = p["gunmetal"], p["polymer"], p["black"]
    tan = mat("rifle_tan", (0.45, 0.38, 0.26), rough=0.7)
    lens = mat("lens", (0.1, 0.5, 0.3), rough=0.05, emit=(0.05, 0.3, 0.15))
    parts = [
        bx((0.048, 0.25, 0.05), (0, -0.05, 0.078), g, bev=0.005),  # upper
        bx((0.044, 0.2, 0.035), (0, -0.02, 0.037), g, bev=0.005),  # lower
        bx((0.05, 0.06, 0.006), (0, -0.07, 0.104), blk, bev=0.0),
        bx((0.012, 0.02, 0.014), (0.03, -0.0, 0.075), blk, bev=0.002),  # forward assist
        bx((0.055, 0.31, 0.055), (0, -0.33, 0.075), tan, bev=0.012),  # handguard
        tube(0.011, 0.17, -0.48, 0.075, blk, verts=12),
        tube(0.017, 0.055, -0.65, 0.075, blk, verts=8),  # brake
        bx((0.036, 0.05, 0.12), (0, 0.035, -0.02), poly, rot=(math.radians(-18), 0, 0), bev=0.006),
        ring(0.022, 0.003, (0, -0.012, 0.012), g, rot=(0, math.radians(90), 0)),
        bx((0.006, 0.012, 0.022), (0, -0.01, 0.014), blk, rot=(math.radians(12), 0, 0), bev=0.0),
        tube(0.014, 0.18, 0.25, 0.07, blk, verts=12),  # buffer tube
        bx((0.044, 0.16, 0.065), (0, 0.27, 0.055), tan, bev=0.012),  # stock
        bx((0.05, 0.025, 0.13), (0, 0.36, 0.04), blk, bev=0.006),
        bx((0.012, 0.012, 0.03), (0, -0.47, 0.115), blk, bev=0.001),  # front sight post
        tube(0.018, 0.11, 0.0, 0.142, blk, verts=16),  # optic tube
        tube(0.022, 0.02, -0.11, 0.142, blk, verts=16),
        cyl(0.019, 0.002, (0, -0.131, 0.142), lens, rot=R90X, verts=16),
        bx((0.02, 0.03, 0.02), (0, -0.055, 0.118), blk, bev=0.002),  # optic mount
    ]
    for i in range(7):  # curved magazine segments
        a = math.radians(10 + i * 3)
        parts.append(bx((0.03, 0.068, 0.034), (0, -0.115 - i * 0.008, -0.002 - i * 0.03), poly, rot=(a, 0, 0), bev=0.003))
    for i in range(5):  # handguard slots
        for sx in (1, -1):
            parts.append(box((0.004, 0.04, 0.012), (sx * 0.0275, -0.22 - i * 0.055, 0.075), blk))
    for i in range(3):  # muzzle brake ports
        for sx in (1, -1):
            parts.append(box((0.01, 0.008, 0.006), (sx * 0.015, -0.632 - i * 0.012, 0.075), p["steel"]))
    rail(parts, blk, 0.07, -0.48, 0.108)
    _wpn_finish("wpn_rifle_halberd", parts, (0, -0.68, 0.075), (0, -0.36, 0.04))


def wpn_shotgun():
    """Breaker 12 — pump shotgun, wood furniture, side shell carrier."""
    reset(); p = P()
    g, blk, wood = p["gunmetal"], p["black"], mat("walnut", (0.30, 0.15, 0.07), rough=0.55)
    shell = mat("shell_red", (0.6, 0.06, 0.04), rough=0.5)
    brass = mat("brass", (0.8, 0.6, 0.25), metal=1.0, rough=0.3)
    parts = [
        bx((0.05, 0.21, 0.068), (0, -0.04, 0.062), g, bev=0.008),  # receiver
        bx((0.052, 0.07, 0.008), (0.0, -0.05, 0.098), blk, bev=0.0),
        tube(0.0155, 0.5, -0.145, 0.082, g, verts=20),  # barrel
        tube(0.0135, 0.4, -0.145, 0.046, blk, verts=16),  # tube mag
        bx((0.034, 0.02, 0.06), (0, -0.53, 0.064), g, bev=0.004),  # barrel clamp
        bx((0.052, 0.16, 0.048), (0, -0.29, 0.044), wood, bev=0.012),  # pump
        bx((0.04, 0.055, 0.12), (0, 0.065, -0.02), wood, rot=(math.radians(-22), 0, 0), bev=0.01),  # grip
        bx((0.046, 0.27, 0.07), (0, 0.22, 0.03), wood, rot=(math.radians(6), 0, 0), bev=0.018),  # stock
        bx((0.05, 0.025, 0.125), (0, 0.36, 0.01), blk, bev=0.008),  # recoil pad
        ring(0.022, 0.0035, (0, -0.0, 0.01), g, rot=(0, math.radians(90), 0)),
        bx((0.006, 0.012, 0.022), (0, 0.0, 0.012), blk, rot=(math.radians(12), 0, 0), bev=0.0),
        sphere(0.003, (0, -0.64, 0.1), brass, seg=8),  # bead
    ]
    for i in range(7):  # pump grooves
        parts.append(box((0.054, 0.006, 0.006), (0, -0.23 - i * 0.02, 0.06), blk))
    parts.append(bx((0.006, 0.1, 0.04), (-0.028, -0.04, 0.066), blk, bev=0.002))  # side saddle
    for i in range(4):
        parts.append(cyl(0.0095, 0.045, (-0.04, -0.075 + i * 0.024, 0.07), shell, verts=12))
        parts.append(cyl(0.0098, 0.01, (-0.04, -0.075 + i * 0.024, 0.093), brass, verts=12))
    _wpn_finish("wpn_shotgun_breaker", parts, (0, -0.65, 0.082), (0, -0.29, 0.03))


# ---------------------------------------------------------------- buildings
# Footprint centred on origin, ground at z=0, front faces -Y.


def window(parts, p, x, y, z, face, w=1.2, h=1.4, glass=None, frame=None, sill=True, depth=0.12):
    """Recessed window on a wall whose outward normal is (0, face, 0)."""
    frame = frame or p["trim"]
    glass = glass or p["glass"]
    parts.append(box((w, 0.05, h), (x, y - face * (0.04 - depth), z), glass))
    parts.append(box((w + 0.12, 0.08, 0.06), (x, y + face * 0.02, z + h / 2 + 0.03), frame))  # head
    parts.append(box((0.06, 0.08, h), (x - w / 2 - 0.03, y + face * 0.02, z), frame))
    parts.append(box((0.06, 0.08, h), (x + w / 2 + 0.03, y + face * 0.02, z), frame))
    parts.append(box((0.04, 0.06, h), (x, y - face * (0.02 - depth), z), frame))  # mullion
    if sill:
        parts.append(box((w + 0.25, 0.18, 0.07), (x, y + face * 0.08, z - h / 2 - 0.035), p["concrete"]))


def side_window(parts, p, x, y, z, face, w=1.2, h=1.4):
    """Window on a wall whose outward normal is (face, 0, 0)."""
    parts.append(box((0.05, w, h), (x - face * (0.04 - 0.12), y, z), p["glass"]))
    parts.append(box((0.08, w + 0.12, 0.06), (x + face * 0.02, y, z + h / 2 + 0.03), p["trim"]))
    parts.append(box((0.18, w + 0.25, 0.07), (x + face * 0.08, y, z - h / 2 - 0.035), p["concrete"]))


def ac_unit(parts, p, loc, rot=0.0):
    parts.append(box((0.8, 0.5, 0.55), loc, p["trim"], rot=(0, 0, rot), bevel=0.02))
    parts.append(cyl(0.18, 0.02, (loc[0], loc[1] - 0.26, loc[2]), p["gunmetal"], rot=R90X, verts=16))


def bldg_apartment():
    """4-storey brick walk-up: recessed windows, fire escape, rooftop parapet (sniper perch)."""
    reset(); p = P()
    W, D, F, H = 12.0, 10.0, 4, 3.2
    band = mat("brick_band", (0.32, 0.12, 0.08), rough=0.9)
    parts = [box((W, D, F * H), (0, 0, F * H / 2), p["brick"])]
    parts.append(box((W + 0.3, D + 0.3, 0.6), (0, 0, 0.3), p["concrete"]))  # plinth
    for f in range(1, F):
        parts.append(box((W + 0.12, D + 0.12, 0.18), (0, 0, f * H), band))  # floor bands
    parts.append(box((W + 0.5, D + 0.5, 0.35), (0, 0, F * H + 0.05), p["concrete"]))  # cornice
    for sy, ly in ((-1, W), (1, W)):
        parts.append(box((W + 0.5, 0.3, 1.1), (0, sy * (D / 2 + 0.1), F * H + 0.75), p["concrete"]))  # parapet
    for sx in (-1, 1):
        parts.append(box((0.3, D + 0.5, 1.1), (sx * (W / 2 + 0.1), 0, F * H + 0.75), p["concrete"]))
    for f in range(F):
        z = f * H + 1.7
        for i in range(4):
            x = -W / 2 + (i + 0.5) * W / 4
            if not (f == 0 and abs(x) < 2):
                window(parts, p, x, -D / 2, z, -1)
            window(parts, p, x, D / 2, z, 1)
        for i in range(3):
            y = -D / 2 + (i + 0.5) * D / 3
            for sx in (-1, 1):
                side_window(parts, p, sx * W / 2, y, z, sx)
    # entrance: frame, door, steps, canopy, lamp
    parts += [box((2.2, 0.25, 3.0), (0, -D / 2 - 0.05, 1.5), p["concrete"]),
              box((1.5, 0.12, 2.4), (0, -D / 2 - 0.12, 1.3), p["wood"], bevel=0.01),
              box((0.04, 0.06, 0.25), (0.55, -D / 2 - 0.2, 1.3), p["steel"]),
              box((2.6, 1.6, 0.12), (0, -D / 2 - 0.8, 3.05), p["concrete"]),
              box((2.4, 0.6, 0.18), (0, -D / 2 - 0.4, 0.09), p["concrete"]),
              box((2.4, 0.3, 0.18), (0, -D / 2 - 0.85, 0.0), p["concrete"]),
              box((0.15, 0.15, 0.2), (1.2, -D / 2 - 0.15, 2.6), mat("lamp", (1, 0.9, 0.6), emit=(1, 0.8, 0.5)))]
    # fire escape on the east side (+X): landings, railings, ladders → vertical combat route
    fe = mat("fire_escape", (0.08, 0.08, 0.09), metal=0.6, rough=0.5)
    for f in range(1, F):
        z = f * H
        parts.append(box((1.2, 4.0, 0.06), (W / 2 + 0.6, 0, z), fe))
        parts.append(box((0.04, 4.0, 0.04), (W / 2 + 1.18, 0, z + 1.0), fe))
        for k in range(9):
            parts.append(box((0.025, 0.025, 1.0), (W / 2 + 1.18, -2.0 + k * 0.5, z + 0.5), fe))
        # ladder to next landing
        lx = W / 2 + 0.9
        for sidey in (1.3, 1.7):
            parts.append(box((0.04, 0.04, H), (lx, sidey, z - H / 2 + 0.05), fe))
        for r in range(10):
            parts.append(box((0.03, 0.4, 0.03), (lx, 1.5, z - H + 0.3 + r * 0.3), fe))
    # roof clutter: access hut, AC units, water tank, vents
    parts.append(box((2.2, 2.2, 2.4), (W / 2 - 1.8, D / 2 - 1.8, F * H + 1.4), p["brick"]))
    parts.append(box((1.0, 0.1, 2.0), (W / 2 - 1.8, D / 2 - 2.95, F * H + 1.2), p["gunmetal"]))
    for x in (-3.5, -1.5, 1.0):
        ac_unit(parts, p, (x, -2.0, F * H + 0.5))
    parts.append(cyl(1.1, 2.4, (-3.5, 2.5, F * H + 2.3), p["wood"], verts=20))
    parts.append(cyl(1.15, 0.6, (-3.5, 2.5, F * H + 3.6), p["gunmetal"], verts=20, r2=0.1))
    for lx, ly in ((-4.5, 1.5), (-2.5, 1.5), (-4.5, 3.5), (-2.5, 3.5)):
        parts.append(box((0.12, 0.12, 1.2), (lx, ly, F * H + 0.6), p["gunmetal"]))
    for x in (0.5, 2.5):
        parts.append(cyl(0.15, 0.6, (x, 2.0, F * H + 0.3), p["steel"], verts=12))
    # drainpipe
    parts.append(cyl(0.07, F * H, (-W / 2 - 0.1, -D / 2 - 0.1, F * H / 2), p["gunmetal"], verts=8))
    join(parts, "bldg_apartment_block-col")


def bldg_warehouse():
    """Dockside warehouse: hollow, roller doors, loading dock, catwalk, skylights."""
    reset(); p = P()
    W, D, H, T = 22.0, 14.0, 8.0, 0.3
    metal = mat("corrugated_teal", (0.18, 0.32, 0.33), metal=0.6, rough=0.5)
    roofm = mat("roof_grey", (0.35, 0.36, 0.37), metal=0.5, rough=0.6)
    slat = mat("roller_slat", (0.55, 0.56, 0.55), metal=0.7, rough=0.4)
    hazard = mat("hazard_yellow", (0.9, 0.72, 0.05), rough=0.6)
    parts = [box((W, D, 0.2), (0, 0, 0.1), p["concrete"])]
    parts.append(box((W, T, H), (0, D / 2, H / 2), metal))
    for sx in (-1, 1):
        parts.append(box((T, D, H), (sx * W / 2, 0, H / 2), metal))
    door_w, door_h = 5.0, 5.0
    xs = [-W / 2, -7.5 - door_w / 2, -7.5 + door_w / 2, 7.5 - door_w / 2, 7.5 + door_w / 2, W / 2]
    for a, c in ((xs[0], xs[1]), (xs[2], xs[3]), (xs[4], xs[5])):
        parts.append(box((c - a, T, H), ((a + c) / 2, -D / 2, H / 2), metal))
    for x in (-7.5, 7.5):
        parts.append(box((door_w, T, H - door_h), (x, -D / 2, door_h + (H - door_h) / 2), metal))
        parts.append(box((door_w + 0.4, 0.6, 0.6), (x, -D / 2 - 0.25, door_h + 0.3), p["gunmetal"], bevel=0.05))
        for sx in (-1, 1):  # door jamb with hazard stripes
            parts.append(box((0.25, 0.4, door_h), (x + sx * (door_w / 2 + 0.12), -D / 2 - 0.1, door_h / 2), hazard))
    # right door half-open roller (slats) → cover + peeking spot
    for k in range(10):
        parts.append(box((door_w, 0.08, 0.2), (7.5, -D / 2 - 0.05, door_h - 0.12 - k * 0.22), slat, bevel=0.01))
    # corrugation ribs on all outer walls
    for i in range(24):
        x = -W / 2 + (i + 0.5) * W / 24
        if not any(abs(x - dx) < door_w / 2 + 0.3 for dx in (-7.5, 7.5)):
            parts.append(box((0.08, 0.06, H), (x, -D / 2 - 0.18, H / 2), metal))
        parts.append(box((0.08, 0.06, H), (x, D / 2 + 0.18, H / 2), metal))
    for j in range(14):
        y = -D / 2 + (j + 0.5) * D / 14
        for sx in (-1, 1):
            parts.append(box((0.06, 0.08, H), (sx * (W / 2 + 0.18), y, H / 2), metal))
    # pitched roof with skylights, gutters, downpipes
    pitch = math.radians(8)
    for sx in (-1, 1):
        parts.append(box((W / 2 + 0.7, D + 0.8, 0.15), (sx * W / 4, 0, H + 0.75), roofm, rot=(0, sx * pitch, 0)))
        for k in range(3):
            parts.append(box((1.4, 1.0, 0.1), (sx * W / 4, -D / 2 + 3 + k * 4, H + 0.86), p["glass"], rot=(0, sx * pitch, 0)))
    for sy in (-1, 1):
        parts.append(box((W + 0.8, 0.2, 0.2), (0, sy * (D / 2 + 0.45), H + 0.05), p["gunmetal"]))
        for sx in (-1, 1):
            parts.append(cyl(0.08, H, (sx * (W / 2 + 0.3), sy * (D / 2 + 0.45), H / 2), p["gunmetal"], verts=8))
    # loading dock in front of left door
    parts.append(box((7.0, 2.5, 1.2), (-7.5, -D / 2 - 1.25, 0.6), p["concrete"]))
    parts.append(box((7.0, 0.15, 0.15), (-7.5, -D / 2 - 2.5, 1.12), hazard))
    for x in (-10.5, -4.5):
        parts.append(box((0.3, 0.25, 0.5), (x, -D / 2 - 2.6, 0.7), p["black"]))  # bumpers
    # interior: catwalk + stairs + columns + office box
    parts.append(box((W - 1.0, 2.0, 0.15), (0, D / 2 - 1.2, 4.0), p["gunmetal"]))
    for k in range(22):
        parts.append(box((0.04, 0.04, 1.0), (-W / 2 + 1 + k, D / 2 - 2.2, 4.5), p["gunmetal"]))
    parts.append(box((W - 1.0, 0.05, 0.05), (0, D / 2 - 2.2, 5.0), p["gunmetal"]))
    for i in range(10):
        parts.append(box((1.2, 0.35, 0.06), (W / 2 - 1.2, D / 2 - 2.6 - i * 0.35, 4.0 - (i + 1) * 0.4), p["gunmetal"]))
    for x in (-W / 3, 0, W / 3):
        parts.append(box((0.35, 0.35, H), (x, 0, H / 2), p["gunmetal"]))
        parts.append(box((0.4, 0.4, 1.2), (x, 0, 0.6), hazard))
    parts.append(box((4.0, 3.0, 3.0), (-W / 2 + 2.3, D / 2 - 4.0, 1.6), mat("office_white", (0.8, 0.8, 0.78))))
    parts.append(box((2.0, 0.06, 1.0), (-W / 2 + 2.3, D / 2 - 5.52, 2.0), p["glass"]))
    for x in (-6, 0, 6):  # wall lights
        parts.append(box((0.4, 0.2, 0.15), (x, -D / 2 - 0.3, H - 0.6), mat("lamp", (1, 0.9, 0.6), emit=(1, 0.8, 0.5))))
    parts.append(box((1.0, 0.1, 2.1), (W / 2 - 2.0, -D / 2 - 0.1, 1.05), mat("door_red", (0.6, 0.1, 0.08))))
    join(parts, "bldg_warehouse-col")


def bldg_shop():
    """Corner convenience store: glass front, striped awning, stocked shelves, back room."""
    reset(); p = P()
    W, D, H, T = 9.0, 8.0, 4.0, 0.25
    stucco = mat("stucco_cream", (0.85, 0.80, 0.68), rough=0.9)
    green = mat("awning_green", (0.08, 0.38, 0.2), rough=0.8)
    white = mat("awning_white", (0.9, 0.9, 0.86), rough=0.8)
    sign = mat("sign_glow", (0.95, 0.85, 0.3), emit=(0.9, 0.7, 0.2))
    goods = [mat(f"goods_{i}", c, rough=0.6) for i, c in enumerate(((0.8, 0.2, 0.1), (0.1, 0.4, 0.8), (0.9, 0.7, 0.1), (0.2, 0.6, 0.3), (0.9, 0.9, 0.9)))]
    parts = [box((W, D, 0.15), (0, 0, 0.075), mat("tile", (0.75, 0.75, 0.72), rough=0.4))]
    parts.append(box((W, T, H), (0, D / 2, H / 2), stucco))
    for sx in (-1, 1):
        parts.append(box((T, D, H), (sx * W / 2, 0, H / 2), stucco))
        parts.append(box((T + 0.1, 0.5, H + 0.4), (sx * W / 2, -D / 2, (H + 0.4) / 2), p["brick"]))  # pilasters
    parts.append(box((W + 0.3, D + 0.3, 0.25), (0, 0, H + 0.12), p["concrete"]))
    parts.append(box((W + 0.4, 0.3, 0.6), (0, -D / 2 - 0.05, H + 0.3), p["concrete"]))  # parapet front
    parts.append(box((W - 2.0, T, 0.8), (-1.0, -D / 2, 0.4), stucco))
    parts.append(box((W - 2.0, 0.06, 2.0), (-1.0, -D / 2, 1.8), p["glass"]))
    for k in range(4):  # storefront mullions
        parts.append(box((0.08, 0.1, 2.0), (-W / 2 + 0.5 + k * (W - 2.0) / 3, -D / 2, 1.8), p["gunmetal"]))
    parts.append(box((W, T, 1.2), (0, -D / 2, H - 0.6), stucco))
    parts.append(box((1.8, 0.06, 2.8), (W / 2 - 1.0, -D / 2, 1.4), p["glass"]))
    parts.append(box((0.06, 0.08, 0.6), (W / 2 - 1.8, -D / 2 - 0.08, 1.2), p["steel"]))  # door handle
    for k in range(10):  # striped awning
        x = -W / 2 + 0.2 + (k + 0.5) * (W - 0.4) / 10
        parts.append(box(((W - 0.4) / 10, 1.6, 0.06), (x, -D / 2 - 0.8, H - 0.9), green if k % 2 else white, rot=(math.radians(-15), 0, 0)))
    parts.append(box((W - 0.4, 0.05, 0.3), (0, -D / 2 - 1.58, H - 1.25), green))
    parts.append(box((W * 0.7, 0.15, 0.6), (0, -D / 2 - 0.12, H - 0.25), sign))
    parts.append(box((W * 0.72, 0.1, 0.66), (0, -D / 2 - 0.05, H - 0.25), p["black"]))
    # interior: counter + register, shelves with goods, fridges, back room
    parts.append(box((3.0, 0.8, 1.05), (-2.2, 0.2, 0.6), p["wood"], bevel=0.02))
    parts.append(box((0.4, 0.35, 0.25), (-2.8, 0.2, 1.25), p["black"], bevel=0.02))
    for i in range(3):
        x = 0.4 + i * 1.3
        parts.append(box((0.5, 3.0, 1.8), (x, 0.6, 0.98), p["gunmetal"]))
        for lvl in range(3):
            for k in range(6):
                parts.append(box((0.12, 0.35, 0.2), (x + (0.2 if (k + lvl) % 2 else -0.2), -0.6 + k * 0.5, 0.45 + lvl * 0.55),
                                 goods[(i + k + lvl) % len(goods)]))
    for k in range(3):
        parts.append(box((1.0, 0.7, 2.1), (-3.6 + k * 1.05, D / 2 - 2.5, 1.2), p["trim"], bevel=0.02))
        parts.append(box((0.85, 0.04, 1.6), (-3.6 + k * 1.05, D / 2 - 2.86, 1.3), p["glass"]))
    parts.append(box((W - 2.5, 0.15, H), (-1.25, D / 2 - 2.0 + 0.85, H / 2), stucco))
    ac_unit(parts, p, (2.0, 1.0, H + 0.55))
    join(parts, "bldg_corner_shop-col")


def bldg_garage():
    """3-level parking garage: columns + low walls = cover-heavy arena, ramps, barrier, lights."""
    reset(); p = P()
    W, D, L, H = 30.0, 20.0, 3, 3.0
    stripe = mat("stripe_yellow", (0.9, 0.75, 0.1), rough=0.6)
    white = mat("paint_white", (0.9, 0.9, 0.88), rough=0.6)
    lamp = mat("tube_light", (0.95, 0.97, 1.0), emit=(0.9, 0.95, 1.0))
    parts = []
    for lvl in range(L + 1):
        z = lvl * H
        if lvl == 0:
            parts.append(box((W, D, 0.2), (0, 0, 0.1), p["asphalt"]))
        else:
            parts.append(box((W - 6.0, D, 0.3), (-3.0, 0, z), p["concrete"]))
            parts.append(box((6.0, D / 2, 0.3), (W / 2 - 3.0, D / 4, z), p["concrete"]))
            for sy in (-1, 1):
                parts.append(box((W, 0.25, 1.0), (0, sy * D / 2, z + 0.65), p["concrete"], bevel=0.03))
                parts.append(box((W, 0.3, 0.1), (0, sy * D / 2, z + 1.2), p["concrete"]))
            for sx in (-1, 1):
                parts.append(box((0.25, D, 1.0), (sx * W / 2, 0, z + 0.65), p["concrete"], bevel=0.03))
        top = z + (0.21 if lvl == 0 else 0.16)
        for i in range(8):  # bay lines both sides + wheel stops
            for sy in (-1, 1):
                x = -W / 2 + 2.5 + i * 2.7
                if x > W / 2 - 6.5:
                    continue
                parts.append(box((0.1, 4.8, 0.02), (x, sy * (D / 2 - 2.6), top), white))
                parts.append(box((1.6, 0.2, 0.12), (x + 1.35, sy * (D / 2 - 0.8), top + 0.06), p["concrete"]))
        if lvl < L:
            for ix in range(5):
                for iy in range(3):
                    x = -W / 2 + 1.0 + ix * (W - 2.0) / 4
                    y = -D / 2 + 1.0 + iy * (D - 2.0) / 2
                    parts.append(box((0.6, 0.6, H), (x, y, z + H / 2), p["concrete"], bevel=0.04))
                    parts.append(box((0.64, 0.64, 0.5), (x, y, z + 0.45), stripe))
                    if iy == 1:
                        parts.append(box((1.2, 0.12, 0.06), (x + 2.5, y, z + H - 0.2), lamp))
            rl = math.hypot(D / 2, H)
            ang = math.atan2(H, D / 2)
            parts.append(box((5.5, rl, 0.25), (W / 2 - 3.0, -D / 4, z + H / 2), p["concrete"], rot=(-ang, 0, 0)))
            for sx in (-1, 1):
                parts.append(box((0.2, rl, 0.9), (W / 2 - 3.0 + sx * 2.85, -D / 4, z + H / 2 + 0.5), p["concrete"], rot=(-ang, 0, 0)))
            for k in range(6):
                parts.append(box((5.0, 0.15, 0.02), (W / 2 - 3.0, -D / 2 + 1 + k * 1.6, z + (k * 1.6 + 1) / (D / 2) * H + 0.14), stripe, rot=(-ang, 0, 0)))
    # stair core + entry barrier booth
    parts.append(box((3.0, 3.0, H * L + 1.5), (-W / 2 + 1.5, -D / 2 + 1.5, (H * L + 1.5) / 2), mat("stair_core", (0.35, 0.36, 0.38))))
    for lvl in range(L + 1):
        parts.append(box((1.0, 0.1, 2.1), (-W / 2 + 1.5, -D / 2 + 3.02, lvl * H + 1.1), mat("door_green", (0.1, 0.35, 0.2))))
    parts.append(box((1.2, 1.2, 2.4), (W / 2 - 7.0, -D / 2 - 1.5, 1.2), p["trim"], bevel=0.03))
    parts.append(box((1.6, 1.6, 0.15), (W / 2 - 7.0, -D / 2 - 1.5, 2.45), p["gunmetal"]))
    parts.append(box((0.3, 0.3, 1.1), (W / 2 - 5.8, -D / 2 - 1.5, 0.55), p["black"]))
    for k in range(8):  # striped barrier arm
        parts.append(box((0.5, 0.08, 0.08), (W / 2 - 5.5 + k * 0.5, -D / 2 - 1.5, 1.0), stripe if k % 2 else white))
    join(parts, "bldg_parking_garage-col")


# ---------------------------------------------------------------- main

GROUPS = {
    "characters": [(rel, (lambda f: (lambda: (reset(), f())))(fn)) for rel, fn in characters.ROSTER],
    "weapons": [
        ("weapons/wpn_pistol_kestrel.glb", wpn_pistol),
        ("weapons/wpn_smg_wasp.glb", wpn_smg),
        ("weapons/wpn_rifle_halberd.glb", wpn_rifle),
        ("weapons/wpn_shotgun_breaker.glb", wpn_shotgun),
    ],
    "buildings": [
        ("buildings/bldg_apartment_block.glb", bldg_apartment),
        ("buildings/bldg_warehouse.glb", bldg_warehouse),
        ("buildings/bldg_corner_shop.glb", bldg_shop),
        ("buildings/bldg_parking_garage.glb", bldg_garage),
    ],
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="game/assets")
    ap.add_argument("--only", default="characters,weapons,buildings")
    a = ap.parse_args(argv)
    for g in a.only.split(","):
        root = a.out
        for rel, fn in GROUPS[g]:
            fn()
            export(os.path.abspath(os.path.join(root, rel)))


if __name__ == "__main__":
    main()
