using Godot;
using System;
using System.Collections.Generic;

namespace Destructibles;

// Appearance/lifetime logic. Only duplicate materials when fading starts.
internal sealed class ShardVisual
{
	private readonly MeshInstance3D _mesh;
	private readonly float _fadeDelay;
	private readonly float _shrinkDelay;
	private readonly bool _particleFade;
	private readonly int _surfaceCount;
	private readonly List<(BaseMaterial3D Material, Color Color)> _materials = new();
	private bool _fadeStarted;
	private float _age;

	public ShardVisual(MeshInstance3D mesh, float fadeDelay, float shrinkDelay, bool particleFade)
	{
		_mesh = mesh;
		_fadeDelay = fadeDelay;
		_shrinkDelay = shrinkDelay;
		_particleFade = particleFade;
		_surfaceCount = mesh.Mesh.GetSurfaceCount();
	}

	public float Scale { get; private set; } = 1;
	public bool Finished { get; private set; }
	public bool Animated => _fadeDelay > 0 || _shrinkDelay > 0;

	public void Update(double delta)
	{
		_age += (float)delta;
		if (_fadeDelay > 0 && _age >= _fadeDelay)
		{
			if (!_fadeStarted)
			{
				var materialOverride = _mesh.MaterialOverride;
				_mesh.MaterialOverride = null;
				for (var surface = 0; surface < _surfaceCount; surface++)
				{
					var source = materialOverride ?? _mesh.GetActiveMaterial(surface);
					// ShaderMaterial has no standard alpha property. Instance transparency
					// is the fallback (Forward+); shrinking works on every renderer.
					if (source is ShaderMaterial) { _mesh.SetSurfaceOverrideMaterial(surface, source); continue; }
					var material = source?.Duplicate() as BaseMaterial3D ?? new StandardMaterial3D();
					material.Transparency = _particleFade
						? BaseMaterial3D.TransparencyEnum.AlphaHash : BaseMaterial3D.TransparencyEnum.Alpha;
					_mesh.SetSurfaceOverrideMaterial(surface, material);
					_materials.Add((material, material.AlbedoColor));
				}
				_fadeStarted = true;
			}
			var progress = Mathf.Clamp((_age - _fadeDelay) / 2f, 0, 1);
			foreach (var (material, original) in _materials)
				material.AlbedoColor = new Color(original, original.A * (1 - progress));
			if (_materials.Count < _surfaceCount) _mesh.Transparency = progress;
		}
		if (_shrinkDelay > 0)
			Scale = 1 - Mathf.Clamp((_age - _shrinkDelay) / 2f, 0, 1);
		var finish = Math.Max(_fadeDelay > 0 ? _fadeDelay + 2 : 0,
			_shrinkDelay > 0 ? _shrinkDelay + 2 : 0);
		Finished = finish > 0 && _age >= finish;
	}
}
