#if TOOLS
using Godot;
using Destructibles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public partial class DestructionTests : Node3D
{
	private int _checks;
	public override async void _Ready()
	{
		try
		{
			Geometry();
			Holes();
			await FractureControls();
			await SourcePreparation();
			await PosedMeshes();
			await RepairedBake();
			await Decomposition();
			await Scheduler();
			await Runtime();
			await Partial();
			await RigidPartial();
			await Baked();
			MaterialLoadingAndFade();
			await Imported();
			await MovingObject();
			await FailedCut();
			await Cancellation();
			await Reparenting();
			await Lifetimes();
			if (OS.GetCmdlineUserArgs().Contains("--benchmark")) await Benchmark();
			GD.Print($"PASS: {_checks} destruction checks");
			await Frames(3);
			GetTree().Quit();
		}
		catch (Exception error)
		{
			GD.PushError("FAIL: " + error);
			GetTree().Quit(1);
		}
	}

	private void Check(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
		_checks++;
	}
	private async Task Frames(int count)
	{
		for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
	private static MeshFracture.Snapshot Box() => MeshFracture.Read(new BoxMesh { Size = Vector3.One * 2 }, Transform3D.Identity);

	private void Geometry()
	{
		var source = Box();
		MeshFracture.Validate(source.Triangles);
		Check(Math.Abs(MeshFracture.Volume(source.Triangles) - 8) < 0.001, "Box volume");
		Check(MeshFracture.Split(source.Triangles, Vector3.Zero, Vector3.Right, 1, out var a, out var b), "Center cut");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(Math.Abs(MeshFracture.Volume(a) - 4) < 0.001 && Math.Abs(MeshFracture.Volume(b) - 4) < 0.001, "Capped cut conserves volume");
		Check(a.Where(t => t.Surface == 1).All(t => t.A.Normal.IsEqualApprox(Vector3.Left)), "Cut normals face outward");
		Check(a.All(t => t.A.Normal.Length() > 0.9f && t.B.Normal.Length() > 0.9f), "Normals preserved");
		Check(MeshFracture.Split(source.Triangles, Vector3.Zero, new Vector3(1, 1, 0), 1, out a, out b), "Cut through existing edges");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(!MeshFracture.Split(source.Triangles, Vector3.One * 20, Vector3.Right, 1, out a, out b), "Missed cut leaves source intact");
		var fragments = MeshFracture.Fracture(source.Triangles, 8, Vector3.Zero, Vector3.Right, 1);
		Check(fragments.Count == 8, "Requested fragment count");
		Check(Math.Abs(fragments.Sum(MeshFracture.Volume) - 8) < 0.001, "Multi-cut volume conserved");
		foreach (var fragment in fragments) MeshFracture.Validate(fragment);
		var again = MeshFracture.Fracture(source.Triangles, 8, Vector3.Zero, Vector3.Right, 1);
		Check(fragments[3].SequenceEqual(again[3]), "Seeded fracture is deterministic");
		var open = MeshFracture.Read(new PlaneMesh(), Transform3D.Identity);
		try { MeshFracture.Validate(open.Triangles); throw new Exception("Open mesh was accepted"); }
		catch (ArgumentException) { _checks++; }
		var concave = Concave();
		MeshFracture.Validate(concave.Triangles);
		Check(MeshFracture.Split(concave.Triangles, Vector3.Zero, Vector3.Back, 1, out a, out b), "Concave cap");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - 3) < 0.001, "Concave cap conserves volume");
		var torus = MeshFracture.Read(new TorusMesh(), Transform3D.Identity);
		Check(MeshFracture.Split(torus.Triangles, Vector3.Zero, Vector3.Up, 1, out a, out b), "Hollow torus cut");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - MeshFracture.Volume(torus.Triangles)) < 0.001, "Hollow cut conserves volume");
		var cancelled = new CancellationToken(true);
		try { MeshFracture.Fracture(source.Triangles, 8, Vector3.Zero, Vector3.Right, 1, cancellation: cancelled); throw new Exception("Cancelled fracture ran"); }
		catch (OperationCanceledException) { _checks++; }
		foreach (var primitive in new Mesh[] { new SphereMesh { RadialSegments = 16, Rings = 8 }, new CylinderMesh { RadialSegments = 12 } })
		{
			var snapshot = MeshFracture.Read(primitive, Transform3D.Identity);
			MeshFracture.Validate(snapshot.Triangles);
			Check(MeshFracture.Split(snapshot.Triangles, new Vector3(0.04f, 0.03f, 0.01f), new Vector3(1, 0.4f, 0.2f), 1, out a, out b), "Curved mesh cut");
			MeshFracture.Validate(a); MeshFracture.Validate(b);
			Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - MeshFracture.Volume(snapshot.Triangles)) < 0.001, "Curved mesh volume");
		}
	}

	private static MeshFracture.Snapshot Concave()
	{
		var cells = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
		var triangles = new List<MeshFracture.Triangle>();
		foreach (var cell in cells)
		{
			var cube = MeshFracture.Read(new BoxMesh { Size = Vector3.One }, new Transform3D(Basis.Identity, cell));
			triangles.AddRange(cube.Triangles.Where(t => !cells.Contains(cell + new Vector3(
				Mathf.Round(t.A.Normal.X), Mathf.Round(t.A.Normal.Y), Mathf.Round(t.A.Normal.Z)))));
		}
		return new MeshFracture.Snapshot(triangles.ToArray(), new Material[] { null });
	}

	private void Holes()
	{
		Vector3[] Square(float min, float max) => new[] { new Vector3(min, min, 0), new Vector3(max, min, 0),
			new Vector3(max, max, 0), new Vector3(min, max, 0) };
		var loops = new List<Vector3[]> { Square(-5, 5), Square(-3, 3).Reverse().ToArray(), Square(-1, 1), Square(-4.5f, -3.5f) };
		var caps = CapTriangulator.Triangulate(loops, Vector3.Zero, Vector3.Right, Vector3.Up, 0.00001f, default).ToArray();
		Check(Math.Abs(caps.Sum(t => (t.B - t.A).Cross(t.C - t.A).Length() / 2) - 67) < 0.001, "Multiple holes and nested island preserve cap area");
		var crossing = new List<Vector3[]> { new[] { new Vector3(-1, -1, 0), new Vector3(1, 1, 0),
			new Vector3(-1, 1, 0), new Vector3(1, -1, 0) } };
		try { CapTriangulator.Triangulate(crossing, Vector3.Zero, Vector3.Right, Vector3.Up, 0.00001f, default).ToArray(); throw new Exception("Intersecting contours were accepted"); }
		catch (ArgumentException) { _checks++; }
		var outer = MeshFracture.Read(new BoxMesh { Size = Vector3.One * 4 }, Transform3D.Identity);
		var inner = Box().Triangles.Select(t => new MeshFracture.Triangle(
			t.C with { Normal = -t.C.Normal }, t.B with { Normal = -t.B.Normal }, t.A with { Normal = -t.A.Normal }, 0));
		var island = MeshFracture.Read(new BoxMesh { Size = Vector3.One * 0.5f }, Transform3D.Identity);
		var hollow = outer.Triangles.Concat(inner).Concat(island.Triangles).ToArray();
		MeshFracture.Validate(hollow);
		Check(MeshFracture.Split(hollow, Vector3.Zero, Vector3.Right, 1, out var a, out var b), "Hollow shell and disconnected nested solid cut");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(Math.Abs(a.Where(t => t.Surface == 1).Sum(t => (t.B.Position - t.A.Position).Cross(t.C.Position - t.A.Position).Length() / 2) - 12.25) < 0.001, "Cavity remains empty in the cap");
		Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - 56.125) < 0.001, "Nested hollow volume preserved");
		var rebuilt = MeshFracture.Read(MeshFracture.Build(a, new Material[] { null, null }, false).Mesh, Transform3D.Identity);
		MeshFracture.Validate(rebuilt.Triangles);
		Check(MeshFracture.Split(rebuilt.Triangles, Vector3.Zero, Vector3.Up, 1, out a, out b), "Hollow remainder can be uploaded and cut again");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		var torus = MeshFracture.Read(new TorusMesh { Rings = 24, RingSegments = 16 }, Transform3D.Identity);
		var fragments = MeshFracture.Fracture(torus.Triangles, 8, Vector3.Zero, Vector3.Up, 1);
		foreach (var fragment in fragments) MeshFracture.Validate(fragment);
		Check(fragments.Count == 8 && Math.Abs(fragments.Sum(MeshFracture.Volume) - MeshFracture.Volume(torus.Triangles)) < 0.001, "Repeated hollow fracture stays watertight");
		Check(!MeshFracture.Split(Box().Triangles, Vector3.Right, Vector3.Right, 1, out a, out b), "Tangent plane does not create a zero-volume shard");
		var concave = Concave();
		Check(MeshFracture.Split(concave.Triangles, Vector3.Up * 0.5f, Vector3.Up, 1, out a, out b), "Cut can follow an existing concave face");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
	}

	private async Task FractureControls()
	{
		// An irregular, skewed custom ArrayMesh, with duplicated surface vertices.
		Vector3 Deform(Vector3 p) => new(p.X * (1 + p.Y * 0.12f + p.Z * 0.07f) + p.Y * 0.3f,
			p.Y + p.X * 0.2f, p.Z * (1 + p.X * 0.1f));
		var custom = Box().Triangles.Select(t =>
		{
			var a = Deform(t.A.Position); var b = Deform(t.B.Position); var c = Deform(t.C.Position);
			var n = (c - a).Cross(b - a).Normalized();
			return new MeshFracture.Triangle(t.A with { Position = a, Normal = n },
				t.B with { Position = b, Normal = n }, t.C with { Position = c, Normal = n }, 0);
		}).ToArray();
		var source = MeshFracture.Read(MeshFracture.Build(custom, new Material[] { null }, false).Mesh, Transform3D.Identity);
		MeshFracture.Validate(source.Triangles);
		var volume = MeshFracture.Volume(custom);
		var origin = MeshFracture.Bounds(custom).GetCenter();
		foreach (var pattern in Enum.GetValues<FracturePattern>())
		{
			using var settings = new FractureSettings { Pattern = pattern };
			var parts = await Task.Run(() => MeshFracture.Fracture(source.Triangles, 16, origin, Vector3.Right, 1, settings: settings));
			foreach (var part in parts) MeshFracture.Validate(part);
			Check(parts.Count == 16 && Math.Abs(parts.Sum(MeshFracture.Volume) - volume) < 0.001, $"{pattern} fractures a custom mesh to the requested count without losing volume");
			if (pattern == FracturePattern.Slices)
				Check(parts.SelectMany(p => p).Where(t => t.Surface == 1).All(t => Math.Abs(t.A.Normal.Y) > 0.999f), "Slice caps follow the chosen local axis");
			if (pattern == FracturePattern.Grid)
				Check(parts.SelectMany(p => p).Where(t => t.Surface == 1).All(t =>
					Math.Max(Math.Abs(t.A.Normal.X), Math.Max(Math.Abs(t.A.Normal.Y), Math.Abs(t.A.Normal.Z))) > 0.999f), "Grid caps follow local coordinate axes");
		}
		TriangleNoiseChecks();
		foreach (var type in Enum.GetValues<FastNoiseLite.NoiseTypeEnum>())
		{
			using var noise = new FastNoiseLite { NoiseType = type, Frequency = 1.4f, Seed = 73, FractalType = FastNoiseLite.FractalTypeEnum.Fbm };
			using var options = new FractureOptions { Noise = noise, NoiseInfluence = 0.4f,
				SurfaceRoughness = 0.02f, SizeVariation = 0.3f, DirectionVariation = 0.1f };
			using var settings = options.Snapshot();
			var parts = await Task.Run(() => MeshFracture.Fracture(custom, 12, origin, Vector3.Right, 1, 41, settings: settings));
			foreach (var part in parts) MeshFracture.Validate(part);
			Check(parts.Count == 12 && Math.Abs(parts.Sum(MeshFracture.Volume) - volume) < 0.001, $"{type} noisy, rough fracture is watertight and conserves volume");
			var again = await Task.Run(() => MeshFracture.Fracture(custom, 12, origin, Vector3.Right, 1, 41, settings: settings));
			Check(parts.Zip(again).All(pair => pair.First.SequenceEqual(pair.Second)), $"{type} is reproducible with both seeds unchanged");
		}
		using var grid = new FractureSettings { Pattern = FracturePattern.Grid };
		var many = MeshFracture.Fracture(Box().Triangles, 100, Vector3.Zero, Vector3.Right, 1, settings: grid);
		foreach (var part in many) MeshFracture.Validate(part);
		Check(many.Count == 100 && Math.Abs(many.Sum(MeshFracture.Volume) - 8) < 0.001, "Shard targets above the old 64-piece limit work");
		var capped = MeshFracture.Fracture(Box().Triangles, 999, Vector3.Zero, Vector3.Right, 1, settings: grid);
		foreach (var part in capped) MeshFracture.Validate(part);
		Check(capped.Count == 256 && Math.Abs(capped.Sum(MeshFracture.Volume) - 8) < 0.001, "Shard targets are bounded to 256 even for direct API calls");
		using var minimum = new FractureSettings { Pattern = FracturePattern.Grid, MinimumVolumeRatio = 0.12f };
		var limited = MeshFracture.Fracture(Box().Triangles, 32, Vector3.Zero, Vector3.Right, 1, settings: minimum);
		Check(limited.Count == 8 && limited.All(p => MeshFracture.Volume(p) >= 8 * 0.12f), "Minimum volume limits shard quantity without dropping geometry");
		using var rough = new FractureSettings { SurfaceRoughness = 0.25f, InteriorUvScale = 3 };
		var thin = MeshFracture.Read(new BoxMesh { Size = new Vector3(0.01f, 2, 2) }, Transform3D.Identity);
		Check(MeshFracture.Split(thin.Triangles, Vector3.Zero, Vector3.Right, 1, out var a, out var b, settings: rough), "Rough cut on a thin wall");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(a.Concat(b).SelectMany(t => new[] { t.A.Position, t.B.Position, t.C.Position }).All(p => Math.Abs(p.X) <= 0.00501f), "Roughness backs off before crossing a thin exterior");
		Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - MeshFracture.Volume(thin.Triangles)) < 0.00001, "Mating rough caps conserve thin-wall volume");
		var torus = MeshFracture.Read(new TorusMesh { Rings = 24, RingSegments = 16 }, Transform3D.Identity);
		Check(MeshFracture.Split(torus.Triangles, Vector3.Zero, Vector3.Up, 1, out a, out b, settings: rough), "Rough cap supports a hollow mesh");
		MeshFracture.Validate(a); MeshFracture.Validate(b);
		Check(Math.Abs(MeshFracture.Volume(a) + MeshFracture.Volume(b) - MeshFracture.Volume(torus.Triangles)) < 0.001, "Rough hollow cap conserves volume");
		await BakePreset();
		await RuntimePreset();
	}

	private void TriangleNoiseChecks()
	{
		using var noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Seed = 19, Frequency = 1 };
		using var options = new FractureOptions { Pattern = FracturePattern.Slices, Axis = Vector3.Zero,
			Origin = new Vector3(-2, 0.3f, 7), MinimumVolumeRatio = 1, Noise = noise };
		using var settings = options.Snapshot();
		var sample = settings.Noise.GetNoise3Dv(new Vector3(0.7f, 0.3f, 0.5f));
		noise.Seed = 98;
		Check(settings.Noise != noise && sample == settings.Noise.GetNoise3Dv(new Vector3(0.7f, 0.3f, 0.5f)), "A running job has a private noise copy unaffected by preset edits");
		Check(settings.Axis == Vector3.Up && settings.Origin == new Vector3(0, 0.3f, 1) && settings.MinimumVolumeRatio == 0.49f, "Preset snapshot clamps invalid options");
		using var plain = new FractureSettings();
		using var textured = new FractureSettings { SurfaceRoughness = 0.05f, InteriorUvScale = 3 };
		MeshFracture.Split(Box().Triangles, Vector3.Zero, Vector3.Right, 1, out var flat, out _, settings: plain);
		MeshFracture.Split(Box().Triangles, Vector3.Zero, Vector3.Right, 1, out var rough, out var other, settings: textured);
		var caps = rough.Where(t => t.Surface == 1).ToArray();
		Check(caps.Any(t => Math.Abs(t.C.Position.X) > 0.0001f), "Surface roughness changes the cap geometry");
		Check(caps.Length > flat.Count(t => t.Surface == 1) && caps.All(t =>
			other.Any(o => o.Surface == 1 && o.A.Position == t.C.Position && o.B.Position == t.B.Position && o.C.Position == t.A.Position)), "Rough mating caps have identical vertices and opposite winding");
		Check(caps.SelectMany(t => new[] { t.A, t.B, t.C }).All(v =>
			v.UV.IsEqualApprox(new Vector2(v.Position.Z, -v.Position.Y) * 3)), "Interior UV scale applies to new cap vertices");
	}

	private async Task BakePreset()
	{
		var (world, mesh, destruction) = Create();
		using var options = new FractureOptions { Pattern = FracturePattern.Slices, Axis = Vector3.Back,
			SurfaceRoughness = 0.03f, InteriorUvScale = 2, NoiseInfluence = 0 };
		destruction.FractureOptions = options;
		destruction.FracturePieces = 10;
		destruction.Set("_savePath", "user://destruction-tests");
		DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://destruction-tests"));
		var presetPath = "user://destruction-tests/FracturePreset.tres";
		Check(ResourceSaver.Save(options, presetPath) == Error.Ok, "Fracture preset saves as a reusable resource");
		var loaded = ResourceLoader.Load<FractureOptions>(presetPath, cacheMode: ResourceLoader.CacheMode.Ignore);
		Check(loaded.Pattern == options.Pattern && loaded.Axis == options.Axis && loaded.SurfaceRoughness == options.SurfaceRoughness, "Fracture preset round-trips through Godot serialization");
		var path = await destruction.BakeAsync();
		var baked = DestructibleUtils.ReadScene(ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore));
		Check(baked.Count == 10 && Math.Abs(baked.Sum(part => MeshFracture.Volume(MeshFracture.Read(part.Mesh, Transform3D.Identity).Triangles)) - 8) < 0.001, "Baking uses the selected pattern, roughness and shard count");
		destruction.Set("_preGeneratedShards", ResourceLoader.Load<PackedScene>(path));
		Check(await destruction.DestroyAsync(0), "Preset-baked shards activate during gameplay");
		Check(world.GetChildren().OfType<ShardGroup>().Single().GetChildCount() == 10, "Gameplay keeps the baked shard quantity");
		world.QueueFree(); await Frames(3);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(presetPath));
	}

	private async Task RuntimePreset()
	{
		var (world, mesh, destruction) = Create();
		using var options = new FractureOptions { Pattern = FracturePattern.Slices, Axis = Vector3.Back };
		destruction.FractureOptions = options;
		destruction.FracturePieces = 9;
		Check(await destruction.DestroyAsync(0), "Runtime destruction uses a fracture preset");
		var pieces = world.GetChildren().OfType<ShardGroup>().Single().GetChildren().OfType<Shard>().ToArray();
		Check(pieces.Length == 9 && pieces.All(p => MeshFracture.Read(p.GetNode<MeshInstance3D>("MeshInstance").Mesh,
			Transform3D.Identity).Triangles.Where(t => t.Surface == 1).All(t => Math.Abs(t.A.Normal.Z) > 0.999f)), "Runtime slice count and axis reach the actual shard meshes");
		world.QueueFree(); await Frames(3);
		(world, mesh, destruction) = Create();
		using var noise = new FastNoiseLite { Frequency = 2 };
		using var rough = new FractureOptions { Pattern = FracturePattern.Radial, Noise = noise,
			NoiseInfluence = 1, SurfaceRoughness = 0.25f, InteriorUvScale = 3 };
		destruction.FractureOptions = rough;
		Check(await destruction.HitAsync(new Vector3(0.25f, 0, 0), Vector3.Right, 5, 1), "Sharp gameplay cut accepts a burst preset");
		var remainder = MeshFracture.Read(mesh.Mesh, Transform3D.Identity);
		Check(remainder.Triangles.Where(t => t.Surface == 1).All(t => Math.Abs(t.A.Position.X - 0.25f) < 0.00001f &&
			Math.Abs(t.B.Position.X - 0.25f) < 0.00001f && Math.Abs(t.C.Position.X - 0.25f) < 0.00001f), "Sharp cuts keep the exact blade plane despite noise and roughness options");
		world.QueueFree(); await Frames(3);
	}

	private async Task SourcePreparation()
	{
		var box = Box().Triangles;
		var unchanged = MeshRepair.Prepare(box, 1);
		Check(ReferenceEquals(unchanged.Triangles, box) && unchanged.Warning == null, "Valid static geometry is left unchanged");
		var duplicate = MeshRepair.Prepare(box.Concat(box.Take(2)).ToArray(), 1);
		MeshFracture.Validate(duplicate.Triangles);
		Check(duplicate.Warning != null && duplicate.Triangles.Length == box.Length, "Duplicate faces are removed without remeshing");
		var reversed = (MeshFracture.Triangle[])box.Clone();
		var t = reversed[0]; reversed[0] = new MeshFracture.Triangle(t.C, t.B, t.A, t.Surface);
		var oriented = MeshRepair.Prepare(reversed, 1);
		MeshFracture.Validate(oriented.Triangles);
		Check(oriented.Warning != null && Math.Abs(MeshFracture.Volume(oriented.Triangles) - 8) < 0.001, "Inconsistent winding is repaired");
		var openBox = box.Where(t => t.A.Normal.Y < 0.9f).ToArray();
		var capped = MeshRepair.Prepare(openBox, 1);
		MeshFracture.Validate(capped.Triangles);
		Check(capped.Warning != null && Math.Abs(MeshFracture.Volume(capped.Triangles) - 8) < 0.001 && capped.Triangles.Any(t => t.Surface == 1), "Planar boundary holes close with interior faces and original volume");
		var plane = MeshFracture.Read(new PlaneMesh(), Transform3D.Identity).Triangles;
		var thick = await Task.Run(() => MeshRepair.Prepare(plane, 1, new MeshRepair.Settings(Resolution: 24)));
		MeshFracture.Validate(thick.Triangles);
		Check(thick.Warning.Contains("distance-field") && MeshFracture.Volume(thick.Triangles) > 0, "An open sheet is automatically given a closed solid shell");
		var again = MeshRepair.Prepare(plane, 1, new MeshRepair.Settings(Resolution: 24));
		Check(thick.Triangles.SequenceEqual(again.Triangles), "Automatic remeshing is deterministic");
		var forced = MeshRepair.Prepare(box, 1, new MeshRepair.Settings(Resolution: 16, ForceRemesh: true));
		Check(forced.Warning.Contains("distance-field") && forced.Triangles.Length > box.Length, "Forced solid remeshing handles otherwise closed overlapping geometry");
		var touching = MeshFracture.Read(new BoxMesh { Size = Vector3.One * 2 }, new Transform3D(Basis.Identity, new Vector3(2, 2, 0))).Triangles.Concat(box).ToArray();
		var repaired = await Task.Run(() => MeshRepair.Prepare(touching, 1, new MeshRepair.Settings(Resolution: 24)));
		MeshFracture.Validate(repaired.Triangles);
		Check(repaired.Warning.Contains("distance-field"), "Solids sharing a non-manifold edge get a manifold reconstruction");
		var fragments = MeshFracture.Fracture(repaired.Triangles, 8, MeshFracture.Bounds(repaired.Triangles).GetCenter(), Vector3.Right, 1);
		foreach (var fragment in fragments) MeshFracture.Validate(fragment);
		Check(fragments.Count == 8 && Math.Abs(fragments.Sum(MeshFracture.Volume) - MeshFracture.Volume(repaired.Triangles)) < 0.01, "Reconstructed non-manifold solids can be fractured");
		try { MeshRepair.Prepare(plane, 1, new MeshRepair.Settings(false)); throw new Exception("Strict preparation accepted an open sheet"); }
		catch (ArgumentException) { _checks++; }
		try { MeshRepair.Prepare(plane, 1, cancellation: new CancellationToken(true)); throw new Exception("Cancelled preparation completed"); }
		catch (OperationCanceledException) { _checks++; }
		var (world, mesh, destruction) = Create();
		mesh.Mesh = new PlaneMesh(); destruction.RepairResolution = 24;
		var original = mesh.Mesh;
		Check(await destruction.DestroyAsync(0), "Automatic repair is used by gameplay destruction");
		Check(original.GetSurfaceCount() == 1, "Repair does not mutate a shared source mesh resource");
		world.QueueFree(); await Frames(3);

		// A model root may have several meshes. Bake and partial cuts must retain
		// all of them, including their local transforms and material overrides.
		var model = new Node3D { Name = "MultiMesh" }; AddChild(model);
		var left = new MeshInstance3D { Mesh = new BoxMesh(), Position = Vector3.Left * 1.25f,
			MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Red } };
		var right = new MeshInstance3D { Mesh = new BoxMesh(), Position = Vector3.Right * 1.25f,
			MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Blue } };
		model.AddChild(left); model.AddChild(right);
		var hidden = new Node3D { Visible = false }; model.AddChild(hidden);
		hidden.AddChild(new MeshInstance3D { Mesh = new BoxMesh(), Position = Vector3.Right * 100 });
		var generator = new Destructible { FracturePieces = 4 }; model.AddChild(generator);
		generator.Set("_savePath", "user://destruction-tests/model");
		var animation = new AnimationPlayer(); model.AddChild(animation);
		var library = new AnimationLibrary();
		library.AddAnimation("idle", new Animation { Length = 10, LoopMode = Animation.LoopModeEnum.Linear });
		animation.AddAnimationLibrary("", library); animation.Play("idle");
		var path = await generator.BakeAsync();
		var baked = DestructibleUtils.ReadScene(ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore));
		var modelBounds = baked.Select(p => MeshFracture.Bounds(MeshFracture.Read(p.Mesh, p.Transform).Triangles)).Aggregate((a, b) => a.Merge(b));
		Check(baked.Count == 4 && Math.Abs(modelBounds.Size.X - 3.5f) < 0.001f, "Model selection bakes visible meshes in a common frame and excludes hidden branches");
		Check(left.Visible && right.Visible && animation.IsPlaying(), "Model baking leaves original visibility and animation untouched");
		var body = new StaticBody3D(); model.AddChild(body);
		body.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
		body.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
		try { await generator.HitAsync(Vector3.Zero, Vector3.Up, 5, 1); throw new Exception("Ambiguous collision selection succeeded"); }
		catch (ArgumentException) { _checks++; }
		Check(left.Visible && right.Visible && animation.IsPlaying(), "Failed partial preparation leaves model meshes and animation untouched");
		body.QueueFree(); await Frames(3);
		Check(await generator.HitAsync(Vector3.Zero, Vector3.Up, 5, 1), "Multi-mesh models accept partial cuts");
		var remainder = model.GetNode<MeshInstance3D>("FractureRemainder");
		Check(!left.Visible && !right.Visible && !animation.IsPlaying(), "Partial model cuts replace original meshes with a frozen merged remainder");
		Check(Enumerable.Range(0, remainder.Mesh.GetSurfaceCount()).Select(remainder.Mesh.SurfaceGetMaterial)
			.OfType<StandardMaterial3D>().Select(m => m.AlbedoColor).Distinct().Count() == 2, "Merged remainder retains both source material overrides");
		Check(await generator.DestroyAsync(0), "Subsequent breaks use the merged remainder rather than hidden original meshes");
		model.QueueFree(); await Frames(3);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
	}

	private async Task RepairedBake()
	{
		var (world, mesh, destruction) = Create();
		var triangles = Box().Triangles.Concat(MeshFracture.Read(new BoxMesh { Size = Vector3.One * 2 },
			new Transform3D(Basis.Identity, new Vector3(2, 2, 0))).Triangles).ToArray();
		mesh.Mesh = MeshFracture.Build(triangles, new Material[] { new StandardMaterial3D() }, false).Mesh;
		var original = mesh.Mesh;
		destruction.RepairResolution = 24;
		destruction.Set("_savePath", "user://destruction-tests/repair");
		var path = await destruction.BakeAsync();
		var parts = DestructibleUtils.ReadScene(ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore));
		Check(parts.Count == 8, "Non-manifold input bakes to the desired shard count");
		Check(mesh.Mesh == original && mesh.Visible, "Non-manifold baking leaves the original mesh intact");
		foreach (var part in parts) MeshFracture.Validate(MeshFracture.Read(part.Mesh, Transform3D.Identity).Triangles);
		Check(await destruction.DestroyAsync(0), "Non-manifold input also fractures directly during gameplay");
		world.QueueFree(); await Frames(3);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
	}

	private async Task PosedMeshes()
	{
		foreach (var influences in new[] { 4, 8 }) foreach (var mode in new[] { Mesh.BlendShapeMode.Normalized, Mesh.BlendShapeMode.Relative })
		{
			var world = new Node3D { Position = new Vector3(4, 7, -2) }; AddChild(world);
			var skeleton = new Skeleton3D { Name = "Skeleton" }; world.AddChild(skeleton);
			skeleton.AddBone("root"); skeleton.AddBone("tip"); skeleton.SetBoneParent(1, 0);
			skeleton.SetBoneRest(0, Transform3D.Identity);
			var rest = new Transform3D(Basis.Identity, Vector3.Up);
			skeleton.SetBoneRest(1, rest);
			skeleton.SetBonePosePosition(0, Vector3.Right);
			skeleton.SetBonePosePosition(1, Vector3.Up);
			skeleton.SetBonePoseRotation(1, new Quaternion(Vector3.Back, 0.3f));
			var arrays = new BoxMesh { Size = Vector3.One * 2 }.SurfaceGetArrays(0);
			var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			var bones = new int[positions.Length * influences]; var weights = new float[bones.Length];
			for (var i = 0; i < positions.Length; i++)
			{
				bones[i * influences] = 0; weights[i * influences] = 0.75f;
				bones[i * influences + influences - 1] = 1; weights[i * influences + influences - 1] = 0.25f;
			}
			arrays[(int)Mesh.ArrayType.Bones] = bones; arrays[(int)Mesh.ArrayType.Weights] = weights;
			var shaped = new ArrayMesh { BlendShapeMode = mode }; shaped.AddBlendShape("Shift");
			var target = new Godot.Collections.Array(); target.Resize((int)Mesh.ArrayType.Max);
			target[(int)Mesh.ArrayType.Vertex] = positions.Select(p => (mode == Mesh.BlendShapeMode.Relative ? Vector3.Zero : p) + Vector3.Right * 0.4f).ToArray();
			var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
			target[(int)Mesh.ArrayType.Normal] = mode == Mesh.BlendShapeMode.Relative ? new Vector3[normals.Length] : normals;
			var tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
			target[(int)Mesh.ArrayType.Tangent] = mode == Mesh.BlendShapeMode.Relative ? new float[tangents.Length] : tangents;
			shaped.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays,
				new Godot.Collections.Array<Godot.Collections.Array> { target }, flags: influences == 8 ? Mesh.ArrayFormat.FlagUse8BoneWeights : 0);
			var material = new StandardMaterial3D { AlbedoColor = Colors.Red }; shaped.SurfaceSetMaterial(0, material);
			var skin = new Skin(); skin.SetBindCount(2);
			skin.SetBindName(0, "tip"); skin.SetBindPose(0, rest.AffineInverse());
			skin.SetBindName(1, "root"); skin.SetBindPose(1, Transform3D.Identity);
			var mesh = new MeshInstance3D { Name = "Posed", Mesh = shaped, Skin = skin, Skeleton = new NodePath("..") };
			skeleton.AddChild(mesh); mesh.SetBlendShapeValue(0, 0.5f);
			var destruction = new Destructible { SourceMesh = mesh, FracturePieces = 4 };
			destruction.Set("_savePath", "user://destruction-tests/pose"); world.AddChild(destruction);
			await Frames(3);
			var tip = skeleton.GetBoneGlobalPose(1) * rest.AffineInverse();
			var root = skeleton.GetBoneGlobalPose(0);
			var snapshot = MeshFracture.Read(shaped, Transform3D.Identity, posedSource: mesh);
			var index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array()[0];
			var input = positions[index] + Vector3.Right * 0.2f;
			var expected = (tip * input) * 0.75f + (root * input) * 0.25f;
			Check(snapshot.Triangles[0].A.Position.DistanceTo(expected) < 0.0001f, $"{influences}-weight {mode} snapshot applies named skin binds, current pose and shape keys together");
			MeshFracture.Validate(snapshot.Triangles);
			var expectedBounds = MeshFracture.Bounds(snapshot.Triangles);
			var before = skeleton.GetBoneGlobalPose(1);
			var path = await destruction.BakeAsync();
			var parts = DestructibleUtils.ReadScene(ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore));
			var bakedBounds = parts.Select(p => MeshFracture.Bounds(MeshFracture.Read(p.Mesh, p.Transform).Triangles)).Aggregate((a, b) => a.Merge(b));
			Check(parts.Count == 4 && bakedBounds.Position.IsEqualApprox(expectedBounds.Position) && bakedBounds.Size.IsEqualApprox(expectedBounds.Size), "Baked fragments match the captured current rig pose");
			Check(mesh.Mesh == shaped && mesh.Skin == skin && mesh.GetBlendShapeValue(0) == 0.5f && skeleton.GetBoneGlobalPose(1).IsEqualApprox(before), "Baking leaves the original rig, mesh, pose and shape keys untouched");
			Check(parts.All(p => p.Mesh is ArrayMesh a && a.GetBlendShapeCount() == 0 &&
				p.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil), "Posed shards are static geometry without live deformation data");
			Check(parts.All(p => p.Mesh.SurfaceGetMaterial(0) is StandardMaterial3D m && m.AlbedoColor.IsEqualApprox(Colors.Red)), "Pose snapshots preserve source materials");
			skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Idle;
			var modifier = new TestPoseModifier(); skeleton.AddChild(modifier); await Frames(4);
			var modified = MeshFracture.Read(shaped, Transform3D.Identity, posedSource: mesh);
			var modifiedBounds = MeshFracture.Bounds(modified.Triangles);
			Check(modifiedBounds.Position.DistanceTo(expectedBounds.Position + Vector3.Up * 2) < 0.001f,
				"Pose snapshot includes the rendered skeleton modifier result after CPU poses are restored");
			Check(skeleton.GetBonePosePosition(0) == Vector3.Right, "Snapshot does not overwrite the skeleton's base animation pose");
			var next = new Node3D(); AddChild(next); world.Reparent(next); await Frames(3);
			var reparented = MeshFracture.Read(shaped, Transform3D.Identity, posedSource: mesh);
			Check(MeshFracture.Bounds(reparented.Triangles).Position.DistanceTo(modifiedBounds.Position) < 0.001f,
				"Reparenting a rig restores final modifier pose observation");
			Check(await destruction.HitAsync(mesh.ToGlobal(modifiedBounds.GetCenter()), Vector3.Right, 5, 1), "A live, non-baked rig accepts a partial sharp cut");
			Check(mesh.Skin == null && mesh.GetBlendShapeCount() == 0, "Partial rig cuts keep a static posed remainder without double skinning");
			Check(await destruction.DestroyAsync(0), "The posed remainder can fracture again during gameplay");
			next.QueueFree(); await Frames(3);
			DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		}
	}

	private async Task Decomposition()
	{
		var settings = new ShardSettings { DecomposeCollision = true, FadeDelay = -1, ShrinkDelay = -1, ExplosionPower = 0 };
		var concave = Concave();
		var mesh = MeshFracture.Build(concave.Triangles, concave.Materials, false).Mesh;
		var timer = Stopwatch.StartNew();
		var shapes = DestructibleUtils.CreateCollisionShapes(mesh, settings);
		GD.Print($"DECOMPOSITION: concave hulls={shapes.Length} preparation_ms={timer.Elapsed.TotalMilliseconds:F2}");
		Check(shapes.Length > 1 && shapes.All(shape => shape is ConvexPolygonShape3D), "Concave debris uses several convex hulls");
		var part = new MeshPart(mesh, Transform3D.Identity, shapes);
		var root = new Node3D();
		root.AddChild(DestructibleUtils.CreateShard(part, settings));
		var error = DestructibleUtils.Save(root, "user://destruction-tests", "Compound");
		root.Free();
		Check(error == Error.Ok, "Compound hull bake saves");
		var path = DestructibleUtils.SavePath("user://destruction-tests", "Compound");
		var scene = ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
		var cached = DestructibleUtils.ReadScene(scene).Single();
		Check(cached.Shapes.Length == shapes.Length && cached.Shapes.Zip(shapes).All(pair => pair.First == pair.Second ||
			((ConvexPolygonShape3D)pair.First).Points.SequenceEqual(((ConvexPolygonShape3D)pair.Second).Points)), "Every compound hull round-trips through baked scene data");
		var scaled = DestructibleUtils.Prepare(cached, new Transform3D(Basis.FromScale(new Vector3(2, 1, 3)), Vector3.Zero), settings);
		Check(scaled.Shapes.Length == shapes.Length && scaled.Transform.Basis.Scale.IsEqualApprox(Vector3.One), "Every cached hull supports non-uniform scale");
		Check(((ConvexPolygonShape3D)scaled.Shapes[0]).Points.Zip(((ConvexPolygonShape3D)cached.Shapes[0]).Points)
			.All(pair => pair.First.IsEqualApprox(pair.Second * new Vector3(2, 1, 3))), "Cached hull points receive the mesh scale");
		var (world, _, destruction) = Create();
		destruction.Set("_preGeneratedShards", scene);
		Check(await destruction.DestroyAsync(0), "Baked concave debris activates");
		var shard = world.GetChildren().OfType<ShardGroup>().Single().GetChildren().OfType<Shard>().Single();
		Check(shard.GetChildren().OfType<CollisionShape3D>().Count() == shapes.Length, "Runtime body retains every baked collider");
		shard.Freeze = true;
		for (var i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		var space = GetWorld3D().DirectSpaceState;
		Check(space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(0.85f, 0.85f, 2), new Vector3(0.85f, 0.85f, -2))).Count == 0, "Compound colliders leave the concave notch empty");
		Check(space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(1, 0, 2), new Vector3(1, 0, -2))).Count > 0, "Compound colliders retain solid collision");
		world.QueueFree(); await Frames(3);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
	}

	private async Task Scheduler()
	{
		var counts = new Dictionary<ulong, int>();
		var tasks = Enumerable.Range(0, 12).Select(_ => DestructionScheduler.Run(GetTree(), () =>
		{
			var frame = Engine.GetProcessFrames();
			counts[frame] = counts.GetValueOrDefault(frame) + 1;
		}, default, 2, 100)).ToArray();
		await Task.WhenAll(tasks);
		Check(counts.Count >= 6 && counts.Values.All(count => count <= 2), "Shared queue bounds work per frame");
	}

	private (Node3D World, MeshInstance3D Mesh, Destructible Destruction) Create()
	{
		var world = new Node3D();
		AddChild(world);
		var mesh = new MeshInstance3D { Name = "Object", Mesh = new BoxMesh { Size = Vector3.One * 2 } };
		var destruction = new Destructible { FracturePieces = 8, ShardsPerFrame = 2 };
		destruction.Set("_fadeDelay", -1); destruction.Set("_shrinkDelay", -1);
		mesh.AddChild(destruction);
		world.AddChild(mesh);
		return (world, mesh, destruction);
	}

	private async Task Runtime()
	{
		var (world, mesh, destruction) = Create();
		mesh.Position = new Vector3(4, 8, -3);
		mesh.Rotation = new Vector3(0.3f, 0.7f, 0.1f);
		mesh.Scale = new Vector3(2, 1, 3);
		var task = destruction.DestroyAsync(3, Vector3.Right);
		Check(destruction.IsBusy && mesh.Visible, "Original remains until preparation completes");
		Check(!await destruction.DestroyAsync(), "Duplicate destruction is ignored");
		Check(await task, "Destruction completes");
		await Frames(3);
		Check(!GodotObject.IsInstanceValid(mesh), "Original is freed");
		var group = world.GetChildren().OfType<Node3D>().Single();
		var shards = group.GetChildren().OfType<Shard>().ToArray();
		Check(shards.Length == 8, "Node shard count");
		Check(shards.All(shard => shard.GlobalBasis.Scale.IsEqualApprox(Vector3.One)), "Physics bodies are unscaled");
		Check(shards.Any(shard => shard.LinearVelocity.X > 0), "Impulse reached physics body");
		Check(shards.All(shard => shard.GetNode<CollisionShape3D>("CollisionShape").Shape != null), "Convex shapes created");
		world.QueueFree(); await Frames(3);
	}

	private async Task Partial()
	{
		var (world, mesh, destruction) = Create();
		var body = new StaticBody3D();
		var collision = new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * 2 } };
		body.AddChild(collision); mesh.AddChild(body);
		var originalShape = collision.Shape;
		Task<bool> followup = null;
		destruction.Fractured += (_, _) =>
		{
			if (followup != null) return;
			Check(!destruction.IsBusy, "Fractured permits a follow-up hit from its callback");
			followup = destruction.HitAsync(new Vector3(-0.25f, 0, 0), Vector3.Right, 5, 1);
		};
		Check(!await destruction.HitAsync(Vector3.Zero, Vector3.Right, 0.5f, 1), "Weak hit ignored");
		Check(await destruction.HitAsync(new Vector3(0.25f, 0, 0), Vector3.Right, 5, 1), "Partial sharp hit");
		Check(mesh.Visible && GodotObject.IsInstanceValid(mesh), "Partial hit keeps the original object");
		var remaining = MeshFracture.Read(mesh.Mesh, Transform3D.Identity);
		Check(Math.Abs(MeshFracture.Volume(remaining.Triangles) - 5) < 0.001, "Larger remainder retained");
		Check(collision.Shape != originalShape, "Remainder collider updated");
		Check(followup != null && await followup, "Repeated partial cut from the completion callback");
		Check(Math.Abs(MeshFracture.Volume(MeshFracture.Read(mesh.Mesh, Transform3D.Identity).Triangles) - 3) < 0.001, "Second remainder volume");
		world.QueueFree(); await Frames(3);
	}

	private async Task RigidPartial()
	{
		var world = new Node3D(); AddChild(world);
		var body = new RigidBody3D { Freeze = true, Position = Vector3.Up * 6 };
		var mesh = new MeshInstance3D { Mesh = new TorusMesh { Rings = 24, RingSegments = 16 } };
		var collision = new CollisionShape3D { Shape = mesh.Mesh.CreateConvexShape() };
		var destruction = new Destructible { DecomposeCollision = true };
		destruction.Set("_fadeDelay", -1); destruction.Set("_shrinkDelay", -1);
		body.AddChild(mesh); body.AddChild(collision); body.AddChild(destruction); world.AddChild(body);
		Check(await destruction.HitAsync(body.Position + Vector3.Up * 0.03f, Vector3.Up, 1, 1), "Hollow rigid body partial cut");
		await Frames(3);
		Check(body.GetChildren().OfType<CollisionShape3D>().Count() > 1, "Rigid remainder receives compound hulls");
		var firstShape = collision.Shape;
		Check(await destruction.HitAsync(body.Position - Vector3.Up * 0.06f, Vector3.Up, 1, 1), "Repeated rigid compound cut finds the original source collider");
		await Frames(3);
		Check(collision.Shape != firstShape && body.GetChildren().OfType<CollisionShape3D>().All(c => !c.IsQueuedForDeletion()), "Repeated rigid cuts replace and clean up previous hulls");
		MeshFracture.Validate(MeshFracture.Read(mesh.Mesh, Transform3D.Identity).Triangles);
		world.QueueFree(); await Frames(3);
	}

	private async Task Baked()
	{
		var (world, mesh, destruction) = Create();
		destruction.Set("_savePath", "user://destruction-tests");
		var material = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.2f, 0.1f) };
		mesh.SetSurfaceOverrideMaterial(0, material);
		var path = await destruction.BakeAsync();
		Check(mesh.Visible && path != null, "Baking keeps the original object");
		var baked = ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
		var parts = DestructibleUtils.ReadScene(baked);
		Check(parts.Count == 8 && parts.All(part => part.Shapes?.Length > 0), "Baked meshes and hulls round-trip");
		Check(parts.All(part => part.Mesh.SurfaceGetMaterial(0) is BaseMaterial3D colour && colour.AlbedoColor.IsEqualApprox(material.AlbedoColor)), "Baking preserves surface overrides");
		destruction.Set("_savePath", "res://");
		var defaultPath = await destruction.BakeAsync();
		Check(defaultPath.StartsWith("res://") && ResourceLoader.Exists(defaultPath), "Default resource save directory round-trips");
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(defaultPath));
		destruction.Set("_preGeneratedShards", baked);
		Check(await destruction.DestroyAsync(), "Baked destruction");
		await Frames(3);
		Check(world.GetChildren().OfType<ShardGroup>().Single().GetChildCount() == 8, "Baked scene activates every shard");
		world.QueueFree(); await Frames(3);
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
	}

	private async Task Imported()
	{
		var scene = GD.Load<PackedScene>("res://Demo/fractured_cube.glb");
		var parts = DestructibleUtils.ReadScene(scene);
		Check(parts.Count > 1, "Existing GLB fragment scene is supported");
		var (world, mesh, destruction) = Create();
		destruction.Set("_fragmented", scene);
		Check(await destruction.DestroyAsync(), "Existing pre-fractured destruction");
		await Frames(3);
		Check(world.GetChildren().OfType<ShardGroup>().Single().GetChildCount() == parts.Count, "Imported shard count");
		world.QueueFree(); await Frames(3);
	}

	private void MaterialLoadingAndFade()
	{
		var root = new Node3D();
		var resource = new BoxMesh();
		var red = new StandardMaterial3D { AlbedoColor = Colors.Red };
		var blue = new StandardMaterial3D { AlbedoColor = Colors.Blue };
		var first = new MeshInstance3D { Mesh = resource, MaterialOverride = blue };
		first.SetSurfaceOverrideMaterial(0, red); root.AddChild(first);
		var second = new MeshInstance3D { Mesh = resource, Position = Vector3.Right * 2 };
		second.SetSurfaceOverrideMaterial(0, red); root.AddChild(second);
		first.Owner = root; second.Owner = root;
		var scene = new PackedScene(); scene.Pack(root);
		foreach (var parts in new[] { DestructibleUtils.ReadMeshes(root), DestructibleUtils.ReadScene(scene) })
		{
			Check(parts.All(p => p.Mesh == resource), "Fragment material overrides retain the shared mesh without rebuilding geometry");
			var settings = new ShardSettings();
			for (var i = 0; i < parts.Count; i++)
			{
				var shard = DestructibleUtils.CreateShard(DestructibleUtils.Prepare(parts[i], Transform3D.Identity, settings), settings);
				Check(shard.GetNode<MeshInstance3D>("MeshInstance").GetActiveMaterial(0) == (i == 0 ? blue : red),
					"Fragment loading preserves overall and per-surface override precedence");
				shard.Free();
			}
		}
		var tiny = DestructibleUtils.Prepare(new MeshPart(resource, Transform3D.Identity),
			new Transform3D(Basis.FromScale(Vector3.One * 0.01f), Vector3.Zero), new ShardSettings());
		Check(tiny.Transform.Basis.IsEqualApprox(Basis.Identity) && tiny.Mesh.GetAabb().Size.IsEqualApprox(Vector3.One * 0.01f), "Small non-zero object scales remain valid and are baked into geometry");
		var visual = new ShardVisual(first, 0.1f, -1, true); visual.Update(1.1);
		Check(first.GetActiveMaterial(0) is BaseMaterial3D faded && faded != blue && Math.Abs(faded.AlbedoColor.A - 0.5f) < 0.001,
			"Fading an overall material override changes the visible material");
		Check(blue.AlbedoColor.A == 1 && resource.Material == null, "Fading preserves shared source materials and mesh resources");
		root.Free();
	}

	private async Task Cancellation()
	{
		var (world, _, destruction) = Create();
		var task = destruction.DestroyAsync();
		world.QueueFree();
		try { await task; throw new Exception("Destruction of a freed object completed"); }
		catch (OperationCanceledException) { _checks++; }
		await Frames(3);
	}

	private async Task MovingObject()
	{
		var (world, mesh, destruction) = Create();
		var task = destruction.DestroyAsync(0);
		mesh.Position = new Vector3(20, 5, 0);
		Check(await task, "Moving object destruction");
		var shards = world.GetChildren().OfType<ShardGroup>().Single().GetChildren().OfType<Shard>().ToArray();
		Check(shards.All(shard => Math.Abs(shard.GlobalPosition.X - 20) < 2), "Shards use the latest object pose");
		world.QueueFree(); await Frames(3);
	}

	private async Task Reparenting()
	{
		var (world, mesh, destruction) = Create();
		var next = new Node3D(); AddChild(next);
		var pending = destruction.DestroyAsync();
		while (!world.GetChildren().OfType<ShardGroup>().Any()) await Frames(1);
		mesh.Reparent(next);
		try { await pending; throw new Exception("Reparenting did not cancel pending preparation"); }
		catch (OperationCanceledException) { _checks++; }
		await Frames(3);
		Check(mesh.Visible && !destruction.IsBusy && !world.GetChildren().OfType<ShardGroup>().Any(), "Cancelled reparenting preserves the source and removes unfinished debris");
		Check(await destruction.DestroyAsync(), "Reparented object can still be destroyed");
		world.QueueFree(); next.QueueFree(); await Frames(3);
	}

	private async Task FailedCut()
	{
		var (world, mesh, destruction) = Create();
		destruction.RepairNonManifold = false;
		mesh.Mesh = new PlaneMesh();
		try { await destruction.DestroyAsync(); throw new Exception("Open mesh destruction succeeded"); }
		catch (ArgumentException) { _checks++; }
		Check(mesh.Visible && !destruction.IsBusy && world.GetChildCount() == 1, "Failed fracture preserves object and creates no debris");
		Check(destruction._GetConfigurationWarnings().Length == 0, "Runtime source configuration is valid");
		var missing = new Destructible();
		Check(missing._GetConfigurationWarnings().Length > 0, "Missing source is reported");
		missing.Free();
		mesh.Mesh = new BoxMesh();
		var failed = ToSignal(destruction, Destructible.SignalName.FractureFailed);
		destruction.Hit(Vector3.Right * 100, Vector3.Right, 1, 1);
		await failed;
		Check(mesh.Visible && !destruction.IsBusy, "Fire-and-forget hits report failure without leaving the UI busy");
		world.QueueFree(); await Frames(3);
	}

	private async Task Lifetimes()
	{
		var group = new ShardGroup { Complete = true };
		AddChild(group);
		var settings = new ShardSettings { FadeDelay = -1, ShrinkDelay = 0.02f, ExplosionPower = 0 };
		var part = DestructibleUtils.Prepare(new MeshPart(new BoxMesh(), Transform3D.Identity), Transform3D.Identity, settings);
		var first = DestructibleUtils.CreateShard(part, settings);
		var second = DestructibleUtils.CreateShard(part, new ShardSettings { FadeDelay = -1, ShrinkDelay = -1, ExplosionPower = 0 });
		group.AddChild(first); group.AddChild(second);
		DestructibleUtils.Activate(first, settings);
		DestructibleUtils.Activate(second, settings);
		await ToSignal(GetTree().CreateTimer(2.1), SceneTreeTimer.SignalName.Timeout);
		Check(!GodotObject.IsInstanceValid(first) && GodotObject.IsInstanceValid(second), "Each shard owns its lifetime");
		group.QueueFree(); await Frames(3);
	}

	private async Task Benchmark()
	{
		var source = MeshFracture.Read(new SphereMesh { RadialSegments = 32, Rings = 16 }, Transform3D.Identity);
		for (var run = 0; run < 4; run++)
		{
			var allocated = GC.GetAllocatedBytesForCurrentThread();
			var timer = Stopwatch.StartNew();
			var prepared = MeshRepair.Prepare(source.Triangles, 1, new MeshRepair.Settings(Resolution: 32, ForceRemesh: true));
			if (run > 0) GD.Print($"BENCHMARK Repair: resolution=32 triangles={prepared.Triangles.Length} elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} allocated_mb={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0:F2}");
		}
		// The same scene/hulls are used throughout.
		var root = new Node3D();
		for (var i = 0; i < 128; i++)
			root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 0.25f }, Position = new Vector3(i % 16, i / 16, 0) });
		foreach (var child in root.GetChildren()) child.Owner = root;
		var scene = new PackedScene(); scene.Pack(root); root.Free();
		// Warm up the node path before recording.
		for (var run = 0; run < 4; run++)
		{
			var (world, _, destruction) = Create();
			destruction.Set("_fragmented", scene);
			destruction.ShardsPerFrame = 8;
			var previous = Stopwatch.GetTimestamp();
			double maximum = 0;
			var task = destruction.DestroyAsync();
			var timer = Stopwatch.StartNew();
			while (!task.IsCompleted)
			{
				await Frames(1);
				var now = Stopwatch.GetTimestamp();
				maximum = Math.Max(maximum, (now - previous) * 1000.0 / Stopwatch.Frequency);
				previous = now;
			}
			await task;
			if (run > 0) GD.Print($"BENCHMARK Nodes: shards=128 elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} max_frame_interval_ms={maximum:F2}");
			world.QueueFree(); await Frames(3);
		}
	}
}
#endif
