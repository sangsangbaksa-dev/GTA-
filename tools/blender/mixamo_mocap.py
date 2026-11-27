"""Mixamo motion-capture fetcher, Harbor 51-bone retargeter, and FBX/glTF exporter.

Fetches canonical Mixamo Y-Bot skeleton + 30-FPS keyframe streams (`sjson` NDJSON)
from Mixamo's public motion catalog (`X-Api-Key: mixamo2`) and retargets full-body +
30-finger motion onto Harbor's 51-bone humanoid rig (`<name>_rig`).

Note on headless custom-mesh upload:
  Mixamo's custom character upload (`POST /api/v1/characters`) and binary FBX export
  (`POST /api/v1/animations/export`) require interactive Adobe IMS OAuth login and
  return HTTP 401 (`401013: Oauth token is not valid`) in headless environments.
  `export_mixamo_character_files()` still writes Mixamo-ready T-pose `.fbx` and `.obj`
  assets for interactive upload, while `attach_mixamo_clips()` streams and retargets
  genuine Mixamo mocap onto Harbor's 51-bone protagonist rig headlessly.
"""
import json
import math
import os
import urllib.request

import bpy
from mathutils import Matrix, Quaternion, Vector

YBOT_CHARACTER_ID = "4f5d21e1-4ccc-41f1-b35b-fb2547bd8493"

MIXAMO_CLIPS = [
    {
        "name": "Idle",
        "slug": "mixamo_idle",
        "source_title": "Male Fitness Idle With Breathing",
        "product_id": "c9cada0b-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 120140901,
        "params": "0",
        "inplace": False,
        "mode": "stationary",
    },
    {
        "name": "Walk",
        "slug": "mixamo_walk",
        "source_title": "Male Standard Walk",
        "product_id": "c9c634c5-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 101410901,
        "params": "0,0,0",
        "inplace": True,
        "mode": "locomotion",
    },
    {
        "name": "Jog",
        "slug": "mixamo_jog",
        "source_title": "Jogging",
        "product_id": "c9c8707d-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 116500901,
        "params": "0",
        "inplace": True,
        "mode": "locomotion",
    },
    {
        "name": "Run",
        "slug": "mixamo_run",
        "source_title": "Standard Running",
        "product_id": "c9ccc5dd-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 123560901,
        "params": "0",
        "inplace": True,
        "mode": "locomotion",
    },
    {
        "name": "TurnLeft",
        "slug": "mixamo_turn_left",
        "source_title": "Standing Left Turn",
        "product_id": "c9c9794e-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 117800905,
        "params": "0",
        "inplace": False,
        "mode": "turn",
    },
    {
        "name": "TurnRight",
        "slug": "mixamo_turn_right",
        "source_title": "Standing Right Turn",
        "product_id": "c9c97a12-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 117800906,
        "params": "0",
        "inplace": False,
        "mode": "turn",
    },
    {
        "name": "Dance",
        "slug": "mixamo_dance",
        "source_title": "Brooklyn Uprock",
        "product_id": "c9cc1b0f-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 121720901,
        "params": "0",
        "inplace": False,
        "mode": "dance",
    },
    {
        "name": "CrouchIdle",
        "slug": "mixamo_crouch_idle",
        "source_title": "Low Crouching Idle",
        "product_id": "c9cc08cf-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 121520902,
        "params": "0",
        "inplace": False,
        "mode": "stationary",
    },
    {
        "name": "CrouchWalk",
        "slug": "mixamo_crouch_walk",
        "source_title": "Crouched Walk",
        "product_id": "c9c640fa-b96c-11e4-a802-0aaa78deedf9",
        "model_id": 101610901,
        "params": "0,0,0",
        "inplace": True,
        "mode": "locomotion",
    },
]

# Mixamo (X=+left, Y=+up, Z=+forward) -> Blender (X=+left, Y=-forward, Z=+up)
_C_MIX_TO_BL = Matrix(((1.0, 0.0, 0.0), (0.0, 0.0, -1.0), (0.0, 1.0, 0.0)))
_C_BL_TO_MIX = _C_MIX_TO_BL.transposed()


def _cache_dir():
    base = os.environ.get("MAKEHUMAN_CACHE_DIR", "/tmp/harbor-tools/makehuman")
    path = os.path.join(base, "mixamo_cache")
    os.makedirs(path, exist_ok=True)
    return path


def _mixamo_get_json(path):
    req = urllib.request.Request(
        "https://www.mixamo.com" + path,
        headers={"X-Api-Key": "mixamo2", "Accept": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _mixamo_post_text(path, payload):
    req = urllib.request.Request(
        "https://www.mixamo.com" + path,
        data=json.dumps(payload).encode("utf-8"),
        headers={"X-Api-Key": "mixamo2", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=45) as resp:
        return resp.read().decode("utf-8")


def ensure_mixamo_cache():
    """Fetch and cache canonical Y-Bot skeleton and all 9 Mixamo motion streams."""
    cdir = _cache_dir()
    skel_path = os.path.join(cdir, "ybot_skeleton.json")
    if not os.path.exists(skel_path):
        skel_data = _mixamo_get_json(f"/api/v1/characters/{YBOT_CHARACTER_ID}/assets/rigged/skeleton.json")
        with open(skel_path, "w", encoding="utf-8") as fh:
            json.dump(skel_data, fh)

    for clip in MIXAMO_CLIPS:
        sjson_path = os.path.join(cdir, f"{clip['name']}.sjson")
        if not os.path.exists(sjson_path):
            raw = _mixamo_post_text(
                "/api/v1/animations/stream",
                {
                    "gms_hash": [
                        {
                            "model-id": clip["model_id"],
                            "mirror": False,
                            "trim": [0, 100],
                            "overdrive": 0,
                            "params": clip["params"],
                            "arm-space": 0,
                            "inplace": clip["inplace"],
                        }
                    ],
                    "character_id": YBOT_CHARACTER_ID,
                    "retargeting_payload": "CharacterSpace,motion;TposeAutofix,on;",
                    "target_type": "skin",
                },
            )
            with open(sjson_path, "w", encoding="utf-8") as fh:
                fh.write(raw)
    return cdir


def _build_harbor_bone_map():
    bone_map = {
        "hips": ("mixamorig:Hips", "mixamorig:Hips", "mixamorig:Spine"),
        "spine": ("mixamorig:Spine1", "mixamorig:Spine", "mixamorig:Spine2"),
        "chest": ("mixamorig:Spine2", "mixamorig:Spine2", "mixamorig:Neck"),
        "neck": ("mixamorig:Neck", "mixamorig:Neck", "mixamorig:Head"),
        "head": ("mixamorig:Head", "mixamorig:Head", "mixamorig:HeadTop_End"),
    }
    for side_hb, side_mx in (("L", "Left"), ("R", "Right")):
        bone_map[f"shoulder.{side_hb}"] = (
            f"mixamorig:{side_mx}Shoulder",
            f"mixamorig:{side_mx}Shoulder",
            f"mixamorig:{side_mx}Arm",
        )
        bone_map[f"upper_arm.{side_hb}"] = (
            f"mixamorig:{side_mx}Arm",
            f"mixamorig:{side_mx}Arm",
            f"mixamorig:{side_mx}ForeArm",
        )
        bone_map[f"forearm.{side_hb}"] = (
            f"mixamorig:{side_mx}ForeArm",
            f"mixamorig:{side_mx}ForeArm",
            f"mixamorig:{side_mx}Hand",
        )
        bone_map[f"hand.{side_hb}"] = (
            f"mixamorig:{side_mx}Hand",
            f"mixamorig:{side_mx}Hand",
            f"mixamorig:{side_mx}HandMiddle1",
        )
        bone_map[f"thigh.{side_hb}"] = (
            f"mixamorig:{side_mx}UpLeg",
            f"mixamorig:{side_mx}UpLeg",
            f"mixamorig:{side_mx}Leg",
        )
        bone_map[f"shin.{side_hb}"] = (
            f"mixamorig:{side_mx}Leg",
            f"mixamorig:{side_mx}Leg",
            f"mixamorig:{side_mx}Foot",
        )
        bone_map[f"foot.{side_hb}"] = (
            f"mixamorig:{side_mx}Foot",
            f"mixamorig:{side_mx}Foot",
            f"mixamorig:{side_mx}ToeBase",
        )
        bone_map[f"toe.{side_hb}"] = (
            f"mixamorig:{side_mx}ToeBase",
            f"mixamorig:{side_mx}ToeBase",
            f"mixamorig:{side_mx}Toe_End",
        )
        for fn_hb, fn_mx in (
            ("thumb", "Thumb"),
            ("index", "Index"),
            ("middle", "Middle"),
            ("ring", "Ring"),
            ("pinky", "Pinky"),
        ):
            for idx in (1, 2, 3):
                bone_map[f"{fn_hb}_{idx}.{side_hb}"] = (
                    f"mixamorig:{side_mx}Hand{fn_mx}{idx}",
                    f"mixamorig:{side_mx}Hand{fn_mx}{idx}",
                    f"mixamorig:{side_mx}Hand{fn_mx}{idx + 1}",
                )
    return bone_map


def _walk_mixamo_rest(node, parent_pos, out_pos):
    pos = parent_pos + Vector((node["x"], node["y"], node["z"])) * 0.01
    out_pos[node["name"]] = _C_MIX_TO_BL @ pos
    for child in node.get("children", []):
        _walk_mixamo_rest(child, pos, out_pos)


def _parse_sjson(path):
    with open(path, "r", encoding="utf-8") as fh:
        lines = [json.loads(line) for line in fh.read().splitlines() if line.strip()]
    header = lines[0]
    frames = [line for line in lines[1:] if line.get("type") == "frame"]
    desc = {(fd["node"], fd["ch"]): fd["offset"] for fd in header["frame_descriptor"]}
    tpo = header["clip"]["tposer-orientation"]
    q_tpo = Quaternion((tpo[3], tpo[0], tpo[1], tpo[2]))
    root_name = header["clip"]["motion-root"]
    return header, frames, desc, q_tpo, root_name


def _eval_mixamo_frame(node, parent_rot, fdata, desc, q_tpo, root_name, out_rot, out_pos):
    name = node["name"]
    pos_off = desc.get((name, "pos"), -1)
    rot_off = desc.get((name, "rot"), -1)
    rq3_off = desc.get((name, "rq3"), -1)
    if pos_off >= 0:
        out_pos[name] = _C_MIX_TO_BL @ (
            Vector((fdata[pos_off], fdata[pos_off + 1], fdata[pos_off + 2])) * 0.01
        )
    if rot_off >= 0:
        quat = Quaternion((fdata[rot_off + 3], fdata[rot_off], fdata[rot_off + 1], fdata[rot_off + 2]))
        if name == root_name:
            quat = q_tpo @ quat
    elif rq3_off >= 0:
        rx, ry, rz = fdata[rq3_off], fdata[rq3_off + 1], fdata[rq3_off + 2]
        rw = max(0.0, 1.0 - rx * rx - ry * ry - rz * rz) ** 0.5
        quat = Quaternion((rw, rx, ry, rz))
    else:
        quat = Quaternion((1.0, 0.0, 0.0, 0.0))
    world_rot = parent_rot @ quat.to_matrix()
    out_rot[name] = _C_MIX_TO_BL @ world_rot @ _C_BL_TO_MIX
    for child in node.get("children", []):
        _eval_mixamo_frame(child, world_rot, fdata, desc, q_tpo, root_name, out_rot, out_pos)


def _extract_yaw(mat3):
    fwd = mat3 @ Vector((0.0, -1.0, 0.0))
    return math.atan2(fwd.x, -fwd.y)


def _estimate_sole_min_z(rig, posed_world, hips_world_pos):
    """Fast forward-kinematics estimate of lowest sole height from posed leg chain."""
    bones = rig.data.bones
    sole_clearance = bones["toe.L"].tail_local.z
    bone_pos = {"hips": hips_world_pos}
    for b in bones:
        if b.parent is not None and b.parent.name in bone_pos:
            parent_b = b.parent
            offset_rest = b.head_local - parent_b.head_local
            rest_p = parent_b.matrix_local.to_3x3()
            bone_pos[b.name] = bone_pos[parent_b.name] + posed_world[parent_b.name] @ (rest_p.inverted() @ offset_rest)
    lowest = 999.0
    for side in ("L", "R"):
        for bname in (f"foot.{side}", f"toe.{side}"):
            b = bones[bname]
            head_p = bone_pos[bname]
            tail_p = head_p + posed_world[bname] @ Vector((0.0, b.length, 0.0))
            lowest = min(lowest, head_p.z - sole_clearance, tail_p.z - sole_clearance)
    return lowest


def attach_mixamo_clips(rig):
    """Retarget all 9 Mixamo clips onto Harbor's 51-bone rig and push them onto NLA tracks."""
    cdir = ensure_mixamo_cache()
    with open(os.path.join(cdir, "ybot_skeleton.json"), "r", encoding="utf-8") as fh:
        skel = json.load(fh)

    mix_rest_pos = {}
    _walk_mixamo_rest(skel["joint"], Vector((0.0, 0.0, 0.0)), mix_rest_pos)

    bone_map = _build_harbor_bone_map()
    bones = list(rig.data.bones)
    rest_3x3 = {b.name: b.matrix_local.to_3x3() for b in bones}
    rest_inv = {b.name: rest_3x3[b.name].inverted() for b in bones}
    hb_to_t = {}
    for b in bones:
        _, mh_h, mh_t = bone_map[b.name]
        u_hb = (b.tail_local - b.head_local).normalized()
        u_mx = (mix_rest_pos[mh_t] - mix_rest_pos[mh_h]).normalized()
        hb_to_t[b.name] = u_hb.rotation_difference(u_mx).to_matrix()

    hips_rest_hb = rig.data.bones["hips"].head_local.copy()
    hips_rest_mx = mix_rest_pos["mixamorig:Hips"]
    scale_ratio = hips_rest_hb.z / hips_rest_mx.z

    bpy.context.scene.render.fps = 30
    if rig.animation_data is None:
        rig.animation_data_create()

    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"

    actions = []
    for clip in MIXAMO_CLIPS:
        cname = clip["name"]
        mode = clip["mode"]
        _, frames, desc, q_tpo, root_name = _parse_sjson(os.path.join(cdir, f"{cname}.sjson"))
        n_frames = len(frames)

        raw_eval = []
        for fr in frames:
            out_rot, out_pos = {}, {}
            _eval_mixamo_frame(
                skel["joint"], Matrix.Identity(3), fr["data"], desc, q_tpo, root_name, out_rot, out_pos
            )
            raw_eval.append((out_rot, out_pos))

        yaw0 = _extract_yaw(raw_eval[0][0]["mixamorig:Hips"])
        raw_dhips = []
        frame_un_yaw = []
        for out_rot, out_pos in raw_eval:
            if mode == "turn":
                dyaw = _extract_yaw(out_rot["mixamorig:Hips"]) - yaw0
                un_yaw = Matrix.Rotation(-dyaw, 3, "Z")
            else:
                un_yaw = Matrix.Identity(3)
            frame_un_yaw.append(un_yaw)
            dh = un_yaw @ ((out_pos["mixamorig:Hips"] - hips_rest_mx) * scale_ratio)
            raw_dhips.append(dh)

        if mode in ("stationary", "locomotion"):
            mean_x = sum(v.x for v in raw_dhips) / n_frames
            mean_y = sum(v.y for v in raw_dhips) / n_frames
            for dh in raw_dhips:
                dh.x -= mean_x
                dh.y -= mean_y
        elif mode == "turn":
            for dh in raw_dhips:
                dh.x *= 0.25
                dh.y *= 0.25
        elif mode == "dance":
            for dh in raw_dhips:
                dh.x *= 0.38
                dh.y *= 0.38

        # First pass: solve local bone poses and measure minimum stance sole height.
        frame_bone_quats = []
        frame_hips_world_delta = []
        sole_mins = []
        for idx_f in range(n_frames):
            out_rot, _ = raw_eval[idx_f]
            un_yaw = frame_un_yaw[idx_f]
            dh = raw_dhips[idx_f].copy()
            posed_world = {}
            quats = {}
            for b in bones:
                dw = un_yaw @ out_rot[bone_map[b.name][0]]
                w_target = dw @ hb_to_t[b.name] @ rest_3x3[b.name]
                if b.parent is None:
                    u_b = rest_3x3[b.name]
                else:
                    u_b = posed_world[b.parent.name] @ rest_inv[b.parent.name] @ rest_3x3[b.name]
                quats[b.name] = (u_b.inverted() @ w_target).to_quaternion().normalized()
                posed_world[b.name] = w_target
            sole_z = _estimate_sole_min_z(rig, posed_world, hips_rest_hb + dh)
            sole_mins.append(sole_z)
            frame_bone_quats.append(quats)
            frame_hips_world_delta.append(dh)

        clip_floor_z = min(sole_mins)
        for idx_f in range(n_frames):
            frame_hips_world_delta[idx_f].z -= clip_floor_z

        # Smooth loop closure on TurnLeft / TurnRight after stripping cumulative yaw.
        if mode == "turn" and n_frames > 6:
            blend_len = 5
            q0 = frame_bone_quats[0]
            h0 = frame_hips_world_delta[0]
            for k in range(1, blend_len + 1):
                fi = n_frames - k
                alpha = (blend_len + 1 - k) / float(blend_len + 1)
                frame_hips_world_delta[fi] = frame_hips_world_delta[fi].lerp(h0, alpha)
                for b in bones:
                    qa = frame_bone_quats[fi][b.name]
                    qb = q0[b.name].copy()
                    if qa.dot(qb) < 0.0:
                        qb.negate()
                    frame_bone_quats[fi][b.name] = qa.slerp(qb, alpha).normalized()

        # Enforce quaternion hemisphere continuity across all frames so interpolation never flips.
        prev_q = {}
        for idx_f in range(n_frames):
            quats = frame_bone_quats[idx_f]
            for b in bones:
                q = quats[b.name]
                if b.name in prev_q and prev_q[b.name].dot(q) < 0.0:
                    q.negate()
                prev_q[b.name] = q

        action = bpy.data.actions.new(name=cname)
        action.use_fake_user = True
        hips_inv = rest_inv["hips"]

        # Direct FCurve population (10x faster than keyframe_insert per bone per frame).
        loc_curves = [action.fcurves.new(data_path='pose.bones["hips"].location', index=ax) for ax in range(3)]
        for ax, fc in enumerate(loc_curves):
            fc.keyframe_points.add(n_frames)
            for idx_f in range(n_frames):
                loc_local = hips_inv @ frame_hips_world_delta[idx_f]
                kp = fc.keyframe_points[idx_f]
                kp.co = (idx_f + 1, loc_local[ax])
                kp.interpolation = "LINEAR"
            fc.update()

        for b in bones:
            dp = f'pose.bones["{b.name}"].rotation_quaternion'
            q_curves = [action.fcurves.new(data_path=dp, index=ax) for ax in range(4)]
            for ax, fc in enumerate(q_curves):
                fc.keyframe_points.add(n_frames)
                for idx_f in range(n_frames):
                    q = frame_bone_quats[idx_f][b.name]
                    kp = fc.keyframe_points[idx_f]
                    kp.co = (idx_f + 1, q[ax])
                    kp.interpolation = "LINEAR"
                fc.update()

        track = rig.animation_data.nla_tracks.new()
        track.name = cname
        strip = track.strips.new(cname, 1, action)
        strip.name = cname
        actions.append(action)

    # Restore clean A-pose rest stance on the rig while keeping NLA tracks ready for glTF export.
    rig.animation_data.action = None
    for pb in rig.pose.bones:
        pb.location = (0.0, 0.0, 0.0)
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        pb.scale = (1.0, 1.0, 1.0)
    bpy.context.view_layer.update()
    return actions


def export_handoff_motion_bundle(rig, mesh_obj, out_dir):
    """Export standalone FBX + glTF motion files and Mixamo-ready character mesh for Set handoff."""
    os.makedirs(out_dir, exist_ok=True)

    # 1. Combined 9-clip Harbor 51-bone motion library (armature + NLA tracks, no heavy textures)
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.gltf(
        filepath=os.path.join(out_dir, "harbor_mixamo_motions.glb"),
        use_selection=True,
        export_format="GLB",
        export_yup=True,
        export_animations=True,
        export_animation_mode="NLA_TRACKS",
        export_force_sampling=False,
    )

    # 2. Mixamo-ready character FBX (embedded textures + all 9 clips) and OBJ mesh
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    mesh_obj.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(out_dir, "chr_harbor_clerk_mixamo.fbx"),
        use_selection=True,
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_nla_strips=True,
        bake_anim_use_all_actions=False,
        path_mode="COPY",
        embed_textures=True,
    )
    bpy.ops.wm.obj_export(
        filepath=os.path.join(out_dir, "chr_harbor_clerk.obj"),
        export_selected_objects=True,
        export_materials=True,
    )

    # 3. Individual per-clip FBX and glTF motion files (for Set repo clip-by-clip publishing)
    tracks = list(rig.animation_data.nla_tracks)
    actions_by_name = {tr.name: tr.strips[0].action for tr in tracks if len(tr.strips) > 0}
    saved_tracks = [(tr.name, tr.strips[0].action) for tr in tracks if len(tr.strips) > 0]
    for tr in list(rig.animation_data.nla_tracks):
        rig.animation_data.nla_tracks.remove(tr)

    for clip in MIXAMO_CLIPS:
        cname = clip["name"]
        slug = clip["slug"]
        act = actions_by_name[cname]
        rig.animation_data.action = None
        tr = rig.animation_data.nla_tracks.new()
        tr.name = cname
        st = tr.strips.new(cname, 1, act)
        st.name = cname
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.export_scene.gltf(
            filepath=os.path.join(out_dir, f"{slug}.glb"),
            use_selection=True,
            export_format="GLB",
            export_yup=True,
            export_animations=True,
            export_animation_mode="NLA_TRACKS",
            export_force_sampling=False,
        )
        bpy.ops.export_scene.fbx(
            filepath=os.path.join(out_dir, f"{slug}.fbx"),
            use_selection=True,
            add_leaf_bones=False,
            bake_anim=True,
            bake_anim_use_nla_strips=True,
            bake_anim_use_all_actions=False,
        )
        rig.animation_data.nla_tracks.remove(tr)

    for tname, act in saved_tracks:
        tr = rig.animation_data.nla_tracks.new()
        tr.name = tname
        st = tr.strips.new(tname, 1, act)
        st.name = tname

