using Godot;
using System;
using System.Linq;
using Godot.Collections;
using Godot.NativeInterop;
using Array = Godot.Collections.Array;


[Tool]
public partial class Destruction : Node
{
	[Export()]
	private PackedScene _fragmented;

	private PackedScene Fragmented
	{
		get => _fragmented;
		set => SetFragmented(value);
	}

	[Export] private PackedScene _shard;

	private PackedScene Shard
	{
		get => _shard;
		set => SetShard(value);
	}
	
	[Export] private Node _shardContainer;
	
	private Node ShardContainer
	{
		get => _shardContainer;
		set => SetShardContainer(value);
	}

	[ExportGroup("Animation")] 
	[Export] private float _fadeDelay = 2f;
	[Export] private float _shrinkDelay = 2f;

	[ExportGroup("Collision")] 
	[Export(PropertyHint.Layers2DPhysics)]
	private uint _collisionLayers = 1;
	[Export(PropertyHint.Layers2DPhysics)]
	private uint _layerMasks = 1;

	
	[ExportGroup("Generation")]
	[Export()]
	public bool GenerateShards
	{
		get => false;
		set
		{
			if (value)
			{
				_saveToScene = true;
				Destroy();
			}
		}
	}

	[Export()] private string _savePath = "res://shard";

	[Export()] private bool _cleanCollisionMesh = true;
	
	[Export()] private bool _simplifyCollisionMesh = false;

	[Export()] private bool _preloadShards = true;

	[Export()] private PackedScene _preGeneratedShards;

	private bool _saveToScene;
	private Node3D _shards;


	public override void _Ready()
	{
		_shardContainer = GetNode("../../");

		if (_preGeneratedShards == null && _preloadShards)
		{
			_shard = (PackedScene)GD.Load("res://addons/DestructionPluginCSharp/shard.tscn");
		}
		else if (_preloadShards)
		{
			_shards = _preGeneratedShards.Instantiate<Node3D>();
		}
	}


	private async void Destroy(float explosionPower = 4f)
	{
		if (_preGeneratedShards == null)
		{
			if (!_preloadShards)
			{
				_shard = (PackedScene)GD.Load("res://addons/DestructionPluginCSharp/shard.tscn");
			}
			DestructionUtils destructionUtils = new DestructionUtils();
			_shards = await destructionUtils.CreateShards(_fragmented.Instantiate() as Node3D, 
				_shard, _collisionLayers, _layerMasks, explosionPower, _fadeDelay, _shrinkDelay, _saveToScene, 
				_savePath, _cleanCollisionMesh, _simplifyCollisionMesh);
			destructionUtils.QueueFree();
			if (_saveToScene)
			{
				return;
			}
		}
		else
		{
			if (!_preloadShards)
			{
				_shards = _preGeneratedShards.Instantiate<Node3D>();
			}
			
			foreach (Node shardNode in _shards.GetChildren())
			{
				Shard shard = shardNode as Shard;
				
				shard.CollisionLayer = _collisionLayers;
				shard.CollisionMask = _layerMasks;
				shard.FadeDelay = _fadeDelay;
				shard.ExplosionPower = explosionPower;
				shard.ShrinkDelay = _shrinkDelay;
			}
		}
		
		_shardContainer.AddChild(_shards);
		Transform3D shardsGlobalTransform = _shards.GlobalTransform;
		shardsGlobalTransform.Origin = GetParent<Node3D>().GlobalTransform.Origin;
		_shards.GlobalTransform = shardsGlobalTransform;
		_shards.TopLevel = true;
		GetParent().QueueFree();
	}
	

	private void SetFragmented(PackedScene to)
	{
		_fragmented = to;
		if (IsInsideTree())
		{
			GetTree().EmitSignal("node_configuration_warning_changed", this);
		}
	}
	

	private void SetShard(PackedScene to)
	{
		_shard = to;
		if (IsInsideTree())
		{
			GetTree().EmitSignal("node_configuration_warning_changed", this);
		}
	}
	
	
	public override string[] _GetConfigurationWarnings()
	{
		string[] warnings = {};

		if (_fragmented == null)
		{
			warnings.Append("No fragmented version set");
		}
		
		if (_shard == null)
		{
			warnings.Append("No shard template set");
		}
		
		if (_shardContainer is PhysicsBody3D || _hasParentOfType(_shardContainer))
		{
			warnings.Append(
				"The shard container is a PhysicsBody or has a PhysicsBody as a parent. This will make the shards added to it behave in unexpected ways.");
		} 
		return base._GetConfigurationWarnings();
	}


	static bool _hasParentOfType(Node node)
	{
		if (node.GetParent() == null)
		{
			return false;
		}

		if (node.GetParent() is PhysicsBody3D)
		{
			return true;
		}

		return _hasParentOfType(node.GetParent());
	}
	private void SetShardContainer(Node to)
	{
		_shardContainer = to;
		if (IsInsideTree())
		{
			GetTree().EmitSignal("node_configuration_warning_changed", this);
		}
	}
}
