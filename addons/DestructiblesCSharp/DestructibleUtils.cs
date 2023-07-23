namespace Destructibles;

using DampMode = RigidBody3D.DampMode;

// Used to generate shards both dynamically and statically (for pre-generated use-cases)
[Tool]
public partial class DestructibleUtils : Node
{
	public async Task<Node3D> CreateShards(ShardSettings settings)
	{
		// Creates new shards holder and sets the name to be that of the object + Shards
		var saveShardDir = settings.SaveDirectory;
		var shards = new Node3D {
			Name = settings.Obj.Name + "Shards"
		};

		// Adds a slash if directory doesn't end with one since the file explorer doesn't give a final slash when using it to set directory.
		if (!saveShardDir.EndsWith("/"))
			saveShardDir += "/";
		
		// Sets the save directory to be the given director + Shards.tscn
		saveShardDir += settings.Obj.Name + "Shards.tscn";

		// Used to run the bulk of the generation on a separate thread to reduce stutter.
		await Task.Run(() =>
		{
			// Runs a loop for all of the children of the scene used to create the shards (should only be MeshInstances)
			foreach (var shardMesh in settings.Obj.GetChildren())
			{
				// Returns if no MeshInstance is found
				if (shardMesh is not MeshInstance3D mesh)
					continue;

				// Instantiates a new shard
				var shardMeshTyped = mesh;
				Shard newShard = settings.ShardScene.Instantiate<Shard>();

				// Calls the scene functions deferred for thread safety
				Callable.From(() =>
				{
					// Sets the shards mesh instance to be that of the objects and adds it as a child of the shard
					var meshInstance = new MeshInstance3D
					{
						Mesh = shardMeshTyped.Mesh,
						Scale = settings.Scale,
						Name = "MeshInstance"
					};
					newShard.AddChild(meshInstance);

					// Sets the shards collision shape to be a generation of the mesh instance with the given variables and adds it as a child.
					var collisionShape = new CollisionShape3D
					{
						Shape = meshInstance.Mesh.CreateConvexShape(
							settings.CleanCollisionMesh,
							settings.SimplifyCollisionMesh),
						Scale = settings.Scale,
						Name = "CollisionShape"
					};
					newShard.AddChild(collisionShape);

					// Sets all of the shard properties
					newShard.Position = shardMeshTyped.Position;
					newShard.CollisionLayer = settings.CollisionLayers;
					newShard.CollisionMask = settings.CollisionMasks;
					newShard.FadeDelay = settings.FadeDelay;
					newShard.ExplosionPower = settings.ExplosionPower;
					newShard.ExplosionDirection = settings.ExplosionDirection;
					newShard.Mass = settings.ShardMass;
					newShard.ShrinkDelay = settings.ShrinkDelay;
					newShard.ParticleFade = settings.ParticleFade;
					newShard.LinearDamp = settings.LinearDampening;
					newShard.LinearDampMode = settings.LinearDampMode;
					newShard.AngularDamp = settings.AngularDampening;
					newShard.AngularDampMode = settings.AngularDampMode;

					// Adds the shard to the shard list
					shards.AddChild(newShard);
				}).CallDeferred();

			}

			Callable.From(() =>
			{
				// Checks if this is to be saved to a scene (for pre-generation use) and if so, saves it to the given path.
				if (settings.SaveToScene)
				{
					var savedShards = new PackedScene();
					var saveDirectoryFolder = DirAccess.Open(settings.SaveDirectory);

					foreach (Node shard in shards.GetChildren())
					{
						shard.Owner = shards;
						foreach (Node shardChild in shard.GetChildren())
							shardChild.Owner = shards;
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
				settings.Obj.QueueFree();
			}).CallDeferred();
		});

		return shards;
	}

}

public class ShardSettings
{
	public Node3D      Obj                   { get; set; }
	public PackedScene ShardScene            { get; set; }
	public uint        CollisionLayers       { get; set; }
	public DampMode    LinearDampMode        { get; set; }
	public DampMode    AngularDampMode       { get; set; }
	public uint        CollisionMasks        { get; set; }
	public float       ExplosionPower        { get; set; }
	public Vector3     ExplosionDirection    { get; set; }
	public float       ShardMass             { get; set; }
	public float       FadeDelay             { get; set; }
	public float       ShrinkDelay           { get; set; }
	public bool        ParticleFade          { get; set; }
	public bool        SaveToScene           { get; set; }
	public float       LinearDampening       { get; set; }
	public float       AngularDampening      { get; set; }
	public string      SaveDirectory         { get; set; } = "res://";
	public bool        CleanCollisionMesh    { get; set; } = true;
	public bool        SimplifyCollisionMesh { get; set; } = false;
	public Vector3     Scale                 { get; set; } = new Vector3();
}
