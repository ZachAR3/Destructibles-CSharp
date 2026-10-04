using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Destructibles;

// Mesh resources stay on the main thread; geometry runs on a worker.
public static class MeshFracture
{
	public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 UV, Vector2 UV2, Color Color)
	{
		public Vertex Lerp(Vertex other, float weight) => new(Position.Lerp(other.Position, weight),
			Normal.Lerp(other.Normal, weight).Normalized(), UV.Lerp(other.UV, weight),
			UV2.Lerp(other.UV2, weight), Color.Lerp(other.Color, weight));
	}
	public readonly record struct Triangle(Vertex A, Vertex B, Vertex C, int Surface);
	public sealed record Snapshot(Triangle[] Triangles, Material[] Materials);
	public sealed record Fragment(ArrayMesh Mesh, Vector3 Center);
	private readonly record struct Point(long X, long Y, long Z);
	private readonly record struct Edge(int A, int B);

	public static Snapshot Read(Mesh mesh, Transform3D transform, Material materialOverride = null,
		MeshInstance3D posedSource = null)
	{
		if (mesh == null) throw new ArgumentException("No source mesh was assigned.");
		if (posedSource == null && mesh is ArrayMesh animated && animated.GetBlendShapeCount() > 0)
			throw new NotSupportedException("Fracture requires a static mesh without blend shapes.");
		if (transform.Basis.Determinant() == 0 || !float.IsFinite(transform.Basis.Determinant()))
			throw new ArgumentException("Cannot fracture a mesh with zero scale.");
		var triangles = new List<Triangle>();
		var materials = new List<Material>();
		var normalTransform = transform.Basis.Inverse().Transposed();
		var mirrored = transform.Basis.Determinant() < 0;
		for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
		{
			if (mesh is ArrayMesh arrayMesh && arrayMesh.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
				throw new NotSupportedException("Fracture requires triangle mesh surfaces.");
			var arrays = mesh.SurfaceGetArrays(surface);
			if (posedSource == null && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil && arrays[(int)Mesh.ArrayType.Bones].AsInt32Array().Length > 0)
				throw new NotSupportedException("Fracture requires a static mesh without skinning weights.");
			var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			var normals = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil
				? Array.Empty<Vector3>() : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
			if (posedSource != null) MeshPose.Apply(posedSource, surface, arrays, positions, normals);
			var uv = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil
				? Array.Empty<Vector2>() : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
			var uv2 = arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil
				? Array.Empty<Vector2>() : arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
			var colors = arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil
				? Array.Empty<Color>() : arrays[(int)Mesh.ArrayType.Color].AsColorArray();
			var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
				? Enumerable.Range(0, positions.Length).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
			if (indices.Length % 3 != 0) throw new ArgumentException("A mesh surface is not triangulated.");
			Vertex Get(int index) => new(transform * positions[index],
				index < normals.Length ? (normalTransform * normals[index]).Normalized() : Vector3.Zero,
				index < uv.Length ? uv[index] : Vector2.Zero, index < uv2.Length ? uv2[index] : Vector2.Zero,
				index < colors.Length ? colors[index] : Colors.White);
			for (var i = 0; i < indices.Length; i += 3)
			{
				var a = Get(indices[i]);
				var b = Get(indices[i + 1]);
				var c = Get(indices[i + 2]);
				if (mirrored) (b, c) = (c, b);
				if (normals.Length == 0)
				{
					var normal = (c.Position - a.Position).Cross(b.Position - a.Position).Normalized();
					a = a with { Normal = normal }; b = b with { Normal = normal }; c = c with { Normal = normal };
				}
				triangles.Add(new Triangle(a, b, c, surface));
			}
			materials.Add(materialOverride ?? mesh.SurfaceGetMaterial(surface));
		}
		if (triangles.Count == 0) throw new ArgumentException("The source mesh has no triangles.");
		var data = triangles.ToArray();
		var epsilon = Tolerance(data);
		// Ignore collapsed pole/cap triangles from Godot primitives.
		data = data.Where(t => t.A.Position.DistanceSquaredTo(t.B.Position) > epsilon * epsilon &&
			t.B.Position.DistanceSquaredTo(t.C.Position) > epsilon * epsilon &&
			t.C.Position.DistanceSquaredTo(t.A.Position) > epsilon * epsilon &&
			(t.B.Position - t.A.Position).Cross(t.C.Position - t.A.Position).LengthSquared() > epsilon * epsilon * epsilon * epsilon).ToArray();
		if (data.Length == 0) throw new ArgumentException("The source mesh has no non-degenerate triangles.");
		return new Snapshot(data, materials.ToArray());
	}

	public static Fragment Build(Triangle[] triangles, Material[] materials, bool recenter = true)
	{
		var center = recenter ? Bounds(triangles).GetCenter() : Vector3.Zero;
		var mesh = new ArrayMesh();
		foreach (var surface in triangles.GroupBy(triangle => triangle.Surface).OrderBy(group => group.Key))
		{
			var vertices = new Vertex[surface.Count() * 3];
			var index = 0;
			foreach (var triangle in surface)
			{
				vertices[index++] = triangle.A; vertices[index++] = triangle.B; vertices[index++] = triangle.C;
			}
			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = vertices.Select(vertex => vertex.Position - center).ToArray();
			arrays[(int)Mesh.ArrayType.Normal] = vertices.Select(vertex => vertex.Normal).ToArray();
			arrays[(int)Mesh.ArrayType.TexUV] = vertices.Select(vertex => vertex.UV).ToArray();
			arrays[(int)Mesh.ArrayType.TexUV2] = vertices.Select(vertex => vertex.UV2).ToArray();
			arrays[(int)Mesh.ArrayType.Color] = vertices.Select(vertex => vertex.Color).ToArray();
			arrays[(int)Mesh.ArrayType.Tangent] = Tangents(vertices);
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			if (surface.Key < materials.Length) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materials[surface.Key]);
		}
		return new Fragment(mesh, center);
	}

	private static float[] Tangents(Vertex[] vertices)
	{
		var result = new float[vertices.Length * 4];
		for (var i = 0; i < vertices.Length; i += 3)
		{
			var edge1 = vertices[i + 1].Position - vertices[i].Position;
			var edge2 = vertices[i + 2].Position - vertices[i].Position;
			var uv1 = vertices[i + 1].UV - vertices[i].UV;
			var uv2 = vertices[i + 2].UV - vertices[i].UV;
			var determinant = uv1.Cross(uv2);
			var tangent = Math.Abs(determinant) > 0.00000001f ? (edge1 * uv2.Y - edge2 * uv1.Y) / determinant : Vector3.Zero;
			var bitangent = Math.Abs(determinant) > 0.00000001f ? (edge2 * uv1.X - edge1 * uv2.X) / determinant : Vector3.Zero;
			for (var v = i; v < i + 3; v++)
			{
				var normal = vertices[v].Normal;
				var t = (tangent - normal * normal.Dot(tangent)).Normalized();
				if (t.IsZeroApprox()) t = normal.Cross(Math.Abs(normal.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
				result[v * 4] = t.X; result[v * 4 + 1] = t.Y; result[v * 4 + 2] = t.Z;
				result[v * 4 + 3] = normal.Cross(t).Dot(bitangent) < 0 ? -1 : 1;
			}
		}
		return result;
	}

	public static Aabb Bounds(Triangle[] triangles)
	{
		var bounds = new Aabb(triangles[0].A.Position, Vector3.Zero);
		foreach (var triangle in triangles)
		{
			bounds = bounds.Expand(triangle.A.Position).Expand(triangle.B.Position).Expand(triangle.C.Position);
		}
		return bounds;
	}

	public static double Volume(Triangle[] triangles)
	{
		// Translate close to the origin to avoid cancellation for far-away meshes.
		var origin = Bounds(triangles).GetCenter();
		double volume = 0;
		foreach (var triangle in triangles)
			volume += (triangle.A.Position - origin).Dot((triangle.B.Position - origin).Cross(triangle.C.Position - origin)) / 6.0;
		return Math.Abs(volume);
	}

	public static void Validate(Triangle[] triangles, CancellationToken cancellation = default)
	{
		if (triangles.Length == 0) throw new ArgumentException("The source mesh has no triangles.");
		if (triangles.Length > 100000) throw new NotSupportedException("Fracture is limited to 100,000 source triangles. Use a simpler mesh or bake it externally.");
		var epsilon = Tolerance(triangles);
		var weld = new Weld(epsilon);
		var edges = new Dictionary<Edge, (int Count, int Direction)>();
		Span<int> indices = stackalloc int[3];
		foreach (var triangle in triangles)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!triangle.A.Position.IsFinite() || !triangle.B.Position.IsFinite() || !triangle.C.Position.IsFinite())
				throw new ArgumentException("The source contains non-finite vertex positions.");
			indices[0] = weld.Add(triangle.A.Position); indices[1] = weld.Add(triangle.B.Position); indices[2] = weld.Add(triangle.C.Position);
			if (indices[0] == indices[1] || indices[1] == indices[2] || indices[2] == indices[0]) throw new ArgumentException("The source mesh contains degenerate triangles.");
			for (var i = 0; i < 3; i++)
			{
				var a = indices[i]; var b = indices[(i + 1) % 3];
				var edge = new Edge(Math.Min(a, b), Math.Max(a, b));
				edges.TryGetValue(edge, out var value);
				edges[edge] = (value.Count + 1, value.Direction + (a < b ? 1 : -1));
			}
		}
		if (edges.Values.Any(value => value.Count != 2 || value.Direction != 0))
			throw new ArgumentException("Fracture requires a closed mesh with consistent triangle winding. Open or non-manifold meshes must be repaired first.");
		if (Volume(triangles) <= epsilon * epsilon * epsilon)
			throw new ArgumentException("The source mesh has no solid volume.");
	}

	public static List<Triangle[]> Fracture(Triangle[] source, int pieces, Vector3 point, Vector3 normal,
		int interiorSurface, int seed = 1, CancellationToken cancellation = default, FractureSettings settings = null)
	{
		Validate(source, cancellation);
		pieces = Math.Clamp(pieces, 2, 256);
		settings ??= new FractureSettings();
		if (normal.IsZeroApprox()) normal = Vector3.Right;
		var originalVolume = Volume(source);
		var result = new List<(Triangle[] Triangles, double Volume, int Failures)> { (source, originalVolume, 0) };
		var random = new Random(seed);
		var minimumVolume = originalVolume * Mathf.Clamp(settings.MinimumVolumeRatio, 0, 0.49f);
		for (var attempt = 0; result.Count < pieces && attempt < pieces * 16; attempt++)
		{
			cancellation.ThrowIfCancellationRequested();
			var index = -1;
			for (var i = 0; i < result.Count; i++)
				if (result[i].Failures < 8 && result[i].Volume > minimumVolume * 2 &&
					(index < 0 || result[i].Volume > result[index].Volume)) index = i;
			if (index < 0) break;
			var item = result[index];
			var triangles = item.Triangles;
			var bounds = Bounds(triangles);
			var cutPoint = bounds.GetCenter().Lerp(point, settings.OriginBias);
			Vector3 cutNormal;
			switch (settings.Pattern)
			{
				case FracturePattern.Slices:
					cutNormal = settings.Axis;
					cutPoint = attempt == 0 ? point : bounds.GetCenter();
					break;
				case FracturePattern.Radial:
					// Bisect the largest wedge through the common origin, around Axis.
					var radial = bounds.GetCenter() - point;
					cutNormal = settings.Axis.Cross(radial).Normalized();
					if (cutNormal.IsZeroApprox() || item.Failures > 0)
						cutNormal = settings.Axis.Cross(RandomVector(random)).Normalized();
					cutPoint = point;
					break;
				case FracturePattern.Grid:
					cutNormal = bounds.Size.X >= bounds.Size.Y && bounds.Size.X >= bounds.Size.Z ? Vector3.Right
						: bounds.Size.Y >= bounds.Size.Z ? Vector3.Up : Vector3.Back;
					cutPoint = bounds.GetCenter();
					break;
				default:
					cutNormal = attempt == 0 ? normal.Normalized() : RandomVector(random).Normalized();
					if (attempt == 0) cutPoint = point;
					break;
			}
			var noise = NoiseVector(settings, bounds.GetCenter());
			cutNormal = (cutNormal + RandomVector(random) * settings.DirectionVariation + noise * settings.NoiseInfluence).Normalized();
			if (cutNormal.IsZeroApprox()) cutNormal = normal.Normalized();
			// Limit offsets to the middle of the piece to avoid producing tiny slivers.
			cutPoint += (RandomVector(random) * settings.SizeVariation + noise * settings.NoiseInfluence) * bounds.Size * 0.25f;
			result[index] = (triangles, item.Volume, item.Failures + 1);
			Triangle[] positive, negative;
			try
			{
				if (!Split(triangles, cutPoint, cutNormal, interiorSurface, out positive, out negative, cancellation, settings, seed)) continue;

				Validate(positive, cancellation);
				Validate(negative, cancellation);
			}
			// Retry unsuitable planes without losing completed fragments.
			catch (ArgumentException) { continue; }
			var positiveVolume = Volume(positive); var negativeVolume = Volume(negative);
			if (positiveVolume < minimumVolume || negativeVolume < minimumVolume) continue;
			result[index] = (positive, positiveVolume, 0);
			result.Add((negative, negativeVolume, 0));
		}
		return result.Select(item => item.Triangles).ToList();
	}

	private static Vector3 RandomVector(Random random) => new((float)random.NextDouble() - 0.5f,
		(float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);

	private static Vector3 NoiseVector(FractureSettings settings, Vector3 point) => settings.Noise == null ? Vector3.Zero
		: new Vector3(SampleNoise(settings, point), SampleNoise(settings, point + new Vector3(31, 73, 17)),
			SampleNoise(settings, point + new Vector3(97, 23, 53)));

	private static float SampleNoise(FractureSettings settings, Vector3 point) => Mathf.Clamp(settings.Noise.GetNoise3Dv(point), -1, 1);

	public static bool Split(Triangle[] source, Vector3 point, Vector3 normal, int interiorSurface,
		out Triangle[] positive, out Triangle[] negative, CancellationToken cancellation = default,
		FractureSettings settings = null, int seed = 1)
	{
		if (!point.IsFinite() || !normal.IsFinite() || normal.IsZeroApprox())
			throw new ArgumentException("The cut point and normal must be finite, with a non-zero normal.");
		normal = normal.Normalized();
		var epsilon = Tolerance(source);
		var front = new List<Triangle>(); var back = new List<Triangle>();
		var weld = new Weld(epsilon);
		var above = false; var below = false;
		var vertices = new Vertex[3];
		var distances = new float[3];
		var polygon = new List<Vertex>(4);
		foreach (var triangle in source)
		{
			cancellation.ThrowIfCancellationRequested();
			vertices[0] = triangle.A; vertices[1] = triangle.B; vertices[2] = triangle.C;
			var coplanar = true;
			for (var i = 0; i < 3; i++)
			{
				distances[i] = normal.Dot(vertices[i].Position - point);
				if (Math.Abs(distances[i]) <= epsilon)
				{
					vertices[i] = vertices[i] with { Position = vertices[i].Position - normal * distances[i] };
					distances[i] = 0;
				}
				coplanar &= distances[i] == 0;
				above |= distances[i] > 0; below |= distances[i] < 0;
			}
			if (coplanar)
			{
				// A face already on the plane belongs to the side containing its solid.
				var outward = (vertices[2].Position - vertices[0].Position).Cross(vertices[1].Position - vertices[0].Position);
				(outward.Dot(normal) < 0 ? front : back).Add(new Triangle(vertices[0], vertices[1], vertices[2], triangle.Surface));
				continue;
			}
			Clip(vertices, distances, true, polygon); AddPolygon(front, polygon, triangle.Surface, epsilon);
			Clip(vertices, distances, false, polygon); AddPolygon(back, polygon, triangle.Surface, epsilon);
		}
		positive = front.ToArray(); negative = back.ToArray();
		if (!above || !below) return false;
		// Existing coplanar faces cancel shared edges; only open edges need caps.
		var planeEdges = new Dictionary<Edge, int>();
		Span<Vector3> positions = stackalloc Vector3[3];
		foreach (var triangle in front)
		{
			cancellation.ThrowIfCancellationRequested();
			positions[0] = triangle.A.Position; positions[1] = triangle.B.Position; positions[2] = triangle.C.Position;
			for (var i = 0; i < 3; i++)
			{
				var a = positions[i]; var b = positions[(i + 1) % 3];
				if (Math.Abs(normal.Dot(a - point)) > epsilon || Math.Abs(normal.Dot(b - point)) > epsilon) continue;
				var from = weld.Add(a); var to = weld.Add(b);
				if (from == to) continue;
				var edge = new Edge(Math.Min(from, to), Math.Max(from, to));
				planeEdges[edge] = planeEdges.GetValueOrDefault(edge) + 1;
			}
		}
		var segments = planeEdges.Where(edge => edge.Value == 1).Select(edge => edge.Key).ToHashSet();
		// A plane between disconnected components can separate them without a cap.
		if (segments.Count == 0) return true;
		var loops = Contours(segments, weld.Positions);
		var tangent = normal.Cross(Math.Abs(normal.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
		var bitangent = normal.Cross(tangent);
		var uvScale = Math.Max(0.001f, settings?.InteriorUvScale ?? 1);
		var size = Bounds(source).Size;
		var roughness = (settings?.SurfaceRoughness ?? 0) * Math.Max(size.X, Math.Max(size.Y, size.Z));
		void AddCap(Vector3 a, Vector3 b, Vector3 c)
		{
			var n = (c - a).Cross(b - a).Normalized();
			Vertex Cap(Vector3 position, Vector3 outward) => new(position, outward,
				new Vector2(tangent.Dot(position - point), bitangent.Dot(position - point)) * uvScale, Vector2.Zero, Colors.White);
			front.Add(new Triangle(Cap(a, n), Cap(b, n), Cap(c, n), interiorSurface));
			back.Add(new Triangle(Cap(c, -n), Cap(b, -n), Cap(a, -n), interiorSurface));
		}
		foreach (var (a, b, c) in CapTriangulator.Triangulate(loops, point, tangent, bitangent, epsilon, cancellation))
		{
			if (roughness <= epsilon) { AddCap(a, b, c); continue; }
			var center = (a + b + c) / 3;
			var sample = settings.Noise != null ? SampleNoise(settings, center)
				: (float)Math.Sin(center.Dot(new Vector3(12.9898f, 78.233f, 37.719f)) + seed);
			var altitude = (b - a).Cross(c - a).Length() / Math.Max((b - a).Length(), Math.Max((c - b).Length(), (a - c).Length()));
			var displacement = sample * Math.Min(roughness, altitude * 0.2f);
			// Keep mating caps identical and back off before crossing exterior faces.
			var peak = center + normal * displacement;
			for (var retry = 0; retry < 8 && Math.Abs(displacement) > epsilon; retry++)
			{
				if (!CapHitsExterior(source, a, b, c, peak, point, normal, epsilon, cancellation)) break;
				displacement *= 0.5f;
				peak = center + normal * displacement;
				if (retry == 7) displacement = 0;
			}
			if (Math.Abs(displacement) <= epsilon) { AddCap(a, b, c); continue; }
			AddCap(a, b, peak); AddCap(b, c, peak); AddCap(c, a, peak);
		}
		positive = front.ToArray(); negative = back.ToArray();
		return true;
	}

	private static bool CapHitsExterior(Triangle[] source, Vector3 a, Vector3 b, Vector3 c, Vector3 peak,
		Vector3 point, Vector3 normal, float epsilon, CancellationToken cancellation)
	{
		var faces = new[] { (a, b, peak), (b, c, peak), (c, a, peak) };
		var bounds = new Aabb(a, Vector3.Zero).Expand(b).Expand(c).Expand(peak).Grow(epsilon);
		foreach (var triangle in source)
		{
			cancellation.ThrowIfCancellationRequested();
			var p = triangle.A.Position; var q = triangle.B.Position; var r = triangle.C.Position;
			if (!bounds.Intersects(new Aabb(p, Vector3.Zero).Expand(q).Expand(r).Grow(epsilon))) continue;
			foreach (var (x, y, z) in faces)
			{
				bool Crosses(Vector3 from, Vector3 to, Vector3 v0, Vector3 v1, Vector3 v2) =>
					SegmentTriangle(from, to, v0, v1, v2, out var hit) && Math.Abs(normal.Dot(hit - point)) > epsilon;
				if (Crosses(x, y, p, q, r) || Crosses(y, z, p, q, r) || Crosses(z, x, p, q, r) ||
					Crosses(p, q, x, y, z) || Crosses(q, r, x, y, z) || Crosses(r, p, x, y, z)) return true;
			}
		}
		return false;
	}

	private static bool SegmentTriangle(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 c, out Vector3 hit)
	{
		// A very small cutoff permits tiny faces; barycentric divisions use doubles.
		hit = default;
		var direction = to - from; var edge1 = b - a; var edge2 = c - a;
		var cross = direction.Cross(edge2);
		double determinant = edge1.Dot(cross);
		if (Math.Abs(determinant) < 1e-20) return false;
		var offset = from - a;
		double u = offset.Dot(cross) / determinant;
		if (u < -1e-6 || u > 1 + 1e-6) return false;
		var q = offset.Cross(edge1);
		double v = direction.Dot(q) / determinant;
		if (v < -1e-6 || u + v > 1 + 1e-6) return false;
		double t = edge2.Dot(q) / determinant;
		if (t < 0 || t > 1) return false;
		hit = from + direction * (float)t;
		return true;
	}

	private static void Clip(Vertex[] vertices, float[] distances, bool positive, List<Vertex> polygon)
	{
		polygon.Clear();
		for (var i = 0; i < 3; i++)
		{
			var next = (i + 1) % 3;
			var inside = positive ? distances[i] >= 0 : distances[i] <= 0;
			var nextInside = positive ? distances[next] >= 0 : distances[next] <= 0;
			if (inside) polygon.Add(vertices[i]);
			if (inside != nextInside) polygon.Add(vertices[i].Lerp(vertices[next], distances[i] / (distances[i] - distances[next])));
		}
	}

	private static void AddPolygon(List<Triangle> target, List<Vertex> polygon, int surface, float epsilon)
	{
		for (var i = 1; i + 1 < polygon.Count; i++)
			if (polygon[0].Position.DistanceSquaredTo(polygon[i].Position) > epsilon * epsilon &&
				polygon[i].Position.DistanceSquaredTo(polygon[i + 1].Position) > epsilon * epsilon &&
				polygon[i + 1].Position.DistanceSquaredTo(polygon[0].Position) > epsilon * epsilon &&
				(polygon[i].Position - polygon[0].Position).Cross(polygon[i + 1].Position - polygon[0].Position).LengthSquared() > epsilon * epsilon * epsilon * epsilon)
				target.Add(new Triangle(polygon[0], polygon[i], polygon[i + 1], surface));
	}

	private static List<Vector3[]> Contours(HashSet<Edge> segments, List<Vector3> positions)
	{
		var neighbours = new Dictionary<int, List<int>>();
		foreach (var edge in segments)
		{
			if (!neighbours.ContainsKey(edge.A)) neighbours[edge.A] = new List<int>();
			if (!neighbours.ContainsKey(edge.B)) neighbours[edge.B] = new List<int>();
			neighbours[edge.A].Add(edge.B); neighbours[edge.B].Add(edge.A);
		}
		if (neighbours.Values.Any(list => list.Count != 2))
			throw new ArgumentException("The cut does not form closed contours. Check mesh topology or move the cut slightly away from a shared vertex.");
		var unused = neighbours.Keys.ToHashSet();
		var loops = new List<Vector3[]>();
		while (unused.Count > 0)
		{
			var first = unused.First(); var current = first; var previous = -1;
			var loop = new List<Vector3>();
			do
			{
				if (!unused.Remove(current)) throw new ArgumentException("The cut contour intersects itself.");
				loop.Add(positions[current]);
				var next = neighbours[current][0] == previous ? neighbours[current][1] : neighbours[current][0];
				previous = current; current = next;
			} while (current != first);
			loops.Add(loop.ToArray());
		}
		return loops;
	}

	internal static float Tolerance(Triangle[] triangles) => Math.Max(0.0000001f, Bounds(triangles).Size.Length() * 0.000001f);

	internal sealed class Weld
	{
		private readonly float _epsilon;
		private readonly Dictionary<Point, List<int>> _buckets = new();
		public readonly List<Vector3> Positions = new();
		public Weld(float epsilon) { _epsilon = epsilon; }
		public int Add(Vector3 position)
		{
			var key = new Point((long)Math.Floor(position.X / _epsilon), (long)Math.Floor(position.Y / _epsilon), (long)Math.Floor(position.Z / _epsilon));
			for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
				if (_buckets.TryGetValue(new Point(key.X + x, key.Y + y, key.Z + z), out var bucket))
					foreach (var index in bucket)
						if (Positions[index].DistanceSquaredTo(position) <= _epsilon * _epsilon) return index;
			var result = Positions.Count; Positions.Add(position);
			if (!_buckets.ContainsKey(key)) _buckets[key] = new List<int>();
			_buckets[key].Add(result);
			return result;
		}
	}
}
