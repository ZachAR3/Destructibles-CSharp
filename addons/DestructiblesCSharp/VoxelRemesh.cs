using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static Destructibles.MeshFracture;

namespace Destructibles;

// Reconstruct triangle soup with a distance field and marching tetrahedra.
internal static class VoxelRemesh
{
	private static readonly int[][] Tetrahedra = { new[] { 0, 1, 3, 7 }, new[] { 0, 3, 2, 7 },
		new[] { 0, 2, 6, 7 }, new[] { 0, 6, 4, 7 }, new[] { 0, 4, 5, 7 }, new[] { 0, 5, 1, 7 } };
	private readonly record struct Edge(int A, int B);

	public static Triangle[] Build(Triangle[] source, int resolution, float thickness, CancellationToken cancellation)
	{
		var original = Bounds(source);
		var longest = Math.Max(original.Size.X, Math.Max(original.Size.Y, original.Size.Z));
		if (!float.IsFinite(longest) || longest <= 0 || !float.IsFinite(thickness)) throw new ArgumentException("The source has no extent to repair.");
		var cell = longest / Math.Clamp(resolution, 16, 96);
		var shell = Math.Max(cell * 0.55f, Math.Max(0, thickness) * 0.5f);
		var origin = original.Position - Vector3.One * (shell + cell * 2.13f);
		var extent = original.Size + Vector3.One * (shell * 2 + cell * 4.26f);
		var size = new[] { (int)Math.Ceiling(extent.X / cell) + 1, (int)Math.Ceiling(extent.Y / cell) + 1, (int)Math.Ceiling(extent.Z / cell) + 1 };
		long count = (long)size[0] * size[1] * size[2];
		if (size.Any(n => n <= 0 || n > 2000000) || count <= 0 || count > 2000000) throw new ArgumentException("Repair grid is too large. Reduce Repair Resolution or Repair Shell Thickness.");
		int Index(int x, int y, int z) => x + size[0] * (y + size[1] * z);
		Vector3 Position(int x, int y, int z) => origin + new Vector3(x, y, z) * cell;
		var votes = new byte[(int)count];
		for (var axis = 0; axis < 3; axis++)
		{
			cancellation.ThrowIfCancellationRequested();
			var u = (axis + 1) % 3; var v = (axis + 2) % 3;
			var rays = new List<(float Position, int Delta)>[size[u] * size[v]];
			foreach (var triangle in source)
			{
				cancellation.ThrowIfCancellationRequested();
				var a = triangle.A.Position; var b = triangle.B.Position; var c = triangle.C.Position;
				var normal = (c - a).Cross(b - a);
				var determinant = (b[u] - a[u]) * (c[v] - a[v]) - (b[v] - a[v]) * (c[u] - a[u]);
				if (Math.Abs(determinant) < cell * cell * 1e-8f) continue;
				var fromU = Math.Max(0, (int)Math.Ceiling((Math.Min(a[u], Math.Min(b[u], c[u])) - origin[u]) / cell));
				var toU = Math.Min(size[u] - 1, (int)Math.Floor((Math.Max(a[u], Math.Max(b[u], c[u])) - origin[u]) / cell));
				var fromV = Math.Max(0, (int)Math.Ceiling((Math.Min(a[v], Math.Min(b[v], c[v])) - origin[v]) / cell));
				var toV = Math.Min(size[v] - 1, (int)Math.Floor((Math.Max(a[v], Math.Max(b[v], c[v])) - origin[v]) / cell));
				for (var j = fromV; j <= toV; j++) for (var i = fromU; i <= toU; i++)
				{
					var pu = origin[u] + i * cell - a[u]; var pv = origin[v] + j * cell - a[v];
					var beta = (pu * (c[v] - a[v]) - pv * (c[u] - a[u])) / determinant;
					var gamma = ((b[u] - a[u]) * pv - (b[v] - a[v]) * pu) / determinant;
					if (beta < -1e-6f || gamma < -1e-6f || beta + gamma > 1.000001f) continue;
					var line = i + size[u] * j;
					(rays[line] ??= new()).Add((a[axis] + beta * (b[axis] - a[axis]) + gamma * (c[axis] - a[axis]), Math.Sign(normal[axis])));
				}
			}
			for (var j = 0; j < size[v]; j++) for (var i = 0; i < size[u]; i++)
			{
				var hits = rays[i + size[u] * j];
				if (hits == null) continue;
				hits.Sort((a, b) => a.Position.CompareTo(b.Position));
				var unique = new List<(float Position, int Delta)>();
				for (var h = 0; h < hits.Count;)
				{
					var p = hits[h].Position; var delta = 0;
					while (h < hits.Count && Math.Abs(hits[h].Position - p) < cell * 1e-4f) delta += hits[h++].Delta;
					unique.Add((p, Math.Sign(delta)));
				}
				var total = unique.Sum(h => h.Delta); var winding = 0; var next = 0;
				var coordinates = new int[3]; coordinates[u] = i; coordinates[v] = j;
				for (var k = 0; k < size[axis]; k++)
				{
					var p = origin[axis] + k * cell;
					while (next < unique.Count && unique[next].Position < p) winding += unique[next++].Delta;
					// A lone sheet needs thickness, not an unbounded inside region.
					if (winding == 0 || winding == total) continue;
					coordinates[axis] = k;
					votes[Index(coordinates[0], coordinates[1], coordinates[2])]++;
				}
			}
		}
		var tree = new NearestTriangle(source);
		var values = new float[(int)count];
		for (var z = 0; z < size[2]; z++) for (var y = 0; y < size[1]; y++) for (var x = 0; x < size[0]; x++)
		{
			cancellation.ThrowIfCancellationRequested();
			var index = Index(x, y, z);
			var nearest = tree.Find(Position(x, y, z));
			var distance = (float)Math.Sqrt(nearest.DistanceSquared);
			var value = Math.Max(votes[index] >= 2 ? distance : -distance, shell - distance);
			// Avoid exact zero vertices, which produce collapsed/ambiguous triangles.
			values[index] = Math.Abs(value) < cell * 0.01f ? -cell * 0.01f : value;
		}
		var intersections = new Dictionary<Edge, Vector3>();
		var output = new List<Triangle>();
		var ids = new int[8];
		var positions = new Vector3[8];
		var inside = new int[4];
		var outside = new int[4];
		for (var z = 0; z + 1 < size[2]; z++) for (var y = 0; y + 1 < size[1]; y++) for (var x = 0; x + 1 < size[0]; x++)
		{
			cancellation.ThrowIfCancellationRequested();
			var positive = 0;
			for (var i = 0; i < 8; i++)
			{
				ids[i] = Index(x + (i & 1), y + ((i >> 1) & 1), z + ((i >> 2) & 1));
				if (values[ids[i]] > 0) positive++;
			}
			if (positive == 0 || positive == 8) continue;
			for (var i = 0; i < 8; i++) positions[i] = Position(x + (i & 1), y + ((i >> 1) & 1), z + ((i >> 2) & 1));
			Vector3 Intersection(int a, int b)
			{
				var edge = new Edge(Math.Min(ids[a], ids[b]), Math.Max(ids[a], ids[b]));
				if (!intersections.TryGetValue(edge, out var p))
					intersections[edge] = p = positions[a].Lerp(positions[b], values[ids[a]] / (values[ids[a]] - values[ids[b]]));
				return p;
			}
			void Add(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
			{
				var normal = (c - a).Cross(b - a);
				if (normal.Dot(outward) < 0) { (b, c) = (c, b); normal = -normal; }
				if (normal.LengthSquared() < cell * cell * cell * cell * 1e-16f) return;
				normal = normal.Normalized();
				var t = tree.Find((a + b + c) / 3).Triangle;
				output.Add(new Triangle(Project(a, normal, t), Project(b, normal, t), Project(c, normal, t), t.Surface));
				if (output.Count > 100000) throw new NotSupportedException("Repaired mesh exceeds 100,000 triangles. Reduce Repair Resolution.");
			}
			foreach (var tetrahedron in Tetrahedra)
			{
				var insideCount = 0; var outsideCount = 0;
				var insideCenter = Vector3.Zero; var outsideCenter = Vector3.Zero;
				foreach (var i in tetrahedron)
				{
					if (values[ids[i]] > 0) { inside[insideCount++] = i; insideCenter += positions[i]; }
					else { outside[outsideCount++] = i; outsideCenter += positions[i]; }
				}
				if (insideCount == 0 || outsideCount == 0) continue;
				var outward = outsideCenter / outsideCount - insideCenter / insideCount;
				if (insideCount == 2)
				{
					var a = Intersection(inside[0], outside[0]); var b = Intersection(inside[0], outside[1]);
					var c = Intersection(inside[1], outside[1]); var d = Intersection(inside[1], outside[0]);
					Add(a, b, c, outward); Add(a, c, d, outward);
				}
				else
				{
					var singleton = insideCount == 1 ? inside[0] : outside[0];
					var other = insideCount == 1 ? outside : inside;
					Add(Intersection(singleton, other[0]), Intersection(singleton, other[1]), Intersection(singleton, other[2]), outward);
				}
			}
		}
		if (output.Count == 0) throw new ArgumentException("No solid could be reconstructed. Increase repair thickness/resolution.");
		return output.ToArray();
	}

	private static Vertex Project(Vector3 p, Vector3 normal, Triangle t)
	{
		var bary = NearestTriangle.Barycentric(p, t);
		return new Vertex(p, normal, t.A.UV * bary.X + t.B.UV * bary.Y + t.C.UV * bary.Z,
			t.A.UV2 * bary.X + t.B.UV2 * bary.Y + t.C.UV2 * bary.Z,
			t.A.Color * bary.X + t.B.Color * bary.Y + t.C.Color * bary.Z);
	}

	// A small BVH keeps distance sampling and attribute projection practical.
	private sealed class NearestTriangle
	{
		public readonly record struct Hit(Triangle Triangle, float DistanceSquared);
		private sealed record Node(Aabb Bounds, int Start, int Count, Node Left = null, Node Right = null);
		private readonly Triangle[] _triangles;
		private readonly Aabb[] _bounds;
		private readonly int[] _indices;
		private readonly Node _root;
		public NearestTriangle(Triangle[] source)
		{
			_triangles = source;
			_bounds = source.Select(t => new Aabb(t.A.Position, Vector3.Zero).Expand(t.B.Position).Expand(t.C.Position)).ToArray();
			_indices = Enumerable.Range(0, source.Length).ToArray();
			_root = Build(0, source.Length);
		}
		private Node Build(int start, int count)
		{
			var bounds = _bounds[_indices[start]];
			for (var i = start + 1; i < start + count; i++) bounds = bounds.Merge(_bounds[_indices[i]]);
			if (count <= 8) return new Node(bounds, start, count);
			var axis = bounds.Size.X >= bounds.Size.Y && bounds.Size.X >= bounds.Size.Z ? 0 : bounds.Size.Y >= bounds.Size.Z ? 1 : 2;
			Array.Sort(_indices, start, count, Comparer<int>.Create((a, b) => _bounds[a].GetCenter()[axis].CompareTo(_bounds[b].GetCenter()[axis])));
			var half = count / 2;
			return new Node(bounds, start, count, Build(start, half), Build(start + half, count - half));
		}
		public Hit Find(Vector3 point)
		{
			var best = new Hit(default, float.PositiveInfinity);
			float Distance(Aabb bounds) => (point - point.Clamp(bounds.Position, bounds.End)).LengthSquared();
			void Visit(Node node)
			{
				if (Distance(node.Bounds) > best.DistanceSquared) return;
				if (node.Left != null)
				{
					if (Distance(node.Left.Bounds) < Distance(node.Right.Bounds)) { Visit(node.Left); Visit(node.Right); }
					else { Visit(node.Right); Visit(node.Left); }
					return;
				}
				for (var i = node.Start; i < node.Start + node.Count; i++)
				{
					var t = _triangles[_indices[i]]; var b = Barycentric(point, t);
					var distance = point.DistanceSquaredTo(t.A.Position * b.X + t.B.Position * b.Y + t.C.Position * b.Z);
					if (distance < best.DistanceSquared) best = new Hit(t, distance);
				}
			}
			Visit(_root); return best;
		}
		// Closest point on a triangle, expressed as barycentric weights (Ericson).
		public static Vector3 Barycentric(Vector3 p, Triangle t)
		{
			var a = t.A.Position; var b = t.B.Position; var c = t.C.Position;
			var ab = b - a; var ac = c - a; var ap = p - a;
			var d1 = ab.Dot(ap); var d2 = ac.Dot(ap);
			if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);
			var bp = p - b; var d3 = ab.Dot(bp); var d4 = ac.Dot(bp);
			if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
			var vc = d1 * d4 - d3 * d2;
			if (vc <= 0 && d1 >= 0 && d3 <= 0) { var v = d1 / (d1 - d3); return new Vector3(1 - v, v, 0); }
			var cp = p - c; var d5 = ab.Dot(cp); var d6 = ac.Dot(cp);
			if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);
			var vb = d5 * d2 - d1 * d6;
			if (vb <= 0 && d2 >= 0 && d6 <= 0) { var w = d2 / (d2 - d6); return new Vector3(1 - w, 0, w); }
			var va = d3 * d6 - d5 * d4;
			if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { var w = (d4 - d3) / (d4 - d3 + d5 - d6); return new Vector3(0, 1 - w, w); }
			var inverse = 1 / (va + vb + vc);
			var beta = vb * inverse; var gamma = vc * inverse;
			return new Vector3(1 - beta - gamma, beta, gamma);
		}
	}
}
