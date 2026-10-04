using Godot;

namespace Destructibles;

[Tool]
public partial class ShardGroup : Node3D
{
	internal bool Complete;
	public override void _Process(double delta)
	{
		if (!Engine.IsEditorHint() && Complete && GetChildCount() == 0) QueueFree();
	}
}
