using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;

public partial class Shard : RigidBody3D
{
	public float ShrinkDelay = -1;
	public float FadeDelay = -1;
	public float ExplosionPower;

	public override async void _Ready()
	{
		if (ShrinkDelay == -1f && FadeDelay == -1f)
		{
			Thread.Sleep(2000);
		}
		else
		{
			GD.Print(FadeDelay);
			await ToSignal(GetTree(), "physics_frame");
			await ToSignal(GetTree(), "physics_frame");

			MeshInstance3D  meshInstance = GetNode<MeshInstance3D>("MeshInstance");
			Material materialSurface = meshInstance.Mesh.SurfaceGetMaterial(0);
			StandardMaterial3D material = materialSurface.Duplicate() as StandardMaterial3D;
				
			if (material == null)
			{
				return;
			}
				
			meshInstance.MaterialOverride = material;
			material.Call("set_flag", "transparent", true);
			material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;

			Tween tween = CreateTween();
			ApplyImpulse(RandomDirection() * ExplosionPower, -Position.Normalized());

			if (FadeDelay > 0)
			{
				GD.Print("fade");
				tween.TweenProperty(material, "albedo_color", new Color(1, 1, 1, 0), 2).
					SetDelay(FadeDelay).
					SetTrans(Tween.TransitionType.Expo).
					SetEase(Tween.EaseType.Out);
			}
				

			if (ShrinkDelay > 0)
			{
				tween.Parallel().TweenProperty(meshInstance, "scale", Vector3.Zero, 2).SetDelay(ShrinkDelay);
			}
			await ToSignal(tween, "finished");
		}
		QueueFree();
	}

	
	static Vector3 RandomDirection()
	{
		return (new Vector3(GD.Randf(), GD.Randf(), GD.Randf()) - Vector3.One / 2.0f).Normalized() * 2.0f;
	}

}
