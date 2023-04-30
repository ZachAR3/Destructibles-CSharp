namespace Destructibles;

#if TOOLS
[Tool]
public partial class DestructiblePlugin : EditorPlugin
{
	public override void _EnterTree()
	{
		var destructionScript = GD.Load<Script>("res://addons/DestructiblesCSharp/Destructible.cs");
		var destructionTexture = GD.Load<Texture2D>("res://addons/DestructiblesCSharp/Destructible.svg");
		AddCustomType("Destructible", "Node", destructionScript, destructionTexture);
	}

	public override void _ExitTree() => RemoveCustomType("Destructible");
}
#endif
