"""Render a contact sheet of exported .glb files (for review, not shipped in-game).

    blender -b --factory-startup -P tools/blender/render_preview.py -- out.png a.glb b.glb ...
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
out, files = argv[0], argv[1:]

bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
x = 0.0
gap = 0.4
lo, hi = Vector((1e9, 1e9, 1e9)), Vector((-1e9, -1e9, -1e9))
for f in files:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=f)
    new = [o for o in bpy.data.objects if o not in before]
    roots = [o for o in new if o.parent is None]
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in new if o.type == "MESH" for c in o.bound_box]
    mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    off = Vector((x - mn.x, 0, -mn.z))
    for r in roots:
        r.location += off
    x += (mx.x - mn.x) + gap * max(1.0, (mx.z - mn.z))
    lo = Vector((min(lo.x, mn.x + off.x), min(lo.y, mn.y), 0))
    hi = Vector((max(hi.x, mx.x + off.x), max(hi.y, mx.y), max(hi.z, mx.z - mn.z)))

center = (lo + hi) / 2
size = max(hi.x - lo.x, hi.z * 1.6)
bpy.ops.mesh.primitive_plane_add(size=size * 6, location=(center.x, center.y, 0))
g = bpy.context.active_object
gm = bpy.data.materials.new("ground"); gm.use_nodes = True
gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.35, 0.36, 0.38, 1)
g.data.materials.append(gm)

cam_data = bpy.data.cameras.new("cam"); cam_data.type = "ORTHO"; cam_data.ortho_scale = size * 1.15
cam = bpy.data.objects.new("cam", cam_data); sc.collection.objects.link(cam)
d = Vector((0.35, -1.0, 0.45)).normalized()
cam.location = center + Vector((0, 0, hi.z * 0.45)) + d * size * 3
cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
sc.camera = cam

sun = bpy.data.lights.new("sun", "SUN"); sun.energy = 3.5; sun.angle = math.radians(8)
so = bpy.data.objects.new("sun", sun); so.rotation_euler = (math.radians(50), math.radians(10), math.radians(30))
sc.collection.objects.link(so)
w = bpy.data.worlds.new("w"); w.use_nodes = True
w.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.62, 0.72, 1)
w.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
sc.world = w

sc.render.engine = "CYCLES"
sc.cycles.device = "CPU"
sc.cycles.samples = 24
sc.cycles.use_denoising = True
sc.render.resolution_x, sc.render.resolution_y = 1600, 700
sc.view_settings.view_transform = "AgX"
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
print("[harbor] preview", out)
