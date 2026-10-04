using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Destructibles.Tessellation.Double;

namespace Destructibles;

// Even-odd winding preserves holes and nested islands.
internal static class CapTriangulator
{
	public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangulate(
		List<Vector3[]> contours, Vector3 origin, Vector3 tangent, Vector3 bitangent,
		float epsilon, CancellationToken cancellation)
	{
		var tess = new Tess { NoEmptyPolygons = true };
		var points = new List<Vector3>();
		var neighbours = new Dictionary<int, (int Previous, int Next)>();
		foreach (var contour in contours)
		{
			cancellation.ThrowIfCancellationRequested();
			var start = points.Count;
			points.AddRange(contour);
			for (var i = 0; i < contour.Length; i++)
				neighbours[start + i] = (start + (i + contour.Length - 1) % contour.Length, start + (i + 1) % contour.Length);
			// Simplify collinear points here; restore the boundary after tessellation.
			var outline = new LinkedList<int>(Enumerable.Range(start, contour.Length));
			var current = outline.First;
			var visited = 0;
			while (outline.Count > 3 && visited < outline.Count)
			{
				cancellation.ThrowIfCancellationRequested();
				var previous = current.Previous ?? outline.Last;
				var next = current.Next ?? outline.First;
				var direction = points[next.Value] - points[previous.Value];
				var weight = direction.Dot(points[current.Value] - points[previous.Value]) / direction.LengthSquared();
				if (weight > 0 && weight < 1 && points[current.Value].DistanceSquaredTo(
					points[previous.Value] + direction * weight) <= epsilon * epsilon)
				{
					outline.Remove(current); current = previous; visited = 0;
				}
				else { current = next; visited++; }
			}
			tess.AddContour(outline.Select(index => new ContourVertex(new Vec3(
				tangent.Dot(points[index] - origin), bitangent.Dot(points[index] - origin), 0), index)).ToArray());
		}
		// Intersecting contours would also require repairing the exterior faces.
		tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3,
			(_, _, _) => throw new ArgumentException("Cut contours intersect or touch. Repair the mesh or move the cut plane."),
			new Vec3(0, 0, 1));
		if (tess.ElementCount == 0) throw new ArgumentException("The cut contours have no triangulatable area.");
		var triangles = new List<(int A, int B, int C)>();
		var edges = new Dictionary<(int, int), int>();
		(int, int) Key(int a, int b) => (Math.Min(a, b), Math.Max(a, b));
		for (var i = 0; i < tess.ElementCount; i++)
		{
			cancellation.ThrowIfCancellationRequested();
			var a = (int)tess.Vertices[tess.Elements[i * 3]].Data;
			var b = (int)tess.Vertices[tess.Elements[i * 3 + 1]].Data;
			var c = (int)tess.Vertices[tess.Elements[i * 3 + 2]].Data;
			if ((points[b] - points[a]).Cross(points[c] - points[a]).Dot(tangent.Cross(bitangent)) < 0) (b, c) = (c, b);
			triangles.Add((a, b, c));
			foreach (var edge in new[] { Key(a, b), Key(b, c), Key(c, a) }) edges[edge] = edges.GetValueOrDefault(edge) + 1;
		}
		// Restore omitted boundary vertices so the cap stays watertight.
		var chains = new Dictionary<(int, int), List<int>>();
		var boundary = new HashSet<(int, int)>();
		foreach (var (edge, count) in edges)
		{
			cancellation.ThrowIfCancellationRequested();
			if (count > 2) throw new ArgumentException("The cap triangulation is non-manifold.");
			if (count != 1) continue;
			var (a, b) = edge;
			var chain = Follow(a, b, neighbours[a].Next) ?? Follow(a, b, neighbours[a].Previous);
			if (chain == null) throw new ArgumentException("The cap boundary does not match the cut contours.");
			if (chain.Count > 0) chains[edge] = chain;
			var previous = a;
			foreach (var next in chain.Append(b)) { boundary.Add(Key(previous, next)); previous = next; }
		}
		if (boundary.Count != neighbours.Count || neighbours.Any(pair => !boundary.Contains(Key(pair.Key, pair.Value.Next))))
			throw new ArgumentException("Triangulation lost part of a cut contour. The feature may be too small for the mesh tolerance.");
		foreach (var (a, b, c) in triangles)
		{
			cancellation.ThrowIfCancellationRequested();
			var polygon = new List<Vector3>();
			foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
			{
				polygon.Add(points[from]);
				if (chains.TryGetValue(Key(from, to), out var chain))
					polygon.AddRange((from < to ? chain : chain.AsEnumerable().Reverse()).Select(index => points[index]));
			}
			if (polygon.Count == 3) yield return (points[a], points[b], points[c]);
			else
			{
				var center = (points[a] + points[b] + points[c]) / 3;
				for (var i = 0; i < polygon.Count; i++) yield return (polygon[i], polygon[(i + 1) % polygon.Count], center);
			}
		}

		List<int> Follow(int a, int b, int current)
		{
			var chain = new List<int>();
			var previous = a;
			var direction = points[b] - points[a];
			while (current != b)
			{
				cancellation.ThrowIfCancellationRequested();
				var weight = direction.Dot(points[current] - points[a]) / direction.LengthSquared();
				if (current == a || weight <= 0 || weight >= 1 ||
					points[current].DistanceSquaredTo(points[a] + direction * weight) > epsilon * epsilon) return null;
				chain.Add(current);
				var next = neighbours[current].Next == previous ? neighbours[current].Previous : neighbours[current].Next;
				previous = current; current = next;
			}
			return chain;
		}
	}
}
