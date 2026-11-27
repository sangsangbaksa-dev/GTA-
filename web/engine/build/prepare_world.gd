extends SceneTree

const BACKDROP_SOURCE := "res://scenes/world/HarborFront.tscn"
const BACKDROP_OUT := "res://scenes/world/HarborBackdrop.scn"
const CHUNKS_DIR := "res://scenes/world/chunks"
const CHUNKS_OUT_ENV := "HARBOR_CHUNKS_OUT"

func _init() -> void:
	var out_dir := OS.get_environment(CHUNKS_OUT_ENV)
	if out_dir == "":
		push_error("Missing HARBOR_CHUNKS_OUT environment variable")
		quit(1)
		return

	DirAccess.make_dir_recursive_absolute(out_dir)
	if not _build_backdrop():
		quit(1)
		return
	if not _build_chunks(out_dir):
		quit(1)
		return

	print("Harbor world backdrop + binary chunk scenes prepared.")
	quit(0)

func _build_backdrop() -> bool:
	var source := load(BACKDROP_SOURCE) as PackedScene
	if source == null:
		push_error("Failed to load " + BACKDROP_SOURCE)
		return false

	var instance := source.instantiate()
	var walk_collision := instance.get_node_or_null("WalkCollision")
	if walk_collision != null:
		instance.remove_child(walk_collision)
		walk_collision.free()

	var packed := PackedScene.new()
	var err := packed.pack(instance)
	instance.free()
	if err != OK:
		push_error("Failed to pack HarborBackdrop: " + str(err))
		return false

	err = ResourceSaver.save(packed, BACKDROP_OUT, ResourceSaver.FLAG_COMPRESS)
	if err != OK:
		push_error("Failed to save HarborBackdrop.scn: " + str(err))
		return false
	return true

func _build_chunks(out_dir: String) -> bool:
	var dir := DirAccess.open(CHUNKS_DIR)
	if dir == null:
		push_error("Cannot open " + CHUNKS_DIR)
		return false

	var count := 0
	dir.list_dir_begin()
	while true:
		var name := dir.get_next()
		if name == "":
			break
		if dir.current_is_dir() or not name.ends_with(".tscn") or not name.begins_with("chunk_"):
			continue

		var src_path := CHUNKS_DIR + "/" + name
		var scene := load(src_path) as PackedScene
		if scene == null:
			push_error("Failed to load chunk " + src_path)
			return false

		var inst := scene.instantiate()
		var packed := PackedScene.new()
		var err := packed.pack(inst)
		inst.free()
		if err != OK:
			push_error("Failed to pack " + src_path + ": " + str(err))
			return false

		var base_name := name.substr(0, name.length() - 5)
		var dst_path := out_dir.path_join(base_name + ".scn")
		err = ResourceSaver.save(packed, dst_path, ResourceSaver.FLAG_COMPRESS)
		if err != OK:
			push_error("Failed to save " + dst_path + ": " + str(err))
			return false
		count += 1
	dir.list_dir_end()

	print("Packed " + str(count) + " harbor chunk scenes.")
	return count > 0
