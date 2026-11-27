extends RefCounted

# A complete in-game day is 18 minutes of active play.
const DAY_SECONDS := 1080.0
const START_HOUR := 15.5
var hour := START_HOUR
var sunlight: DirectionalLight3D
var atmosphere: Environment
var sky_material: ProceduralSkyMaterial
var low_detail := false
var refresh_left := 0.0
var last_hour := -1.0

func setup(world: Node3D, reduced: bool) -> void:
	low_detail = reduced
	sunlight = world.get_node_or_null("LateAfternoonSun") as DirectionalLight3D
	var env := world.get_node_or_null("Environment") as WorldEnvironment
	if env != null and env.environment != null:
		# The packed resource is shared; duplicate it before changing it.
		env.environment = env.environment.duplicate() as Environment
		atmosphere = env.environment
		if atmosphere.sky != null:
			atmosphere.sky = atmosphere.sky.duplicate() as Sky
			if atmosphere.sky.sky_material is ProceduralSkyMaterial:
				sky_material = atmosphere.sky.sky_material.duplicate() as ProceduralSkyMaterial
				atmosphere.sky.sky_material = sky_material
	apply()

func advance(dt: float) -> void:
	hour = fposmod(hour + clampf(dt, 0.0, 0.25) * 24.0 / DAY_SECONDS, 24.0)
	refresh_left -= dt
	if refresh_left <= 0.0:
		refresh_left = 0.5 if not low_detail else 1.0
		apply()

func reset() -> void:
	hour = START_HOUR
	apply()

func apply() -> void:
	var elevation := sin((hour - 6.0) * TAU / 24.0)
	var day := smoothstep(-0.13, 0.23, elevation)
	var dusk := (1.0 - smoothstep(0.05, 0.40, absf(elevation))) * smoothstep(-0.20, 0.05, elevation)
	var ambient := Color("#344968").lerp(Color("#d0dce3"), day).lerp(Color("#e0ab88"), dusk * 0.45)
	var fog := Color("#26354b").lerp(Color("#9fb9c6"), day).lerp(Color("#bc8273"), dusk * 0.44)
	if sunlight != null:
		sunlight.rotation_degrees = Vector3(-maxf(4.0, elevation * 67.0), hour * 15.0 - 279.0, 0.0)
		sunlight.light_color = Color("#a8c8ff").lerp(Color("#fff0d2"), day).lerp(Color("#f6a370"), dusk * 0.8)
		sunlight.light_energy = lerpf(0.08, 1.11, day)
		sunlight.shadow_enabled = not low_detail and day > 0.10
	if atmosphere != null:
		atmosphere.ambient_light_color = ambient
		atmosphere.ambient_light_energy = lerpf(0.27, 0.60, day)
		atmosphere.fog_light_color = fog
		atmosphere.fog_density = lerpf(0.0031, 0.00255 if low_detail else 0.00195, day)
	# Keep sky rebuilds infrequent: each quantized 20-minute time bucket.
	if sky_material != null and (last_hour < 0.0 or absf(hour - last_hour) >= 1.0 / 3.0):
		last_hour = hour
		sky_material.sky_top_color = Color("#101c33").lerp(Color("#4a80ad"), day)
		sky_material.sky_horizon_color = Color("#344359").lerp(Color("#c9d1cc"), day).lerp(Color("#df966d"), dusk * 0.75)
		sky_material.ground_bottom_color = Color("#111a24").lerp(Color("#263d47"), day)
		sky_material.ground_horizon_color = sky_material.sky_horizon_color.lerp(Color("#34434b"), 0.3)
