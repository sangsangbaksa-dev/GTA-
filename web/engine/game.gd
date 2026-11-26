extends Node3D

const WORLD_PATH := "res://scenes/world/HarborFront.tscn"
const PEOPLE := [
	"chr_harbor_rook", "chr_npc_civilian", "chr_harbor_dockhand",
	"chr_harbor_crew", "chr_harbor_officer",
]
const CAR_IDS := ["calloway_compact", "pier_runner", "breakwater_utility"]
const CAR_COLORS := [Color("#476d73"), Color("#973f39"), Color("#c4ad85"), Color("#40527d")]
const PAINT_MATERIALS := ["pip_copper_orange", "vehicle_paint"]
const WHEELS := ["FL", "FR", "RL", "RR"]
const STRIDE := {"Walk": 1.27, "Jog": 1.85, "Run": 2.64, "CrouchWalk": 0.94}
const TRACER_LIFETIME := 0.12

var bridge: JavaScriptObject
var low_detail := false
var camera := Camera3D.new()
var actor_root := Node3D.new()
var player: Dictionary
var actor_scenes: Array[PackedScene] = []
var vehicle_scenes: Dictionary = {}
var people: Dictionary = {}
var police: Dictionary = {}
var vehicles: Dictionary = {}
var cargo: Dictionary = {}
var last_tick := -1
var frame: Dictionary = {}
var camera_yaw := 0.0
var camera_arm := 5.5
var trail_material := StandardMaterial3D.new()
var cache_mesh := BoxMesh.new()
var cache_material := StandardMaterial3D.new()
var strap_mesh := BoxMesh.new()
var strap_material := StandardMaterial3D.new()
var contact_mesh := PlaneMesh.new()
var contact_material := ShaderMaterial.new()

func _ready() -> void:
	if OS.has_feature("web"):
		bridge = JavaScriptBridge.get_interface("harborView")
		low_detail = bool(bridge.lowDetail())

	var world_scene := load(WORLD_PATH) as PackedScene
	var world := world_scene.instantiate() as Node3D
	var collisions := world.get_node_or_null("WalkCollision")
	if collisions != null:
		collisions.free()
	add_child(world)
	configure_lighting(world)

	for person in PEOPLE:
		actor_scenes.append(load("res://assets/characters/%s.glb" % person))
	for id in CAR_IDS:
		vehicle_scenes[id] = load("res://assets/vehicles/veh_%s.glb" % id)
	add_child(actor_root)
	player = create_human(0)

	camera.fov = 59
	camera.near = 0.09
	camera.far = 310.0 if low_detail else 660.0
	add_child(camera)
	camera.make_current()

	trail_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	trail_material.albedo_color = Color("#ffd995")
	cache_mesh.size = Vector3(0.70, 0.54, 0.50)
	cache_material.albedo_color = Color("#9b7750")
	cache_material.roughness = 0.87
	strap_mesh.size = Vector3(0.065, 0.575, 0.522)
	strap_material.albedo_color = Color("#f3be78")
	strap_material.roughness = 0.62
	var contact_shader := Shader.new()
	contact_shader.code = """shader_type spatial;
		render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
		void fragment() {
			float edge = length((UV - vec2(0.5)) * 2.0);
			ALBEDO = vec3(0.12, 0.17, 0.20);
			ALPHA = 0.38 * (1.0 - smoothstep(0.13, 1.0, edge));
		}"""
	contact_material.shader = contact_shader

	look_over_harbor(Vector3(0.0, 0.0, 5.0))
	if bridge != null:
		bridge.ready()

func configure_lighting(world: Node3D) -> void:
	var environment_node := world.get_node_or_null("Environment") as WorldEnvironment
	if environment_node:
		var atmosphere := environment_node.environment
		atmosphere.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
		atmosphere.ambient_light_color = Color("#d0dce3")
		atmosphere.ambient_light_energy = 0.60
		atmosphere.fog_enabled = true
		atmosphere.fog_light_color = Color("#9fb9c6")
		atmosphere.fog_density = 0.00255 if low_detail else 0.00195
		atmosphere.fog_sky_affect = 0.30
	var sunlight := world.get_node_or_null("LateAfternoonSun") as DirectionalLight3D
	if sunlight:
		sunlight.light_color = Color("#ffddb6")
		sunlight.light_energy = 1.11
		sunlight.rotation_degrees = Vector3(-35.5, -46.0, 0.0)
		sunlight.directional_shadow_max_distance = 90.0
		sunlight.shadow_enabled = not low_detail
	if low_detail:
		get_viewport().scaling_3d_scale = 0.86
		for instance in world.find_children("*", "GeometryInstance3D", true, false):
			if instance is MultiMeshInstance3D or instance is MeshInstance3D:
				var geometry := instance as GeometryInstance3D
				var extent := geometry.get_aabb().size.length()
				if extent > 0.01 and extent < 40.0:
					geometry.visibility_range_end = 125.0 + extent * 2.0
	else:
		get_viewport().msaa_3d = Viewport.MSAA_2X

func _process(delta: float) -> void:
	if bridge == null:
		return
	var tick := int(bridge.frameVersion())
	if tick != last_tick:
		last_tick = tick
		var decoded = JSON.parse_string(str(bridge.frame()))
		if decoded is Dictionary:
			frame = decoded
			draw_shots(frame.get("shots", []))
	if frame.is_empty():
		return
	var state: Dictionary = frame.state
	update_population(state, delta)
	var mode := str(bridge.screen())
	if mode == "intro":
		look_over_harbor(vec(frame.position))
	elif mode == "play":
		follow_player(state, delta)

func look_over_harbor(point: Vector3) -> void:
	camera.position = point + Vector3(34.0, 12.7, 26.0)
	camera.look_at(point + Vector3(7.0, 2.6, -23.0))

func follow_player(state: Dictionary, dt: float) -> void:
	var driving: bool = state.isDriving
	var look_yaw := float(bridge.yaw())
	var look_pitch := float(bridge.pitch())
	camera_yaw = lerp_angle(camera_yaw, float(state.vehicle.yaw), 1.0 - exp(-5.0 * dt)) if driving else look_yaw
	var player_node: Node3D = player.root
	var focus := player_node.position
	if driving:
		var active: Dictionary = vehicles.get(int(state.playerVehicleId), {})
		focus = active.root.position if not active.is_empty() else vec(state.vehicle.position)
	var height: float = 1.17 if driving else (1.06 if frame.crouched else 1.43)
	var pivot := focus + Vector3.UP * height
	var pitch := -0.24 if driving else look_pitch
	var aiming: bool = frame.aiming
	var radius := 7.0 if driving else (2.65 if aiming else 4.6)
	var shoulder := 0.0 if driving else (0.62 if aiming else 0.48)
	var horizontal := radius * cos(pitch)
	var offset := Vector3(
		sin(camera_yaw) * horizontal + cos(camera_yaw) * shoulder,
		-sin(pitch) * radius + (0.9 if driving else 0.53),
		cos(camera_yaw) * horizontal - sin(camera_yaw) * shoulder
	)
	var direction := offset.normalized()
	var room := float(bridge.cameraDistance(
		pivot.x, pivot.y, pivot.z, direction.x, direction.y, direction.z, offset.length()
	))
	camera_arm = minf(room, lerpf(camera_arm, room, 1.0 - exp(-10.0 * dt)))
	camera.position = pivot + direction * camera_arm
	if driving:
		camera.look_at(pivot + Vector3(-sin(camera_yaw) * 1.65, 0.23, -cos(camera_yaw) * 1.65))
	else:
		var forward := Vector3(-sin(look_yaw) * cos(look_pitch), sin(look_pitch), -cos(look_yaw) * cos(look_pitch))
		camera.look_at(pivot + forward * 23.0)

func create_human(index: int) -> Dictionary:
	var holder := Node3D.new()
	var body := actor_scenes[index].instantiate() as Node3D
	body.rotation.y = PI
	holder.add_child(body)
	add_contact_shadow(holder, Vector2(1.15, 0.75))
	actor_root.add_child(holder)
	var clips := body.find_child("AnimationPlayer", true, false) as AnimationPlayer
	if clips:
		for id in clips.get_animation_list():
			clips.get_animation(id).loop_mode = Animation.LOOP_LINEAR
	return {"root": holder, "body": body, "clips": clips, "gait": ""}

func update_human(view: Dictionary, point: Vector3, yaw: float, speed: float, crouch: bool,
		flee: bool, alive: bool, dt: float) -> void:
	var holder: Node3D = view.root
	move_view(holder, point, yaw, dt)
	var body: Node3D = view.body
	body.rotation.z = lerpf(body.rotation.z, 0.0 if alive else -1.45, 1.0 - exp(-11.0 * dt))
	var animation: AnimationPlayer = view.clips
	if animation == null:
		return
	var gait := "Idle"
	if crouch:
		gait = "CrouchIdle" if speed < 0.19 else "CrouchWalk"
	elif speed >= 0.19:
		gait = "Run" if flee or speed > 4.4 else ("Jog" if speed > 2.4 else "Walk")
	if view.gait != gait:
		animation.play(gait, 0.17 if not view.gait.is_empty() else 0.0)
		view.gait = gait
	var stride: float = STRIDE.get(gait, 0.0)
	animation.speed_scale = clampf(speed * animation.current_animation_length / stride, 0.45, 1.65) if stride > 0 else 1.0

func create_vehicle(state: Dictionary) -> Dictionary:
	var holder := Node3D.new()
	var source: PackedScene = vehicle_scenes.get(state.modelId, vehicle_scenes["calloway_compact"])
	var chassis := source.instantiate() as Node3D
	chassis.rotation.y = PI
	holder.add_child(chassis)
	add_contact_shadow(holder, Vector2(float(state.halfWidth) * 2.85, float(state.halfLength) * 2.6))
	actor_root.add_child(holder)
	var steers: Array[Node3D] = []
	var rollers: Array[Node3D] = []
	for wheel in WHEELS:
		var pivot := chassis.find_child("Wheel" + wheel, true, false) as Node3D
		if pivot and wheel.begins_with("F"):
			steers.append(pivot)
		var roll := chassis.find_child("Wheel" + wheel + "Roll", true, false) as Node3D
		if roll:
			rollers.append(roll)

	var variant := posmod(int(state.appearance), CAR_COLORS.size())
	if int(state.id) != 1 and variant != 0:
		for item in chassis.find_children("*", "MeshInstance3D", true, false):
			var mesh := item as MeshInstance3D
			for surface in range(mesh.get_surface_override_material_count()):
				var material := mesh.get_active_material(surface) as StandardMaterial3D
				if material == null or not PAINT_MATERIALS.has(material.resource_name):
					continue
				var paint := material.duplicate() as StandardMaterial3D
				paint.albedo_color = CAR_COLORS[variant]
				mesh.set_surface_override_material(surface, paint)
	return {"root": holder, "steers": steers, "rollers": rollers, "distance": float(state.distance)}

func update_vehicle(view: Dictionary, car: Dictionary, dt: float) -> void:
	move_view(view.root, vec(car.position), float(car.yaw), dt)
	view.distance = lerpf(float(view.distance), float(car.distance), 1.0 - exp(-21.0 * dt))
	for pivot in view.steers:
		pivot.rotation.y = lerpf(pivot.rotation.y, -float(car.steering), 1.0 - exp(-14.0 * dt))
	for wheel in view.rollers:
		wheel.rotation.x = fmod(float(view.distance) / maxf(0.1, float(car.wheelRadius)), TAU)

func update_population(state: Dictionary, dt: float) -> void:
	var at := vec(frame.position)
	var player_velocity := vec(frame.velocity)
	var speed := Vector2(player_velocity.x, player_velocity.z).length()
	var heading := float(frame.yaw)
	if speed > 0.32 and not frame.aiming:
		heading = atan2(-player_velocity.x, -player_velocity.z)
	player.root.visible = not state.isDriving
	update_human(player, at, heading, speed, bool(frame.crouched), false, bool(frame.alive), dt)

	var current := {}
	for car in state.vehicles:
		var position := vec(car.position)
		if position.distance_squared_to(at) > 175.0 * 175.0:
			continue
		var id := int(car.id)
		current[id] = true
		if not vehicles.has(id):
			vehicles[id] = create_vehicle(car)
			vehicles[id].root.position = position
		update_vehicle(vehicles[id], car, dt)
	retire_missing(vehicles, current)

	current.clear()
	var pedestrian_range := 56.0 if low_detail else 108.0
	for pedestrian in state.ambient.pedestrians:
		var position := vec(pedestrian.position)
		if position.distance_squared_to(at) > pedestrian_range * pedestrian_range:
			continue
		var id := int(pedestrian.id)
		current[id] = true
		if not people.has(id):
			people[id] = create_human(1 + posmod(int(pedestrian.appearance), 3))
			people[id].root.position = position
		update_human(people[id], position, float(pedestrian.yaw), float(pedestrian.speed),
			false, bool(pedestrian.fleeing), true, dt)
	retire_missing(people, current)

	current.clear()
	for enemy in state.enemies:
		var position := vec(enemy.position)
		var id := int(enemy.id)
		current[id] = true
		if not police.has(id):
			police[id] = create_human(4 if int(enemy.team) == 3 else 3)
			police[id].root.position = position
		var flags := str(enemy.flags)
		update_human(police[id], position, float(enemy.yaw), vec(enemy.velocity).length(),
			flags.contains("crouched"), false, not flags.contains("dead"), dt)
	retire_missing(police, current)

	current.clear()
	for cache in state.ambient.get("cargo", []):
		if not cache.available:
			continue
		current[cache.id] = true
		if not cargo.has(cache.id):
			cargo[cache.id] = create_cache(vec(cache.position))
	retire_missing(cargo, current)

func create_cache(point: Vector3) -> Dictionary:
	var holder := Node3D.new()
	holder.position = point
	var box := MeshInstance3D.new()
	box.mesh = cache_mesh
	box.material_override = cache_material
	box.position.y = 0.27
	holder.add_child(box)
	for x in [-0.225, 0.225]:
		var strap := MeshInstance3D.new()
		strap.mesh = strap_mesh
		strap.material_override = strap_material
		strap.position = Vector3(x, 0.285, 0.0)
		holder.add_child(strap)
	actor_root.add_child(holder)
	return {"root": holder}

func add_contact_shadow(parent: Node3D, size: Vector2) -> void:
	if not low_detail:
		return
	var mark := MeshInstance3D.new()
	mark.mesh = contact_mesh
	mark.material_override = contact_material
	mark.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	mark.position = Vector3(0.11, 0.035, 0.07)
	mark.scale = Vector3(size.x, 1.0, size.y)
	parent.add_child(mark)

func draw_shots(shots: Array) -> void:
	for shot in shots:
		var trace := ImmediateMesh.new()
		trace.surface_begin(Mesh.PRIMITIVE_LINES, trail_material)
		trace.surface_add_vertex(vec(shot.origin))
		trace.surface_add_vertex(vec(shot.end))
		trace.surface_end()
		var instance := MeshInstance3D.new()
		instance.mesh = trace
		instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		add_child(instance)
		get_tree().create_timer(TRACER_LIFETIME).timeout.connect(instance.queue_free)

static func retire_missing(views: Dictionary, current: Dictionary) -> void:
	for id in views.keys():
		if not current.has(id):
			views[id].root.queue_free()
			views.erase(id)

static func move_view(node: Node3D, target: Vector3, yaw: float, dt: float) -> void:
	var weight := 1.0 if node.position.distance_squared_to(target) > 36.0 else 1.0 - exp(-19.0 * dt)
	node.position = node.position.lerp(target, weight)
	node.rotation.y = lerp_angle(node.rotation.y, yaw, weight)

static func vec(point: Dictionary) -> Vector3:
	return Vector3(float(point.x), float(point.y), float(point.z))
