extends Node3D

signal initial_chunks_ready

const Grid := preload("res://world_grid.gd")
const INDEX := "res://scenes/world/chunk_index.json"
const LOCAL_CHUNKS := "res://scenes/world/chunks/"
const TEMP_CHUNKS := "/tmp/harbor-world/"
const MAX_REQUESTS := 3
const PRELOAD_MARGIN := 24.0
const UNLOAD_MARGIN := 90.0
const RETRY_MS := 2500

var bridge: JavaScriptObject
var download_callback: JavaScriptObject
var files: Dictionary = {}
var bounds: Dictionary = {}
var loaded: Dictionary = {}
var pending: Dictionary = {}
var retry_after: Dictionary = {}
var completed: Array[Array] = []
var wanted: Array[Vector2i] = []
var required: Dictionary = {}
var in_view: Dictionary = {}
var last_camera := Transform3D.IDENTITY
var last_focus := Vector3.INF
var last_viewport_size := Vector2.ZERO
var render_distance := 0.0
var previous_report := ""

func begin(web_bridge: JavaScriptObject, camera: Camera3D, spawn: Vector3) -> void:
	bridge = web_bridge
	if bridge != null:
		download_callback = JavaScriptBridge.create_callback(on_chunk_downloaded)
	var entries: Array = JSON.parse_string(FileAccess.get_file_as_string(INDEX))
	var frustum := camera.get_frustum()
	var start_cell := Grid.cell_at(spawn)
	for entry in entries:
		var cell := Vector2i(int(entry[0]), int(entry[1]))
		files[cell] = String(entry[2])
		var box: Array = entry[3]
		bounds[cell] = AABB(Vector3(box[0], box[1], box[2]), Vector3(box[3], box[4], box[5]))
		if cell == start_cell or (Grid.distance_to_cell(spawn, cell) <= 80.0 and Grid.intersects_view(bounds[cell], frustum)):
			required[cell] = true
	update_interest(camera, spawn)
	if required.is_empty():
		initial_chunks_ready.emit()

func update_interest(camera: Camera3D, focus: Vector3) -> void:
	var view := camera.global_transform
	var viewport_size := camera.get_viewport().get_visible_rect().size
	if render_distance == camera.far and view.is_equal_approx(last_camera) and viewport_size == last_viewport_size 			and focus.distance_squared_to(last_focus) < 0.01:
		return
	last_camera = view
	last_viewport_size = viewport_size
	last_focus = focus
	render_distance = camera.far
	var frustum := camera.get_frustum()
	wanted.clear()
	in_view.clear()
	var priorities: Dictionary = {}
	for cell: Vector2i in files:
		var box: AABB = bounds[cell]
		var distance := Grid.distance_to_cell(focus, cell)
		var visible := Grid.intersects_view(box, frustum)
		if visible:
			in_view[cell] = true
		if required.has(cell) or visible or distance <= 28.0 or Grid.intersects_view(box, frustum, PRELOAD_MARGIN):
			wanted.append(cell)
			priorities[cell] = distance + (-2000.0 if required.has(cell) else (-1000.0 if visible else 0.0))
		if not loaded.has(cell):
			continue
		var chunk: Node3D = loaded[cell]
		if not visible and not required.has(cell) and distance > render_distance + UNLOAD_MARGIN:
			chunk.queue_free()
			loaded.erase(cell)
		else:
			chunk.visible = visible
	wanted.sort_custom(func(a: Vector2i, b: Vector2i) -> bool: return priorities[a] < priorities[b])
	report_progress()

func _process(_delta: float) -> void:
	if not completed.is_empty():
		var entry: Array = completed.pop_front()
		mount_chunk(entry[0], entry[1])
	var now := Time.get_ticks_msec()
	for cell in wanted:
		if pending.size() >= MAX_REQUESTS:
			break
		var file: String = files[cell]
		if loaded.has(cell) or pending.has(file) or now < int(retry_after.get(cell, 0)):
			continue
		pending[file] = cell
		if bridge != null:
			bridge.requestChunk(file, download_callback)
		else:
			completed.append([file, true])

func on_chunk_downloaded(args: Array) -> void:
	completed.append([String(args[0]), bool(args[1])])

func mount_chunk(file: String, transferred: bool) -> void:
	var cell: Vector2i = pending[file]
	pending.erase(file)
	var path := (TEMP_CHUNKS if bridge != null else LOCAL_CHUNKS) + file
	var packed: PackedScene
	if transferred:
		if wanted.has(cell):
			packed = ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
		if bridge != null:
			DirAccess.remove_absolute(path)
	if packed == null:
		if wanted.has(cell):
			retry_after[cell] = Time.get_ticks_msec() + RETRY_MS
		return
	retry_after.erase(cell)
	var chunk := packed.instantiate() as Node3D
	chunk.visible = in_view.has(cell)
	add_child(chunk)
	loaded[cell] = chunk
	if required.erase(cell) and required.is_empty():
		initial_chunks_ready.emit()
	report_progress()

func report_progress() -> void:
	if bridge == null:
		return
	var active := PackedStringArray()
	for cell: Vector2i in in_view:
		if loaded.has(cell):
			active.append("%d,%d" % [cell.x, cell.y])
	var signature := " ".join(active)
	var report := "%d/%d/%s" % [loaded.size(), int(render_distance), signature]
	if report != previous_report:
		previous_report = report
		bridge.worldChunks(loaded.size(), active.size(), files.size(), render_distance, signature)
