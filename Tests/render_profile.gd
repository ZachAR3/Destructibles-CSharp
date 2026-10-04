extends SceneTree

# Run with a real renderer, e.g. --rendering-method forward_plus.
# Frozen, uniquely meshed shards keep camera/physics identical between cases.
func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	if DisplayServer.get_name() == "headless":
		push_error("Render profiling needs a display and GPU; remove --headless.")
		quit(1)
		return
	Engine.max_fps = 0
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	DisplayServer.window_set_size(Vector2i(1920, 1080))
	var count := 512
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--shards="):
			count = clampi(argument.trim_prefix("--shards=").to_int(), 128, 2048)
	var world := Node3D.new()
	root.add_child(world)
	var environment := WorldEnvironment.new()
	environment.environment = Environment.new()
	environment.environment.background_mode = Environment.BG_COLOR
	environment.environment.background_color = Color(0.08, 0.1, 0.13)
	environment.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.environment.ambient_light_color = Color(0.6, 0.65, 0.7)
	world.add_child(environment)
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-35, -20, 0)
	light.shadow_enabled = true
	world.add_child(light)
	var camera := Camera3D.new()
	camera.position = Vector3(0, 0, 17)
	world.add_child(camera)
	camera.look_at(Vector3.ZERO)
	var floor_mesh := MeshInstance3D.new()
	var floor_box := BoxMesh.new()
	floor_box.size = Vector3(28, 17, 0.25)
	floor_mesh.mesh = floor_box
	floor_mesh.position.z = -4
	world.add_child(floor_mesh)
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.3, 0.55, 0.75)
	var high := SphereMesh.new()
	high.radius = 0.72
	high.height = 1.44
	high.radial_segments = 64
	high.rings = 32
	if "--dense" in OS.get_cmdline_user_args():
		high.radial_segments = 128
		high.rings = 64
	var low := SphereMesh.new()
	low.radius = high.radius
	low.height = high.height
	low.radial_segments = 16
	low.rings = 8
	var hull := BoxMesh.new().create_convex_shape()
	var template := Node3D.new()
	for i in count:
		var body := Node3D.new()
		body.position = Vector3((i % 32 - 15.5) * 0.85, ((i / 32) % 16 - 7.5) * 0.85, -(i / 512) * 1.5)
		template.add_child(body)
		body.owner = template
		var mesh := MeshInstance3D.new()
		mesh.mesh = unique_mesh(high, material)
		body.add_child(mesh)
		mesh.owner = template
		var collision := CollisionShape3D.new()
		collision.shape = hull
		body.add_child(collision)
		collision.owner = template
	var baked := PackedScene.new()
	baked.pack(template)
	template.free()
	var object := MeshInstance3D.new()
	object.name = "Object"
	object.mesh = BoxMesh.new()
	var destruction = load("res://addons/DestructiblesCSharp/Destructible.cs").new()
	destruction.set("_preGeneratedShards", baked)
	destruction.set("_collisionLayers", 0)
	destruction.set("_layerMasks", 0)
	destruction.set("_fadeDelay", -1)
	destruction.set("_shrinkDelay", -1)
	destruction.set("ShardsPerFrame", 8)
	object.add_child(destruction)
	world.add_child(object)
	var started := Time.get_ticks_usec()
	destruction.Destroy(0.0, Vector3.UP)
	await destruction.Fractured
	var preparation_ms := (Time.get_ticks_usec() - started) / 1000.0
	var meshes: Array[MeshInstance3D] = []
	var group := world.get_node("ObjectShards")
	for i in group.get_child_count():
		var body = group.get_child(i)
		body.freeze = true
		body.position = Vector3((i % 32 - 15.5) * 0.85, ((i / 32) % 16 - 7.5) * 0.85, -(i / 512) * 1.5)
		meshes.append(body.get_node("MeshInstance"))
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	print("RENDER_DEVICE ", RenderingServer.get_video_adapter_name(), " method=", RenderingServer.get_current_rendering_method(), " shards=", count, " resolution=1920x1080 preparation_ms=", preparation_ms)
	for case in ["opaque_shadows", "opaque_no_shadows", "hash_fade_shadows", "alpha_fade_shadows", "low_detail_no_shadows"]:
		for mesh in meshes:
			mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF if case.ends_with("no_shadows") else GeometryInstance3D.SHADOW_CASTING_SETTING_ON
			var surface := material.duplicate() as StandardMaterial3D
			if case.begins_with("hash"):
				surface.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_HASH
				surface.albedo_color.a = 0.5
			elif case.begins_with("alpha"):
				surface.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
				surface.albedo_color.a = 0.5
			mesh.material_override = surface if "fade" in case else material
			if case.begins_with("low_detail"):
				mesh.mesh = unique_mesh(low, material)
		for frame in 90:
			await process_frame
		var gpu: Array[float] = []
		var cpu: Array[float] = []
		var intervals: Array[float] = []
		var previous := Time.get_ticks_usec()
		for frame in 180:
			await process_frame
			var now := Time.get_ticks_usec()
			intervals.append((now - previous) / 1000.0)
			previous = now
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
			cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()) + RenderingServer.get_frame_setup_time_cpu())
		print("RENDER_PROFILE ", JSON.stringify({"case": case, "gpu_ms": stats(gpu), "render_cpu_ms": stats(cpu), "frame_interval_ms": stats(intervals), "draw_calls": root.get_render_info(Viewport.RENDER_INFO_TYPE_VISIBLE, Viewport.RENDER_INFO_DRAW_CALLS_IN_FRAME), "shadow_draw_calls": root.get_render_info(Viewport.RENDER_INFO_TYPE_SHADOW, Viewport.RENDER_INFO_DRAW_CALLS_IN_FRAME), "visible_triangles": root.get_render_info(Viewport.RENDER_INFO_TYPE_VISIBLE, Viewport.RENDER_INFO_PRIMITIVES_IN_FRAME)}))
	world.queue_free()
	await process_frame
	await process_frame
	quit()

func unique_mesh(source: Mesh, material: Material) -> ArrayMesh:
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, source.surface_get_arrays(0))
	mesh.surface_set_material(0, material)
	return mesh

func stats(samples: Array[float]) -> Dictionary:
	samples.sort()
	return {"median": samples[samples.size() / 2], "p95": samples[int(samples.size() * 0.95)]}
