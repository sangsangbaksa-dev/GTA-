extends Node3D
class_name WorldStreamerWeb

const CACHE_DIR := "user://harbor_chunks"
const MAX_CONCURRENT_REQUESTS := 4

var _chunk_size := 64.0
var _load_radius := 2
var _unload_padding := 1
var _lookahead_seconds := 1.5

var _base_url := ""
var _manifest_ready := false
var _manifest_failed := false
var _chunk_urls: Dictionary = {}
var _loaded_nodes: Dictionary = {}
var _pending_keys: Dictionary = {}
var _desired_keys: Array[String] = []
var _active_downloads := 0

func _ready() -> void:
	DirAccess.make_dir_recursive_absolute(CACHE_DIR)
	_base_url = _resolve_asset_root()
	_fetch_manifest()

func update_focus(pos: Vector3, vel: Vector3, streaming_cfg: Dictionary = {}) -> void:
	if streaming_cfg.has("chunkSizeMeters"):
		_chunk_size = maxf(16.0, float(streaming_cfg["chunkSizeMeters"]))
	if streaming_cfg.has("loadRadiusChunks"):
		_load_radius = maxi(1, int(streaming_cfg["loadRadiusChunks"]))
	if streaming_cfg.has("unloadPaddingChunks"):
		_unload_padding = maxi(0, int(streaming_cfg["unloadPaddingChunks"]))
	if streaming_cfg.has("lookaheadSeconds"):
		_lookahead_seconds = maxf(0.0, float(streaming_cfg["lookaheadSeconds"]))

	if not _manifest_ready:
		return

	var player_coord := _coord_of(pos.x, pos.z)
	var ahead := pos + vel * _lookahead_seconds
	var focus_coord := _coord_of(ahead.x, ahead.z)
	var keep_dist := _load_radius + _unload_padding

	var wanted_map: Dictionary = {}
	_add_disk(wanted_map, player_coord, _load_radius)
	_add_disk(wanted_map, focus_coord, _load_radius)

	for existing_key in _loaded_nodes.keys():
		var parts := String(existing_key).split("_")
		if parts.size() == 2:
			var coord := Vector2i(int(parts[0]), int(parts[1]))
			if _chebyshev(coord, player_coord) <= keep_dist or _chebyshev(coord, focus_coord) <= keep_dist:
				wanted_map[existing_key] = coord

	var to_unload: Array[String] = []
	for existing_key in _loaded_nodes.keys():
		if not wanted_map.has(existing_key):
			to_unload.append(String(existing_key))
	for key in to_unload:
		var node := _loaded_nodes[key] as Node
		if node != null:
			node.queue_free()
		_loaded_nodes.erase(key)

	var candidates: Array = []
	for key in wanted_map.keys():
		if _chunk_urls.has(key) and not _loaded_nodes.has(key) and not _pending_keys.has(key):
			var coord: Vector2i = wanted_map[key]
			var dist := mini(_chebyshev(coord, player_coord), _chebyshev(coord, focus_coord))
			candidates.append({"key": String(key), "dist": dist})

	candidates.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return int(a["dist"]) < int(b["dist"]))
	_desired_keys.clear()
	for entry in candidates:
		_desired_keys.append(String(entry["key"]))

	_pump_downloads()

func status() -> Dictionary:
	return {
		"manifestReady": _manifest_ready,
		"manifestFailed": _manifest_failed,
		"loadedCount": _loaded_nodes.size(),
		"pendingCount": _pending_keys.size() + _desired_keys.size(),
		"totalAvailable": _chunk_urls.size(),
	}

func _resolve_asset_root() -> String:
	if OS.has_feature("web"):
		var raw: Variant = JavaScriptBridge.eval("window.__HARBOR_ASSET_ROOT || window.location.origin + '/'", true)
		if typeof(raw) == TYPE_STRING and String(raw) != "":
			var root := String(raw)
			return root if root.ends_with("/") else root + "/"
	return "http://127.0.0.1:4173/"

func _fetch_manifest() -> void:
	var http := HTTPRequest.new()
	add_child(http)
	var url := _base_url + "godot/chunks/chunk_index.json"
	http.request_completed.connect(func(result: int, code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
		http.queue_free()
		if result != HTTPRequest.RESULT_SUCCESS or code < 200 or code >= 300:
			_manifest_failed = true
			push_warning("Chunk manifest fetch failed: HTTP " + str(code))
			return
		var parsed: Variant = JSON.parse_string(body.get_string_from_utf8())
		if typeof(parsed) != TYPE_DICTIONARY:
			_manifest_failed = true
			return
		var manifest: Dictionary = parsed
		if manifest.has("chunkSizeMeters"):
			_chunk_size = float(manifest["chunkSizeMeters"])
		if manifest.has("loadRadiusChunks"):
			_load_radius = int(manifest["loadRadiusChunks"])
		if manifest.has("unloadPaddingChunks"):
			_unload_padding = int(manifest["unloadPaddingChunks"])
		if manifest.has("lookaheadSeconds"):
			_lookahead_seconds = float(manifest["lookaheadSeconds"])
		var chunks: Array = manifest.get("chunks", [])
		for item in chunks:
			if typeof(item) == TYPE_DICTIONARY:
				var cx := int(item.get("x", 0))
				var cz := int(item.get("z", 0))
				var file_rel := String(item.get("file", ""))
				if file_rel != "":
					_chunk_urls[_key(cx, cz)] = _base_url + "godot/chunks/" + file_rel
		_manifest_ready = true
	)
	var err := http.request(url)
	if err != OK:
		_manifest_failed = true

func _pump_downloads() -> void:
	while _active_downloads < MAX_CONCURRENT_REQUESTS and not _desired_keys.is_empty():
		var key: String = _desired_keys.pop_front()
		if _loaded_nodes.has(key) or _pending_keys.has(key) or not _chunk_urls.has(key):
			continue
		_start_chunk_download(key, String(_chunk_urls[key]))

func _start_chunk_download(key: String, url: String) -> void:
	_pending_keys[key] = true
	_active_downloads += 1
	var local_path := CACHE_DIR + "/chunk_" + key.replace("-", "m") + ".scn"
	var http := HTTPRequest.new()
	http.download_file = local_path
	add_child(http)
	http.request_completed.connect(func(result: int, code: int, _headers: PackedStringArray, _body: PackedByteArray) -> void:
		http.queue_free()
		_pending_keys.erase(key)
		_active_downloads = maxi(0, _active_downloads - 1)
		if result == HTTPRequest.RESULT_SUCCESS and code >= 200 and code < 300:
			var packed := ResourceLoader.load(local_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
			if packed != null:
				var instance := packed.instantiate()
				instance.name = "Chunk_" + key
				add_child(instance)
				_loaded_nodes[key] = instance
		_pump_downloads()
	)
	var err := http.request(url)
	if err != OK:
		http.queue_free()
		_pending_keys.erase(key)
		_active_downloads = maxi(0, _active_downloads - 1)

func _coord_of(x: float, z: float) -> Vector2i:
	return Vector2i(floori(x / _chunk_size), floori(z / _chunk_size))

func _chebyshev(a: Vector2i, b: Vector2i) -> int:
	return maxi(absi(a.x - b.x), absi(a.y - b.y))

func _add_disk(into: Dictionary, center: Vector2i, radius: int) -> void:
	for x in range(center.x - radius, center.x + radius + 1):
		for z in range(center.y - radius, center.y + radius + 1):
			into[_key(x, z)] = Vector2i(x, z)

func _key(x: int, z: int) -> String:
	return str(x) + "_" + str(z)
