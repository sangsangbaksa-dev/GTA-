extends Node3D

const WHEEL_RADIUS := 0.33
const CAR_PAINT := ["#3e5e65", "#784745", "#a69c7d", "#4c5979"]
const WEAPON_PATHS := {
	"kestrel_p10": "res://assets/weapons/wpn_pistol_kestrel.glb",
	"wasp_smg": "res://assets/weapons/wpn_smg_wasp.glb",
	"halberd_ar": "res://assets/weapons/wpn_rifle_halberd.glb",
	"breaker_12": "res://assets/weapons/wpn_shotgun_breaker.glb",
}
const ROSTER_MODELS := [
	"res://assets/characters/chr_harbor_clerk.glb",
	"res://assets/characters/chr_harbor_officer.glb",
	"res://assets/characters/chr_harbor_crew.glb",
	"res://assets/characters/chr_harbor_rook.glb",
	"res://assets/characters/chr_harbor_dockhand.glb",
	"res://assets/characters/chr_harbor_stevedore.glb",
	"res://assets/characters/chr_npc_civilian.glb",
	"res://assets/characters/chr_harbor_broker.glb",
]
const AUDIO_NAMES := [
	"dock_ambience", "district_alert", "kestrel", "wasp", "halberd", "breaker",
	"impact", "hit", "hurt", "reload", "ready", "swap", "step_1", "step_2", "step_3",
]

var _world_root: Node3D
var _streamer: WorldStreamerWeb
var _camera: Camera3D

var _player_root: Node3D
var _player_model: Node3D
var _player_mixamo: MixamoGaitWeb
var _player_gait: RookGaitWeb
var _player_weapon_anchor: Node3D
var _player_equipped_id := ""
var _player_body_yaw := 0.0
var _player_phase := 0.0
var _step_distance := 0.0
var _last_player_pos := Vector3.ZERO
var _has_last_pos := false
var _was_reloading := false

var _vehicle_root: Node3D
var _vehicle_fl: Node3D
var _vehicle_fr: Node3D
var _vehicle_rolls: Array[Node3D] = []

var _ped_scene_slots: Array[PackedScene] = []
var _crew_scene: PackedScene
var _officer_scene: PackedScene
var _car_scene: PackedScene
var _weapon_scenes: Dictionary = {}
var _sounds: Dictionary = {}
var _ambience_player: AudioStreamPlayer
var _audio_unlocked := false

var _pedestrians: Dictionary = {}
var _traffic_cars: Dictionary = {}
var _enemies: Dictionary = {}
var _effects: Array[Dictionary] = []
var _markers: Node3D
var _start_ring: MeshInstance3D
var _waypoint_ring: MeshInstance3D

var _roster_stage: Node3D
var _roster_turntable: Node3D
var _roster_camera: Camera3D
var _roster_person: Node3D
var _roster_index := -1
var _roster_height := 1.75

var _run_generation := -1
var _rng := RandomNumberGenerator.new()

func _ready() -> void:
	_rng.seed = 613
	_world_root = Node3D.new()
	_world_root.name = "HarborWorld"
	add_child(_world_root)

	_load_backdrop()
	_streamer = WorldStreamerWeb.new()
	_streamer.name = "WorldStreamer"
	_world_root.add_child(_streamer)

	_preload_assets()
	_setup_player()
	_setup_vehicle()
	_setup_markers()
	_setup_roster_stage()

	_camera = Camera3D.new()
	_camera.name = "MainCamera"
	_camera.fov = 68.0
	_camera.near = 0.05
	_camera.far = 950.0
	_camera.current = true
	_world_root.add_child(_camera)
	_camera.global_position = Vector3(-16.0, 9.5, 18.0)
	_camera.look_at(Vector3(2.0, 1.4, -12.0), Vector3.UP)

	if OS.has_feature("web"):
		JavaScriptBridge.eval("window.__HARBOR_GODOT_READY = true;", true)

func _process(delta: float) -> void:
	var dt := clampf(delta, 0.0, 0.1)
	_update_effects(dt)

	if not OS.has_feature("web"):
		return

	_publish_stream_status()
	_sync_audio_unlock()

	var roster_json: Variant = JavaScriptBridge.eval("window.__HARBOR_ROSTER_JSON || ''", true)
	if typeof(roster_json) == TYPE_STRING and String(roster_json) != "":
		var roster_state: Variant = JSON.parse_string(String(roster_json))
		if typeof(roster_state) == TYPE_DICTIONARY and bool(roster_state.get("active", false)):
			_apply_roster_mode(roster_state)
			return
	_exit_roster_mode()

	var raw_frame: Variant = JavaScriptBridge.eval("window.__HARBOR_CONSUME_FRAME ? window.__HARBOR_CONSUME_FRAME() : (window.__HARBOR_FRAME_JSON || '')", true)
	if typeof(raw_frame) != TYPE_STRING or String(raw_frame) == "":
		_streamer.update_focus(Vector3(0.0, 0.0, 4.0), Vector3.ZERO)
		return

	var parsed: Variant = JSON.parse_string(String(raw_frame))
	if typeof(parsed) != TYPE_DICTIONARY:
		return
	var frame: Dictionary = parsed
	_apply_frame(frame, dt)

func _load_backdrop() -> void:
	var path := "res://scenes/world/HarborBackdrop.scn"
	if ResourceLoader.exists(path):
		var backdrop := load(path) as PackedScene
		if backdrop != null:
			var inst := backdrop.instantiate()
			inst.name = "HarborBackdrop"
			_world_root.add_child(inst)
			return

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color("#5d8aa6")
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color("#d2e0ee")
	env.ambient_light_energy = 0.85
	var we := WorldEnvironment.new()
	we.environment = env
	_world_root.add_child(we)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-38.0, -34.0, 0.0)
	sun.light_color = Color("#ffe5c4")
	sun.light_energy = 1.15
	sun.shadow_enabled = true
	_world_root.add_child(sun)

func _preload_assets() -> void:
	var ped_paths := [
		"res://assets/characters/chr_harbor_dockhand.glb",
		"res://assets/characters/chr_harbor_stevedore.glb",
		"res://assets/characters/chr_npc_civilian.glb",
		"res://assets/characters/chr_harbor_broker.glb",
	]
	for path in ped_paths:
		_ped_scene_slots.append(_load_scene(path))

	_crew_scene = _load_scene("res://assets/characters/chr_harbor_crew.glb")
	if _crew_scene == null:
		_crew_scene = _load_scene("res://assets/characters/chr_enemy_gunman.glb")

	_officer_scene = _load_scene("res://assets/characters/chr_harbor_officer.glb")
	if _officer_scene == null:
		_officer_scene = _load_scene("res://assets/characters/chr_enemy_enforcer.glb")

	_car_scene = _load_scene("res://assets/vehicles/veh_calloway_compact.glb")

	for wid in WEAPON_PATHS.keys():
		var wscene := _load_scene(String(WEAPON_PATHS[wid]))
		if wscene != null:
			_weapon_scenes[wid] = wscene

	if DisplayServer.get_name() != "headless":
		for cue in AUDIO_NAMES:
			var apath := "res://assets/audio/" + String(cue) + ".ogg"
			if ResourceLoader.exists(apath):
				var stream := load(apath) as AudioStreamOggVorbis
				if stream != null:
					stream.loop = (cue == "dock_ambience" or cue == "district_alert")
					_sounds[cue] = stream

func _setup_player() -> void:
	_player_root = Node3D.new()
	_player_root.name = "PlayerAvatar"
	_world_root.add_child(_player_root)

	var clerk := _load_scene("res://assets/characters/chr_harbor_clerk.glb")
	if clerk == null:
		clerk = _load_scene("res://assets/characters/chr_player_rook.glb")
	if clerk != null:
		_player_model = clerk.instantiate() as Node3D
		_player_model.rotation = Vector3(0.0, PI, 0.0)
		_player_root.add_child(_player_model)
		_player_mixamo = MixamoGaitWeb.create(_player_model)
		if _player_mixamo == null:
			_player_gait = RookGaitWeb.create(_player_model)
		_player_weapon_anchor = _attach_hand_socket(_player_model)

func _setup_vehicle() -> void:
	_vehicle_root = Node3D.new()
	_vehicle_root.name = "PlayerVehicle"
	_world_root.add_child(_vehicle_root)
	if _car_scene == null:
		return
	var chassis := _car_scene.instantiate() as Node3D
	chassis.rotate_y(PI)
	_vehicle_root.add_child(chassis)
	_vehicle_fl = chassis.find_child("WheelFL", true, false) as Node3D
	_vehicle_fr = chassis.find_child("WheelFR", true, false) as Node3D
	for wname in ["WheelFLRoll", "WheelFRRoll", "WheelRLRoll", "WheelRRRoll"]:
		var w := chassis.find_child(wname, true, false) as Node3D
		if w != null:
			_vehicle_rolls.append(w)

func _setup_markers() -> void:
	_markers = Node3D.new()
	_markers.name = "ObjectiveMarkers"
	_world_root.add_child(_markers)
	_start_ring = _make_ring(Color("#f7bc70"))
	_waypoint_ring = _make_ring(Color("#88ded5"))
	_markers.add_child(_start_ring)
	_markers.add_child(_waypoint_ring)

func _make_ring(col: Color) -> MeshInstance3D:
	var torus := TorusMesh.new()
	torus.inner_radius = 2.05
	torus.outer_radius = 2.32
	torus.rings = 36
	torus.ring_segments = 12
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(col.r, col.g, col.b, 0.78)
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.emission_enabled = true
	mat.emission = col
	mat.emission_energy_multiplier = 1.15
	var mesh := MeshInstance3D.new()
	mesh.mesh = torus
	mesh.material_override = mat
	mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	mesh.visible = false
	return mesh

func _setup_roster_stage() -> void:
	_roster_stage = Node3D.new()
	_roster_stage.name = "RosterStage"
	_roster_stage.position = Vector3(0.0, -420.0, 0.0)
	_roster_stage.visible = false
	add_child(_roster_stage)

	var key_light := DirectionalLight3D.new()
	key_light.rotation_degrees = Vector3(-30.0, -32.0, 0.0)
	key_light.light_color = Color("#ffe7cc")
	key_light.light_energy = 1.25
	_roster_stage.add_child(key_light)

	var rim_light := OmniLight3D.new()
	rim_light.position = Vector3(-1.8, 2.2, -1.9)
	rim_light.light_color = Color("#88ded5")
	rim_light.light_energy = 1.4
	rim_light.omni_range = 7.0
	_roster_stage.add_child(rim_light)

	var backdrop_mesh := BoxMesh.new()
	backdrop_mesh.size = Vector3(9.0, 6.0, 0.2)
	var backdrop_mat := StandardMaterial3D.new()
	backdrop_mat.albedo_color = Color("#16323d")
	backdrop_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	var backdrop := MeshInstance3D.new()
	backdrop.mesh = backdrop_mesh
	backdrop.material_override = backdrop_mat
	backdrop.position = Vector3(0.0, 2.2, -2.6)
	_roster_stage.add_child(backdrop)

	var plinth := CylinderMesh.new()
	plinth.top_radius = 0.92
	plinth.bottom_radius = 1.06
	plinth.height = 0.10
	plinth.radial_segments = 48
	var plinth_mat := StandardMaterial3D.new()
	plinth_mat.albedo_color = Color("#294b56")
	plinth_mat.roughness = 0.85
	var plinth_node := MeshInstance3D.new()
	plinth_node.mesh = plinth
	plinth_node.material_override = plinth_mat
	plinth_node.position = Vector3(0.0, -0.05, 0.0)
	_roster_stage.add_child(plinth_node)

	_roster_turntable = Node3D.new()
	_roster_stage.add_child(_roster_turntable)

	_roster_camera = Camera3D.new()
	_roster_camera.fov = 35.0
	_roster_camera.near = 0.03
	_roster_camera.far = 20.0
	_roster_stage.add_child(_roster_camera)

func _apply_roster_mode(state: Dictionary) -> void:
	_world_root.visible = false
	_roster_stage.visible = true
	if not _roster_camera.current:
		_roster_camera.make_current()

	var idx := posmod(int(state.get("index", 0)), ROSTER_MODELS.size())
	if idx != _roster_index:
		_roster_index = idx
		if _roster_person != null:
			_roster_turntable.remove_child(_roster_person)
			_roster_person.queue_free()
			_roster_person = null
		var scene := _load_scene(String(ROSTER_MODELS[idx]))
		_roster_height = 1.75
		if scene != null:
			_roster_person = scene.instantiate() as Node3D
			_roster_turntable.add_child(_roster_person)
			var mix := MixamoGaitWeb.create(_roster_person)
			if mix == null:
				var rg := RookGaitWeb.create(_roster_person)
				if rg != null:
					rg.update(0.0, 0.0, false, 1.0)
			for child in _roster_person.find_children("*", "MeshInstance3D", true, false):
				var mi := child as MeshInstance3D
				if mi != null:
					var top := _roster_person.to_local(mi.to_global(mi.get_aabb().end))
					_roster_height = maxf(_roster_height, top.y)

	var yaw_deg := float(state.get("yawDeg", -12.0))
	_roster_turntable.rotation_degrees = Vector3(-0.0, yaw_deg, 0.0)
	var face := bool(state.get("face", false))
	var target := Vector3(0.0, -420.0, 0.0) + Vector3.UP * _roster_height * (0.91 if face else 0.51)
	var offset := Vector3(-0.34 if not face else -0.10, 0.023 if face else 0.10, 0.68 if face else _roster_height * 1.78)
	_roster_camera.global_position = target + offset
	_roster_camera.look_at(target + Vector3(0.22 if not face else 0.06, 0.0, 0.0), Vector3.UP)

func _exit_roster_mode() -> void:
	if _roster_stage.visible:
		_roster_stage.visible = false
		_world_root.visible = true
		_camera.make_current()

func _apply_frame(frame: Dictionary, dt: float) -> void:
	var gen := int(frame.get("runGeneration", 1))
	if gen != _run_generation:
		_run_generation = gen
		_clear_dynamic_views()

	var paused := bool(frame.get("paused", false))
	var driving := bool(frame.get("isDriving", false))
	var player_data: Dictionary = frame.get("player", {})
	var cam_data: Dictionary = frame.get("camera", {})
	var combat_data: Dictionary = frame.get("combat", {})
	var vehicle_data: Dictionary = frame.get("vehicle", {})
	var streaming_cfg: Dictionary = frame.get("streaming", {})

	var player_pos := _vec3(player_data.get("position", [0.0, 0.0, 4.0]))
	var player_vel := _vec3(player_data.get("velocity", [0.0, 0.0, 0.0]))
	_streamer.update_focus(player_pos, player_vel, streaming_cfg)

	_update_player(player_pos, player_vel, player_data, cam_data, combat_data, driving, paused, dt)
	_update_vehicle(vehicle_data)
	_update_camera(cam_data, player_pos, paused)
	_update_ambient(frame.get("ambient", {}), dt)
	_update_enemies(frame.get("enemies", []), dt)
	_update_markers(frame.get("lockdown", {}), frame.get("jobs", {}))
	_handle_events(frame.get("shots", []), frame.get("damages", []), frame.get("kills", []))

func _update_player(
	pos: Vector3,
	vel: Vector3,
	player_data: Dictionary,
	cam_data: Dictionary,
	combat_data: Dictionary,
	driving: bool,
	paused: bool,
	dt: float
) -> void:
	_player_root.visible = not driving
	_player_root.position = pos

	var planar := Vector2(vel.x, vel.z).length()
	var aiming := bool(cam_data.get("aiming", false))
	var cam_yaw := float(cam_data.get("yaw", 0.0))
	var crouched := bool(player_data.get("crouched", false))
	var sprinting := bool(player_data.get("sprinting", false))
	var dancing := bool(player_data.get("dancing", false))
	var blend := 1.0 - exp(-12.0 * dt)

	var prev_yaw := _player_body_yaw
	if aiming:
		_player_body_yaw = lerp_angle(_player_body_yaw, cam_yaw, blend)
	elif planar > 0.15:
		_player_body_yaw = lerp_angle(_player_body_yaw, atan2(-vel.x, -vel.z), blend)
	elif not dancing and absf(angle_difference(_player_body_yaw, cam_yaw)) > 1.05:
		_player_body_yaw = lerp_angle(_player_body_yaw, cam_yaw, 1.0 - exp(-6.5 * dt))

	_player_root.rotation = Vector3(0.0, _player_body_yaw, 0.0)
	var turn_rate := angle_difference(prev_yaw, _player_body_yaw) / maxf(dt, 0.001)
	var local_vel := Basis(Vector3.UP, -_player_body_yaw) * vel
	var retreating := aiming and local_vel.z > 0.35

	var wid := String(combat_data.get("weaponId", ""))
	if wid != _player_equipped_id:
		if _player_equipped_id != "" and not paused:
			_play_cue("swap", -6.0)
		_player_equipped_id = wid
		_equip_on_socket(_player_weapon_anchor, wid, not driving and not dancing)
	elif _player_weapon_anchor != null:
		_player_weapon_anchor.visible = not driving and not dancing

	var reloading := bool(combat_data.get("isReloading", false))
	if reloading != _was_reloading:
		_was_reloading = reloading
		if not paused:
			_play_cue("reload" if reloading else "ready", -5.0)

	if _player_mixamo != null:
		_player_mixamo.update(planar if not paused else 0.0, crouched, sprinting, dancing, turn_rate, retreating)
	elif _player_gait != null:
		_player_phase = fposmod(_player_phase + minf(planar, 9.0) * dt * (2.45 if sprinting else 3.35), TAU)
		_player_gait.update(_player_phase, planar if not paused else 0.0, crouched, blend, dt, sprinting)

	if _has_last_pos and not paused and not driving and planar > 0.2:
		var moved := pos.distance_to(_last_player_pos)
		if moved < 2.0:
			_step_distance += moved
			var interval := 0.57 if crouched else 0.96
			if _step_distance >= interval:
				_step_distance = fmod(_step_distance, interval)
				_play_cue("step_" + str(_rng.randi_range(1, 3)), -14.0 if crouched else -9.0)
	else:
		_step_distance = 0.0
	_last_player_pos = pos
	_has_last_pos = true

func _update_vehicle(vehicle_data: Dictionary) -> void:
	var pos := _vec3(vehicle_data.get("position", [5.0, 0.0, 9.0]))
	var yaw := float(vehicle_data.get("yaw", 0.0))
	var steering := float(vehicle_data.get("steering", 0.0))
	var distance := float(vehicle_data.get("distance", 0.0))

	_vehicle_root.position = pos
	_vehicle_root.rotation = Vector3(0.0, yaw, 0.0)
	var steer_rot := Vector3(0.0, -steering, 0.0)
	if _vehicle_fl != null:
		_vehicle_fl.rotation = steer_rot
	if _vehicle_fr != null:
		_vehicle_fr.rotation = steer_rot
	var roll := fmod(distance / WHEEL_RADIUS, TAU)
	for wheel in _vehicle_rolls:
		wheel.rotation = Vector3(roll, 0.0, 0.0)

func _update_camera(cam_data: Dictionary, player_pos: Vector3, paused: bool) -> void:
	var title_mode := false
	if OS.has_feature("web"):
		var raw_title: Variant = JavaScriptBridge.eval("window.__HARBOR_TITLE_CAMERA ? '1' : '0'", true)
		title_mode = String(raw_title) == "1"

	if title_mode and paused:
		var t := Time.get_ticks_msec() * 0.00018
		var orbit := Vector3(-18.0 + sin(t) * 5.5, 10.5, 22.0 + cos(t * 0.8) * 4.0)
		_camera.fov = 60.0
		_camera.global_position = orbit
		_camera.look_at(Vector3(3.0, 1.6, -8.0), Vector3.UP)
		return

	var pos := _vec3(cam_data.get("position", [player_pos.x, player_pos.y + 2.2, player_pos.z + 4.2]))
	var target := _vec3(cam_data.get("target", [player_pos.x, player_pos.y + 1.5, player_pos.z - 8.0]))
	_camera.fov = clampf(float(cam_data.get("fov", 68.0)), 42.0, 82.0)
	_camera.global_position = pos
	if pos.distance_squared_to(target) > 0.001:
		_camera.look_at(target, Vector3.UP)

func _update_ambient(ambient: Dictionary, dt: float) -> void:
	var blend := 1.0 - exp(-12.0 * dt)
	var seen_peds: Dictionary = {}
	var peds: Array = ambient.get("pedestrians", [])
	for item in peds:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var ped: Dictionary = item
		var pid := int(ped.get("id", 0))
		seen_peds[pid] = true
		var pos := _vec3(ped.get("position", [0.0, 0.0, 0.0]))
		var yaw := float(ped.get("yaw", 0.0))
		var speed := float(ped.get("speed", 0.0))
		var fleeing := bool(ped.get("fleeing", false))

		if not _pedestrians.has(pid):
			_pedestrians[pid] = _spawn_pedestrian(pid, int(ped.get("appearance", 0)), pos, yaw)
		var entry: Dictionary = _pedestrians[pid]
		var root := entry["root"] as Node3D
		_smooth_move(root, pos, yaw, blend, 16.0)
		var phase := fposmod(float(entry["phase"]) + speed * (3.0 if fleeing else 4.8) * dt, TAU)
		entry["phase"] = phase
		var gait := entry["gait"] as RookGaitWeb
		if gait != null:
			gait.update(phase, speed, false, blend, dt, fleeing, fleeing)
	_prune_dict(_pedestrians, seen_peds)

	var seen_cars: Dictionary = {}
	var cars: Array = ambient.get("traffic", [])
	for item in cars:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var car: Dictionary = item
		var cid := int(car.get("id", 0))
		seen_cars[cid] = true
		var pos := _vec3(car.get("position", [0.0, 0.0, 0.0]))
		var yaw := float(car.get("yaw", 0.0))
		var speed := float(car.get("speed", 0.0))

		if not _traffic_cars.has(cid):
			_traffic_cars[cid] = _spawn_traffic_car(cid, int(car.get("appearance", 0)), pos, yaw)
		var entry: Dictionary = _traffic_cars[cid]
		var root := entry["root"] as Node3D
		var turn := angle_difference(root.rotation.y, yaw)
		_smooth_move(root, pos, yaw, blend, 100.0)
		var roll := fposmod(float(entry["roll"]) + speed * dt / WHEEL_RADIUS, TAU)
		entry["roll"] = roll
		var wheels: Array = entry["wheels"]
		for w in wheels:
			(w as Node3D).rotation = Vector3(roll, 0.0, 0.0)
		var steer := Vector3(0.0, -clampf(turn, -0.38, 0.38), 0.0)
		if entry["fl"] != null:
			(entry["fl"] as Node3D).rotation = steer
		if entry["fr"] != null:
			(entry["fr"] as Node3D).rotation = steer
	_prune_dict(_traffic_cars, seen_cars)

func _update_enemies(enemies_list: Array, dt: float) -> void:
	var blend := 1.0 - exp(-16.0 * dt)
	var seen: Dictionary = {}
	for item in enemies_list:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var enemy: Dictionary = item
		var eid := int(enemy.get("id", 0))
		seen[eid] = true
		var pos := _vec3(enemy.get("position", [0.0, 0.0, 0.0]))
		var vel := _vec3(enemy.get("velocity", [0.0, 0.0, 0.0]))
		var yaw := float(enemy.get("yaw", 0.0))
		var team := int(enemy.get("team", 2))
		var alive := bool(enemy.get("alive", true))
		var downed := bool(enemy.get("downed", false))
		var crouched := bool(enemy.get("crouched", false))
		var sprinting := bool(enemy.get("sprinting", false))

		if not _enemies.has(eid):
			_enemies[eid] = _spawn_enemy(eid, team, pos, yaw)
		var entry: Dictionary = _enemies[eid]
		var root := entry["root"] as Node3D
		var model := entry["model"] as Node3D
		_smooth_move(root, pos, yaw, blend, 36.0)

		if not alive or downed:
			var fall_age := minf(0.46, float(entry["fall_age"]) + dt)
			entry["fall_age"] = fall_age
			var fall := fall_age / 0.46
			var arc := sin(fall * PI * 0.5)
			model.position = Vector3(0.0, 0.17 * arc, 0.0)
			model.rotation = Vector3(-PI * 0.5 * arc, PI, 0.16 * fall * float(entry["fall_side"]))
			if entry["beacon"] != null:
				(entry["beacon"] as Node3D).visible = false
			continue

		entry["fall_age"] = 0.0
		var flinch := move_toward(float(entry["flinch"]), 0.0, dt * 4.8)
		entry["flinch"] = flinch
		model.position = Vector3.ZERO
		model.rotation = Vector3(flinch * 0.15, PI, 0.11 * flinch * float(entry["fall_side"]))

		var speed := Vector2(vel.x, vel.z).length()
		var phase := fposmod(float(entry["phase"]) + minf(speed, 9.0) * dt * (2.45 if sprinting else 3.35), TAU)
		entry["phase"] = phase
		var gait := entry["gait"] as RookGaitWeb
		if gait != null:
			gait.update(phase, speed, crouched, blend, dt, sprinting)

		if entry["beacon"] != null:
			(entry["beacon"] as Node3D).visible = fposmod(Time.get_ticks_msec() * 0.001 + eid * 0.11, 1.1) < 0.16
	_prune_dict(_enemies, seen)

func _update_markers(lockdown: Dictionary, jobs: Dictionary) -> void:
	_start_ring.visible = false
	_waypoint_ring.visible = false
	var spin := Time.get_ticks_msec() * 0.0014

	var active_job: Variant = jobs.get("active", null)
	if typeof(active_job) == TYPE_DICTIONARY:
		var wp := _vec3((active_job as Dictionary).get("waypoint", [0.0, 0.0, 0.0]))
		_waypoint_ring.position = wp + Vector3(0.0, 0.08, 0.0)
		_waypoint_ring.rotation.y = spin
		_waypoint_ring.visible = true
		return

	var phase := String(lockdown.get("phase", "FreeRoam"))
	if phase == "Extraction":
		var ex := _vec3(lockdown.get("extractionPosition", [18.5, 0.0, 11.0]))
		_waypoint_ring.position = ex + Vector3(0.0, 0.08, 0.0)
		_waypoint_ring.rotation.y = spin
		_waypoint_ring.visible = true
	elif phase == "FreeRoam":
		var st := _vec3(lockdown.get("startPosition", [8.0, 0.0, -3.0]))
		_start_ring.position = st + Vector3(0.0, 0.08, 0.0)
		_start_ring.rotation.y = -spin
		_start_ring.visible = true

func _handle_events(shots: Array, damages: Array, kills: Array) -> void:
	for item in shots:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var s: Dictionary = item
		var own := bool(s.get("own", false))
		var origin := _vec3(s.get("origin", [0.0, 1.5, 0.0]))
		var end_pt := _vec3(s.get("end", [0.0, 1.5, -10.0]))
		var wid := String(s.get("weaponId", "kestrel_p10"))
		var cue := "wasp" if wid == "wasp_smg" else "halberd" if wid == "halberd_ar" else "breaker" if wid == "breaker_12" else "kestrel"
		_play_cue(cue, -2.5 if own else -6.0)
		_spawn_tracer(origin, end_pt, Color("#fff1a3") if own else Color("#ff7955"))

	for item in damages:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var d: Dictionary = item
		var vid := int(d.get("victimId", -1))
		if _enemies.has(vid):
			_enemies[vid]["flinch"] = 1.0
		if bool(d.get("victimOwn", false)):
			_play_cue("hurt", -6.0)
		elif bool(d.get("attackerOwn", false)):
			_play_cue("hit", -8.0)

	for item in kills:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		if bool((item as Dictionary).get("killerOwn", false)):
			_play_cue("ready", -12.0)

func _spawn_tracer(from_pt: Vector3, to_pt: Vector3, col: Color) -> void:
	var dist := from_pt.distance_to(to_pt)
	if dist < 0.3:
		return
	var dir := (to_pt - from_pt) / dist
	var start_pt := from_pt + dir * minf(0.45, dist * 0.2)
	var length := minf(dist * 0.82, 16.0)

	var box := BoxMesh.new()
	box.size = Vector3(0.012, 0.012, length)
	var mat := StandardMaterial3D.new()
	mat.albedo_color = col
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.emission_enabled = true
	mat.emission = col
	mat.emission_energy_multiplier = 1.35

	var beam := MeshInstance3D.new()
	beam.mesh = box
	beam.material_override = mat
	beam.position = start_pt + dir * (length * 0.5)
	beam.look_at_from_position(beam.position, beam.position + dir, Vector3.UP)
	beam.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_world_root.add_child(beam)

	var flash_light := OmniLight3D.new()
	flash_light.position = start_pt
	flash_light.light_color = col
	flash_light.light_energy = 1.3
	flash_light.omni_range = 3.2
	_world_root.add_child(flash_light)

	_effects.append({"node": beam, "mat": mat, "light": flash_light, "left": 0.09, "dur": 0.09})

func _update_effects(dt: float) -> void:
	for i in range(_effects.size() - 1, -1, -1):
		var fx: Dictionary = _effects[i]
		var left := float(fx["left"]) - dt
		fx["left"] = left
		if left <= 0.0:
			if fx["node"] != null:
				(fx["node"] as Node).queue_free()
			if fx["light"] != null:
				(fx["light"] as Node).queue_free()
			_effects.remove_at(i)
			continue
		var ratio := clampf(left / float(fx["dur"]), 0.0, 1.0)
		var mat := fx["mat"] as StandardMaterial3D
		if mat != null:
			var c := mat.albedo_color
			c.a = ratio
			mat.albedo_color = c
		var light := fx["light"] as OmniLight3D
		if light != null:
			light.light_energy = 1.3 * ratio

func _spawn_pedestrian(pid: int, appearance: int, pos: Vector3, yaw: float) -> Dictionary:
	var root := Node3D.new()
	root.name = "Pedestrian_" + str(pid)
	root.position = pos
	root.rotation = Vector3(0.0, yaw, 0.0)
	_world_root.add_child(root)

	var slot := posmod(appearance, 4)
	var scene: PackedScene = null
	if slot < _ped_scene_slots.size():
		scene = _ped_scene_slots[slot]
	if scene == null:
		for candidate in _ped_scene_slots:
			if candidate != null:
				scene = candidate
				break

	var gait: RookGaitWeb = null
	if scene != null:
		var model := scene.instantiate() as Node3D
		model.rotation = Vector3(0.0, PI, 0.0)
		var scale_h := lerpf(0.96, 1.04, float(posmod(pid * 13, 7)) / 6.0)
		model.scale = Vector3.ONE * scale_h
		root.add_child(model)
		if ((pid - 1) / 4) % 2 == 1:
			if slot == 0:
				_recolor(model, "dockhand_hivis_vermilion", Color("#c7aa50"))
			elif slot == 1:
				_recolor(model, "stevedore_mustard_shirt", Color("#4d7a73"))
			elif slot == 2:
				_recolor(model, "hoodie_cobalt", Color("#755b72"))
			else:
				_recolor(model, "broker_tweed_jacket", Color("#2f4652"))
		gait = RookGaitWeb.create(model)

	return {"root": root, "gait": gait, "phase": fposmod(pid * 1.7, TAU)}

func _spawn_traffic_car(cid: int, appearance: int, pos: Vector3, yaw: float) -> Dictionary:
	var root := Node3D.new()
	root.name = "Traffic_" + str(cid)
	root.position = pos
	root.rotation = Vector3(0.0, yaw, 0.0)
	_world_root.add_child(root)

	var wheels: Array[Node3D] = []
	var fl: Node3D = null
	var fr: Node3D = null
	if _car_scene != null:
		var model := _car_scene.instantiate() as Node3D
		model.rotate_y(PI)
		root.add_child(model)
		_recolor(model, "pip_copper_orange", Color(String(CAR_PAINT[posmod(appearance, CAR_PAINT.size())])))
		fl = model.find_child("WheelFL", true, false) as Node3D
		fr = model.find_child("WheelFR", true, false) as Node3D
		for wname in ["WheelFLRoll", "WheelFRRoll", "WheelRLRoll", "WheelRRRoll"]:
			var w := model.find_child(wname, true, false) as Node3D
			if w != null:
				wheels.append(w)

	return {"root": root, "wheels": wheels, "fl": fl, "fr": fr, "roll": 0.0}

func _spawn_enemy(eid: int, team: int, pos: Vector3, yaw: float) -> Dictionary:
	var root := Node3D.new()
	root.name = "Enemy_" + str(eid)
	root.position = pos
	root.rotation = Vector3(0.0, yaw, 0.0)
	_world_root.add_child(root)

	var scene := _officer_scene if team == 3 else _crew_scene
	var model := Node3D.new()
	var gait: RookGaitWeb = null
	var beacon: MeshInstance3D = null
	if scene != null:
		model = scene.instantiate() as Node3D
		model.rotation = Vector3(0.0, PI, 0.0)
		root.add_child(model)
		gait = RookGaitWeb.create(model)
		var sock := _attach_hand_socket(model)
		_equip_on_socket(sock, "kestrel_p10", true)
		if team == 3:
			var bmesh := BoxMesh.new()
			bmesh.size = Vector3(0.09, 0.04, 0.06)
			var bmat := StandardMaterial3D.new()
			bmat.albedo_color = Color("#48baff")
			bmat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
			bmat.emission_enabled = true
			bmat.emission = Color("#48baff")
			beacon = MeshInstance3D.new()
			beacon.mesh = bmesh
			beacon.material_override = bmat
			beacon.position = Vector3(-0.20, 1.55, 0.055)
			model.add_child(beacon)
	else:
		root.add_child(model)

	return {
		"root": root,
		"model": model,
		"gait": gait,
		"beacon": beacon,
		"phase": fposmod(eid * 1.73, TAU),
		"fall_age": 0.0,
		"flinch": 0.0,
		"fall_side": 1.0 if eid % 2 == 0 else -1.0,
	}

func _attach_hand_socket(model: Node3D) -> Node3D:
	for node in model.find_children("*", "Skeleton3D", true, false):
		var skel := node as Skeleton3D
		if skel == null:
			continue
		for bname in ["hand.R", "mixamorig:RightHand", "RightHand"]:
			var bidx := skel.find_bone(bname)
			if bidx >= 0:
				var attachment := BoneAttachment3D.new()
				attachment.bone_idx = bidx
				skel.add_child(attachment)
				var holder := Node3D.new()
				holder.position = Vector3(0.02, 0.06, 0.04)
				holder.rotation_degrees = Vector3(-85.0, 0.0, 0.0)
				attachment.add_child(holder)
				return holder
	return null

func _equip_on_socket(socket: Node3D, weapon_id: String, vis: bool) -> void:
	if socket == null:
		return
	for child in socket.get_children():
		child.queue_free()
	socket.visible = vis
	if weapon_id != "" and _weapon_scenes.has(weapon_id):
		var wscene := _weapon_scenes[weapon_id] as PackedScene
		if wscene != null:
			var inst := wscene.instantiate() as Node3D
			socket.add_child(inst)

func _recolor(root: Node3D, mat_name: String, col: Color) -> void:
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null:
			continue
		for s in range(mesh.get_surface_override_material_count()):
			var src := mesh.get_active_material(s) as StandardMaterial3D
			if src != null and src.resource_name == mat_name:
				var copy := src.duplicate() as StandardMaterial3D
				copy.albedo_color = col
				mesh.set_surface_override_material(s, copy)

func _smooth_move(root: Node3D, pos: Vector3, yaw: float, blend: float, snap_sq: float) -> void:
	var w := 1.0 if root.position.distance_squared_to(pos) > snap_sq else blend
	root.position = root.position.lerp(pos, w)
	root.rotation = Vector3(0.0, lerp_angle(root.rotation.y, yaw, w), 0.0)

func _prune_dict(store: Dictionary, seen: Dictionary) -> void:
	var dead: Array = []
	for key in store.keys():
		if not seen.has(key):
			dead.append(key)
	for key in dead:
		var entry: Dictionary = store[key]
		if entry.has("root") and entry["root"] != null:
			(entry["root"] as Node).queue_free()
		store.erase(key)

func _clear_dynamic_views() -> void:
	_prune_dict(_pedestrians, {})
	_prune_dict(_traffic_cars, {})
	_prune_dict(_enemies, {})

func _sync_audio_unlock() -> void:
	if _audio_unlocked:
		return
	var unlocked: Variant = JavaScriptBridge.eval("window.__HARBOR_AUDIO_UNLOCKED ? '1' : '0'", true)
	if String(unlocked) != "1":
		return
	_audio_unlocked = true
	if _sounds.has("dock_ambience") and _ambience_player == null:
		_ambience_player = AudioStreamPlayer.new()
		_ambience_player.stream = _sounds["dock_ambience"]
		_ambience_player.volume_db = -5.5
		add_child(_ambience_player)
		_ambience_player.play()

func _play_cue(cue: String, db: float) -> void:
	if not _audio_unlocked or not _sounds.has(cue):
		return
	var player := AudioStreamPlayer.new()
	player.stream = _sounds[cue]
	player.volume_db = db
	player.pitch_scale = _rng.randf_range(0.95, 1.05)
	add_child(player)
	player.finished.connect(player.queue_free)
	player.play()

func _publish_stream_status() -> void:
	var st := _streamer.status()
	var js := "window.__HARBOR_STREAM_STATUS = " + JSON.stringify(st) + ";"
	JavaScriptBridge.eval(js, true)

func _load_scene(path: String) -> PackedScene:
	return load(path) as PackedScene if ResourceLoader.exists(path) else null

func _vec3(raw: Variant) -> Vector3:
	if typeof(raw) == TYPE_ARRAY:
		var arr: Array = raw
		if arr.size() >= 3:
			return Vector3(float(arr[0]), float(arr[1]), float(arr[2]))
	return Vector3.ZERO
