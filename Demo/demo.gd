extends Node3D

@onready var destruction = $DestructibleCube/Destructible
@onready var destroy_button: Button = $DestroyButton

var destructible_cube_scene := preload("res://Demo/destructible_cube.tscn")

func _on_destroy_button_pressed() -> void:
	destruction.Destroy(0, Vector3.ZERO)
	destroy_button.disabled = true
	await get_tree().create_timer(1).timeout
	var new := destructible_cube_scene.instantiate()
	add_child(new)
	destruction = new.get_node("Destructible")
	destroy_button.disabled = false
