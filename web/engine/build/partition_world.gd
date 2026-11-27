extends SceneTree

const Grid := preload("res://world_grid.gd")
const WORLD := "res://scenes/world/"
const CHUNKS := WORLD + "chunks/"

var roots: Dictionary = {}
var batches: Dictionary = {}

func _initialize() -> void:
	var result := build()
	if result != OK:
		printerr("Harbor world partition failed: ", error_string(result))
	quit(int(result))

func build() -> Error:
	var source := load(WORLD + "HarborFront.tscn") as PackedScene
	if source == null:
		return ERR_FILE_CANT_READ
	var original := source.instantiate() as Node3D
	var backdrop := Node3D.new()
	backdrop.name = "HarborBackdrop"
	for node in original.get_children():
		match String(node.name):
			"Environment":
				move_into(node, backdrop)
			"LateAfternoonSun", "Bay":
				move_into(node, backdrop, node.transform)
			"Terrain":
				for surface in node.get_children():
					move_into(surface, backdrop, node.transform * surface.transform)
			"DistrictGeometry":
				for instance in node.get_children():
					partition_batch(instance, backdrop)
			"WalkCollision":
				pass
			_:
				if node is Marker3D:
					continue
				if node.scene_file_path.is_empty() and node.get_class() == "Node3D":
					for item in node.get_children():
						partition_node(item, node.transform * item.transform)
				else:
					partition_node(node, node.transform)
	original.free()
	for root in batches:
		write_batches(root, batches[root])
	batches.clear()
	var result := save_scene(backdrop, WORLD + "HarborBackdrop.scn")
	backdrop.free()
	if result != OK:
		return result
	var index: Array[Array] = []
	var cells := roots.keys()
	cells.sort()
	for cell: Vector2i in cells:
		var root: Node3D = roots[cell]
		var prefix := "cell_%d_%d" % [cell.x, cell.y]
		var temporary := CHUNKS + prefix + ".scn"
		result = save_scene(root, temporary)
		root.free()
		if result != OK:
			return result
		var file := prefix + "." + FileAccess.get_sha256(temporary).left(12) + ".scn"
		result = DirAccess.rename_absolute(temporary, CHUNKS + file)
		if result != OK:
			return result
		index.append([cell.x, cell.y, file])
	var manifest := FileAccess.open(WORLD + "chunk_index.json", FileAccess.WRITE)
	if manifest == null:
		return FileAccess.get_open_error()
	manifest.store_string(JSON.stringify(index))
	print("Generated Harbor backdrop and %d map chunks." % index.size())
	return manifest.get_error()

func chunk_at(cell: Vector2i) -> Node3D:
	if not roots.has(cell):
		var root := Node3D.new()
		root.name = "Chunk_%d_%d" % [cell.x, cell.y]
		roots[cell] = root
	return roots[cell]

func partition_node(node: Node3D, transform: Transform3D) -> void:
	move_into(node, chunk_at(Grid.cell_at(transform.origin)), transform)

func partition_batch(source: MultiMeshInstance3D, backdrop: Node3D) -> void:
	var info := source.multimesh
	var bounds := info.mesh.get_aabb()
	var key := [info.mesh, source.material_override, source.cast_shadow]
	var buffer := info.buffer
	var continuous_road := String(source.name).begins_with("Road") or String(source.name).begins_with("Markings_")
	for i in info.instance_count:
		# The headless renderer only keeps the serialized MultiMesh buffer.
		var row := i * 12
		var axes := Basis(
			Vector3(buffer[row], buffer[row + 4], buffer[row + 8]),
			Vector3(buffer[row + 1], buffer[row + 5], buffer[row + 9]),
			Vector3(buffer[row + 2], buffer[row + 6], buffer[row + 10])
		)
		var origin := Vector3(buffer[row + 3], buffer[row + 7], buffer[row + 11])
		var transform := source.transform * Transform3D(axes, origin)
		var size := (transform * bounds).size
		var cell := Grid.cell_at(transform.origin)
		# Keep the coastline, long roads and tall building silhouettes visible beyond the detail radius.
		var root := backdrop if continuous_road or maxf(size.x, size.z) >= 48.0 or size.y >= 25.0 else chunk_at(cell)
		# Backdrop batches still use local bounds so the renderer can cull them.
		var batch_key := key + [cell] if root == backdrop else key
		if not batches.has(root):
			batches[root] = {}
		var group: Dictionary = batches[root]
		if not group.has(batch_key):
			group[batch_key] = PackedFloat32Array()
		var data: PackedFloat32Array = group[batch_key]
		var basis := transform.basis
		var position := transform.origin
		data.append_array([
			basis.x.x, basis.y.x, basis.z.x, position.x,
			basis.x.y, basis.y.y, basis.z.y, position.y,
			basis.x.z, basis.y.z, basis.z.z, position.z,
		])
		group[batch_key] = data

func write_batches(root: Node3D, groups: Dictionary) -> void:
	for key: Array in groups:
		var data: PackedFloat32Array = groups[key]
		var mm := MultiMesh.new()
		mm.transform_format = MultiMesh.TRANSFORM_3D
		mm.mesh = key[0]
		mm.instance_count = data.size() / 12
		mm.buffer = data
		var node := MultiMeshInstance3D.new()
		node.multimesh = mm
		node.material_override = key[1]
		node.cast_shadow = key[2]
		root.add_child(node, true)
		node.owner = root

static func move_into(node: Node, root: Node3D, transform := Transform3D.IDENTITY) -> void:
	node.owner = null
	node.get_parent().remove_child(node)
	root.add_child(node, true)
	if node is Node3D:
		node.transform = transform
	node.owner = root

static func save_scene(root: Node3D, path: String) -> Error:
	var scene := PackedScene.new()
	var result := scene.pack(root)
	if result != OK:
		return result
	return ResourceSaver.save(scene, path, ResourceSaver.FLAG_COMPRESS)
