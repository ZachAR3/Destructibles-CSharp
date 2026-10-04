extends Node3D

const OBJECT_SCENE := preload("res://Demo/fracture_object.tscn")
const OPTIONS_SCRIPT := preload("res://addons/DestructiblesCSharp/FractureOptions.cs")
var object: MeshInstance3D
var destruction: Node

func _ready() -> void:
	$Camera3D.look_at(Vector3(0, 1, 0))
	for pattern in ["Random", "Slices", "Radial", "Grid"]:
		$UI/Options/Pattern.add_item(pattern)
	for noise in ["No noise", "Simplex", "Simplex Smooth", "Cellular", "Perlin", "Value Cubic", "Value"]:
		$UI/Options/Noise.add_item(noise)
	reset()

func reset() -> void:
	for child in $Objects.get_children():
		child.queue_free()
	for child in $Debris.get_children():
		child.queue_free()
	object = OBJECT_SCENE.instantiate()
	destruction = object.get_node("Destructible")
	if $UI/Controls/Hollow.button_pressed:
		var torus := TorusMesh.new()
		torus.inner_radius = 0.7
		torus.outer_radius = 1.3
		torus.rings = 32
		torus.ring_segments = 24
		torus.material = object.mesh.surface_get_material(0)
		object.mesh = torus
		object.get_node("StaticBody3D/CollisionShape3D").shape = torus.create_trimesh_shape()
		destruction.set("DecomposeCollision", true)
	var options := OPTIONS_SCRIPT.new()
	options.set("Pattern", $UI/Options/Pattern.selected)
	options.set("Axis", Vector3.UP)
	options.set("SurfaceRoughness", $UI/Options/Roughness.value)
	if $UI/Options/Noise.selected > 0:
		var noise := FastNoiseLite.new()
		noise.noise_type = $UI/Options/Noise.selected - 1
		noise.frequency = 1.4
		noise.seed = int($UI/Options/Seed.value)
		options.set("Noise", noise)
	destruction.set("FractureOptions", options)
	destruction.set("FracturePieces", int($UI/Options/Pieces.value))
	destruction.set("FractureSeed", int($UI/Options/Seed.value))
	destruction.set("_shardContainer", $Debris)
	destruction.connect("Fractured", _on_fractured)
	destruction.connect("FractureFailed", _on_failed)
	$Objects.add_child(object)
	set_busy(false)
	$UI/Status.text = "Cut keeps the larger piece. Burst fractures the whole mesh."

func burst() -> void:
	set_busy(true)
	destruction.Destroy(4.0, Vector3.ZERO)

func cut() -> void:
	set_busy(true)
	var bounds := object.mesh.get_aabb()
	var normal := Vector3.UP if $UI/Controls/Hollow.button_pressed else Vector3.RIGHT
	var point := bounds.get_center() + normal * bounds.size.dot(normal) * 0.1
	destruction.Hit(object.to_global(point), normal, 4.0, 1.0)

func _on_fractured(count: int, partial: bool) -> void:
	set_busy(false)
	$UI/Controls/Burst.disabled = not partial
	$UI/Controls/Cut.disabled = not partial
	$UI/Status.text = "%d fragment(s) released%s" % [count, "; cut again or reset." if partial else "; reset to try again."]

func _on_failed(reason: String) -> void:
	set_busy(false)
	$UI/Status.text = reason

func set_busy(busy: bool) -> void:
	$UI/Controls/Burst.disabled = busy
	$UI/Controls/Cut.disabled = busy
	$UI/Controls/Reset.disabled = busy
	$UI/Controls/Hollow.disabled = busy
	$UI/Options/Pattern.disabled = busy
	$UI/Options/Noise.disabled = busy
	for spin in [$UI/Options/Pieces, $UI/Options/Seed, $UI/Options/Roughness]:
		spin.editable = not busy

func change_shape(_enabled: bool) -> void:
	reset()

func change_options(_value: Variant) -> void:
	reset()
