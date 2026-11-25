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
    The pilot tug uses the waterline at z=0 for placement.
  * Godot generates LODs on import; target ~6k visual tris for props and ~10k for landmarks.

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


# ================================================================ DOCK KIT


def dock_finish(parts, name):
    visual = join(parts, name)
    _active(visual)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def prop_dock_power_pedestal():
    reset()
    p = P()
    parts = [
        box((0.52, 0.42, 0.06), (0, 0, 0.03), p["gunmetal"], bevel=0.012),
        box((0.29, 0.24, 0.72), (0, 0.035, 0.42), p["teal"], bevel=0.018),
        box((0.46, 0.35, 0.53), (0, 0.02, 0.865), p["teal"], bevel=0.025),
        box((0.49, 0.38, 0.05), (0, 0.015, 1.135), p["galv"], bevel=0.015),
        box((0.395, 0.032, 0.425), (0, -0.155, 0.872), p["gunmetal"], bevel=0.012),
        box((0.20, 0.023, 0.063), (0, -0.174, 1.005), p["hazard"], bevel=0.005),
    ]
    for x in (-0.11, 0.11):
        socket_axis = (math.pi / 2, 0, 0)
        parts.extend((
            cyl(0.064, 0.024, (x, -0.178, 0.865), p["galv"], rot=socket_axis, verts=16),
            cyl(0.045, 0.022, (x, -0.195, 0.865), p["black"], rot=socket_axis, verts=16),
            box((0.048, 0.018, 0.02), (x, -0.179, 0.774), p["screen"], bevel=0.004),
        ))
    for x in (-0.20, 0.20):
        for y in (-0.15, 0.15):
            parts.append(cyl(0.023, 0.02, (x, y, 0.061), p["galv"], verts=6))
    dock_finish(parts, "prop_dock_power_pedestal")
    col("prop_dock_power_pedestal", (0.52, 0.42, 1.16), (0, 0, 0.58))


def prop_dock_access_gate():
    reset()
    p = P()
    parts = []
    for x in (-1.17, 1.17):
        parts.extend((
            box((0.26, 0.36, 0.07), (x, 0, 0.035), p["gunmetal"], bevel=0.01),
            box((0.14, 0.14, 1.93), (x, 0, 1.005), p["galv"], bevel=0.012),
            box((0.16, 0.16, 0.04), (x, 0, 1.98), p["hazard"], bevel=0.005),
            box((0.13, 0.018, 0.26), (x, -0.075, 0.96), p["hazard"], bevel=0.004),
        ))
    for x in (-1.04, 1.04):
        parts.append(box((0.07, 0.09, 1.53), (x, 0, 0.97), p["teal"], bevel=0.006))
    for z in (0.23, 0.92, 1.71):
        parts.append(box((2.08, 0.09, 0.07), (0, 0, z), p["teal"], bevel=0.006))
    for i in range(-7, 8):
        parts.append(box((0.025, 0.04, 1.43), (i * 0.13, 0, 0.97), p["galv"], bevel=0.003))
    parts.extend((
        box((0.45, 0.035, 0.27), (0.70, -0.046, 1.18), p["gunmetal"], bevel=0.012),
        box((0.37, 0.012, 0.065), (0.70, -0.067, 1.24), p["hazard"], bevel=0.004),
        box((0.12, 0.01, 0.045), (0.70, -0.068, 1.12), p["reflect"], bevel=0.003),
    ))
    dock_finish(parts, "prop_dock_access_gate")
    col("prop_dock_access_gate", (2.60, 0.36, 2.00), (0, 0, 1.00))


def prop_dock_safety_rail():
    reset()
    p = P()
    parts = []
    for x in (-1.08, 0, 1.08):
        parts.extend((
            box((0.24, 0.30, 0.055), (x, 0, 0.0275), p["gunmetal"], bevel=0.009),
            box((0.08, 0.08, 1.01), (x, 0, 0.545), p["hazard"], bevel=0.014),
            box((0.095, 0.095, 0.16), (x, 0, 0.14), p["teal"], bevel=0.01),
        ))
    for z in (0.53, 1.06):
        parts.append(box((2.32, 0.08, 0.08), (0, 0, z), p["hazard"], bevel=0.025, seg=3))
        for x in (-1.09, 1.09):
            parts.append(box((0.09, 0.085, 0.085), (x, 0, z), p["gunmetal"], bevel=0.015))
    parts.append(box((2.29, 0.035, 0.12), (0, -0.02, 0.15), p["teal"], bevel=0.01))
    dock_finish(parts, "prop_dock_safety_rail")
    col("prop_dock_safety_rail", (2.40, 0.30, 1.10), (0, 0, 0.55))


def prop_mooring_fender():
    reset()
    p = P()
    parts = [
        box((1.18, 0.14, 0.08), (0, 0.15, 0.04), p["gunmetal"], bevel=0.015),
        box((1.09, 0.13, 0.75), (0, 0.145, 0.445), p["galv"], bevel=0.025),
        box((1.08, 0.27, 0.66), (0, -0.052, 0.39), p["black"], bevel=0.063, seg=3),
    ]
    for x in (-0.39, -0.13, 0.13, 0.39):
        parts.append(box((0.18, 0.12, 0.54), (x, -0.22, 0.39), p["plastic_k"], bevel=0.04, seg=3))
    for x in (-0.47, 0.47):
        parts.extend((
            box((0.10, 0.13, 0.095), (x, 0.065, 0.7525), p["hazard"], bevel=0.012),
            cyl(0.031, 0.046, (x, 0.075, 0.792), p["steel"], verts=8),
        ))
    dock_finish(parts, "prop_mooring_fender")
    col("prop_mooring_fender", (1.18, 0.50, 0.82), (0, -0.03, 0.41))


def prop_loading_ramp():
    reset()
    p = P()
    ramp_shape = [(-1.20, 0), (1.20, 0), (1.20, 0.48), (0.88, 0.48), (-1.20, 0.025)]
    parts = [profile(ramp_shape, 1.45, p["gunmetal"], axis="X", bevel=0.012)]
    slope = 0.455 / 2.08
    for i in range(9):
        y = -1.03 + i * 0.24
        height = 0.035 + (y + 1.20) * slope
        parts.append(box((1.24, 0.065, 0.021), (0, y, height), p["galv"], bevel=0.004))
    for x in (-0.685, 0.685):
        parts.append(profile([(-1.16, 0.04), (0.88, 0.49), (1.20, 0.49),
                              (1.20, 0.54), (0.88, 0.54), (-1.16, 0.08)],
                             0.067, p["hazard"], (x, 0, 0), bevel=0.004, axis="X"))
    parts.append(box((1.32, 0.23, 0.018), (0, 1.045, 0.485), p["galv"], bevel=0.005))
    dock_finish(parts, "prop_loading_ramp")
    collision = profile(ramp_shape, 1.45, axis="X", name="prop_loading_ramp-colonly")
    collision.data.name = collision.name


def prop_dock_cargo_net():
    reset()
    p = P()
    parts = []
    for x in (-0.52, 0, 0.52):
        parts.append(box((0.10, 1.00, 0.11), (x, 0, 0.055), p["wood_dark"], bevel=0.008))
    for y in (-0.4, -0.2, 0, 0.2, 0.4):
        parts.append(box((1.24, 0.16, 0.045), (0, y, 0.1325), p["wood_raw"], bevel=0.005))
    parts.extend((
        box((1.09, 0.85, 0.40), (0, 0, 0.355), p["canvas"], bevel=0.036),
        box((0.87, 0.69, 0.24), (0.06, 0.01, 0.675), p["teal"], bevel=0.032),
    ))
    for y in (-0.32, -0.16, 0, 0.16, 0.32):
        points = [(-0.56, y, 0.16), (-0.56, y, 0.52), (-0.36, y, 0.806),
                  (0.48, y, 0.806), (0.56, y, 0.52), (0.56, y, 0.16)]
        for start, end in zip(points, points[1:]):
            parts.append(rod(start, end, 0.014, p["orange"], verts=8))
    for x in (-0.35, -0.18, 0, 0.18, 0.35, 0.47):
        points = [(x, -0.44, 0.16), (x, -0.44, 0.52), (x, -0.324, 0.814),
                  (x, 0.344, 0.814), (x, 0.44, 0.52), (x, 0.44, 0.16)]
        for start, end in zip(points, points[1:]):
            parts.append(rod(start, end, 0.014, p["orange"], verts=8))
    dock_finish(parts, "prop_dock_cargo_net")
    col("prop_dock_cargo_net_base", (1.24, 1.00, 0.555), (0, 0, 0.2775))
    col("prop_dock_cargo_net_top", (0.87, 0.69, 0.27), (0.06, 0.01, 0.69))


def prop_harbor_gantry_crane():
    reset()
    p = P()
    paint = mat("crane_mustard", (0.68, 0.40, 0.048), metal=0.36, rough=0.49)
    shadow = mat("crane_oxide", (0.36, 0.16, 0.036), metal=0.38, rough=0.63)
    parts = []
    for column, x in enumerate((-2.63, 2.63)):
        for side, y in enumerate((-1.62, 1.62)):
            parts.extend((
                box((0.98, 0.86, 0.24), (x, y, 0.12), p["concrete_dark"], bevel=0.032),
                box((0.72, 0.64, 0.085), (x, y, 0.25), p["gunmetal"], bevel=0.018),
                box((0.37, 0.43, 6.59), (x, y, 3.56), paint, bevel=0.034),
                box((0.39, 0.18, 5.7), (x, y, 3.43), shadow, bevel=0.014),
                box((0.49, 0.51, 0.20), (x, y, 6.62), p["gunmetal"], bevel=0.015),
            ))
            for dx in (-0.28, 0.28):
                for dy in (-0.24, 0.24):
                    parts.append(cyl(0.052, 0.060, (x + dx, y + dy, 0.315), p["galv"], verts=6))
            for i in range(3):
                parts.append(box((0.30, 0.016, 0.15), (x, y - 0.219, 0.66 + 0.26 * i),
                                 p["gunmetal"], rot=(0, -0.38, 0), bevel=0.004))
            col(f"crane_leg_{column}_{side}", (0.98, 0.86, 0.48), (x, y, 0.24))
            col(f"crane_column_{column}_{side}", (0.40, 0.46, 6.45), (x, y, 3.56))
        parts.append(box((0.48, 3.68, 0.54), (x, 0, 6.49), paint, bevel=0.03))
        for sign in (-1, 1):
            parts.append(rod((x, sign * 1.62, 4.54), (x, sign * 0.62, 6.45), 0.095, shadow, verts=8))
    for y in (-1.42, 1.42):
        for z in (6.58, 7.10):
            parts.append(box((6.4, 0.29, 0.19), (0, y, z), paint, bevel=0.024))
        for i in range(8):
            x = -2.9 + i * 0.725
            lower, upper = (6.60, 7.08) if i % 2 == 0 else (7.08, 6.60)
            parts.append(rod((x, y, lower), (x + 0.725, y, upper), 0.051, shadow, verts=8))
        for x in (-2.97, -1.45, 0, 1.45, 2.97):
            parts.append(box((0.10, 0.31, 0.51), (x, y, 6.84), paint, bevel=0.01))
    for x in (-2.9, -1.45, 0, 1.45, 2.9):
        parts.append(box((0.16, 3.05, 0.14), (x, 0, 7.1), shadow, bevel=0.016))
    for y in (-0.54, 0.54):
        parts.append(box((5.35, 0.11, 0.11), (0, y, 7.19), p["gunmetal"], bevel=0.013))
    parts.extend((
        box((0.92, 1.41, 0.26), (0.65, 0, 7.27), p["gunmetal"], bevel=0.037),
        box((0.64, 0.57, 0.50), (0.65, 0, 6.93), p["teal"], bevel=0.06),
        cyl(0.23, 0.54, (0.65, 0, 6.61), p["gunmetal"], rot=(math.pi / 2, 0, 0), verts=20),
    ))
    for y in (-0.17, 0.17):
        parts.append(rod((0.65, y, 6.61), (0.65, y, 4.76), 0.025, p["gunmetal"], verts=8))
    parts.append(box((0.36, 0.49, 0.27), (0.65, 0, 4.69), p["hazard"], bevel=0.04))
    hook = [(0.50, 4.62), (0.69, 4.62), (0.69, 4.46), (0.58, 4.38), (0.58, 4.28),
            (0.65, 4.22), (0.78, 4.24), (0.85, 4.32), (0.93, 4.39), (0.93, 4.18),
            (0.80, 4.07), (0.59, 4.07), (0.44, 4.19), (0.41, 4.39), (0.50, 4.50)]
    parts.append(profile(hook, 0.13, p["gunmetal"], bevel=0.016))
    # Service ladder stays inside one leg assembly.
    for y in (1.38, 1.81):
        parts.append(rod((2.39, y, 0.38), (2.39, y, 6.49), 0.031, p["galv"], verts=8))
    for z in [0.56 + i * 0.33 for i in range(18)]:
        parts.append(rod((2.39, 1.38, z), (2.39, 1.81, z), 0.028, p["galv"], verts=8))
    parts.extend((
        box((0.48, 0.24, 0.67), (2.36, -1.71, 1.30), p["teal"], bevel=0.026),
        box((0.29, 0.025, 0.20), (2.36, -1.84, 1.42), p["gunmetal"], bevel=0.009),
        box((0.72, 0.07, 0.34), (-2.33, -1.59, 6.82), p["gunmetal"], bevel=0.016),
        box((0.55, 0.018, 0.072), (-2.33, -1.63, 6.87), p["hazard"], bevel=0.008),
    ))
    for x in (-2.63, 2.63):
        parts.append(box((0.24, 0.18, 0.13), (x, -1.59, 6.25), p["lamp"], bevel=0.018))
    dock_finish(parts, "prop_harbor_gantry_crane")
    col("crane_overhead", (6.4, 3.30, 1.02), (0, 0, 6.90))


def tug_hull(stations, palette):
    """Loft transom-to-stem sections in Blender coordinates and assign painted strakes."""
    # The angular lower two rows suggest chines without smoothing away the hull shape.
    sections = []
    for y, beam, sheer, keel in stations:
        depth = 0.035 - keel
        rings = [(0, keel), (beam * 0.69, keel + depth * 0.31), (beam * 0.91, keel + depth * 0.70),
                 (beam, 0.035), (beam * 0.99, 0.19), (beam * 0.90, sheer)]
        sections.append([(-x, y, z) for x, z in reversed(rings[1:])] +
                        [(x, y, z) for x, z in rings])
    mesh = bpy.data.meshes.new("pilot_tug_loft")
    row = len(sections[0])
    faces = [tuple(reversed(range(row)))]
    material_ids = [2]
    bands = (2, 3, 1, 0, 0, 0, 0, 1, 3, 2)
    for i in range(len(sections) - 1):
        a, b = i * row, (i + 1) * row
        for j in range(row - 1):
            faces.append((a + j, b + j, b + j + 1, a + j + 1))
            material_ids.append(bands[j])
        faces.append((a, a + row - 1, b + row - 1, b))
        material_ids.append(4)
    faces.append(tuple(range((len(sections) - 1) * row, len(sections) * row)))
    material_ids.append(2)
    mesh.from_pydata([point for section in sections for point in section], [], faces)
    for material in palette:
        mesh.materials.append(material)
    for face, index in zip(mesh.polygons, material_ids):
        face.material_index = index
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new("pilot_tug_loft", mesh)
    bpy.context.scene.collection.objects.link(obj)
    return _finish(obj, None, 0.026)


def prop_harbor_pilot_tug():
    reset()
    p = P()
    red = mat("tug_antifouling", (0.30, 0.06, 0.039), rough=0.63)
    blue = mat("tug_deep_blue", (0.035, 0.145, 0.20), metal=0.29, rough=0.48)
    cream = mat("tug_warm_white", (0.79, 0.72, 0.56), rough=0.5)
    deck = mat("tug_deck_gray", (0.19, 0.23, 0.23), rough=0.86)
    windows = mat("tug_glazing", (0.046, 0.13, 0.18), metal=0.29, rough=0.2, clearcoat=0.6)
    green_light = mat("navigation_green", (0.025, 0.45, 0.09), emit=(0.025, 0.45, 0.09))
    stations = [(4.68, 1.36, 0.78, -0.36), (4.22, 1.56, 0.75, -0.55),
                (2.65, 1.64, 0.70, -0.76), (0, 1.67, 0.70, -0.80),
                (-2.45, 1.55, 0.86, -0.72), (-3.62, 1.19, 1.02, -0.54),
                (-4.48, 0.63, 1.19, -0.37), (-4.94, 0.13, 1.30, -0.25)]
    parts = [tug_hull(stations, (red, p["black"], blue, cream, deck))]
    for sign in (-1, 1):
        # Dark rubbing strip follows the hull; thin white line is the painted boot top.
        rim = [(sign * beam * 0.90, y, sheer - 0.035) for y, beam, sheer, _ in stations]
        for a, b in zip(rim, rim[1:]):
            parts.append(rod(a, b, 0.062, p["black"], verts=10))
        posts = [(sign * (beam * 0.90 - 0.08), y, sheer) for y, beam, sheer, _ in stations[1:-1]]
        for x, y, z in posts:
            parts.append(rod((x, y, z), (x, y, z + 0.67), 0.029, p["galv"], verts=8))
        for a, b in zip(posts, posts[1:]):
            for h in (0.37, 0.67):
                parts.append(rod((a[0], a[1], a[2] + h), (b[0], b[1], b[2] + h),
                                 0.025, p["galv"], verts=8))
        for y in (2.25, 1.11, -0.38, -1.55):
            parts.append(torus(0.205, 0.075, (sign * 1.53, y, 0.52), p["black"],
                               rot=(0, math.pi / 2, 0), major=20, minor=6, name="rubber_fender"))
            parts.append(rod((sign * 1.52, y, 0.77), (sign * 1.41, y, 0.94),
                             0.022, p["gunmetal"], verts=8))
    # Wheelhouse volume and raked windscreen, with a separate roof overhang.
    lower = [(-1.77, 0.68), (1.64, 0.68), (1.64, 1.59), (-1.47, 1.59), (-1.77, 1.34)]
    upper = [(-1.40, 1.53), (0.96, 1.53), (0.96, 2.78), (-1.03, 2.78), (-1.40, 2.19)]
    parts.extend((
        profile(lower, 2.31, cream, axis="X", bevel=0.055, seg=3),
        profile(upper, 1.95, cream, axis="X", bevel=0.044, seg=3),
        box((2.17, 2.19, 0.16), (0, -0.09, 2.79), blue, bevel=0.056, seg=3),
        box((2.29, 2.32, 0.063), (0, -0.1, 2.88), cream, bevel=0.025),
    ))
    windscreen_angle = math.atan2(0.37, 0.59)
    for x in (-0.61, 0, 0.61):
        parts.append(box((0.53, 0.045, 0.57), (x, -1.30, 2.41), windows,
                         rot=(-windscreen_angle, 0, 0), bevel=0.02))
    for sign in (-1, 1):
        for y in (-0.67, 0.05, 0.66):
            parts.append(box((0.034, 0.51, 0.48), (sign * 0.979, y, 2.34), windows, bevel=0.03))
        parts.extend((
            box((0.047, 0.59, 0.68), (sign * 1.159, 0.95, 1.14), blue, bevel=0.027),
            box((0.059, 0.19, 0.055), (sign * 1.18, 0.78, 1.16), p["galv"], bevel=0.014),
            box((0.042, 0.90, 0.24), (sign * 1.163, -0.43, 1.15), p["teal"], bevel=0.021),
            box((0.04, 0.45, 0.037), (sign * 1.18, -0.45, 1.16), cream, bevel=0.008),
        ))
    for x in (-0.53, 0.53):
        parts.append(box((0.77, 0.044, 0.37), (x, 0.983, 2.29), windows, bevel=0.024))
    # Foredeck and stern fittings, aerials, and navigation lights.
    for y, z in ((-3.39, 0.99), (3.56, 0.77)):
        parts.extend((
            box((0.71, 0.39, 0.12), (0, y, z + 0.035), p["gunmetal"], bevel=0.022),
            cyl(0.22, 0.30, (0, y, z + 0.22), p["galv"], verts=18, bevel=0.018),
            cyl(0.26, 0.075, (0, y, z + 0.37), p["gunmetal"], verts=18, bevel=0.015),
        ))
    parts.extend((
        box((0.71, 0.35, 0.12), (0, 0.18, 3.0), blue, bevel=0.03),
        rod((0, 0.18, 2.97), (0, 0.18, 3.88), 0.067, p["gunmetal"]),
        rod((-0.65, 0.18, 3.64), (0.65, 0.18, 3.64), 0.032, p["gunmetal"], verts=8),
        box((0.85, 0.15, 0.072), (0, 0.18, 3.86), cream, bevel=0.026),
        rod((0.22, 0.26, 3.89), (0.26, 0.30, 4.37), 0.018, p["galv"], verts=8),
        cyl(0.096, 0.13, (0, 0.18, 4.0), p["lamp"], verts=12),
        cyl(0.19, 0.57, (0.67, 1.55, 2.61), p["gunmetal"], verts=16, bevel=0.018),
        cyl(0.21, 0.09, (0.67, 1.55, 2.94), p["black"], verts=16, bevel=0.013),
        box((0.16, 0.13, 0.09), (-0.63, 0.18, 3.70), p["red"], bevel=0.017),
        box((0.16, 0.13, 0.09), (0.63, 0.18, 3.70), green_light, bevel=0.017),
    ))
    dock_finish(parts, "prop_harbor_pilot_tug")
    col("tug_deck", (2.8, 6.8, 0.93), (0, 0.78, 0.24))
    col("tug_foredeck", (1.40, 2.20, 1.05), (0, -3.10, 0.40))
    col("tug_cabin", (2.34, 3.27, 2.30), (0, -0.05, 1.78))
    marker("boarding_port", (-1.45, 2.62, 0.76))
    marker("boarding_starboard", (1.45, 2.62, 0.76))


def prop_dock_inspection_terminal():
    reset()
    p = P()
    parts = [
        box((0.82, 0.68, 0.10), (0, 0, 0.05), p["gunmetal"], bevel=0.047, seg=3),
        box((0.49, 0.39, 0.11), (0, 0.045, 0.125), p["galv"], bevel=0.025),
        box((0.36, 0.33, 0.82), (0, 0.075, 0.515), p["teal"], bevel=0.058, seg=3),
        box((0.30, 0.045, 0.64), (0, -0.109, 0.49), p["gunmetal"], bevel=0.024),
        box((0.70, 0.51, 0.60), (0, -0.027, 1.105), p["teal"], bevel=0.053, seg=3),
        box((0.635, 0.043, 0.495), (0, -0.271, 1.112), p["gunmetal"], bevel=0.032, seg=3),
        box((0.548, 0.023, 0.320), (0, -0.296, 1.172), p["black"], bevel=0.015),
        box((0.482, 0.018, 0.266), (0, -0.309, 1.172), p["screen"], bevel=0.012),
        box((0.78, 0.62, 0.082), (0, -0.018, 1.459), p["galv"], bevel=0.026, seg=3),
        box((0.12, 0.034, 0.075), (-0.22, -0.305, 0.937), p["hazard"], bevel=0.011),
        box((0.16, 0.032, 0.037), (0.20, -0.304, 0.956), p["plastic_w"], bevel=0.007),
    ]
    for x in (-0.32, 0.32):
        for y in (-0.23, 0.23):
            parts.append(cyl(0.031, 0.024, (x, y, 0.111), p["steel"], verts=6))
        parts.append(box((0.09, 0.056, 0.085), (x, -0.294, 1.457), p["hazard"], bevel=0.012))
    for i in range(5):
        parts.append(box((0.015, 0.22, 0.021), (0.344, 0.019, 0.964 + i * 0.052),
                         p["gunmetal"], bevel=0.005))
    dock_finish(parts, "prop_dock_inspection_terminal")
    col("prop_dock_inspection_terminal", (0.82, 0.68, 1.50), (0, 0, 0.75))
    marker("inspection_screen", (0, -0.325, 1.172))
    marker("inspection_stand", (0, -0.85, 0))


def prop_harbor_beacon():
    reset()
    p = P()
    enamel = mat("beacon_sea_enamel", (0.019, 0.25, 0.26), metal=0.36, rough=0.55)
    cream = mat("beacon_aged_white", (0.76, 0.72, 0.60), rough=0.79)
    copper = mat("beacon_copper_roof", (0.21, 0.35, 0.31), metal=0.68, rough=0.5)
    bronze = mat("beacon_brass", (0.55, 0.37, 0.13), metal=0.73, rough=0.31)
    glazing = mat("beacon_lantern_glass", (0.29, 0.57, 0.64), rough=0.12, alpha=0.18)
    lens = mat("beacon_fresnel_lens", (0.92, 0.57, 0.13), metal=0.12, rough=0.20,
               emit=(1.0, 0.68, 0.24), emit_strength=3.3)
    parts = [
        cyl(2.28, 0.21, (0, 0, 0.105), p["concrete_dark"], verts=12, bevel=0.045),
        lathe([(2.12, 0.20), (2.12, 0.36), (1.99, 0.48), (1.94, 0.93),
               (1.85, 1.10), (1.70, 1.14)], p["concrete"], verts=32),
    ]
    # Tapered enamel panels are separated by structural collars and a gallery cornice.
    cladding = [
        (1.12, 2.45, 1.72, 1.53, enamel), (2.45, 4.63, 1.53, 1.42, cream),
        (4.63, 5.89, 1.42, 1.35, enamel), (5.89, 8.29, 1.35, 1.22, cream),
        (8.29, 9.56, 1.22, 1.15, enamel), (9.56, 11.17, 1.15, 1.06, cream),
    ]
    for low, high, radius_base, radius_top, surface in cladding:
        parts.append(lathe([(radius_base, low), (radius_top, high)], surface, verts=40))
        parts.append(lathe([(radius_top + 0.018, high - 0.062), (radius_top + 0.022, high),
                            (radius_top + 0.008, high + 0.028)], surface, verts=40))
    for i in range(4):
        z = 0.10 + 0.19 * i
        parts.append(box((1.36, 0.96 - 0.16 * i, z + 0.08), (0, -2.08 + 0.08 * i, (z + 0.08) / 2),
                         p["concrete"], bevel=0.025))
    doorway = [(-0.52, 0.77), (0.52, 0.77), (0.52, 2.15), (0.39, 2.34), (0.22, 2.43),
               (-0.22, 2.43), (-0.39, 2.34), (-0.52, 2.15)]
    parts.append(profile(doorway, 0.21, p["gunmetal"], (0, -1.61, 0), bevel=0.025))
    parts.append(profile([(x * 0.86, 0.81 + (z - 0.77) * 0.92) for x, z in doorway], 0.07,
                         enamel, (0, -1.75, 0), bevel=0.018))
    parts.append(cyl(0.162, 0.03, (0, -1.793, 1.92), bronze, rot=(math.pi / 2, 0, 0), verts=20))
    parts.append(cyl(0.123, 0.035, (0, -1.814, 1.92), p["screen"], rot=(math.pi / 2, 0, 0), verts=20))
    parts.append(box((0.19, 0.063, 0.033), (0.27, -1.808, 1.43), bronze, bevel=0.009))
    for angle, z, r in ((0, 3.54, 1.49), (math.pi, 6.75, 1.31), (0, 9.55, 1.16)):
        y = -r * math.cos(angle)
        parts.extend((
            box((0.44, 0.14, 0.84), (0, y, z), bronze, bevel=0.06, seg=3),
            box((0.30, 0.08, 0.65), (0, y - 0.075 * math.cos(angle), z), p["gunmetal"], bevel=0.044),
            box((0.21, 0.023, 0.49), (0, y - 0.12 * math.cos(angle), z), p["screen"], bevel=0.026),
        ))
    parts.extend((
        lathe([(1.04, 11.01), (1.20, 11.16), (1.43, 11.20), (1.44, 11.32),
               (1.78, 11.39), (1.90, 11.49), (1.90, 11.65), (0, 11.65)], copper, verts=40),
        cyl(1.14, 0.20, (0, 0, 11.71), enamel, verts=32, bevel=0.028),
        lathe([(1.022, 11.78), (1.022, 13.32), (0.995, 13.32), (0.995, 11.78)], glazing, verts=24),
        cyl(0.74, 0.14, (0, 0, 11.91), bronze, verts=32, bevel=0.025),
        lathe([(0, 12.0), (0.36, 12.0), (0.45, 12.18), (0.49, 12.44), (0.49, 12.78),
               (0.43, 12.99), (0.31, 13.15), (0, 13.16)], lens, verts=32),
        lathe([(1.27, 13.25), (1.40, 13.29), (1.42, 13.38), (1.17, 13.47),
               (0.87, 13.76), (0.49, 14.03), (0.18, 14.17), (0.05, 14.20)], copper, verts=32),
        cyl(0.14, 0.40, (0, 0, 14.31), bronze, verts=16),
        rod((0, 0, 14.37), (0, 0, 14.80), 0.027, p["gunmetal"], verts=8),
    ))
    for z, radius in ((12.13, 0.43), (12.27, 0.49), (12.42, 0.52), (12.57, 0.526),
                      (12.72, 0.52), (12.87, 0.49), (13.02, 0.44)):
        parts.append(torus(radius, 0.026, (0, 0, z), lens, major=24, minor=6))

    for i in range(12):
        angle = i * math.tau / 12
        x, y = math.cos(angle), math.sin(angle)
        parts.extend((
            rod((1.038 * x, 1.038 * y, 11.79), (1.038 * x, 1.038 * y, 13.33), 0.032, bronze, verts=8),
            rod((1.79 * x, 1.79 * y, 11.58), (1.79 * x, 1.79 * y, 12.41), 0.032, p["gunmetal"], verts=8),
        ))
    for z in (11.97, 12.40):
        parts.append(torus(1.79, 0.039, (0, 0, z), p["gunmetal"], major=32, minor=6))
    dock_finish(parts, "prop_harbor_beacon")
    col_cyl("beacon_plinth", 2.28, 0.43, (0, 0, 0.215))
    col_cyl("beacon_tower", 1.69, 11.20, (0, 0, 5.60))
    col_cyl("beacon_gallery", 1.9, 0.42, (0, 0, 11.50))
    col("beacon_stoep", (1.36, 1.06, 0.66), (0, -2.05, 0.33))
    marker("beacon_light", (0, 0, 12.59))


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
    "prop_dock_power_pedestal": (prop_dock_power_pedestal, "port", "decor", "부두 전원함"),
    "prop_dock_access_gate": (prop_dock_access_gate, "port", "decor", "부두 출입문 — 닫힌 고정형"),
    "prop_dock_safety_rail": (prop_dock_safety_rail, "port", "decor", "부두 안전 난간"),
    "prop_mooring_fender": (prop_mooring_fender, "port", "cover_low", "접안용 고무 방충재"),
    "prop_loading_ramp": (prop_loading_ramp, "port", "decor", "강철 하역 경사판"),
    "prop_dock_cargo_net": (prop_dock_cargo_net, "port", "cover_low", "그물로 고정한 팔레트 화물"),
    "prop_harbor_gantry_crane": (prop_harbor_gantry_crane, "port", "decor", "부두 갠트리 크레인 — 하부 통행 가능"),
    "prop_harbor_pilot_tug": (prop_harbor_pilot_tug, "port", "decor", "부두 작업선 — 원점은 수선, 선수는 Blender -Y / glTF +Z"),
    "prop_dock_inspection_terminal": (prop_dock_inspection_terminal, "port", "decor", "부두 점검 단말기 — 화면 및 점검 위치 마커 포함"),
    "prop_harbor_beacon": (prop_harbor_beacon, "port", "decor", "항만 등대 — 프레넬 렌즈와 등화 마커 포함"),
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
