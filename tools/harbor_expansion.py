"""Lay out the districts surrounding the original Calloway Quay."""

from math import atan2, ceil, cos, degrees, hypot, pi, sin
from random import Random

TERRAIN_MATERIALS = [
    ('ForestFloor', '0.24, 0.29, 0.21', 0.15, 0.88, 0.22),
    ('ForestPath', '0.43, 0.36, 0.28', 0.42, 0.73, 0.18),
    ('DesertSand', '0.67, 0.46, 0.32', 0.10, 0.67, 0.21),
    ('DesertTrack', '0.49, 0.32, 0.24', 0.26, 0.79, 0.16),
    ('MesaClay', '0.53, 0.29, 0.19', 0.23, 0.74, 0.20),
    ('MesaCap', '0.65, 0.42, 0.29', 0.26, 0.67, 0.18),
    ('CityPaving', '0.43, 0.45, 0.45', 0.38, 0.50, 0.08),
    ('SluiceYard', '0.40, 0.35, 0.31', 0.30, 0.87, 0.21),
    ('Airfield', '0.32, 0.33, 0.29', 0.32, 0.76, 0.13),
    ('VillageGrass', '0.32, 0.41, 0.30', 0.18, 0.58, 0.11),
    ('VillageStone', '0.62, 0.59, 0.49', 0.37, 0.49, 0.09),
]

DISTRICTS = [
    {'id': 'harbor_front', 'name': 'Harbor Front', 'theme': 'harbor', 'center': (-35, -50), 'size': (360, 290)},
    {'id': 'glass_heights', 'name': 'Glass Heights', 'theme': 'city', 'center': (-17.5, -397.5), 'size': (395, 405)},
    {'id': 'old_sluice', 'name': 'Old Sluice', 'theme': 'low_income', 'center': (-442.5, -65), 'size': (455, 260)},
    {'id': 'cinder_flats', 'name': 'Cinder Flats', 'theme': 'desert', 'center': (-442.5, -397.5), 'size': (455, 405)},
    {'id': 'breakwater_station', 'name': 'Breakwater Station', 'theme': 'military', 'center': (400, -397.5), 'size': (440, 405)},
    {'id': 'alderwash', 'name': 'Alderwash', 'theme': 'forest', 'center': (-442.5, 287.5), 'size': (455, 445)},
    {'id': 'lantern_point', 'name': 'Lantern Point', 'theme': 'wealthy', 'center': (202.5, 340), 'size': (835, 340)},
]

LANDMARKS = [
    ('calloway_quay', 'Calloway Quay', (0, 0, 5), 'harbor_front', 'harbor'),
    ('basin_bridge', 'Basin Bridge', (0, 0, -72), 'harbor_front', 'bridge'),
    ('exchange_plaza', 'Exchange Plaza', (-108, 0, -390), 'glass_heights', 'plaza'),
    ('meridian_tower', 'Meridian Tower', (-12, 0, -384), 'glass_heights', 'tower'),
    ('sluice_market', 'Sluice Market', (-353, 0, -89), 'old_sluice', 'market'),
    ('salvage_rows', 'Secondhand Works', (-521, 0, -5), 'old_sluice', 'workshop'),
    ('ochre_steps', 'Ochre Steps', (-577, 0, -554), 'cinder_flats', 'mesa'),
    ('relay_stop', 'Cinder Relay', (-336, 0, -298), 'cinder_flats', 'outpost'),
    ('breakwater_field', 'Breakwater Airfield', (470, 0, -386), 'breakwater_station', 'airfield'),
    ('station_hangars', 'Coastal Hangars', (332, 0, -391), 'breakwater_station', 'hangar'),
    ('alderwash_lodge', 'Alderwash Lodge', (-373, 0, 240), 'alderwash', 'lodge'),
    ('fern_glade', 'Fern Glade', (-514, 0, 307), 'alderwash', 'glade'),
    ('lantern_square', 'Lantern Square', (28, 0, 348), 'lantern_point', 'plaza'),
    ('garden_terrace', 'Garden Terrace', (473, 0, 325), 'lantern_point', 'garden'),
    ('west_causeway', 'Alderwash Causeway', (-155, 0, 134), 'lantern_point', 'bridge'),
    ('lighthouse_causeway', 'Lighthouse Causeway', (99, 0, 134), 'lantern_point', 'bridge'),
]
LANDMARKS = [dict(zip(('id', 'name', 'position', 'district', 'kind'), item)) for item in LANDMARKS]

# Roads are authored by centerline. X runs east and Z runs south.
ROADS = [
    ('West Viaduct', 15, 'arterial', [(-155, -195), (-155, -561)]),
    ('Archive Avenue', 13, 'street', [(-70, -242), (-70, -530)]),
    ('Meridian Avenue', 13, 'street', [(22, -242), (22, -561)]),
    ('City Lighthouse Drive', 15, 'arterial', [(99, -195), (105, -242), (105, -561)]),
    ('Northern Exchange', 17, 'arterial', [(-265, -242), (-215, -256), (-170, -256), (-155, -242), (105, -242), (180, -268), (224, -268)]),
    ('Market Connector', 12, 'street', [(-85, -195), (-85, -218), (-70, -242)]),
    ('Basin Connector', 12, 'street', [(-39, -195), (-39, -220), (-43, -242)]),
    ('Berth Connector', 12, 'street', [(40, -195), (40, -218), (22, -242)]),
    ('Civic Street', 13, 'street', [(-155, -345), (180, -345), (224, -345)]),
    ('Union Boulevard', 19, 'arterial', [(-595, -440), (105, -440), (180, -424), (224, -424)]),
    ('Observatory Avenue', 16, 'arterial', [(-265, -530), (180, -530), (224, -530), (550, -530)]),
    ('North Terrace', 11, 'street', [(-155, -561), (105, -561)]),
    ('East Arcade', 9, 'street', [(154, -543), (154, -304)]),
    ('Cinder Main', 13, 'street', [(-595, -440), (-599, -381), (-591, -327), (-565, -275), (-530, -247), (-460, -242)]),
    ('Salt Country Road', 14, 'arterial', [(-265, -530), (-265, -238), (-285, -199), (-285, -175)]),
    ('Ochre Pass', 9, 'service', [(-595, -440), (-632, -479), (-623, -511), (-594, -521), (-508, -487), (-474, -441)]),
    ('Relay Track', 10, 'service', [(-460, -440), (-449, -373), (-437, -323), (-414, -289), (-388, -276), (-300, -276), (-265, -287)]),
    ('Sluice High Street', 15, 'arterial', [(-580, -156), (-580, 70), (-590, 128), (-580, 174)]),
    ('Furnace Road', 11, 'street', [(-450, -178), (-450, 72)]),
    ('Sluice Back Road', 14, 'street', [(-285, -175), (-285, 145)]),
    ('Sluice Supply Road', 12, 'street', [(-460, -242), (-460, -198), (-450, -175), (-215, -175)]),
    ('Rail Exchange West', 15, 'arterial', [(-640, -119), (-215, -119)]),
    ('Freight Lane West', 11, 'street', [(-580, -41), (-215, -41)]),
    ('Calloway Boulevard West', 17, 'arterial', [(-640, 33), (-215, 33)]),
    ('Stackyard Road West', 12, 'street', [(-450, 71), (-215, 71)]),
    ('Alderwash Parkway', 14, 'arterial', [(-580, 174), (-562, 224), (-581, 278), (-581, 361), (-570, 413), (-541, 438), (-215, 438)]),
    ('Fern Road', 12, 'street', [(-285, 145), (-301, 205), (-290, 275), (-282, 339), (-282, 438)]),
    ('Lodge Road', 11, 'street', [(-564, 221), (-499, 207), (-436, 212), (-376, 208), (-300, 210)]),
    ('Ranger Cut', 8, 'service', [(-581, 350), (-535, 361), (-490, 365), (-450, 349), (-412, 347), (-364, 365), (-282, 373)]),
    ('Fern Glade Trail', 4, 'path', [(-536, 214), (-523, 250), (-506, 277), (-514, 307), (-498, 345), (-491, 365)]),
    ('Alderwalk Trail', 4, 'path', [(-394, 209), (-417, 257), (-397, 290), (-401, 327), (-412, 347)]),
    ('Alderwash Causeway', 16, 'bridge', [(-155, 95), (-155, 170)]),
    ('Lighthouse Causeway', 17, 'bridge', [(99, 95), (99, 170)]),
    ('Lantern West', 14, 'street', [(-155, 170), (-155, 438)]),
    ('Lantern Lighthouse Drive', 16, 'arterial', [(99, 170), (99, 438)]),
    ('Harbor View Avenue', 17, 'arterial', [(-155, 224), (434, 224), (518, 245), (554, 287)]),
    ('Garden View', 14, 'street', [(385, 224), (385, 438)]),
    ('Seabank Road', 15, 'arterial', [(-215, 438), (482, 438), (540, 420), (568, 379), (568, 326), (554, 287)]),
    ('Lantern Square West', 11, 'street', [(-56, 224), (-56, 438)]),
    ('Terrace Walk', 5, 'path', [(385, 326), (438, 326), (473, 325), (517, 335), (568, 340)]),
    ('Station Road', 17, 'arterial', [(224, -530), (224, -221)]),
    ('Station Perimeter', 15, 'street', [(224, -268), (550, -268), (550, -530)]),
    ('Hangar Service Road', 12, 'street', [(224, -345), (388, -345), (388, -529)]),
    ('Barracks Road', 12, 'street', [(224, -424), (388, -424)]),
    ('Breakwater Taxiway', 16, 'service', [(388, -268), (388, -345)]),
    ('Breakwater Runway', 28, 'runway', [(470, -556), (470, -247)]),
]

LAND = {
    'city': 'CityPaving', 'low_income': 'SluiceYard', 'forest': 'ForestFloor',
    'desert': 'DesertSand', 'military': 'Airfield', 'wealthy': 'VillageGrass',
}


def open_intervals(start, end, openings):
    """Return the remaining pieces of an edge after cutting road entrances."""
    cursor = start
    for begin, finish in sorted(openings):
        if begin > cursor:
            yield cursor, min(begin, end)
        cursor = max(cursor, finish)
    if cursor < end:
        yield cursor, end


def road_clearance(x, z, segment):
    ax, az = segment['from']
    bx, bz = segment['to']
    px, pz = bx-ax, bz-az
    progress = max(0, min(1, ((x-ax)*px+(z-az)*pz)/(px*px+pz*pz)))
    return hypot(x-ax-progress*px, z-az-progress*pz)-segment['size'][0]/2


class Expansion:
    def __init__(self, primitive, collider, prop, sign, deck, wall, roads, water, ground):
        self.primitive = primitive
        self.collider = collider
        self.prop = prop
        self.sign = sign
        self.deck = deck
        self.wall = wall
        self.roads = roads
        self.water = water
        self.ground = ground
        self.random = Random(2961)
        self.segments = []
        for name, width, kind, points in ROADS:
            for (ax, az), (bx, bz) in zip(points, points[1:]):
                self.segments.append({'name': name, 'kind': kind, 'from': (ax, az), 'to': (bx, bz),
                                      'center': ((ax+bx)/2, (az+bz)/2), 'size': (width, hypot(bx-ax, bz-az)),
                                      'yaw': degrees(atan2(bx-ax, bz-az))})

    def district_ground(self):
        for district in DISTRICTS[1:]:
            x, z = district['center']
            w, d = district['size']
            theme = district['theme']
            self.deck('Ground_'+theme, (x-w/2, z-d/2, x+w/2, z+d/2), LAND[theme], top=-0.024, thickness=2.6)
            self.ground.append({k: district[k] for k in ('center', 'size', 'theme')})
        self.water.extend([
            {'center': (202.5, 132.5), 'size': (835, 75)},
            {'center': (382.5, -50), 'size': (475, 290)},
        ])

    def build_roads(self):
        self.roads.extend(self.segments)
        for index, segment in enumerate(self.segments):
            ax, az = segment['from']
            bx, bz = segment['to']
            x, z = segment['center']
            width, length = segment['size']
            fx, fz = (bx-ax)/length, (bz-az)/length
            yaw, kind = segment['yaw'], segment['kind']
            layer = -0.004 + (index%8)*0.001
            zone = f'Road_{int(x//128)}_{int(z//128)}'
            paint = f'Markings_{int(x//128)}_{int(z//128)}'
            finish = 'ForestPath' if kind == 'path' else ('DesertTrack' if kind == 'service' and x < -250 and z < -195 else 'Asphalt')
            extended = length + min(2.5, width*0.3)
            if kind in ('arterial', 'street') and x > -215 and z < -195 and x < 180:
                self.primitive(zone, 'VillageStone', (x, layer-0.002, z), (width+4.0, 0.020, extended), yaw)
            if kind == 'bridge':
                self.primitive(zone, 'ConcreteEdge', (x, -0.44, z), (width+1.0, 0.88, length+0.2), yaw)
                for side in (-1, 1):
                    rail_x = x+width*side/2
                    self.wall(f'NewBridge{index}{side+1}', zone, (min(az, bz), rail_x), max(az, bz), 'z', bridge=True)
                for support in (z-20, z+20):
                    self.primitive(zone, 'StoneBlue', (x, -1.2, support), (width*0.85, 2.30, 2.6))
            self.primitive(zone, finish, (x, layer+0.014, z), (width, 0.027, extended), yaw)
            if kind in ('path', 'service'):
                continue
            interval = 11 if kind == 'runway' else 7
            for distance in range(5, ceil(length)-3, interval):
                sx, sz = ax+fx*distance, az+fz*distance
                if any(road_clearance(sx, sz, other) < 1.9 for other in self.segments if other is not segment):
                    continue
                self.primitive(paint, 'PaintIvory' if kind == 'runway' else 'PaintAmber', (sx, 0.042, sz),
                               (0.38 if kind == 'runway' else 0.13, 0.009, interval*0.57), yaw)
                if kind in ('bridge', 'arterial', 'runway'):
                    for side in (-1, 1):
                        offset = side*(width/2-0.48)
                        self.primitive(paint, 'PaintIvory', (sx+fz*offset, 0.043, sz-fx*offset),
                                       (0.12, 0.009, interval-0.04), yaw)
            if kind == 'runway':
                for end in (10, length-11):
                    for row in range(10):
                        offset = (row-4.5)*1.85
                        self.primitive(paint, 'PaintIvory', (ax+fx*end+fz*offset, 0.046, az+fz*end-fx*offset),
                                       (0.9, 0.01, 5.6), yaw)

    def shoreline(self):
        edges = [
            ('NorthGround', 'x', -600, -670, 620, []),
            ('WestGround', 'z', -670, -600, 510, []),
            ('AirfieldEast', 'z', 620, -600, -195, []),
            ('AirfieldShore', 'x', -195, 145, 620, []),
            ('AlderwashCove', 'z', -215, 95, 170, []),
            ('LanternInlet', 'x', 170, -215, 620, [(-165, -145), (88, 110)]),
            ('LanternEast', 'z', 620, 170, 510, []),
            ('LanternSouth', 'x', 510, -670, 620, []),
        ]
        for name, axis, fixed, start, end, gaps in edges:
            for index, (first, last) in enumerate(open_intervals(start, end, gaps)):
                self.wall(f'{name}{index}', f'Coast_{name}', (first, fixed), last, axis)
        for index, (x, z) in enumerate([(-165, 119), (109, 153), (272, 160), (402, 157), (541, 160)]):
            self.prop(f'LanternBuoy{index}', 'Beacon', (x, -1.2, z), 18*index, 0.9)

    def clear(self, x, z, margin):
        return min(road_clearance(x, z, road) for road in self.segments) > margin

    def model(self, zone, name, center, size, material, shape='UnitBox', collision='concrete'):
        self.primitive(zone, material, center, size, mesh=shape)
        if collision:
            self.collider(name, center, size, collision)

    def label(self, tag, text, x, z, yaw=0):
        self.sign(f'{tag}Welcome', text, (x, 2.8, z), yaw, min(19, max(7.5, len(text)*0.49)), 0.82)
        for side in (-1, 1):
            offset = min(3, len(text)*0.17)*side
            theta = yaw*pi/180
            self.primitive('StreetFurniture', 'Shutter', (x+offset*cos(theta), 1.34, z-offset*sin(theta)),
                           (0.13, 2.68, 0.13))

    def branches(self, zone, name, x, z, h, pine):
        trunk = max(0.27, h*0.049)
        self.model(zone, name, (x, h*0.27, z), (trunk, h*0.54, trunk), 'Trunk', 'Cylinder', 'wood')
        if pine:
            for level, width, height, tint in [(0.46, .68, .62, 'PineDark'), (0.67, .52, .60, 'PineLight'),
                                               (0.86, .37, .56, 'PineDark')]:
                self.primitive(zone, tint, (x, h*level, z), (h*width, h*height, h*width), mesh='PineCone')
        else:
            for ox, oz, height, width, tint in [(0.0, 0, .89, .63, 'Moss'), (-.16, -.11, .78, .52, 'Leaf'),
                                                (.19, .20, .74, .56, 'PineLight')]:
                self.primitive(zone, tint, (x+ox*h, h*height, z+oz*h), (h*width, h*.54, h*width), mesh='Foliage')

    def building(self, zone, name, x, z, w, d, height, facade, style):
        self.model(zone, name, (x, height/2, z), (w, height, d), facade)
        self.primitive(zone, 'IvoryStone' if style in ('tower', 'villa') else 'ConcreteEdge',
                       (x, 0.32, z), (w+.12, 0.64, d+.12))
        self.primitive(zone, 'Roof', (x, height+0.08, z), (w+.5, .19, d+.5))
        floor_count = max(1, round(height/(4.0 if style == 'tower' else 2.7)))
        for floor in range(floor_count):
            y = (floor+.57)*height/floor_count
            for side in (-1, 1):
                front = z+side*(d/2+.045)
                east = x+side*(w/2+.045)
                if style == 'tower':
                    for cx, cz, window in [(x, front, (w*.91, height/floor_count*.70, .11)),
                                           (east, z, (.11, height/floor_count*.70, d*.91))]:
                        self.primitive(zone, 'SkyGlass' if floor%4 == 0 else 'BlueGlass', (cx, y, cz), window)
                else:
                    columns = max(2, round(w/(4.3 if style == 'hangar' else 3.9)))
                    for bay in range(columns):
                        cx = x+(bay-(columns-1)/2)*w/(columns+.38)
                        if floor == 0 and bay == columns//2:
                            self.primitive(zone, 'Shutter', (cx, 1.04, front), (min(w*.12, 3.8), 2.08, .11))
                        else:
                            tint = 'WarmGlass' if (floor+bay)%5 == 0 else 'Glass'
                            self.primitive(zone, tint, (cx, y, front), (1.3, min(1.31, height*.29), .12))
                            self.primitive(zone, 'IvoryStone' if style == 'villa' else 'ConcreteEdge',
                                           (cx, y-.66, front+side*.06), (1.52, .10, .20))
                    for nz in (-.27, .27):
                        self.primitive(zone, 'Glass', (east, y, z+nz*d), (.11, 1.16, 1.38))
        if style == 'tower':
            for side in (-1, 1):
                for third in (-1, 0, 1):
                    self.primitive(zone, 'IvoryStone', (x+third*w*.27, height*.53, z+side*(d/2+.12)),
                                   (.23, height*.94, .23))
                for third in (-1, 0, 1):
                    self.primitive(zone, 'IvoryStone', (x+side*(w/2+.12), height*.53, z+third*d*.27),
                                   (.23, height*.94, .23))
            self.model(zone, name+'Crown', (x, height+1.35, z), (w*.58, 2.5, d*.53), 'StoneBlue')
            self.primitive(zone, 'BlueGlass', (x, height+2.71, z), (w*.46, .23, d*.44))
        elif style in ('villa', 'lodge', 'hangar'):
            cap = min(4.3, w*.16)
            self.primitive(zone, 'Terracotta' if style == 'villa' else 'Shutter', (x, height+cap/2, z),
                           (w+1.1, cap, d+1.02), mesh='RoofPrism')
            if style == 'villa':
                self.primitive(zone, 'Limestone', (x+w*.23, height+cap*.81, z-d*.21), (1.08, cap*.92, 1.1))
                self.primitive(zone, 'IvoryStone', (x, 0.16, z+d/2+2.3), (4.35, .32, 4.6))
            if style == 'hangar':
                self.primitive(zone, 'Limestone', (x, height*.41, z+d/2+.10), (w*.65, height*.78, .14))
                for i in range(9):
                    self.primitive(zone, 'Shutter', (x+(i-4)*w*.068, height*.41, z+d/2+.19),
                                   (.11, height*.76, .05))
        else:
            self.primitive(zone, 'Shutter', (x+w*.24, height+.56, z+d*.2), (1.46, 1.10, 1.43))
            self.primitive(zone, 'RedBrick', (x-w*.22, height+.6, z-d*.19), (.67, 1.2, .69))

    def fountain(self, zone, tag, x, z, size):
        self.model(zone, tag, (x, .43, z), (size, .86, size), 'IvoryStone', 'Cylinder')
        self.primitive(zone, 'BlueGlass', (x, .89, z), (size*.86, .08, size*.86), mesh='Cylinder')
        self.primitive(zone, 'IvoryStone', (x, 1.6, z), (size*.17, 1.4, size*.17), mesh='Cylinder')
        self.primitive(zone, 'BluePaint', (x, 2.35, z), (size*.3, .17, size*.30), mesh='Cylinder')

    def city(self):
        for row, z in enumerate((-510, -466, -411, -375, -319, -274)):
            for column, x in enumerate((-192, -130, -92, -44, -4, 49, 76, 131, 169)):
                if (-151 < x < -72 and -432 < z < -356) or not self.clear(x, z, 10):
                    continue
                if (x, z) in [(-4, -411), (-4, -375), (131, -466), (49, -319)]:
                    continue
                tag = f'GlassBlock{row}{column}'
                zone = f'Glass_{row//2}_{column//3}'
                w = self.random.uniform(12.0, 19.8)
                d = self.random.uniform(15.0, 23.0)
                h = [17, 21, 31, 37, 46, 24, 58][(row*4+column)%7] + self.random.uniform(-2, 3)
                self.building(zone, tag, x, z, w, d, h, 'StoneBlue' if column%3 else 'Limestone', 'tower')
                self.prop(f'{tag}Lamp', 'Lamp', (x+w/2+1.2, 0, z+d/2+.8), 90)
        self.building('Meridian', 'MeridianTower', -12, -384, 28, 35, 86, 'BlueGlass', 'tower')
        self.model('Meridian', 'MeridianSpire', (-12, 98, -384), (.82, 18, .82), 'Shutter', 'Cylinder', 'metal')
        self.primitive('Meridian', 'RedPaint', (-12, 107.2, -384), (1.38, .6, 1.38), mesh='Foliage')
        self.deck('Ground_Civic', (-144, -426, -79, -356), 'VillageStone', top=.038, thickness=.12)
        self.fountain('Civic', 'ExchangeFountain', -110, -389, 9.8)
        for index, (x, z) in enumerate([(-138, -421), (-86, -421), (-138, -361), (-86, -361), (-127, -388), (-94, -388)]):
            self.branches('Civic', f'ExchangeTree{index}', x, z, 6, False)
            self.prop(f'ExchangeSeat{index}', 'Bench', (x, .04, z+4), 0)
        self.label('Exchange', 'EXCHANGE PLAZA', -110, -420, 180)
        self.label('City', 'GLASS HEIGHTS', -144, -225)

    def sluice(self):
        for row, z in enumerate((-148, -89, -67, -15, 12)):
            for column, x in enumerate((-631, -605, -552, -523, -494, -469, -422, -394, -365, -336, -306, -259, -234)):
                if (-377 < x < -319 and -103 < z < -75) or (-548 < x < -483 and -25 < z < 24):
                    continue
                if not self.clear(x, z, 8):
                    continue
                tag = f'SluiceBlock{row}_{column}'
                zone = f'Sluice_{row//2}_{column//4}'
                w, d = self.random.uniform(9.7, 15.0), self.random.uniform(10.2, 15.6)
                h = self.random.choice([3.4, 4.1, 5.2, 6.7, 7.8, 8.6])
                facade = ['Celadon', 'StoneBlue', 'Terra', 'Limestone', 'RedBrick'][((row*7)+column)%5]
                self.building(zone, tag, x, z, w, d, h, facade, 'rowhouse')
                if column%3 == 0:
                    self.primitive(zone, 'Terracotta', (x-.18, 2.24, z+d/2+.62), (w*.72, .24, 1.15))
                    self.prop(tag+'Bin', 'Dumpster', (x+w/2+1.2, 0, z-.8), 180)
        self.deck('Ground_Sluice', (-381, -105, -317, -77), 'Pavers', top=.034, thickness=.10)
        for index, x in enumerate(range(-376, -316, 10)):
            for side in (-1, 1):
                z = -91+side*8.5
                tag = f'MarketStall{index}_{side+1}'
                self.model('SluiceMarket', tag, (x, .62, z), (4.4, 1.24, 2.2), 'Trunk', collision='wood')
                for ox in (-2.05, 2.05):
                    for oz in (-1, 1):
                        self.primitive('SluiceMarket', 'Trunk', (x+ox, 1.72, z+oz), (.13, 3.44, .13))
                self.primitive('SluiceMarket', 'Terra' if index%2 else 'BluePaint', (x, 3.25, z),
                               (5.3, .43, 3.25), mesh='RoofPrism')
        for j, (x, z, w, h) in enumerate([(-526, -16, 26, 6.8), (-491, -16, 24, 6.2), (-522, 13, 25, 7.2)]):
            self.building('SluiceWorks', f'SalvageShed{j}', x, z, w, 11.8, h, 'Rust', 'hangar')
        for index, (x, z) in enumerate([(-539, 7), (-509, 8), (-501, 15), (-481, 11), (-476, -1)]):
            self.prop(f'SalvageStack{index}', 'Pallet', (x, 0, z), 55*index)
        self.label('Sluice', 'OLD SLUICE', -229, 44)
        self.label('SluiceMarket', 'SLUICE MARKET', -352, -87)
        self.label('SluiceWorks', 'SECONDHAND WORKS', -518, 3)

    def forest(self):
        occupied = []
        for _ in range(850):
            x, z = self.random.uniform(-653, -228), self.random.uniform(80, 492)
            if not self.clear(x, z, 5.6) or hypot(x+373, z-240) < 29 or hypot(x+514, z-307) < 37:
                continue
            if any((ox-x)**2+(oz-z)**2 < 8.5**2 for ox, oz in occupied):
                continue
            occupied.append((x, z))
            zone = f'Forest_{int(x//90)}_{int(z//90)}'
            h = self.random.uniform(5.8, 13.9)
            self.branches(zone, f'AlderTree{len(occupied)}', x, z, h, self.random.random() < .74)
        understory = Random(265)
        for index in range(380):
            ox, oz = understory.choice(occupied)
            x, z = ox+understory.uniform(-13, 13), oz+understory.uniform(-13, 13)
            if not (-652 < x < -229 and 81 < z < 491) or not self.clear(x, z, 3.2):
                continue
            if hypot(x+373, z-240) < 27 or hypot(x+514, z-307) < 34:
                continue
            zone = f'Forest_{int(x//90)}_{int(z//90)}'
            if index%3 == 0 and all((cx-x)**2+(cz-z)**2 > 6.1**2 for cx, cz in occupied):
                self.branches(zone, f'AlderSapling{index}', x, z, understory.uniform(3.6, 8.2), True)
                occupied.append((x, z))
            for dx, dz, color in [(-.9, .31, 'Leaf'), (.4, -.5, 'PineLight'), (1.05, .3, 'Moss')]:
                self.primitive(zone, color, (x+dx, .21, z+dz), (1.55, .73, 1.23), mesh='Foliage')
            if index%7 == 0:
                self.primitive(zone, 'Limestone', (x-.35, .12, z-.8), (1.29, .38, .98), index*47, 'Foliage')
        for index, (x, z, w, d, h) in enumerate([(-643, 257, 26, 45, 6), (-628, 278, 28, 35, 8), (-630, 311, 31, 27, 5),
                                               (-320, 472, 40, 32, 6), (-355, 485, 49, 27, 8), (-483, 144, 35, 41, 8),
                                               (-509, 137, 22, 30, 6), (-259, 89, 24, 20, 6)]):
            self.primitive('ForestRelief', 'PineDark', (x, .6, z), (w+8, h*.68, d+7), mesh='Foliage')
            self.primitive('ForestRelief', 'StoneBlue', (x, h*.26, z), (w, h, d), mesh='Foliage')
            self.collider(f'AlderBluff{index}', (x, h*.21, z), (w*.68, h*.65, d*.68), 'earth')
        self.deck('Ground_Lodge', (-398, 220, -347, 266), 'ForestPath', top=.01, thickness=.10)
        self.building('Lodge', 'AlderLodge', -375, 246, 28, 20, 5.8, 'Trunk', 'lodge')
        for index, x in enumerate((-392, -358, -350)):
            self.prop(f'LodgeBench{index}', 'Bench', (x, .01, 228), 0)
        self.label('Ranger', 'ALDERWASH FOREST', -571, 145, 180)
        self.label('Lodge', 'ALDERWASH LODGE', -373, 225, 180)
        for index, (x, z) in enumerate([(-488, 302), (-527, 294), (-535, 312), (-515, 331)]):
            self.primitive('FernGlade', 'Leaf', (x, .07, z), (3.2, .38, 2.7), mesh='Foliage')
            self.primitive('FernGlade', 'Limestone', (x-2, .38, z-2), (1.9, .85, 2), mesh='Foliage')
        self.label('Fern', 'FERN GLADE', -505, 310, 90)

    def desert(self):
        mesas = [(-592, -561, 91, 44, 27), (-511, -566, 64, 43, 19), (-446, -573, 59, 35, 15),
                 (-647, -379, 33, 77, 20), (-641, -321, 40, 52, 26), (-613, -258, 57, 45, 13),
                 (-328, -479, 71, 55, 31), (-399, -488, 49, 35, 13), (-381, -376, 72, 49, 22),
                 (-515, -353, 61, 43, 15), (-526, -388, 39, 26, 10), (-590, -226, 59, 25, 9)]
        for index, (x, z, w, d, h) in enumerate(mesas):
            zone = f'CinderRelief_{index//3}'
            self.primitive(zone, 'MesaClay', (x, h*.11, z), (w*1.06, h*.39, d*1.03), 12+index*9, 'Foliage')
            for dx, dz, height in [(-.21, -.08, .84), (.13, .12, .63), (.21, -.11, .77)]:
                for step, span in enumerate((1.0, .9, .83, .72, .57)):
                    band = h*height
                    pos = (x+dx*w+step*.61, band*(.16+step*.175), z+dz*d-step*.56)
                    size = (w*span*(.61-abs(dx)), band*.25, d*span*.56)
                    self.primitive(zone, 'MesaCap' if step in (2, 4) else 'MesaClay', pos, size, 15+index*9+step*7, 'Cylinder')
            self.collider(f'CinderMesa{index}', (x, h*.16, z), (w*.70, h*.50, d*.67), 'earth')
        for index in range(170):
            x, z = self.random.uniform(-650, -231), self.random.uniform(-584, -212)
            if not self.clear(x, z, 4) or hypot(x+335, z+298) < 25:
                continue
            if index%3 == 0:
                self.primitive('CinderScrub', 'Moss', (x, .24, z), (1.6, .73, 1.42), mesh='Foliage')
            else:
                size = self.random.uniform(.4, 3.8)
                self.primitive('CinderRocks', 'SandstoneLight' if index%2 else 'Dune', (x, size*.10, z),
                               (size, size*.54, size*1.2), index*48, 'Foliage')
        self.building('CinderRelay', 'RelayShop', -334, -302, 14, 11, 4.4, 'OffWhite', 'rowhouse')
        self.model('CinderRelay', 'RelayMast', (-352, 15.3, -302), (.8, 30.6, .8), 'Shutter', 'Cylinder', 'metal')
        for level in (7.0, 17, 26, 30):
            self.primitive('CinderRelay', 'OffWhite', (-352, level, -302), (4.7, .43, .44))
            self.primitive('CinderRelay', 'RedPaint', (-352, level, -302), (.43, .43, 4.7))
        self.label('Cinder', 'CINDER FLATS', -252, -314)
        self.label('CinderRelay', 'CINDER RELAY', -333, -286)
        self.label('Ochre', 'OCHRE STEPS', -601, -535, 180)

    def airfield(self):
        for index, z in enumerate((-493, -396, -310)):
            self.building('Hangars', f'CoastalHangar{index}', 333, z, 51.5, 42, 10.6+index, 'UniformOlive', 'hangar')
            self.deck('Ground_Hangar' + str(index), (304, z+22, 367, z+33), 'Concrete', top=.036, thickness=.10)
        for row, z in enumerate((-499, -458, -390, -309)):
            for col, x in enumerate((259, 285, 520, 581)):
                tag = f'StationBarracks{row}{col}'
                self.building('Barracks', tag, x, z, 14.2, 23.8, 4.2, 'UniformOlive' if col%2 else 'OffWhite', 'lodge')
        self.model('Control', 'ControlTowerBase', (422, 13, -404), (8.4, 26, 8.4), 'Limestone')
        self.model('Control', 'ControlDeck', (422, 27.5, -404), (13.4, 3.2, 12), 'BlueGlass')
        self.primitive('Control', 'Limestone', (422, 29.55, -404), (14.1, .76, 13))
        self.primitive('Control', 'RedPaint', (422, 33.3, -404), (.52, 6.8, .52), mesh='Cylinder')
        self.primitive('Control', 'OffWhite', (422, 35, -404), (4.2, .42, 1.46))
        for index, (x, z) in enumerate([(586, -561), (556, -570), (284, -562)]):
            self.model('AirfieldFuel', f'StationTank{index}', (x, 3.6, z), (8.2, 7.2, 8.2), 'OffWhite', 'Cylinder', 'metal')
            self.primitive('AirfieldFuel', 'Rust', (x, 5.12, z), (8.4, .38, 8.4), mesh='Cylinder')
        for index, (x, z) in enumerate([(419, -365), (419, -341)]):
            tag = f'CoastalTrainer{index}'
            self.model('AirfieldApron', tag, (x, 1.51, z), (2.7, 2.34, 11.2), 'StoneBlue', 'Foliage', 'metal')
            self.primitive('AirfieldApron', 'BlueGlass', (x, 2.77, z+.66), (1.52, .94, 3.8), mesh='Foliage')
            self.primitive('AirfieldApron', 'UniformOlive', (x, 2.07, z-.45), (17.2, .35, 4.4), mesh='Foliage')
            self.primitive('AirfieldApron', 'Limestone', (x, 2.28, z-4.32), (5.1, .25, 2.03), mesh='Foliage')
            self.primitive('AirfieldApron', 'Rust', (x, 2.84, z-4.32), (2.10, 2.43, .37), 90, 'RoofPrism')
            for side in (-1, 1):
                self.primitive('AirfieldApron', 'Rubber', (x+1.04*side, .48, z-.45), (.48, .97, .97), mesh='Foliage')
                self.primitive('AirfieldApron', 'PaintIvory', (x+7.2*side, 2.21, z-.45), (.31, .06, 1.61))
        self.label('Station', 'BREAKWATER STATION', 198, -331, -90)
        self.label('StationHangar', 'COASTAL HANGARS', 328, -369)
        self.label('StationRunway', 'BREAKWATER FIELD', 452, -282, 90)
        for index, z in enumerate(range(-543, -258, 15)):
            for side in (-1, 1):
                x = 470+side*15.8
                self.primitive('AirfieldMarker', 'BluePaint' if index%3 else 'RedPaint', (x, .24, z), (.24, .48, .24))
        for edge, openings in [(-543, [(214, 233), (380, 399), (450, 491), (539, 561)]),
                               (-246, [(214, 233), (450, 491), (543, 557)])]:
            for index, (x0, x1) in enumerate(open_intervals(235, 602, openings)):
                self.wall(f'AirfieldFence{abs(edge)}_{index}', 'AirfieldFence', (x0, edge), x1, 'x')

    def village(self):
        for row, z in enumerate((197, 265, 312, 376, 405, 475)):
            for column, x in enumerate((-187, -117, -81, -26, 24, 69, 136, 191, 249, 302, 351, 426, 479, 528)):
                if not self.clear(x, z, 14.4) or (x > 402 and 279 < z < 401) or (-36 < x < 69 and 280 < z < 412):
                    continue
                zone = f'Lantern_{row//2}_{column//4}'
                tag = f'Villa{row}_{column}'
                w, d = self.random.uniform(13.5, 18.5), self.random.uniform(12.4, 18.6)
                facade = ['OffWhite', 'IvoryStone', 'Limestone', 'Celadon'][column%4]
                self.building(zone, tag, x, z, w, d, 4.5+1.8*(column%3), facade, 'villa')
                self.deck('Ground_'+zone, (x-w*.58, z-d*.63, x+w*.58, z+d*.89), 'VillageStone', top=.019, thickness=.09)
                for side in (-1, 1):
                    self.branches(zone, tag+'Cypress'+str(side+1), x+side*(w/2+2.5), z+d*.54, 5.3, True)
                self.primitive(zone, 'Moss', (x, .42, z-d*.78), (w+.9, .84, .67))
        self.deck('Ground_LanternSquare', (-27, 301, 72, 395), 'VillageStone', top=.036, thickness=.12)
        self.fountain('LanternSquare', 'LanternFountain', 28, 348, 12.5)
        for index, (x, z) in enumerate([(-17, 311), (61, 311), (-17, 383), (61, 383), (-3, 325), (50, 371)]):
            self.branches('LanternSquare', f'SquareTree{index}', x, z, 6.2, False)
            self.prop(f'SquareBench{index}', 'Bench', (x+3, .03, z), 90)
        self.deck('Ground_Terrace', (433, 299, 499, 355), 'VillageStone', top=.029, thickness=.11)
        for x, z, w, d in [(439, 323, 2.1, 49), (494, 323, 2.1, 49), (465, 300, 51, 2.1), (465, 352, 51, 2.1)]:
            self.primitive('LanternTerrace', 'Pavers', (x, .041, z), (w, .016, d))
        self.fountain('LanternTerrace', 'TerraceFountain', 473, 309, 7.6)
        self.fountain('LanternTerrace', 'TerraceEastFountain', 491, 345, 7.6)
        for index, (x, z) in enumerate([(448, 308), (490, 299), (437, 340), (454, 347), (512, 365), (526, 356)]):
            self.branches('LanternTerrace', f'TerraceCypress{index}', x, z, 7.6, True)
        self.label('LanternEast', 'LANTERN POINT', 111, 187, 180)
        self.label('LanternSquare', 'LANTERN SQUARE', 23, 304, 180)
        self.label('GardenTerrace', 'GARDEN TERRACE', 456, 317, 180)


def expand(primitive, collider, prop, sign, deck, wall, roads, water, ground):
    city = Expansion(primitive, collider, prop, sign, deck, wall, roads, water, ground)
    city.district_ground()
    city.build_roads()
    city.shoreline()
    city.city()
    city.sluice()
    city.forest()
    city.desert()
    city.airfield()
    city.village()
