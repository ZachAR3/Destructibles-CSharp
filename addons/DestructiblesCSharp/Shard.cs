namespace Destructibles;

[Tool]
public partial class Shard : RigidBody3D
{
	// Instances variables to be set by the other destruction scripts
	public float ShrinkDelay = -1;
	public float FadeDelay = -1;
	public float ExplosionPower;
	public bool ParticleFade = true;
	public Vector3 ExplosionDirection = Vector3.Zero;

	public override void _Ready()
	{
		// Checks if inside a game or the editor, if in a game runs initialize.
		if (!Engine.IsEditorHint())
			Initialize();
	}


	public async void Initialize()
	{
		// Awaits two physics frame due to this bug https://github.com/godotengine/godot/issues/75934
		await ToSignal(GetTree(), "physics_frame");
		await ToSignal(GetTree(), "physics_frame");

		// If no direction for explosion is given set a random one.
		if (ExplosionDirection == Vector3.Zero)
			ExplosionDirection = RandomDirection();

		// Gets the mesh instance and material for later use
		var meshInstance = GetNode<MeshInstance3D>("MeshInstance");
		var materialSurface = meshInstance.Mesh.SurfaceGetMaterial(0);
		// Duplicates material, so tweens don't affect original object / other instances of it.

		// Returns if no material is found
		if (materialSurface.Duplicate(true) is not StandardMaterial3D material)
		{
			GD.PrintErr("No material found, returning...");
			return;
		}

		// Sets mesh material to be new material
		meshInstance.MaterialOverride = material;

		// Checks if ParticleFade is true, and if so uses particle fade material. Otherwise, uses the standard fade.
		material.Transparency = ParticleFade ? BaseMaterial3D.TransparencyEnum.AlphaHash : BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;


		var tween = CreateTween();
		// Applies explosion force (if it has any) to the shard.
		ApplyImpulse(ExplosionDirection * ExplosionPower, -Position.Normalized());

		// Run fade tween if fade is enabled (checked through fade delay being greater than 0)
		if (FadeDelay > 0)
		{
			tween.TweenProperty(material, "albedo_color", new Color(1, 1, 1, 0), 2).
				SetDelay(FadeDelay).
				SetTrans(Tween.TransitionType.Expo).
				SetEase(Tween.EaseType.Out);
		}
			

		// Run shrink tween if fade is enabled (checked through fade delay being greater than 0)
		if (ShrinkDelay > 0)
		{
			tween.Parallel().TweenProperty(meshInstance, "scale", Vector3.Zero, 2).SetDelay(ShrinkDelay);
		}
		// Wait for the shard to finish its tween (disappear)
		await ToSignal(tween, "finished");
		
		// Removes shard parent along with all other shards
		GetParent().QueueFree();
	}

	
	// Simple function to return a random direction
	static Vector3 RandomDirection() =>
		(new Vector3(GD.Randf(), GD.Randf(), GD.Randf()) - Vector3.One / 2.0f)
			.Normalized() * 2.0f;

}
