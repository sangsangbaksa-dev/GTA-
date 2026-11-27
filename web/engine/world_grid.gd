extends RefCounted

const SIZE := 128.0

static func cell_at(point: Vector3) -> Vector2i:
	return Vector2i(floori(point.x / SIZE + 0.5), floori(point.z / SIZE + 0.5))

static func distance_to_cell(point: Vector3, cell: Vector2i) -> float:
	var center := Vector2(cell) * SIZE
	var offset := (Vector2(point.x, point.z) - center).abs() - Vector2.ONE * SIZE * 0.5
	return offset.max(Vector2.ZERO).length()

static func intersects_view(box: AABB, planes: Array[Plane], margin := 0.0) -> bool:
	var end := box.end
	for plane in planes:
		var normal := plane.normal
		# If even the corner nearest the inside is past a plane, the entire box is outside.
		var nearest := Vector3(
			box.position.x if normal.x >= 0.0 else end.x,
			box.position.y if normal.y >= 0.0 else end.y,
			box.position.z if normal.z >= 0.0 else end.z
		)
		if plane.distance_to(nearest) > margin:
			return false
	return true
