#if TOOLS
using Godot;

public partial class TestPoseModifier : SkeletonModifier3D
{
	public override void _ProcessModificationWithDelta(double delta)
	{
		GetSkeleton().SetBonePosePosition(0, Vector3.Right + Vector3.Up * 2);
	}
}
#endif
