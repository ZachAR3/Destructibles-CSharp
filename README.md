# Godot Destruction Plugin CSharp

Used in conjuction with shard meshes to easily create destructable objects.

*Requires Godot Mono.*

## Usage

1. Install the **Cell Fracture** addon in Blender, **join your mesh** and use **F3** to search for Cell Fracture. Set the Source Limit to how many RigidBodies you want in your game. (\~5 – 50)
2. Select everything, right click and select `Set Origin > Origin to Center of Mass (Volume)`.
3. Export it as a .ojb or GLTF, import it in Godot **as a scene** and create an **instance** of this scene.
4. Install and **enable** the **Destructibles plugin** from the asset library. (You may have to click build in the top right corner if you get errors, while trying to enable it; or under `project > tools > C# > press Create C# Solution`)
5. Add a **Destruction** node to the **intact** scene and set the `Fragmented` scene to the **fragmented** scene.
6. Set the `Shard Container` to the node the fragmented objects will be added to at runtime or leave it empty.
7. Call **Destroy(explosionPower: int, explosionDirection : Vector3())** to destroy the object with a given explosion force and direction. (To have each shard given a random direction use Vector3.ZERO as the direction)

## Options
Under the generation tab there are various options which affect both pre-generated and dynamically generated shards. Such options are listed below along with a short explanation:
* `Preload shards` which as the name suggests loads the shard scene at ready instead of when the `Destroy` function is called.
* `Simplify collision mesh` which at the cost of time and processing power simplifies the shard meshes collision shapes which can drastically improve performance in instances with large quantites of shards.
* `Clean collision mesh` is reccomended to be enabled as it doesn't cause much of a performance impact and removes duplicate and interior vertices which aren't needed.
* `ParticleFade` when enabled causes the fade affect to appear spotted such as particles or dust. When disabled uses a clean fade.
* `Mass` Simply setting the set the mass of each shard.
* `Linear dampening` Sets the linear dampening affect on each shard.
* `Linear dampening mode` Sets the linear dampening mode on each shard (Combine, adds the local dampening + the global, while replace just uses the local).
* `Angular dampening` Sets the angular dampening affect on each shard.
* `Angular dampening mode` Sets the angular dampening mode on each shard (Combine, adds the local dampening + the global, while replace just uses the local).


## Optimizations

In order to increase framerate and the quantity of objects that can be used at once, it's reccomened to pre-generate your shards.
By doing this you:
* A: Reduce stutter caused by shard generation
* B:Can enable `Simplify Collision Mesh` which drastically increases performance, at the cost of taking longer to generate; which is negated by pre-generating the shards.

## Pre-generation usage
1. Configure destruction node first by following the `Usage` steps.
2. Go down to `Generation` and select a path to store your generated shard scene under "save path"
3. Click generate mesh (Checkbox that acts as a button) and wait for it to place a new shard scene at your desired location
4. There are various options you can enable or disable listed above, simplify collision mesh and clean collision mesh have to be enabled BEFORE generation, in order for use with pre-generated meshes.
4. The final step is selecting your new shard mesh scene under "Pre generated shards"

You are now done, when you next run your game / program it will be using the pregenerated meshes improving latency, and stutter, along with performance if simplify mesh is enabled.

