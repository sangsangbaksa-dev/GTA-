extends Node3D

signal initial_chunks_ready

const Grid := preload("res://world_grid.gd")
const INDEX := "res://scenes/world/chunk_index.json"
const LOCAL_CHUNKS := "res://scenes/world/chunks/"
const TEMP_CHUNKS := "/tmp/harbor-world/"
const MAX_REQUESTS := 3
const UNLOAD_MARGIN := 90.0
const RETRY_MS := 2500

var bridge: JavaScriptObject
var download_callback: JavaScriptObject
var radius := 300.0
var files: Dictionary = {}
var loaded: Dictionary = {}
var pending: Dictionary = {}
var retry_after: Dictionary = {}
var completed: Array[Array] = []
var wanted: Array[Vector2i] = []
var required: Dictionary = {}
var last_focus := Vector3.INF
var last_ahead := Vector3.INF

func begin(web_bridge: JavaScriptObject, low_detail: bool, spawn: Vector3) -> void:
	bridge = web_bridge
	if bridge != null:
		download_callback = JavaScriptBridge.create_callback(on_chunk_downloaded)
	if low_detail:
		radius = 170.0
	var entries: Array = JSON.parse_string(FileAccess.get_file_as_string(INDEX))
	var start_cell := Grid.cell_at(spawn)
	for entry in entries:
		var cell := Vector2i(int(entry[0]), int(entry[1]))
		files[cell] = String(entry[2])
		var offset := (cell - start_cell).abs()
		if offset.x <= 1 and offset.y <= 1:
			required[cell] = true
	update_interest(spawn, Vector3.ZERO)
	report_progress()

func update_interest(focus: Vector3, velocity: Vector3) -> void:
	var ahead := focus + (velocity * 1.4).limit_length(52.0)
	if focus.distance_squared_to(last_focus) < 16.0 ** 2 and ahead.distance_squared_to(last_ahead) < 24.0 ** 2:
		return
	last_focus = focus
	last_ahead = ahead
	wanted.clear()
	var priorities: Dictionary = {}
	for cell: Vector2i in files:
		var distance := Grid.distance_to_cell(focus, cell)
		var lead := Grid.distance_to_cell(ahead, cell)
		if required.has(cell) or minf(distance, lead) <= radius:
			wanted.append(cell)
			priorities[cell] = distance + 0.5 * lead - (1000.0 if required.has(cell) else 0.0)
		elif loaded.has(cell) and distance > radius + UNLOAD_MARGIN:
			loaded[cell].queue_free()
			loaded.erase(cell)
			report_progress()
	wanted.sort_custom(func(a: Vector2i, b: Vector2i) -> bool: return priorities[a] < priorities[b])

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
	if not wanted.has(cell):
		if transferred and bridge != null:
			DirAccess.remove_absolute(path)
		return
	var packed: PackedScene = null
	if transferred:
		packed = ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
		if bridge != null:
			DirAccess.remove_absolute(path)
	if packed == null:
		retry_after[cell] = Time.get_ticks_msec() + RETRY_MS
		return
	retry_after.erase(cell)
	var chunk := packed.instantiate() as Node3D
	add_child(chunk)
	loaded[cell] = chunk
	if required.erase(cell) and required.is_empty():
		initial_chunks_ready.emit()
	report_progress()

func report_progress() -> void:
	if bridge != null:
		bridge.worldChunks(loaded.size(), files.size())
