extends RefCounted
class_name RookGaitWeb

var _rig: Skeleton3D
var _left_leg: Dictionary
var _right_leg: Dictionary
var _left_arm: Dictionary
var _right_arm: Dictionary
var _pelvis: Dictionary
var _spine: Dictionary
var _chest: Dictionary
var _neck: Dictionary
var _pelvis_rest := Vector3.ZERO
var _step := 0.0
var _crouch := 0.0
var _run := 0.0
var _strafe := 0.0
var _alert := 0.0
var _breath := 0.0

static func create(model: Node3D) -> RookGaitWeb:
	for node in model.find_children("*", "Skeleton3D", true, false):
		var skel := node as Skeleton3D
		if skel == null:
			continue
		var gait := RookGaitWeb.new()
		gait._setup(skel)
		return gait
	return null

func _setup(rig: Skeleton3D) -> void:
	_rig = rig
	_left_leg = _read_leg("L")
	_right_leg = _read_leg("R")
	_left_arm = _read_arm("L")
	_right_arm = _read_arm("R")
	_pelvis = _read_bone("hips")
	_spine = _read_bone("spine")
	_chest = _read_bone("chest")
	_neck = _read_bone("neck")
	if int(_pelvis["index"]) >= 0:
		_pelvis_rest = _rig.get_bone_pose_position(int(_pelvis["index"]))

func update(
	phase: float,
	speed: float,
	crouched: bool,
	blend: float,
	dt: float = 0.0,
	sprinting: bool = false,
	hurried: bool = false,
	strafe: float = 0.0
) -> void:
	if _rig == null:
		return

	_step = lerpf(_step, smoothstep(0.0, 1.0, clampf(speed / 2.8, 0.0, 1.0)), blend)
	_crouch = lerpf(_crouch, 1.0 if crouched else 0.0, blend)
	_run = lerpf(_run, 1.0 if (sprinting and not crouched) else 0.0, blend)
	_strafe = lerpf(_strafe, strafe, blend)
	_alert = lerpf(_alert, 1.0 if hurried else 0.0, blend)
	_breath = fposmod(_breath + dt * (2.35 + 2.6 * _run), TAU)

	var swing := sin(phase)
	var breath := sin(_breath) * (1.0 - 0.70 * _step)
	var vertical := -0.37 * _crouch + (absf(cos(phase)) - 0.45) * (0.038 + 0.033 * _run) * _step
	var pelvis_idx := int(_pelvis["index"])
	if pelvis_idx >= 0:
		var center := Vector3(cos(phase) * 0.024 * _step, vertical, -0.14 * _crouch)
		var to_parent: Basis = _pelvis["to_parent"]
		_rig.set_bone_pose_position(pelvis_idx, _pelvis_rest + to_parent * center)

	var turn := swing * (0.048 + 0.09 * _run) * _step
	_pose(_pelvis, -0.07 * _crouch, -0.038 * swing * _step, turn)
	_pose(_spine, 0.34 * _crouch + 0.18 * _run, -0.075 * _strafe, -turn * 0.8)
	_pose(_chest, breath * 0.016 + 0.06 * _alert, 0.035 * swing * _step, -turn)
	_pose(_neck, -0.08 * _alert - 0.06 * _run, 0.02 * breath, _alert * swing * 0.17)

	_pose_leg(_left_leg, swing, -1.0)
	_pose_leg(_right_leg, -swing, 1.0)
	_pose_arm(_left_arm, swing, -1.0)
	_pose_arm(_right_arm, -swing, 1.0)

func _pose_leg(leg: Dictionary, swing: float, side: float) -> void:
	var travel := _step * (1.0 - 0.28 * _crouch)
	var bend := 1.90 * _crouch + travel * pow(maxf(0.0, swing), 1.45) * (0.44 + 0.47 * _run)
	var thigh := -travel * swing * (0.41 + 0.28 * _run) - 0.98 * _crouch
	_pose(leg["thigh"], thigh, side * 0.02 - 0.11 * _strafe, 0.08 * side * _strafe)
	_pose(leg["shin"], bend)
	_pose(leg["foot"], -0.64 * (thigh + bend), 0.0, -0.025 * side)

func _pose_arm(arm: Dictionary, swing: float, side: float) -> void:
	var stride := (0.23 + 0.29 * _run) * _step * swing
	var tuck := side * (0.69 - 0.045 * _run + 0.035 * _alert)
	_pose(arm["upper"], stride - _crouch * 0.15 - _alert * 0.08, tuck)
	var elbow := 0.17 + 0.76 * _run + 0.13 * _alert + 0.11 * maxf(0.0, swing) * _step
	_pose(arm["fore"], -elbow)

func _read_leg(side: String) -> Dictionary:
	return {
		"thigh": _read_bone("thigh." + side),
		"shin": _read_bone("shin." + side),
		"foot": _read_bone("foot." + side),
	}

func _read_arm(side: String) -> Dictionary:
	return {
		"upper": _read_bone("upper_arm." + side),
		"fore": _read_bone("forearm." + side),
	}

func _read_bone(bone_name: String) -> Dictionary:
	var idx := _rig.find_bone(bone_name)
	if idx < 0:
		return {"index": -1, "rest": Quaternion.IDENTITY, "to_parent": Basis.IDENTITY}
	var parent := _rig.get_bone_parent(idx)
	var parent_basis := Basis.IDENTITY if parent < 0 else _rig.get_bone_global_rest(parent).basis.orthonormalized()
	return {
		"index": idx,
		"rest": _rig.get_bone_pose_rotation(idx),
		"to_parent": parent_basis.inverse(),
	}

func _pose(bone: Dictionary, pitch: float, roll: float = 0.0, yaw: float = 0.0) -> void:
	var idx := int(bone["index"])
	if idx < 0:
		return
	var to_parent: Basis = bone["to_parent"]
	var rest: Quaternion = bone["rest"]
	var q_swing := Quaternion((to_parent * Vector3.RIGHT).normalized(), pitch)
	var q_lower := Quaternion((to_parent * Vector3.BACK).normalized(), roll)
	var q_turn := Quaternion((to_parent * Vector3.UP).normalized(), yaw)
	_rig.set_bone_pose_rotation(idx, (q_turn * q_swing * q_lower * rest).normalized())
