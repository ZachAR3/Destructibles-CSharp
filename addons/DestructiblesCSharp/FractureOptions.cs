using Godot;
using System;

namespace Destructibles;

public enum FracturePattern { Random, Slices, Radial, Grid }

[Tool, GlobalClass]
public partial class FractureOptions : Resource
{
	[Export] public FracturePattern Pattern { get; set; } = FracturePattern.Random;
	[Export] public Vector3 Axis { get; set; } = Vector3.Up;
	// Position within the source mesh's bounding box; gameplay hits supply their own origin.
	[Export] public Vector3 Origin { get; set; } = Vector3.One * 0.5f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float OriginBias { get; set; } = 0.2f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float SizeVariation { get; set; }
	[Export(PropertyHint.Range, "0,1,0.01")] public float DirectionVariation { get; set; }
	[Export(PropertyHint.Range, "0,0.49,0.0001")] public float MinimumVolumeRatio { get; set; } = 0.0001f;
	[ExportGroup("Noise")]
	[Export] public FastNoiseLite Noise { get; set; }
	[Export(PropertyHint.Range, "0,1,0.01")] public float NoiseInfluence { get; set; } = 0.25f;
	[ExportGroup("Interior")]
	// Fraction of the current piece's longest dimension, conservatively limited locally.
	[Export(PropertyHint.Range, "0,0.25,0.001")] public float SurfaceRoughness { get; set; }
	[Export(PropertyHint.Range, "0.001,100,0.01,or_greater")] public float InteriorUvScale { get; set; } = 1;

	// Copy noise on the main thread so edits cannot affect a running job.
	public FractureSettings Snapshot() => new()
	{
		Pattern = Enum.IsDefined(Pattern) ? Pattern : FracturePattern.Random,
		Axis = Axis.IsZeroApprox() ? Vector3.Up : Axis.Normalized(),
		Origin = Origin.Clamp(Vector3.Zero, Vector3.One),
		OriginBias = Mathf.Clamp(OriginBias, 0, 1),
		SizeVariation = Mathf.Clamp(SizeVariation, 0, 1),
		DirectionVariation = Mathf.Clamp(DirectionVariation, 0, 1),
		MinimumVolumeRatio = Mathf.Clamp(MinimumVolumeRatio, 0, 0.49f),
		Noise = Noise?.Duplicate() as FastNoiseLite,
		NoiseInfluence = Mathf.Clamp(NoiseInfluence, 0, 1),
		SurfaceRoughness = Mathf.Clamp(SurfaceRoughness, 0, 0.25f),
		InteriorUvScale = Math.Max(0.001f, InteriorUvScale)
	};
}

// Dispose the private noise copy after awaiting the worker.
public sealed record FractureSettings : IDisposable
{
	public FracturePattern Pattern { get; init; }
	public Vector3 Axis { get; init; } = Vector3.Up;
	public Vector3 Origin { get; init; } = Vector3.One * 0.5f;
	public float OriginBias { get; init; } = 0.2f;
	public float SizeVariation { get; init; }
	public float DirectionVariation { get; init; }
	public float MinimumVolumeRatio { get; init; } = 0.0001f;
	public FastNoiseLite Noise { get; init; }
	public float NoiseInfluence { get; init; } = 0.25f;
	public float SurfaceRoughness { get; init; }
	public float InteriorUvScale { get; init; } = 1;
	public void Dispose() => Noise?.Dispose();
}
