using System.Threading.Tasks;
using Godot;


// Used to generate shards both dynamically and statically (for pre-generated use-cases)
[Tool]
public partial class DestructibleUtils : Node
{
	public async Task<Node3D> CreateShards(Node3D obj, PackedScene shardScene, uint collisionLayers,
		uint collisionMasks,
		float explosionPower, Vector3 explosionDirection, float shardMass,  float fadeDelay, float shrinkDelay, 
		bool particleFade, bool saveToScene, float linearDampening, RigidBody3D.DampMode linearDampMode, 
		float angularDampening, RigidBody3D.DampMode angularDampMode, string saveDirectory = "res://", 
		bool cleanCollisionMesh = true, bool simplifyCollisionMesh = false)

	{
		// Creates new shards holder and sets the name to be that of the object + Shards
		string saveShardDir = saveDirectory;
		Node3D shards = new Node3D();
		shards.Name = obj.Name + "Shards";


		// Adds a slash if directory doesn't end with one since the file explorer doesn't give a final slash when using it to set directory.
		if (!saveShardDir.EndsWith("/"))
		{
			saveShardDir += "/";
		}
		
		// Sets the save directory to be the given director + Shards.tscn
		saveShardDir += obj.Name + "Shards.tscn";

		// Used to run the bulk of the generation on a separate thread to reduce stutter.
		await Task.Run(() =>
		{
			// Runs a loop for all of the children of the scene used to create the shards (should only be MeshInstances)
			foreach (var shardMesh in obj.GetChildren())
			{
				var mesh = shardMesh as MeshInstance3D;
				
				// Returns if no MeshInstance is found
				if (mesh == null)
				{
					continue;
				}

				// Instantiates a new shard
				MeshInstance3D shardMeshTyped = mesh;
				Shard newShard = shardScene.Instantiate<Shard>();
				
				// Sets the shards mesh instance to be that of the objects and adds it as a child of the shard
				MeshInstance3D meshInstance = new MeshInstance3D();
				meshInstance.Mesh = shardMeshTyped.Mesh;
				meshInstance.Name = "MeshInstance";
				newShard.AddChild(meshInstance);

				// Sets the shards collision shape to be a generation of the mesh instance with the given variables and adds it as a child.
				CollisionShape3D collisionShape = new CollisionShape3D();
				collisionShape.Shape = meshInstance.Mesh.CreateConvexShape(cleanCollisionMesh, simplifyCollisionMesh);
				collisionShape.Name = "CollisionShape";
				newShard.AddChild(collisionShape);

				// Sets all of the shard properties
				newShard.Position = shardMeshTyped.Position;
				newShard.CollisionLayer = collisionLayers;
				newShard.CollisionMask = collisionMasks;
				newShard.FadeDelay = fadeDelay;
				newShard.ExplosionPower = explosionPower;
				newShard.ExplosionDirection = explosionDirection;
				newShard.Mass = shardMass;
				newShard.ShrinkDelay = shrinkDelay;
				newShard.ParticleFade = particleFade;
				newShard.LinearDamp = linearDampening;
				newShard.LinearDampMode = linearDampMode;
				newShard.AngularDamp = angularDampening;
				newShard.AngularDampMode = angularDampMode;

				// Adds the shard to the shard list
				shards.AddChild(newShard);
			}

			// Checks if this is to be saved to a scene (for pre-generation use) and if so, saves it to the given path.
			if (saveToScene)
			{
				PackedScene savedShards = new PackedScene();
				DirAccess saveDirectoryFolder = DirAccess.Open(saveDirectory);
				foreach (Node shard in shards.GetChildren())
				{
					shard.Owner = shards;
					foreach (Node shardChild in shard.GetChildren())
					{
						shardChild.Owner = shards;
					}
				}

				if (saveDirectoryFolder == null)
				{
					GD.PrintErr("Save directory error:", DirAccess.GetOpenError());
					return;
				}

				savedShards.Pack(shards);
				ResourceSaver.Save(savedShards, saveShardDir);
				GD.PrintRich("[color=green]Generation completed.[/color]");
			}

			// Necessary to avoid orphan nodes
			obj.QueueFree();
		});

		return shards;
	}

}
