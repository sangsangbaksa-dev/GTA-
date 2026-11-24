"""Procedural asset generator for Project Harbor (original low-poly designs).

Run headless:
    blender -b --factory-startup -P tools/blender/build_assets.py -- \
        --out game/assets --props-out ../set/props [--only characters,weapons]

Outputs .glb files (glTF 2.0). Conventions (see game/assets/README.md):
  * 1 unit = 1 m, Blender Z-up / -Y forward (exporter converts to glTF Y-up).
  * Characters: one skinned mesh + armature "rig", T-pose, feet at origin.
  * Weapons: origin at grip, barrel along -Y, empties "muzzle" and "grip_off".
  * Buildings / props: mesh name ends with "-col" so Godot generates trimesh collision.
"""
import argparse
import math
import os
import sys

import bpy
from mathutils import Vector

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
        mod.segments = 1
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

SKIN = [(0.80, 0.62, 0.50), (0.55, 0.38, 0.27), (0.36, 0.24, 0.17), (0.92, 0.76, 0.64)]


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


# ---------------------------------------------------------------- characters

BONES = {
    # name: (head, tail, parent)
    "hips": ((0, 0, 0.95), (0, 0, 1.08), None),
    "spine": ((0, 0, 1.08), (0, 0, 1.25), "hips"),
    "chest": ((0, 0, 1.25), (0, 0, 1.45), "spine"),
    "neck": ((0, 0, 1.45), (0, 0, 1.55), "chest"),
    "head": ((0, 0, 1.55), (0, 0, 1.80), "neck"),
    "upper_arm.L": ((0.20, 0, 1.42), (0.47, 0, 1.42), "chest"),
    "forearm.L": ((0.47, 0, 1.42), (0.72, 0, 1.42), "upper_arm.L"),
    "hand.L": ((0.72, 0, 1.42), (0.82, 0, 1.42), "forearm.L"),
    "thigh.L": ((0.10, 0, 0.95), (0.10, 0, 0.52), "hips"),
    "shin.L": ((0.10, 0, 0.52), (0.10, 0, 0.09), "thigh.L"),
    "foot.L": ((0.10, 0, 0.09), (0.10, -0.15, 0.02), "shin.L"),
}
for _n in list(BONES):
    if _n.endswith(".L"):
        h, t, p = BONES[_n]
        BONES[_n[:-2] + ".R"] = ((-h[0], h[1], h[2]), (-t[0], t[1], t[2]), p.replace(".L", ".R") if p.endswith(".L") else p)


def bone_pt(name, which, s):
    return Vector(BONES[name][0 if which == "h" else 1]) * s


class Body:
    """Collects parts bound rigidly to bones, then builds a skinned mesh."""

    def __init__(self, height=1.80):
        self.s = height / 1.80
        self.parts = []

    def add(self, obj, bone):
        vg = obj.vertex_groups.new(name=bone)
        vg.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
        self.parts.append(obj)
        return obj

    def pt(self, bone, which="h"):
        return bone_pt(bone, which, self.s)

    def build(self, name):
        s = self.s
        arm_data = bpy.data.armatures.new("rig")
        rig = bpy.data.objects.new("rig", arm_data)
        bpy.context.scene.collection.objects.link(rig)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode="EDIT")
        eb = {}
        for bn in BONES:  # dict order keeps parents first
            h, t, p = BONES[bn]
            b = arm_data.edit_bones.new(bn)
            b.head = Vector(h) * s
            b.tail = Vector(t) * s
            if p:
                b.parent = eb[p]
                b.use_connect = (Vector(BONES[p][1]) - Vector(h)).length < 1e-4
            eb[bn] = b
        bpy.ops.object.mode_set(mode="OBJECT")
        mesh = join(self.parts, name)
        mesh.parent = rig
        mod = mesh.modifiers.new("rig", "ARMATURE")
        mod.object = rig
        rig.name = f"{name}_rig"
        return rig, mesh


def base_human(b, skin, top, pants, shoes, bulk=1.0, sleeves=True, sleeve_mat=None):
    s = b.s
    w = bulk
    # torso
    b.add(box((0.34 * w * s, 0.20 * w * s, 0.17 * s), b.pt("hips") + Vector((0, 0, 0.06 * s)), pants, bevel=0.03), "hips")
    b.add(box((0.32 * w * s, 0.19 * w * s, 0.20 * s), b.pt("spine") + Vector((0, 0, 0.09 * s)), top, bevel=0.03), "spine")
    b.add(box((0.40 * w * s, 0.23 * w * s, 0.22 * s), b.pt("chest") + Vector((0, 0, 0.10 * s)), top, bevel=0.04), "chest")
    b.add(limb(b.pt("neck"), b.pt("neck", "t"), 0.055 * s, skin), "neck")
    b.add(sphere(0.115 * s, b.pt("head") + Vector((0, 0, 0.11 * s)), skin, scale=(0.9, 1.0, 1.12)), "head")
    # nose (gives facing direction)
    b.add(box((0.03 * s, 0.04 * s, 0.05 * s), b.pt("head") + Vector((0, -0.11 * s, 0.10 * s)), skin), "head")
    for side in ("L", "R"):
        arm_m = (sleeve_mat or top) if sleeves else skin
        b.add(limb(b.pt(f"upper_arm.{side}"), b.pt(f"upper_arm.{side}", "t"), 0.062 * w * s, arm_m, 0.052 * w * s), f"upper_arm.{side}")
        b.add(limb(b.pt(f"forearm.{side}"), b.pt(f"forearm.{side}", "t"), 0.050 * w * s, arm_m if sleeves else skin, 0.042 * w * s), f"forearm.{side}")
        b.add(box((0.10 * s, 0.05 * s, 0.09 * s), (b.pt(f"hand.{side}") + b.pt(f"hand.{side}", "t")) / 2, skin, bevel=0.01), f"hand.{side}")
        b.add(limb(b.pt(f"thigh.{side}"), b.pt(f"thigh.{side}", "t"), 0.085 * w * s, pants, 0.065 * w * s), f"thigh.{side}")
        b.add(limb(b.pt(f"shin.{side}"), b.pt(f"shin.{side}", "t"), 0.062 * w * s, pants, 0.050 * s), f"shin.{side}")
        f = b.pt(f"foot.{side}")
        b.add(box((0.11 * s, 0.26 * s, 0.09 * s), (f.x, -0.05 * s, 0.045 * s), shoes, bevel=0.02), f"foot.{side}")


def char_player():
    """Rook — the protagonist: ex-courier, charcoal bomber jacket, teal scarf."""
    reset(); p = P()
    jacket = mat("jacket_charcoal", (0.10, 0.11, 0.12), rough=0.5)
    cargo = mat("cargo_khaki", (0.30, 0.27, 0.20), rough=0.85)
    hair = mat("hair_dark", (0.05, 0.04, 0.03), rough=0.9)
    b = Body(1.80)
    base_human(b, mat("skin", SKIN[0]), jacket, cargo, p["boots"])
    s = b.s
    b.add(cyl(0.085 * s, 0.07 * s, b.pt("neck") + Vector((0, 0, 0.03 * s)), p["teal"], verts=12), "neck")  # scarf
    b.add(sphere(0.12 * s, b.pt("head") + Vector((0, 0.01 * s, 0.17 * s)), hair, scale=(0.95, 1.0, 0.6)), "head")
    b.add(box((0.30 * s, 0.03 * s, 0.30 * s), b.pt("chest") + Vector((0, 0.13 * s, 0.05 * s)), mat("pack", (0.18, 0.16, 0.12)), bevel=0.03), "chest")  # sling pack
    b.add(box((0.36 * s, 0.22 * s, 0.05 * s), b.pt("hips") + Vector((0, 0, 0.15 * s)), p["black"]), "hips")  # belt
    b.add(box((0.06 * s, 0.10 * s, 0.14 * s), b.pt("thigh.R") + Vector((-0.09 * s, 0, -0.12 * s)), p["black"], bevel=0.01), "thigh.R")  # holster
    for side in ("L", "R"):
        b.add(box((0.12 * s, 0.07 * s, 0.07 * s), b.pt(f"hand.{side}") + Vector((0.03 * (1 if side == 'L' else -1) * s, 0, 0)), p["black"]), f"hand.{side}")
    b.build("chr_player_rook")


def char_brawler():
    """Saltline Crew brawler — heavy dockside muscle, sleeveless, orange beanie."""
    reset(); p = P()
    tank = mat("tank_grey", (0.45, 0.45, 0.42), rough=0.9)
    jeans = mat("jeans_dark", (0.10, 0.13, 0.22), rough=0.85)
    orange = mat("beanie_orange", (0.85, 0.35, 0.05), rough=0.9)
    b = Body(1.88)
    base_human(b, mat("skin", SKIN[1]), tank, jeans, p["boots"], bulk=1.25, sleeves=False)
    s = b.s
    b.add(sphere(0.12 * s, b.pt("head") + Vector((0, 0, 0.17 * s)), orange, scale=(1.0, 1.05, 0.65)), "head")
    b.add(box((0.14 * s, 0.03 * s, 0.05 * s), b.pt("head") + Vector((0, -0.105 * s, 0.05 * s)), mat("beard", (0.08, 0.05, 0.03))), "head")
    b.add(box((0.50 * s, 0.28 * s, 0.06 * s), b.pt("chest") + Vector((0, 0, 0.21 * s)), mat("vest_hi", (0.9, 0.75, 0.1)), bevel=0.02), "chest")
    for side in ("L", "R"):  # knuckle wraps
        b.add(box((0.115 * s, 0.065 * s, 0.10 * s), b.pt(f"hand.{side}") + Vector((0.04 * (1 if side == 'L' else -1) * s, 0, 0)), mat("wrap", (0.85, 0.82, 0.75))), f"hand.{side}")
    b.build("chr_enemy_brawler")


def char_gunman():
    """Velvet Hand syndicate gunman — slim maroon suit, dark glasses."""
    reset(); p = P()
    suit = mat("suit_maroon", (0.30, 0.04, 0.07), rough=0.45)
    shirt = mat("shirt_black", (0.04, 0.04, 0.05), rough=0.6)
    b = Body(1.78)
    base_human(b, mat("skin", SKIN[3]), suit, suit, mat("shoes_black", (0.02, 0.02, 0.02), rough=0.2), bulk=0.95)
    s = b.s
    b.add(box((0.12 * s, 0.02 * s, 0.20 * s), b.pt("chest") + Vector((0, -0.115 * s, 0.10 * s)), shirt), "chest")  # shirt front
    b.add(box((0.17 * s, 0.03 * s, 0.035 * s), b.pt("head") + Vector((0, -0.10 * s, 0.13 * s)), p["black"]), "head")  # glasses
    b.add(sphere(0.12 * s, b.pt("head") + Vector((0, 0.015 * s, 0.17 * s)), mat("hair_slick", (0.02, 0.02, 0.02), rough=0.25), scale=(0.95, 1.05, 0.55)), "head")
    b.add(box((0.05 * s, 0.02 * s, 0.03 * s), b.pt("chest") + Vector((0.1 * s, -0.12 * s, 0.17 * s)), mat("pin_gold", (0.9, 0.7, 0.2), metal=1.0, rough=0.3)), "chest")  # lapel pin
    b.build("chr_enemy_gunman")


def char_enforcer():
    """Ironclad Security riot enforcer — armored PMC, visor helmet, arm shield."""
    reset(); p = P()
    olive = mat("armor_olive", (0.16, 0.18, 0.12), rough=0.7)
    plate = mat("plate_dark", (0.08, 0.09, 0.08), metal=0.3, rough=0.5)
    visor = mat("visor", (0.6, 0.15, 0.05), metal=0.5, rough=0.1, emit=(0.4, 0.08, 0.02))
    b = Body(1.86)
    base_human(b, mat("skin", SKIN[2]), olive, olive, p["boots"], bulk=1.15)
    s = b.s
    b.add(box((0.48 * s, 0.30 * s, 0.38 * s), b.pt("chest") + Vector((0, 0, 0.04 * s)), plate, bevel=0.04), "chest")  # vest
    b.add(sphere(0.145 * s, b.pt("head") + Vector((0, 0, 0.13 * s)), plate, scale=(1, 1.05, 1)), "head")  # helmet
    b.add(box((0.20 * s, 0.05 * s, 0.06 * s), b.pt("head") + Vector((0, -0.13 * s, 0.12 * s)), visor), "head")
    for side in ("L", "R"):
        sx = 1 if side == "L" else -1
        b.add(sphere(0.09 * s, b.pt(f"upper_arm.{side}") + Vector((0.04 * sx * s, 0, 0.02 * s)), plate, scale=(1.1, 1, 0.8)), f"upper_arm.{side}")
        b.add(box((0.12 * s, 0.10 * s, 0.14 * s), (b.pt(f"thigh.{side}", "t") + Vector((0, -0.05 * s, 0))), plate, bevel=0.02), f"shin.{side}")  # knee pad
    # riot shield on left forearm, facing forward
    sh = box((0.55 * s, 0.04 * s, 0.85 * s), b.pt("forearm.L") + Vector((0.12 * s, -0.12 * s, -0.12 * s)), mat("shield", (0.12, 0.14, 0.16), metal=0.4, rough=0.4), bevel=0.02)
    b.add(sh, "forearm.L")
    b.add(box((0.35 * s, 0.045 * s, 0.10 * s), b.pt("forearm.L") + Vector((0.12 * s, -0.125 * s, 0.12 * s)), mat("shield_window", (0.3, 0.4, 0.45), rough=0.1)), "forearm.L")
    b.build("chr_enemy_enforcer")


def char_civilian():
    """Port Calloway civilian — hoodie and jeans (generic NPC base)."""
    reset(); p = P()
    b = Body(1.72)
    base_human(b, mat("skin", SKIN[1]), mat("hoodie_blue", (0.15, 0.30, 0.55)), mat("jeans", (0.18, 0.22, 0.35)), mat("sneakers", (0.9, 0.9, 0.9)), bulk=0.95)
    s = b.s
    b.add(sphere(0.125 * s, b.pt("head") + Vector((0, 0.03 * s, 0.13 * s)), mat("hood", (0.12, 0.26, 0.48)), scale=(1, 1.05, 0.9)), "head")
    b.build("chr_npc_civilian")


# ---------------------------------------------------------------- weapons
# Origin = grip centre. Barrel points -Y. Empties: muzzle, grip_off (support hand).


def _wpn_finish(name, parts, muzzle, support=None):
    o = join(parts, name)
    empty("muzzle", muzzle, o)
    if support:
        empty("grip_off", support, o)
    return o


def wpn_pistol():
    """Kestrel P10 — compact polymer-frame pistol."""
    reset(); p = P()
    parts = [
        box((0.030, 0.19, 0.035), (0, -0.05, 0.075), p["gunmetal"], bevel=0.004),  # slide
        box((0.028, 0.16, 0.025), (0, -0.035, 0.045), p["polymer"], bevel=0.003),  # frame
        box((0.028, 0.045, 0.11), (0, 0.02, -0.01), p["polymer"], rot=(math.radians(-15), 0, 0), bevel=0.004),  # grip
        box((0.006, 0.04, 0.025), (0, -0.01, 0.022), p["polymer"]),  # trigger guard
        cyl(0.007, 0.02, (0, -0.15, 0.075), p["black"], rot=(math.radians(90), 0, 0), verts=8),
        box((0.006, 0.006, 0.008), (0, -0.135, 0.097), p["teal"]),  # front sight
    ]
    _wpn_finish("wpn_pistol_kestrel", parts, (0, -0.16, 0.075))


def wpn_smg():
    """Wasp SMG — boxy, top-heavy, folding stock."""
    reset(); p = P()
    parts = [
        box((0.045, 0.30, 0.07), (0, -0.08, 0.06), p["gunmetal"], bevel=0.005),  # receiver
        box((0.035, 0.05, 0.12), (0, 0.0, -0.02), p["polymer"], rot=(math.radians(-12), 0, 0), bevel=0.005),  # grip
        box((0.03, 0.045, 0.20), (0, -0.12, -0.05), p["polymer"], rot=(math.radians(8), 0, 0)),  # mag
        cyl(0.013, 0.12, (0, -0.28, 0.065), p["black"], rot=(math.radians(90), 0, 0), verts=10),  # barrel
        box((0.03, 0.10, 0.012), (0, -0.07, 0.102), p["black"]),  # rail
        box((0.01, 0.20, 0.012), (0.018, 0.17, 0.06), p["steel"]),  # stock rods
        box((0.01, 0.20, 0.012), (-0.018, 0.17, 0.06), p["steel"]),
        box((0.045, 0.02, 0.09), (0, 0.27, 0.04), p["polymer"], bevel=0.004),  # butt
        box((0.035, 0.06, 0.04), (0, -0.20, 0.01), p["polymer"]),  # foregrip
    ]
    _wpn_finish("wpn_smg_wasp", parts, (0, -0.34, 0.065), (0, -0.20, -0.01))


def wpn_rifle():
    """Halberd AR — bullpup-free conventional rifle with angular handguard."""
    reset(); p = P()
    parts = [
        box((0.05, 0.28, 0.08), (0, -0.05, 0.06), p["gunmetal"], bevel=0.006),  # receiver
        box((0.055, 0.30, 0.06), (0, -0.33, 0.065), p["polymer"], bevel=0.008),  # handguard
        cyl(0.011, 0.20, (0, -0.57, 0.065), p["black"], rot=(math.radians(90), 0, 0), verts=10),  # barrel
        cyl(0.018, 0.05, (0, -0.68, 0.065), p["black"], rot=(math.radians(90), 0, 0), verts=6),  # muzzle brake
        box((0.035, 0.05, 0.12), (0, 0.03, -0.02), p["polymer"], rot=(math.radians(-18), 0, 0), bevel=0.005),  # grip
        box((0.032, 0.07, 0.20), (0, -0.12, -0.06), p["polymer"], rot=(math.radians(12), 0, 0), bevel=0.004),  # mag
        box((0.045, 0.24, 0.07), (0, 0.22, 0.045), p["polymer"], bevel=0.01),  # stock
        box((0.05, 0.03, 0.13), (0, 0.345, 0.03), p["black"]),  # butt pad
        box((0.03, 0.50, 0.012), (0, -0.20, 0.102), p["black"]),  # top rail
        box((0.04, 0.09, 0.04), (0, -0.05, 0.128), p["black"], bevel=0.004),  # optic body
        box((0.03, 0.005, 0.03), (0, -0.095, 0.128), mat("lens", (0.1, 0.5, 0.3), rough=0.05, emit=(0.05, 0.3, 0.15))),
    ]
    _wpn_finish("wpn_rifle_halberd", parts, (0, -0.71, 0.065), (0, -0.36, 0.03))


def wpn_shotgun():
    """Breaker 12 — pump shotgun with wood furniture and tube mag."""
    reset(); p = P()
    parts = [
        box((0.05, 0.22, 0.07), (0, -0.04, 0.06), p["gunmetal"], bevel=0.006),  # receiver
        cyl(0.016, 0.50, (0, -0.40, 0.08), p["gunmetal"], rot=(math.radians(90), 0, 0), verts=12),  # barrel
        cyl(0.014, 0.40, (0, -0.35, 0.045), p["black"], rot=(math.radians(90), 0, 0), verts=10),  # tube mag
        box((0.05, 0.16, 0.05), (0, -0.30, 0.042), p["wood"], bevel=0.01),  # pump
        box((0.04, 0.05, 0.12), (0, 0.06, -0.02), p["wood"], rot=(math.radians(-20), 0, 0), bevel=0.006),  # grip
        box((0.045, 0.28, 0.07), (0, 0.22, 0.03), p["wood"], rot=(math.radians(6), 0, 0), bevel=0.012),  # stock
        box((0.05, 0.03, 0.12), (0, 0.365, 0.01), p["black"]),
        box((0.006, 0.006, 0.01), (0, -0.64, 0.10), p["steel"]),  # bead sight
    ]
    _wpn_finish("wpn_shotgun_breaker", parts, (0, -0.66, 0.08), (0, -0.30, 0.03))


# ---------------------------------------------------------------- buildings
# Footprint centred on origin, ground at z=0, front faces -Y.


def _windows(parts, p, width, depth, floors, floor_h, z0=0.0, per_side=3, frame=None, skip_front_ground=True):
    frame = frame or p["trim"]
    for f in range(floors):
        if skip_front_ground and f == 0:
            continue
        z = z0 + f * floor_h + floor_h * 0.55
        for i in range(per_side):
            x = -width / 2 + (i + 0.5) * width / per_side
            for y in (-depth / 2, depth / 2):
                parts.append(box((1.2, 0.08, 1.4), (x, y, z), p["glass"]))
                parts.append(box((1.4, 0.10, 0.12), (x, y, z - 0.76), frame))


def bldg_apartment():
    """4-storey brick walk-up with balconies and rooftop parapet (rooftop = sniper perch)."""
    reset(); p = P()
    W, D, F, H = 12.0, 10.0, 4, 3.2
    parts = [box((W, D, F * H), (0, 0, F * H / 2), p["brick"])]
    parts.append(box((W + 0.4, D + 0.4, 0.3), (0, 0, F * H + 0.15), p["concrete"]))  # roof slab
    for sx, sy, lx, ly in ((0, -1, W, 0.25), (0, 1, W, 0.25), (-1, 0, 0.25, D), (1, 0, 0.25, D)):
        parts.append(box((lx + 0.4, ly + 0.4, 1.1), (sx * W / 2, sy * D / 2, F * H + 0.85), p["concrete"]))  # parapet (cover)
    _windows(parts, p, W, D, F, H, per_side=4)
    for f in range(1, F):  # front balconies
        z = f * H
        parts.append(box((3.0, 1.2, 0.15), (0, -D / 2 - 0.6, z), p["concrete"]))
        parts.append(box((3.0, 0.05, 1.0), (0, -D / 2 - 1.18, z + 0.5), p["gunmetal"]))
    parts.append(box((1.6, 0.2, 2.4), (0, -D / 2 - 0.05, 1.2), p["wood"]))  # door
    parts.append(box((2.4, 1.2, 0.12), (0, -D / 2 - 0.6, 2.7), p["concrete"]))  # canopy
    parts.append(box((1.0, 1.0, 2.2), (W / 2 - 1.0, 0, F * H + 1.4), p["concrete"]))  # roof access hut
    join(parts, "bldg_apartment_block-col")


def bldg_warehouse():
    """Dockside warehouse — hollow interior, two roll-up doors, catwalk, ribbed walls."""
    reset(); p = P()
    W, D, H, T = 22.0, 14.0, 8.0, 0.3
    metal = mat("corrugated_teal", (0.18, 0.32, 0.33), metal=0.6, rough=0.5)
    parts = [box((W, D, 0.2), (0, 0, 0.1), p["concrete"])]  # floor
    parts.append(box((W, T, H), (0, D / 2, H / 2), metal))  # back wall
    for sx in (-1, 1):
        parts.append(box((T, D, H), (sx * W / 2, 0, H / 2), metal))
    # front wall with two door openings 5m wide x 5m tall
    door_w, door_h = 5.0, 5.0
    xs = [-W / 2, -7.5 - door_w / 2, -7.5 + door_w / 2, 7.5 - door_w / 2, 7.5 + door_w / 2, W / 2]
    for a, c in ((xs[0], xs[1]), (xs[2], xs[3]), (xs[4], xs[5])):
        parts.append(box((c - a, T, H), ((a + c) / 2, -D / 2, H / 2), metal))
    for x in (-7.5, 7.5):
        parts.append(box((door_w, T, H - door_h), (x, -D / 2, door_h + (H - door_h) / 2), metal))
        parts.append(box((door_w, 0.5, 0.6), (x, -D / 2 - 0.2, door_h + 0.3), p["gunmetal"]))  # roller housing
    # ribs
    for i in range(12):
        x = -W / 2 + (i + 0.5) * W / 12
        for y in (-D / 2 - 0.18, D / 2 + 0.18):
            if y < 0 and any(abs(x - dx) < door_w / 2 + 0.2 for dx in (-7.5, 7.5)):
                continue
            parts.append(box((0.12, 0.08, H), (x, y, H / 2), metal))
    # pitched roof
    for sx in (-1, 1):
        parts.append(box((W / 2 + 0.6, D + 0.6, 0.15), (sx * W / 4, 0, H + 0.6), metal, rot=(0, sx * math.radians(6), 0)))
    # interior catwalk along back wall + stairs (vertical combat)
    parts.append(box((W - 1.0, 2.0, 0.15), (0, D / 2 - 1.2, 4.0), p["gunmetal"]))
    parts.append(box((W - 1.0, 0.05, 1.0), (0, D / 2 - 2.2, 4.6), p["gunmetal"]))
    for i in range(10):
        parts.append(box((1.2, 0.35, 0.18), (W / 2 - 1.2, D / 2 - 2.6 - i * 0.35, 4.0 - (i + 1) * 0.4), p["gunmetal"]))
    for x in (-W / 3, 0, W / 3):
        parts.append(box((0.3, 0.3, H), (x, 0, H / 2), p["gunmetal"]))  # columns (cover)
    parts.append(box((1.0, 0.1, 2.1), (W / 2 - 2.0, -D / 2 - 0.05, 1.05), mat("door_red", (0.6, 0.1, 0.08))))
    join(parts, "bldg_warehouse-col")


def bldg_shop():
    """Corner convenience shop — glass storefront, awning, counter inside, back room."""
    reset(); p = P()
    W, D, H, T = 9.0, 8.0, 4.0, 0.25
    stucco = mat("stucco_cream", (0.85, 0.80, 0.68), rough=0.9)
    awning = mat("awning_green", (0.10, 0.40, 0.22), rough=0.8)
    sign = mat("sign_glow", (0.95, 0.85, 0.3), emit=(0.9, 0.7, 0.2))
    parts = [box((W, D, 0.15), (0, 0, 0.075), mat("tile", (0.75, 0.75, 0.72), rough=0.4))]
    parts.append(box((W, T, H), (0, D / 2, H / 2), stucco))
    for sx in (-1, 1):
        parts.append(box((T, D, H), (sx * W / 2, 0, H / 2), stucco))
    parts.append(box((W, D, 0.2), (0, 0, H + 0.1), p["concrete"]))  # roof
    # front: low wall + glass + door gap
    parts.append(box((W - 2.0, T, 0.8), (-1.0, -D / 2, 0.4), stucco))
    parts.append(box((W - 2.0, 0.06, 2.0), (-1.0, -D / 2, 1.8), p["glass"]))
    parts.append(box((W, T, 1.2), (0, -D / 2, H - 0.6), stucco))
    parts.append(box((1.8, 0.06, 2.8), (W / 2 - 1.0, -D / 2, 1.4), p["glass"]))  # glass door (shootable)
    parts.append(box((W + 0.4, 1.6, 0.08), (0, -D / 2 - 0.8, H - 0.9), awning, rot=(math.radians(-12), 0, 0)))
    parts.append(box((W * 0.7, 0.15, 0.6), (0, -D / 2 - 0.1, H - 0.25), sign))
    # interior: counter, shelves, back-room wall
    parts.append(box((3.0, 0.8, 1.1), (-2.0, 0.2, 0.55), p["wood"]))
    for i in range(3):
        parts.append(box((0.5, 3.0, 1.8), (0.5 + i * 1.3, 0.8, 0.9), p["gunmetal"]))
    parts.append(box((W - 2.5, 0.15, H), (-1.25, D / 2 - 2.0, H / 2), stucco))
    join(parts, "bldg_corner_shop-col")


def bldg_garage():
    """3-level open parking garage — columns + low perimeter walls = cover-heavy arena."""
    reset(); p = P()
    W, D, L, H = 30.0, 20.0, 3, 3.0
    stripe = mat("stripe_yellow", (0.9, 0.75, 0.1), rough=0.6)
    parts = []
    for lvl in range(L + 1):
        z = lvl * H
        if lvl == 0:
            parts.append(box((W, D, 0.2), (0, 0, 0.1), p["asphalt"]))
        else:
            # leave a ramp hole at +X end
            parts.append(box((W - 6.0, D, 0.3), (-3.0, 0, z), p["concrete"]))
            parts.append(box((6.0, D / 2, 0.3), (W / 2 - 3.0, D / 4, z), p["concrete"]))
            for sy in (-1, 1):
                parts.append(box((W, 0.25, 1.0), (0, sy * D / 2, z + 0.65), p["concrete"]))  # perimeter cover wall
            for sx in (-1, 1):
                parts.append(box((0.25, D, 1.0), (sx * W / 2, 0, z + 0.65), p["concrete"]))
        if lvl < L:
            for ix in range(5):
                for iy in range(3):
                    x = -W / 2 + 1.0 + ix * (W - 2.0) / 4
                    y = -D / 2 + 1.0 + iy * (D - 2.0) / 2
                    parts.append(box((0.6, 0.6, H), (x, y, z + H / 2), p["concrete"]))
            # ramp from this level up, alternating along Y in the hole
            rl = math.hypot(D / 2, H)
            ang = math.atan2(H, D / 2)
            parts.append(box((5.5, rl, 0.25), (W / 2 - 3.0, -D / 4, z + H / 2), p["concrete"], rot=(-ang, 0, 0)))
            for i in range(6):
                parts.append(box((0.12, 4.5, 0.02), (-W / 2 + 3 + i * 3.0, 0, z + (0.21 if lvl == 0 else 0.16)), stripe))
    parts.append(box((1.2, 1.2, H * L + 1.5), (-W / 2 + 0.6, -D / 2 + 0.6, (H * L + 1.5) / 2), mat("stair_core", (0.35, 0.36, 0.38))))
    join(parts, "bldg_parking_garage-col")


# ---------------------------------------------------------------- props (→ Set repo)


def prop_crate():
    reset(); p = P()
    wood = mat("crate_wood", (0.55, 0.40, 0.22), rough=0.85)
    parts = [box((1.2, 1.2, 1.2), (0, 0, 0.6), wood, bevel=0.02)]
    for z in (0.08, 1.12):
        for y in (-0.61, 0.61):
            parts.append(box((1.22, 0.04, 0.14), (0, y, z), p["wood"]))
        for x in (-0.61, 0.61):
            parts.append(box((0.04, 1.22, 0.14), (x, 0, z), p["wood"]))
    join(parts, "prop_crate_wood-col")


def prop_barrel():
    reset(); p = P()
    red = mat("barrel_red", (0.65, 0.08, 0.05), metal=0.5, rough=0.5)
    parts = [cyl(0.30, 0.9, (0, 0, 0.45), red, verts=16)]
    for z in (0.2, 0.7):
        parts.append(cyl(0.31, 0.04, (0, 0, z), p["gunmetal"], verts=16))
    parts.append(box((0.25, 0.01, 0.2), (0, -0.30, 0.45), mat("hazard_label", (0.95, 0.8, 0.1))))
    join(parts, "prop_barrel_explosive-col")


def prop_barrier():
    reset(); p = P()
    parts = [
        box((2.0, 0.6, 0.3), (0, 0, 0.15), p["concrete"]),
        box((2.0, 0.3, 0.55), (0, 0, 0.55), p["concrete"], bevel=0.03),
        box((2.0, 0.18, 0.12), (0, 0, 0.88), p["concrete"]),
        box((0.3, 0.32, 0.1), (-0.6, 0, 0.75), mat("stripe_white_red", (0.85, 0.15, 0.1))),
        box((0.3, 0.32, 0.1), (0.6, 0, 0.75), mat("stripe_white_red", (0.85, 0.15, 0.1))),
    ]
    join(parts, "prop_concrete_barrier-col")


def prop_sandbags():
    reset(); p = P()
    sack = mat("sack", (0.55, 0.50, 0.36), rough=1.0)
    parts = []
    for row in range(3):
        n = 4 - (row % 2)
        for i in range(n):
            x = -0.75 + i * 0.5 + (0.25 if row % 2 else 0)
            parts.append(sphere(0.25, (x, 0, 0.13 + row * 0.22), sack, scale=(1.0, 0.75, 0.48), seg=10))
    join(parts, "prop_sandbag_wall-col")


def prop_dumpster():
    reset(); p = P()
    green = mat("dumpster_green", (0.10, 0.30, 0.15), metal=0.4, rough=0.6)
    parts = [
        box((1.8, 1.1, 1.1), (0, 0, 0.65), green, bevel=0.03),
        box((1.85, 1.15, 0.06), (0, 0.02, 1.23), p["black"], rot=(math.radians(-4), 0, 0)),
    ]
    for x in (-0.75, 0.75):
        for y in (-0.45, 0.45):
            parts.append(cyl(0.06, 0.05, (x, y, 0.06), p["black"], rot=(0, math.radians(90), 0), verts=8))
    join(parts, "prop_dumpster-col")


def prop_ammo_box():
    reset(); p = P()
    olive = mat("ammo_olive", (0.22, 0.25, 0.14), metal=0.3, rough=0.6)
    parts = [
        box((0.30, 0.15, 0.18), (0, 0, 0.09), olive, bevel=0.008),
        box((0.31, 0.16, 0.03), (0, 0, 0.18), olive),
        box((0.12, 0.02, 0.02), (0, 0, 0.205), p["black"]),
        box((0.10, 0.005, 0.05), (0, -0.078, 0.10), mat("ammo_label", (0.9, 0.8, 0.2))),
    ]
    join(parts, "prop_ammo_box-col")


def prop_cone():
    reset(); p = P()
    orange = mat("cone_orange", (0.95, 0.35, 0.05), rough=0.6)
    parts = [box((0.4, 0.4, 0.04), (0, 0, 0.02), p["black"]), cyl(0.15, 0.68, (0, 0, 0.38), orange, verts=12, r2=0.03)]
    parts.append(cyl(0.11, 0.08, (0, 0, 0.42), mat("reflect", (0.95, 0.95, 0.95), emit=(0.3, 0.3, 0.3)), verts=12, r2=0.09))
    join(parts, "prop_traffic_cone-col")


# ---------------------------------------------------------------- main

GROUPS = {
    "characters": [
        ("characters/chr_player_rook.glb", char_player),
        ("characters/chr_enemy_brawler.glb", char_brawler),
        ("characters/chr_enemy_gunman.glb", char_gunman),
        ("characters/chr_enemy_enforcer.glb", char_enforcer),
        ("characters/chr_npc_civilian.glb", char_civilian),
    ],
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
    "props": [
        ("prop_crate_wood.glb", prop_crate),
        ("prop_barrel_explosive.glb", prop_barrel),
        ("prop_concrete_barrier.glb", prop_barrier),
        ("prop_sandbag_wall.glb", prop_sandbags),
        ("prop_dumpster.glb", prop_dumpster),
        ("prop_ammo_box.glb", prop_ammo_box),
        ("prop_traffic_cone.glb", prop_cone),
    ],
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="game/assets")
    ap.add_argument("--props-out", default=None, help="props dir (Set repo); skipped if omitted")
    ap.add_argument("--only", default="characters,weapons,buildings,props")
    a = ap.parse_args(argv)
    for g in a.only.split(","):
        root = a.props_out if g == "props" else a.out
        if root is None:
            print(f"[harbor] skip {g} (no output dir)")
            continue
        for rel, fn in GROUPS[g]:
            fn()
            export(os.path.abspath(os.path.join(root, rel)))


if __name__ == "__main__":
    main()
