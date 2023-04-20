#if TOOLS
using Godot;
using System;

[Tool]
public partial class DestructionPlugin : EditorPlugin
{
	public override void _EnterTree()
	{
		Script destructionScript = GD.Load<Script>("res://addons/DestructionPluginCSharp/Destruction.cs");
		Texture2D destructionTexture = GD.Load<Texture2D>("res://addons/DestructionPluginCSharp/Destruction.svg");
		AddCustomType("Destruction", "Node", destructionScript, destructionTexture);
	}

	public override void _ExitTree()
	{
		RemoveCustomType("Destruction");
	}
}
#endif
