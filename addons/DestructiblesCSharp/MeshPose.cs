using Godot;
using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Destructibles;

// Snapshot the current pose into copied arrays on the main thread.
internal static class MeshPose
{
	private sealed class FinalPose
	{
		public Transform3D[] Bones;
	}
	private static readonly ConditionalWeakTable<Skeleton3D, FinalPose> FinalPoses = new();

	public static void Watch(MeshInstance3D source)
	{
		var skeleton = source.GetNodeOrNull<Skeleton3D>(source.Skeleton);
		if (skeleton == null || FinalPoses.TryGetValue(skeleton, out _)) return;
		var pose = new FinalPose();
		FinalPoses.Add(skeleton, pose);
		// This signal runs after IK/modifiers, before Godot restores the CPU poses.
		void Capture()
		{
			if (!skeleton.GetChildren().OfType<SkeletonModifier3D>().Any(m => m.Active))
			{ pose.Bones = null; return; }
			var count = skeleton.GetBoneCount();
			if (pose.Bones?.Length != count) pose.Bones = new Transform3D[count];
			for (var i = 0; i < count; i++) pose.Bones[i] = skeleton.GetBoneGlobalPose(i);
		}
		void Release()
		{
			skeleton.SkeletonUpdated -= Capture;
			skeleton.TreeExiting -= Release;
			FinalPoses.Remove(skeleton);
		}
		skeleton.SkeletonUpdated += Capture;
		skeleton.TreeExiting += Release;
	}

	public static void Apply(MeshInstance3D source, int surface, Godot.Collections.Array arrays,
		Vector3[] positions, Vector3[] normals)
	{
		if (source.Mesh is ArrayMesh mesh && mesh.GetBlendShapeCount() > 0)
		{
			var shapes = mesh.SurfaceGetBlendShapeArrays(surface);
			var original = (Vector3[])positions.Clone();
			var originalNormals = (Vector3[])normals.Clone();
			for (var s = 0; s < mesh.GetBlendShapeCount(); s++)
			{
				var weight = source.GetBlendShapeValue(s);
				if (Mathf.IsZeroApprox(weight)) continue;
				var shape = shapes[s];
				var vertices = shape[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				var n = shape[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil
					? Array.Empty<Vector3>() : shape[(int)Mesh.ArrayType.Normal].AsVector3Array();
				var relative = mesh.BlendShapeMode == Mesh.BlendShapeMode.Relative;
				for (var i = 0; i < positions.Length; i++)
				{
					positions[i] += (vertices[i] - (relative ? Vector3.Zero : original[i])) * weight;
					if (i < normals.Length && i < n.Length)
						normals[i] += (n[i] - (relative ? Vector3.Zero : originalNormals[i])) * weight;
				}
			}
		}
		if (arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil) return;
		var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
		if (bones.Length == 0) return;
		var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
		var influences = positions.Length == 0 ? 0 : bones.Length / positions.Length;
		if ((influences != 4 && influences != 8) || weights.Length != bones.Length)
			throw new ArgumentException("The rig has invalid bone/weight arrays.");
		var skeleton = source.GetNodeOrNull<Skeleton3D>(source.Skeleton);
		if (skeleton == null) throw new ArgumentException("A weighted mesh needs a valid Skeleton path to capture its pose.");
		Watch(source);
		FinalPoses.TryGetValue(skeleton, out var finalPose);
		var modified = skeleton.GetChildren().OfType<SkeletonModifier3D>().Any(m => m.Active) ? finalPose?.Bones : null;
		var reference = source.GetSkinReference();
		var skin = source.Skin ?? reference?.GetSkin();
		var ownsSkin = skin == null;
		if (ownsSkin) skin = skeleton.CreateSkinFromRestTransforms();
		try
		{
			var transforms = new Transform3D[skin.GetBindCount()];
			for (var i = 0; i < transforms.Length; i++)
			{
				var name = skin.GetBindName(i);
				var bone = name.IsEmpty ? skin.GetBindBone(i) : skeleton.FindBone(name);
				if (bone < 0 || bone >= skeleton.GetBoneCount()) throw new ArgumentException("A skin bind refers to a missing skeleton bone.");
				transforms[i] = (modified != null && bone < modified.Length ? modified[bone]
					: skeleton.GetBoneGlobalPose(bone)) * skin.GetBindPose(i);
			}
			for (var i = 0; i < positions.Length; i++)
			{
				var position = positions[i];
				var normal = i < normals.Length ? normals[i] : Vector3.Zero;
				for (var w = 0; w < influences; w++)
				{
					var index = i * influences + w;
					var weight = weights[index];
					if (weight <= 0) continue;
					var bind = bones[index];
					if (bind < 0 || bind >= transforms.Length) throw new ArgumentException("A vertex refers to a missing skin bind.");
					positions[i] += (transforms[bind] * position - position) * weight;
					if (i < normals.Length) normals[i] += (transforms[bind].Basis * normal - normal) * weight;
				}
				if (i < normals.Length) normals[i] = normals[i].Normalized();
			}
		}
		finally { if (ownsSkin) skin.Dispose(); }
	}
}
