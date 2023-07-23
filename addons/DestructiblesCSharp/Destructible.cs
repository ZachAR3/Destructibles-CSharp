global using Godot;
global using System.Linq;
global using System.Threading.Tasks;

namespace Destructibles;

[Tool]
public partial class Destructible : Node
{
	[Export()] private PackedScene _fragmented;

	private PackedScene Fragmented
	{
		get => _fragmented;
		set => SetFragmented(value);
	}

	private PackedScene _shard;

	private PackedScene Shard
	{
		get => _shard;
		set => SetShard(value);
	}

	[Export]
	private Node _shardContainer;

	private Node ShardContainer
	{
		get => _shardContainer;
		set => SetShardContainer(value);
	}

	[ExportGroup("Animation")]
	[Export] private float _fadeDelay = 2f;
	[Export] private float _shrinkDelay = 2f;
	[Export] private bool _particleFade = true;

	[ExportGroup("Collision")]
	[Export(PropertyHint.Layers3DPhysics)] private uint _collisionLayers = 1;
	[Export(PropertyHint.Layers3DPhysics)] private uint _layerMasks = 1;


	[ExportGroup("Generation")]
	[Export()] public bool GenerateShards
	{
		get => false;
		set
		{
			if (value)
			{
				_saveToScene = true;
				GD.PrintRich("[color=yellow]Generation started.[/color]");
				Destroy();
			}
		}
	}

	[Export()] private bool _preloadShards = true;

	[Export(PropertyHint.Dir)] private string _savePath = "res://shard";

	[Export()] private bool _cleanCollisionMesh = true;

	[Export()] private bool _simplifyCollisionMesh = false;

	[Export()] private PackedScene _preGeneratedShards;

	[Export()] private float _shardMass = 1f;

	[Export()] private float _linearDampening = 0f;
	[Export()] private RigidBody3D.DampMode _linearDampMode = RigidBody3D.DampMode.Combine;

	[Export()] private float _angularDampening = 0f;
	[Export()] private RigidBody3D.DampMode _angularDampMode = RigidBody3D.DampMode.Combine;

	private bool _saveToScene;
	private Vector3 _scale = Vector3.One;
	private Node3D _shards;
	private Node3D _fragmentedInstance;

	public override void _Ready()
	{
		_shardContainer = GetNodeOrNull("../../");
		_scale = GetParent<Node3D>().Scale;

		_shard = (PackedScene)GD.Load("res://addons/DestructiblesCSharp/shard.tscn");
		// If preloading shards is enabled instances the correct shards for either dynamic generated or pre-generated shards.
		if (_preGeneratedShards == null && _preloadShards)
		{
			if (_fragmented == null)
			{
				GD.PrintErr("No fragment scene found");
				return;
			}
			_fragmentedInstance = _fragmented.Instantiate() as Node3D;
		}
		else if (_preloadShards)
		{
			_shards = _preGeneratedShards.Instantiate<Node3D>();
		}
	}


	// Destroy function to be called when destroying an object (Also used to handle pre-generation of shards)
	private async void Destroy(float explosionPower = 4f, Vector3 explosionDirection = default(Vector3))
	{
		_shard = (PackedScene)GD.Load("res://addons/DestructiblesCSharp/shard.tscn");
		// Checks if a pre-generated shard scene is given, if not generates the shards with the given options.
		if (_preGeneratedShards == null)
		{
			// Checks if shards are preloaded, if not loads them
			if (!IsInstanceValid(_fragmentedInstance) || _fragmentedInstance == null)
			{
				if (_fragmented != null)
				{
					_fragmentedInstance = _fragmented.Instantiate() as Node3D;
				}
				else
				{
					GD.PrintErr("No fragmented scene given, aborting!");
					return;
				}
			}

			var destructionUtils = new DestructibleUtils();

			_shards = await destructionUtils.CreateShards(new ShardSettings
			{
				Obj = _fragmentedInstance,
				ShardScene = _shard, 
				CollisionLayers = _collisionLayers, 
				CollisionMasks = _layerMasks, 
				ExplosionPower = explosionPower, 
				ExplosionDirection = explosionDirection, 
				ShardMass = _shardMass, 
				FadeDelay = _fadeDelay,
				ShrinkDelay = _shrinkDelay, 
				ParticleFade = _particleFade, 
				SaveToScene = _saveToScene, 
				LinearDampening = _linearDampening, 
				LinearDampMode = _linearDampMode,
				AngularDampening = _angularDampening, 
				AngularDampMode = _angularDampMode, 
				SaveDirectory = _savePath, 
				CleanCollisionMesh = _cleanCollisionMesh,
				SimplifyCollisionMesh = _simplifyCollisionMesh, 
				Scale = _scale
			});

			destructionUtils.QueueFree(); // Necessary to avoid orphan nodes
			if (_saveToScene)
			{
				return;
			}
		}
		else
		{
			// Checks if shards are preloaded, if not loads them
			if (!_preloadShards)
			{
				_shards = _preGeneratedShards.Instantiate<Node3D>();
			}
		
			// Sets the variables on each shard that would otherwise be set when generating the shards dynamically.
			foreach (Node shardNode in _shards.GetChildren())
			{
				var shard = shardNode as Shard;
		
				shard.CollisionLayer = _collisionLayers;
				shard.CollisionMask = _layerMasks;
				shard.FadeDelay = _fadeDelay;
				shard.ExplosionPower = explosionPower;
				shard.ExplosionDirection = explosionDirection;
				shard.Mass = _shardMass;
				shard.ShrinkDelay = _shrinkDelay;
				shard.ParticleFade = _particleFade;
				shard.LinearDamp = _linearDampening;
				shard.LinearDampMode = _linearDampMode;
				shard.AngularDamp = _angularDampening;
				shard.AngularDampMode = _angularDampMode;
			}
		}

		// Called as deferred so it is run from the main thread for safety.
		Callable.From(() =>
		{
			_shards.TopLevel = true;
			_shardContainer.AddChild(_shards);
			_shards.GlobalRotation = GetParent<Node3D>().GlobalRotation;
			_shards.GlobalPosition = GetParent<Node3D>().GlobalPosition;

			// Necessary to avoid orphan nodes
			GetParent().QueueFree();
		}).CallDeferred();
	}


	// Sets the fragmented value to the one set in the editor, and checks for errors, if so issuing a warning
	private void SetFragmented(PackedScene to)
	{
		_fragmented = to;

		if (IsInsideTree())
			UpdateConfigurationWarnings();

		_Ready();
	}


	// Sets the Shard value to the one set in the editor, and checks for errors, if so issuing a warning
	private void SetShard(PackedScene to)
	{
		_shard = to;

		if (IsInsideTree())
			UpdateConfigurationWarnings();

		_Ready();
	}


	// Sets the Shard Container value to the one set in the editor, and checks for errors, if so issuing a warning
	private void SetShardContainer(Node to)
	{
		_shardContainer = to;

		if (IsInsideTree())
			UpdateConfigurationWarnings();

		_Ready();
	}


	// Run when an above function issues a warning, passes this warning on to the user.
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new string[] { };

		if (_fragmented == null)
			warnings.Append("No fragmented version set");

		if (_shard == null)
			warnings.Append("No shard template set");

		if (_shardContainer is PhysicsBody3D || _hasParentOfType(_shardContainer))
			warnings.Append
				("The shard container is a PhysicsBody or has a PhysicsBody " +
				"as a parent. This will make the shards added to it behave " +
				"in unexpected ways.");

		return base._GetConfigurationWarnings();
	}

	// Simple function to see if a parent of a given node is a curtain type.
	static bool _hasParentOfType(Node node)
	{
		if (node.GetParent() == null)
			return false;

		if (node.GetParent() is PhysicsBody3D)
			return true;

		return _hasParentOfType(node.GetParent());
	}
}
