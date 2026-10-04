using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static Destructibles.MeshFracture;

namespace Destructibles;

// Preserve source surfaces when possible; remesh if topology still fails.
public static class MeshRepair
{
	public sealed record Settings(bool Enabled = true, int Resolution = 64, float ShellThickness = 0, bool ForceRemesh = false);
	public sealed record Result(Triangle[] Triangles, string Warning = null);
	private readonly record struct Edge(int A, int B);
	private readonly record struct Face(int A, int B, int C);

	public static Result Prepare(Triangle[] source, int interiorSurface, Settings settings = null,
		CancellationToken cancellation = default)
	{
		settings ??= new Settings();
		if (source.Length == 0 || source.Length > 100000)
			throw new NotSupportedException("Source preparation needs 1–100,000 triangles.");
		if (source.Any(t => !t.A.Position.IsFinite() || !t.B.Position.IsFinite() || !t.C.Position.IsFinite()))
			throw new ArgumentException("The source contains non-finite vertex positions.");
		if (!settings.ForceRemesh || !settings.Enabled)
		{
			try { Validate(source, cancellation); return new Result(source); }
			catch (ArgumentException) { if (!settings.Enabled) throw; }
		}
		var cleaned = Clean(source, interiorSurface, cancellation);
		try
		{
			Validate(cleaned, cancellation);
			if (!settings.ForceRemesh)
				return new Result(cleaned, "Fracture automatically repaired duplicate faces, winding or planar boundary holes on a temporary copy. The source asset was left intact.");
		}
		catch (ArgumentException) { }
		var rebuilt = VoxelRemesh.Build(cleaned, settings.Resolution, settings.ShellThickness, cancellation);
		Validate(rebuilt, cancellation);
		return new Result(rebuilt, $"Fracture source was rebuilt on a {Math.Clamp(settings.Resolution, 16, 96)}-cell distance-field grid. Open surfaces receive thickness; small gaps/details, UVs and volume can change. Increase Repair Resolution for detail, or bake ahead of gameplay. The source asset was left intact.");
	}

	private static Dictionary<Edge, List<(int Triangle, int Direction)>> Edges(List<Triangle> triangles, Weld weld)
	{
		var result = new Dictionary<Edge, List<(int, int)>>();
		Span<int> vertices = stackalloc int[3];
		for (var i = 0; i < triangles.Count; i++)
		{
			var t = triangles[i];
			vertices[0] = weld.Add(t.A.Position); vertices[1] = weld.Add(t.B.Position); vertices[2] = weld.Add(t.C.Position);
			for (var v = 0; v < 3; v++)
			{
				var a = vertices[v]; var b = vertices[(v + 1) % 3];
				var edge = new Edge(Math.Min(a, b), Math.Max(a, b));
				if (!result.TryGetValue(edge, out var list)) result[edge] = list = new();
				list.Add((i, a < b ? 1 : -1));
			}
		}
		return result;
	}

	private static Triangle[] Clean(Triangle[] source, int interiorSurface, CancellationToken cancellation)
	{
		var epsilon = Tolerance(source);
		var weld = new Weld(epsilon);
		var faces = new HashSet<Face>();
		var triangles = new List<Triangle>();
		foreach (var t in source)
		{
			cancellation.ThrowIfCancellationRequested();
			var a = weld.Add(t.A.Position); var b = weld.Add(t.B.Position); var c = weld.Add(t.C.Position);
			var low = Math.Min(a, Math.Min(b, c)); var high = Math.Max(a, Math.Max(b, c));
			if (a == b || b == c || c == a || !faces.Add(new Face(low, a + b + c - low - high, high))) continue;
			triangles.Add(new Triangle(t.A with { Position = weld.Positions[a] },
				t.B with { Position = weld.Positions[b] }, t.C with { Position = weld.Positions[c] }, t.Surface));
		}
		if (triangles.Count == 0) throw new ArgumentException("The source has no usable faces after welding.");
		var edges = Edges(triangles, weld);
		var neighbours = Enumerable.Range(0, triangles.Count).Select(_ => new List<(int Index, bool Flip)>()).ToArray();
		foreach (var list in edges.Values.Where(e => e.Count == 2))
		{
			var a = list[0]; var b = list[1];
			neighbours[a.Triangle].Add((b.Triangle, a.Direction == b.Direction));
			neighbours[b.Triangle].Add((a.Triangle, a.Direction == b.Direction));
		}
		var orientation = Enumerable.Repeat(-1, triangles.Count).ToArray();
		for (var start = 0; start < triangles.Count; start++)
		{
			if (orientation[start] >= 0) continue;
			var queue = new Queue<int>(); var component = new List<int>();
			queue.Enqueue(start); orientation[start] = 0;
			while (queue.Count > 0)
			{
				cancellation.ThrowIfCancellationRequested();
				var i = queue.Dequeue(); component.Add(i);
				foreach (var (next, flip) in neighbours[i])
					if (orientation[next] < 0) { orientation[next] = orientation[i] ^ (flip ? 1 : 0); queue.Enqueue(next); }
			}
			// Follow the source shading normals when deciding the component's outside.
			var score = component.Sum(i =>
			{
				var t = triangles[i];
				return (t.C.Position - t.A.Position).Cross(t.B.Position - t.A.Position)
					.Dot(t.A.Normal + t.B.Normal + t.C.Normal) * (orientation[i] == 0 ? 1 : -1);
			});
			foreach (var i in component)
			{
				if ((orientation[i] == 1) == (score >= 0))
				{
					var t = triangles[i]; triangles[i] = new Triangle(t.C, t.B, t.A, t.Surface);
				}
				var oriented = triangles[i];
				var normal = (oriented.C.Position - oriented.A.Position).Cross(oriented.B.Position - oriented.A.Position);
				if (normal.Dot(oriented.A.Normal + oriented.B.Normal + oriented.C.Normal) < 0)
					triangles[i] = new Triangle(oriented.A with { Normal = -oriented.A.Normal },
						oriented.B with { Normal = -oriented.B.Normal }, oriented.C with { Normal = -oriented.C.Normal }, oriented.Surface);
			}
		}
		edges = Edges(triangles, weld);
		if (edges.Values.Any(e => e.Count > 2)) return triangles.ToArray();
		var boundary = edges.Where(e => e.Value.Count == 1).ToArray();
		if (boundary.Length == 0) return triangles.ToArray();
		var nextVertices = new Dictionary<int, int>();
		foreach (var (edge, list) in boundary)
		{
			var a = list[0].Direction > 0 ? edge.A : edge.B;
			var b = list[0].Direction > 0 ? edge.B : edge.A;
			if (!nextVertices.TryAdd(a, b)) return triangles.ToArray();
		}
		var loops = new List<(Vector3[] Points, Vector3 Normal, float Area)>();
		while (nextVertices.Count > 0)
		{
			var first = nextVertices.Keys.First(); var current = first; var points = new List<Vector3>();
			do
			{
				if (!nextVertices.Remove(current, out var next)) return triangles.ToArray();
				points.Add(weld.Positions[current]); current = next;
			} while (current != first);
			var area = Vector3.Zero;
			for (var i = 0; i < points.Count; i++) area += (points[i] - points[0]).Cross(points[(i + 1) % points.Count] - points[0]);
			var normal = area.Normalized();
			if (normal.IsZeroApprox() || points.Any(p => Math.Abs(normal.Dot(p - points[0])) > epsilon * 8)) return triangles.ToArray();
			loops.Add((points.ToArray(), normal, area.Length()));
		}
		while (loops.Count > 0)
		{
			var outer = loops.OrderByDescending(l => l.Area).First();
			var group = loops.Where(l => Math.Abs(l.Normal.Dot(outer.Normal)) > 0.9999f &&
				l.Points.All(p => Math.Abs(outer.Normal.Dot(p - outer.Points[0])) < epsilon * 8)).ToList();
			foreach (var loop in group) loops.Remove(loop);
			var point = outer.Points[0];
			var tangent = outer.Normal.Cross(Math.Abs(outer.Normal.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
			var bitangent = outer.Normal.Cross(tangent);
			try
			{
				foreach (var (a, b, c) in CapTriangulator.Triangulate(group.Select(l => l.Points).ToList(), point, tangent, bitangent, epsilon, cancellation))
				{
					Vertex Cap(Vector3 p) => new(p, outer.Normal, new Vector2(tangent.Dot(p - point), bitangent.Dot(p - point)), Vector2.Zero, Colors.White);
					triangles.Add(new Triangle(Cap(c), Cap(b), Cap(a), interiorSurface));
				}
			}
			catch (ArgumentException) { return triangles.ToArray(); }
		}
		return triangles.ToArray();
	}
}
