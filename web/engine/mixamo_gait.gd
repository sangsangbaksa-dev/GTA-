extends RefCounted
class_name MixamoGaitWeb

const LIBRARY_NAME := "mixamo_local"
const LOOPING_CLIPS := [
	"Idle", "Walk", "Jog", "Run", "TurnLeft", "TurnRight", "Dance", "CrouchIdle", "CrouchWalk"
]

var _player: AnimationPlayer
var _prefix := ""
var _active_clip := ""
var _turning_latched := false

static func create(model: Node3D) -> MixamoGaitWeb:
	for node in model.find_children("*", "AnimationPlayer", true, false):
		var anim_player := node as AnimationPlayer
		if anim_player == null:
			continue
		var prefix := _try_configure_looping_library(anim_player)
		if prefix == "__NONE__":
			continue
		var gait := MixamoGaitWeb.new()
		gait._player = anim_player
		gait._prefix = prefix
		anim_player.active = true
		gait._play_clip("Idle", 0.0, 1.0)
		return gait
	return null

func update(
	speed: float,
	crouched: bool,
	sprinting: bool,
	dancing: bool,
	turn_rate: float,
	retreating: bool
) -> void:
	if _player == null:
		return

	var dir_sign := -1.0 if retreating else 1.0
	var abs_turn := absf(turn_rate)
	if abs_turn > 0.58:
		_turning_latched = true
	elif abs_turn < 0.24 or speed > 0.22:
		_turning_latched = false

	var target := "Idle"
	var blend_seconds := 0.25
	var speed_scale := 1.0

	if dancing and not crouched and speed <= 0.22:
		target = "Dance" if _has("Dance") else "Idle"
		blend_seconds = 0.26
	elif crouched:
		if speed > 0.20 and _has("CrouchWalk"):
			target = "CrouchWalk"
			speed_scale = clampf(speed / 1.95, 0.68, 1.42) * dir_sign
			blend_seconds = 0.20
		else:
			target = "CrouchIdle" if _has("CrouchIdle") else "Idle"
			blend_seconds = 0.22
	elif speed > 0.22:
		if ((sprinting and speed > 5.15) or speed > 5.45) and _has("Run"):
			target = "Run"
			speed_scale = clampf(speed / 6.0, 0.82, 1.28) * dir_sign
			blend_seconds = 0.20
		elif (sprinting or speed > 3.75) and _has("Jog"):
			target = "Jog"
			speed_scale = clampf(speed / 4.35, 0.80, 1.26) * dir_sign
			blend_seconds = 0.20
		else:
			target = "Walk"
			speed_scale = clampf(speed / 2.65, 0.72, 1.32) * dir_sign
			blend_seconds = 0.22
	elif _turning_latched:
		var turn_clip := "TurnLeft" if turn_rate > 0.0 else "TurnRight"
		target = turn_clip if _has(turn_clip) else "Idle"
		speed_scale = clampf(abs_turn / 2.1, 0.78, 1.45)
		blend_seconds = 0.18

	_play_clip(target, blend_seconds, speed_scale)

func _has(clip: String) -> bool:
	return _player != null and _player.has_animation(_prefix + clip)

func _play_clip(clip: String, blend_seconds: float, speed_scale: float) -> void:
	var full_name := _prefix + clip
	if not _player.has_animation(full_name):
		return
	if _active_clip != clip:
		_active_clip = clip
		_player.play(full_name, blend_seconds)
	_player.speed_scale = speed_scale

static func _try_configure_looping_library(player: AnimationPlayer) -> String:
	var source_lib := "__MISSING__"
	for lib_name in player.get_animation_library_list():
		var name_str := String(lib_name)
		var candidate := "" if name_str == "" else name_str + "/"
		if player.has_animation(candidate + "Idle") and player.has_animation(candidate + "Walk") and player.has_animation(candidate + "Run"):
			source_lib = name_str
			break

	if source_lib == "__MISSING__":
		return "__NONE__"

	if not player.has_animation_library(LIBRARY_NAME):
		var src := player.get_animation_library(source_lib)
		var local := AnimationLibrary.new()
		for anim_name in src.get_animation_list():
			var copy := src.get_animation(anim_name).duplicate() as Animation
			if LOOPING_CLIPS.has(String(anim_name)):
				copy.loop_mode = Animation.LOOP_LINEAR
			local.add_animation(anim_name, copy)
		player.add_animation_library(LIBRARY_NAME, local)

	return LIBRARY_NAME + "/"
