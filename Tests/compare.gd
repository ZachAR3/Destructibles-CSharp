extends SceneTree

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.5, 0.6, 0.7)
	var source := Node3D.new()
	for i in 128:
		var piece := MeshInstance3D.new()
		var piece_mesh := BoxMesh.new()
		piece_mesh.size = Vector3.ONE * 0.25
		piece_mesh.material = material
		piece.mesh = piece_mesh
		piece.position = Vector3(i % 16, i / 16, 0)
		source.add_child(piece)
		piece.owner = source
	var fragments := PackedScene.new()
	fragments.pack(source)
	source.free()
	var script := load("res://addons/DestructiblesCSharp/Destructible.cs")
	for trial in 4:
		var world := Node3D.new()
		root.add_child(world)
		var cube := MeshInstance3D.new()
		cube.mesh = BoxMesh.new()
		var destruction = script.new()
		destruction.set("_fragmented", fragments)
		destruction.set("_preloadShards", false)
		destruction.set("_fadeDelay", 10.0)
		destruction.set("_shrinkDelay", 10.0)
		if "--new" in OS.get_cmdline_user_args():
			destruction.set("ShardsPerFrame", 8)
			destruction.set("FrameBudgetMilliseconds", 2.0)
		cube.add_child(destruction)
		world.add_child(cube)
		await process_frame
		var start := Time.get_ticks_usec()
		var previous := start
		var maximum := 0
		destruction.Destroy(4.0, Vector3.UP)
		while is_instance_valid(cube):
			await process_frame
			var now := Time.get_ticks_usec()
			maximum = maxi(maximum, now - previous)
			previous = now
		var elapsed := Time.get_ticks_usec() - start
		for i in 6:
			await process_frame
			var now := Time.get_ticks_usec()
			maximum = maxi(maximum, now - previous)
			previous = now
		if trial > 0:
			print("COMPARE elapsed_ms=", elapsed / 1000.0, " max_interval_ms=", maximum / 1000.0)
		world.queue_free()
		await process_frame
		await process_frame
		await process_frame
	quit()
