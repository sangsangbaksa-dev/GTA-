"""Procedural prop generator for Project Harbor (original designs) -> sangsangbaksa-dev/Set.

Run headless (from this repo root, Set cloned next to it):
    blender -b --factory-startup -P tools/blender/build_props.py -- --out ../set/props [--only prop_atm,prop_safe]

Quality rules (higher than the first placeholder pass):
  * Multi-segment bevels + smooth-by-angle shading, so silhouettes catch light.
  * Real profiles (extruded polygons) instead of stacked boxes where shape matters.
  * Every prop ships a cheap collision hull: separate mesh(es) named "*-colonly"
    (Godot builds a static collider from them and drops the visual). Pickups use the
    same hull as their trigger volume.
  * 1 unit = 1 m, ground at z=0, origin at footprint centre, front faces -Y (Blender).
  * Godot generates LODs on import; keep visual meshes < ~6k tris.

Each prop is registered in PROPS with category + gameplay tag; the Set README table and
data/props/props.json in Harbor are derived from the same list.
"""
import argparse
import json
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

# ---------------------------------------------------------------- scene helpers

_MATS = {}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _MATS.clear()


def mat(name, rgb, metal=0.0, rough=0.7, alpha=1.0, emit=None, emit_strength=2.0, clearcoat=0.0):
    if name in _MATS:
        return _MATS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*rgb, 1.0)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    if clearcoat:
        b.inputs["Coat Weight"].default_value = clearcoat
    if alpha < 1.0:
        b.inputs["Alpha"].default_value = alpha
        m.blend_method = "BLEND"
    if emit:
        b.inputs["Emission Color"].default_value = (*emit, 1.0)
        b.inputs["Emission Strength"].default_value = emit_strength
    _MATS[name] = m
    return m


def _active(o):
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    bpy.context.view_layer.objects.active = o


def _finish(o, material, bevel=0.0, seg=2, smooth=True):
    if material:
        o.data.materials.append(material)
    _active(o)
    if bevel > 0:
        mod = o.modifiers.new("bevel", "BEVEL")
        mod.width = bevel
        mod.segments = seg
        mod.limit_method = "ANGLE"
        mod.harden_normals = False
        bpy.ops.object.modifier_apply(modifier=mod.name)
    if smooth:
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    return o


def box(size, loc, m=None, rot=(0, 0, 0), bevel=0.0, seg=2, name="box"):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return _finish(o, m, bevel, seg)


def cyl(r, depth, loc, m=None, rot=(0, 0, 0), verts=24, name="cyl", r2=None, bevel=0.0, seg=2):
    if r2 is None:
        bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r, depth=depth, location=loc, rotation=rot)
    else:
        bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r, radius2=r2, depth=depth, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    return _finish(o, m, bevel, seg)


def torus(R, r, loc, m=None, rot=(0, 0, 0), major=32, minor=10, name="torus"):
    bpy.ops.mesh.primitive_torus_add(major_radius=R, minor_radius=r, major_segments=major,
                                     minor_segments=minor, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    return _finish(o, m)


def sphere(r, loc, m=None, scale=(1, 1, 1), seg=20, name="sph"):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=max(8, seg // 2), radius=r, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return _finish(o, m)


def rod(p0, p1, r, m, verts=12, name="rod"):
    """Cylinder between two points (pipes, rails, handles)."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    o = cyl(r, d.length, (p0 + p1) / 2, m, verts=verts, name=name)
    o.rotation_mode = "QUATERNION"
    o.rotation_quaternion = d.to_track_quat("Z", "Y")
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
    return o


def profile(points_xz, depth, m=None, loc=(0, 0, 0), bevel=0.0, seg=2, name="profile", axis="Y"):
    """Extrude a closed 2D polygon (x, z) along Y (default) or X by `depth`, centred."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    h = depth / 2
    if axis == "Y":
        front = [bm.verts.new((x, -h, z)) for x, z in points_xz]
        back = [bm.verts.new((x, h, z)) for x, z in points_xz]
    else:
        front = [bm.verts.new((-h, x, z)) for x, z in points_xz]
        back = [bm.verts.new((h, x, z)) for x, z in points_xz]
    n = len(points_xz)
    bm.faces.new(front)
    bm.faces.new(list(reversed(back)))
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((front[i], front[j], back[j], back[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    o.location = loc
    _active(o)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    return _finish(o, m, bevel, seg)


def lathe(points_rz, m=None, loc=(0, 0, 0), verts=32, name="lathe"):
    """Revolve an open (r, z) polyline around Z — bottles, buoys, bollards, hydrants."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    rings = []
    for k in range(verts):
        a = 2 * math.pi * k / verts
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new((r * ca, r * sa, z)) for r, z in points_rz])
    for k in range(verts):
        a, b = rings[k], rings[(k + 1) % verts]
        for i in range(len(points_rz) - 1):
            bm.faces.new((a[i], b[i], b[i + 1], a[i + 1]))
    # caps
    if points_rz[0][0] > 0:
        bm.faces.new([r[0] for r in reversed(rings)])
    if points_rz[-1][0] > 0:
        bm.faces.new([r[-1] for r in rings])
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    o.location = loc
    _active(o)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    return _finish(o, m)


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


def col(name, size, loc, rot=(0, 0, 0)):
    """Invisible collision box; Godot '-colonly' suffix."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    o.name = f"{name}-colonly"
    o.data.name = o.name
    return o


def col_cyl(name, r, h, loc):
    bpy.ops.mesh.primitive_cylinder_add(vertices=10, radius=r, depth=h, location=loc)
    o = bpy.context.active_object
    o.name = f"{name}-colonly"
    o.data.name = o.name
    return o


def marker(name, loc):
    """Empty used by gameplay code (interaction point, muzzle of a turret, loot socket...)."""
    e = bpy.data.objects.new(name, None)
    e.location = loc
    e.empty_display_size = 0.08
    bpy.context.scene.collection.objects.link(e)
    return e


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_apply=True,
                              export_yup=True, export_animations=False)
    print(f"[harbor] wrote {path}")


# ---------------------------------------------------------------- palette


def P():
    return dict(
        black=mat("black_rubber", (0.025, 0.025, 0.028), rough=0.75),
        gunmetal=mat("gunmetal", (0.11, 0.115, 0.125), metal=0.9, rough=0.38),
        steel=mat("steel_brushed", (0.58, 0.59, 0.61), metal=1.0, rough=0.32),
        galv=mat("steel_galvanized", (0.62, 0.64, 0.64), metal=0.85, rough=0.55),
        rust=mat("steel_rusted", (0.33, 0.17, 0.09), metal=0.4, rough=0.85),
        concrete=mat("concrete", (0.56, 0.55, 0.52), rough=0.95),
        concrete_dark=mat("concrete_dark", (0.38, 0.37, 0.35), rough=0.95),
        wood_raw=mat("wood_pine", (0.62, 0.46, 0.27), rough=0.85),
        wood_dark=mat("wood_stained", (0.30, 0.18, 0.09), rough=0.7),
        plastic_w=mat("plastic_white", (0.86, 0.86, 0.83), rough=0.45),
        plastic_k=mat("plastic_black", (0.05, 0.05, 0.055), rough=0.5),
        glass=mat("glass", (0.30, 0.42, 0.48), metal=0.1, rough=0.04, alpha=0.35),
        screen=mat("screen_on", (0.02, 0.06, 0.08), rough=0.2, emit=(0.15, 0.55, 0.7), emit_strength=1.5),
        hazard=mat("hazard_yellow", (0.95, 0.72, 0.05), rough=0.55),
        red=mat("paint_red", (0.62, 0.06, 0.04), metal=0.3, rough=0.45, clearcoat=0.3),
        orange=mat("safety_orange", (0.95, 0.33, 0.04), rough=0.55),
        reflect=mat("reflective_white", (0.95, 0.95, 0.95), rough=0.2, emit=(0.25, 0.25, 0.25), emit_strength=1.0),
        olive=mat("paint_olive", (0.21, 0.24, 0.13), metal=0.3, rough=0.6),
        teal=mat("harbor_teal", (0.02, 0.38, 0.40), metal=0.35, rough=0.45, clearcoat=0.4),
        cash=mat("cash_green", (0.45, 0.58, 0.40), rough=0.9),
        canvas=mat("canvas_navy", (0.08, 0.10, 0.17), rough=0.95),
        lamp=mat("lamp_warm", (1.0, 0.85, 0.6), emit=(1.0, 0.78, 0.45), emit_strength=6.0),
    )


# ================================================================ COVER


def prop_crate_wood():
    reset(); p = P()
    s = 1.2
    parts = [box((s - 0.06, s - 0.06, s - 0.06), (0, 0, s / 2), p["wood_raw"], bevel=0.01)]
    # plank seams: thin dark grooves on each side face
    for i in range(1, 5):
        z = i * s / 5
        for y in (-s / 2 + 0.028, s / 2 - 0.028):
            parts.append(box((s - 0.1, 0.006, 0.008), (0, y, z), p["wood_dark"]))
        for x in (-s / 2 + 0.028, s / 2 - 0.028):
            parts.append(box((0.006, s - 0.1, 0.008), (x, 0, z), p["wood_dark"]))
    # frame battens + diagonal brace on each vertical face
    t, w = 0.035, 0.1
    for y in (-s / 2, s / 2):
        for z in (w / 2, s - w / 2):
            parts.append(box((s, t, w), (0, y, z), p["wood_dark"], bevel=0.006))
        for x in (-s / 2 + w / 2, s / 2 - w / 2):
            parts.append(box((w, t, s), (x, y, s / 2), p["wood_dark"], bevel=0.006))
        parts.append(box((s * 1.2, t * 0.9, w * 0.9), (0, y, s / 2), p["wood_dark"], rot=(0, math.radians(45), 0), bevel=0.005))
    for x in (-s / 2, s / 2):
        for z in (w / 2, s - w / 2):
            parts.append(box((t, s, w), (x, 0, z), p["wood_dark"], bevel=0.006))
        for y in (-s / 2 + w / 2, s / 2 - w / 2):
            parts.append(box((t, w, s), (x, y, s / 2), p["wood_dark"], bevel=0.006))
    # stencil plate
    parts.append(box((0.36, 0.004, 0.16), (0.15, -s / 2 - 0.02, 0.78), mat("stencil_black", (0.04, 0.04, 0.04), rough=0.9)))
    join(parts, "prop_crate_wood")
    col("prop_crate_wood", (s, s, s), (0, 0, s / 2))


def prop_crate_stack():
    reset(); p = P()
    parts = []

    def crate(sx, sy, sz, loc, rz=0.0):
        cx, cy, cz = loc
        r = (0, 0, math.radians(rz))
        out = [box((sx - 0.05, sy - 0.05, sz - 0.05), (cx, cy, cz + sz / 2), p["wood_raw"], rot=r, bevel=0.01)]
        for dz in (0.05, sz - 0.05):
            out.append(box((sx, sy, 0.09), (cx, cy, cz + dz), p["wood_dark"], rot=r, bevel=0.006))
        return out

    parts += crate(1.2, 1.2, 1.0, (-0.65, 0, 0))
    parts += crate(1.0, 1.0, 0.9, (0.55, 0.05, 0), rz=6)
    parts += crate(0.9, 0.9, 0.7, (-0.55, 0.05, 1.0), rz=-8)
    join(parts, "prop_crate_stack")
    col("prop_crate_stack_a", (2.3, 1.25, 1.0), (-0.05, 0, 0.5))
    col("prop_crate_stack_b", (1.0, 1.0, 0.7), (-0.55, 0.05, 1.35))


def _drum(p, body_mat, name):
    r, h = 0.29, 0.88
    parts = [cyl(r, h, (0, 0, h / 2), body_mat, verts=32, bevel=0.01)]
    for z in (0.012, h - 0.012):  # chimes
        parts.append(torus(r - 0.004, 0.012, (0, 0, z), body_mat, major=32, minor=8))
    for z in (h / 3, 2 * h / 3):  # rolling hoops
        parts.append(torus(r + 0.004, 0.011, (0, 0, z), body_mat, major=32, minor=8))
    parts.append(cyl(0.035, 0.015, (0.17, 0, h + 0.002), p["gunmetal"], verts=12))  # bung caps
    parts.append(cyl(0.022, 0.015, (-0.17, 0.05, h + 0.002), p["gunmetal"], verts=12))
    return parts, r, h


def prop_barrel_explosive():
    reset(); p = P()
    parts, r, h = _drum(p, p["red"], "drum")
    # wrap-around hazard band + diamond label
    parts.append(cyl(r + 0.002, 0.12, (0, 0, h / 2), p["hazard"], verts=32))
    lab = box((0.13, 0.006, 0.13), (0, -r - 0.004, h / 2), mat("label_white", (0.92, 0.92, 0.9), rough=0.6),
              rot=(0, math.radians(45), 0))
    parts.append(lab)
    join(parts, "prop_barrel_explosive")
    col_cyl("prop_barrel_explosive", r, h, (0, 0, h / 2))


def prop_barrel_oil():
    reset(); p = P()
    parts, r, h = _drum(p, mat("paint_blue_worn", (0.07, 0.18, 0.40), metal=0.35, rough=0.6), "drum")
    parts.append(cyl(r + 0.0015, 0.05, (0, 0, h * 0.82), p["rust"], verts=32))
    join(parts, "prop_barrel_oil")
    col_cyl("prop_barrel_oil", r, h, (0, 0, h / 2))


def prop_concrete_barrier():
    reset(); p = P()
    # Jersey-style safety profile (x = across, z = up), 2 m long along X
    prof = [(-0.30, 0.0), (0.30, 0.0), (0.30, 0.08), (0.20, 0.26), (0.09, 0.81), (-0.09, 0.81), (-0.20, 0.26), (-0.30, 0.08)]
    body = profile(prof, 2.0, p["concrete"], bevel=0.015, seg=3, name="barrier", axis="X")
    parts = [body]
    # lifting loops + drain slots + reflector stripes
    for y in (-0.55, 0.55):
        parts.append(torus(0.05, 0.012, (y, 0, 0.83), p["rust"], rot=(math.radians(90), 0, 0), major=16, minor=6))
    for y in (-0.6, 0.6):
        parts.append(box((0.30, 0.62, 0.07), (y, 0, 0.035), p["concrete_dark"]))
    for y in (-0.35, 0.35):
        parts.append(box((0.22, 0.005, 0.06), (y, -0.105, 0.66), p["reflect"], rot=(math.radians(-11), 0, 0)))
    o = join(parts, "prop_concrete_barrier")
    # rotate so length runs along X (profile extruded along X already gives length on X)
    col("prop_concrete_barrier", (2.0, 0.6, 0.82), (0, 0, 0.41))


def prop_sandbag_wall():
    reset(); p = P()
    sack = mat("burlap", (0.56, 0.50, 0.35), rough=1.0)
    sack2 = mat("burlap_dark", (0.46, 0.41, 0.29), rough=1.0)
    parts = []
    radius = 3.0  # gentle arc, bags face -Y
    for row in range(4):
        n = 6 - (row % 2)
        for i in range(n):
            u = (i - (n - 1) / 2) * 0.46
            a = u / radius
            x, y = radius * math.sin(a), -radius * (1 - math.cos(a)) * -1
            z = 0.11 + row * 0.19
            s = sphere(0.26, (x, y, z), sack if (i + row) % 3 else sack2, scale=(1.0, 0.62, 0.40), seg=16)
            s.rotation_euler = (0, math.radians(3 * ((i + row) % 3 - 1)), -a)
            bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
            parts.append(s)
            # tied ear
            parts.append(sphere(0.05, (x + 0.24 * math.cos(a), y + 0.24 * math.sin(a), z), sack2, scale=(1.3, 0.8, 0.6), seg=8))
    join(parts, "prop_sandbag_wall")
    col("prop_sandbag_wall", (2.7, 0.45, 0.80), (0, 0.06, 0.40))


def prop_dumpster():
    reset(); p = P()
    green = mat("dumpster_green", (0.08, 0.26, 0.13), metal=0.45, rough=0.55)
    # tapered body via profile (front lower than back)
    prof = [(-0.55, 0.18), (0.55, 0.18), (0.62, 1.22), (-0.55, 1.12)]
    body = profile([(y, z) for y, z in prof], 1.9, green, bevel=0.02, seg=2, name="body", axis="X")
    # profile axis X: first coord maps to Y
    parts = [body]
    for x in (-0.95, 0.95):  # side ribs
        parts.append(box((0.04, 1.15, 0.9), (x, 0.03, 0.68), green, bevel=0.01))
    for x in (-0.5, 0.0, 0.5):
        parts.append(box((0.08, 0.05, 0.85), (x, -0.57, 0.62), green, bevel=0.01))
    parts.append(box((2.0, 0.08, 0.10), (0, -0.60, 0.95), p["gunmetal"], bevel=0.01))  # fork pocket / rail
    # split plastic lids, slightly ajar
    lid = p["plastic_k"]
    parts.append(box((0.95, 1.22, 0.05), (-0.48, 0.03, 1.21), lid, rot=(math.radians(-5), 0, 0), bevel=0.012))
    parts.append(box((0.95, 1.22, 0.05), (0.48, 0.03, 1.24), lid, rot=(math.radians(-9), 0, 0), bevel=0.012))
    for x in (-0.8, 0.8):  # hinges
        parts.append(cyl(0.025, 0.12, (x, 0.62, 1.20), p["gunmetal"], rot=(0, math.radians(90), 0), verts=10))
    for x in (-0.78, 0.78):  # casters
        for y in (-0.42, 0.42):
            parts.append(box((0.08, 0.06, 0.06), (x, y, 0.15), p["gunmetal"]))
            parts.append(cyl(0.07, 0.05, (x, y, 0.075), p["black"], rot=(0, math.radians(90), 0), verts=16))
    parts.append(box((0.4, 0.005, 0.18), (0.45, -0.585, 0.75), mat("decal_white", (0.85, 0.85, 0.82), rough=0.7)))
    join(parts, "prop_dumpster")
    col("prop_dumpster", (1.95, 1.2, 1.25), (0, 0.03, 0.625))


def prop_shipping_container():
    reset(); p = P()
    L, W, H = 6.06, 2.44, 2.59
    paint = mat("container_rust_red", (0.42, 0.10, 0.06), metal=0.5, rough=0.6)
    frame = mat("container_frame", (0.30, 0.08, 0.05), metal=0.5, rough=0.6)
    parts = [box((L - 0.2, W - 0.12, H - 0.2), (0, 0, H / 2), paint)]
    # corrugation on long sides (trapezoid ribs)
    n = 34
    for i in range(n):
        x = -L / 2 + 0.2 + (i + 0.5) * (L - 0.4) / n
        for y in (-W / 2 + 0.05, W / 2 - 0.05):
            parts.append(box((0.08, 0.05, H - 0.32), (x, y, H / 2), paint, bevel=0.012))
    # roof dents
    for i in range(10):
        x = -L / 2 + 0.4 + i * (L - 0.8) / 9
        parts.append(box((0.25, W - 0.2, 0.03), (x, 0, H - 0.08), paint, bevel=0.01))
    # frame: corner posts + rails + corner castings
    for x in (-L / 2 + 0.08, L / 2 - 0.08):
        for y in (-W / 2 + 0.08, W / 2 - 0.08):
            parts.append(box((0.16, 0.16, H), (x, y, H / 2), frame, bevel=0.01))
            for z in (0.09, H - 0.09):
                parts.append(box((0.18, 0.18, 0.18), (x, y, z), p["gunmetal"], bevel=0.01))
    for z in (0.08, H - 0.08):
        for y in (-W / 2 + 0.06, W / 2 - 0.06):
            parts.append(box((L - 0.3, 0.12, 0.16), (0, y, z), frame))
        for x in (-L / 2 + 0.06, L / 2 - 0.06):
            parts.append(box((0.12, W - 0.3, 0.16), (x, 0, z), frame))
    # doors at +X end: two leaves, 4 lock bars, handles
    xd = L / 2 - 0.02
    for y in (-W / 4, W / 4):
        parts.append(box((0.04, W / 2 - 0.08, H - 0.34), (xd, y, H / 2), paint, bevel=0.01))
    for y in (-0.85, -0.35, 0.35, 0.85):
        parts.append(rod((xd + 0.05, y, 0.2), (xd + 0.05, y, H - 0.2), 0.022, p["galv"], verts=10))
        parts.append(box((0.06, 0.16, 0.05), (xd + 0.09, y + 0.06, 1.2), p["galv"], bevel=0.01))
    parts.append(box((0.006, 0.9, 0.12), (xd + 0.025, -W / 4, H - 0.45), mat("decal_white", (0.85, 0.85, 0.82), rough=0.7)))
    join(parts, "prop_shipping_container")
    col("prop_shipping_container", (L, W, H), (0, 0, H / 2))
    marker("door_interact", (L / 2 + 0.6, 0, 1.0))


def prop_pallet_stack():
    reset(); p = P()
    parts = []

    def pallet(z):
        out = []
        for x in (-0.55, 0, 0.55):
            out.append(box((0.10, 1.0, 0.09), (x, 0, z + 0.045), p["wood_raw"], bevel=0.005))
        for i in range(7):
            y = -0.45 + i * 0.15
            out.append(box((1.2, 0.11, 0.022), (0, y, z + 0.1), p["wood_raw"], bevel=0.003))
        for y in (-0.45, 0.45):
            out.append(box((1.2, 0.11, 0.022), (0, y, z + 0.011), p["wood_raw"], bevel=0.003))
        return out

    parts += pallet(0.0)
    # shrink-wrapped load of boxes
    wrap = mat("shrink_wrap", (0.78, 0.82, 0.84), rough=0.15, alpha=0.55)
    cardboard = mat("cardboard", (0.55, 0.40, 0.24), rough=0.9)
    for ix in range(2):
        for iy in range(2):
            for iz in range(2):
                parts.append(box((0.56, 0.47, 0.4), (-0.29 + ix * 0.58, -0.245 + iy * 0.49, 0.32 + iz * 0.41), cardboard, bevel=0.01))
    parts.append(box((1.18, 1.0, 0.84), (0, 0, 0.53), wrap, bevel=0.03))
    parts += pallet(0.95)
    parts += pallet(1.07)
    join(parts, "prop_pallet_stack")
    col("prop_pallet_stack", (1.2, 1.0, 1.2), (0, 0, 0.6))


def prop_gas_cylinder_cage():
    reset(); p = P()
    parts = []
    W, D, H = 1.3, 0.7, 1.75
    for x in (-W / 2, W / 2):
        for y in (-D / 2, D / 2):
            parts.append(box((0.05, 0.05, H), (x, y, H / 2), p["galv"], bevel=0.005))
    for z in (0.05, H - 0.03, H / 2):
        for y in (-D / 2, D / 2):
            parts.append(box((W, 0.04, 0.04), (0, y, z), p["galv"]))
        for x in (-W / 2, W / 2):
            parts.append(box((0.04, D, 0.04), (x, 0, z), p["galv"]))
    # mesh bars on the front
    for i in range(12):
        x = -W / 2 + (i + 0.5) * W / 12
        parts.append(rod((x, -D / 2, 0.05), (x, -D / 2, H - 0.03), 0.006, p["galv"], verts=6))
    parts.append(box((W, D, 0.03), (0, 0, H), p["galv"]))
    colors = [mat("cyl_orange", (0.85, 0.35, 0.05), metal=0.3, rough=0.45),
              mat("cyl_green", (0.10, 0.35, 0.12), metal=0.3, rough=0.45),
              mat("cyl_grey", (0.40, 0.42, 0.45), metal=0.3, rough=0.45)]
    for i in range(4):
        x = -0.45 + i * 0.3
        c = colors[i % 3]
        parts.append(lathe([(0.0, 0.03), (0.11, 0.03), (0.115, 0.06), (0.115, 1.18), (0.09, 1.30), (0.035, 1.36), (0.0, 1.36)], c, loc=(x, 0.02, 0)))
        parts.append(cyl(0.022, 0.07, (x, 0.02, 1.40), p["steel"], verts=10))
        parts.append(cyl(0.04, 0.035, (x, 0.02, 1.44), p["gunmetal"], verts=12))
    parts.append(box((0.30, 0.005, 0.2), (0, -D / 2 - 0.01, 1.2), p["hazard"]))
    join(parts, "prop_gas_cylinder_cage")
    col("prop_gas_cylinder_cage", (W, D, H), (0, 0, H / 2))


def prop_cable_spool():
    reset(); p = P()
    R, w = 0.75, 0.9
    parts = []
    for y in (-w / 2, w / 2):
        parts.append(cyl(R, 0.06, (0, y, R), p["wood_raw"], rot=(math.radians(90), 0, 0), verts=40, bevel=0.008))
        for k in range(8):  # bolts
            a = k * math.pi / 4
            parts.append(cyl(0.025, 0.08, (0.45 * math.cos(a), y, R + 0.45 * math.sin(a)), p["gunmetal"], rot=(math.radians(90), 0, 0), verts=8))
    parts.append(cyl(0.25, w, (0, 0, R), p["wood_dark"], rot=(math.radians(90), 0, 0), verts=24))
    cable = mat("cable_black", (0.03, 0.03, 0.03), rough=0.45)
    for k in range(6):  # wound cable layers
        parts.append(cyl(0.5 + k * 0.025, w - 0.08, (0, 0, R), cable, rot=(math.radians(90), 0, 0), verts=40))
    join(parts, "prop_cable_spool")
    bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=R, depth=w + 0.06, location=(0, 0, R), rotation=(math.radians(90), 0, 0))
    o = bpy.context.active_object
    o.name = o.data.name = "prop_cable_spool-colonly"


def prop_scaffold_section():
    reset(); p = P()
    W, D, H = 2.5, 1.0, 2.0
    parts = []
    for x in (-W / 2, W / 2):
        for y in (-D / 2, D / 2):
            parts.append(rod((x, y, 0), (x, y, H + 1.0), 0.024, p["galv"]))
            parts.append(box((0.15, 0.15, 0.01), (x, y, 0.005), p["galv"]))
    for z in (0.15, H, H + 1.0):
        for y in (-D / 2, D / 2):
            parts.append(rod((-W / 2, y, z), (W / 2, y, z), 0.022, p["galv"]))
        for x in (-W / 2, W / 2):
            parts.append(rod((x, -D / 2, z), (x, D / 2, z), 0.022, p["galv"]))
    for y in (-D / 2, D / 2):  # cross braces
        parts.append(rod((-W / 2, y, 0.15), (W / 2, y, H), 0.018, p["galv"]))
    for i in range(4):  # deck planks
        parts.append(box((W + 0.2, 0.22, 0.04), (0, -0.36 + i * 0.24, H + 0.03), p["wood_raw"], bevel=0.004))
    for z in (H + 0.5,):  # guard rail mid
        parts.append(rod((-W / 2, -D / 2, z), (W / 2, -D / 2, z), 0.02, p["galv"]))
    parts.append(box((W, 0.02, 0.15), (0, -D / 2, H + 0.12), p["hazard"]))  # toe board
    join(parts, "prop_scaffold_section")
    col("prop_scaffold_deck", (W + 0.2, D, 0.06), (0, 0, H + 0.03))
    for x in (-W / 2, W / 2):
        for y in (-D / 2, D / 2):
            col(f"prop_scaffold_post_{x:+.0f}{y:+.0f}", (0.06, 0.06, H + 1.0), (x, y, (H + 1.0) / 2))


# ================================================================ PICKUPS / LOOT


def prop_ammo_box():
    reset(); p = P()
    parts = [box((0.30, 0.15, 0.17), (0, 0, 0.085), p["olive"], bevel=0.008)]
    parts.append(box((0.31, 0.16, 0.035), (0, 0, 0.185), p["olive"], bevel=0.006))  # lid
    for x in (-0.12, 0.12):  # lid ribs
        parts.append(box((0.02, 0.165, 0.012), (x, 0, 0.205), p["olive"]))
    parts.append(rod((-0.07, 0, 0.22), (0.07, 0, 0.22), 0.008, p["black"], verts=8))  # handle
    for x in (-0.07, 0.07):
        parts.append(box((0.012, 0.02, 0.02), (x, 0, 0.208), p["gunmetal"]))
    parts.append(box((0.04, 0.03, 0.08), (0.157, 0, 0.13), p["gunmetal"], bevel=0.004))  # latch
    parts.append(box((0.14, 0.003, 0.05), (-0.03, -0.077, 0.10), p["hazard"]))
    join(parts, "prop_ammo_box")
    col("prop_ammo_box", (0.32, 0.17, 0.23), (0, 0, 0.115))


def prop_medkit():
    reset(); p = P()
    case = mat("medkit_white", (0.90, 0.90, 0.88), rough=0.35)
    redc = mat("medkit_red", (0.75, 0.05, 0.05), rough=0.4)
    parts = [box((0.36, 0.12, 0.26), (0, 0, 0.13), case, bevel=0.02, seg=3)]
    parts.append(box((0.365, 0.125, 0.012), (0, 0, 0.18), mat("medkit_seam", (0.6, 0.6, 0.6), rough=0.4)))
    parts.append(box((0.10, 0.004, 0.03), (0, -0.062, 0.12), redc))
    parts.append(box((0.03, 0.004, 0.10), (0, -0.062, 0.12), redc))
    parts.append(rod((-0.06, 0, 0.29), (0.06, 0, 0.29), 0.012, p["plastic_k"], verts=10))
    for x in (-0.06, 0.06):
        parts.append(rod((x, 0, 0.26), (x, 0, 0.29), 0.012, p["plastic_k"], verts=10))
    join(parts, "prop_medkit")
    col("prop_medkit", (0.37, 0.13, 0.31), (0, 0, 0.155))


def prop_armor_vest():
    reset(); p = P()
    fabric = mat("plate_carrier", (0.16, 0.17, 0.15), rough=0.95)
    webbing = mat("webbing", (0.10, 0.11, 0.09), rough=0.9)
    parts = []
    # lying flat on ground, front panel up (along X = width, Y = length)
    parts.append(box((0.30, 0.38, 0.055), (0, -0.05, 0.03), fabric, bevel=0.015, seg=3))  # front plate
    parts.append(box((0.30, 0.38, 0.04), (0.03, 0.30, 0.022), fabric, rot=(0, 0, math.radians(8)), bevel=0.012, seg=3))  # back plate
    for x in (-0.11, 0.11):  # shoulder straps
        parts.append(box((0.05, 0.20, 0.02), (x, 0.13, 0.05), webbing, bevel=0.005))
    for i in range(3):  # magazine pouches
        parts.append(box((0.08, 0.06, 0.09), (-0.095 + i * 0.095, -0.17, 0.09), fabric, bevel=0.01, seg=2))
        parts.append(box((0.082, 0.065, 0.015), (-0.095 + i * 0.095, -0.17, 0.138), webbing))
    for k in range(5):  # MOLLE rows
        parts.append(box((0.28, 0.008, 0.008), (0, -0.0 + k * 0.03, 0.06), webbing))
    parts.append(box((0.10, 0.04, 0.003), (0, 0.04, 0.059), mat("patch_teal", (0.02, 0.38, 0.40), rough=0.8)))
    join(parts, "prop_armor_vest")
    col("prop_armor_vest", (0.36, 0.78, 0.15), (0.01, 0.12, 0.075))


def prop_cash_bag():
    reset(); p = P()
    parts = [sphere(0.22, (0, 0, 0.2), p["canvas"], scale=(1.0, 2.0, 0.85), seg=24)]
    parts.append(box((0.03, 0.85, 0.012), (0, 0, 0.385), mat("zipper", (0.6, 0.55, 0.3), metal=0.9, rough=0.3)))
    for x in (-0.07, 0.07):  # handles
        parts.append(torus(0.1, 0.012, (x, 0, 0.36), p["black"], rot=(0, math.radians(90), 0), major=20, minor=6))
    for y in (-0.42, 0.42):
        parts.append(cyl(0.15, 0.02, (0, y * 0.98, 0.2), mat("canvas_dark", (0.04, 0.05, 0.09), rough=0.95), rot=(math.radians(90), 0, 0), verts=24))
    # bills spilling from the half-open zip
    for i, (x, y, rz) in enumerate([(0.02, 0.1, 10), (-0.03, -0.05, -15), (0.05, -0.18, 30)]):
        parts.append(box((0.16, 0.07, 0.03), (x, y, 0.40 + i * 0.012), p["cash"], rot=(math.radians(8), 0, math.radians(rz)), bevel=0.002))
        parts.append(box((0.03, 0.071, 0.031), (x, y, 0.40 + i * 0.012), mat("band_white", (0.9, 0.9, 0.85)), rot=(math.radians(8), 0, math.radians(rz))))
    join(parts, "prop_cash_bag")
    col("prop_cash_bag", (0.44, 0.9, 0.42), (0, 0, 0.21))


def prop_cash_stack():
    reset(); p = P()
    parts = []
    band = mat("band_white", (0.9, 0.9, 0.85))
    for iz in range(3):
        for ix in range(3):
            for iy in range(2):
                x, y, z = -0.17 + ix * 0.17, -0.04 + iy * 0.08, 0.016 + iz * 0.032
                parts.append(box((0.156, 0.068, 0.03), (x, y, z), p["cash"], bevel=0.002))
                parts.append(box((0.03, 0.069, 0.031), (x, y, z), band))
    parts.append(box((0.156, 0.068, 0.03), (0.02, 0.0, 0.113), p["cash"], rot=(0, 0, math.radians(25)), bevel=0.002))
    join(parts, "prop_cash_stack")
    col("prop_cash_stack", (0.52, 0.17, 0.13), (0, 0, 0.065))


def prop_bond_case():
    reset(); p = P()
    alu = mat("aluminium", (0.75, 0.76, 0.78), metal=1.0, rough=0.25)
    parts = [box((0.46, 0.12, 0.34), (0, 0, 0.17), alu, bevel=0.015, seg=3)]
    for z in (0.05, 0.29):  # ribs
        parts.append(box((0.47, 0.125, 0.012), (0, 0, z), alu, bevel=0.003))
    parts.append(box((0.47, 0.125, 0.006), (0, 0, 0.17), p["black"]))
    parts.append(rod((-0.07, 0, 0.37), (0.07, 0, 0.37), 0.012, p["black"], verts=10))
    for x in (-0.07, 0.07):
        parts.append(box((0.025, 0.03, 0.04), (x, 0, 0.35), p["gunmetal"]))
        parts.append(box((0.035, 0.01, 0.025), (x * 2.2, -0.065, 0.29), p["gunmetal"]))  # latches
    join(parts, "prop_bond_case")
    col("prop_bond_case", (0.47, 0.13, 0.39), (0, 0, 0.195))


def prop_weapon_crate():
    reset(); p = P()
    L = 1.3
    parts = [box((L, 0.5, 0.36), (0, 0, 0.18), p["olive"], bevel=0.012, seg=2)]
    parts.append(box((L + 0.01, 0.51, 0.08), (0, 0, 0.36), p["olive"], bevel=0.01))
    for x in (-0.45, 0.0, 0.45):  # ribs
        parts.append(box((0.04, 0.52, 0.38), (x, 0, 0.2), p["olive"], bevel=0.006))
    for x in (-0.3, 0.3):  # latches
        parts.append(box((0.05, 0.02, 0.09), (x, -0.262, 0.34), p["gunmetal"], bevel=0.004))
    for x in (-L / 2 - 0.02, L / 2 + 0.02):  # rope handles
        parts.append(torus(0.06, 0.012, (x, 0, 0.24), p["black"], rot=(math.radians(90), 0, 0), major=16, minor=6))
    parts.append(box((0.4, 0.004, 0.08), (0, -0.252, 0.18), p["hazard"]))
    join(parts, "prop_weapon_crate")
    col("prop_weapon_crate", (L, 0.5, 0.4), (0, 0, 0.2))
    marker("interact", (0, -0.6, 0.5))


# ================================================================ ECONOMY / INTERACTABLES


def prop_atm():
    reset(); p = P()
    shell = mat("atm_shell", (0.18, 0.20, 0.23), metal=0.6, rough=0.35, clearcoat=0.3)
    parts = [box((0.70, 0.75, 1.30), (0, 0.05, 0.65), shell, bevel=0.02, seg=3)]
    # sloped fascia
    fascia = profile([(-0.36, 0.95), (-0.24, 1.55), (0.42, 1.55), (0.42, 0.95)], 0.72, p["teal"], bevel=0.015, name="fascia", axis="X")
    parts.append(fascia)
    parts.append(box((0.48, 0.10, 0.36), (0, -0.30, 1.29), p["plastic_k"], rot=(math.radians(-20), 0, 0), bevel=0.01))  # screen bezel
    parts.append(box((0.38, 0.01, 0.27), (0, -0.355, 1.30), p["screen"], rot=(math.radians(-20), 0, 0)))
    parts.append(box((0.52, 0.22, 0.04), (0, -0.40, 1.04), p["gunmetal"], rot=(math.radians(-12), 0, 0), bevel=0.008))  # keypad shelf
    for i in range(3):
        for j in range(4):
            parts.append(box((0.03, 0.02, 0.018), (-0.06 + i * 0.04, -0.44 + j * 0.035, 1.065 + j * 0.007), p["steel"], rot=(math.radians(-12), 0, 0)))
    parts.append(box((0.10, 0.02, 0.012), (0.17, -0.42, 1.07), p["plastic_k"]))  # card slot
    parts.append(box((0.30, 0.03, 0.04), (0, -0.33, 0.88), p["plastic_k"]))  # cash slot
    parts.append(box((0.55, 0.20, 0.12), (0, -0.25, 1.62), p["teal"], bevel=0.01))  # header
    parts.append(box((0.45, 0.005, 0.07), (0, -0.351, 1.62), mat("atm_sign", (0.9, 0.95, 1.0), emit=(0.6, 0.9, 1.0), emit_strength=2.0)))
    parts.append(box((0.74, 0.8, 0.06), (0, 0.05, 0.03), p["concrete_dark"]))
    join(parts, "prop_atm")
    col("prop_atm", (0.72, 0.8, 1.7), (0, 0.05, 0.85))
    marker("interact", (0, -0.75, 1.0))


def prop_vending_machine():
    reset(); p = P()
    body = mat("vend_body", (0.55, 0.08, 0.05), metal=0.4, rough=0.35, clearcoat=0.5)
    parts = [box((0.95, 0.80, 1.85), (0, 0, 0.925), body, bevel=0.02, seg=3)]
    parts.append(box((0.62, 0.04, 1.30), (-0.12, -0.41, 1.05), p["glass"]))
    # shelves with cans
    can_cols = [mat(f"can_{i}", c, metal=0.8, rough=0.3) for i, c in
                enumerate([(0.85, 0.15, 0.1), (0.1, 0.3, 0.8), (0.9, 0.75, 0.1), (0.2, 0.65, 0.25)])]
    for row in range(5):
        z = 0.55 + row * 0.25
        parts.append(box((0.6, 0.3, 0.012), (-0.12, -0.24, z - 0.07), p["steel"]))
        for k in range(6):
            parts.append(cyl(0.033, 0.12, (-0.38 + k * 0.1, -0.30, z), can_cols[(row + k) % 4], verts=14))
    parts.append(box((0.24, 0.03, 1.30), (0.33, -0.41, 1.05), p["plastic_k"], bevel=0.005))  # control panel
    for k in range(8):
        parts.append(box((0.05, 0.02, 0.03), (0.33, -0.43, 1.45 - k * 0.07), p["steel"]))
    parts.append(box((0.14, 0.02, 0.08), (0.33, -0.43, 0.85), p["screen"]))
    parts.append(box((0.6, 0.06, 0.18), (-0.12, -0.40, 0.22), p["plastic_k"], bevel=0.01))  # dispense bin
    parts.append(box((0.9, 0.02, 0.18), (0, -0.405, 1.76), mat("vend_glow", (1, 1, 1), emit=(1.0, 0.95, 0.85), emit_strength=3.0)))
    join(parts, "prop_vending_machine")
    col("prop_vending_machine", (0.95, 0.82, 1.85), (0, 0, 0.925))
    marker("interact", (0.2, -0.85, 1.0))


def prop_cash_register():
    reset(); p = P()
    parts = [box((0.42, 0.42, 0.10), (0, 0, 0.05), p["plastic_k"], bevel=0.01, seg=3)]  # drawer
    parts.append(box((0.36, 0.005, 0.03), (0, -0.212, 0.06), p["steel"]))
    parts.append(profile([(-0.18, 0.10), (0.18, 0.10), (0.18, 0.17), (-0.18, 0.13)], 0.38, p["plastic_k"], bevel=0.008, name="kb", axis="X"))
    for i in range(5):
        for j in range(4):
            parts.append(box((0.04, 0.035, 0.012), (-0.10 + i * 0.05, -0.12 + j * 0.06, 0.14 + j * 0.008), p["plastic_w"]))
    parts.append(rod((0.0, 0.12, 0.12), (0.0, 0.14, 0.30), 0.02, p["plastic_k"]))
    parts.append(box((0.26, 0.03, 0.18), (0, 0.13, 0.36), p["plastic_k"], rot=(math.radians(-15), 0, 0), bevel=0.008))
    parts.append(box((0.22, 0.005, 0.14), (0, 0.112, 0.36), p["screen"], rot=(math.radians(-15), 0, 0)))
    join(parts, "prop_cash_register")
    col("prop_cash_register", (0.42, 0.42, 0.45), (0, 0, 0.225))
    marker("interact", (0, -0.45, 0.2))


def prop_safe():
    reset(); p = P()
    body = mat("safe_body", (0.12, 0.14, 0.13), metal=0.7, rough=0.4, clearcoat=0.2)
    parts = [box((0.80, 0.75, 1.10), (0, 0, 0.58), body, bevel=0.035, seg=3)]
    parts.append(box((0.66, 0.05, 0.92), (0, -0.39, 0.6), body, bevel=0.015, seg=2))  # door
    for z in (0.25, 0.95):  # hinges
        parts.append(cyl(0.03, 0.12, (0.35, -0.40, z), p["steel"], verts=12))
    parts.append(cyl(0.085, 0.04, (-0.05, -0.425, 0.78), p["steel"], rot=(math.radians(90), 0, 0), verts=40, bevel=0.005))  # dial
    for k in range(20):
        a = k * math.pi / 10
        parts.append(box((0.004, 0.006, 0.014), (-0.05 + 0.075 * math.cos(a), -0.447, 0.78 + 0.075 * math.sin(a)), p["black"],
                         rot=(0, -a, 0)))
    parts.append(cyl(0.03, 0.05, (-0.05, -0.45, 0.78), p["gunmetal"], rot=(math.radians(90), 0, 0), verts=16))
    # three-spoke handle
    parts.append(cyl(0.035, 0.06, (-0.05, -0.44, 0.50), p["steel"], rot=(math.radians(90), 0, 0), verts=16))
    for k in range(3):
        a = k * 2 * math.pi / 3 + 0.3
        parts.append(rod((-0.05, -0.46, 0.50), (-0.05 + 0.13 * math.cos(a), -0.46, 0.50 + 0.13 * math.sin(a)), 0.012, p["steel"]))
        parts.append(sphere(0.022, (-0.05 + 0.13 * math.cos(a), -0.46, 0.50 + 0.13 * math.sin(a)), p["steel"], seg=10))
    parts.append(box((0.16, 0.006, 0.04), (0.18, -0.418, 1.0), mat("brass", (0.75, 0.55, 0.2), metal=1.0, rough=0.3)))
    for x in (-0.33, 0.33):
        for y in (-0.3, 0.3):
            parts.append(box((0.1, 0.1, 0.04), (x, y, 0.02), p["black"]))
    join(parts, "prop_safe")
    col("prop_safe", (0.8, 0.8, 1.12), (0, 0, 0.56))
    marker("interact", (-0.05, -0.85, 0.8))
    marker("loot_socket", (0, 0, 0.6))


def prop_workbench():
    reset(); p = P()
    parts = [box((1.8, 0.75, 0.06), (0, 0, 0.92), p["wood_dark"], bevel=0.008)]
    for x in (-0.85, 0.85):
        for y in (-0.32, 0.32):
            parts.append(box((0.06, 0.06, 0.9), (x, y, 0.45), p["gunmetal"], bevel=0.005))
    parts.append(box((1.75, 0.7, 0.03), (0, 0, 0.18), p["wood_dark"]))  # lower shelf
    parts.append(box((1.8, 0.03, 0.9), (0, 0.36, 1.4), mat("pegboard", (0.55, 0.45, 0.32), rough=0.9)))  # pegboard
    for x in (-0.85, 0.85):
        parts.append(box((0.05, 0.05, 1.0), (x, 0.36, 1.4), p["gunmetal"]))
    # hanging tools: wrench, hammer, pistol frame silhouettes
    parts.append(box((0.03, 0.01, 0.25), (-0.6, 0.33, 1.5), p["steel"]))
    parts.append(box((0.03, 0.01, 0.28), (-0.45, 0.33, 1.45), p["wood_raw"]))
    parts.append(box((0.10, 0.03, 0.05), (-0.45, 0.33, 1.60), p["steel"]))
    parts.append(box((0.22, 0.01, 0.12), (0.4, 0.33, 1.5), p["gunmetal"]))
    # vice
    parts.append(box((0.14, 0.18, 0.1), (0.7, -0.25, 1.0), p["teal"], bevel=0.01))
    parts.append(box((0.16, 0.04, 0.06), (0.7, -0.37, 1.02), p["teal"]))
    parts.append(rod((0.7, -0.4, 1.0), (0.7, -0.52, 1.0), 0.01, p["steel"]))
    # bench lamp + parts tray + toolbox
    parts.append(rod((-0.8, 0.25, 0.95), (-0.75, 0.15, 1.35), 0.012, p["gunmetal"]))
    parts.append(cyl(0.08, 0.1, (-0.68, 0.05, 1.33), p["gunmetal"], rot=(math.radians(50), 0, 0), r2=0.04, verts=20))
    parts.append(box((0.35, 0.22, 0.04), (-0.1, -0.12, 0.97), p["galv"], bevel=0.004))
    parts.append(box((0.5, 0.25, 0.25), (0.0, 0.0, 0.33), p["red"], bevel=0.01))
    join(parts, "prop_workbench")
    col("prop_workbench", (1.8, 0.75, 0.95), (0, 0, 0.475))
    marker("interact", (0, -0.8, 1.0))


def prop_laptop_hack():
    reset(); p = P()
    shell = mat("laptop_shell", (0.16, 0.16, 0.18), metal=0.8, rough=0.35)
    parts = [box((0.34, 0.24, 0.018), (0, 0, 0.009), shell, bevel=0.004, seg=3)]
    parts.append(box((0.30, 0.12, 0.002), (0, -0.02, 0.019), p["plastic_k"]))
    parts.append(box((0.34, 0.012, 0.23), (0, 0.13, 0.115), shell, rot=(math.radians(-15), 0, 0), bevel=0.004))
    parts.append(box((0.31, 0.002, 0.2), (0, 0.122, 0.118), mat("screen_hack", (0.01, 0.05, 0.02), emit=(0.1, 0.9, 0.35), emit_strength=1.5),
                     rot=(math.radians(-15), 0, 0)))
    parts.append(box((0.06, 0.03, 0.015), (0.25, 0.0, 0.0075), p["plastic_k"], bevel=0.003))  # dongle
    parts.append(rod((0.22, 0.0, 0.008), (0.17, 0.0, 0.008), 0.003, p["plastic_k"], verts=6))
    join(parts, "prop_laptop_hack")
    col("prop_laptop_hack", (0.36, 0.26, 0.25), (0, 0.02, 0.125))
    marker("interact", (0, -0.4, 0.2))


# ================================================================ STREET / PORT DRESSING


def prop_traffic_cone():
    reset(); p = P()
    parts = [box((0.38, 0.38, 0.035), (0, 0, 0.0175), p["black"], bevel=0.01, seg=2)]
    parts.append(lathe([(0.16, 0.035), (0.15, 0.06), (0.035, 0.70), (0.0, 0.705)], p["orange"], verts=28))
    parts.append(lathe([(0.118, 0.25), (0.096, 0.36)], p["reflect"], verts=28))
    parts.append(lathe([(0.077, 0.45), (0.062, 0.52)], p["reflect"], verts=28))
    join(parts, "prop_traffic_cone")
    col("prop_traffic_cone", (0.36, 0.36, 0.7), (0, 0, 0.35))


def prop_street_lamp():
    reset(); p = P()
    pole = mat("lamp_pole", (0.10, 0.13, 0.13), metal=0.6, rough=0.45)
    parts = [lathe([(0.0, 0.0), (0.16, 0.0), (0.16, 0.08), (0.11, 0.12), (0.09, 0.5), (0.065, 0.55), (0.06, 5.6), (0.0, 5.6)], pole, verts=20)]
    for k in range(4):  # base bolts
        a = k * math.pi / 2 + math.pi / 4
        parts.append(cyl(0.015, 0.04, (0.13 * math.cos(a), 0.13 * math.sin(a), 0.1), p["galv"], verts=8))
    # curved arm (segmented)
    pts = [(0, 0, 5.5), (0.0, -0.4, 5.75), (0.0, -0.9, 5.85), (0.0, -1.5, 5.86)]
    for a, b in zip(pts, pts[1:]):
        parts.append(rod(a, b, 0.045, pole))
    parts.append(box((0.28, 0.6, 0.09), (0, -1.6, 5.82), pole, bevel=0.03, seg=3))  # head
    parts.append(box((0.22, 0.5, 0.02), (0, -1.6, 5.77), p["lamp"]))
    parts.append(box((0.14, 0.006, 0.4), (0, -0.07, 2.6), mat("pole_sticker", (0.85, 0.8, 0.7), rough=0.8)))
    join(parts, "prop_street_lamp")
    col_cyl("prop_street_lamp", 0.1, 5.6, (0, 0, 2.8))
    marker("light", (0, -1.6, 5.7))


def prop_fire_hydrant():
    reset(); p = P()
    paint = mat("hydrant_yellow", (0.85, 0.62, 0.06), metal=0.4, rough=0.45, clearcoat=0.2)
    parts = [lathe([(0.0, 0.0), (0.16, 0.0), (0.16, 0.05), (0.11, 0.07), (0.11, 0.55), (0.13, 0.57), (0.13, 0.62),
                    (0.10, 0.66), (0.06, 0.74), (0.03, 0.78), (0.0, 0.79)], paint, verts=28)]
    for k in range(6):  # flange bolts
        a = k * math.pi / 3
        parts.append(cyl(0.014, 0.03, (0.13 * math.cos(a), 0.13 * math.sin(a), 0.06), p["gunmetal"], verts=6))
    parts.append(cyl(0.022, 0.04, (0, 0, 0.80), p["gunmetal"], verts=5))  # pentagon nut
    for a in (0, math.pi):  # side nozzles
        parts.append(cyl(0.045, 0.09, (0.14 * math.cos(a), 0, 0.42), paint, rot=(0, math.radians(90), 0), verts=20))
        parts.append(cyl(0.05, 0.03, (0.19 * math.cos(a), 0, 0.42), p["gunmetal"], rot=(0, math.radians(90), 0), verts=6))
    parts.append(cyl(0.07, 0.10, (0, -0.15, 0.42), paint, rot=(math.radians(90), 0, 0), verts=20))  # pumper
    parts.append(cyl(0.075, 0.035, (0, -0.21, 0.42), p["gunmetal"], rot=(math.radians(90), 0, 0), verts=6))
    join(parts, "prop_fire_hydrant")
    col_cyl("prop_fire_hydrant", 0.14, 0.8, (0, 0, 0.4))


def prop_bench():
    reset(); p = P()
    iron = mat("cast_iron", (0.06, 0.07, 0.07), metal=0.7, rough=0.5)
    parts = []
    for x in (-0.8, 0.8):  # cast side frames as profiles
        parts.append(profile([(-0.25, 0.0), (-0.18, 0.0), (-0.16, 0.40), (0.18, 0.40), (0.20, 0.0), (0.27, 0.0), (0.24, 0.46),
                              (0.27, 0.86), (0.22, 0.86), (0.19, 0.48), (-0.24, 0.46)], 0.06, iron, loc=(x, 0, 0), bevel=0.01, name="side", axis="X"))
        parts.append(box((0.08, 0.10, 0.05), (x, -0.25, 0.62), iron, bevel=0.01))  # armrest
    for i in range(5):  # seat slats
        parts.append(box((1.8, 0.065, 0.035), (0, -0.20 + i * 0.085, 0.43), p["wood_dark"], bevel=0.008))
    for i in range(3):  # back slats, reclined
        z = 0.56 + i * 0.1
        parts.append(box((1.8, 0.035, 0.07), (0, 0.205 + i * 0.012, z), p["wood_dark"], rot=(math.radians(-8), 0, 0), bevel=0.008))
    join(parts, "prop_bench")
    col("prop_bench_seat", (1.8, 0.5, 0.45), (0, 0, 0.225))
    col("prop_bench_back", (1.8, 0.08, 0.45), (0, 0.22, 0.67))
    marker("sit", (0, -0.05, 0.45))


def prop_trash_bin():
    reset(); p = P()
    parts = [lathe([(0.0, 0.0), (0.22, 0.0), (0.24, 0.85), (0.26, 0.88), (0.26, 0.92), (0.0, 0.92)], p["teal"], verts=28)]
    for k in range(12):  # vertical slats look
        a = k * math.pi / 6
        parts.append(box((0.03, 0.02, 0.8), (0.235 * math.cos(a), 0.235 * math.sin(a), 0.45), p["teal"], rot=(0, 0, a)))
    parts.append(lathe([(0.0, 0.92), (0.27, 0.92), (0.27, 0.96), (0.22, 1.02), (0.0, 1.04)], p["gunmetal"], verts=28))  # dome lid
    parts.append(box((0.22, 0.05, 0.1), (0, -0.25, 0.97), p["black"]))  # throw slot
    parts.append(box((0.16, 0.005, 0.16), (0, -0.245, 0.6), mat("decal_white", (0.85, 0.85, 0.82), rough=0.7)))
    join(parts, "prop_trash_bin")
    col_cyl("prop_trash_bin", 0.26, 1.04, (0, 0, 0.52))


def prop_bus_shelter():
    reset(); p = P()
    frame = mat("shelter_frame", (0.12, 0.13, 0.14), metal=0.8, rough=0.35)
    W, D, H = 3.2, 1.4, 2.5
    parts = []
    for x in (-W / 2, W / 2):
        for y in (-D / 2 + 0.05, D / 2 - 0.05):
            parts.append(box((0.08, 0.08, H), (x, y, H / 2), frame, bevel=0.01))
    parts.append(profile([(-D / 2 - 0.2, H), (D / 2 + 0.05, H + 0.12), (D / 2 + 0.05, H + 0.18), (-D / 2 - 0.2, H + 0.06)], W + 0.3, frame,
                         bevel=0.01, name="roof", axis="X"))
    parts.append(box((W, 0.02, H - 0.25), (0, D / 2 - 0.05, H / 2 + 0.05), p["glass"]))  # back glass
    for x in (-W / 2, W / 2):
        parts.append(box((0.02, D - 0.4, H - 0.25), (x, 0.15, H / 2 + 0.05), p["glass"]))
    # ad panel (lit), bench, route sign
    parts.append(box((0.12, 1.2, 1.8), (W / 2 + 0.08, 0.05, 1.2), frame, bevel=0.01))
    parts.append(box((0.01, 1.05, 1.6), (W / 2 + 0.145, 0.05, 1.2), mat("ad_glow", (0.9, 0.85, 0.7), emit=(1.0, 0.85, 0.6), emit_strength=2.5)))
    parts.append(box((2.0, 0.35, 0.04), (-0.3, 0.45, 0.45), p["steel"], bevel=0.008))
    for x in (-1.1, 0.5):
        parts.append(box((0.04, 0.3, 0.43), (x, 0.45, 0.22), frame))
    parts.append(box((0.5, 0.04, 0.3), (-W / 2 + 0.3, -D / 2 - 0.15, H + 0.3), p["teal"], bevel=0.01))
    parts.append(rod((-W / 2 + 0.3, -D / 2 - 0.15, H + 0.12), (-W / 2 + 0.3, -D / 2 - 0.15, H + 0.15), 0.02, frame))
    join(parts, "prop_bus_shelter")
    col("prop_bus_shelter_back", (W, 0.1, H), (0, D / 2 - 0.05, H / 2))
    col("prop_bus_shelter_roof", (W + 0.3, D + 0.25, 0.15), (0, -0.07, H + 0.09))
    col("prop_bus_shelter_ad", (0.15, 1.2, 2.1), (W / 2 + 0.08, 0.05, 1.05))


def prop_parking_meter():
    reset(); p = P()
    parts = [rod((0, 0, 0), (0, 0, 1.05), 0.035, p["gunmetal"], verts=16)]
    parts.append(box((0.10, 0.10, 0.02), (0, 0, 0.01), p["gunmetal"]))
    head = mat("meter_head", (0.55, 0.57, 0.6), metal=0.8, rough=0.35)
    parts.append(box((0.18, 0.14, 0.30), (0, 0, 1.2), head, bevel=0.03, seg=3))
    parts.append(sphere(0.09, (0, 0, 1.35), head, scale=(1, 0.75, 0.6), seg=20))
    parts.append(box((0.12, 0.01, 0.07), (0, -0.072, 1.27), p["screen"]))
    parts.append(box((0.03, 0.01, 0.006), (0, -0.072, 1.18), p["black"]))  # coin slot
    parts.append(box((0.05, 0.01, 0.05), (0, -0.072, 1.11), p["hazard"]))
    join(parts, "prop_parking_meter")
    col("prop_parking_meter", (0.18, 0.15, 1.42), (0, 0, 0.71))
    marker("interact", (0, -0.4, 1.2))


def prop_newspaper_box():
    reset(); p = P()
    paint = mat("news_blue", (0.06, 0.18, 0.45), metal=0.4, rough=0.45, clearcoat=0.3)
    parts = [box((0.45, 0.42, 0.55), (0, 0, 0.62), paint, bevel=0.015, seg=2)]
    parts.append(profile([(-0.21, 0.89), (0.21, 0.89), (0.21, 0.98), (-0.21, 0.93)], 0.46, paint, bevel=0.008, name="top", axis="X"))
    parts.append(box((0.36, 0.01, 0.22), (0, -0.215, 0.72), p["glass"]))
    parts.append(box((0.30, 0.005, 0.16), (0, -0.21, 0.72), mat("newsprint", (0.85, 0.83, 0.78), rough=0.95)))
    parts.append(box((0.24, 0.03, 0.03), (0, -0.23, 0.58), p["steel"]))  # handle
    parts.append(box((0.08, 0.02, 0.06), (0.14, -0.22, 0.88), p["steel"]))  # coin mech
    for x in (-0.17, 0.17):
        parts.append(box((0.04, 0.38, 0.35), (x, 0, 0.175), p["gunmetal"]))
    join(parts, "prop_newspaper_box")
    col("prop_newspaper_box", (0.46, 0.43, 0.98), (0, 0, 0.49))


def prop_dock_bollard():
    reset(); p = P()
    iron = mat("bollard_iron", (0.07, 0.07, 0.075), metal=0.7, rough=0.55)
    parts = [box((0.6, 0.6, 0.06), (0, 0, 0.03), iron, bevel=0.015)]
    parts.append(lathe([(0.0, 0.06), (0.22, 0.06), (0.18, 0.12), (0.15, 0.45), (0.19, 0.52), (0.24, 0.60), (0.24, 0.66), (0.0, 0.66)], iron, verts=32))
    parts.append(cyl(0.245, 0.03, (0, 0, 0.62), p["hazard"], verts=32))
    for k in range(4):
        a = k * math.pi / 2 + math.pi / 4
        parts.append(cyl(0.03, 0.04, (0.24 * math.cos(a), 0.24 * math.sin(a), 0.07), p["gunmetal"], verts=6))
    rope = mat("rope_hemp", (0.62, 0.52, 0.34), rough=1.0)
    parts.append(torus(0.19, 0.03, (0, 0, 0.30), rope, major=32, minor=10))
    parts.append(torus(0.18, 0.03, (0, 0, 0.36), rope, rot=(math.radians(6), 0, 0), major=32, minor=10))
    parts.append(rod((0.0, -0.18, 0.33), (0.0, -1.2, 0.05), 0.03, rope, verts=10))
    join(parts, "prop_dock_bollard")
    col_cyl("prop_dock_bollard", 0.24, 0.66, (0, 0, 0.33))


def prop_life_ring_stand():
    reset(); p = P()
    parts = [rod((0, 0.05, 0), (0, 0.05, 1.5), 0.03, p["galv"], verts=16)]
    parts.append(box((0.18, 0.18, 0.02), (0, 0.05, 0.01), p["galv"]))
    parts.append(box((0.7, 0.04, 0.7), (0, 0.09, 1.05), p["plastic_w"], bevel=0.01))  # backing board
    ring_o = mat("ring_orange", (0.95, 0.30, 0.05), rough=0.5)
    parts.append(torus(0.26, 0.06, (0, 0.0, 1.05), ring_o, rot=(math.radians(90), 0, 0), major=40, minor=14))
    for k in range(4):  # white bands
        a = k * math.pi / 2 + math.pi / 4
        parts.append(cyl(0.065, 0.06, (0.26 * math.cos(a), 0.0, 1.05 + 0.26 * math.sin(a)), p["plastic_w"],
                         rot=(0, -a + math.pi / 2, 0), verts=16))
    parts.append(box((0.04, 0.04, 0.12), (0, 0.04, 1.33), p["galv"]))  # hook
    parts.append(box((0.5, 0.005, 0.1), (0, 0.068, 1.48), mat("sign_red", (0.75, 0.08, 0.06), rough=0.6)))
    join(parts, "prop_life_ring_stand")
    col("prop_life_ring_stand", (0.7, 0.2, 1.5), (0, 0.05, 0.75))


def prop_road_barricade():
    reset(); p = P()
    parts = []
    stripe = [p["orange"], p["plastic_w"]]
    for z in (0.55, 0.85):  # striped rails
        for i in range(6):
            parts.append(box((0.25, 0.04, 0.18), (-0.625 + i * 0.25, -0.12, z), stripe[i % 2], bevel=0.005))
    for x in (-0.65, 0.65):  # A-frame legs
        for sy in (-1, 1):
            parts.append(rod((x, -0.12, 0.95), (x, -0.12 + sy * 0.38, 0.0), 0.025, p["plastic_w"]))
        parts.append(box((0.08, 0.85, 0.04), (x, -0.12, 0.02), p["plastic_k"]))
    parts.append(box((0.12, 0.04, 0.12), (-0.55, -0.15, 1.02), mat("amber_lamp", (1, 0.6, 0.1), emit=(1.0, 0.55, 0.05), emit_strength=4.0), bevel=0.02))
    join(parts, "prop_road_barricade")
    col("prop_road_barricade", (1.5, 0.12, 1.0), (0, -0.12, 0.5))


# ---------------------------------------------------------------- registry

# id -> (builder, category, gameplay tag, short Korean description)
PROPS = {
    "prop_crate_wood": (prop_crate_wood, "cover", "cover_high", "나무 상자 1.2m — 하이 커버, 파괴 가능"),
    "prop_crate_stack": (prop_crate_stack, "cover", "cover_high", "상자 더미 — 하이 커버, 올라설 수 있음"),
    "prop_barrel_explosive": (prop_barrel_explosive, "cover", "explosive", "빨간 드럼통 — 사격 시 폭발"),
    "prop_barrel_oil": (prop_barrel_oil, "cover", "cover_low", "파란 드럼통 — 로우 커버, 관통됨"),
    "prop_concrete_barrier": (prop_concrete_barrier, "cover", "cover_low", "저지형 콘크리트 방호벽 2m — 로우 커버, 관통 불가"),
    "prop_sandbag_wall": (prop_sandbag_wall, "cover", "cover_low", "모래주머니 호형 벽 — 로우 커버"),
    "prop_dumpster": (prop_dumpster, "cover", "cover_high", "대형 쓰레기통 — 하이 커버, 밀 수 있음"),
    "prop_shipping_container": (prop_shipping_container, "cover", "cover_full", "20피트 선적 컨테이너 — 벽, 문 열림"),
    "prop_pallet_stack": (prop_pallet_stack, "cover", "cover_high", "랩 감은 팔레트 화물 — 하이 커버"),
    "prop_gas_cylinder_cage": (prop_gas_cylinder_cage, "cover", "explosive", "가스통 보관함 — 연쇄 폭발"),
    "prop_cable_spool": (prop_cable_spool, "cover", "cover_high", "케이블 드럼 — 하이 커버, 굴릴 수 있음"),
    "prop_scaffold_section": (prop_scaffold_section, "cover", "vantage", "비계 한 칸 — 고지대 포지션"),
    "prop_ammo_box": (prop_ammo_box, "pickup", "pickup_ammo", "탄약 상자 — 탄약 보급"),
    "prop_medkit": (prop_medkit, "pickup", "pickup_health", "구급 상자 — 체력 회복"),
    "prop_armor_vest": (prop_armor_vest, "pickup", "pickup_armor", "방탄 조끼 — 방어구 충전"),
    "prop_cash_bag": (prop_cash_bag, "loot", "loot_carry", "현금 가방 — 습격 전리품, 들고 뛰면 느려짐"),
    "prop_cash_stack": (prop_cash_stack, "loot", "loot_small", "현금 다발 — 즉시 획득"),
    "prop_bond_case": (prop_bond_case, "loot", "loot_carry", "채권 서류가방 — 고가 전리품"),
    "prop_weapon_crate": (prop_weapon_crate, "loot", "supply_drop", "무기 보급 상자 — 공중 보급/이벤트"),
    "prop_atm": (prop_atm, "economy", "atm", "ATM — 입출금, 해킹 대상"),
    "prop_vending_machine": (prop_vending_machine, "economy", "shop_small", "자판기 — 소모품 구매"),
    "prop_cash_register": (prop_cash_register, "economy", "register", "계산대 — 상점 강도 대상"),
    "prop_safe": (prop_safe, "economy", "safe", "금고 — 습격 목표, 드릴/다이얼"),
    "prop_workbench": (prop_workbench, "economy", "workbench", "작업대 — 무기 개조"),
    "prop_laptop_hack": (prop_laptop_hack, "economy", "hack_terminal", "해킹 노트북 — 습격 준비 미션"),
    "prop_traffic_cone": (prop_traffic_cone, "street", "decor_physics", "라바콘 — 물리 소품"),
    "prop_street_lamp": (prop_street_lamp, "street", "decor_light", "가로등 5.6m — 조명"),
    "prop_fire_hydrant": (prop_fire_hydrant, "street", "decor_breakable", "소화전 — 차로 치면 물 분출"),
    "prop_bench": (prop_bench, "street", "decor_sit", "공원 벤치 — 앉기"),
    "prop_trash_bin": (prop_trash_bin, "street", "decor_physics", "가로 쓰레기통"),
    "prop_bus_shelter": (prop_bus_shelter, "street", "cover_high", "버스 정류장 — 유리 파괴"),
    "prop_parking_meter": (prop_parking_meter, "street", "decor_breakable", "주차 미터기 — 부수면 잔돈"),
    "prop_newspaper_box": (prop_newspaper_box, "street", "cover_low", "신문 가판함 — 로우 커버"),
    "prop_dock_bollard": (prop_dock_bollard, "port", "cover_low", "부두 계류주 — 로우 커버"),
    "prop_life_ring_stand": (prop_life_ring_stand, "port", "decor", "구명환 거치대"),
    "prop_road_barricade": (prop_road_barricade, "street", "decor_physics", "도로 바리케이드 — 경찰 검문"),
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, help="props dir (Set repo)")
    ap.add_argument("--only", default="", help="comma-separated prop ids")
    ap.add_argument("--manifest", default=None, help="write prop manifest json here")
    a = ap.parse_args(argv)
    ids = [s for s in a.only.split(",") if s] or list(PROPS)
    for pid in ids:
        PROPS[pid][0]()
        export(os.path.abspath(os.path.join(a.out, f"{pid}.glb")))
    if a.manifest:
        man = [{"id": k, "category": v[1], "tag": v[2], "description": v[3], "asset": f"props/{k}.glb"} for k, v in PROPS.items()]
        with open(a.manifest, "w", encoding="utf-8") as f:
            json.dump({"props": man}, f, ensure_ascii=False, indent=2)
            f.write("\n")


if __name__ == "__main__":
    main()
