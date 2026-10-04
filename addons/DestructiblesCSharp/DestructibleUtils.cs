using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Destructibles;

public sealed record MeshPart(Mesh Mesh, Transform3D Transform, Shape3D[] Shapes = null, Material Material = null,
	Material[] SurfaceMaterials = null);

public static class DestructibleUtils
{
	// Read baked resources directly; inherited scenes need a temporary tree.
	public static List<MeshPart> ReadScene(PackedScene scene)
	{
		var state = scene.GetState();
		if (state.GetBaseSceneState() != null || Enumerable.Range(0, state.GetNodeCount()).Any(i => state.GetNodeInstance(i) != null))
		{
			var root = scene.Instantiate<Node3D>();
			try { return ReadMeshes(root); }
			finally { root.Free(); }
		}
		var transforms = new Dictionary<string, Transform3D>();
		var properties = new Dictionary<string, Dictionary<string, Variant>>();
		var meshes = new List<(string Path, Mesh Mesh, Material Material, Material[] Surfaces)>();
		for (var i = 0; i < state.GetNodeCount(); i++)
		{
			var path = state.GetNodePath(i).ToString();
			var values = new Dictionary<string, Variant>();
			for (var p = 0; p < state.GetNodePropertyCount(i); p++)
				values[state.GetNodePropertyName(i, p).ToString()] = state.GetNodePropertyValue(i, p);
			properties[path] = values;
			var parent = ParentPath(path);
			var transform = values.TryGetValue("transform", out var value) ? value.AsTransform3D() : Transform3D.Identity;
			if (!values.ContainsKey("transform"))
			{
				var rotation = values.TryGetValue("rotation", out value) ? value.AsVector3() : Vector3.Zero;
				if (values.TryGetValue("rotation_degrees", out value)) rotation = value.AsVector3() * Mathf.Pi / 180;
				var basis = Basis.FromEuler(rotation);
				var scale = values.TryGetValue("scale", out value) ? value.AsVector3() : Vector3.One;
				basis.X *= scale.X; basis.Y *= scale.Y; basis.Z *= scale.Z;
				transform = new Transform3D(basis, values.TryGetValue("position", out value) ? value.AsVector3() : Vector3.Zero);
			}
			transforms[path] = path == "." ? transform : transforms.GetValueOrDefault(parent, Transform3D.Identity) * transform;
			if (values.TryGetValue("mesh", out value) && value.AsGodotObject() is Mesh mesh)
			{
				Material[] surfaces = null;
				for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
					if (values.TryGetValue("surface_material_override/" + surface, out var material) && material.AsGodotObject() is Material overridden)
					{
						surfaces ??= new Material[mesh.GetSurfaceCount()];
						surfaces[surface] = overridden;
					}
				meshes.Add((path, mesh, values.TryGetValue("material_override", out value) ? value.AsGodotObject() as Material : null, surfaces));
			}
		}
		var meshCounts = meshes.GroupBy(mesh => ParentPath(mesh.Path)).ToDictionary(group => group.Key, group => group.Count());
		var hulls = properties.Where(pair => pair.Key != "." && pair.Value.GetValueOrDefault("shape").AsGodotObject() is ConvexPolygonShape3D &&
			!pair.Value.GetValueOrDefault("disabled").AsBool()).GroupBy(pair => ParentPath(pair.Key))
			.ToDictionary(group => group.Key, group => group.ToArray());
		return meshes.Select(item =>
		{
			var body = ParentPath(item.Path);
			// Baked hulls share their mesh's coordinates. Retain every aligned hull.
			var shapes = hulls.TryGetValue(body, out var siblings) && meshCounts[body] == 1
				? siblings.Where(pair => transforms[item.Path].IsEqualApprox(transforms[pair.Key]))
					.Select(pair => (Shape3D)pair.Value["shape"].AsGodotObject()).ToArray() : Array.Empty<Shape3D>();
			return new MeshPart(item.Mesh, transforms[item.Path], shapes.Length > 0 ? shapes : null, item.Material, item.Surfaces);
		}).ToList();
	}

	private static string ParentPath(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? "." : path[..slash];
	}

	public static List<MeshPart> ReadMeshes(Node3D root)
	{
		var result = new List<MeshPart>();
		void Visit(Node node, Transform3D transform)
		{
			if (node is Node3D spatial) transform *= spatial.Transform;
			if (node is MeshInstance3D mesh && mesh.Mesh != null)
			{
				Material[] surfaces = null;
				for (var i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
					if (mesh.GetSurfaceOverrideMaterial(i) is Material material)
					{
						surfaces ??= new Material[mesh.Mesh.GetSurfaceCount()];
						surfaces[i] = material;
					}
				var shapes = mesh.GetParent()?.GetChildren().OfType<CollisionShape3D>()
					.Where(collision => !collision.Disabled && collision.Transform.IsEqualApprox(mesh.Transform))
					.Select(collision => collision.Shape).OfType<ConvexPolygonShape3D>().Cast<Shape3D>().ToArray();
				if (mesh.GetParent()?.GetChildren().OfType<MeshInstance3D>().Count() != 1) shapes = null;
				result.Add(new MeshPart(mesh.Mesh, transform, shapes?.Length > 0 ? shapes : null, mesh.MaterialOverride, surfaces));
			}
			foreach (var child in node.GetChildren()) Visit(child, transform);
		}
		Visit(root, Transform3D.Identity);
		return result;
	}

	// Bake scale/shear into geometry so rigid bodies stay unscaled.
	public static MeshPart Prepare(MeshPart part, Transform3D objectTransform, ShardSettings settings)
	{
		var world = objectTransform * part.Transform;
		if (world.Basis.Determinant() == 0 || !float.IsFinite(world.Basis.Determinant()))
			throw new ArgumentException("Cannot create shards with zero or non-finite scale.");
		var rotation = world.Basis.Orthonormalized();
		if (rotation.Determinant() < 0) rotation.X = -rotation.X;
		var stretch = rotation.Inverse() * world.Basis;
		var mesh = part.Mesh;
		var shapes = part.Shapes;
		if (!stretch.IsEqualApprox(Basis.Identity))
		{
			var snapshot = MeshFracture.Read(mesh, new Transform3D(stretch, Vector3.Zero));
			mesh = MeshFracture.Build(snapshot.Triangles, snapshot.Materials, false).Mesh;
			shapes = shapes?.All(shape => shape is ConvexPolygonShape3D) == true
				? shapes.Cast<ConvexPolygonShape3D>().Select(convex => (Shape3D)new ConvexPolygonShape3D
					{ Points = convex.Points.Select(point => stretch * point).ToArray() }).ToArray() : null;
		}
		shapes ??= CreateCollisionShapes(mesh, settings);
		return new MeshPart(mesh, new Transform3D(rotation, world.Origin), shapes, settings.ShardMaterial ?? part.Material, part.SurfaceMaterials);
	}

	// V-HACD needs temporary nodes. Retain the hulls and free the tree.
	public static Shape3D[] CreateCollisionShapes(Mesh mesh, ShardSettings settings)
	{
		if (!settings.DecomposeCollision)
			return new Shape3D[] { mesh.CreateConvexShape(settings.CleanCollisionMesh, settings.SimplifyCollisionMesh)
				?? throw new ArgumentException("Could not build a convex collider for a shard.") };
		var temporary = new MeshInstance3D { Mesh = mesh };
		try
		{
			using var defaults = settings.DecompositionSettings == null ? new MeshConvexDecompositionSettings
				{ MaxConvexHulls = 8, Resolution = 100000, MaxConcavity = 0.002f, NormalizeMesh = true } : null;
			temporary.CreateMultipleConvexCollisions(settings.DecompositionSettings ?? defaults);
			var shapes = temporary.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>()
				.Select(collision => collision.Shape).OfType<ConvexPolygonShape3D>().Cast<Shape3D>().ToArray();
			if (shapes.Length == 0) throw new ArgumentException("Convex decomposition produced no hulls. Check mesh topology and decomposition settings.");
			return shapes;
		}
		finally { temporary.Free(); }
	}

	public static Shard CreateShard(MeshPart part, ShardSettings settings)
	{
		var shard = new Shard
		{
			Prepared = true, Freeze = true, Visible = false, Transform = part.Transform,
			CollisionLayer = 0, CollisionMask = 0, Mass = Math.Max(0.001f, settings.ShardMass),
			FadeDelay = settings.FadeDelay, ShrinkDelay = settings.ShrinkDelay, ParticleFade = settings.ParticleFade,
			ExplosionPower = settings.ExplosionPower, ExplosionDirection = settings.ExplosionDirection,
			LinearDamp = settings.LinearDampening, LinearDampMode = settings.LinearDampMode,
			AngularDamp = settings.AngularDampening, AngularDampMode = settings.AngularDampMode
		};
		var mesh = new MeshInstance3D { Name = "MeshInstance", Mesh = part.Mesh,
			CastShadow = settings.CastShadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
		for (var i = 0; i < part.Mesh.GetSurfaceCount(); i++)
		{
			var material = part.Material ?? (i < part.SurfaceMaterials?.Length ? part.SurfaceMaterials[i] : null);
			if (material != null) mesh.SetSurfaceOverrideMaterial(i, material);
		}
		shard.AddChild(mesh);
		for (var i = 0; i < part.Shapes.Length; i++)
			shard.AddChild(new CollisionShape3D { Name = i == 0 ? "CollisionShape" : "CollisionShape" + i, Shape = part.Shapes[i] });
		return shard;
	}

	public static void Activate(Shard shard, ShardSettings settings)
	{
		shard.CollisionLayer = settings.CollisionLayers;
		shard.CollisionMask = settings.CollisionMasks;
		shard.Freeze = false;
		shard.Visible = true;
		shard.LinearVelocity = settings.LinearVelocity + settings.AngularVelocity.Cross(shard.GlobalPosition - settings.ObjectOrigin);
		shard.AngularVelocity = settings.AngularVelocity;
		shard.Initialize();
	}

	public static Error Save(Node3D shards, string directory, string name)
	{
		var absolute = ProjectSettings.GlobalizePath(directory);
		var error = DirAccess.MakeDirRecursiveAbsolute(absolute);
		if (error != Error.Ok) return error;
		void SetOwner(Node node)
		{
			foreach (var child in node.GetChildren()) { child.Owner = shards; SetOwner(child); }
		}
		SetOwner(shards);
		using var packed = new PackedScene();
		error = packed.Pack(shards);
		return error == Error.Ok ? ResourceSaver.Save(packed, SavePath(directory, name)) : error;
	}

	public static string SavePath(string directory, string name) =>
		directory + (directory.EndsWith('/') ? "" : "/") + name + "Shards.tscn";

	public static Vector3 RandomDirection()
	{
		Vector3 direction;
		do { direction = new Vector3(GD.Randf() * 2 - 1, GD.Randf() * 2 - 1, GD.Randf() * 2 - 1); }
		while (direction.IsZeroApprox() || direction.LengthSquared() > 1);
		return direction.Normalized();
	}
}

public sealed class ShardSettings
{
	public uint CollisionLayers { get; init; } = 1;
	public uint CollisionMasks { get; init; } = 1;
	public float ExplosionPower { get; init; } = 4;
	public Vector3 ExplosionDirection { get; init; }
	public float ShardMass { get; init; } = 1;
	public float FadeDelay { get; init; } = 2;
	public float ShrinkDelay { get; init; } = 2;
	public bool ParticleFade { get; init; } = true;
	public float LinearDampening { get; init; }
	public RigidBody3D.DampMode LinearDampMode { get; init; }
	public float AngularDampening { get; init; }
	public RigidBody3D.DampMode AngularDampMode { get; init; }
	public bool CleanCollisionMesh { get; init; } = true;
	public bool SimplifyCollisionMesh { get; init; }
	public bool CastShadows { get; init; } = true;
	public bool DecomposeCollision { get; init; }
	public MeshConvexDecompositionSettings DecompositionSettings { get; init; }
	public Material ShardMaterial { get; init; }
	public Vector3 LinearVelocity { get; init; }
	public Vector3 AngularVelocity { get; init; }
	public Vector3 ObjectOrigin { get; init; }
}
