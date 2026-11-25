extends SceneTree
func _merge(n: Node, xf: Transform3D, acc: Array) -> void:
	var t := xf
	if n is Node3D: t = xf * (n as Node3D).transform
	if n is MeshInstance3D:
		var a: AABB = t * (n as MeshInstance3D).get_aabb()
		acc[0] = a if acc[0] == null else (acc[0] as AABB).merge(a)
		if String(n.name).ends_with("-col") or String(n.name).ends_with("_col"): acc[1] = true
	for c in n.get_children(): _merge(c, t, acc)
func _init():
	var out := {}
	for dir in ["buildings","props","characters","weapons"]:
		for f in DirAccess.get_files_at("res://assets/"+dir):
			if not f.ends_with(".glb"): continue
			var s = load("res://assets/%s/%s" % [dir,f]).instantiate()
			var acc = [null, false]
			_merge(s, Transform3D.IDENTITY, acc)
			var a: AABB = acc[0]
			var names := []
			for c in s.get_children(): names.append("%s:%s" % [c.name, c.get_class()])
			out[f.get_basename()] = {"min":[snappedf(a.position.x,0.01),snappedf(a.position.y,0.01),snappedf(a.position.z,0.01)],"size":[snappedf(a.size.x,0.01),snappedf(a.size.y,0.01),snappedf(a.size.z,0.01)],"children":names}
			s.free()
	print(JSON.stringify(out))
	quit()
