using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Destructibles;

[Tool]
public partial class Destructible : Node
{
	// Keep the original export names so existing scenes still load.
	[Export] private PackedScene _fragmented;
	[Export] private Node _shardContainer;
	[ExportGroup("Animation")]
	[Export] private float _fadeDelay = 2;
	[Export] private float _shrinkDelay = 2;
	[Export] private bool _particleFade = true;
	[ExportGroup("Collision")]
	[Export(PropertyHint.Layers3DPhysics)] private uint _collisionLayers = 1;
	[Export(PropertyHint.Layers3DPhysics)] private uint _layerMasks = 1;
	[Export] public bool DecomposeCollision { get; set; }
	[Export] public MeshConvexDecompositionSettings DecompositionSettings { get; set; }
	[ExportGroup("Generation")]
	[Export] public bool GenerateShards
	{
		get => false;
		set { if (value && Engine.IsEditorHint()) Observe(BakeAsync()); }
	}
	[Export] private Material _fragmentedMaterial;
	[Export] private PackedScene _preGeneratedShards;
	[Export] private bool _preloadShards = true;
	[Export(PropertyHint.Dir)] private string _savePath = "res://";
	[Export] private bool _cleanCollisionMesh = true;
	[Export] private bool _simplifyCollisionMesh;
	[Export(PropertyHint.Range, "0.001,100,0.001,or_greater")] private float _shardMass = 1;
	[Export] private float _linearDampening;
	[Export] private RigidBody3D.DampMode _linearDampMode = RigidBody3D.DampMode.Combine;
	[Export] private float _angularDampening;
	[Export] private RigidBody3D.DampMode _angularDampMode = RigidBody3D.DampMode.Combine;
	[ExportGroup("Performance")]
	[Export(PropertyHint.Range, "1,64,1")] public int ShardsPerFrame { get; set; } = 4;
	[Export(PropertyHint.Range, "0.1,16,0.1")] public float FrameBudgetMilliseconds { get; set; } = 2;
	[Export] public bool ShardShadows { get; set; } = true;
	[ExportGroup("Mesh fracture")]
	[Export] public MeshInstance3D SourceMesh { get; set; }
	[Export] public CollisionShape3D SourceCollision { get; set; }
	[Export] public Material InteriorMaterial { get; set; }
	[Export(PropertyHint.Range, "2,256,1")] public int FracturePieces { get; set; } = 8;
	[Export] public int FractureSeed { get; set; } = 1;
	[Export] public FractureOptions FractureOptions { get; set; }
	[Export(PropertyHint.Range, "0.001,100,0.1,or_greater")] public float MinimumForce { get; set; } = 1;
	[Export] public bool KeepRemainder { get; set; } = true;
	[ExportGroup("Source preparation")]
	[Export] public bool CaptureCurrentPose { get; set; } = true;
	[Export] public bool RepairNonManifold { get; set; } = true;
	[Export] public bool ForceSolidRemesh { get; set; }
	[Export(PropertyHint.Range, "16,96,1")] public int RepairResolution { get; set; } = 64;
	[Export(PropertyHint.Range, "0,10,0.001,or_greater")] public float RepairShellThickness { get; set; }
	[Signal] public delegate void FracturedEventHandler(int shardCount, bool partial);
	[Signal] public delegate void FractureFailedEventHandler(string reason);

	private CancellationTokenSource _lifetime = new();
	private Task<List<MeshPart>> _preparation;
	private PackedScene _cachedScene;
	private bool _busy;
	private bool _destroyed;
	private readonly List<CollisionShape3D> _remainderCollisions = new();
	private MeshInstance3D _mergedRemainder;
	public bool IsBusy => _busy;

	public override void _EnterTree()
	{

		if (!_lifetime.IsCancellationRequested) return;
		_lifetime = new CancellationTokenSource();
		_preparation = null;
		_cachedScene = null;
	}

	public override void _Ready()
	{
		if (CaptureCurrentPose) foreach (var source in FindSources()) MeshPose.Watch(source);
		if (Engine.IsEditorHint()) UpdateConfigurationWarnings();
		if (!Engine.IsEditorHint() && _preloadShards && (_preGeneratedShards ?? _fragmented) != null)
			Observe(PrepareSceneAsync(_lifetime.Token));
	}

	public override void _ExitTree()
	{
		RequestReady();
		_lifetime.Cancel();
		_lifetime.Dispose();
	}

	public void Destroy(float explosionPower = 4, Vector3 explosionDirection = default) =>
		Observe(DestroyAsync(explosionPower, explosionDirection));

	public Task<bool> DestroyAsync(float explosionPower = 4, Vector3 explosionDirection = default) =>
		BreakAsync(explosionPower, explosionDirection, null, 0);

	public void Hit(Vector3 worldPosition, Vector3 worldDirection, float force, float sharpness = 0) =>
		Observe(HitAsync(worldPosition, worldDirection, force, sharpness));

	public Task<bool> HitAsync(Vector3 worldPosition, Vector3 worldDirection, float force, float sharpness = 0)
	{
		if (!worldPosition.IsFinite() || !worldDirection.IsFinite() || !float.IsFinite(force) || !float.IsFinite(sharpness))
			throw new ArgumentException("Hit position, direction, force and sharpness must be finite.");
		if (force < MinimumForce || worldDirection.IsZeroApprox()) return Task.FromResult(false);
		return BreakAsync(force, worldDirection, worldPosition, Mathf.Clamp(sharpness, 0, 1));
	}

	private async Task<List<MeshPart>> PrepareSceneAsync(CancellationToken cancellation)
	{
		var scene = _preGeneratedShards ?? _fragmented;
		if (scene == null) return null;
		if (_preparation != null && _cachedScene == scene) return await _preparation;
		_cachedScene = scene;
		async Task<List<MeshPart>> Read()
		{
			List<MeshPart> parts = null;
			await Schedule(() => parts = DestructibleUtils.ReadScene(scene), cancellation);
			if (parts.Count == 0) throw new ArgumentException("The fragment scene contains no meshes.");
			return parts;
		}
		_preparation = Read();
		return await _preparation;
	}

	private MeshInstance3D[] FindSources()
	{
		var parent = GetParent();
		if (IsInstanceValid(_mergedRemainder)) return new[] { _mergedRemainder };
		if (IsInstanceValid(SourceMesh)) return new[] { SourceMesh };
		if (parent is MeshInstance3D mesh) return new[] { mesh };
		if (parent == null) return Array.Empty<MeshInstance3D>();
		return parent.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()
			.Where(m => m.IsVisibleInTree() && m.Mesh != null).ToArray();
	}

	private MeshFracture.Snapshot ReadSources(MeshInstance3D[] sources, Transform3D world)
	{
		var triangles = new List<MeshFracture.Triangle>();
		var materials = new List<Material>();
		foreach (var source in sources)
		{
			var snapshot = MeshFracture.Read(source.Mesh, world.AffineInverse() * source.GlobalTransform,
				posedSource: CaptureCurrentPose ? source : null);
			var offset = materials.Count;
			triangles.AddRange(snapshot.Triangles.Select(t => t with { Surface = t.Surface + offset }));
			for (var i = 0; i < snapshot.Materials.Length; i++) materials.Add(source.GetActiveMaterial(i));
		}
		return new MeshFracture.Snapshot(triangles.ToArray(), materials.ToArray());
	}

	private async Task<(MeshFracture.Snapshot Mesh, Transform3D World, Transform3D Local)> PrepareSourceAsync(
		Node3D parent, MeshInstance3D[] sources, CancellationToken cancellation)
	{
		MeshFracture.Snapshot snapshot = null;
		var world = Transform3D.Identity;
		var local = Transform3D.Identity;
		var repair = RepairSettings();
		await Schedule(() =>
		{
			ValidateSources(parent, sources);
			world = sources.Length == 1 ? sources[0].GlobalTransform : parent.GlobalTransform;
			local = parent.GlobalTransform.AffineInverse() * world;
			snapshot = ReadSources(sources, world);
		}, cancellation);
		var prepared = await Task.Run(() => MeshRepair.Prepare(snapshot.Triangles, snapshot.Materials.Length, repair, cancellation), cancellation);
		cancellation.ThrowIfCancellationRequested();
		if (prepared.Warning != null) GD.PushWarning(prepared.Warning);
		return (snapshot with { Triangles = prepared.Triangles }, world, local);
	}

	private static void ValidateSources(Node3D parent, MeshInstance3D[] sources)
	{
		if (sources.Any(s => !IsInstanceValid(s) || !s.IsInsideTree() || s.Mesh == null || (s != parent && !parent.IsAncestorOf(s))))
			throw new ArgumentException("Source Mesh must remain inside the destructible object during preparation.");
	}

	private MeshRepair.Settings RepairSettings() => new(RepairNonManifold, RepairResolution, RepairShellThickness, ForceSolidRemesh);

	private ShardSettings Settings(float power, Vector3 direction) => new()
	{
		CollisionLayers = _collisionLayers, CollisionMasks = _layerMasks,
		ExplosionPower = Math.Max(0, power), ExplosionDirection = direction,
		ShardMass = Math.Max(0.001f, _shardMass), FadeDelay = _fadeDelay, ShrinkDelay = _shrinkDelay,
		ParticleFade = _particleFade, LinearDampening = Math.Max(0, _linearDampening), LinearDampMode = _linearDampMode,
		AngularDampening = Math.Max(0, _angularDampening), AngularDampMode = _angularDampMode,
		CleanCollisionMesh = _cleanCollisionMesh, SimplifyCollisionMesh = _simplifyCollisionMesh,
		DecomposeCollision = DecomposeCollision, DecompositionSettings = DecompositionSettings,
		CastShadows = ShardShadows,
		ShardMaterial = _fragmentedMaterial,
		LinearVelocity = GetParent() is RigidBody3D body ? body.LinearVelocity : Vector3.Zero,
		AngularVelocity = GetParent() is RigidBody3D spinning ? spinning.AngularVelocity : Vector3.Zero,
		ObjectOrigin = GetParent() is Node3D spatial ? spatial.GlobalPosition : Vector3.Zero
	};

	private Task Schedule(Action action, CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		return DestructionScheduler.Run(GetTree(), action, cancellation, ShardsPerFrame, FrameBudgetMilliseconds);
	}

	private async Task<bool> BreakAsync(float power, Vector3 direction, Vector3? hit, float sharpness)
	{
		if (_busy || _destroyed || !IsInsideTree() || Engine.IsEditorHint()) return false;
		if (!float.IsFinite(power) || !direction.IsFinite()) throw new ArgumentException("Explosion power and direction must be finite.");
		_busy = true;
		var cancellation = _lifetime.Token;
		ShardGroup group = null;
		var partial = false;
		var released = 0;
		try
		{
			var parent = GetParent() as Node3D ?? throw new ArgumentException("Destructible needs a Node3D parent.");
			var settings = Settings(power, direction);
			var exactCut = hit.HasValue && sharpness >= 0.5f && _fragmented == null && _preGeneratedShards == null;
			partial = exactCut && KeepRemainder;
			var transform = parent.GlobalTransform;
			var sources = FindSources();
			var source = sources.FirstOrDefault();
			ValidateSources(parent, sources);
			var parts = await PrepareSceneAsync(cancellation);
			Mesh remainder = null;
			Shape3D[] remainderShapes = null;
			CollisionShape3D sourceCollision = null;
			if (parts == null)
			{
				if (source?.Mesh == null) throw new ArgumentException("Assign Source Mesh, a fragmented scene, or a baked shard scene.");
				using var fracture = FractureOptions?.Snapshot() ?? new FractureSettings();
				var maximumPieces = Math.Clamp(FracturePieces, 2, 256);
				var seed = FractureSeed;
				if (partial) sourceCollision = FindSourceCollision(parent);
				var captured = await PrepareSourceAsync(parent, sources, cancellation);
				transform = captured.World;
				var snapshot = captured.Mesh;
				var materials = snapshot.Materials.Concat(new[] { InteriorMaterial }).ToArray();
				var bounds = MeshFracture.Bounds(snapshot.Triangles);
				var point = hit.HasValue ? transform.AffineInverse() * hit.Value : bounds.Position + bounds.Size * fracture.Origin;
				if (hit.HasValue && !new Aabb(bounds.Position - Vector3.One * 0.001f, bounds.Size + Vector3.One * 0.002f).HasPoint(point)) return false;
				var normal = direction.IsZeroApprox() ? Vector3.Right : (transform.Basis.Transposed() * direction).Normalized();
				var pieces = hit.HasValue ? (int)Math.Clamp(2 + power / Math.Max(0.001f, MinimumForce) * (1 - sharpness), 2, maximumPieces) : maximumPieces;
				if (hit.HasValue && sharpness < 0.5f) point = point.Lerp(bounds.GetCenter(), 0.2f);
				var fragments = await Task.Run(() => exactCut
					? Cut(snapshot.Triangles, point, normal, snapshot.Materials.Length, cancellation, fracture.InteriorUvScale)
					: MeshFracture.Fracture(snapshot.Triangles, pieces, point, normal, snapshot.Materials.Length, seed, cancellation, fracture), cancellation);
				cancellation.ThrowIfCancellationRequested();
				if (fragments.Count < 2) return false;
				if (!exactCut && fragments.Count < pieces) GD.PushWarning($"Fracture generated {fragments.Count} of {pieces} requested shards. Minimum volume or mesh geometry prevented further cuts.");
				parts = new List<MeshPart>();
				if (partial)
				{
					fragments = fragments.OrderByDescending(MeshFracture.Volume).ToList();
					await Schedule(() =>
					{
						remainder = MeshFracture.Build(fragments[0], materials, false).Mesh;
						if (sourceCollision != null)
						{
							var common = sources.Length == 1 ? source.GlobalTransform : parent.GlobalTransform;
							var colliderMesh = MeshFracture.Read(remainder, sourceCollision.GlobalTransform.AffineInverse() * common);
							var mesh = MeshFracture.Build(colliderMesh.Triangles, colliderMesh.Materials, false).Mesh;
							remainderShapes = sourceCollision.GetParent() is StaticBody3D
								? new Shape3D[] { mesh.CreateTrimeshShape() } : DestructibleUtils.CreateCollisionShapes(mesh, settings);
						}
					}, cancellation);
					fragments.RemoveAt(0);
				}
				await Task.WhenAll(fragments.Select(triangles => Schedule(() =>
					{
						var fragment = MeshFracture.Build(triangles, materials);
						parts.Add(new MeshPart(fragment.Mesh, new Transform3D(Basis.Identity, fragment.Center)));
					}, cancellation)));
			}
			var container = IsInstanceValid(_shardContainer) ? _shardContainer : parent.GetParent();
			if (container == null || container == parent || parent.IsAncestorOf(container))
				throw new ArgumentException("The shard container must be outside the destructible object.");
			var activations = new List<Action>();
			var shift = Transform3D.Identity;
			await Schedule(() =>
			{
				group = new ShardGroup();
				group.Name = parent.Name + "Shards";
				container.AddChild(group);
				group.TopLevel = true;
				group.GlobalTransform = Transform3D.Identity;
			}, cancellation);
			await Task.WhenAll(parts.Select(part => Schedule(() =>
				{
					var prepared = DestructibleUtils.Prepare(part, transform, settings);
					var shard = DestructibleUtils.CreateShard(prepared, settings);
					group.AddChild(shard);
					activations.Add(() =>
					{
						shard.GlobalTransform = shift * shard.GlobalTransform;
						DestructibleUtils.Activate(shard, settings);
					});
				}, cancellation)));
			await Schedule(() =>
			{
				ValidateSources(parent, sources);
				if (sourceCollision != null && (!IsInstanceValid(sourceCollision) || !parent.IsAncestorOf(sourceCollision)))
					throw new ArgumentException("Source Collision must remain inside the destructible object during preparation.");
				shift = (_fragmented == null && _preGeneratedShards == null && sources.Length == 1 ? source.GlobalTransform : parent.GlobalTransform) * transform.AffineInverse();
				if (shift.Basis.Determinant() <= 0 || !shift.Basis.IsEqualApprox(shift.Basis.Orthonormalized()))
					throw new InvalidOperationException("Object scale changed during shard preparation. Retry after scaling is finished.");
				settings = Settings(power, direction);
				if (partial)
				{
					if (sources.Length > 1 || sources.Any(m => m.Skin != null || m.GetSkinReference() != null || m.GetBlendShapeCount() > 0))
					{
						foreach (var animation in parent.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>()) animation.Stop(true);
						foreach (var animation in parent.FindChildren("*", "AnimationTree", true, false).OfType<AnimationTree>()) animation.Active = false;
					}
					if (sources.Length > 1)
					{
						foreach (var mesh in sources) mesh.Visible = false;
						_mergedRemainder = new MeshInstance3D { Name = "FractureRemainder" };
						parent.AddChild(_mergedRemainder);
						source = _mergedRemainder;
					}
					// The remainder is already posed; skinning it again would deform it twice.
					source.Skin = null;
					source.Skeleton = new NodePath("");
					source.Mesh = remainder;
					source.MaterialOverride = null;
					for (var i = 0; i < source.GetSurfaceOverrideMaterialCount(); i++) source.SetSurfaceOverrideMaterial(i, null);
					if (sourceCollision != null) ReplaceRemainderCollision(sourceCollision, remainderShapes);
				}
				else
				{
					parent.Visible = false;
					foreach (var collision in parent.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>()) collision.Disabled = true;
					if (parent is RigidBody3D body) body.Freeze = true;
				}
			}, cancellation);
			await Task.WhenAll(activations.Select(action => Schedule(action, cancellation)));
			group.Complete = true;
			if (!partial) { _destroyed = true; parent.QueueFree(); }
			released = parts.Count;
			return true;
		}
		catch
		{
			if (IsInstanceValid(group)) group.QueueFree();
			throw;
		}
		finally
		{
			_busy = false;
			if (released > 0 && IsInstanceValid(this) && IsInsideTree()) EmitSignal(SignalName.Fractured, released, partial);
		}
	}

	private static List<MeshFracture.Triangle[]> Cut(MeshFracture.Triangle[] triangles, Vector3 point, Vector3 normal,
		int interior, CancellationToken cancellation, float uvScale)
	{
		MeshFracture.Validate(triangles, cancellation);
		// Sharp gameplay cuts retain their exact plane, regardless of burst presets.
		if (!MeshFracture.Split(triangles, point, normal, interior, out var a, out var b, cancellation,
			new FractureSettings { InteriorUvScale = uvScale })) return new List<MeshFracture.Triangle[]>();
		MeshFracture.Validate(a, cancellation); MeshFracture.Validate(b, cancellation);
		return new List<MeshFracture.Triangle[]> { a, b };
	}

	private CollisionShape3D FindSourceCollision(Node3D parent)
	{
		if (IsInstanceValid(SourceCollision))
		{
			if (!parent.IsAncestorOf(SourceCollision)) throw new ArgumentException("Source Collision must belong to the destructible object.");
			return SourceCollision;
		}
		var collisions = parent.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>()
			.Where(collision => !collision.IsQueuedForDeletion() && !_remainderCollisions.Contains(collision)).ToArray();
		if (collisions.Length > 1) throw new ArgumentException("Assign Source Collision for partial cuts on objects with multiple colliders.");
		return collisions.FirstOrDefault();
	}

	private void ReplaceRemainderCollision(CollisionShape3D source, Shape3D[] shapes)
	{
		foreach (var collision in _remainderCollisions)
			if (IsInstanceValid(collision)) { collision.Disabled = true; collision.QueueFree(); }
		_remainderCollisions.Clear();
		source.Shape = shapes[0];
		for (var i = 1; i < shapes.Length; i++)
		{
			var collision = new CollisionShape3D { Shape = shapes[i], Transform = source.Transform };
			source.GetParent().AddChild(collision);
			_remainderCollisions.Add(collision);
		}
	}

	public async Task<string> BakeAsync()
	{
		if (_busy || !IsInsideTree()) return null;
		_busy = true;
		var cancellation = _lifetime.Token;
		var root = new Node3D { Name = GetParent().Name + "Shards" };
		try
		{
			var settings = Settings(0, Vector3.Zero);
			var parts = await PrepareSceneAsync(cancellation);
			if (parts == null)
			{
				var sources = FindSources();
				if (sources.Length == 0 || sources.Any(s => s != GetParent() && !GetParent().IsAncestorOf(s)))
					throw new ArgumentException("Assign a Source Mesh belonging to the destructible object.");
				using var fracture = FractureOptions?.Snapshot() ?? new FractureSettings();
				var seed = FractureSeed;
				var pieces = Math.Clamp(FracturePieces, 2, 256);
				var captured = await PrepareSourceAsync(GetParent<Node3D>(), sources, cancellation);
				var snapshot = captured.Mesh;
				var bounds = MeshFracture.Bounds(snapshot.Triangles);
				var fragments = await Task.Run(() => MeshFracture.Fracture(snapshot.Triangles, pieces,
					bounds.Position + bounds.Size * fracture.Origin, Vector3.Right, snapshot.Materials.Length, seed, cancellation, fracture), cancellation);
				cancellation.ThrowIfCancellationRequested();
				if (fragments.Count < 2) throw new InvalidOperationException("The selected fracture options did not produce any shards. Move Origin or reduce Minimum Volume Ratio.");
				if (fragments.Count < pieces) GD.PushWarning($"Baking generated {fragments.Count} of {pieces} requested shards. Minimum volume or mesh geometry prevented further cuts.");
				var materials = snapshot.Materials.Concat(new[] { InteriorMaterial }).ToArray();
				parts = new List<MeshPart>();
				await Task.WhenAll(fragments.Select(triangles => Schedule(() =>
					{
						var fragment = MeshFracture.Build(triangles, materials);
						parts.Add(new MeshPart(fragment.Mesh, captured.Local * new Transform3D(Basis.Identity, fragment.Center)));
					}, cancellation)));
			}
			await Task.WhenAll(parts.Select(part => Schedule(() =>
				{
					var prepared = DestructibleUtils.Prepare(part, Transform3D.Identity, settings);
					var shard = DestructibleUtils.CreateShard(prepared, settings);
					// Runtime settings are applied when these templates activate.
					shard.Freeze = false; shard.Visible = true;
					shard.CollisionLayer = _collisionLayers; shard.CollisionMask = _layerMasks;
					root.AddChild(shard);
				}, cancellation)));
			Error error = Error.Ok;
			await Schedule(() => error = DestructibleUtils.Save(root, _savePath, GetParent().Name), cancellation);
			if (error != Error.Ok) throw new InvalidOperationException("Could not save baked shards: " + error);
			var path = DestructibleUtils.SavePath(_savePath, GetParent().Name);
			GD.Print("Saved shards to ", path);
			return path;
		}
		finally { root.Free(); _busy = false; }
	}

	private async void Observe(Task task)
	{
		try
		{
			await task;
			if (task is Task<bool> result && !result.Result && !_busy && !_destroyed && IsInstanceValid(this) && IsInsideTree())
				EmitSignal(SignalName.FractureFailed, "The hit did not split the mesh.");
		}
		catch (OperationCanceledException) { }
		catch (Exception error)
		{
			GD.PushError("Destruction failed: " + error.Message);
			if (task is Task<bool> && IsInstanceValid(this) && IsInsideTree()) EmitSignal(SignalName.FractureFailed, error.Message);
		}
	}

	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		if (GetParent() is not Node3D) warnings.Add("Destructible needs a Node3D parent.");
		if (_fragmented == null && _preGeneratedShards == null && FindSources().FirstOrDefault()?.Mesh == null)
			warnings.Add("Assign Source Mesh, a fragmented scene, or a baked shard scene.");
		if (IsInstanceValid(_shardContainer))
		{
			if (GetParent() != null && (_shardContainer == GetParent() || GetParent().IsAncestorOf(_shardContainer)))
				warnings.Add("The shard container must be outside the destructible object.");
			for (var node = _shardContainer; node != null; node = node.GetParent())
				if (node is PhysicsBody3D) { warnings.Add("Use a shard container outside physics bodies."); break; }
		}
		return warnings.ToArray();
	}
}
