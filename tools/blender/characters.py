"""Character roster (original designs). Each builder returns (rig, mesh)."""
import math

from mathutils import Matrix, Vector

import human as H
from human import mat

SKIN = {
    "light": (0.86, 0.66, 0.54),
    "tan": (0.72, 0.50, 0.37),
    "brown": (0.48, 0.31, 0.21),
    "dark": (0.30, 0.19, 0.13),
}


def skin(tone):
    return mat(f"skin_{tone}", SKIN[tone], rough=0.5, sss=0.12)


# ---------------------------------------------------------------- region predicates
# All in reference space (1.80 m, before final scaling).


def arm_info(co, J):
    """(t along shoulder→wrist, distance from arm axis) or None."""
    if abs(co.x) < 0.12:
        return None
    s = "L" if co.x > 0 else "R"
    sh, wr = J[f"shoulder.{s}"], J[f"wrist.{s}"]
    d = wr - sh
    t = (co - sh).dot(d) / d.length_squared
    if t < -0.15:
        return None
    dist = (co - (sh + d * max(0.0, min(1.0, t)))).length
    if dist > 0.085:
        return None
    return t, dist


def is_hand(co, J):
    a = arm_info(co, J)
    if a is None:
        # fingers lie beyond the wrist and may exceed the arm radius
        for s in ("L", "R"):
            w, k = J[f"wrist.{s}"], J[f"knuckle.{s}"]
            if (co - w).dot((k - w).normalized()) > -0.004 and (co - w).length < 0.2:
                return True
        return False
    return a[0] > 1.0 - 0.004 / 0.255


class Region:
    def __init__(self, keep, planes=lambda J: []):
        self.keep, self.planes = keep, planes

    def __call__(self, co, J):
        return self.keep(co, J)


EPS = 1e-4
Z = Vector((0, 0, 1))


def arm_planes(J, t):
    out = []
    for s in ("L", "R"):
        sh, wr = J[f"shoulder.{s}"], J[f"wrist.{s}"]
        out.append((sh + (wr - sh) * t, (wr - sh).normalized()))
    return out


def torso_band(lo, hi, sleeve=None, collar_open=None):
    """Torso between z lo..hi (hi only applies near the neck); arms up to sleeve fraction."""
    def keep(co, J):
        if is_hand(co, J):
            return False
        a = arm_info(co, J)
        if a is not None and a[0] > 0.12:
            return sleeve is not None and a[0] <= sleeve + EPS
        if co.z < lo - EPS:
            return False
        if co.z > hi + EPS and abs(co.x) < 0.11:
            return False
        if collar_open and collar_open(co):
            return False
        return True

    def planes(J):
        pl = [(Vector((0, 0, lo)), Z), (Vector((0, 0, hi)), Z)]
        if sleeve:
            pl += arm_planes(J, sleeve)
        return pl
    return Region(keep, planes)


def legs_band(lo, hi):
    def keep(co, J):
        if is_hand(co, J) or arm_info(co, J) is not None:
            return False
        return lo - EPS <= co.z <= hi + EPS
    return Region(keep, lambda J: [(Vector((0, 0, lo)), Z), (Vector((0, 0, hi)), Z)])


def feet(hi):
    def keep(co, J):
        return co.z <= hi + EPS and abs(co.x) < 0.25
    return Region(keep, lambda J: [(Vector((0, 0, hi)), Z)])


def hand_region(fingers=True):
    def keep(co, J):
        if not is_hand(co, J):
            a = arm_info(co, J)
            return a is not None and a[0] > 0.9 - EPS  # cuff over the wrist
        if fingers:
            return True
        s = "L" if co.x > 0 else "R"
        w, k = J[f"wrist.{s}"], J[f"knuckle.{s}"]
        return (co - w).dot((k - w).normalized()) < 0.105 + EPS

    def planes(J):
        pl = arm_planes(J, 0.9)
        if not fingers:
            for s in ("L", "R"):
                w, k = J[f"wrist.{s}"], J[f"knuckle.{s}"]
                d = (k - w).normalized()
                pl.append((w + d * 0.105, d))
        return pl
    return Region(keep, planes)


def G(ctx, name, material, keep, **kw):
    J = ctx.J
    planes = keep.planes(J) if isinstance(keep, Region) else []
    o = H.garment(ctx.body, name, material, lambda co: keep(co, J), planes=planes, **kw)
    ctx.add(o)
    return o


def hide(*preds):
    def f(co):
        return any(p(co) for p in preds)
    return f


def rigid(ctx, obj, material, bone):
    obj.data.materials.append(material)
    ctx.rigid(obj, bone)
    return obj


def hair(ctx, style, material, offset=0.009, noise=0.006, scale=45.0):
    keep = H.hair_region(ctx, style)
    o = H.garment(ctx.body, f"hair_{style}", material, keep, offset=offset, thick=0.004, smooth=1,
                  planes=H.hair_planes(ctx, style),
                  noise_amp=noise, noise_scale=scale)
    ctx.add(o)
    return o


# ---------------------------------------------------------------- roster


def player_rook():
    """Rook — protagonist. Ex-courier: charcoal bomber, teal scarf, cargo pants, fingerless gloves."""
    jacket = mat("bomber_charcoal", (0.075, 0.08, 0.09), rough=0.45)
    tee = mat("tee_grey", (0.22, 0.23, 0.24), rough=0.9)
    cargo = mat("cargo_khaki", (0.33, 0.29, 0.21), rough=0.85)
    boots = mat("boots_brown", (0.14, 0.09, 0.06), rough=0.45)
    glove = mat("glove_black", (0.03, 0.03, 0.03), rough=0.5)
    teal = mat("scarf_teal", (0.02, 0.38, 0.40), rough=0.95)
    hairm = mat("hair_black_brown", (0.035, 0.025, 0.02), rough=0.7)
    leather = mat("strap_leather", (0.10, 0.06, 0.04), rough=0.5)
    metal = mat("buckle_metal", (0.6, 0.6, 0.62), metal=1.0, rough=0.3)

    def dress(ctx):
        J = ctx.J
        H.eyes(ctx, (0.18, 0.28, 0.22))
        H.brows(ctx, hairm)
        hair(ctx, "short", hairm, offset=0.010, noise=0.008)
        G(ctx, "tee", tee, torso_band(0.98, 1.50, sleeve=0.35), offset=0.004, thick=0.003)
        G(ctx, "jacket", jacket, torso_band(0.97, 1.455, sleeve=0.97), offset=0.017, thick=0.006, smooth=3)
        rigid(ctx, H.box((0, -0.118, 1.2), (0.008, 0.01, 0.42)), mat("zip_metal", (0.5, 0.5, 0.52), metal=1.0, rough=0.35), "chest")
        G(ctx, "cargo", cargo, legs_band(0.15, 1.0), offset=0.009, thick=0.005, smooth=3)
        G(ctx, "boots", boots, feet(0.17), offset=0.015, thick=0.006, smooth=2)
        G(ctx, "gloves", glove, hand_region(fingers=False), offset=0.003, thick=0.002, smooth=1)
        # scarf wrapped around the neck
        rigid(ctx, H.torus((0, 0.01, 1.485), 0.068, 0.022, scale=(1.05, 1.0, 1.0)), teal, "neck")
        rigid(ctx, H.torus((0, 0.005, 1.455), 0.078, 0.02, rot=Matrix.Rotation(math.radians(6), 3, "X")), teal, "chest")
        rigid(ctx, H.ellipsoid((0.03, -0.085, 1.38), (0.03, 0.012, 0.07), rot=Matrix.Rotation(math.radians(-10), 3, "Y")), teal, "chest")
        # ribbed cuffs + waistband
        for s in ("L", "R"):
            w = J[f"wrist.{s}"].lerp(J[f"elbow.{s}"], 0.05)
            d = (J[f"wrist.{s}"] - J[f"elbow.{s}"]).normalized()
            rigid(ctx, H.torus(w, 0.035, 0.009, rot=d.to_track_quat("Z", "Y").to_matrix()), jacket, f"forearm.{s}")
        rigid(ctx, H.torus((0, 0.0, 0.985), 0.15, 0.014, scale=(1.0, 0.72, 1.0)), jacket, "hips")
        # belt + buckle
        rigid(ctx, H.torus((0, 0.006, 1.02), 0.142, 0.012, scale=(1.0, 0.70, 0.7)), leather, "hips")
        rigid(ctx, H.box((0, -0.106, 1.02), (0.04, 0.008, 0.028)), metal, "hips")
        # cargo pockets
        for s, sx in (("L", 1), ("R", -1)):
            c = J[f"hip.{s}"].lerp(J[f"knee.{s}"], 0.5) + Vector((sx * 0.068, -0.005, 0))
            rigid(ctx, H.box(c, (0.03, 0.12, 0.13)), cargo, f"thigh.{s}")
        # right thigh holster + sling strap across chest
        hc = J["hip.R"].lerp(J["knee.R"], 0.3) + Vector((-0.088, 0.0, 0))
        rigid(ctx, H.box(hc, (0.035, 0.07, 0.16), rot=Matrix.Rotation(math.radians(5), 3, "Y")), glove, "thigh.R")
        rigid(ctx, H.torus((0, 0.0, 1.27), 0.2, 0.01, rot=Matrix.Rotation(math.radians(38), 3, "Y"), scale=(1.0, 0.62, 1.0)), leather, "chest")
        rigid(ctx, H.box((-0.09, 0.14, 1.12), (0.18, 0.07, 0.16)), mat("pack_canvas", (0.22, 0.2, 0.15), rough=0.9), "spine")
        torso_hidden = lambda co: 1.03 < co.z < 1.43 and abs(co.x) < 0.14 and arm_info(co, J) is None
        legs_hidden = lambda co: 0.2 < co.z < 0.85 and arm_info(co, J) is None and not is_hand(co, J)
        return hide(torso_hidden, legs_hidden)

    return H.make_character("chr_player_rook", dict(skin_mat=skin("tan"), height=1.82, muscle=1.1), dress)


def enemy_brawler():
    """Saltline Crew brawler — dockside heavy: tank top, beanie, beard, wrapped fists."""
    tank = mat("tank_offwhite", (0.62, 0.6, 0.55), rough=0.95)
    jeans = mat("jeans_indigo", (0.08, 0.11, 0.2), rough=0.85)
    boots = mat("workboot_tan", (0.36, 0.22, 0.1), rough=0.6)
    beanie = mat("beanie_orange", (0.85, 0.32, 0.04), rough=0.95)
    beard = mat("beard_red_brown", (0.16, 0.07, 0.03), rough=0.9)
    wrap = mat("hand_wrap", (0.85, 0.82, 0.74), rough=0.95)
    chain = mat("chain_steel", (0.7, 0.7, 0.72), metal=1.0, rough=0.25)
    tattoo = mat("tattoo_ink", (0.08, 0.12, 0.16), rough=0.6)

    def dress(ctx):
        J = ctx.J
        H.eyes(ctx, (0.12, 0.18, 0.3))
        H.brows(ctx, beard, thick=1.3)
        o = H.garment(ctx.body, "beard", beard, H.beard_region(ctx), offset=0.006, thick=0.003, smooth=1,
                      noise_amp=0.007, noise_scale=90.0)
        ctx.add(o)
        hair(ctx, "beanie", beanie, offset=0.016, noise=0.002)
        rigid(ctx, H.torus(J["head"] + Vector((0, 0.01, 0.128)) * ctx.p["head"], 0.09 * ctx.p["head"], 0.013,
                           scale=(1.0, 1.15, 1.0), rot=Matrix.Rotation(math.radians(-10), 3, "X")), beanie, "head")  # fold
        G(ctx, "tank", tank, torso_band(0.98, 1.47, sleeve=0.0,
                                         collar_open=lambda co: co.z > 1.36 and abs(co.x) > 0.085 and abs(co.x) < 0.15 and abs(co.y) < 0.06),
          offset=0.005, thick=0.003, smooth=2)
        G(ctx, "jeans", jeans, legs_band(0.14, 1.0), offset=0.01, thick=0.005, smooth=3)
        G(ctx, "boots", boots, feet(0.16), offset=0.016, thick=0.006, smooth=2)
        G(ctx, "wraps", wrap, hand_region(fingers=False), offset=0.004, thick=0.002, smooth=1)
        rigid(ctx, H.torus((0, -0.01, 1.47), 0.075, 0.004, rot=Matrix.Rotation(math.radians(-25), 3, "X")), chain, "chest")
        rigid(ctx, H.torus((0, 0.006, 1.02), 0.16, 0.013, scale=(1.0, 0.74, 0.7)), mat("belt_black", (0.03, 0.03, 0.03)), "hips")
        # forearm tattoo bands
        for s in ("L", "R"):
            c = J[f"elbow.{s}"].lerp(J[f"wrist.{s}"], 0.45)
            d = (J[f"wrist.{s}"] - J[f"elbow.{s}"]).normalized()
            rigid(ctx, H.torus(c, 0.042 * ctx.p["bulk"], 0.006, rot=d.to_track_quat("Z", "Y").to_matrix()), tattoo, f"forearm.{s}")
        legs_hidden = lambda co: 0.2 < co.z < 0.85 and arm_info(co, J) is None and not is_hand(co, J)
        torso_hidden = lambda co: 1.03 < co.z < 1.36 and abs(co.x) < 0.12 and arm_info(co, J) is None
        return hide(legs_hidden, torso_hidden)

    return H.make_character("chr_enemy_brawler", dict(skin_mat=skin("brown"), height=1.90, bulk=1.22, muscle=1.35,
                                                      belly=0.7, shoulder=1.12, head=1.02), dress)


def enemy_gunman():
    """Velvet Hand syndicate gunman — slim maroon suit, black shirt, shades, slicked hair."""
    suit = mat("suit_maroon", (0.28, 0.03, 0.06), rough=0.4)
    shirt = mat("shirt_black", (0.02, 0.02, 0.025), rough=0.55)
    shoes = mat("oxford_black", (0.02, 0.02, 0.02), rough=0.15)
    hairm = mat("hair_slick_black", (0.015, 0.015, 0.015), rough=0.25)
    lens = mat("shade_lens", (0.02, 0.02, 0.03), metal=0.6, rough=0.05)
    gold = mat("gold", (0.9, 0.68, 0.25), metal=1.0, rough=0.25)
    glove = mat("glove_leather_black", (0.02, 0.02, 0.02), rough=0.35)

    def dress(ctx):
        J, p = ctx.J, ctx.p
        hp = lambda v: J["head"] + (Vector(v) - Vector((0, 0, 1.585))) * p["head"]
        H.eyes(ctx)
        H.brows(ctx, hairm)
        hair(ctx, "slick", hairm, offset=0.009, noise=0.0015)
        vneck = lambda co: co.y < -0.03 and co.z > 1.24 and abs(co.x) < (co.z - 1.24) * 0.38 + 0.004
        G(ctx, "shirt", shirt, torso_band(0.98, 1.51, sleeve=1.0), offset=0.004, thick=0.003)
        G(ctx, "suit_jacket", suit, torso_band(0.86, 1.49, sleeve=0.95, collar_open=vneck), offset=0.017, thick=0.006, smooth=3)
        G(ctx, "trousers", mat("trouser_charcoal", (0.06, 0.05, 0.06), rough=0.5), legs_band(0.06, 1.0), offset=0.009, thick=0.004, smooth=3)
        G(ctx, "shoes", shoes, feet(0.11), offset=0.01, thick=0.005, smooth=2)
        G(ctx, "gloves", glove, hand_region(fingers=True), offset=0.002, thick=0.0015, smooth=1)
        # lapels
        for sx in (1, -1):
            rigid(ctx, H.box((sx * 0.045, -0.105, 1.36), (0.035, 0.006, 0.16), rot=Matrix.Rotation(sx * math.radians(-18), 3, "Y")), suit, "chest")
        rigid(ctx, H.box((0.075, -0.11, 1.40), (0.012, 0.004, 0.012)), gold, "chest")  # pin
        # sunglasses
        for sx in (1, -1):
            rigid(ctx, H.ellipsoid(hp((sx * 0.034, -0.1035, 1.693)), (0.019, 0.004, 0.013), seg=16), lens, "head")
        rigid(ctx, H.box(hp((0, -0.106, 1.70)), (0.02, 0.003, 0.003)), gold, "head")
        # wrist watch on left
        w = J["wrist.L"].lerp(J["elbow.L"], 0.1)
        d = (J["wrist.L"] - J["elbow.L"]).normalized()
        rigid(ctx, H.torus(w, 0.029, 0.006, rot=d.to_track_quat("Z", "Y").to_matrix()), gold, "forearm.L")
        torso_hidden = lambda co: 1.0 < co.z < 1.24 and abs(co.x) < 0.13 and arm_info(co, J) is None
        legs_hidden = lambda co: 0.12 < co.z < 0.85 and arm_info(co, J) is None and not is_hand(co, J)
        return hide(torso_hidden, legs_hidden)

    return H.make_character("chr_enemy_gunman", dict(skin_mat=skin("light"), height=1.80, bulk=0.95, muscle=0.95), dress)


def enemy_enforcer():
    """Ironclad Security riot enforcer — PMC fatigues, plate carrier, visor helmet, arm shield."""
    olive = mat("fatigue_olive", (0.17, 0.19, 0.13), rough=0.85)
    plate = mat("carrier_black", (0.05, 0.055, 0.05), rough=0.7)
    hard = mat("hardshell_grey", (0.12, 0.13, 0.13), metal=0.2, rough=0.4)
    visor = mat("visor_amber", (0.55, 0.22, 0.04), metal=0.4, rough=0.05, emit=(0.25, 0.08, 0.0))
    boots = mat("tactical_boot", (0.05, 0.05, 0.045), rough=0.5)
    glove = mat("tactical_glove", (0.06, 0.065, 0.06), rough=0.6)
    shield_m = mat("shield_poly", (0.10, 0.12, 0.14), metal=0.35, rough=0.35)
    window = mat("shield_window", (0.35, 0.45, 0.5), rough=0.05, alpha=0.55)
    stripe = mat("hivis_stripe", (0.9, 0.75, 0.1), rough=0.5, emit=(0.3, 0.25, 0.0))

    def dress(ctx):
        J, p = ctx.J, ctx.p
        hp = lambda v: J["head"] + (Vector(v) - Vector((0, 0, 1.585))) * p["head"]
        H.eyes(ctx)
        H.brows(ctx, mat("hair_dark", (0.03, 0.025, 0.02)))
        G(ctx, "fatigues_top", olive, torso_band(0.95, 1.50, sleeve=0.98), offset=0.016, thick=0.004, smooth=3)
        G(ctx, "fatigues_pants", olive, legs_band(0.15, 1.0), offset=0.01, thick=0.004, smooth=3)
        G(ctx, "boots", boots, feet(0.2), offset=0.016, thick=0.006, smooth=2)
        G(ctx, "gloves", glove, hand_region(fingers=True), offset=0.004, thick=0.002, smooth=1)
        G(ctx, "carrier", plate, torso_band(1.06, 1.45, sleeve=None), offset=0.045, thick=0.012, smooth=6)
        hel = H.garment(ctx.body, "helmet", hard, H.hair_region(ctx, "helmet"), offset=0.03, thick=0.008, smooth=4)
        ctx.add(hel)
        rigid(ctx, H.ellipsoid(hp((0, -0.078, 1.703)), (0.086, 0.05, 0.024), seg=32), visor, "head")
        # pouches on carrier
        for i, x in enumerate((-0.09, -0.03, 0.03, 0.09)):
            rigid(ctx, H.box((x, -0.16, 1.16), (0.055, 0.035, 0.08)), plate, "spine")
        rigid(ctx, H.box((0, -0.155, 1.33), (0.16, 0.012, 0.035)), stripe, "chest")
        for s, sx in (("L", 1), ("R", -1)):
            kn = J[f"knee.{s}"]
            rigid(ctx, H.ellipsoid(kn + Vector((0, -0.05, 0.01)), (0.05, 0.025, 0.06)), hard, f"shin.{s}")
            el = J[f"elbow.{s}"]
            rigid(ctx, H.ellipsoid(el + Vector((0, 0.035, 0.0)), (0.04, 0.025, 0.045)), hard, f"forearm.{s}")
            sh = J[f"shoulder.{s}"]
            rigid(ctx, H.ellipsoid(sh + Vector((sx * 0.02, 0, 0.03)), (0.075, 0.075, 0.045),
                                   rot=Matrix.Rotation(sx * math.radians(-30), 3, "Y")), hard, f"upper_arm.{s}")
        # riot shield strapped to left forearm, facing forward
        e, w = J["elbow.L"], J["wrist.L"]
        c = e.lerp(w, 0.5) + Vector((0.0, -0.13, 0.0))
        shield = H.box(c, (0.55, 0.035, 0.9), name="shield")
        bev = shield.modifiers.new("bev", "BEVEL")
        bev.width = 0.03
        bev.segments = 3
        H.apply_mod(shield, bev)
        rigid(ctx, shield, shield_m, "forearm.L")
        rigid(ctx, H.box(c + Vector((0, -0.02, 0.24)), (0.38, 0.01, 0.13)), window, "forearm.L")
        rigid(ctx, H.box(c + Vector((0, -0.02, -0.2)), (0.5, 0.01, 0.05)), stripe, "forearm.L")
        torso_hidden = lambda co: 1.0 < co.z < 1.42 and abs(co.x) < 0.14 and arm_info(co, J) is None
        legs_hidden = lambda co: 0.22 < co.z < 0.85 and arm_info(co, J) is None and not is_hand(co, J)
        head_hidden = lambda co: co.z > J["head"].z + 0.16 * p["head"] and abs(co.x) < 0.07
        return hide(torso_hidden, legs_hidden, head_hidden)

    return H.make_character("chr_enemy_enforcer", dict(skin_mat=skin("dark"), height=1.86, bulk=1.1, muscle=1.15,
                                                       shoulder=1.05), dress)


def npc_civilian():
    """Port Calloway civilian — hoodie, jeans, sneakers, ponytail."""
    hoodie = mat("hoodie_cobalt", (0.10, 0.24, 0.52), rough=0.95)
    jeans = mat("jeans_light", (0.30, 0.40, 0.55), rough=0.85)
    sneak = mat("sneaker_white", (0.88, 0.88, 0.86), rough=0.6)
    sole = mat("sneaker_sole", (0.2, 0.2, 0.2), rough=0.7)
    hairm = mat("hair_auburn", (0.22, 0.07, 0.03), rough=0.6)
    string = mat("drawstring", (0.9, 0.9, 0.9), rough=0.8)

    def dress(ctx):
        J, p = ctx.J, ctx.p
        hp = lambda v: J["head"] + (Vector(v) - Vector((0, 0, 1.585))) * p["head"]
        H.eyes(ctx, (0.22, 0.32, 0.12))
        H.brows(ctx, hairm, thick=0.7)
        hair(ctx, "long", hairm, offset=0.012, noise=0.008, scale=35.0)
        for sx in (1, -1):  # side-swept fringe
            rigid(ctx, H.ellipsoid(hp((sx * 0.032, -0.086, 1.756)), (0.04, 0.014, 0.011), rot=Matrix.Rotation(sx * math.radians(25), 3, "Y")), hairm, "head")
        # ponytail
        pts = [hp((0, 0.095, 1.72)), hp((0, 0.125, 1.67)), hp((0, 0.13, 1.59)), hp((0, 0.115, 1.52))]
        rads = [0.028, 0.03, 0.024, 0.012]
        for i in range(3):
            rigid(ctx, H.capsule(pts[i], pts[i + 1], rads[i], rads[i + 1]), hairm, "head" if i == 0 else "neck")
        rigid(ctx, H.torus(hp((0, 0.1, 1.715)), 0.022, 0.006, rot=Matrix.Rotation(math.radians(70), 3, "X")), sole, "head")
        G(ctx, "hoodie", hoodie, torso_band(0.90, 1.49, sleeve=0.97), offset=0.018, thick=0.006, smooth=4)
        # hood bunched behind the neck + front pocket + drawstrings
        rigid(ctx, H.torus((0, 0.03, 1.47), 0.085, 0.028, scale=(1.0, 1.1, 1.0), rot=Matrix.Rotation(math.radians(-18), 3, "X")), hoodie, "chest")
        rigid(ctx, H.box((0, -0.12, 1.02), (0.2, 0.015, 0.11)), hoodie, "spine")
        for sx in (1, -1):
            rigid(ctx, H.capsule((sx * 0.03, -0.11, 1.44), (sx * 0.035, -0.125, 1.32), 0.0035, 0.0035), string, "chest")
        G(ctx, "jeans", jeans, legs_band(0.09, 1.0), offset=0.008, thick=0.004, smooth=3)
        G(ctx, "sneakers", sneak, feet(0.12), offset=0.014, thick=0.006, smooth=2)
        for s, sx in (("L", 1), ("R", -1)):
            an = J[f"ankle.{s}"]
            rigid(ctx, H.box((an.x, -0.04, 0.008), (0.1, 0.27, 0.018)), sole, f"foot.{s}")
        torso_hidden = lambda co: 1.0 < co.z < 1.42 and abs(co.x) < 0.13 and arm_info(co, J) is None
        legs_hidden = lambda co: 0.15 < co.z < 0.85 and arm_info(co, J) is None and not is_hand(co, J)
        return hide(torso_hidden, legs_hidden)

    return H.make_character("chr_npc_civilian", dict(skin_mat=skin("light"), height=1.68, fem=1.0, shoulder=0.88,
                                                     hip=1.08, bulk=0.95, muscle=0.75, head=1.04), dress)


ROSTER = [
    ("characters/chr_player_rook.glb", player_rook),
    ("characters/chr_enemy_brawler.glb", enemy_brawler),
    ("characters/chr_enemy_gunman.glb", enemy_gunman),
    ("characters/chr_enemy_enforcer.glb", enemy_enforcer),
    ("characters/chr_npc_civilian.glb", npc_civilian),
]
