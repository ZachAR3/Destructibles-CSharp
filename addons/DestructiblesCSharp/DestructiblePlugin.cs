#if TOOLS
using Godot;

[Tool]
public partial class DestructiblePlugin : EditorPlugin
{
	public override void _EnterTree()
	{
		Script destructionScript = GD.Load<Script>("res://addons/DestructiblesCSharp/Destructible.cs");
		Texture2D destructionTexture = GD.Load<Texture2D>("res://addons/DestructiblesCSharp/Destructible.svg");
		AddCustomType("Destructible", "Node", destructionScript, destructionTexture);
	}

	public override void _ExitTree()
	{
		RemoveCustomType("Destructible");
	}
}
#endif
