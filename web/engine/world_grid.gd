extends RefCounted

const SIZE := 128.0

static func cell_at(point: Vector3) -> Vector2i:
	return Vector2i(floori(point.x / SIZE + 0.5), floori(point.z / SIZE + 0.5))

static func distance_to_cell(point: Vector3, cell: Vector2i) -> float:
	var center := Vector2(cell) * SIZE
	var offset := (Vector2(point.x, point.z) - center).abs() - Vector2.ONE * SIZE * 0.5
	return offset.max(Vector2.ZERO).length()
