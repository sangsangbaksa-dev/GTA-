"""Build the original Calloway Pip road compact for Project Harbor and Set.

blender -b --factory-startup -P tools/blender/build_vehicles.py -- --out game/assets/vehicles
"""
import argparse
import math
import os
import sys

import bpy

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_props import P, _finish, box, cyl, dock_finish, export, mat, profile, reset, rod, torus


def face(name, coordinates, material):
    mesh = bpy.data.meshes.new(name)
    # Two-sided by default in glTF, used for window glazing and body decals.
    mesh.from_pydata(coordinates, [], [(0, 1, 2, 3)])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return _finish(obj, material, smooth=False)


def wheel(name, position, p):
    axis = (0, math.pi / 2, 0)
    # Tire, the darker inset drum, and a machined circular hub all spin together.
    pieces = [
        cyl(0.31, 0.186, (0, 0, 0), p["black"], rot=axis, verts=32, bevel=0.041, seg=3),
        cyl(0.202, 0.193, (0, 0, 0), p["gunmetal"], rot=axis, verts=24, bevel=0.008),
        cyl(0.075, 0.214, (0, 0, 0), p["galv"], rot=axis, verts=16, bevel=0.011),
    ]
    side = math.copysign(1, position[0])
    for i in range(5):
        angle = i * 2 * math.pi / 5
        y, z = math.cos(angle), math.sin(angle)
        pieces.append(rod((side * 0.106, 0.05 * y, 0.05 * z), (side * 0.106, 0.172 * y, 0.172 * z),
                          0.018, p["galv"], verts=8))
    pieces.append(torus(0.177, 0.014, (side * 0.105, 0, 0), p["galv"], rot=axis, major=24, minor=6))
    dock_finish(pieces, name + "Roll")
    hub = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(hub)
    hub.location = position
    bpy.data.objects[name + "Roll"].parent = hub


def compact():
    reset()
    p = P()
    paint = mat("pip_copper_orange", (0.74, 0.19, 0.066), metal=0.36, rough=0.34, clearcoat=0.55)
    trim = mat("pip_graphite_trim", (0.064, 0.068, 0.075), metal=0.23, rough=0.53)
    glass = mat("pip_smoked_glazing", (0.078, 0.17, 0.21), metal=0.24, rough=0.18, clearcoat=0.6)
    front_light = mat("pip_lamp_white", (0.71, 0.80, 0.83), emit=(0.62, 0.72, 0.76), emit_strength=1.2)
    rear_light = mat("pip_lamp_red", (0.62, 0.025, 0.022), emit=(0.38, 0.019, 0.013), emit_strength=1.5)
    # Two scallops in the quarter-panel silhouette clear the front and rear tires.
    side = [(-1.90, 0.38), (-1.84, 0.60), (-1.69, 0.76), (-1.39, 0.84), (0.82, 0.84),
            (1.62, 0.79), (1.87, 0.68), (1.90, 0.45), (1.76, 0.31),
            (1.53, 0.31), (1.50, 0.49), (1.40, 0.62), (1.26, 0.68), (1.10, 0.68),
            (0.96, 0.62), (0.86, 0.49), (0.83, 0.31), (-0.83, 0.31),
            (-0.86, 0.49), (-0.96, 0.62), (-1.10, 0.68), (-1.26, 0.68),
            (-1.40, 0.62), (-1.50, 0.49), (-1.53, 0.31), (-1.77, 0.31)]
    body = profile(side, 1.65, paint, axis="X", bevel=0.037, seg=3)
    for panel in body.data.polygons:
        if panel.loop_total > 4:
            panel.use_smooth = False
    parts = [
        body,
        box((1.10, 3.23, 0.22), (0, 0, 0.28), trim, bevel=0.09, seg=3),
        box((1.53, 0.13, 0.18), (0, -1.835, 0.45), trim, bevel=0.048, seg=3),
        box((1.58, 0.13, 0.18), (0, 1.835, 0.46), trim, bevel=0.048, seg=3),
        box((0.62, 0.047, 0.16), (0, -1.885, 0.605), p["gunmetal"], bevel=0.025),
        box((0.42, 0.027, 0.087), (0, 1.907, 0.607), p["plastic_w"], bevel=0.011),
    ]
    roof_side = [(-0.98, 0.79), (-0.55, 1.46), (-0.32, 1.53), (0.95, 1.53),
                 (1.17, 1.46), (1.52, 0.79)]
    parts.append(profile(roof_side, 1.36, trim, axis="X", bevel=0.048, seg=3))
    parts.append(face("windscreen", [(-0.58, -0.618, 1.404), (-0.62, -0.951, 0.857),
                                     (0.62, -0.951, 0.857), (0.58, -0.618, 1.404)], glass))
    parts.append(face("hatch_glass", [(0.58, 1.220, 1.402), (0.61, 1.485, 0.896),
                                      (-0.61, 1.485, 0.896), (-0.58, 1.220, 1.402)], glass))
    for sign in (-1, 1):
        x = sign * 0.683
        parts.extend((
            face("front_side_glass", [(x, -0.865, 0.907), (x, -0.518, 1.407),
                                      (x, 0.156, 1.407), (x, 0.156, 0.907)], glass),
            face("rear_side_glass", [(x, 0.25, 0.907), (x, 0.25, 1.407),
                                     (x, 1.101, 1.407), (x, 1.36, 0.907)], glass),
            box((0.047, 1.43, 0.043), (sign * 0.81, 0.02, 0.574), trim, bevel=0.011),
            box((0.048, 1.43, 0.020), (sign * 0.811, 0.02, 0.63), p["hazard"], bevel=0.005),
            box((0.032, 0.18, 0.045), (sign * 0.825, 0.059, 0.744), p["gunmetal"], bevel=0.016),
            box((0.014, 0.023, 0.43), (sign * 0.826, 0.20, 0.582), trim, bevel=0.004),
            box((0.19, 0.21, 0.099), (sign * 0.785, -0.61, 0.982), trim, bevel=0.032, seg=3),
            box((0.37, 0.045, 0.11), (sign * 0.51, -1.779, 0.683), trim, bevel=0.018),
            box((0.31, 0.045, 0.078), (sign * 0.51, -1.808, 0.687), front_light, bevel=0.016, seg=3),
            box((0.11, 0.026, 0.046), (sign * 0.698, -1.796, 0.69), p["orange"], bevel=0.01),
            box((0.26, 0.046, 0.115), (sign * 0.626, 1.873, 0.683), rear_light, bevel=0.018, seg=3),
        ))
    for y in (-0.18, 0.91):
        parts.append(box((1.14, 0.074, 0.023), (0, y, 1.528), trim, bevel=0.01))
    dock_finish(parts, "veh_calloway_compact")
    # Front faces Blender -Y; driver-left therefore corresponds to positive X.
    wheel("WheelFL", (0.781, -1.18, 0.31), p)
    wheel("WheelFR", (-0.781, -1.18, 0.31), p)
    wheel("WheelRL", (0.781, 1.18, 0.31), p)
    wheel("WheelRR", (-0.781, 1.18, 0.31), p)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="vehicle asset directory")
    args = parser.parse_args(argv)
    compact()
    export(os.path.abspath(os.path.join(args.out, "veh_calloway_compact.glb")))


if __name__ == "__main__":
    main()
