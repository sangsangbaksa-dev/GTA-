"""Headless MakeHuman (MPFB2) character builder adapted to Harbor's 51-bone rig.

Requires Blender 4.2+ with MPFB2 and CC0 MakeHuman asset packs installed
(run `tools/blender/setup_makehuman.sh` once per machine).

Produces a single joined skinned mesh `<name>` parented to `<name>_rig` with:
  * Harbor's exact 51-bone hierarchy (`hips`, `spine`, `chest`, `neck`, `head`,
    `shoulder/upper_arm/forearm/hand.{L,R}`, `{thumb,index,middle,ring,pinky}_{1..3}.{L,R}`,
    `thigh/shin/foot/toe.{L,R}`), roll = 0, 42-degree A-pose rest stance
  * Feet grounded at Z = 0, facing -Y in Blender (+Z in glTF / Godot)
  * glTF-ready PBR materials with neutral grayscale weave + baseColorFactor on
    garments so fold detail and runtime recoloring both work cleanly
"""
import importlib
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Quaternion, Vector


A_POSE_DEG = 42.0


def _load_mpfb():
    if "bl_ext.user_default.mpfb" not in bpy.context.preferences.addons:
        try:
            bpy.ops.preferences.addon_enable(module="bl_ext.user_default.mpfb")
        except Exception as exc:
            raise RuntimeError(
                "MPFB2 Blender extension not found. Run `tools/blender/setup_makehuman.sh` first."
            ) from exc
    mpfb_mod = importlib.import_module("bl_ext.user_default.mpfb")
    services = mpfb_mod.MPFB_CONTEXTUAL_INFORMATION["SERVICES"]
    services["AssetService"].update_all_asset_lists()
    return services


def _remove_transparent_islands(obj, alpha_cutoff=0.45):
    if not obj.material_slots or not obj.material_slots[0].material:
        return
    mat = obj.material_slots[0].material
    tex_nodes = [n for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
    if not tex_nodes:
        return
    img = tex_nodes[0].image
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape((h, w, 4))
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    uv_layer = bm.loops.layers.uv.active
    seen = set()
    kill_faces = []
    for face in bm.faces:
        if face.index in seen:
            continue
        stack, comp = [face], []
        seen.add(face.index)
        while stack:
            cur = stack.pop()
            comp.append(cur)
            for edge in cur.edges:
                for linked in edge.link_faces:
                    if linked.index not in seen:
                        seen.add(linked.index)
                        stack.append(linked)
        alphas = [
            px[
                min(h - 1, max(0, int(loop[uv_layer].uv.y * (h - 1)))),
                min(w - 1, max(0, int(loop[uv_layer].uv.x * (w - 1)))),
                3,
            ]
            for f in comp
            for loop in f.loops
        ]
        if float(np.mean(alphas)) < alpha_cutoff:
            kill_faces.extend(comp)
    if kill_faces:
        bmesh.ops.delete(bm, geom=kill_faces, context="FACES")
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
        bm.to_mesh(obj.data)
    bm.free()


def _make_opaque(mat, roughness=None, metallic=None, base_rgb=None):
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if not bsdf:
        return
    for link in list(nt.links):
        if link.to_node == bsdf and link.to_socket.name == "Alpha":
            nt.links.remove(link)
    bsdf.inputs["Alpha"].default_value = 1.0
    mat.blend_method = "OPAQUE"
    if roughness is not None:
        bsdf.inputs["Roughness"].default_value = roughness
    if metallic is not None:
        bsdf.inputs["Metallic"].default_value = metallic
    if base_rgb is not None:
        for link in list(nt.links):
            if link.to_node == bsdf and link.to_socket.name == "Base Color":
                nt.links.remove(link)
        bsdf.inputs["Base Color"].default_value = (*base_rgb, 1.0)


def _tinted_weave_material(name, weave_img, normal_img, base_rgba, roughness=0.78, normal_strength=0.9):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.blend_method = "OPAQUE"
    nt = mat.node_tree
    nt.nodes.clear()
    out_n = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf_n = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf_n.inputs["Roughness"].default_value = roughness
    bsdf_n.inputs["Metallic"].default_value = 0.0

    tex_n = nt.nodes.new("ShaderNodeTexImage")
    tex_n.image = weave_img

    mix_n = nt.nodes.new("ShaderNodeMix")
    mix_n.data_type = "RGBA"
    mix_n.blend_type = "MULTIPLY"
    mix_n.inputs["Factor"].default_value = 1.0
    mix_n.inputs["B"].default_value = base_rgba

    nt.links.new(tex_n.outputs["Color"], mix_n.inputs["A"])
    nt.links.new(mix_n.outputs["Result"], bsdf_n.inputs["Base Color"])

    if normal_img is not None:
        norm_tex_n = nt.nodes.new("ShaderNodeTexImage")
        norm_tex_n.image = normal_img
        norm_map_n = nt.nodes.new("ShaderNodeNormalMap")
        norm_map_n.inputs["Strength"].default_value = normal_strength
        nt.links.new(norm_tex_n.outputs["Color"], norm_map_n.inputs["Color"])
        nt.links.new(norm_map_n.outputs["Normal"], bsdf_n.inputs["Normal"])

    nt.links.new(bsdf_n.outputs["BSDF"], out_n.inputs["Surface"])
    return mat


def _build_neutral_weave_image(name, diff_path, ao_path=None, norm_divisor=None):
    diff_img = bpy.data.images.load(diff_path)
    dw, dh = diff_img.size
    dpx = np.array(diff_img.pixels[:], dtype=np.float32).reshape((dh, dw, 4))
    lum = 0.299 * dpx[:, :, 0] + 0.587 * dpx[:, :, 1] + 0.114 * dpx[:, :, 2]
    if norm_divisor is None:
        active = lum[lum > 0.015]
        norm_divisor = max(0.18, float(np.percentile(active, 85))) if active.size > 0 else 0.5
    if ao_path and os.path.exists(ao_path):
        ao_img = bpy.data.images.load(ao_path)
        if tuple(ao_img.size) != (dw, dh):
            ao_img.scale(dw, dh)
        apx = np.array(ao_img.pixels[:], dtype=np.float32).reshape((dh, dw, 4))
        weave = np.clip((lum / norm_divisor) * (0.35 + 0.65 * apx[:, :, 0]), 0.12, 1.0)
        bpy.data.images.remove(ao_img)
    else:
        weave = np.clip(lum / norm_divisor, 0.12, 1.0)
    bpy.data.images.remove(diff_img)

    out_px = np.ones((dh, dw, 4), dtype=np.float32)
    out_px[:, :, 0] = weave
    out_px[:, :, 1] = weave
    out_px[:, :, 2] = weave
    tex = bpy.data.images.new(name, width=dw, height=dh, alpha=False)
    tex.pixels[:] = out_px.ravel().tolist()
    tex.pack()
    return tex


def _recolor_hair_alpha_image(name, src_img, target_rgb):
    w, h = src_img.size
    px = np.array(src_img.pixels[:], dtype=np.float32).reshape((h, w, 4))
    lum = 0.299 * px[:, :, 0] + 0.587 * px[:, :, 1] + 0.114 * px[:, :, 2]
    active = lum[px[:, :, 3] > 0.2]
    ref = max(0.14, float(np.percentile(active, 82))) if active.size > 0 else 0.35
    shade = np.clip(lum / ref, 0.15, 1.25)
    out = np.zeros((h, w, 4), dtype=np.float32)
    out[:, :, 0] = np.clip(shade * target_rgb[0], 0.0, 1.0)
    out[:, :, 1] = np.clip(shade * target_rgb[1], 0.0, 1.0)
    out[:, :, 2] = np.clip(shade * target_rgb[2], 0.0, 1.0)
    out[:, :, 3] = px[:, :, 3]
    tex = bpy.data.images.new(name, width=w, height=h, alpha=True)
    tex.pixels[:] = out.ravel().tolist()
    tex.pack()
    return tex


def _find_garment_textures(mhclo_path):
    gdir = os.path.dirname(mhclo_path)
    files = sorted(os.listdir(gdir))
    pngs = [f for f in files if f.lower().endswith(".png")]
    diff_f = next((f for f in pngs if "diff" in f.lower() or "base_color" in f.lower()), None)
    if diff_f is None:
        diff_f = next(
            (f for f in pngs if not any(k in f.lower() for k in ("norm", "bump", "spec", "ao", "rough", "height"))),
            None,
        )
    ao_f = next((f for f in pngs if "_ao" in f.lower()), None)
    norm_f = next((f for f in pngs if "norm" in f.lower()), None)
    return (
        os.path.join(gdir, diff_f) if diff_f else None,
        os.path.join(gdir, ao_f) if ao_f else None,
        os.path.join(gdir, norm_f) if norm_f else None,
    )


def _style_garment_components(garment_obj, mhclo_path, prefix, rules, norm_divisor=None):
    """Assign tinted PBR weave materials to topological garment components via predicate rules.

    Each rule is `(mat_name, rgba, roughness, predicate_fn(face_count, z_min, z_max))`.
    The first matching rule wins; unmatched faces keep the first rule's material.
    """
    diff_p, ao_p, norm_p = _find_garment_textures(mhclo_path)
    if not diff_p:
        return
    weave_tex = _build_neutral_weave_image(f"{prefix}_weave", diff_p, ao_p, norm_divisor=norm_divisor)
    norm_img = None
    if norm_p and os.path.exists(norm_p):
        norm_img = bpy.data.images.load(norm_p)
        norm_img.colorspace_settings.name = "Non-Color"

    garment_obj.data.materials.clear()
    for mname, rgba, rough, _ in rules:
        garment_obj.data.materials.append(
            _tinted_weave_material(mname, weave_tex, norm_img, (*rgba, 1.0), roughness=rough)
        )

    bm = bmesh.new()
    bm.from_mesh(garment_obj.data)
    seen = set()
    for face in bm.faces:
        if face.index in seen:
            continue
        stack, comp = [face], []
        seen.add(face.index)
        while stack:
            cur = stack.pop()
            comp.append(cur)
            for edge in cur.edges:
                for linked in edge.link_faces:
                    if linked.index not in seen:
                        seen.add(linked.index)
                        stack.append(linked)
        zs = [v.co.z for fc in comp for v in fc.verts]
        fcount, z0, z1 = len(comp), min(zs), max(zs)
        chosen = 0
        for idx_r, (_, _, _, pred) in enumerate(rules):
            if pred(fcount, z0, z1):
                chosen = idx_r
                break
        for fc in comp:
            fc.material_index = chosen
    bm.to_mesh(garment_obj.data)
    bm.free()


def _harbor_rename_map():
    rename = {
        "pelvis": "hips",
        "spine_03": "chest",
        "neck_01": "neck",
        "head": "head",
    }
    for s_mh, s_hb in (("l", "L"), ("r", "R")):
        rename[f"clavicle_{s_mh}"] = f"shoulder.{s_hb}"
        rename[f"upperarm_{s_mh}"] = f"upper_arm.{s_hb}"
        rename[f"lowerarm_{s_mh}"] = f"forearm.{s_hb}"
        rename[f"hand_{s_mh}"] = f"hand.{s_hb}"
        rename[f"thigh_{s_mh}"] = f"thigh.{s_hb}"
        rename[f"calf_{s_mh}"] = f"shin.{s_hb}"
        rename[f"foot_{s_mh}"] = f"foot.{s_hb}"
        rename[f"ball_{s_mh}"] = f"toe.{s_hb}"
        for finger in ("thumb", "index", "middle", "ring", "pinky"):
            for idx in (1, 2, 3):
                rename[f"{finger}_0{idx}_{s_mh}"] = f"{finger}_{idx}.{s_hb}"
    return rename


def _merge_vgroup(obj, src_name, dst_name):
    src = obj.vertex_groups.get(src_name)
    if not src:
        return
    dst = obj.vertex_groups.get(dst_name) or obj.vertex_groups.new(name=dst_name)
    s_idx = src.index
    for v in obj.data.vertices:
        sw = 0.0
        for g in v.groups:
            if g.group == s_idx:
                sw = g.weight
                break
        if sw > 0.0:
            dw = 0.0
            for g in v.groups:
                if g.group == dst.index:
                    dw = g.weight
                    break
            dst.add([v.index], min(1.0, dw + sw), "REPLACE")
    obj.vertex_groups.remove(src)


def _convert_to_harbor_51_rig(name, rig, parts, height):
    rename = _harbor_rename_map()
    keep_bones = set(rename.values()) | {"spine"}

    for part in parts:
        _merge_vgroup(part, "Root", "pelvis")
        _merge_vgroup(part, "spine_02", "spine_01")
        sp1 = part.vertex_groups.get("spine_01")
        if sp1:
            sp1.name = "spine"
        for old_n, new_n in rename.items():
            vg = part.vertex_groups.get(old_n)
            if vg and old_n != new_n:
                vg.name = new_n
        for vg in list(part.vertex_groups):
            if vg.name not in keep_bones:
                part.vertex_groups.remove(vg)

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    eb = rig.data.edit_bones
    eb["pelvis"].parent = None
    eb.remove(eb["Root"])
    sp1_eb = eb["spine_01"]
    sp2_eb = eb["spine_02"]
    sp3_eb = eb["spine_03"]
    sp1_eb.tail = sp2_eb.tail.copy()
    sp3_eb.parent = sp1_eb
    sp3_eb.use_connect = True
    eb.remove(eb["spine_02"])
    sp1_eb.name = "spine"
    for old_n, new_n in rename.items():
        if old_n in eb and old_n != new_n:
            eb[old_n].name = new_n
    bpy.ops.object.mode_set(mode="OBJECT")

    rig.name = rig.data.name = f"{name}_rig"

    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    a_rad = math.radians(A_POSE_DEG)
    for s_hb, sx in (("L", 1.0), ("R", -1.0)):
        arm_dir = Vector((sx * math.cos(a_rad), -0.02, -math.sin(a_rad))).normalized()
        for bname in (f"upper_arm.{s_hb}", f"forearm.{s_hb}", f"hand.{s_hb}"):
            pb = rig.pose.bones[bname]
            bpy.context.view_layer.update()
            cur_dir = (pb.tail - pb.head).normalized()
            q_world = cur_dir.rotation_difference(arm_dir)
            m_world = (rig.matrix_world @ pb.matrix).to_3x3()
            q_local = m_world.inverted().to_quaternion() @ q_world @ m_world.to_quaternion()
            pb.rotation_mode = "QUATERNION"
            pb.rotation_quaternion = (q_local @ pb.rotation_quaternion).normalized()

        q_in = Quaternion(Vector((0.0, 1.0, 0.0)), sx * math.radians(4.2))
        pb_thigh = rig.pose.bones[f"thigh.{s_hb}"]
        bpy.context.view_layer.update()
        m_t = (rig.matrix_world @ pb_thigh.matrix).to_3x3()
        pb_thigh.rotation_mode = "QUATERNION"
        pb_thigh.rotation_quaternion = (
            m_t.inverted().to_quaternion() @ q_in @ m_t.to_quaternion() @ pb_thigh.rotation_quaternion
        ).normalized()

        pb_foot = rig.pose.bones[f"foot.{s_hb}"]
        bpy.context.view_layer.update()
        m_f = (rig.matrix_world @ pb_foot.matrix).to_3x3()
        pb_foot.rotation_mode = "QUATERNION"
        pb_foot.rotation_quaternion = (
            m_f.inverted().to_quaternion() @ q_in.inverted() @ m_f.to_quaternion() @ pb_foot.rotation_quaternion
        ).normalized()

    bpy.ops.object.mode_set(mode="OBJECT")

    for part in parts:
        bpy.ops.object.select_all(action="DESELECT")
        bpy.context.view_layer.objects.active = part
        for mod in list(part.modifiers):
            if mod.type == "ARMATURE":
                bpy.ops.object.modifier_apply(modifier=mod.name)

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode="EDIT")
    for bone in rig.data.edit_bones:
        bone.roll = 0.0
    bpy.ops.object.mode_set(mode="OBJECT")

    bpy.ops.object.select_all(action="DESELECT")
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    mesh_obj = bpy.context.active_object
    mesh_obj.name = mesh_obj.data.name = name

    bpy.context.view_layer.update()
    zs = [(mesh_obj.matrix_world @ v.co).z for v in mesh_obj.data.vertices]
    z_min, z_max = min(zs), max(zs)
    scale_f = height / (z_max - z_min)

    for obj in (rig, mesh_obj):
        obj.location.z -= z_min
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    mesh_obj.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    rig.scale = (scale_f, scale_f, scale_f)
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    mesh_obj.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    mesh_obj.parent = rig
    arm_mod = mesh_obj.modifiers.new(name="Armature", type="ARMATURE")
    arm_mod.object = rig
    return mesh_obj, rig


def _cap_scene_textures():
    for img in bpy.data.images:
        if img.size[0] == 0 or img.size[1] == 0:
            continue
        stem = os.path.splitext(img.name)[0].lower()
        limit = 1024 if "skin" in stem or "diffuse" in stem else 512
        if img.size[0] > limit or img.size[1] > limit:
            ratio = limit / max(img.size)
            img.scale(max(1, round(img.size[0] * ratio)), max(1, round(img.size[1] * ratio)))
            if img.packed_file:
                img.pack()


def build_makehuman_person(
    name,
    height,
    macro,
    skin_name,
    eye_color,
    eyebrow_name,
    hair_name,
    garments,
    shoes_name,
    shoes_rgb=None,
    hair_rgb=None,
    glasses_name=None,
    glasses_rgb=(0.66, 0.50, 0.22),
    glasses_mat_name=None,
):
    """Build a distinct MakeHuman character mapped onto Harbor's 51-bone rig."""
    services = _load_mpfb()
    AssetService = services["AssetService"]
    HumanService = services["HumanService"]

    bm_obj = HumanService.create_human(
        mask_helpers=True,
        detailed_helpers=True,
        extra_vertex_groups=True,
        feet_on_ground=True,
        scale=0.1,
        macro_detail_dict=macro,
    )
    rig = HumanService.add_builtin_rig(bm_obj, "game_engine", import_weights=True)

    skins = {p.stem: str(p) for p in AssetService.list_mhmat_assets("skins")}
    HumanService.set_character_skin(skins[skin_name], bm_obj, skin_type="GAMEENGINE")

    eyes = {p.stem: str(p) for p in AssetService.list_mhclo_assets("eyes")}
    eye_obj = HumanService.add_mhclo_asset(
        eyes["high-poly"], bm_obj, asset_type="Eyes", subdiv_levels=0, material_type="GAMEENGINE"
    )
    eye_mats = {p.stem: str(p) for p in AssetService.list_mhmat_assets("eyes")}
    if eye_color in eye_mats:
        eye_dir = os.path.dirname(eye_mats[eye_color])
        eye_png = os.path.join(eye_dir, f"{eye_color}_eye.png")
        if os.path.exists(eye_png):
            eye_img = bpy.data.images.load(eye_png)
            for node in eye_obj.material_slots[0].material.node_tree.nodes:
                if node.type == "TEX_IMAGE":
                    node.image = eye_img

    teeth = {p.stem: str(p) for p in AssetService.list_mhclo_assets("teeth")}
    teeth_obj = HumanService.add_mhclo_asset(
        teeth["teeth_base"], bm_obj, asset_type="Teeth", subdiv_levels=0, material_type="GAMEENGINE"
    )

    brows = {p.stem: str(p) for p in AssetService.list_mhclo_assets("eyebrows")}
    brow_obj = HumanService.add_mhclo_asset(
        brows[eyebrow_name], bm_obj, asset_type="Eyebrows", subdiv_levels=0, material_type="GAMEENGINE"
    )

    hairs = {p.stem: str(p) for p in AssetService.list_mhclo_assets("hair")}
    hair_obj = HumanService.add_mhclo_asset(
        hairs[hair_name], bm_obj, asset_type="Hair", subdiv_levels=0, material_type="GAMEENGINE"
    )

    clothes = {p.stem: str(p) for p in AssetService.list_mhclo_assets("clothes")}
    garment_objs = []
    for gspec in garments:
        g_name = gspec["asset"]
        g_obj = HumanService.add_mhclo_asset(
            clothes[g_name], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
        )
        if gspec.get("hem_expand"):
            hem_push = float(gspec["hem_expand"])
            for v in g_obj.data.vertices:
                if v.co.z < 1.06:
                    w_push = max(0.0, min(1.0, (1.06 - v.co.z) / 0.16))
                    radial = Vector((v.co.x, v.co.y, 0.0))
                    if radial.length > 1e-4:
                        v.co += radial.normalized() * (hem_push * w_push)
            g_obj.data.update()
        if "rules" in gspec:
            _style_garment_components(
                g_obj,
                clothes[g_name],
                prefix=f"{name}_{g_name}",
                rules=gspec["rules"],
                norm_divisor=gspec.get("norm_divisor"),
            )
        else:
            for slot in g_obj.material_slots:
                if slot.material:
                    _make_opaque(slot.material, roughness=gspec.get("roughness", 0.82))
        garment_objs.append(g_obj)

    shoes_obj = HumanService.add_mhclo_asset(
        clothes[shoes_name], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
    )
    if shoes_rgb is not None:
        _style_garment_components(
            shoes_obj,
            clothes[shoes_name],
            prefix=f"{name}_shoes",
            rules=[(f"{name}_footwear", shoes_rgb, 0.48, lambda fc, z0, z1: True)],
        )
    else:
        shoes_obj.material_slots[0].material.name = f"{name}_footwear"
        _make_opaque(shoes_obj.material_slots[0].material, roughness=0.46)

    rigid_head_objs = [eye_obj, teeth_obj]
    extra_parts = []
    if glasses_name is not None:
        glasses_obj = HumanService.add_mhclo_asset(
            clothes[glasses_name], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
        )
        _remove_transparent_islands(glasses_obj, alpha_cutoff=0.45)
        _make_opaque(glasses_obj.material_slots[0].material, roughness=0.28, metallic=0.80, base_rgb=glasses_rgb)
        glasses_obj.material_slots[0].material.name = glasses_mat_name or f"{name}_glasses"
        rigid_head_objs.append(glasses_obj)
        extra_parts.append(glasses_obj)

    _remove_transparent_islands(eye_obj, alpha_cutoff=0.45)
    _make_opaque(eye_obj.material_slots[0].material, roughness=0.08)
    eye_obj.material_slots[0].material.name = f"{name}_eyes"

    if hair_rgb is not None:
        for obj, suffix in ((hair_obj, "hair"), (brow_obj, "brows")):
            mat = obj.material_slots[0].material
            for node in mat.node_tree.nodes:
                if node.type == "TEX_IMAGE" and node.image:
                    node.image = _recolor_hair_alpha_image(f"{name}_{suffix}_tex", node.image, hair_rgb)
                    break

    for obj, mname, mode in ((hair_obj, f"{name}_hair", "CLIP"), (brow_obj, f"{name}_eyebrows", "BLEND")):
        mat = obj.material_slots[0].material
        mat.name = mname
        mat.blend_method = mode
        if mode == "CLIP":
            mat.alpha_threshold = 0.28
        mat.use_backface_culling = False

    bm_obj.material_slots[0].material.name = f"{name}_skin"
    _make_opaque(bm_obj.material_slots[0].material, roughness=0.58)
    teeth_obj.material_slots[0].material.name = f"{name}_teeth"
    _make_opaque(teeth_obj.material_slots[0].material, roughness=0.25)

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = bm_obj
    if bm_obj.data.shape_keys:
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    for mod in list(bm_obj.modifiers):
        if mod.type == "MASK":
            bpy.ops.object.modifier_apply(modifier=mod.name)

    for rigid_obj in rigid_head_objs:
        rigid_obj.vertex_groups.clear()
        vg = rigid_obj.vertex_groups.new(name="head")
        vg.add(list(range(len(rigid_obj.data.vertices))), 1.0, "REPLACE")

    all_parts = [bm_obj, eye_obj, teeth_obj, brow_obj, hair_obj, *garment_objs, shoes_obj, *extra_parts]
    mesh_obj, rig = _convert_to_harbor_51_rig(name, rig, all_parts, height=height)

    _cap_scene_textures()
    return rig, mesh_obj


def build_amara_cole(name="chr_calloway_amara", height=1.69):
    """Amara Cole — Dock electrician (Pedestrian Slot 0) in signal-orange work sweater and olive trousers."""
    rig, mesh_obj = build_makehuman_person(
        name=name,
        height=height,
        macro={
            "gender": 0.0,
            "age": 0.39,
            "muscle": 0.55,
            "weight": 0.52,
            "proportions": 0.48,
            "height": 0.45,
            "cupsize": 0.48,
            "firmness": 0.52,
            "race": {"asian": 0.05, "caucasian": 0.07, "african": 0.88},
        },
        skin_name="young_african_female",
        eye_color="brown",
        eyebrow_name="eyebrow008",
        hair_name="elvs_double_mh_braid",
        garments=[
            {
                "asset": "toigo_fisherman_sweater",
                "hem_expand": 0.008,
                "rules": [
                    ("dockhand_hivis_vermilion", (0.82, 0.27, 0.06), 0.78, lambda fc, z0, z1: True),
                ],
            },
            {
                "asset": "toigo_wool_pants",
                "rules": [
                    ("dockhand_olive_canvas", (0.20, 0.23, 0.14), 0.88, lambda fc, z0, z1: True),
                ],
            },
        ],
        shoes_name="shoes02",
        shoes_rgb=(0.18, 0.11, 0.07),
    )
    return rig, mesh_obj


def build_mateo_silva(name="chr_calloway_mateo", height=1.84):
    """Mateo Silva — Quay stevedore (Pedestrian Slot 1) in marine-slate bib overalls and mustard work shirt."""
    rig, mesh_obj = build_makehuman_person(
        name=name,
        height=height,
        macro={
            "gender": 1.0,
            "age": 0.60,
            "muscle": 0.68,
            "weight": 0.68,
            "proportions": 0.50,
            "height": 0.68,
            "cupsize": 0.5,
            "firmness": 0.5,
            "race": {"asian": 0.08, "caucasian": 0.18, "african": 0.74},
        },
        skin_name="middleage_african_male",
        eye_color="brown",
        eyebrow_name="eyebrow011",
        hair_name="short02",
        hair_rgb=(0.04, 0.04, 0.04),
        garments=[
            {
                "asset": "male_worksuit01",
                "rules": [
                    ("stevedore_slate_bibs", (0.17, 0.26, 0.32), 0.86, lambda fc, z0, z1: fc > 1800),
                    ("stevedore_mustard_shirt", (0.66, 0.49, 0.18), 0.84, lambda fc, z0, z1: z0 > 0.98 and fc > 500),
                    ("stevedore_brass_buckle", (0.68, 0.53, 0.22), 0.42, lambda fc, z0, z1: True),
                ],
            }
        ],
        shoes_name="shoes05",
        shoes_rgb=(0.11, 0.09, 0.08),
    )
    return rig, mesh_obj


def build_linnea_park(name="chr_calloway_linnea", height=1.66):
    """Linnea Park — Market commuter (Pedestrian Slot 2) in cobalt jacket/top, faded jeans, and white sneakers."""
    rig, mesh_obj = build_makehuman_person(
        name=name,
        height=height,
        macro={
            "gender": 0.0,
            "age": 0.28,
            "muscle": 0.38,
            "weight": 0.40,
            "proportions": 0.50,
            "height": 0.38,
            "cupsize": 0.45,
            "firmness": 0.60,
            "race": {"asian": 0.82, "caucasian": 0.14, "african": 0.04},
        },
        skin_name="young_asian_female",
        eye_color="green",
        eyebrow_name="eyebrow003",
        hair_name="ponytail01",
        hair_rgb=(0.18, 0.09, 0.05),
        garments=[
            {
                "asset": "toigo_female_double-breasted_suit",
                "rules": [
                    ("hoodie_cobalt", (0.14, 0.33, 0.58), 0.78, lambda fc, z0, z1: fc > 1200 and z0 > 0.35),
                    ("commuter_denim", (0.24, 0.34, 0.48), 0.85, lambda fc, z0, z1: z0 < 0.25 and fc > 1200),
                    ("commuter_cream_shirt", (0.82, 0.80, 0.75), 0.76, lambda fc, z0, z1: True),
                ],
            }
        ],
        shoes_name="shoes04",
        shoes_rgb=(0.82, 0.83, 0.81),
    )
    return rig, mesh_obj


def build_arthur_pendelton(name="chr_calloway_arthur", height=1.75):
    """Arthur Pendelton — Customs broker (Pedestrian Slot 3) in burgundy-tweed suit, reading glasses, and oxfords."""
    rig, mesh_obj = build_makehuman_person(
        name=name,
        height=height,
        macro={
            "gender": 1.0,
            "age": 0.72,
            "muscle": 0.36,
            "weight": 0.56,
            "proportions": 0.49,
            "height": 0.54,
            "cupsize": 0.5,
            "firmness": 0.5,
            "race": {"asian": 0.12, "caucasian": 0.82, "african": 0.06},
        },
        skin_name="old_caucasian_male",
        eye_color="grey",
        eyebrow_name="eyebrow007",
        hair_name="short03",
        hair_rgb=(0.62, 0.63, 0.65),
        glasses_name="frankyaye_glasses_library_male",
        glasses_rgb=(0.24, 0.25, 0.28),
        glasses_mat_name="broker_silver_frames",
        garments=[
            {
                "asset": "male_elegantsuit01",
                "rules": [
                    ("broker_tweed_jacket", (0.24, 0.21, 0.18), 0.80, lambda fc, z0, z1: z1 > 1.10),
                    ("broker_charcoal_slacks", (0.14, 0.15, 0.17), 0.82, lambda fc, z0, z1: True),
                ],
            }
        ],
        shoes_name="shoes05",
        shoes_rgb=(0.14, 0.08, 0.06),
    )
    return rig, mesh_obj


CIVILIANS = [
    ("characters/chr_calloway_amara.glb", build_amara_cole),
    ("characters/chr_calloway_mateo.glb", build_mateo_silva),
    ("characters/chr_calloway_linnea.glb", build_linnea_park),
    ("characters/chr_calloway_arthur.glb", build_arthur_pendelton),
]
