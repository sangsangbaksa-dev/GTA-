"""Downtown Calloway residents and factions, sharing Harbor's existing character skeleton."""

import math

import bpy
from mathutils import Matrix, Vector

import human as H
from characters import G, arm_info, feet, hair, hand_region, is_hand, legs_band, rigid, skin, torso_band
from human import mat


def head_point(ctx, coords):
    return ctx.J["head"] + (Vector(coords) - Vector((0, 0, 1.585))) * ctx.p["head"]


def shell_region(ctx, name, material, keep, offset, **kwargs):
    obj = H.garment(ctx.body, name, material, keep, offset=offset, **kwargs)
    ctx.add(obj)
    return obj


def surface(ctx, x, z, clearance, back=False):
    """Find the underlying body surface to fit seams, plates, and accessories."""
    direction = -1 if back else 1
    origin = Vector((x, -0.52 * direction, z))
    found, point, _, _ = ctx.body.ray_cast(origin, Vector((0, direction, 0)))
    if not found:
        raise ValueError(f"Body surface missed at ({x:.3f}, {z:.3f})")
    point.y -= direction * clearance
    return point


def applique(ctx, name, outline, clearance, material, bone="chest", back=False):
    mesh = bpy.data.meshes.new(name)
    points = [surface(ctx, x, z, clearance, back=back) for x, z in outline]
    signed_area = sum(x * next_z - next_x * z
                      for (x, z), (next_x, next_z) in zip(outline, outline[1:] + outline[:1]))
    order = tuple(range(len(points)))
    mesh.from_pydata(points, [], [order[::-1] if (signed_area < 0) != back else order])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return rigid(ctx, obj, material, bone)


def strip(ctx, name, from_xz, to_xz, width, clearance, material, bone="chest", back=False):
    a, b = Vector(from_xz), Vector(to_xz)
    delta = (b - a).normalized()
    across = width * 0.5 * Vector((-delta.y, delta.x))
    pairs = [(a.lerp(b, i / 5) + across, a.lerp(b, i / 5) - across) for i in range(6)]
    points = [surface(ctx, x, z, clearance, back=back) for pair in pairs for x, z in pair]
    faces = [(2 * i, 2 * i + 1, 2 * i + 3, 2 * i + 2) for i in range(5)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(points, [], [face[::-1] for face in faces] if back else faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return rigid(ctx, obj, material, bone)


def rounded(ctx, point, size, material, bone, width=0.007, rotation=None, name="hardware"):
    obj = H.box(point, size, rot=rotation, name=name)
    bevel = obj.modifiers.new("bevel", "BEVEL")
    bevel.width = width
    bevel.segments = 2
    H.apply_mod(obj, bevel)
    for polygon in obj.data.polygons:
        polygon.use_smooth = polygon.loop_total == 4
    return rigid(ctx, obj, material, bone)


def arm_ring(ctx, side, fraction, radius, width, material):
    elbow, wrist = ctx.J[f"elbow.{side}"], ctx.J[f"wrist.{side}"]
    direction = (wrist - elbow).normalized()
    center = elbow.lerp(wrist, fraction)
    rigid(ctx, H.torus(center, radius, width, rot=direction.to_track_quat("Z", "Y").to_matrix()),
          material, f"forearm.{side}")


def footwear(ctx, uppers, rubber, detail, cut=0.19):
    G(ctx, "boots", uppers, feet(cut), offset=0.014, thick=0.006, smooth=2)
    for side in ("L", "R"):
        x = ctx.J[f"ankle.{side}"].x
        rounded(ctx, (x, -0.050, 0.008), (0.102, 0.238, 0.023), rubber,
                f"foot.{side}", width=0.008, name="tread")
        rigid(ctx, H.ellipsoid((x, -0.129, 0.046), (0.045, 0.051, 0.028), seg=16),
              uppers, f"toe.{side}")
        for i in range(3):
            rounded(ctx, (x, -0.085 + i * 0.020, 0.085 + i * 0.011), (0.040, 0.006, 0.004),
                    detail, f"foot.{side}", width=0.002, name="laces")


def pants_hidden(ctx, lo=0.22, hi=0.92):
    return lambda co: lo < co.z < hi and arm_info(co, ctx.J) is None and not is_hand(co, ctx.J)


def covered(ctx, bottom=1.035):
    inside_pants = pants_hidden(ctx)
    return lambda co: inside_pants(co) or (
        bottom < co.z < 1.40 and abs(co.x) < 0.11 and arm_info(co, ctx.J) is None)


def make_person(name, params, dress):
    rig, mesh = H.make_character(name, dict(params, portrait_eye_socket=True), dress)
    # Decimation can retain degenerate garment or facial faces after the mesh join.
    mesh.data.validate(clean_customdata=False)
    mesh.data.update()
    return rig, mesh


def portrait(ctx, iris, eyebrows, brow_weight, lips_rgb):
    hs = ctx.p["head"]
    sclera = mat("portrait_sclera", (0.68, 0.67, 0.59), rough=0.32)
    iris_mat = mat(f"portrait_iris_{iris}", iris, rough=0.28)
    pupil = mat("portrait_pupil", (0.016, 0.012, 0.010), rough=0.25)
    for side in (-1, 1):
        x = side * 0.033
        # Thin ellipses sit inside the carved sockets, with the iris and pupil following the cornea.
        for label, depth, width, thickness, height, material in (
            ("sclera", -0.0873, 0.0134, 0.0061, 0.0073, sclera),
            ("iris", -0.0927, 0.0048, 0.0013, 0.0048, iris_mat),
            ("pupil", -0.0939, 0.00225, 0.00055, 0.00225, pupil),
        ):
            obj = H.ellipsoid(head_point(ctx, (x, depth, 1.692)),
                              (width * hs, thickness * hs, height * hs), seg=16, name=label)
            rigid(ctx, obj, material, "head")
    H.brows(ctx, eyebrows, thick=brow_weight)
    lips = mat(f"lip_{lips_rgb}", lips_rgb, rough=0.64)
    points = [head_point(ctx, v) for v in ((-0.016, -0.096, 1.627),
              (0, -0.105, 1.625), (0.016, -0.096, 1.627))]
    for a, b in zip(points, points[1:]):
        rigid(ctx, H.capsule(a, b, 0.0014, 0.0015, seg=8, name="lip_line"), lips, "head")


def cuffed_cap(ctx, crown, visor, trim):
    hp = lambda v: head_point(ctx, v)
    hs = ctx.p["head"]
    rigid(ctx, H.ellipsoid(hp((0, 0.008, 1.771)), (0.098 * hs, 0.110 * hs, 0.064 * hs), seg=32), crown, "head")
    rigid(ctx, H.torus(hp((0, 0.005, 1.744)), 0.093 * hs, 0.008 * hs, scale=(1, 1.13, 1)), trim, "head")
    rigid(ctx, H.ellipsoid(hp((0, -0.078, 1.736)), (0.093 * hs, 0.068 * hs, 0.010 * hs),
                          rot=Matrix.Rotation(math.radians(8), 3, "X"), seg=28), visor, "head")


def rook():
    """Lean courier in a sea-green waxed jacket, sand neck wrap and body sling."""
    def dress(ctx):
        jacket = mat("rook_waxed_seagreen", (0.035, 0.235, 0.224), rough=0.54)
        leather = mat("rook_dyed_coal", (0.032, 0.039, 0.042), rough=0.62)
        canvas = mat("rook_desert_canvas", (0.37, 0.271, 0.15), rough=0.90)
        sand = mat("rook_sand_wrap", (0.57, 0.397, 0.22), rough=0.86)
        trousers = mat("rook_slate_dungarees", (0.096, 0.116, 0.125), rough=0.84)
        stitch = mat("rook_saffron_stitch", (0.69, 0.376, 0.11), rough=0.58)
        hairm = mat("rook_charred_hair", (0.023, 0.017, 0.016), rough=0.74)
        portrait(ctx, (0.16, 0.235, 0.185), hairm, 0.90, (0.26, 0.11, 0.082))
        hair(ctx, "slick", hairm, offset=0.011, noise=0.004)
        G(ctx, "liner", leather, torso_band(0.99, 1.51, sleeve=0.93), offset=0.007, thick=0.003)
        G(ctx, "wax_jacket", jacket, torso_band(0.93, 1.47, sleeve=0.97), offset=0.022, thick=0.008,
          smooth=3, noise_amp=0.006, noise_scale=16)
        G(ctx, "field_trousers", trousers, legs_band(0.16, 1.02), offset=0.017, thick=0.004, smooth=3)
        G(ctx, "riding_gloves", leather, hand_region(fingers=False), offset=0.004, thick=0.002)
        footwear(ctx, leather, trousers, stitch)
        for side in ("L", "R"):
            arm_ring(ctx, side, 0.83, 0.036, 0.007, leather)
            knee = ctx.J[f"knee.{side}"]
            rigid(ctx, H.ellipsoid(knee + Vector((0, -0.043, 0)), (0.056, 0.011, 0.073), seg=16),
                  leather, f"shin.{side}")
        for z, radius in ((1.459, 0.068), (1.482, 0.062), (1.500, 0.057)):
            rigid(ctx, H.torus((0, 0.009, z), radius, 0.016, scale=(1.12, 1.05, 1.0),
                               rot=Matrix.Rotation(math.radians(-12), 3, "X")), sand, "chest")
        strip(ctx, "center_zip", (0.006, 1.12), (0.006, 1.43), 0.012, 0.031, leather)
        strip(ctx, "sling_strap", (-0.102, 1.425), (0.102, 1.135), 0.039, 0.034, canvas)
        strip(ctx, "rear_strap", (-0.108, 1.426), (0.108, 1.152), 0.039, 0.035, canvas, back=True)
        strip(ctx, "wax_pocket_left", (-0.121, 1.21), (-0.073, 1.28), 0.013, 0.036, leather)
        strip(ctx, "wax_pocket_right", (0.074, 1.28), (0.121, 1.21), 0.013, 0.041, leather)
        rounded(ctx, surface(ctx, -0.053, 1.356, 0.047), (0.043, 0.012, 0.031), stitch, "chest", name="sling_buckle")
        bag = surface(ctx, 0.067, 1.155, 0.074, back=True)
        rounded(ctx, bag, (0.22, 0.13, 0.205), canvas, "spine", width=0.031, name="courier_pouch")
        rounded(ctx, bag + Vector((0, 0.063, 0.017)), (0.177, 0.014, 0.024), sand, "spine", name="pouch_band")
        return covered(ctx)

    return make_person("chr_harbor_rook", dict(skin_mat=skin("tan"), height=1.82, bulk=0.97,
        shoulder=1.04, muscle=1.06, face_width=0.98, jaw_width=0.94, chin_width=0.95,
        nose_depth=1.03, nose_width=0.91), dress)


def officer():
    """Harbor service officer in blue work shirt, navy vest and a visored patrol cap."""
    def dress(ctx):
        navy = mat("port_midnight_navy", (0.033, 0.064, 0.117), rough=0.71)
        blue = mat("port_coastal_blue", (0.098, 0.235, 0.38), rough=0.80)
        indigo = mat("port_blue_black", (0.024, 0.04, 0.068), rough=0.61)
        slate = mat("port_vest_slate", (0.068, 0.115, 0.17), rough=0.77)
        white = mat("port_ice_piping", (0.53, 0.69, 0.73), rough=0.48)
        brass = mat("port_badge_brass", (0.66, 0.48, 0.19), metal=0.63, rough=0.37)
        hairm = mat("port_russet_hair", (0.13, 0.061, 0.03), rough=0.75)
        portrait(ctx, (0.10, 0.24, 0.32), hairm, 0.74, (0.36, 0.155, 0.122))
        hair(ctx, "short", hairm, offset=0.007, noise=0.003)
        cuffed_cap(ctx, navy, indigo, slate)
        rigid(ctx, H.ellipsoid(head_point(ctx, (0, -0.103, 1.774)), (0.019, 0.007, 0.014), seg=16), brass, "head")
        G(ctx, "service_shirt", blue, torso_band(0.96, 1.507, sleeve=0.82), offset=0.013,
          thick=0.004, smooth=3, noise_amp=0.003, noise_scale=25)
        G(ctx, "utility_vest", slate, torso_band(1.028, 1.454, sleeve=None), offset=0.025, thick=0.006, smooth=4)
        G(ctx, "uniform_trousers", navy, legs_band(0.16, 1.05), offset=0.017, thick=0.006, smooth=3)
        footwear(ctx, indigo, navy, slate, cut=0.18)
        strip(ctx, "service_zip", (0, 1.08), (0, 1.42), 0.011, 0.035, indigo)
        strip(ctx, "service_chest", (-0.102, 1.24), (0.102, 1.24), 0.028, 0.037, navy)
        strip(ctx, "watch_panel", (0.055, 1.33), (0.111, 1.33), 0.016, 0.038, white)
        applique(ctx, "breakwater_badge", [(-0.093, 1.382), (-0.065, 1.389), (-0.039, 1.378),
                 (-0.046, 1.347), (-0.064, 1.330), (-0.089, 1.345)], 0.039, brass)
        strip(ctx, "patrol_back",
              (-0.11, 1.325), (0.11, 1.325), 0.031, 0.038, white, back=True)
        rounded(ctx, surface(ctx, -0.108, 1.16, 0.063), (0.070, 0.059, 0.112), indigo, "spine", name="radio")
        p = surface(ctx, -0.108, 1.21, 0.069)
        rigid(ctx, H.capsule(p, p + Vector((0, 0, 0.122)), 0.004, 0.0025, seg=8, name="radio_aerial"), indigo, "spine")
        rigid(ctx, H.torus((0, 0.002, 1.008), 0.150, 0.017, scale=(1, 0.75, 0.86)), indigo, "hips")
        for side, sign in (("L", 1), ("R", -1)):
            shoulder = ctx.J[f"shoulder.{side}"]
            rotated = Matrix.Rotation(sign * math.radians(32), 3, "Y")
            rounded(ctx, shoulder + Vector((0, 0.004, 0.040)), (0.078, 0.041, 0.013), navy,
                    f"upper_arm.{side}", rotation=rotated, name="shoulder_ranks")
            rounded(ctx, shoulder + Vector((sign * 0.009, -0.001, 0.060)), (0.038, 0.028, 0.009), white,
                    f"upper_arm.{side}", rotation=rotated, name="rank_piping")
            rounded(ctx, (sign * 0.135, 0.027, 0.989), (0.067, 0.095, 0.107), indigo,
                    "hips", name="duty_pouch")
        return covered(ctx)

    return make_person("chr_harbor_officer", dict(skin_mat=skin("light"), height=1.79,
        bulk=0.91, shoulder=0.92, hip=1.04, muscle=0.88, fem=0.66, head=1.02,
        face_width=0.95, jaw_width=0.89, chin_width=0.90, nose_bridge=0.88, nose_width=0.92), dress)


def rival_crew():
    """Saltline lookout in a cranberry flotation shell, gray hoodie and road boots."""
    def dress(ctx):
        red = mat("saltline_oxblood_canvas", (0.335, 0.052, 0.063), rough=0.81)
        cream = mat("saltline_chalk", (0.59, 0.51, 0.37), rough=0.88)
        gray = mat("saltline_granite_knit", (0.13, 0.145, 0.15), rough=0.95)
        jeans = mat("saltline_washed_indigo", (0.12, 0.16, 0.23), rough=0.88)
        charcoal = mat("saltline_tar_rubber", (0.04, 0.035, 0.036), rough=0.85)
        fade = mat("saltline_cocoa_fade", (0.035, 0.025, 0.022), rough=0.85)
        nickel = mat("saltline_nickel", (0.50, 0.53, 0.52), metal=0.8, rough=0.45)
        portrait(ctx, (0.16, 0.084, 0.045), fade, 1.24, (0.155, 0.058, 0.047))
        hair(ctx, "short", fade, offset=0.005, noise=0.004)
        beard = H.beard_region(ctx)
        shell_region(ctx, "trimmed_beard", fade, lambda co: beard(co) and H.head_local(ctx, co).z < 1.615,
                     0.004, thick=0.002, smooth=1, noise_amp=0.004, noise_scale=90)
        rigid(ctx, H.torus(head_point(ctx, (-0.086, 0.012, 1.661)), 0.008, 0.002,
                           rot=Matrix.Rotation(math.pi / 2, 3, "Y")), nickel, "head")
        G(ctx, "knit_pullover", gray, torso_band(0.94, 1.49, sleeve=0.73), offset=0.022,
          thick=0.006, smooth=4, noise_amp=0.007, noise_scale=20)
        G(ctx, "saltline_float_vest", red, torso_band(1.045, 1.465, sleeve=None), offset=0.048,
          thick=0.012, smooth=5, noise_amp=0.005, noise_scale=22)
        G(ctx, "washed_denim", jeans, legs_band(0.15, 1.05), offset=0.021, thick=0.005, smooth=3)
        footwear(ctx, charcoal, charcoal, cream)
        for side in ("L", "R"):
            arm_ring(ctx, side, 0.41, 0.053, 0.010, gray)
        rigid(ctx, H.torus((0, 0.044, 1.476), 0.078, 0.027, scale=(1.06, 1.26, 1),
                           rot=Matrix.Rotation(math.radians(-22), 3, "X")), gray, "chest")
        strip(ctx, "float_main_zip", (0, 1.11), (0, 1.43), 0.014, 0.066, charcoal)
        for z in (1.17, 1.215, 1.26):
            strip(ctx, "welded_rib", (-0.13, z), (0.13, z), 0.009, 0.064, charcoal)
        for side in (-1, 1):
            strip(ctx, "saltline_sash", (side * 0.114, 1.414), (side * 0.094, 1.327),
                  0.038, 0.063, cream)
        for z in (1.345, 1.376):
            strip(ctx, "saltline_breakwater", (-0.126, z), (0.126, z), 0.019, 0.064, cream, back=True)
        return covered(ctx)

    return make_person("chr_harbor_crew", dict(skin_mat=skin("brown"), height=1.92,
        bulk=1.18, shoulder=1.09, muscle=1.19, belly=0.38, jaw_width=1.16,
        chin_width=1.16, face_width=1.07, nose_bridge=1.12, nose_width=1.20, nose_depth=0.98), dress)


def dockhand():
    """Dock electrician in olive bibs, a signal-orange vest, and a white work helmet."""
    def dress(ctx):
        coveralls = mat("dockhand_olive_canvas", (0.164, 0.185, 0.108), rough=0.91)
        tees = mat("dockhand_clay_knit", (0.29, 0.133, 0.077), rough=0.91)
        orange = mat("dockhand_hivis_vermilion", (0.78, 0.199, 0.026), rough=0.63)
        reflex = mat("dockhand_reflex_ivory", (0.76, 0.78, 0.60), rough=0.60)
        ivory = mat("dockhand_helmet_ivory", (0.77, 0.75, 0.62), rough=0.4)
        black = mat("dockhand_graphite_web", (0.037, 0.039, 0.031), rough=0.82)
        tint = mat("dockhand_deep_hair", (0.026, 0.016, 0.016), rough=0.88)
        brown = mat("dockhand_scuffed_boot", (0.119, 0.066, 0.038), rough=0.65)
        portrait(ctx, (0.195, 0.128, 0.048), tint, 0.78, (0.134, 0.065, 0.057))
        hair(ctx, "long", tint, offset=0.010, noise=0.008, scale=96.0)
        for sx in (-1, 1):
            a = head_point(ctx, (sx * 0.025, 0.083, 1.710))
            b = head_point(ctx, (sx * 0.037, 0.105, 1.604))
            c = head_point(ctx, (sx * 0.066, 0.125, 1.543))
            rigid(ctx, H.capsule(a, b, 0.024, 0.021, seg=14, name="gathered_braids"), tint, "head")
            rigid(ctx, H.capsule(b, c, 0.022, 0.011, seg=12, name="braid_tail"), tint, "neck")
        cuffed_cap(ctx, ivory, ivory, black)
        rigid(ctx, H.ellipsoid(head_point(ctx, (0, 0.003, 1.811)), (0.018, 0.10, 0.033), seg=22), ivory, "head")
        lamp = head_point(ctx, (0, -0.11, 1.769))
        rounded(ctx, lamp, (0.050, 0.028, 0.035), black, "head", width=0.007, name="helmet_lamp")
        rounded(ctx, lamp + Vector((0, -0.015, 0)), (0.034, 0.006, 0.020), reflex, "head", width=0.003)
        G(ctx, "rolled_undershirt", tees, torso_band(0.94, 1.491, sleeve=0.49), offset=0.014,
          thick=0.004, smooth=2)
        G(ctx, "dock_bib_trousers", coveralls, legs_band(0.17, 1.06), offset=0.023,
          thick=0.006, smooth=3, noise_amp=0.006, noise_scale=20)
        G(ctx, "inspection_vest", orange, torso_band(1.005, 1.462, sleeve=None), offset=0.032,
          thick=0.006, smooth=5, noise_amp=0.004, noise_scale=21)
        G(ctx, "work_gloves", black, hand_region(fingers=False), offset=0.004, thick=0.002)
        footwear(ctx, brown, black, reflex, cut=0.20)
        for side in (-1, 1):
            strip(ctx, "front_reflector", (side * 0.103, 1.411), (side * 0.087, 1.19), 0.029, 0.050, reflex)
            strip(ctx, "rear_reflector", (side * 0.103, 1.411), (side * 0.104, 1.185), 0.029, 0.053, reflex,
                  back=True)
            rounded(ctx, (side * 0.131, 0.021, 0.969), (0.077, 0.106, 0.116), brown,
                    "hips", width=0.015, name="tool_wallet")
        strip(ctx, "front_reflector_waist", (-0.118, 1.16), (0.118, 1.16), 0.027, 0.049, reflex)
        strip(ctx, "rear_reflector_waist", (-0.118, 1.15), (0.118, 1.15), 0.027, 0.049, reflex, back=True)
        strip(ctx, "dark_zip", (0, 1.04), (0, 1.41), 0.012, 0.051, black)
        badge = surface(ctx, 0.045, 1.29, 0.063)
        rounded(ctx, badge, (0.053, 0.014, 0.077), black, "chest", name="lanyard_card")
        rounded(ctx, badge + Vector((0, -0.009, 0.003)), (0.040, 0.004, 0.056), ivory, "chest", width=0.002)
        return covered(ctx)

    return make_person("chr_harbor_dockhand", dict(skin_mat=skin("dark"), height=1.69,
        bulk=0.95, shoulder=0.90, hip=1.09, muscle=0.84, fem=0.87, head=1.08,
        face_width=1.09, jaw_width=1.04, chin_width=1.08, nose_bridge=1.11, nose_width=1.22,
        nose_depth=0.97), dress)


ROSTER = [
    ("characters/chr_harbor_rook.glb", rook),
    ("characters/chr_harbor_officer.glb", officer),
    ("characters/chr_harbor_crew.glb", rival_crew),
    ("characters/chr_harbor_dockhand.glb", dockhand),
]
