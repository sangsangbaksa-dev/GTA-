"""Headless MakeHuman (MPFB2) character builder adapted to Harbor's 51-bone rig.

Requires Blender 4.2+ with MPFB2 and CC0 MakeHuman asset packs installed
(run `tools/blender/setup_makehuman.sh` once per machine).

Produces a single joined skinned mesh `<name>` parented to `<name>_rig` with:
  * Harbor's exact 51-bone hierarchy (`hips`, `spine`, `chest`, `neck`, `head`,
    `shoulder/upper_arm/forearm/hand.{L,R}`, `{thumb,index,middle,ring,pinky}_{1..3}.{L,R}`,
    `thigh/shin/foot/toe.{L,R}`), roll = 0, 42-degree A-pose rest stance
  * Feet grounded at Z = 0, facing -Y in Blender (+Z in glTF / Godot)
  * glTF-ready PBR materials with neutral grayscale weave + baseColorFactor on
    recolorable garments (e.g. `clerk_ochre_coat` for `StreetPopulationView`)
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


def _build_neutral_weave_image(name, diff_path, ao_path=None, norm_divisor=0.27):
    diff_img = bpy.data.images.load(diff_path)
    dw, dh = diff_img.size
    dpx = np.array(diff_img.pixels[:], dtype=np.float32).reshape((dh, dw, 4))
    lum = 0.299 * dpx[:, :, 0] + 0.587 * dpx[:, :, 1] + 0.114 * dpx[:, :, 2]
    if ao_path and os.path.exists(ao_path):
        ao_img = bpy.data.images.load(ao_path)
        apx = np.array(ao_img.pixels[:], dtype=np.float32).reshape((dh, dw, 4))
        weave = np.clip((lum / norm_divisor) * (0.35 + 0.65 * apx[:, :, 0]), 0.10, 1.0)
        bpy.data.images.remove(ao_img)
    else:
        weave = np.clip(lum / norm_divisor, 0.10, 1.0)
    bpy.data.images.remove(diff_img)

    out_px = np.ones((dh, dw, 4), dtype=np.float32)
    out_px[:, :, 0] = weave
    out_px[:, :, 1] = weave
    out_px[:, :, 2] = weave
    tex = bpy.data.images.new(name, width=dw, height=dh, alpha=False)
    tex.pixels[:] = out_px.ravel().tolist()
    tex.pack()
    return tex


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

    # Pose into Harbor's 42-degree A-pose and upright leg alignment, then bake as rest pose.
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


def build_makehuman_clerk(name="chr_harbor_clerk", height=1.76):
    """Tomas Reyes — Quay freight clerk built on MakeHuman (MPFB2 + CC0 MakeHuman assets)."""
    services = _load_mpfb()
    AssetService = services["AssetService"]
    HumanService = services["HumanService"]

    macro = {
        "gender": 1.0,
        "age": 0.42,
        "muscle": 0.46,
        "weight": 0.45,
        "proportions": 0.52,
        "height": 0.58,
        "cupsize": 0.5,
        "firmness": 0.5,
        "race": {"asian": 0.22, "caucasian": 0.68, "african": 0.10},
    }

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
    HumanService.set_character_skin(skins["young_caucasian_male2"], bm_obj, skin_type="GAMEENGINE")

    eyes = {p.stem: str(p) for p in AssetService.list_mhclo_assets("eyes")}
    eye_obj = HumanService.add_mhclo_asset(
        eyes["high-poly"], bm_obj, asset_type="Eyes", subdiv_levels=0, material_type="GAMEENGINE"
    )
    brownlight_mhmat = AssetService.find_asset_absolute_path("eyes/materials/brownlight.mhmat", "eyes")
    if brownlight_mhmat:
        hazel_png = os.path.join(os.path.dirname(brownlight_mhmat), "brownlight_eye.png")
        if os.path.exists(hazel_png):
            hazel_img = bpy.data.images.load(hazel_png)
            for node in eye_obj.material_slots[0].material.node_tree.nodes:
                if node.type == "TEX_IMAGE":
                    node.image = hazel_img

    teeth = {p.stem: str(p) for p in AssetService.list_mhclo_assets("teeth")}
    teeth_obj = HumanService.add_mhclo_asset(
        teeth["teeth_base"], bm_obj, asset_type="Teeth", subdiv_levels=0, material_type="GAMEENGINE"
    )

    brows = {p.stem: str(p) for p in AssetService.list_mhclo_assets("eyebrows")}
    brow_obj = HumanService.add_mhclo_asset(
        brows["eyebrow002"], bm_obj, asset_type="Eyebrows", subdiv_levels=0, material_type="GAMEENGINE"
    )

    hairs = {p.stem: str(p) for p in AssetService.list_mhclo_assets("hair")}
    hair_obj = HumanService.add_mhclo_asset(
        hairs["short02"], bm_obj, asset_type="Hair", subdiv_levels=0, material_type="GAMEENGINE"
    )

    clothes = {p.stem: str(p) for p in AssetService.list_mhclo_assets("clothes")}
    suit_obj = HumanService.add_mhclo_asset(
        clothes["male_casualsuit05"], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
    )
    shoes_obj = HumanService.add_mhclo_asset(
        clothes["shoes01"], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
    )
    glasses_obj = HumanService.add_mhclo_asset(
        clothes["kwnet_at_optical_glasses"], bm_obj, asset_type="Clothes", subdiv_levels=0, material_type="GAMEENGINE"
    )

    _remove_transparent_islands(eye_obj, alpha_cutoff=0.45)
    _remove_transparent_islands(glasses_obj, alpha_cutoff=0.45)

    _make_opaque(eye_obj.material_slots[0].material, roughness=0.08)
    eye_obj.material_slots[0].material.name = "clerk_eyes"

    _make_opaque(glasses_obj.material_slots[0].material, roughness=0.28, metallic=0.82, base_rgb=(0.66, 0.50, 0.22))
    glasses_obj.material_slots[0].material.name = "clerk_aged_brass"

    for obj, mname, mode in ((hair_obj, "clerk_hair", "CLIP"), (brow_obj, "clerk_eyebrows", "BLEND")):
        mat = obj.material_slots[0].material
        mat.name = mname
        mat.blend_method = mode
        if mode == "CLIP":
            mat.alpha_threshold = 0.28
        mat.use_backface_culling = False

    bm_obj.material_slots[0].material.name = "clerk_skin"
    _make_opaque(bm_obj.material_slots[0].material, roughness=0.58)
    teeth_obj.material_slots[0].material.name = "clerk_teeth"
    _make_opaque(teeth_obj.material_slots[0].material, roughness=0.25)
    shoes_obj.material_slots[0].material.name = "clerk_leather_shoes"
    _make_opaque(shoes_obj.material_slots[0].material, roughness=0.46)

    # Separate male_casualsuit05 into denim trousers, oatmeal work shirt, and recolorable clerk_ochre_coat.
    denim_mat = suit_obj.material_slots[0].material
    denim_mat.name = "clerk_denim_trousers"
    _make_opaque(denim_mat, roughness=0.84)

    suit_dir = os.path.dirname(clothes["male_casualsuit05"])
    diff_path = os.path.join(suit_dir, "male_casualsuit05_diffuse.png")
    ao_path = os.path.join(suit_dir, "male_casualsuit05_ao.png")
    norm_img = bpy.data.images.load(os.path.join(suit_dir, "male_casualsuit05_normal.png"))
    norm_img.colorspace_settings.name = "Non-Color"

    weave_tex = _build_neutral_weave_image("clerk_coat_weave", diff_path, ao_path, norm_divisor=0.27)
    coat_mat = _tinted_weave_material(
        "clerk_ochre_coat", weave_tex, norm_img, (0.52, 0.33, 0.11, 1.0), roughness=0.78, normal_strength=0.9
    )
    shirt_mat = _tinted_weave_material(
        "clerk_oat_knit", weave_tex, norm_img, (0.58, 0.53, 0.44, 1.0), roughness=0.88, normal_strength=0.55
    )
    suit_obj.data.materials.append(coat_mat)
    suit_obj.data.materials.append(shirt_mat)

    bm_suit = bmesh.new()
    bm_suit.from_mesh(suit_obj.data)
    seen = set()
    for face in bm_suit.faces:
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
        if len(comp) in (1600, 234):
            for f in comp:
                f.material_index = 1  # clerk_ochre_coat
        elif len(comp) == 308:
            for f in comp:
                f.material_index = 2  # clerk_oat_knit
    bm_suit.to_mesh(suit_obj.data)
    bm_suit.free()

    # Bake MakeHuman morph shape keys and permanently apply MASK modifiers on the base mesh.
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = bm_obj
    if bm_obj.data.shape_keys:
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    for mod in list(bm_obj.modifiers):
        if mod.type == "MASK":
            bpy.ops.object.modifier_apply(modifier=mod.name)

    for rigid_obj in (eye_obj, teeth_obj, glasses_obj):
        rigid_obj.vertex_groups.clear()
        vg = rigid_obj.vertex_groups.new(name="head")
        vg.add(list(range(len(rigid_obj.data.vertices))), 1.0, "REPLACE")

    all_parts = [bm_obj, eye_obj, teeth_obj, brow_obj, hair_obj, suit_obj, shoes_obj, glasses_obj]
    mesh_obj, rig = _convert_to_harbor_51_rig(name, rig, all_parts, height=height)

    # Cap texture resolutions so the exported GLB stays ~4-5 MB in Git LFS.
    primary_1k = {"young_lightskinned_male_diffuse2", "male_casualsuit05_diffuse", "male_casualsuit05_normal", "short02_diffuse"}
    for img in bpy.data.images:
        if img.size[0] == 0 or img.size[1] == 0:
            continue
        stem = os.path.splitext(img.name)[0]
        limit = 1024 if any(k in stem for k in primary_1k) else 512
        if img.size[0] > limit or img.size[1] > limit:
            img.scale(min(limit, img.size[0]), min(limit, img.size[1]))
            if img.packed_file:
                img.pack()

    return mesh_obj, rig
