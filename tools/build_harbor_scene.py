"""Emit the Harbor Front Godot scene and matching server collision layout."""

import json
import math
import random
from collections import defaultdict
from pathlib import Path

from harbor_expansion import TERRAIN_MATERIALS, DISTRICTS, LANDMARKS, expand, open_intervals

ROOT = Path(__file__).resolve().parents[1]
SPAWN = (0, 0, 5)
BOUNDS = {'min': (-670, -600), 'max': (620, 510)}
WATER_LEVEL = -1.12
TERRAIN = [
    {'id': 'alder_ridge', 'name': 'Alder Ridge', 'center': (-347, 310), 'size': (86, 82), 'height': 11.5,
     'district': 'alderwash', 'theme': 'forest', 'material': 'ForestFloor'},
    {'id': 'cinder_dunes', 'name': 'Cinder Dunes', 'center': (-377, -537), 'size': (98, 78), 'height': 16,
     'district': 'cinder_flats', 'theme': 'desert', 'material': 'DesertSand'},
    {'id': 'seabank_hill', 'name': 'Seabank Hill', 'center': (238, 343), 'size': (76, 42), 'height': 5.8,
     'district': 'lantern_point', 'theme': 'wealthy', 'material': 'VillageGrass'},
]
obstacles = []
roads = []
water = []
ground = []
batches = defaultdict(list)
rng = random.Random(34)
visual_materials = []
visual_primitives = []
visual_assets = []
visual_signs = []
asset_paths = {}
interiors = json.loads((ROOT / 'data/world/harbor_interiors.json').read_text(encoding='utf-8'))
interior_placements = {
    'customs_exchange_cafe': ((64.5, 0.13, -12), 'harbor_front'),
    'atlantic_repair': ((119, 0.13, -61), 'harbor_front'),
    'alderwash_lodge': ((-375, 0.01, 246), 'alderwash'),
}
shape_ids = {'UnitBox': 'box', 'Cylinder': 'cylinder', 'Foliage': 'sphere',
             'PineCone': 'cone', 'RoofPrism': 'gable'}


scene = []
sub_count = 0
ext_count = 0


def emit(*lines):
    scene.extend(lines)
    scene.append('')


def val(value):
    if isinstance(value, tuple):
        return f'Vector3({", ".join(map(str, value))})'
    return str(value)


def ext(name, path, kind='PackedScene'):
    global ext_count
    ext_count += 1
    if kind == 'PackedScene':
        asset_paths[name] = path
    emit(f'[ext_resource type="{kind}" path="res://{path}" id="{name}"]')


def sub(kind, name, **props):
    global sub_count
    sub_count += 1
    emit(f'[sub_resource type="{kind}" id="{name}"]', *(f'{k} = {val(v)}' for k, v in props.items()))


def node(name, kind, parent='.', *, instance=None, groups=None, **props):
    definition = f'[node name="{name}"'
    if kind:
        definition += f' type="{kind}"'
    if parent is not None:
        definition += f' parent="{parent}"'
    if groups:
        definition += f' groups=["{groups}"]'
    if instance:
        definition += f' instance=ExtResource("{instance}")'
        visual_assets.append({'id': name, 'path': asset_paths[instance], 'position': props.get('position', (0, 0, 0)),
                              'scale': props.get('scale', (1, 1, 1)), 'rotation': props.get('rotation_degrees', (0, 0, 0))})
    emit(definition + ']', *(f'{k} = {val(v)}' for k, v in props.items()))


def material(name, color, roughness=0.92, **props):
    visual_materials.append({'id': name, 'color': [float(c) for c in color.split(',')], 'roughness': roughness})
    sub('StandardMaterial3D', name, albedo_color=f'Color({color}, 1)', roughness=roughness, **props)


def altitude(x, z):
    height = 0
    for patch in TERRAIN:
        px, pz = patch['center']
        width, depth = patch['size']
        falloff = max(0, 1 - (2*(x-px)/width)**2 - (2*(z-pz)/depth)**2)
        height = max(height, patch['height']*falloff**2)
    return round(height, 5)


def on_ground(center):
    x, y, z = center
    rise = altitude(x, z)
    return (x, round(y + rise, 5), z) if rise else center


def box(name, center, size, material_name, parent, rotation=None):
    # The shared unit cube keeps the scene small, and each object reads as a simple blockout primitive.
    center = on_ground(center)
    props = {'position': center, 'scale': size}
    if rotation:
        props['rotation_degrees'] = rotation
    record_primitive(parent, material_name, center, size, rotation or (0, 0, 0), 'UnitBox')
    node(name, 'MeshInstance3D', parent, **props, mesh='SubResource("UnitBox")',
         material_override=f'SubResource("{material_name}")')


def collider(name, center, size, surface="concrete"):
    center = on_ground(center)
    obstacles.append({"tag": name, "center": center, "size": size, "material": surface})
    node(name, 'CollisionShape3D', 'WalkCollision', groups='walk_obstacle', position=center,
         shape=f'SubResource("Shape{len(obstacles) - 1:03}")')


def prop(name, resource, pos, rotate=0, scale=1, parent='DockKit'):
    props = {'position': on_ground(pos)}
    if rotate:
        props['rotation_degrees'] = (0, rotate, 0)
    if scale != 1:
        props['scale'] = (scale, scale, scale)
    node(name, None, parent, instance=resource, **props)


ext('Warehouse', 'assets/buildings/bldg_warehouse.glb')
for resource, asset in [
    ('Apartment', 'apartment_block'), ('Shop', 'corner_shop'), ('Garage', 'parking_garage'),
]:
    ext(resource, f'assets/buildings/bldg_{asset}.glb')
ext('PavingShader', 'scenes/world/HarborPaving.gdshader', 'Shader')
ext('WaterShader', 'scenes/world/HarborWater.gdshader', 'Shader')
for interior in interiors:
    ext(interior['id'], f"assets/interiors/{interior['id']}.glb")
for name, filename in [
    ('PowerPedestal', 'dock_power_pedestal'), ('AccessGate', 'dock_access_gate'),
    ('SafetyRail', 'dock_safety_rail'), ('Fender', 'mooring_fender'),
    ('LoadingRamp', 'loading_ramp'), ('CargoNet', 'dock_cargo_net'),
    ('Barrier', 'concrete_barrier'), ('Crates', 'crate_stack'), ('Container', 'shipping_container'),
    ('Lamp', 'street_lamp'), ('Bollard', 'dock_bollard'), ('Gantry', 'harbor_gantry_crane'),
    ('Tug', 'harbor_pilot_tug'), ('Terminal', 'dock_inspection_terminal'), ('Drum', 'barrel_oil'),
    ('Bench', 'bench'), ('Dumpster', 'dumpster'), ('Shelter', 'bus_shelter'),
    ('LifeRing', 'life_ring_stand'), ('Beacon', 'harbor_beacon'),
    ('Barricade', 'road_barricade'), ('Pallet', 'pallet_stack'), ('Cone', 'traffic_cone'),
    ('Spool', 'cable_spool'), ('Workbench', 'workbench'), ('Kiosk', 'vending_machine'),
    ('Newspaper', 'newspaper_box'),
]:
    ext(name, f'assets/props/prop_{filename}.glb')

sub('ProceduralSkyMaterial', 'CoastalSky', sky_top_color='Color(0.29, 0.5, 0.68, 1)',
    sky_horizon_color='Color(0.79, 0.82, 0.80, 1)', ground_bottom_color='Color(0.15, 0.24, 0.28, 1)',
    ground_horizon_color='Color(0.75, 0.8, 0.80, 1)', sun_angle_max=18.0)
sub('Sky', 'Sky', sky_material='SubResource("CoastalSky")')
sub('Environment', 'CoastalLight', background_mode=2, sky='SubResource("Sky")',
    ambient_light_source=3, ambient_light_color='Color(0.82, 0.88, 0.96, 1)',
    ambient_light_sky_contribution=0.52, ambient_light_energy=0.78, tonemap_mode=2,
    fog_enabled='true', fog_light_color='Color(0.66, 0.75, 0.79, 1)', fog_density=0.0019,
    fog_sky_affect=0.35)
sub('BoxMesh', 'UnitBox')
sub('ShaderMaterial', 'WaterSurface', shader='ExtResource("WaterShader")')
sub('PlaneMesh', 'Bay', material='SubResource("WaterSurface")', size='Vector2(2400, 2250)')
for label, color, scale, strength, grit in [
    ('Asphalt', '0.205, 0.231, 0.234', 0.29, 0.75, 0.17),
    ('Concrete', '0.45, 0.435, 0.392', 0.24, 0.66, 0.10),
    ('Sidewalk', '0.49, 0.465, 0.423', 0.43, 0.48, 0.08),
    ('Pavers', '0.42, 0.316, 0.254', 0.69, 0.56, 0.09),
    ('Ballast', '0.25, 0.263, 0.244', 0.40, 0.90, 0.24),
    ('PlanterSoil', '0.22, 0.233, 0.177', 0.33, 0.68, 0.22),
    *TERRAIN_MATERIALS,
]:
    visual_materials.append({'id': label, 'color': [float(c) for c in color.split(',')],
                             'roughness': 0.9, 'texture': {'scale': scale, 'strength': strength, 'grit': grit}})
    sub('ShaderMaterial', label, shader='ExtResource("PavingShader")', **{
        'shader_parameter/base_color': f'Color({color}, 1.0)',
        'shader_parameter/stain_scale': scale,
        'shader_parameter/stain_strength': strength,
        'shader_parameter/grit_strength': grit,
    })
material('ConcreteEdge', '0.54, 0.51, 0.44')
material('Joint', '0.34, 0.34, 0.31')
material('PaintAmber', '0.95, 0.66, 0.20')
material('PaintIvory', '0.81, 0.80, 0.67')
material('DeepTeal', '0.10, 0.22, 0.23')
material('Shutter', '0.23, 0.29, 0.30', 0.66)
material('Shore', '0.33, 0.41, 0.42')
for label, color, roughness in [
    ('Dune', '0.47, 0.398, 0.315', 0.91), ('RedBrick', '0.39, 0.20, 0.16', 0.92),
    ('Terra', '0.59, 0.32, 0.24', 0.9), ('Limestone', '0.62, 0.57, 0.49', 0.88),
    ('StoneBlue', '0.34, 0.40, 0.42', 0.89), ('Celadon', '0.30, 0.40, 0.36', 0.87),
    ('OffWhite', '0.66, 0.64, 0.57', 0.90), ('Rust', '0.40, 0.227, 0.16', 0.74),
    ('Rubber', '0.065, 0.071, 0.068', 0.97), ('Glass', '0.071, 0.153, 0.181', 0.28),
    ('WarmGlass', '0.25, 0.215, 0.156', 0.32), ('Moss', '0.22, 0.31, 0.168', 0.96),
    ('Leaf', '0.32, 0.43, 0.202', 0.93), ('Trunk', '0.28, 0.196, 0.12', 0.91),
    ('Roof', '0.26, 0.27, 0.26', 0.88), ('BluePaint', '0.15, 0.43, 0.57', 0.80),
    ('Whitecap', '0.51, 0.69, 0.65', 0.46), ('RedPaint', '0.68, 0.21, 0.15', 0.90),
    ('PineDark', '0.10, 0.24, 0.17', 0.97), ('PineLight', '0.20, 0.34, 0.24', 0.96),
    ('Sandstone', '0.69, 0.39, 0.24', 0.94), ('SandstoneLight', '0.79, 0.54, 0.34', 0.92),
    ('BlueGlass', '0.15, 0.31, 0.39', 0.27), ('SkyGlass', '0.37, 0.54, 0.58', 0.31),
    ('IvoryStone', '0.79, 0.77, 0.68', 0.87), ('Terracotta', '0.58, 0.27, 0.18', 0.94),
    ('UniformOlive', '0.34, 0.36, 0.28', 0.95),
]:
    material(label, color, roughness)
sub('SphereMesh', 'Foliage', radial_segments=7, rings=4, radius=0.5, height=1.0)
sub('CylinderMesh', 'Cylinder', radial_segments=12, top_radius=0.5, bottom_radius=0.5, height=1.0)
sub('CylinderMesh', 'PineCone', radial_segments=8, top_radius=0.0, bottom_radius=0.5, height=1.0)
sub('PrismMesh', 'RoofPrism', size=(1, 1, 1))
scene_nodes_start = len(scene)

def record_primitive(zone, label, center, size, rotation, mesh):
    visual_primitives.append({'center': [round(c, 5) for c in center], 'size': [round(s, 5) for s in size],
                              'material': label, 'shape': shape_ids[mesh],
                              'rotation': rotation, 'sector': zone})

node('HarborFront', 'Node3D', None)
node('Environment', 'WorldEnvironment', environment='SubResource("CoastalLight")')
node('LateAfternoonSun', 'DirectionalLight3D', rotation_degrees=(-38, -39, 0),
     light_color='Color(1, 0.91, 0.75, 1)', light_energy=1.33, shadow_enabled='true',
     directional_shadow_max_distance=126.0)
node('Spawn', 'Marker3D', groups='walk_spawn', position=SPAWN)
node('Bay', 'MeshInstance3D', position=(-25, WATER_LEVEL, -45), mesh='SubResource("Bay")', cast_shadow=0)
node('Yard', 'Node3D')
box('Apron', (0, -0.48, -5), (48, 0.96, 48), 'Concrete', 'Yard')
box('ServiceLane', (-5.5, -0.009, -2.4), (33, 0.029, 34), 'Asphalt', 'Yard')
box('NorthQuay', (0, 0.12, -28.75), (48, 0.58, 0.54), 'ConcreteEdge', 'Yard')
box('EastApronJoint', (23.75, 0.007, -5), (0.075, 0.014, 48), 'Joint', 'Yard')
for j, z in enumerate([-24.75, -20.1, -15.4, -10.7, -6, -1.3, 3.4, 8.1, 12.8]):
    box(f'EastJoint{j:02}', (17.67, 0.007, z), (11.45, 0.009, 0.035), 'Joint', 'Yard')
for j, x in enumerate(range(-21, 24, 5)):
    box(f'NorthJoint{j:02}', (x, 0.008, -24.1), (0.035, 0.009, 8.8), 'Joint', 'Yard')
for name, position, size in [
    ('EastLine', (12.5, 0.014, -5), (0.13, 0.008, 39.5)),
    ('NorthLine', (-5.8, 0.014, -24.8), (36.6, 0.008, 0.13)),
    ('LaneLeft', (-3.7, 0.013, -8), (0.10, 0.008, 22.2)),
    ('LaneRight', (3.7, 0.013, -8), (0.10, 0.008, 22.2)),
]:
    box(name, position, size, 'PaintAmber', 'Yard')
for j in range(5):
    box(f'LaneDash{j}', (0, 0.014, -15 + j * 3.55), (0.10, 0.008, 1.30), 'PaintIvory', 'Yard')
for j in range(6):
    box(f'CrossingStripe{j}', (-2.75 + j * 1.1, 0.014, 2.45), (0.54, 0.008, 1.65), 'PaintIvory', 'Yard')
for j in range(5):
    box(f'LoadingStripe{j}', (-13.75 + j * 1.15, 0.014, 0.13), (0.11, 0.008, 3), 'PaintAmber', 'Yard', (0, 33, 0))

node('Warehouse', None, instance='Warehouse', position=(-12.35, 0, -10.2))
node('WarehouseDetails', 'Node3D')
box('ShutterLeft', (-19.85, 2.7, -3.24), (4.95, 4.99, 0.08), 'Shutter', 'WarehouseDetails')
box('ShutterRight', (-4.85, 1.48, -3.24), (4.95, 2.55, 0.08), 'Shutter', 'WarehouseDetails')
box('SignBack', (-10.5, 6.12, -2.96), (8.7, 1.24, 0.10), 'DeepTeal', 'WarehouseDetails')
node('WarehouseSign', 'Label3D', 'WarehouseDetails', position=(-10.5, 6.16, -2.896),
     pixel_size=0.016, font_size=55, outline_size=0, modulate='Color(0.93, 0.88, 0.70, 1)',
     text='"CALLOWAY QUAY"')
visual_signs.append({'text': 'CALLOWAY QUAY', 'position': (-10.5, 6.16, -2.896),
                     'rotation': (0, 0, 0), 'width': 8.7, 'height': 1.24})
for j in range(8):
    box(f'LeftSlat{j}', (-19.85, 1.3 + j * 0.46, -3.17), (4.94, 0.035, 0.04), 'DeepTeal', 'WarehouseDetails')
for j in range(6):
    box(f'RightSlat{j}', (-4.85, 0.28 + j * 0.45, -3.17), (4.94, 0.035, 0.04), 'DeepTeal', 'WarehouseDetails')

node('DockKit', 'Node3D')
prop('PowerPedestal', 'PowerPedestal', (6.38, 0, -5.0))
prop('QuayPowerPedestal', 'PowerPedestal', (13.85, 0, -21.8))
prop('AccessGate', 'AccessGate', (8.1, 0, -9.8))
prop('LoadingRamp', 'LoadingRamp', (-5.0, 0, -0.95))
prop('CargoNet', 'CargoNet', (-9.1, 0, -0.25))
for j in range(4):
    prop(f'NorthRail{j}', 'SafetyRail', (4.15 + j * 2.31, 0.41, -28.66))
for j in range(3):
    prop(f'NorthFender{j}', 'Fender', (2.1 + j * 5.93, -0.68, -29.23), 180)
prop('SpareFender', 'Fender', (10.7, 0, -8.9))

node('WalkCollision', 'StaticBody3D')
for name, center, size, surface in [
    ('Warehouse', (-12.35, 4, -10.2), (22.36, 8, 14.32), 'concrete'),
    ('LoadingPlatform', (-19.85, 0.6, -1.88), (7, 1.2, 2.65), 'concrete'),
    ('Pedestal', (6.38, 0.58, -5), (0.52, 1.16, 0.42), 'metal'),
    ('QuayPedestal', (13.85, 0.58, -21.8), (0.52, 1.16, 0.42), 'metal'),
    ('Gate', (8.1, 1, -9.8), (2.60, 2.0, 0.36), 'metal'),
    ('Ramp', (-5, 0.27, -0.95), (1.45, 0.54, 2.4), 'metal'),
    ('Net', (-9.1, 0.415, -0.25), (1.24, 0.83, 1.0), 'wood'),
    ('Fender', (10.7, 0.41, -8.87), (1.18, 0.82, 0.50), 'wood'),
]:
    collider(name, center, size, surface)


def primitive(zone, label, center, size, heading=0, mesh='UnitBox'):
    """Store a Godot MultiMesh row-major 3×4 transform."""
    center = on_ground(center)
    record_primitive(zone, label, center, size, (0, heading, 0), mesh)
    x, y, z = center
    width, height, depth = size
    angle = math.radians(heading)
    cos, sin = math.cos(angle), math.sin(angle)
    batches[(zone, label, mesh)].extend(
        round(value, 5) for value in (
            width * cos, 0, depth * sin, x,
            0, height, 0, y,
            -width * sin, 0, depth * cos, z,
        )
    )


def deck(zone, bounds, finish, top=0, thickness=0.6):
    x0, z0, x1, z1 = bounds
    primitive(zone, finish, ((x0+x1)/2, top-thickness/2, (z0+z1)/2),
              (x1-x0, thickness, z1-z0))


def road(name, x0, z0, x1, z1, *, vertical, crossings=(), zone='Roads', bridge=False):
    width, depth = x1-x0, z1-z0
    center = ((x0+x1)/2, (z0+z1)/2)
    roads.append({'name': name, 'center': center, 'size': (width, depth),
                  'kind': 'bridge' if bridge else 'street'})
    road_top = -0.008 if vertical else 0.003
    deck(zone, (x0, z0, x1, z1), 'Asphalt', top=road_top, thickness=0.86 if bridge else 0.035)
    long_start, long_end = (z0, z1) if vertical else (x0, x1)
    midpoint = center[0 if vertical else 1]
    half = (width if vertical else depth)/2
    def stripe(across, along, thick, length, finish):
        position = (across, 0.017, along) if vertical else (along, 0.017, across)
        size = (thick, 0.009, length) if vertical else (length, 0.009, thick)
        primitive(zone, finish, position, size)
    for along in range(math.ceil(long_start)+5, math.floor(long_end)-3, 5):
        if any(abs(along-at) < extent+2.6 for at, extent in crossings):
            continue
        stripe(midpoint, along, 0.12, 2.45, 'PaintAmber')
        if bridge or half >= 5:
            stripe(midpoint-half+0.38, along, 0.10, 4.92, 'PaintIvory')
            stripe(midpoint+half-0.38, along, 0.10, 4.92, 'PaintIvory')


# Land wraps around an inner basin. The original starting quay looks directly onto the water.
for bounds in [
    (-215, -195, -29, 95), (31, -195, 145, 95),
    (-29, -195, 31, -108), (-29, -29, 31, 95),
]:
    deck('Ground', bounds, 'Concrete', top=-0.028, thickness=2.58)
    x0, z0, x1, z1 = bounds
    ground.append({'center': ((x0+x1)/2, (z0+z1)/2), 'size': (x1-x0, z1-z0), 'theme': 'harbor'})
water.append({'center': (1, -68.5), 'size': (60, 79)})
roads_x = [(-155, 6), (-85, 5.5), (-39, 5.5), (40, 5.5), (99, 6)]
roads_z = [(-175, 5), (-119, 6), (33, 6.5), (71, 4.5)]
for name, x, width in zip(
    ['WEST VIADUCT', 'DRYDOCK AVENUE', 'CANAL STREET', 'BERTH ROAD', 'LIGHTHOUSE DRIVE'],
    (x for x, _ in roads_x), (width for _, width in roads_x)
):
    road(name, x-width, -195, x+width, 95, vertical=True, crossings=roads_z)
for name, z, width in zip(
    ['SUPPLY ROAD', 'RAIL EXCHANGE', 'CALLOWAY BOULEVARD', 'STACKYARD ROAD'],
    (z for z, _ in roads_z), (width for _, width in roads_z)
):
    road(name, -215, z-width, 145, z+width, vertical=False, crossings=roads_x)
road('BASIN BRIDGE', -39, -77, 40, -67, vertical=False, crossings=roads_x, bridge=True)
road('PILOT WAY', 40, -41, 145, -31, vertical=False, crossings=roads_x)
road('FREIGHT LANE', -215, -46, -39, -36, vertical=False, crossings=roads_x)
road('CALLOWAY QUAY', -39, 1, -24, 9, vertical=False)
road('BONDED YARD', 24, 2, 40, 11, vertical=False)
road('GATE APPROACH', -3.6, 17, 3.6, 33, vertical=True,
     crossings=[(33, 6.5)])

# Reflective roadside lettering and crosswalks orient drivers at the four corners of the basin.
node('DistrictSigns', 'Node3D')
def sign(name, text, center, angle=0, width=4.0, height=0.62):
    x, y, z = center
    rad = math.radians(angle)
    primitive('Signs', 'DeepTeal', center, (width, height, 0.09), angle)
    position = (x+math.sin(rad)*0.06, y, z+math.cos(rad)*0.06)
    visual_signs.append({'text': text, 'position': position, 'rotation': (0, angle, 0),
                         'width': width, 'height': height})
    node(name, 'Label3D', 'DistrictSigns', position=position,
         rotation_degrees=(0, angle, 0), pixel_size=0.0068,
         font_size=48, outline_size=0, modulate='Color(0.95, 0.89, 0.69, 1)',
         text=json.dumps(text), cast_shadow=0)

for data in [
    ('QuayGuide', 'CALLOWAY QUAY', (26.5, 3.35, -13.5), -30, 6.2),
    ('OutboundGuide', 'MARKET  /  BERTH ROAD', (9, 3.55, 26), 180, 8.2),
    ('WestBridgeGuide', 'BASIN BRIDGE', (-31.4, 3.6, -65.75), -90, 5.0),
    ('EastBridgeGuide', 'BASIN BRIDGE', (34, 3.6, -78.15), 90, 5.0),
    ('NorthGuide', 'RAIL EXCHANGE', (33.5, 3.5, -107), 0, 6.0),
    ('EastGuide', 'PILOT BERTH', (123, 3.5, -33), 90, 5.0),
    ('FreightGuide', 'FREIGHT  /  STACKYARD', (-30, 3.5, 38.8), 0, 8.1),
]:
    name, text, (x, y, z), angle, width = data
    sign(name, text, (x, y, z), angle, width)
    primitive('Signs', 'Shutter', (x, y/2, z), (0.11, y, 0.11))
for x, z in [(-39, 26), (40, 26), (40, -112), (-39, -112), (-85, 26), (-155, 26), (99, 26)]:
    for i in range(9):
        primitive('Roads', 'PaintIvory', (x-4+i*1, 0.025, z), (0.52, 0.009, 1.7))
for x in [-156, -86, -40, 41, 100]:
    for z in range(-174, 82, 34):
        prop(f'VerticalLamp{str(x).replace("-", "W")}_{str(z).replace("-", "N")}',
             'Lamp', (x+6.2, 0.14, z+10), 90, parent='DockKit')
for j, x in enumerate([-198, -176, -132, -109, -64, -22, 16, 64, 126]):
    prop(f'BoulevardLamp{j}', 'Lamp', (x, 0.13, 40.45), 180)
    prop(f'ExchangeLamp{j}', 'Lamp', (x, 0.13, -127.2), 180)


def quay_wall(name, zone, start, end, axis, *, bridge=False, decorative=False):
    """A visual kerb, handrail and aligned boundary keep the water and bridge edges legible."""
    start_point, fixed = start
    length = end-start_point
    at = (end+start_point)/2
    def point(along, y, side=0):
        return (along, y, fixed+side) if axis == 'x' else (fixed+side, y, along)
    def section(thick, height, distance):
        return (distance, height, thick) if axis == 'x' else (thick, height, distance)
    rail_height = 0.95 if bridge else 0.82
    primitive(zone, 'ConcreteEdge', point(at, -0.09), section(0.78, 0.81, length))
    primitive(zone, 'DeepTeal', point(at, rail_height), section(0.09, 0.08, length))
    primitive(zone, 'Shutter', point(at, rail_height*0.60), section(0.065, 0.05, length))
    for index in range(max(2, math.ceil(length/2.5)+1)):
        n = max(1, math.ceil(length/2.5))
        along = start_point + length*index/n
        primitive(zone, 'Shutter', point(along, rail_height/2), (0.10, rail_height, 0.10))
    if not decorative:
        collider(name, point(at, 0.49), section(0.67, 1.02, length), 'metal')


quay_wall('NorthQuay', 'Basin', (-29, -28.91), 31, 'x')
quay_wall('BasinNorth', 'Basin', (-29, -108.10), 31, 'x')
quay_wall('BasinWestNorth', 'Basin', (-108, -28.9), -77, 'z')
quay_wall('BasinWestSouth', 'Basin', (-67, -28.9), -29, 'z')
quay_wall('BasinEastNorth', 'Basin', (-108, 30.9), -77, 'z')
quay_wall('BasinEastSouth', 'Basin', (-67, 30.9), -29, 'z')
quay_wall('BridgeNorth', 'Basin', (-29.45, -76.86), 31.45, 'x', bridge=True)
quay_wall('BridgeSouth', 'Basin', (-29.45, -67.14), 31.45, 'x', bridge=True)
for x in (-22, 24):
    primitive('Basin', 'Shutter', (x, -0.79, -72), (2.1, 2.45, 9))
for x0, z0, x1, z1 in [(140, -195, 145, 95), (-215, 91, 145, 95)]:
    deck('OceanEdge', (x0, z0, x1, z1), 'Sidewalk', top=0.05)
quay_wall('EastPromenade', 'OceanEdge', (-195, 144.72), 95, 'z')
for edge, fixed, axis, start, end, openings in [
    ('SouthPromenade', 94.73, 'x', -215, 145, [(-165, -145), (88, 110)]),
    ('WestPerimeter', -214.74, 'z', -195, 95, [(-181, -168), (-127, -111), (-48, -34), (24, 42), (64, 78)]),
    ('NorthPerimeter', -194.74, 'x', -215, 145, [(-165, -145), (-93, -77), (-47, -31), (32, 48), (89, 109)]),
]:
    for index, (first, last) in enumerate(open_intervals(start, end, openings)):
        quay_wall(f'{edge}{index}', 'OceanEdge', (first, fixed), last, axis)

# Working harbor silhouettes line a dry quay immediately beyond the encounter.
for index, (x, z, scale, heading) in enumerate([
    (-19, -25.8, 1.12, 0), (23.8, -25.5, 1.18, 0),
    (55.5, -96, 1.86, 90), (72.1, -90, 1.53, 90),
    (128, -140, 2.23, 0), (129, -103, 1.95, 0), (133, -44, 1.55, 0),
]):
    prop(f'Gantry{index}', 'Gantry', (x, 0, z), heading, scale)
    for leg in (-1, 1):
        for rail in (-1, 1):
            tx, tz = leg*2.68*scale, rail*1.64*scale
            if heading:
                tx, tz = tz, -tx
            collider(f'Gantry{index}Foot{leg+1}{rail+1}', (x+tx, 0.75, z+tz),
                     (0.86*scale, 1.5, 0.86*scale), 'metal')

for index, (x, z, angle, scale) in enumerate([
    (5.2, -40.8, 74, 1.10), (16.8, -58.5, 0, 1.4), (-7.7, -93.1, -23, 1.32),
    (153.5, -67, 8, 1.2), (154.4, -128, -24, 1.40), (158, 46, 8, 1.04),
]):
    prop(f'PilotTug{index}', 'Tug', (x, -1.03, z), angle, scale)
    for side in (-1, 1):
        primitive('Basin', 'Whitecap', (x+side*scale*1.84, -1.109, z),
                  (0.055, 0.008, scale*5.5), angle, 'Foliage')
for index, x in enumerate([-26.75, -9, 1.2, 17.5, 27.1]):
    prop(f'BasinBollard{index}', 'Bollard', (x, 0, -27.25), 90)
    collider(f'BasinBollard{index}', (x, 0.33, -27.25), (0.57, 0.63, 0.63), 'metal')
for index, z in enumerate([-178, -147, -117, -84, -49, -18, 12, 43, 76]):
    prop(f'EastLoopBollard{index}', 'Bollard', (142, 0.07, z), 0)
    prop(f'LifeRing{index}', 'LifeRing', (143.55, 0.07, z+1.4), 90)
    collider(f'EastLoopBollard{index}', (142, 0.38, z), (0.57, 0.63, 0.63), 'metal')

# Eleven hand-placed obstacles break up the starting combat yard while preserving all approach lanes.
prop('ManifestTerminal', 'Terminal', (8, 0, -3))
collider('ManifestTerminal', (8, 0.75, -3), (0.82, 1.5, 0.68), 'metal')
for index in range(6):
    primitive('YardPaint', 'PaintAmber', (6.88+index*0.45, 0.019, -2.3),
              (0.10, 0.008, 0.64), 31)
primitive('YardPaint', 'PaintAmber', (8, 0.019, -1.95), (2.42, 0.008, 0.075))
prop('QuayEntryLampWest', 'Lamp', (-6.40, 0, 2.87), 45)
prop('QuayEntryLampEast', 'Lamp', (13.91, 0, 2.78), -60)
for x in (-16.4, -8.15):
    primitive('QuayRoof', 'Shutter', (x, 9.28, -10.9), (1.34, 0.4, 1.60))
    primitive('QuayRoof', 'Limestone', (x, 9.60, -10.9), (1.43, 0.25, 1.72))
for index in range(18):
    x = -23.0 + index*2.68
    primitive('YardPaint', 'PaintAmber', (x, 0.021, -27.34), (1.85, 0.008, 0.075))

def cover(name, kind, x, z, heading=0, y=0, *, collision=True, scale=1):
    size, surface = {
        'Barrier': ((2.0, 0.89, 0.62), 'concrete'),
        'Crates': ((2.34, 1.68, 1.24), 'wood'),
        'Container': ((6.16, 2.60, 2.46), 'metal'),
        'Drum': ((0.62, 0.89, 0.62), 'metal'),
        'Dumpster': ((2.00, 1.36, 1.28), 'metal'),
        'Bench': ((1.72, 0.90, 0.55), 'wood'),
        'Shelter': ((3.50, 2.95, 1.60), 'metal'),
        'Barricade': ((1.50, 1.08, 0.86), 'metal'),
        'Pallet': ((1.20, 1.20, 1.00), 'wood'),
        'Spool': ((1.50, 1.50, 0.98), 'wood'),
        'Workbench': ((1.80, 1.90, 0.91), 'wood'),
        'Kiosk': ((0.94, 1.85, 0.85), 'metal'),
    }[kind]
    prop(name, kind, (x, y, z), heading, scale)
    if collision:
        w, h, d = [n * scale for n in size]
        if heading % 180 == 90:
            w, d = d, w
        collider(name, (x, y+h/2, z), (w, h, d), surface)


for name, kind, x, z, heading in [
    ('LowBlockWest', 'Barrier', -0.6, -6.4, 0),
    ('LowBlockCenter', 'Barrier', 5.5, -11.9, 90),
    ('LowBlockEast', 'Barrier', 13.1, -5.9, 0),
    ('ManifestCrates', 'Crates', 9.8, -4.55, 90),
    ('CustomsCrates', 'Crates', 16.2, -9, 0),
    ('TwinPalletCrates', 'Crates', 3.8, -21.5, 90),
    ('EastShieldContainer', 'Container', 23, -14.8, 90),
    ('QuayHoldContainer', 'Container', 7.1, -25.17, 0),
    ('EastOilDrum', 'Drum', 17.5, -15.3, 0),
    ('InspectionDrum', 'Drum', 5.4, -16.0, 0),
    ('RubbishHopper', 'Dumpster', -10.8, -0.28, 0),
]:
    cover(name, kind, x, z, heading)
for j, (x, z) in enumerate([(16.15, 6.5), (24.25, 6.5), (24.25, 14.83), (16.15, 14.83)]):
    for offset in (-0.31, 0, 0.31):
        primitive('YardPaint', 'PaintIvory', (x+offset, 0.021, z), (0.085, 0.009, 1.1), -25)

# The southern town is built around the running lanes, with distinct streetscapes and sight lines.
def urban_building(name, zone, x, z, w, d, height, facade, bays, entrance_sign=None):
    primitive(zone, facade, (x, height/2, z), (w, height, d))
    collider(name, (x, height/2, z), (w, height, d))
    primitive(zone, 'Limestone', (x, 0.21, z), (w+0.34, 0.42, d+0.25))
    primitive(zone, 'ConcreteEdge', (x, height+0.085, z), (w+0.45, 0.20, d+0.45))
    primitive(zone, 'Roof', (x, height+0.20, z), (w+0.19, 0.18, d+0.21))
    floors = max(2, round(height/2.8))
    pitch = height/floors
    for floor in range(floors):
        y = pitch*(floor+0.53)
        for side in (-1, 1):
            for index in range(bays):
                wx = x+(index-(bays-1)/2)*w/(bays+0.4)
                if floor == 0 and index == bays//2:
                    primitive(zone, 'DeepTeal', (wx, 1.04, z+side*(d/2+0.025)),
                              (1.2, 2.04, 0.075))
                    if side == 1:
                        primitive(zone, 'Limestone', (wx, 2.28, z+d/2+0.55),
                                  (1.9, 0.16, 1.25))
                    continue
                tone = 'WarmGlass' if rng.random() < 0.17 else 'Glass'
                sw = min(1.27, w/(bays+2))
                depth = z+side*(d/2+0.027)
                primitive(zone, 'Limestone', (wx, y, depth), (sw+0.17, 1.70, 0.12))
                primitive(zone, tone, (wx, y+0.035, depth+side*0.067), (sw, 1.48, 0.026))
                primitive(zone, 'Limestone', (wx, y-0.78, depth+side*0.07), (sw+0.25, 0.09, 0.22))
            for index in range(max(2, round(d/3.4))):
                wz = z+(index-(max(2, round(d/3.4))-1)/2)*3.05
                primitive(zone, 'Limestone', (x+side*(w/2+0.03), y, wz), (0.085, 1.47, 1.07))
                primitive(zone, 'Glass', (x+side*(w/2+0.082), y+0.02, wz), (0.023, 1.34, 0.93))
        if floor in (0, floors-2) and floors > 3:
            primitive(zone, 'Limestone', (x, (floor+1)*pitch+0.03, z), (w+0.20, 0.17, d+0.20))
    for ix, sign_code in enumerate((-1, 1)):
        primitive(zone, 'Roof', (x+sign_code*w*0.23, height+0.72, z+d*0.12),
                  (1.8, 1.20, 1.6))
        primitive(zone, 'Shutter', (x+sign_code*w*0.23, height+1.40, z+d*0.12),
                  (1.05, 0.18, 1.05), mesh='Cylinder')
    if entrance_sign:
        sign(name+'Address', entrance_sign, (x, pitch+0.44, z+d/2+0.15), 0, min(w-0.4, 6.7))


def factory(name, zone, x, z, w, d, height, finish, label=None):
    primitive(zone, finish, (x, height/2, z), (w, height, d))
    collider(name, (x, height/2, z), (w, height, d), 'metal')
    primitive(zone, 'Roof', (x, height+0.19, z), (w+0.73, 0.38, d+0.72))
    primitive(zone, 'Limestone', (x, 0.31, z), (w+0.12, 0.62, d+0.10))
    for side in (-1, 1):
        for j in range(1, round(w/0.9)):
            wx = x-w/2+j*0.9
            primitive(zone, 'Shutter', (wx, height*0.53, z+side*(d/2+0.035)),
                      (0.055, height*0.9, 0.075))
        for j in range(1, round(w/4.0)):
            wx = x-w/2+j*4
            primitive(zone, 'Glass', (wx, height*0.775, z+side*(d/2+0.071)),
                      (1.90, 0.62, 0.08))
    for off in (-0.28, 0.27):
        sx = x+off*w
        primitive(zone, 'DeepTeal', (sx, 1.91, z+d/2+0.07), (3.5, 3.71, 0.115))
        for row in range(10):
            primitive(zone, 'Shutter', (sx, 0.30+row*0.34, z+d/2+0.134),
                      (3.47, 0.035, 0.020))
        primitive(zone, 'PaintAmber', (sx-1.91, 0.55, z+d/2+0.40), (0.17, 1.1, 0.17))
        primitive(zone, 'PaintAmber', (sx+1.91, 0.55, z+d/2+0.40), (0.17, 1.1, 0.17))
    for offset in (-1, 1):
        primitive(zone, 'Shutter', (x+offset*w*0.29, height+1.36, z+d*0.10),
                  (0.56, 2.4, 0.56), mesh='Cylinder')
        primitive(zone, 'Limestone', (x+offset*w*0.29, height+2.58, z+d*0.10),
                  (0.66, 0.16, 0.66), mesh='Cylinder')
    if label:
        sign(name+'Address', label, (x, height*0.64, z+d/2+0.13), 0, min(w*0.77, 9.0))


def tree(zone, name, x, z, height):
    primitive(zone, 'ConcreteEdge', (x, 0.16, z), (2.50, 0.32, 2.50))
    primitive(zone, 'PlanterSoil', (x, 0.329, z), (2.25, 0.04, 2.25))
    primitive(zone, 'Trunk', (x, height*0.46, z), (0.25, height*0.86, 0.25), mesh='Cylinder')
    for ox, oy, oz, scale, tone in [
        (0, 1.02, 0, 1, 'Moss'), (0.52, .89, 0.4, .88, 'Leaf'),
        (-0.76, .91, 0.33, .91, 'Moss'), (0.67, .92, -.63, .76, 'Leaf'),
        (-.45, 1.10, -.57, .85, 'Leaf'),
    ]:
        primitive(zone, tone, (x+ox, height*oy, z+oz), (scale*2.2, scale*2.5, scale*2.27),
                  mesh='Foliage')
    collider(name, (x, 0.25, z), (2.45, 0.51, 2.45))


# City blocks are placed individually by neighborhood, retaining wide fire lanes and mid-block spaces.
for district, bx0, bx1, bz0, bz1, style in [
    ('FoundryWest', -208, -162, -167, -127, 'factory'),
    ('FoundryCentral', -148, -92, -167, -127, 'factory'),
    ('FoundryCanal', -78, -46, -168, -127, 'factory'),
    ('DepotNorth', -32, 33, -168, -127, 'factory'),
    ('DepotEast', 47, 91, -168, -127, 'factory'),
    ('LighthouseWorks', 107, 136, -167, -127, 'factory'),
    ('SundownNorth', -208, -162, -112, -48, 'town'),
    ('RiveterNorth', -148, -92, -112, -48, 'town'),
    ('CanalNorth', -78, -46, -112, -48, 'town'),
    ('SundownSouth', -207, -162, -34, 24, 'town'),
    ('RiveterSouth', -148, -92, -34, 24, 'town'),
    ('CanalSouth', -78, -46, -34, 24, 'town'),
    ('MarketWest', -208, -162, 42, 64, 'shop'),
    ('MarketCentral', -148, -92, 42, 64, 'shop'),
    ('MarketEast', -78, -46, 42, 64, 'shop'),
    ('MarketQuay', -32, 33, 42, 64, 'shop'),
    ('MarketPilot', 47, 92, 42, 64, 'shop'),
    ('MarketLighthouse', 107, 136, 42, 64, 'shop'),
]:
    deck(district, (bx0, bz0, bx1, bz1), 'Sidewalk', top=0.13)
    deck(district, (bx0+2.6, bz0+2.6, bx1-2.6, bz1-2.6),
         'Pavers' if style != 'factory' else 'Concrete', top=0.133)
    columns = 2 if bx1-bx0 >= 40 else 1
    rows = 2 if bz1-bz0 >= 34 else 1
    for col in range(columns):
        for row in range(rows):
            cellw = (bx1-bx0)/columns
            celld = (bz1-bz0)/rows
            x = bx0+cellw*(col+0.5) + rng.uniform(-1.35, 1.35)
            z = bz0+celld*(row+0.5) + rng.uniform(-1.15, 1.15)
            name = f'{district}Block{col}{row}'
            if style == 'factory':
                title = {'FoundryWest': 'FOUNDRY WEST', 'DepotEast': 'EXCHANGE FREIGHT'}.get(district)
                factory(name, district, x, z, cellw-7.4, celld-5.4, rng.uniform(5.5, 7.5),
                        rng.choice(['StoneBlue', 'Celadon', 'Rust', 'Shutter']),
                        title if row == rows-1 and col == columns-1 else None)
            elif style == 'shop' and rng.random() < 0.56:
                prop(name, 'Shop', (x, 0.134, z), 0, 1.35)
                collider(name, (x, 2.52, z+0.50), (12.66, 4.77, 12.85))
            elif style == 'town' and rng.random() < 0.20:
                prop(name, 'Apartment', (x, 0.13, z), 0, 1.14)
                collider(name, (x+0.50, 8.5, z+0.5), (14.5, 17.0, 12.5))
            else:
                h = rng.uniform(5.1, 8.6) if style == 'shop' else rng.uniform(8.2, 17.4)
                title = {'MarketQuay': 'NIGHT MARKET', 'RiveterSouth': 'CANAL EXCHANGE'}.get(district)
                urban_building(name, district, x, z, cellw-7.15, celld-8.55, h,
                               rng.choice(['RedBrick', 'Terra', 'Dune', 'Celadon', 'OffWhite']),
                               3 if columns == 2 else 5,
                               title if row == rows-1 and col == columns-1 else None)
    if style != 'factory':
        for n, x in enumerate(range(math.ceil(bx0)+5, math.floor(bx1)-2, 15)):
            tree(district, f'{district}Planter{n}', x, bz1-1.9, rng.uniform(2.68, 3.31))
        if style == 'shop':
            cover(district+'Bench', 'Bench', bx0+2.3, bz0+1.76)
            cover(district+'Dumpster', 'Dumpster', bx1-2.8, bz0+1.53, 90)

# Color-coded stalls and lowered awnings give the market a recognizable street frontage.
deck('QuayMarket', (-25.5, 64.4, 26.5, 66.1), 'Pavers', top=0.13)
for n, x in enumerate((-22, -16, -10, -4, 5, 11, 17, 23)):
    z = 64.05
    for side in (-1, 1):
        primitive('QuayMarket', 'Shutter', (x+side*1.79, 1.30, z+0.84),
                  (0.075, 2.60, 0.075))
        primitive('QuayMarket', 'Shutter', (x+side*1.79, 1.30, z-0.90),
                  (0.075, 2.60, 0.075))
    accent = ('Terra', 'Celadon', 'Dune', 'StoneBlue')[n % 4]
    primitive('QuayMarket', 'ConcreteEdge', (x, 2.67, z), (3.70, 0.09, 1.85))
    for r in range(8):
        primitive('QuayMarket', accent if r%2 == 0 else 'OffWhite',
                  (x-1.67+r*0.48, 2.713, z), (0.43, 0.035, 1.93))
    primitive('QuayMarket', 'Limestone', (x, 0.90, z-0.35), (3.00, 0.10, 0.79))
    primitive('QuayMarket', 'Shutter', (x, 0.47, z-0.35), (2.88, 0.84, 0.62))
    collider(f'MarketStall{n}', (x, 0.50, z-0.35), (3.00, 1.0, 0.79))

prop('CanalParking', 'Garage', (-59.5, 0, 81.4), 0, 0.88)
collider('CanalParking', (-58.65, 4.74, 82.0), (28.0, 9.48, 19.2))
sign('GarageDirection', 'CANAL PARKING', (-59.7, 3.1, 91.96), 0, 7.2)
cover('MarketTransitStop', 'Shelter', 0, 41.45)
cover('CanalTransitStop', 'Shelter', -125.5, 41.45)
cover('PilotTransitStop', 'Shelter', 66, 41.45)
for j, x in enumerate([-205, -184, -156, -132, -110, -78, -25, -4, 18, 65, 114, 131]):
    if x not in (-156, -78, 114):
        tree('Promenade', f'SouthPromenadePlanter{j}', x, 88.8, 3.45)
    if x not in (-156, -78):
        cover(f'SouthPromenadeBench{j}', 'Bench', x+3.4, 92.49, 180)

# Container depots and a public promenade contrast with the narrower brick streets across the basin.
for name, x, z, w, d, finish in [
    ('PilotSorting', 68, -48, 25, 18, 'StoneBlue'),
    ('CustomsOffice', 64.5, -12, 17, 13, 'Limestone'),
    ('HarborMachinery', 119, -61, 19.5, 26, 'Celadon'),
    ('SouthService', -118, 82.4, 28, 9.2, 'Rust'),
    ('BondPermit', 68, 10.1, 16.5, 14, 'StoneBlue'),
]:
    deck(name, (x-w/2-3, z-d/2-2.6, x+w/2+3, z+d/2+2.6), 'Sidewalk', top=0.13)
    height = rng.uniform(6.28, 8.1)
    if name not in ('CustomsOffice', 'HarborMachinery'):
        factory(name, name, x, z, w, d, height, finish,
                {'PilotSorting': 'PILOT LOGISTICS', 'BondPermit': 'BONDED FREIGHT'}.get(name))

for index, (x, z, heading) in enumerate([
    (61, -97, 90), (65, -97, 90), (67.8, -88, 0), (57, -84, 0),
    (74, -103, 0), (79.7, -91, 90), (85, -96.1, 90), (88.5, -97, 90),
    (119, -100, 90), (123, -100, 90), (130, -117, 0), (127, -111, 0),
    (89, -25, 90), (89, -20, 90), (89, -12, 90), (85, -7, 0),
    (112, 12, 90), (116.1, 12, 90), (123, -2.5, 0), (124, 4.5, 0),
    (114, 78, 0), (120.5, 80.7, 0), (126.1, 86, 0), (117.6, 84.9, 90),
]):
    kind = ['Celadon', 'Rust', 'StoneBlue', 'DeepTeal'][index % 4]
    cover(f'DepotContainer{index}', 'Container', x, z, heading, collision=False)
    w, d = (2.46, 6.16) if heading else (6.16, 2.46)
    if index % 4 in (1, 2):
        cover(f'DepotContainerUpper{index}', 'Container', x, z, heading, y=2.59, collision=False)
        # Stacked doors receive their own color blocking.
        primitive('DepotTrim', kind, (x, 3.83, z+d/2+0.015), (w*0.92, 2.26, 0.036))
    collider(f'DepotContainer{index}', (x, 2.6 if index % 4 in (1, 2) else 1.3, z),
             (w, 5.2 if index % 4 in (1, 2) else 2.6, d), 'metal')
    panel_x = x+w/2+0.014
    primitive('DepotTrim', kind, (panel_x, 1.27, z), (0.055, 2.32, d*0.94))
    for j in range(round(d/0.30)):
        rib = z-d*.47+j*d*.9/max(1, round(d/0.30)-1)
        primitive('DepotTrim', 'Shutter', (panel_x+0.021, 1.25, rib), (0.04, 2.22, 0.035))
for j, (x, z) in enumerate([
    (49.8, -100), (49.8, -93), (48.5, -54), (86, -47), (110, -11), (124, -17), (111, 80),
]):
    cover(f'StackyardBarrier{j}', 'Barrier', x, z, 90)
    cover(f'StackyardPallet{j}', 'Crates', x+1.9, z-3, 0)

# The channel beacon and distant shores anchor the view during long approaches.
beacon_x, beacon_z = 139.4, -176.5
deck('Beacon', (135.1, -181, 144, -172.2), 'ConcreteEdge', top=0.20)
prop('ChannelBeacon', 'Beacon', (beacon_x, 0.20, beacon_z))
collider('BeaconPlinth', (beacon_x, 0.42, beacon_z), (4.55, 0.43, 4.55))
collider('BeaconTower', (beacon_x, 5.8, beacon_z), (3.38, 11.2, 3.38))
collider('BeaconSteps', (beacon_x, 0.53, beacon_z+2.05), (1.36, 0.66, 1.06))
sign('BeaconSign', 'CHANNEL LIGHT', (138.7, 2.05, -172.45), 0, 3.7)

for j, (x, z, spacing, angle) in enumerate([(196, -106, 7, -12), (-21, 140, 8, -7), (-130, -244, 8, 8)]):
    primitive('FarShore', 'Shore', (x, -0.64, z), (85, 1.8, 12.0), angle)
    for i in range(9):
        height = rng.uniform(4.7, 20)
        sx, sz = x+(i-4)*spacing, z+rng.uniform(-3.1, 2.4)
        primitive('FarShore', rng.choice(['Shore', 'StoneBlue', 'Shutter']), (sx, height/2-.1, sz),
                  (rng.uniform(4.8, 8.2), height, rng.uniform(5.7, 9.2)), rng.uniform(-11, 11))
for j, (x, z) in enumerate([(47, -211), (66, -211), (89, -219), (113, -218), (131, -216)]):
    prop(f'RailGantry{j}', 'Gantry', (x, -0.1, z), 0, 1.85)
deck('FarShore', (29, -227, 149, -202), 'Shore', top=-0.10, thickness=1.5)

def parking_bays(zone, x, z, count, finish, *, angle=0):
    """Paint discrete roadside parking positions without raising the driving surface."""
    radians = math.radians(angle)
    ca, sa = math.cos(radians), math.sin(radians)

    def stripe(u, v, w, d):
        primitive(zone, finish, (x + ca*u + sa*v, 0.025, z - sa*u + ca*v),
                  (w, 0.010, d), angle)

    for index in range(count+1):
        stripe(index*2.73, 0, 0.081, 4.8)
    stripe(count*2.73/2, 2.37, count*2.73, 0.085)


def job_canopy(name, zone, x, z, width, depth, finish, text, *, south=False):
    """Build an open-front timber or metal canopy as a readable street address."""
    for side in (-1, 1):
        sx = x + side*(width/2-0.22)
        for face in (-1, 1):
            sz = z + face*(depth/2-0.21)
            primitive(zone, 'DeepTeal', (sx, 1.62, sz), (0.15, 3.24, 0.15))
            primitive(zone, 'ConcreteEdge', (sx, 0.125, sz), (0.25, 0.25, 0.25))
            collider(f'{name}Post{side+1}{face+1}', (sx, 1.5, sz), (0.23, 3.0, 0.23), 'metal')
        primitive(zone, 'Shutter', (sx, 3.12, z), (0.17, 0.30, depth))
    primitive(zone, 'DeepTeal', (x, 3.30, z), (width, 0.25, depth))
    roof_ribs = max(3, round(width/0.39))
    for index in range(roof_ribs):
        sx = x + (index+0.5)*width/roof_ribs - width/2
        primitive(zone, finish, (sx, 3.46, z), (0.27, 0.09, depth+0.20))
    front = z+(depth/2+0.09)*(1 if south else -1)
    sign(name+'Sign', text, (x, 3.16, front), 0 if south else 180, width-0.13, 0.51)
    for sx in (x-width/4, x+width/4):
        primitive(zone, 'PaintIvory', (sx, 3.14, z-0.63), (0.90, 0.04, 0.15))


# East Stackyard. The active freight lanes run between measured existing crate and container cover.
deck('StackyardJob', (47.2, -109.0, 93.0, -70.7), 'Ballast', top=0.004, thickness=0.057)
deck('StackyardJob', (48.1, -106.0, 91.1, -73.3), 'Concrete', top=0.009, thickness=0.052)
job_canopy('StackyardIntake', 'StackyardJob', 52.8, -74.86, 5.6, 3.14, 'PaintAmber', 'EAST STACKYARD', south=True)
cover('FreightIntakeDesk', 'Workbench', 51.8, -74.4)
cover('FreightIntakeCart', 'Pallet', 55.4, -74.2)
for index, (x, z, shape, angle) in enumerate([
    (51.9, -84.8, 'Barricade', 0), (66.0, -83.9, 'Crates', 0), (64.1, -93.1, 'Pallet', 90),
    (75.6, -88.3, 'Pallet', 90), (79.5, -81.1, 'Barrier', 0), (88.0, -85.2, 'Drum', 0),
    (78.57, -102.2, 'Crates', 0), (83.1, -102.7, 'Pallet', 90),
]):
    cover(f'FreightWorksiteCover{index}', shape, x, z, angle)
for x in (52.45, 58.55, 69.59, 74.61):
    primitive('StackyardJob', 'Joint', (x, 0.020, -91.8), (0.23, 0.015, 28.9))
    primitive('StackyardJob', 'Shutter', (x, 0.026, -91.8), (0.071, 0.014, 28.9))
for i in range(8):
    primitive('StackyardJob', 'PaintAmber', (54.48+i*0.55, 0.028, -79.53),
              (0.18, 0.011, 0.83), -36)
sign('FreightHoldSign', 'CARGO HOLD 04', (79.72, 3.44, -103.73), 0, 5.0, 0.56)
for index, x in enumerate((77.65, 82.32)):
    prop(f'HoldCone{index}', 'Cone', (x, 0.009, -101.25))


# Pilot Quay. Hoses, winches and a short breakwater establish a separate coastal worksite.
deck('RecoveryJob', (114.0, -30.25, 144.33, -5.9), 'Concrete', top=0.012, thickness=0.11)
deck('RecoveryJob', (140.1, -30.65, 144.29, -5.8), 'Sidewalk', top=0.075)
job_canopy('PilotQuayOffice', 'RecoveryJob', 133.35, -25.3, 4.6, 2.90, 'StoneBlue', 'PILOT QUAY 02')
cover('PilotRecoveryDesk', 'Workbench', 132.77, -25.18)
cover('PilotQuayWinch', 'Spool', 141.24, -15.7, 90, 0.075)
for index, (x, z, shape, angle) in enumerate([
    (125.1, -26.1, 'Crates', 0), (125.1, -17.45, 'Barrier', 90), (127.3, -12.4, 'Pallet', 90),
    (135.6, -20.8, 'Barricade', 0), (136.2, -8.8, 'Container', 0),
    (141.9, -8.37, 'Pallet', 90), (123.3, -8.9, 'Drum', 0),
]):
    cover(f'PilotRecoveryCover{index}', shape, x, z, angle)
for index in range(4):
    prop(f'PilotQuayCone{index}', 'Cone', (142.55, 0.075, -20.4+index*2.01), 22*index)
for x in (116.0, 126.5, 139.45):
    primitive('RecoveryJob', 'PaintAmber', (x, 0.024, -21.5), (0.089, 0.009, 14.7))
prop('PilotQuayLaunch', 'Tug', (154.1, -1.03, -17.4), 13, 1.17)


# West Market Dispatch. The bays are separate from westbound traffic and public walkways.
deck('DispatchJob', (-170.0, 39.61, -160.7, 46.45), 'Pavers', top=0.14)
job_canopy('WestMarketDispatch', 'DispatchJob', -165.8, 43.12, 8.2, 4.29, 'Terra', 'WEST MARKET DISPATCH')
cover('MarketCourierWorktop', 'Workbench', -166.5, 44.73, y=0.14)
cover('MarketCourierKiosk', 'Kiosk', -162.72, 44.6, 180, 0.14)
prop('MarketDispatchPaperBox', 'Newspaper', (-169.1, 0.16, 40.74), 180)
for x in (-169.25, -165.77, -162.29):
    primitive('DispatchJob', 'PaintIvory', (x, 0.156, 41.03), (0.075, 0.01, 1.88))
primitive('DispatchJob', 'PaintAmber', (-165.8, 0.156, 40.23), (7.0, 0.009, 0.11))
sign('DispatchBlueAddress', 'SUNDOWN COURIER', (-171.15, 2.12, 42.03), 180, 5.0, 0.48)
deck('DispatchJob', (-161.01, 8.30, -157.3, 24.45), 'Asphalt', top=0.004, thickness=0.045)
for z in (8.57, 14.12, 19.91, 24.18):
    primitive('DispatchJob', 'PaintIvory', (-159.1, 0.024, z), (3.36, 0.010, 0.086))
primitive('DispatchJob', 'PaintAmber', (-160.85, 0.024, 16.375), (0.078, 0.010, 15.6))
sign('DispatchReturnSign', 'DISPATCH TURNOUT', (-161.56, 2.58, 19.2), 90, 3.90, 0.49)
primitive('DispatchJob', 'Shutter', (-161.56, 1.29, 19.2), (0.11, 2.58, 0.11))


# Basin time trial. Raised shoulders are visual only and leave both travel lanes centered.
deck('BridgeWalkway', (-29.30, -76.45, 31.36, -75.2), 'Sidewalk', top=0.062, thickness=0.08)
deck('BridgeWalkway', (-29.30, -68.85, 31.36, -67.55), 'Sidewalk', top=0.062, thickness=0.08)
deck('CanalWalkway', (-33.4, -65.5, -30.58, -34.35), 'Sidewalk', top=0.094)
deck('CanalWalkway', (-33.35, 11.0, -30.56, 26.08), 'Sidewalk', top=0.094)
for z in (-68.87, -75.18):
    primitive('BridgeWalkway', 'PaintIvory', (1.0, 0.038, z), (61.0, 0.011, 0.075))
sign('BasinTimingAddress', 'BASIN TIMING POINT', (-30.42, 2.72, -62.62), -90, 5.40)
for z in (-64.72, -60.51):
    primitive('CanalWalkway', 'DeepTeal', (-30.34, 1.37, z), (0.14, 2.74, 0.13))
    prop(f'TrackTimingCone{str(z).replace("-", "N").replace(".", "_")}', 'Cone', (-34.02, 0.01, z))
for row in range(2):
    for i in range(17):
        if (row+i) % 2 == 0:
            primitive('BridgeWalkway', 'PaintIvory', (-43.50+i*0.53, 0.024, -71.20+row*0.51),
                      (0.43, 0.01, 0.40))
            primitive('BridgeWalkway', 'PaintIvory', (-43.50+i*0.53, 0.024, -79.27+row*0.51),
                      (0.43, 0.01, 0.40))


# A branded loading stop and drop-off bay bookend Lighthouse Delivery.
deck('BeaconDelivery', (64.8, 19.3, 83.3, 27.65), 'Asphalt', top=0.003, thickness=0.056)
parking_bays('BeaconDelivery', 67.2, 24.8, 5, 'PaintIvory')
job_canopy('BondedExpress', 'BeaconDelivery', 67.4, 19.64, 4.8, 3.32, 'Celadon', 'BONDED EXPRESS', south=True)
cover('BondedExpressCart', 'Pallet', 66.07, 19.2)
cover('BondedExpressBox', 'Crates', 69.03, 19.46, 90)
deck('BeaconDelivery', (127.8, -171.78, 142.55, -165.3), 'Asphalt', top=0.004, thickness=0.056)
parking_bays('BeaconDelivery', 130.7, -168.71, 4, 'PaintAmber')
sign('BeaconParcelDrop', 'BEACON DELIVERY', (141.8, 2.05, -166.21), 180, 4.75, 0.53)
prop('BeaconReceivingCone', 'Cone', (141.5, 0.006, -171.10))
prop('BeaconReceivingParcel', 'Pallet', (140.73, 0.006, -167.27), 90)

# Sundown Market: an outdoor row of food and repair stalls with a separate seating terrace.
deck('SundownMarket', (-208.15, 75.9, -164.5, 87.1), 'Pavers', top=0.085)
deck('SundownMarket', (-208.15, 75.45, -164.5, 76.05), 'ConcreteEdge', top=0.10)
deck('SundownMarket', (-208.15, 83.55, -164.5, 84.1), 'Sidewalk', top=0.088)
for index, (x, finish, label) in enumerate([
    (-203.5, 'Terra', 'FISH & CHIPS'), (-195.8, 'BluePaint', 'MARKET COFFEE'),
    (-187.8, 'Moss', 'GREENSTAND'), (-179.8, 'Dune', 'BIKE REPAIR'), (-171.8, 'Rust', 'FRESH BREAD'),
]):
    job_canopy(f'MarketVendor{index}', 'SundownMarket', x, 79.48, 4.68, 4.20, finish, label)
    for panel in range(8):
        stripe = finish if panel % 2 else 'OffWhite'
        primitive('SundownMarket', stripe, (x-2.07+panel*0.59, 3.43, 77.06),
                  (0.55, 0.11, 0.71), 0)
    prop(f'MarketVendorBox{index}', 'Crates', (x-1.05, 0.085, 80.8), 90, 0.65)
    cover(f'MarketVendorCounter{index}', 'Workbench', x+0.45, 79.9, 180, 0.085)
    cover(f'MarketTerraceBench{index}', 'Bench', x-0.3, 85.65, 0, 0.085)
    primitive('SundownMarket', 'Dune', (x-0.3, 0.76, 84.65), (0.77, 0.10, 0.77), mesh='Cylinder')
    primitive('SundownMarket', 'Shutter', (x-0.3, 0.41, 84.65), (0.12, 0.79, 0.12))
    collider(f'MarketTerraceTable{index}', (x-0.3, 0.43, 84.65), (0.76, 0.76, 0.76), 'wood')
sign('MarketTerraceAddress', 'SUNDOWN MARKET', (-186.2, 4.55, 76.32), 180, 11.7, 0.74)
for x in (-191.83, -180.57):
    primitive('SundownMarket', 'Shutter', (x, 2.23, 76.35), (0.14, 4.46, 0.15))
node('MarketBunting', 'Node3D')
poles = [(-206.84, 4.92), (-187.45, 4.88), (-166.49, 4.92)]
for index, (x, height) in enumerate(poles):
    primitive('SundownMarket', 'Shutter', (x, height/2, 83.20), (0.14, height, 0.14))
    collider(f'MarketBuntingPole{index}', (x, height/2, 83.20), (0.16, height, 0.16), 'metal')
for span, (left, right) in enumerate(zip(poles, poles[1:])):
    for segment in range(10):
        x0 = left[0] + (right[0]-left[0])*segment/10
        x1 = left[0] + (right[0]-left[0])*(segment+1)/10
        y0 = left[1] - 0.60*math.sin(math.pi*segment/10)
        y1 = left[1] - 0.60*math.sin(math.pi*(segment+1)/10)
        x, y = (x0+x1)/2, (y0+y1)/2
        length = math.hypot(x1-x0, y1-y0)
        angle = math.degrees(math.atan2(y1-y0, x1-x0))
        box(f'Cable{span}{segment}', (x, y, 83.2), (length+0.015, 0.032, 0.032),
            'Shutter', 'MarketBunting', (0, 0, angle))
        primitive('SundownMarket', ('Terra', 'OffWhite', 'BluePaint', 'PaintAmber')[(span+segment)%4],
                  (x, y-0.237, 83.20), (0.34, 0.49, 0.026), 9*math.sin(segment))


# Foundry Row: colored entry bays and awnings above a residential pocket park south of the market.
for facade_name, number, tint in [
    ('SundownSouthBlock00', '12', 'Terra'),
    ('SundownSouthBlock11', '18', 'Moss'),
    ('RiveterSouthBlock00', '26', 'BluePaint'),
    ('RiveterSouthBlock11', '32', 'Rust'),
]:
    facade_box = next(obstacle for obstacle in obstacles if obstacle['tag'] == facade_name)
    x, _, z = facade_box['center']
    w, h, d = facade_box['size']
    front_z = z+d/2
    for entry in (-0.27, 0.0, 0.27):
        e = x+w*entry
        primitive('FoundryRow', tint, (e, 1.26, front_z+0.115), (2.06, 2.37, 0.11))
        primitive('FoundryRow', 'DeepTeal', (e, 1.18, front_z+0.17), (1.19, 2.10, 0.08))
        primitive('FoundryRow', 'Limestone', (e, 2.49, front_z+0.39), (2.17, 0.13, 0.90))
        primitive('FoundryRow', tint, (e, 2.47, front_z+0.85), (2.15, 0.15, 0.095))
    for fraction in (-0.42, 0.42):
        primitive('FoundryRow', tint, (x+w*fraction, h*0.55, front_z+0.09),
                  (0.15, h*0.73, 0.11))
    sign('FoundryRow'+number, 'CANAL ROW  '+number, (x, 3.32, front_z+0.22), 0, 5.15, 0.53)

deck('CanalPark', (-30.0, 77.25, 34.3, 86.65), 'PlanterSoil', top=0.074, thickness=0.085)
deck('CanalPark', (-30.0, 79.02, 34.3, 81.66), 'Sidewalk', top=0.096, thickness=0.10)
deck('CanalPark', (-8.24, 76.5, -5.82, 86.7), 'Sidewalk', top=0.101, thickness=0.10)
deck('CanalPark', (12.0, 76.5, 14.42, 86.7), 'Sidewalk', top=0.101, thickness=0.10)
for index, (x, z) in enumerate([
    (-25.5, 84.57), (-16.0, 84.51), (-12.3, 78.0), (8.31, 78.0),
    (20.1, 84.37), (30.4, 84.55),
]):
    tree('CanalPark', f'CanalParkTree{index}', x, z, 3.03+index%3*0.36)
for index, x in enumerate((-20.9, -10.2, 5.7, 17.1, 25.45)):
    cover(f'CanalParkBench{index}', 'Bench', x, 83.58, 180, 0.095)
for index, (start_x, end_x) in enumerate([(-29.4, -21.8), (-19.4, -9.3), (-4.9, -1.2), (3.8, 12.3), (16.2, 33.7)]):
    cx = (start_x+end_x)/2
    primitive('CanalPark', 'Moss', (cx, 0.48, 86.17), (end_x-start_x, 0.82, 0.67))
    collider(f'CanalParkHedge{index}', (cx, 0.48, 86.17), (end_x-start_x, 0.82, 0.67), 'wood')
pivot = (1.3, 83.9)
for finish, y, width, height in [('Limestone', 0.25, 4.35, 0.45),
                                  ('BluePaint', 0.49, 3.94, 0.085),
                                  ('Limestone', 0.53, 1.13, 0.29),
                                  ('StoneBlue', 0.92, 0.41, 0.67)]:
    primitive('CanalPark', finish, (pivot[0], y, pivot[1]), (width, height, width), mesh='Cylinder')
collider('CanalParkFountain', (pivot[0], 0.34, pivot[1]), (4.20, 0.64, 4.20))
sign('CanalParkAddress', 'FOUNDRY GARDENS', (-9.8, 2.28, 77.45), 180, 5.4, 0.63)
primitive('CanalPark', 'Shutter', (-9.8, 1.14, 77.47), (0.09, 2.28, 0.09))


# East coast: separate landscaped overlooks on the public path, with a working breakwater offshore.
for index, z in enumerate((-157.0, -126.6, -91.03, -58.75)):
    deck('HarborWalk', (137.8, z-3.2, 144.35, z+3.2), 'Sidewalk', top=0.087, thickness=0.102)
    deck('HarborWalk', (141.6, z-2.46, 144.46, z+2.46), 'Pavers', top=0.105, thickness=0.13)
    cover(f'OverlookBench{index}', 'Bench', 143.06, z, -90, 0.105)
    prop(f'OverlookLamp{index}', 'Lamp', (143.79, 0.097, z-2.86), 90)
    prop(f'OverlookLifeRing{index}', 'LifeRing', (144.13, 0.105, z+2.02), -90)
    for end in (-1, 1):
        primitive('HarborWalk', 'ConcreteEdge', (143.02, 0.172, z+end*2.53),
                  (2.43, 0.18, 0.13))
for name, z, text in [
    ('ChannelOverlook', -155, 'CHANNEL LOOKOUT'), ('SouthOverlook', -89, 'HARBOR WALK'),
]:
    sign(name, text, (143.81, 2.26, z), -90, 4.37, 0.55)
    primitive('HarborWalk', 'Shutter', (143.98, 1.12, z), (0.10, 2.24, 0.10))
for index in range(16):
    x = 166.5+index*3.15
    z = -163.0+0.45*index
    primitive('Breakwater', 'StoneBlue' if index%3 else 'Shore',
              (x, -0.87, z), (4.0, 2.20, 4.9), 18-index*3.64)
    primitive('Breakwater', 'Shutter', (x+1.05, -1.09, z+2.5),
              (2.81, 1.29, 2.67), 31+index*13.6)
for x, z in ((164.4, -164.0), (213.6, -155.5)):
    primitive('Breakwater', 'Limestone', (x, 0.43, z), (1.08, 0.45, 1.08), mesh='Cylinder')
    primitive('Breakwater', 'RedPaint', (x, 1.38, z), (0.51, 1.52, 0.51), mesh='Cylinder')
    primitive('Breakwater', 'PaintAmber', (x, 2.18, z), (0.41, 0.23, 0.41), mesh='Cylinder')


for name, position in [
    ('CarSpawn', (5, 0, 9)),
    ('MissionTerminal', (8, 0, -3)),
    ('ExtractionPoint', (18.5, 0, 11)),
    ('PoliceApproachNorth', (-37.8, 0, -38)),
    ('PoliceApproachEast', (30, 0, -6)),
    ('PoliceApproachSouth', (-4, 0, 31)),
    ('PoliceApproachBerth', (41, 0, 23)),
]:
    node(name, 'Marker3D', position=position)


def place_interiors():
    node('Interiors', 'Node3D')
    active = []
    for room in interiors:
        slug = room['id']
        position, district = interior_placements[slug]

        def world(local):
            return tuple(round(base + offset, 5) for base, offset in zip(position, local))

        prop(slug, slug, position, parent='Interiors')
        for index, (center, size, surface) in enumerate(room['colliders']):
            collider(f'{slug}_{index:02}', world(center), size, surface)
        for index, light in enumerate(room['lights']):
            color = [int(light['color'][start:start+2], 16) / 255 for start in (0, 2, 4)]
            node(f'{slug}_light_{index}', 'OmniLight3D', 'Interiors', position=world(light['position']),
                 light_color=f'Color({color[0]}, {color[1]}, {color[2]}, 1)', light_energy=light['energy'],
                 omni_range=light['range'], omni_attenuation=1.35)
        active.append({'id': slug, 'name': room['name'], 'position': position, 'district': district, 'kind': 'interior'})
    return active


def terrain_surfaces():
    materials = {label: (color, scale, strength, grit) for label, color, scale, strength, grit in TERRAIN_MATERIALS}
    for patch in TERRAIN:
        name = patch['id']
        w, d = patch['size']
        color, scale, strength, grit = materials[patch['material']]
        sub('ShaderMaterial', name + 'Surface', shader='ExtResource("PavingShader")', **{
            'shader_parameter/base_color': f'Color({color}, 1)',
            'shader_parameter/stain_scale': scale,
            'shader_parameter/stain_strength': strength,
            'shader_parameter/grit_strength': grit,
            'shader_parameter/terrain_height': patch['height'],
            'shader_parameter/terrain_half_size': f'Vector2({w/2}, {d/2})',
        })
        sub('PlaneMesh', name + 'Mesh', size=f'Vector2({w}, {d})', subdivide_width=64, subdivide_depth=56,
            material=f'SubResource("{name}Surface")')


def serialize():
    active_interiors = place_interiors()
    interior_ids = {room['id'] for room in active_interiors}
    lookouts = [{'id': patch['id'], 'name': patch['name'], 'district': patch['district'], 'kind': 'lookout',
                 'position': (patch['center'][0], patch['height'], patch['center'][1])} for patch in TERRAIN]
    node_sections = scene[scene_nodes_start:]
    del scene[scene_nodes_start:]
    terrain_surfaces()
    for index, obstacle in enumerate(obstacles):
        sub('BoxShape3D', f'Shape{index:03}', size=obstacle['size'])
    for index, ((zone, material_name, mesh), buffer) in enumerate(batches.items()):
        sub('MultiMesh', f'Batch{index:03}', transform_format=1,
            mesh=f'SubResource("{mesh}")', instance_count=len(buffer)//12,
            buffer='PackedFloat32Array(' + ', '.join(map(str, buffer)) + ')')
    scene.extend(node_sections)
    node('Terrain', 'Node3D')
    for patch in TERRAIN:
        x, z = patch['center']
        w, d = patch['size']
        node(patch['id'], 'MeshInstance3D', 'Terrain', position=(x, -.027, z),
             mesh=f'SubResource("{patch["id"]}Mesh")', cast_shadow=0,
             custom_aabb=f'AABB({-w/2}, -0.05, {-d/2}, {w}, {patch["height"]+.10}, {d})')
    node('DistrictGeometry', 'Node3D')
    for index, (zone, material_name, mesh) in enumerate(batches):
        no_shadow = zone in ('Roads', 'Ground', 'YardPaint') or zone.startswith(('Ground_', 'Road_', 'Markings_')) or material_name in (
            'PaintAmber', 'PaintIvory', 'Whitecap', 'WarmGlass', 'Glass', 'PlanterSoil',
        )
        node(zone+material_name+mesh, 'MultiMeshInstance3D', 'DistrictGeometry',
             multimesh=f'SubResource("Batch{index:03}")', material_override=f'SubResource("{material_name}")',
             cast_shadow=0 if no_shadow else 1)
    path = ROOT / 'game/scenes/world/HarborFront.tscn'
    path.write_text(f'[gd_scene load_steps={ext_count + sub_count + 1} format=3]\n\n' +
                    '\n'.join(scene), encoding='utf-8')
    layout = {'spawn': SPAWN, 'bounds': BOUNDS, 'waterLevel': WATER_LEVEL, 'roads': roads, 'water': water,
              'ground': ground, 'districts': DISTRICTS,
              'landmarks': [item for item in LANDMARKS if item['id'] not in interior_ids] + active_interiors + lookouts,
              'terrain': [{key: patch[key] for key in ('center', 'size', 'height', 'theme')} for patch in TERRAIN],
              'obstacles': obstacles}
    write_json(ROOT / 'data/world/harbor_front.json', layout)
    visuals = {'version': 1, 'waterLevel': WATER_LEVEL, 'materials': visual_materials, 'primitives': visual_primitives,
               'assets': visual_assets, 'signs': visual_signs}
    write_json(ROOT / 'data/world/harbor_visuals.json', visuals, compact=True)


def write_json(path, document, *, compact=False):
    # Keep each marker on a single line so changes in building and cover placement remain reviewable.
    lines = ['{']
    for index, (key, value) in enumerate(document.items()):
        if isinstance(value, list):
            separators = (',', ':') if compact else None
            items = [f'    {json.dumps(item, separators=separators)}' for item in value]
            serialized = '[\n' + ',\n'.join(items) + '\n  ]'
        else:
            serialized = json.dumps(value)
        suffix = '' if index == len(document) - 1 else ','
        lines.append(f'  "{key}": {serialized}{suffix}')
    lines.append('}')
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


if __name__ == '__main__':
    expand(primitive, collider, prop, sign, deck, quay_wall, roads, water, ground)
    serialize()
