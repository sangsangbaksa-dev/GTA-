"""Turntable-style review shots for character .glb files (full body + face close-up).

    blender -b --factory-startup -P tools/blender/render_characters.py -- out_dir a.glb b.glb ...
Writes <out_dir>/<name>_full.png and <name>_face.png.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
out_dir, files = argv[0], argv[1:]
os.makedirs(out_dir, exist_ok=True)

for f in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    bpy.ops.import_scene.gltf(filepath=f)
    meshes = [o for o in sc.objects if o.type == "MESH"]
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    top = max(p.z for p in pts)
    w = bpy.data.worlds.new("w"); w.use_nodes = True
    w.node_tree.nodes["Background"].inputs["Color"].default_value = (0.5, 0.55, 0.62, 1)
    w.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.6
    sc.world = w
    for rot, en in (((55, 0, 35), 3.0), ((70, 0, -120), 1.2)):
        L = bpy.data.lights.new("s", "SUN"); L.energy = en; L.angle = 0.2
        o = bpy.data.objects.new("s", L); o.rotation_euler = [math.radians(a) for a in rot]; sc.collection.objects.link(o)
    sc.render.engine = "CYCLES"; sc.cycles.device = "CPU"; sc.cycles.samples = 48; sc.cycles.use_denoising = True
    sc.view_settings.view_transform = "AgX"
    cam = bpy.data.objects.new("c", bpy.data.cameras.new("c")); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.lens = 85
    name = os.path.splitext(os.path.basename(f))[0]
    for kind, tgt, dist, res in (("full", top * 0.5, top * 2.35, (700, 1000)), ("face", top - 0.13 * top / 1.8, 0.95, (700, 700))):
        ang = math.radians(25)
        d = Vector((math.sin(ang), -math.cos(ang), 0.08))
        t = Vector((0, 0, tgt))
        cam.location = t + d * dist
        cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
        sc.render.resolution_x, sc.render.resolution_y = res
        sc.render.filepath = os.path.join(out_dir, f"{name}_{kind}.png")
        bpy.ops.render.render(write_still=True)
    print("[harbor] rendered", name)
