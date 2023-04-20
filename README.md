# Godot Destruction Plugin CSharp

Used in conjuction with shard meshes to easily create destructable objects.

*Requires Godot Mono.*

## Usage

1. Install the **Cell Fracture** addon in Blender, **join your mesh** and use **F3** to search for Cell Fracture. Set the Source Limit to how many RigidBodies you want in your game. (\~5 – 20)
2. Select everything, right click and select `Set Origin > Origin to Center of Mass (Volume)`.
3. Export it as a .ojb or GLTF, import it in Godot **as a scene** and create an **instance** of this scene.
4. For Godot **3** Install and **enable** the **Destruction plugin** from the asset library. On Godot **4** and above, currently the best method is to download this repository; and extract the **addons/** folder into the root of your project. It can then be enabled under plugin settings as usual.
5. Add a **Destruction** node to the **intact** scene and set the `Fragmented` scene to the **fragmented** scene.
6. Set the `Shard Container` to the node the fragmented objects will be added to at runtime or leave it empty.
7. Call **destroy()** to destroy the object.

## Options
Under the generation tab there are various options which affect both pre-generated and dynamically generated shards.
* You can enable various options listed below:
* Preload shards which as the name suggests loads the shard scene at ready instead of when the "Destroy" function is called.
* Simplify collision mesh which at the cost of resources simplifies the shard meshes collision boxes causing a large performance uplift.
* Clean collision mesh is reccomended to be enabled as it doesn't cause much of a performance impact and removes duplicate and interior vertices which aren't needed.


## Optimizations

In order to increase framerate and the quantity of objects that can be used at once, it's reccomened to pre-generate your shards.
By doing this you:
A: Reduce stutter caused by shard generation
B:Can enable "Simplify Collision Mesh" which drastically increases performance, at the cost of taking longer to generate; which is negated by pre-generating the shards.

## Pre-generation usage
1. Once you follow the original steps and have an object with a "Destruction" node configured with a shard mesh made in Blender go down to the "Generation tab".
2. Select a path to store your generated shard scene under "save path"
3. Click generate mesh and wait for it to place a new shard scene at your desired location
4. There are various options you can enable or disable listed above, simplify collision mesh and clean collision mesh have to be enabled BEFORE generation, in order for use with pre-generated meshes.
4. The final step is selecting your new shard mesh scene under "Pre generated shards"

You are now done, when you next run your game / program it will be using the pregenerated meshes improving latency, and stutter, along with performance if simplify mesh is enabled.

