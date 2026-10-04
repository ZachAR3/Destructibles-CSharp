using Godot;

namespace Destructibles;

[Tool]
public partial class Shard : RigidBody3D
{
	public float ShrinkDelay = -1;
	public float FadeDelay = -1;
	public float ExplosionPower;
	public bool ParticleFade = true;
	public Vector3 ExplosionDirection;
	public Material ShardMaterial;
	internal bool Prepared;
	private bool _initialized;
	private bool _impulsePending;
	private MeshInstance3D _mesh;
	private ShardVisual _visual;

	public override void _Ready()
	{
		SetProcess(false);
		if (!Engine.IsEditorHint() && !Prepared) Initialize();
	}

	public void Initialize()
	{
		if (_initialized || !IsInsideTree()) return;
		_mesh = GetNodeOrNull<MeshInstance3D>("MeshInstance");
		if (_mesh?.Mesh == null)
		{
			GD.PushWarning("Shard needs a MeshInstance child with a mesh.");
			return;
		}
		_initialized = true;
		if (ShardMaterial != null)
			for (var i = 0; i < _mesh.Mesh.GetSurfaceCount(); i++) _mesh.SetSurfaceOverrideMaterial(i, ShardMaterial);
		if (ExplosionDirection.IsZeroApprox()) ExplosionDirection = DestructibleUtils.RandomDirection();
		_visual = new ShardVisual(_mesh, FadeDelay, ShrinkDelay, ParticleFade);
		_impulsePending = true;
		SetProcess(_visual.Animated);
	}

	public override void _IntegrateForces(PhysicsDirectBodyState3D state)
	{
		if (!_impulsePending) return;
		// Apply after entering the physics space, without per-shard awaits.
		state.ApplyCentralImpulse(ExplosionDirection.Normalized() * ExplosionPower);
		_impulsePending = false;
	}

	public override void _Process(double delta)
	{
		_visual.Update(delta);
		if (_visual.Scale < 1) _mesh.Scale = Vector3.One * Mathf.Max(0.0001f, _visual.Scale);
		if (_visual.Finished) QueueFree();
	}
}
