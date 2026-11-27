"""Build the editable Blender scenes, GLBs and matching collision for Harbor interiors."""

import bpy
import bmesh
import json
import math
import os
import random
import sys
from pathlib import Path
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[1]
GLBS = ROOT / 'game/assets/interiors'
BLENDS = Path(sys.argv[sys.argv.index('--') + 1]) if '--' in sys.argv else None

if addon_path := os.environ.get('HARBOR_BUILDING_TOOLS'):
    sys.path.insert(0, addon_path)
try:
    from btools.building.generator import FloorplanGenerator, RoofGenerator
    from btools.building.material import register_material
    import btools.utils.util_common as building_tools_utils
except ImportError as error:
    raise RuntimeError('Set HARBOR_BUILDING_TOOLS to the pinned Building Tools checkout in web/README.md') from error


def addon_error(*args, **kwargs):
    # Building Tools normally reports generator errors with a GUI popup.
    raise RuntimeError(f'Building Tools: {args} {kwargs}')


building_tools_utils.popup_message = addon_error

PALETTE = {
    'charcoal': ('242a2c', .81), 'rubber': ('242524', .96), 'galvanized': ('87989a', .42, .72),
    'iron': ('314146', .56, .63), 'brass': ('be8e53', .32, .70), 'warm steel': ('beb6a5', .40, .65),
    'painted cream': ('e9d5b5', .77), 'pale plaster': ('e7d7c4', .94), 'cement': ('888883', .95),
    'cement seam': ('646766', .95), 'deep teal': ('234d4b', .64), 'sage': ('647b68', .73),
    'walnut': ('684635', .63), 'walnut edge': ('49332a', .52), 'oak': ('ad7b4d', .68),
    'oak light': ('ba8757', .69), 'oak shade': ('9b683f', .67), 'oak amber': ('af7749', .66),
    'cream tile': ('e4d2b2', .31), 'green tile': ('39716c', .37), 'brick': ('995944', .89),
    'terracotta': ('b76140', .82), 'burgundy': ('743b39', .83), 'canvas': ('cdbba0', .91),
    'porcelain': ('f2e4d2', .36), 'amber paint': ('d7a95d', .72), 'safety red': ('9d4636', .65),
    'milk glass': ('ffe1af', .41, 0, .65), 'neon': ('ecba83', .41, 0, 1.35),
    'window': ('a4c7cc', .23), 'ink': ('253e43', .82), 'leaf': ('587348', .90),
}


def srgb(value):
    n = int(value, 16) / 255
    return n / 12.92 if n <= .04045 else ((n + .055) / 1.055) ** 2.4


def materials():
    result = {}
    for name, (hex_value, roughness, *values) in PALETTE.items():
        material = bpy.data.materials.new(name)
        material.use_nodes = True
        shader = material.node_tree.nodes.get('Principled BSDF')
        rgba = tuple(srgb(hex_value[i:i + 2]) for i in (0, 2, 4)) + (1,)
        material.diffuse_color = rgba
        shader.inputs['Base Color'].default_value = rgba
        shader.inputs['Roughness'].default_value = roughness
        if values:
            shader.inputs['Metallic'].default_value = values[0]
        if len(values) > 1:
            shader.inputs['Emission Color'].default_value = rgba
            shader.inputs['Emission Strength'].default_value = values[1]
        if name == 'window':
            shader.inputs['Alpha'].default_value = .34
        result[name] = material
    return result


def axis(point):
    x, y, z = point
    return Vector((x, -z, y))


class Room:
    def __init__(self, name, size, front, back):
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        for library in (bpy.data.meshes, bpy.data.curves, bpy.data.materials):
            for item in list(library):
                if not item.users:
                    library.remove(item)
        self.scene = bpy.context.scene
        self.scene.name = name
        self.scene.unit_settings.system = 'METRIC'
        self.mats = materials()
        self.name = name
        self.width, self.depth, self.height = size
        self.portals = {'front': front, 'back': back}
        self.meshes = {}
        self.colliders = []
        self.lights = []
        self.section = 'Architecture'
        self.rng = random.Random(83)

    def batch(self, material):
        key = (self.section, material)
        if key not in self.meshes:
            self.meshes[key] = bmesh.new()
        return self.meshes[key]

    def box(self, point, size, material, bevel=.015, solid=None, yaw=0):
        sx, sy, sz = size
        mesh = self.batch(material)
        transform = (Matrix.Translation(axis(point)) @ Matrix.Rotation(math.radians(yaw), 4, 'Z')
                     @ Matrix.Diagonal((sx / 2, sz / 2, sy / 2, 1)))
        vertices = bmesh.ops.create_cube(mesh, size=2.0, matrix=transform)['verts']
        if bevel:
            edges = list({edge for vertex in vertices for edge in vertex.link_edges})
            bmesh.ops.bevel(mesh, geom=edges, offset=min(bevel, min(size) * .28), segments=2,
                           affect='EDGES', clamp_overlap=True)
        if solid:
            self.colliders.append([point, size, solid])

    def cylinder(self, point, radius, height, material, *, radius_top=None, lying=False, sides=16):
        mesh = self.batch(material)
        transform = Matrix.Translation(axis(point))
        if lying:
            transform @= Matrix.Rotation(math.pi / 2, 4, 'X')
        bmesh.ops.create_cone(mesh, cap_ends=True, cap_tris=False, segments=sides, radius1=radius,
                              radius2=radius if radius_top is None else radius_top,
                              depth=height, matrix=transform)

    def sign(self, text, point, size, material, *, inside=False):
        curve = bpy.data.curves.new(text, 'FONT')
        curve.body, curve.size, curve.align_x = text, size, 'CENTER'
        curve.extrude, curve.bevel_depth = .004, .002
        curve.materials.append(self.mats[material])
        label = bpy.data.objects.new(text, curve)
        label.location = axis(point)
        label.rotation_euler.x = math.pi / 2
        label.rotation_euler.z = math.pi if inside else 0
        self.scene.collection.objects.link(label)

    def vertical_wall(self, along, fixed, span, holes, outer):
        low, high = -span / 2, span / 2
        cursor = low

        def panel(a, b, bottom, top, material=outer, collision='concrete', thickness=.24):
            if b - a < .005 or top - bottom < .005:
                return
            h, center = top - bottom, (top + bottom) / 2
            p, w = (a + b) / 2, b - a
            self.box((p, center, fixed) if along == 'x' else (fixed, center, p),
                     (w, h, thickness) if along == 'x' else (thickness, h, w),
                     material, .013, collision)

        for center, width, lower, upper in sorted(holes):
            a, b = center - width / 2, center + width / 2
            assert low <= a < b <= high and a >= cursor
            panel(cursor, a, 0, self.height)
            panel(a, b, 0, lower)
            panel(a, b, upper, self.height)
            for left, right in ((a - .035, a + .02), (b - .02, b + .035)):
                panel(left, right, lower, upper + .04, 'iron', None, .30)
            panel(a, b, upper - .025, upper + .045, 'iron', None, .30)
            if lower:
                panel(a, b, lower, upper, 'window', 'glass', .065)
                panel(a, b, lower - .042, lower + .02, 'walnut', None, .41)
                panel(center - .023, center + .023, lower, upper, 'iron', None, .12)
            cursor = b
        panel(cursor, high, 0, self.height)

    @staticmethod
    def footprint(name, width, depth):
        obj = FloorplanGenerator().build_from_props({'type': 'RECTANGULAR', 'width': width, 'length': depth})
        obj.name = name
        obj['building_tools_width_m'] = width
        obj['building_tools_depth_m'] = depth
        return obj

    def shell(self, outer, floor, *, glazed=True, roof_type='FLAT', rise=.65):
        w, d, h = self.width, self.depth, self.height
        slab = self.footprint('Building Tools · Floor slab', w + .25, d + .25)
        slab.data.materials.append(self.mats[floor])
        thickness = slab.modifiers.new('Floor thickness 160 mm', 'SOLIDIFY')
        thickness.thickness, thickness.offset = .16, -1
        roof = self.footprint('Building Tools · ' + roof_type.title() + ' roof', w, d)
        bpy.context.view_layer.objects.active = roof
        roof.select_set(True)
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        options = {'type': roof_type, 'gable_type': 'BOX', 'height': rise, 'thickness': .22,
                   'outset': .31, 'border': .27}
        status = RoofGenerator().build_from_props(options)
        bpy.ops.object.mode_set(mode='OBJECT')
        if status != {'FINISHED'} or len(roof.data.polygons) < 5:
            raise RuntimeError('Building Tools did not produce the roof for ' + self.name)
        roof.location.z = h
        roof['building_tools_roof_type'] = roof_type
        roof['building_tools_rise_m'] = rise
        roof.data.materials.clear()
        roof.data.materials.append(self.mats['charcoal' if glazed else 'iron'])
        for face in roof.data.polygons:
            face.material_index = 0
        self.colliders.append([(0, h + .08, 0), (w + .45, .22, d + .45), 'metal'])
        self.box((0, h - .08, 0), (w - .24, .10, d - .24), 'pale plaster' if glazed else 'iron')
        for face, side in [('front', 1), ('back', -1)]:
            center, width, height = self.portals[face]
            windows = [(x, 2.9 if w < 22 else 3.7, .86, 2.72) for x in (-w * .29, w * .29)] if glazed else []
            self.vertical_wall('x', d / 2 * side, w, [(center, width, 0, height), *windows], outer)
        for side in (-1, 1):
            windows = [(d * x, 2.45, 1.03, 2.66) for x in (-.30, 0, .30)] if glazed else []
            self.vertical_wall('z', w / 2 * side, d, windows, outer)

    def planks(self, material_options, w=None, d=None, *, boards=.38):
        w, d = w or self.width, d or self.depth
        for index in range(math.ceil((w - .30) / boards)):
            x = -w / 2 + .15 + (index + .5) * boards
            for segment in range(3):
                z = (segment - 1) * (d - .30) / 3
                self.box((x, .008, z), (boards - .012, .017, (d - .30) / 3 - .012),
                         self.rng.choice(material_options), 0)

    def light(self, x, y, z, *, color='ffc88c', energy=1.3, radius=5.4, pendant=True):
        if pendant:
            self.cylinder((x, (self.height + y) / 2, z), .024, self.height - y, 'iron', sides=8)
            self.cylinder((x, y, z), .31, .23, 'iron', radius_top=.12, sides=20)
            self.cylinder((x, y - .116, z), .27, .031, 'milk glass', sides=20)
        self.lights.append({'position': (x, y - .22, z), 'color': color, 'energy': energy, 'range': radius})

    def plant(self, x, z, y=0):
        self.cylinder((x, y + .24, z), .18, .48, 'terracotta', radius_top=.23)
        for index in range(7):
            angle = math.tau * index / 7
            u, v = math.cos(angle), math.sin(angle)
            self.box((x + .14 * u, y + .52 + (index % 3) * .07, z + .14 * v),
                     (.13, .26, .075), 'leaf', .025, yaw=index * 52)

    def table(self, x, z, *, round_top=False):
        self.box((x, .38, z), (1.18, .76, .83), 'walnut', 0, 'wood') if not round_top else None
        if round_top:
            self.cylinder((x, .74, z), .58, .10, 'walnut', sides=24)
            self.cylinder((x, .37, z), .075, .73, 'iron')
            self.cylinder((x, .045, z), .28, .09, 'iron')
            self.colliders.append([(x, .4, z), (1.08, .8, 1.08), 'wood'])
        for offset in (-1, 1):
            self.box((x + offset * .23, .803, z), (.125, .045, .125), 'porcelain', .018)
            self.cylinder((x + offset * .19, .86, z + .035), .038, .081, 'cream tile')

    def chair(self, x, z, face=1):
        for dx in (-.19, .19):
            for dz in (-.18, .18):
                self.box((x + dx, .23, z + dz), (.055, .46, .055), 'walnut edge')
        self.box((x, .46, z), (.48, .15, .47), 'burgundy', .048, 'wood')
        self.box((x, .73, z + face * .21), (.47, .47, .10), 'walnut', .025)

    def export(self, slug):
        for (section, material), mesh in self.meshes.items():
            data = bpy.data.meshes.new(section + ' · ' + material)
            mesh.normal_update()
            mesh.to_mesh(data)
            mesh.free()
            data.materials.append(self.mats[material])
            obj = bpy.data.objects.new(data.name, data)
            self.scene.collection.objects.link(obj)
        # Bake signage curves to meshes for the Godot glTF importer.
        bpy.ops.object.select_all(action='DESELECT')
        for obj in self.scene.objects:
            if obj.type == 'FONT':
                obj.select_set(True)
                bpy.context.view_layer.objects.active = obj
        if bpy.context.selected_objects:
            bpy.ops.object.convert(target='MESH')
        if BLENDS:
            bpy.ops.wm.save_as_mainfile(filepath=str(BLENDS / (slug + '.blend')))
        bpy.ops.export_scene.gltf(filepath=str(GLBS / (slug + '.glb')), export_format='GLB',
                                  export_apply=True, export_texcoords=False, export_animations=False)
        return {'id': slug, 'name': self.name, 'size': (self.width, self.depth, self.height),
                'entries': self.portals, 'colliders': self.colliders, 'lights': self.lights}


def cafe():
    room = Room('Customs Exchange Cafe', (17, 13, 4.15), (0, 2.65, 2.95), (0, 2.45, 2.9))
    room.shell('painted cream', 'cement seam')
    room.section = 'Tile and joinery'
    for x in range(-11, 12):
        for z in range(-8, 9):
            room.box((x * .72, .008, z * .73), (.705, .016, .715),
                     'cream tile' if (x + z) % 2 else 'green tile', 0)
    for x in (-8.32, 8.32):
        room.box((x, .35, 0), (.08, .70, 12.1), 'deep teal')
    room.box((0, 3.35, 6.68), (9.0, .68, .19), 'deep teal')
    room.sign('CUSTOMS EXCHANGE', (0, 3.19, 6.80), .43, 'neon')
    room.sign('COFFEE  /  CANTEEN', (0, 2.94, 6.80), .17, 'painted cream')
    for x in (-5.25, 5.25):
        room.box((x, 2.94, 7.08), (4.0, .12, 1.13), 'deep teal')
        for j in range(8):
            room.box((x + (j - 3.5) * .5, 2.85, 7.64), (.49, .15, .06),
                     'painted cream' if j % 2 else 'green tile')
    room.section = 'Coffee counter'
    room.box((4.05, .52, -.6), (1.14, 1.04, 7.7), 'deep teal', .025, 'wood')
    room.box((4.05, 1.07, -.6), (1.29, .12, 7.85), 'walnut', .032)
    room.box((3.44, .21, -.6), (.05, .05, 7.65), 'brass')
    for j in range(24):
        room.box((3.465, .57, -4.30 + j * .32), (.035, .73, .032), 'green tile', .006)
    room.box((7.41, .55, -1.1), (1.0, 1.1, 7.0), 'walnut edge', .02, 'wood')
    for z in (-3.6, -1.6, .4):
        room.box((7.12, 2.04, z), (.83, .08, 1.69), 'walnut')
        for j in range(4):
            room.cylinder((7.14, 2.17, z + (j - 1.5) * .32), .079, .22, 'porcelain' if j % 2 else 'terracotta')
    room.box((4.05, 1.37, -1.83), (.61, .51, .86), 'warm steel', .045)
    room.box((3.73, 1.38, -1.83), (.035, .32, .72), 'iron')
    for z in (-2.05, -1.6):
        room.cylinder((3.63, 1.26, z), .08, .09, 'brass')
    room.box((4.05, 1.34, .75), (.53, .39, 1.11), 'window', .025)
    for z in (.43, .74, 1.05):
        room.cylinder((4.04, 1.18, z), .11, .12, 'oak', radius_top=.085)
    room.box((4.1, 1.23, 2.72), (.34, .21, .42), 'iron')
    room.section = 'Dining room'
    for z in (-3.96, -.75, 2.46):
        room.box((-7.14, .23, z), (1.18, .46, 2.24), 'walnut edge', .03, 'wood')
        room.box((-7.14, .51, z), (1.18, .21, 2.24), 'burgundy', .065)
        room.box((-7.68, .9, z), (.22, .82, 2.25), 'burgundy', .063)
        room.table(-5.40, z, round_top=True)
        room.chair(-4.32, z + .67)
    room.table(-1.85, -2.45, round_top=True)
    room.chair(-1.85, -3.45, -1)
    room.chair(-1.85, -1.45, 1)
    room.plant(-7.6, 5.25)
    room.plant(7.63, 4.76)
    for x, z in [(-5.35, -3.8), (-5.35, 1.15), (-.5, 3), (4.2, -.6), (4.2, -3.55)]:
        room.light(x, 2.88, z, energy=1.12)
    return room.export('customs_exchange_cafe')


def repair_shop():
    room = Room('Atlantic Repair', (19.5, 26, 6.15), (0, 5.55, 4.30), (0, 5.55, 4.30))
    room.shell('cement', 'cement', glazed=False, roof_type='GABLE', rise=1.35)
    room.section = 'Workshop frame'
    room.box((0, 5.17, 13.15), (12.0, .88, .21), 'deep teal')
    room.sign('ATLANTIC REPAIR', (0, 4.98, 13.28), .58, 'neon')
    for z in (-12.64, -8.4, -4.2, 0, 4.2, 8.4, 12.64):
        room.box((0, 5.67, z), (19.38, .30, .24), 'iron')
        for x in (-9.43, 9.43):
            room.box((x, 2.84, z), (.22, 5.68, .31), 'iron', .03, 'metal')
        for x in (-3.3, 3.3):
            room.box((x, .007, z), (.15, .012, 3.6), 'amber paint', 0)
    for x in (-2.78, 2.78):
        for z in (-12.6, 12.6):
            room.box((x, 2.08, z), (.14, 4.16, .16), 'galvanized')
    for z in (-12.82, 12.82):
        for j in range(9):
            room.box((0, 4.43 + j * .12, z), (5.55, .115, .16), 'galvanized')
    for x in (-8.3, -5.9, -3.4, 3.4, 5.9, 8.3):
        room.box((x, .005, 0), (.015, .006, 25.8), 'cement seam', 0)
    room.section = 'Service equipment'
    for x in (-5.15, 5.15):
        room.box((x, 1.82, 1.6), (.26, 3.64, .34), 'safety red', .04, 'metal')
        room.box((x, .07, 1.6), (.76, .14, .92), 'iron')
        for z in (.90, 2.3):
            room.box((x * .83, .18, z), (1.6, .19, .22), 'safety red')
    room.box((0, 3.82, 1.60), (10.35, .23, .32), 'safety red', .03, 'metal')
    room.box((8.52, .53, -3.95), (1.15, 1.06, 8.1), 'iron', .03, 'metal')
    room.box((8.52, 1.10, -3.95), (1.29, .12, 8.2), 'oak', .027)
    room.box((9.47, 1.86, -4), (.08, 1.27, 8.25), 'charcoal')
    for j in range(24):
        z = -7.7 + j * .32
        room.box((9.39, 1.72 + (j % 3) * .17, z), (.071, .24, .07), 'warm steel', .012)
    for z in (-6.47, -4.1, -1.7):
        room.box((7.70, .52, z), (.75, .97, 1.4), 'safety red', .045, 'metal')
        for y in (.29, .47, .65, .83):
            room.box((7.30, y, z), (.07, .026, 1.12), 'galvanized')
    for z in (-8.0, -4.5, -.90):
        room.box((-8.2, .08, z), (1.4, .16, 2.8), 'walnut')
        room.box((-8.2, .92, z), (1.38, 1.68, 2.57), 'iron', .01, 'metal')
        for dy in (.27, 1.12, 1.77):
            room.box((-8.20, dy, z), (1.49, .11, 2.8), 'galvanized')
        for j in range(3):
            room.box((-7.99, 1.99, z + (j - 1) * .77), (.65, .38, .61), 'safety red' if j == 1 else 'sage')
    for z in (5.35, 7.05, 8.75):
        for index in range(3):
            y = .17 + index * .23
            room.cylinder((-8.35, y, z), .41, .225, 'rubber', sides=24)
            room.cylinder((-8.35, y + .117, z), .218, .016, 'galvanized', sides=20)
        room.colliders.append([(-8.35, .38, z), (.82, .76, .82), 'metal'])
    for x, z in [(-5.6, -8), (5.6, -8), (-5.6, 0), (5.6, 0), (-5.6, 8), (5.6, 8)]:
        room.box((x, 4.97, z), (.41, .12, 2.06), 'iron')
        room.box((x, 4.895, z), (.30, .03, 1.99), 'milk glass')
        room.light(x, 4.72, z, color='ffe7c9', energy=1.55, radius=6.9, pendant=False)
    return room.export('atlantic_repair')


def lodge():
    room = Room('Alderwash Lodge', (28, 20, 5.4), (0, 3.2, 3.1), (0, 3.2, 3.1))
    room.shell('walnut', 'walnut edge', roof_type='GABLE', rise=2.75)
    room.section = 'Timber cladding and trusses'
    room.planks(('oak', 'oak light', 'oak shade', 'oak amber'), boards=.42)
    for z in (-10.17, 10.17):
        room.box((0, 3.75, z), (8.7, .77, .17), 'walnut edge')
    room.sign('ALDERWASH LODGE', (0, 3.57, -10.29), .51, 'neon', inside=True)
    room.sign('ALDERWASH LODGE', (0, 3.57, 10.29), .51, 'neon')
    for z in (-8, -4, 0, 4, 8):
        room.box((0, 4.80, z), (27.85, .35, .30), 'walnut edge')
        for x in (-13.6, 13.6):
            room.box((x, 2.40, z), (.40, 4.80, .43), 'walnut edge', .025, 'wood')
    room.section = 'Fireside reading room'
    room.box((-12.75, 1.65, 0), (1.88, 3.3, 3.85), 'brick', .045, 'concrete')
    room.box((-11.78, .62, 0), (.062, 1.16, 2.21), 'charcoal')
    room.box((-11.65, 1.41, 0), (.45, .20, 3.46), 'walnut edge')
    room.box((-11.64, .30, 0), (.15, .22, 1.38), 'terracotta')
    for z in (-.52, -.28, -.08, .17, .41):
        room.box((-11.69, .46, z), (.10, .32 + abs(z) * .30, .16), 'neon', .035)
    for z in (-5.1, -3.9, 3.9, 5.1):
        room.box((-12.49, 1.04, z), (1.05, 2.08, 1.02), 'walnut edge', .015, 'wood')
        for j in range(3):
            room.box((-11.93, .69 + .48 * j, z), (.062, .08, .98), 'oak')
            for i in range(5):
                room.box((-11.89, .82 + .48 * j, z + (i - 2) * .17), (.079, .22, .115),
                         ('canvas', 'sage', 'burgundy')[i % 3])
    room.box((-7.05, .02, 0), (6.55, .018, 5.85), 'burgundy')
    for z in (-2.79, -2.66, 2.66, 2.79):
        room.box((-7.05, .031, z), (6.41, .007, .045), 'amber paint', 0)
    for z in (-2.0, 2.0):
        room.box((-7.85, .32, z), (3.40, .64, 1.07), 'walnut edge', .032, 'wood')
        room.box((-7.85, .57, z), (3.22, .24, .89), 'sage', .084)
        room.box((-7.85, .86, z + math.copysign(.40, z)), (3.22, .75, .34), 'sage', .089)
        for x in (-8.93, -6.83):
            room.box((x, .91, z + math.copysign(.22, z)), (.47, .47, .19), 'canvas', .066, yaw=12)
    room.table(-7.72, 0, round_top=True)
    room.section = 'Reception and boot room'
    room.box((7.72, .52, 3.1), (5.33, 1.04, 1.05), 'walnut', .028, 'wood')
    room.box((7.72, 1.09, 3.1), (5.56, .15, 1.19), 'oak', .046)
    for x in range(5, 11):
        room.box((x, .57, 2.55), (.03, .66, .042), 'brass')
    room.box((7.55, 1.33, 3.02), (.48, .28, .34), 'iron')
    room.box((11.59, 1.18, 5.62), (2.12, 2.36, .56), 'walnut edge', .027, 'wood')
    for x in (10.8, 11.3, 11.8, 12.3):
        for y in (.43, 1.01, 1.59, 2.17):
            room.box((x, y, 5.31), (.42, .45, .07), 'oak', .02)
            room.box((x, y + .11, 5.26), (.14, .025, .045), 'brass')
    room.box((8.0, .26, -5.43), (4.8, .52, 1.01), 'walnut', .045, 'wood')
    for x in (6.43, 7.78, 9.3):
        room.box((x, .63, -5.43), (.85, .21, .81), 'canvas', .065)
    room.plant(12.6, -8.49)
    room.plant(-12.65, 8.5)
    for x in (-8.0, 0, 8.0):
        for z in (-4.1, 4.1):
            room.light(x, 3.55, z, color='ffc187', energy=1.52, radius=7.8)
    room.light(-11.35, .83, 0, color='ff9251', energy=.99, radius=3.6, pendant=False)
    return room.export('alderwash_lodge')


GLBS.mkdir(parents=True, exist_ok=True)
if BLENDS:
    BLENDS.mkdir(parents=True, exist_ok=True)
bpy.context.preferences.filepaths.save_version = 0
register_material()
metadata = [cafe(), repair_shop(), lodge()]
target = ROOT / 'data/world/harbor_interiors.json'
target.write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
