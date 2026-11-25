#!/usr/bin/env python3
"""Generates the Port Calloway play map shared by server and client (ADR 0008).

Reads activity/economy data (objective points, turf zones, supply sites, safehouses) and asset footprints
(data/world/assets.json, measured from the imported .glb by tools/map/measure_assets.gd), then writes:
  data/world/map.json       roads, district areas, building/prop placements
  data/ai/encounters.json   NPC groups (gangs at turf, Ironclad at heist targets)

Deterministic (fixed seed). Re-run after changing activity positions or assets:
  python3 tools/map/build_map.py
"""
import glob
import json
import math
import os
import random

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "data")
rng = random.Random(20261125)


def load(rel):
    with open(os.path.join(DATA, rel), encoding="utf-8") as f:
        return json.load(f)


ASSETS = load("world/assets.json")["assets"]

# District rectangles [minX, minZ, maxX, maxZ] around the authored activity points.
DISTRICTS = {
    "old_mill": [-700, 200, -260, 480],
    "harbor_front": [110, -430, 420, -60],
    "glass_heights": [720, 400, 1060, 760],
}
BOUNDS = [-760, -520, 1120, 820]
WATER_Z = -450  # harbour waterline: south of this is sea

# ------------------------------------------------------------------ gather points of interest
poi = []  # (x, z, clearance, kind, extra)
for path in sorted(glob.glob(os.path.join(DATA, "activities", "contracts", "*.json"))):
    with open(path, encoding="utf-8") as f:
        c = json.load(f)
    for o in c["objectives"]:
        x, _, z = o["at"]
        poi.append((x, z, 9 if o["type"] != "escape" else 4, "objective", (c["id"], o)))
for zone in load("activities/turf.json")["zones"]:
    x, _, z = zone["at"]
    poi.append((x, z, zone["radius"] + 3, "turf", zone))
for site in load("activities/supply_drops.json")["sites"]:
    x, _, z = site["at"]
    poi.append((x, z, 10, "supply", site))
for sh in load("world/safehouses.json")["safehouses"]:
    x, _, z = sh["position"]
    poi.append((x, z, 12, "safehouse", sh))

placements = []
occupied = []  # (minx, minz, maxx, maxz)


def footprint(asset, x, z, rot):
    a = ASSETS[asset]
    mn, sz = a["min"], a["size"]
    corners = [(mn[0], mn[2]), (mn[0] + sz[0], mn[2] + sz[2])]
    out = []
    for cx, cz in corners:
        if rot == 1:
            cx, cz = cz, -cx
        elif rot == 2:
            cx, cz = -cx, -cz
        elif rot == 3:
            cx, cz = -cz, cx
        out.append((cx, cz))
    return (min(out[0][0], out[1][0]) + x, min(out[0][1], out[1][1]) + z,
            max(out[0][0], out[1][0]) + x, max(out[0][1], out[1][1]) + z)


def overlaps(r, margin=0.0):
    for o in occupied:
        if r[0] - margin < o[2] and r[2] + margin > o[0] and r[1] - margin < o[3] and r[3] + margin > o[1]:
            return True
    return False


def near_poi(r, extra=0.0):
    for (px, pz, clr, _, _) in poi:
        cx = min(max(px, r[0]), r[2])
        cz = min(max(pz, r[1]), r[3])
        if (cx - px) ** 2 + (cz - pz) ** 2 < (clr + extra) ** 2:
            return True
    return False


def place(asset, x, z, rot=0, y=0.0, force=False, margin=0.4):
    r = footprint(asset, x, z, rot)
    if not force and (overlaps(r, margin) or r[1] < WATER_Z):
        return False
    placements.append({"asset": asset, "pos": [round(x, 2), round(y, 2), round(z, 2)], "rot": rot})
    if ASSETS[asset]["solid"]:
        occupied.append(r)
    return True


# ------------------------------------------------------------------ roads
def road(a, b, w=12):
    return {"from": [a[0], a[1]], "to": [b[0], b[1]], "width": w}


roads = [
    # trunk: Old Mill → Harbor Front → Glass Heights
    road((-700, 340), (-100, 340), 14), road((-100, 340), (-100, -200), 14), road((-100, -200), (600, -200), 14),
    road((600, -200), (600, 600), 14), road((600, 600), (1060, 600), 14),
]
for z in (250, 420):
    roads.append(road((-700, z), (-260, z)))
for x in (-600, -480, -350):
    roads.append(road((x, 200), (x, 480)))
for z in (-300, -110):
    roads.append(road((110, z), (420, z)))
for x in (200, 300):
    roads.append(road((x, -430), (x, -60)))
for z in (520, 700):
    roads.append(road((720, z), (1060, z)))
for x in (800, 900, 1000):
    roads.append(road((x, 400), (x, 760)))


def road_hit(r, pad=1.5):
    for rd in roads:
        (x0, z0), (x1, z1), w = rd["from"], rd["to"], rd["width"] / 2 + pad
        rx0, rx1 = min(x0, x1) - w, max(x0, x1) + w
        rz0, rz1 = min(z0, z1) - w, max(z0, z1) + w
        if r[0] < rx1 and r[2] > rx0 and r[1] < rz1 and r[3] > rz0:
            return True
    return False


# ------------------------------------------------------------------ objective props (what the player interacts with)
def objective_prop(label, crime):
    l = label.lower()
    if "register" in l:
        return "prop_cash_register"
    if "atm" in l:
        return "prop_atm"
    if "vault" in l or "safe" in l:
        return "prop_safe"
    if "container" in l:
        return "prop_shipping_container"
    if "van" in l or "drill" in l:
        return "prop_weapon_crate"
    return "prop_laptop_hack"


for (x, z, _, kind, extra) in poi:
    if kind == "objective":
        cid, o = extra
        if o["type"] == "interact":
            asset = objective_prop(o["label"], o.get("crime", ""))
            if asset == "prop_shipping_container":
                place(asset, x, z + 3.2, 0, force=True)
            elif asset in ("prop_laptop_hack", "prop_cash_register"):
                # laptop/register sit on a crate counter next to the point
                place("prop_crate_wood", x + 1.3, z, 0, force=True)
                place(asset, x + 1.3, z, 0, y=1.2, force=True)
            else:
                place(asset, x + 1.1, z, 1, force=True)

# ------------------------------------------------------------------ cover around fights
COVER = {
    "old_mill": ["prop_concrete_barrier", "prop_pallet_stack", "prop_cable_spool", "prop_barrel_oil", "prop_dumpster", "prop_barrel_explosive", "prop_sandbag_wall"],
    "harbor_front": ["prop_crate_wood", "prop_crate_stack", "prop_pallet_stack", "prop_barrel_oil", "prop_barrel_explosive", "prop_dock_bollard", "prop_gas_cylinder_cage"],
    "glass_heights": ["prop_concrete_barrier", "prop_newspaper_box", "prop_trash_bin", "prop_bus_shelter", "prop_road_barricade", "prop_vending_machine", "prop_bench"],
}


def district_of(x, z):
    for d, r in DISTRICTS.items():
        if r[0] <= x <= r[2] and r[1] <= z <= r[3]:
            return d
    return None


for (x, z, clr, kind, extra) in poi:
    d = district_of(x, z) or "old_mill"
    n = {"turf": 9, "objective": 5, "supply": 6, "safehouse": 3}[kind]
    for i in range(n):
        for _ in range(8):
            ang = rng.random() * math.tau
            dist = rng.uniform(4.5, max(clr + 4, 10))
            asset = rng.choice(COVER[d])
            if place(asset, x + math.cos(ang) * dist, z + math.sin(ang) * dist, rng.randrange(4), margin=1.2):
                break
    if kind == "turf":
        # a sandbag ring marks a turf zone
        for k in range(6):
            ang = k * math.tau / 6 + 0.3
            place("prop_sandbag_wall", x + math.cos(ang) * (extra["radius"] - 2), z + math.sin(ang) * (extra["radius"] - 2),
                  0 if abs(math.sin(ang)) > 0.7 else 1, margin=0.2)

# ------------------------------------------------------------------ buildings
BUILDINGS = {
    "old_mill": [("bldg_warehouse", 5), ("bldg_parking_garage", 2), ("bldg_corner_shop", 1)],
    "harbor_front": [("bldg_warehouse", 5), ("bldg_corner_shop", 2)],
    "glass_heights": [("bldg_apartment_block", 6), ("bldg_corner_shop", 2), ("bldg_parking_garage", 1)],
}


def pick(table):
    total = sum(w for _, w in table)
    r = rng.uniform(0, total)
    for a, w in table:
        r -= w
        if r <= 0:
            return a
    return table[-1][0]


for d, rect in DISTRICTS.items():
    step = 36 if d != "glass_heights" else 28
    z = rect[1] + 10
    while z < rect[3] - 6:
        x = rect[0] + 10
        while x < rect[2] - 6:
            asset = pick(BUILDINGS[d])
            rot = rng.randrange(4)
            jx, jz = rng.uniform(-3, 3), rng.uniform(-3, 3)
            r = footprint(asset, x + jx, z + jz, rot)
            if not road_hit(r) and not near_poi(r, 2) and r[1] > WATER_Z + 15:
                place(asset, x + jx, z + jz, rot, margin=2.5)
            x += step
        z += step

# harbour yard: container rows (stacked 2 high) between the piers
for row in range(4):
    for col in range(6):
        x, z = 140 + col * 8.5, -410 + row * 9
        r = footprint("prop_shipping_container", x, z, 0)
        if road_hit(r) or near_poi(r, 1):
            continue
        if place("prop_shipping_container", x, z, 0, margin=0.6) and rng.random() < 0.5:
            placements.append({"asset": "prop_shipping_container", "pos": [x, 2.59, z], "rot": 0})

# quay edge
for x in range(110, 430, 14):
    place("prop_dock_bollard", x, WATER_Z + 3, 0, force=True)
    if x % 42 == 0:
        place("prop_life_ring_stand", x + 5, WATER_Z + 4, 0, force=True)

# ------------------------------------------------------------------ street dressing
for rd in roads:
    (x0, z0), (x1, z1) = rd["from"], rd["to"]
    length = math.hypot(x1 - x0, z1 - z0)
    ux, uz = (x1 - x0) / length, (z1 - z0) / length
    nx, nz = -uz, ux
    off = rd["width"] / 2 + 1.2
    rot = 1 if abs(ux) > abs(uz) else 0
    s = 15.0
    while s < length - 10:
        px, pz = x0 + ux * s, z0 + uz * s
        side = 1 if int(s / 30) % 2 == 0 else -1
        place("prop_street_lamp", px + nx * off * side, pz + nz * off * side, rot, force=True)
        if rng.random() < 0.18:
            extra = rng.choice(["prop_fire_hydrant", "prop_trash_bin", "prop_bench", "prop_newspaper_box", "prop_parking_meter"])
            place(extra, px + nx * off * -side + ux * 4, pz + nz * off * -side + uz * 4, rot, margin=0.5)
        s += 30.0

# district labels for the HUD
areas = [{"id": d, "district": d, "rect": r} for d, r in DISTRICTS.items()]
spawn = next(s for s in load("world/safehouses.json")["safehouses"] if s["price"] == 0)["position"]

mapdata = {
    "_generated": "tools/map/build_map.py — do not edit by hand",
    "bounds": BOUNDS,
    "spawn": spawn,
    "roads": roads,
    "areas": areas,
    "placements": placements,
}
with open(os.path.join(DATA, "world", "map.json"), "w", encoding="utf-8") as f:
    json.dump(mapdata, f, indent=1)

# ------------------------------------------------------------------ encounters
FACTION_BY_DISTRICT = {
    "old_mill": [("saltline_brawler", 2), ("saltline_gunman", 2)],
    "harbor_front": [("saltline_brawler", 1), ("saltline_gunman", 2)],
    "glass_heights": [("velvet_gunman", 3)],
}
enc = []
for zone in load("activities/turf.json")["zones"]:
    x, _, z = zone["at"]
    enc.append({"id": f"enc_{zone['id']}", "position": [x, 0, z], "radius": zone["radius"] - 4,
                "leashRadius": 45, "respawnSeconds": 240,
                "members": [{"archetype": a, "count": n} for a, n in FACTION_BY_DISTRICT[zone["district"]]]})
for path in sorted(glob.glob(os.path.join(DATA, "activities", "contracts", "hf_*.json"))):
    with open(path, encoding="utf-8") as f:
        c = json.load(f)
    x, _, z = c["objectives"][1]["at"]
    enc.append({"id": f"enc_{c['id']}", "position": [x, 0, z], "radius": 7, "leashRadius": 50, "respawnSeconds": 300,
                "members": [{"archetype": "ironclad_enforcer", "count": 3}, {"archetype": "velvet_gunman" if c["district"] == "glass_heights" else "saltline_gunman", "count": 1}]})
with open(os.path.join(DATA, "ai", "encounters.json"), "w", encoding="utf-8") as f:
    json.dump({"_generated": "tools/map/build_map.py", "encounters": enc}, f, indent=1)

print(f"map: {len(placements)} placements, {len(roads)} roads; encounters: {len(enc)}")
