using System.IO;
using System.Threading.Tasks;
using Godot;


public partial class DestructionUtils : Node
{
	public async Task<Node3D> CreateShards(Node3D obj, PackedScene shardScene, uint collisionLayers,
		uint collisionMasks,
		float explosionPower, float fadeDelay, float shrinkDelay, bool saveToScene,
		string saveDirectory = "res://shards", bool cleanCollisionMesh = true, bool simplifyCollisionMesh = false)

	{
		Node3D shards = new Node3D();
		shards.Name = obj.Name + "Shards";
		saveDirectory += obj.Name + ".tscn";

		await Task.Run(() =>
		{
			foreach (var shardMesh in obj.GetChildren())
			{
				var mesh = shardMesh as MeshInstance3D;
				if (mesh == null)
				{
					continue;
				}

				MeshInstance3D shardMeshTyped = mesh;

				PackedScene _shard = (PackedScene)GD.Load("res://addons/DestructionPluginCSharp/shard.tscn");
				Node shard = _shard.Instantiate();
				var shardScript = shard.GetScript();
				//Shard newShard = new Shard();//shardScene.Instantiate<Shard>();
				MeshInstance3D meshInstance = shard.GetNode<MeshInstance3D>("MeshInstance");
				meshInstance.Mesh = shardMeshTyped.Mesh;

				CollisionShape3D collisionShape = shard.GetNode<CollisionShape3D>("CollisionShape");
				collisionShape.Shape = meshInstance.Mesh.CreateConvexShape(cleanCollisionMesh, simplifyCollisionMesh);

				// newShard.Position = shardMeshTyped.Position;
				// newShard.CollisionLayer = collisionLayers;
				// newShard.CollisionMask = collisionMasks;
				// newShard.FadeDelay = fadeDelay;
				// newShard.ExplosionPower = explosionPower;
				// newShard.ShrinkDelay = shrinkDelay;

				RigidBody3D tempShard = shard as RigidBody3D;
				;
				tempShard.Position = shardMeshTyped.Position;
				tempShard.CollisionLayer = collisionLayers;
				tempShard.CollisionMask = collisionMasks;
				tempShard.Set("fadeDelay", fadeDelay);
				tempShard.Set("explosionPower", explosionPower);
				tempShard.Set("shrinkDelay", shrinkDelay);


				shards.AddChild(tempShard);
			}

			if (saveToScene)
			{
				GD.Print("save");
				PackedScene savedShards = new PackedScene();
				foreach (Node shard in shards.GetChildren())
				{
					shard.Owner = shards;
					foreach (Node shardChild in shard.GetChildren())
					{
						shardChild.Owner = shards;
						GD.Print(shardChild.Name);
					}
				}

				savedShards.Pack(shards);
				ResourceSaver.Save(savedShards, saveDirectory);
			}

			obj.QueueFree();
		});

		return shards;
	}

}
