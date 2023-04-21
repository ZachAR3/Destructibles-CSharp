using System.IO;
using System.Threading.Tasks;
using Godot;

[Tool]
public partial class DestructionUtils : Node
{
	public async Task<Node3D> CreateShards(Node3D obj, PackedScene shardScene, uint collisionLayers,
		uint collisionMasks,
		float explosionPower, float fadeDelay, float shrinkDelay, bool saveToScene,
		string saveDirectory = "res://shards", bool cleanCollisionMesh = true, bool simplifyCollisionMesh = false)

	{
		Node3D shards = new Node3D();
		shards.Name = obj.Name + "Shards";
		saveDirectory += obj.Name + "Shards" + ".tscn";

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
				Shard newShard = shardScene.Instantiate<Shard>();
				
				MeshInstance3D meshInstance = new MeshInstance3D();
				meshInstance.Mesh = shardMeshTyped.Mesh;
				meshInstance.Name = "MeshInstance";
				newShard.AddChild(meshInstance);

				CollisionShape3D collisionShape = new CollisionShape3D();
				collisionShape.Shape = meshInstance.Mesh.CreateConvexShape(cleanCollisionMesh, simplifyCollisionMesh);
				collisionShape.Name = "CollisionShape";
				newShard.AddChild(collisionShape);

				newShard.Position = shardMeshTyped.Position;
				newShard.CollisionLayer = collisionLayers;
				newShard.CollisionMask = collisionMasks;
				newShard.FadeDelay = fadeDelay;
				newShard.ExplosionPower = explosionPower;
				newShard.ShrinkDelay = shrinkDelay;

				shards.AddChild(newShard);
			}

			if (saveToScene)
			{
				PackedScene savedShards = new PackedScene();
				foreach (Node shard in shards.GetChildren())
				{
					shard.Owner = shards;
					foreach (Node shardChild in shard.GetChildren())
					{
						shardChild.Owner = shards;
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
