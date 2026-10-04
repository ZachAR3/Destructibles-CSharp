# Godot Destructibles C#

Destruction for Godot 4.6+ .NET and .NET 8 (this project uses Godot 4.7.2). Use existing fractured models, bake fragments in Godot, or cut meshes during gameplay, including current rig poses. The addon includes its tessellator and requires no additional packages. Demos and tests are available in this repository.

The original `Demo/demo.tscn` uses an imported fractured model. Run `Demo/fracture_demo.tscn` to try runtime bursts, repeated partial cuts, and hollow torus cuts without a pre-broken asset. Its controls expose patterns, all six noise types, shard quantity, seeds, and interior roughness.

## Install and use

1. Copy `addons/DestructiblesCSharp` into your project, build the C# solution, and enable the plugin.
2. Add a **Destructible** child to the standalone object root, which must be a `Node3D`.
3. For runtime fracture, assign **Source Mesh**, or put Destructible directly under a `MeshInstance3D`. With a model root, every visible descendant mesh is combined in the root’s coordinate space. Set **Source Mesh** to select just one. Full destruction removes the object root; partial cuts leave one combined static remainder.
4. Alternatively, assign **Fragmented** to an imported scene containing fragment meshes, or **Pre Generated Shards** to a scene baked by this plugin. Baked scenes take priority, followed by imported fragments, then runtime fracture.
5. Optionally choose **Shard Container**, outside the object being destroyed. Its parent is the default container.

```csharp
// Existing call: destroy the whole object. Zero gives each shard a random impulse direction.
destructible.Destroy(4f, Vector3.Zero);

// C# callers can await completion and check whether a break happened.
bool broken = await destructible.DestroyAsync(4f, Vector3.Up);

// Sharp hit: world-space point and cutting-plane normal. Keeps the larger remainder.
bool cut = await destructible.HitAsync(hitPosition, bladeNormal, force: 8f, sharpness: 1f);

// Blunt hit: world-space contact point and impulse direction. Strength controls fragment count.
destructible.Hit(hitPosition, hitDirection, force: 8f, sharpness: 0f);
```

From GDScript use `destruction.Destroy(4.0, Vector3.ZERO)` or `destruction.Hit(point, direction, 8.0, 1.0)`. The `Fractured(shardCount, partial)` signal fires after activation. Fire-and-forget `Destroy`/`Hit` calls emit `FractureFailed(reason)` if no break completes; C# callers can use the Task result or exception instead. Duplicate calls while preparing are ignored; freeing the object cancels pending work.

## Performance

**Shards Per Frame** defaults to 4 and **Frame Budget Milliseconds** to 2. Mesh uploads, hull generation, body creation, and activation go through one main-thread queue shared by all destructible objects. The strictest budget among queued jobs applies. A single native operation cannot be interrupted, so this is a soft time limit. Mesh snapshot reads and scene-data reads are also single operations; bake large assets to avoid these runtime costs.

The original object remains visible while fragments are prepared. It is replaced when preparation finishes, then fragment activation is spread across frames. A moving object's rigid transform is refreshed at replacement; changing its scale during preparation aborts the operation before replacement.

Each shard is a `Shard` rigid body with a mesh and one or more collision shapes. Debris uses nodes throughout: the measured server path had essentially the same performance and was removed to keep the code simpler.

Prefer baked meshes/colliders and a modest fragment count. Disable **Shard Shadows** for small debris in GPU-heavy scenes; reducing visual mesh detail also helps. Sleeping is enabled by default. Materials retain their source settings and remain shared until fading starts. Per-surface overrides are carried with fragment data, without rebuilding shared meshes. Set both fade and shrink delays to 0 or a negative value to keep debris indefinitely.

See [the evaluation and measured comparison](docs/EVALUATION.md) for timings, tradeoffs, and remaining limits.

## Runtime fracture

- **Fracture Pieces**: desired quantity for `Destroy` and baking, from 2 to 256; default 8. Blunt hits use `2 + floor(force / MinimumForce * (1 - sharpness))`, capped at this setting. Minimum size and geometry can produce fewer pieces; `Fractured` reports the actual count, with a warning if a burst/bake cannot reach its target.
- **Minimum Force**: weaker hits do nothing; default 1. `Destroy` bypasses this threshold.
- **Sharpness**: values below 0.5 make a burst near the hit. Values at least 0.5 make one exact plane cut. For sharp hits, direction is the plane's normal; for blunt hits, it is the impulse direction.
- **Keep Remainder**: sharp cuts retain the larger piece and release the smaller one. Disable it to release both. The remainder can be cut again. Existing fragmented/baked scenes always break as a whole.
- **Interior Material**: material for new cut faces. Exterior surfaces preserve materials, normals, UV/UV2, and vertex colours; tangents are regenerated.
- **Fracture Seed**: deterministic random planes and roughness for bursts and baking. A noise resource has its own independent seed.
- **Fracture Options**: optional reusable resource for patterns, noise, minimum volume, and interior detail; see below. Leaving it empty uses the simple random-plane fracture.
- **Source Collision**: collider to replace for partial cuts. A single descendant collider is found automatically. Assign this explicitly when the object has multiple colliders. Static-body remainders use triangle collision; dynamic-body remainders use the selected convex collision mode, including multiple hulls when decomposition is enabled.

Custom and imported meshes work; the generator is not restricted to Godot primitives or standard shapes. Automatic source preparation supports many open/non-manifold meshes and captures skeletons and blend shapes in their current pose. It cannot guarantee an exact fracture of every possible mesh: the cutter still needs a closed triangle solid, and repair sometimes reconstructs an approximation. Geometry errors occur before replacing the live mesh. Material stress and crack propagation belong in a separate gameplay model.

## Source preparation and posed rigs

These settings apply to procedural baking and gameplay fracture. Existing imported/pre-baked fragments bypass source preparation.

| Setting | Effect |
| --- | --- |
| Capture Current Pose | Default on. Snapshot skeleton deformation and blend shapes together, producing static posed shards. Supports four/eight bone influences, named/indexed skin binds, normalized/relative shape keys, and the final skeleton modifier/IK pose observed during skeleton updates. |
| Repair Non Manifold | Default on. Keep valid solids unchanged; otherwise remove duplicate faces, reconcile winding, and close planar boundary loops (including holes). If these fixes fail, reconstruct a solid using a distance field. Every repaired/reconstructed input emits a warning. Disable for strict rejection of unsuitable topology. |
| Repair Resolution | Longest dimension’s grid resolution, 16–96; default 64. Higher values retain more detail and increase processing time and triangle count. Only used for distance-field reconstruction. |
| Repair Shell Thickness | Minimum reconstructed thickness in mesh-local units; 0 uses about one grid cell. Open sheets receive thickness automatically. Only used for reconstruction; use Force Solid Remesh to thicken an otherwise valid solid. |
| Force Solid Remesh | Default off. Request reconstruction even if the edge/winding check passes, useful for overlapping or self-intersecting shells that appear closed. Requires Repair Non Manifold. |

The initial cleanup preserves exterior attributes where possible. Reconstruction samples a bounded distance field, uses three-axis inside votes, and extracts a closed surface with marching tetrahedra. Materials, UV/UV2, and colours are projected from nearby source faces; shading normals are rebuilt. This can round sharp edges, fill small gaps, merge nearby parts, change volume, or distort textures. Increase resolution and inspect the result when these details matter. There are limits of 100,000 input/reconstructed triangles and two million grid samples; exceeding them reports an actionable error rather than replacing the object.

Rig capture reads copied vertex arrays and applies the current skin/shape transforms on the CPU. **Generate Shards never swaps the original mesh or skin, changes the pose, or stops animation**, so no temporary bake/undo is required. Static meshes remain static; the original resource is never modified. The pose is captured when the queued snapshot runs, then stays fixed while asynchronous preparation finishes. Successful partial cuts freeze the posed remainder and stop AnimationPlayers/AnimationTrees inside the object root to keep visual geometry and collision aligned. Full destruction releases static debris. Bare `MeshFracture.Read(mesh, transform)` intentionally requires static data; the Destructible node handles pose observation and repair automatically.

GPU vertex shaders/VAT, soft-body deformation, and arbitrary Boolean/modeling operations are outside this snapshot path. Instance shader parameters are not copied to shards; configure their material resource explicitly. Closed topology checks do not detect every geometric self-intersection; Force Solid Remesh offers an approximate fallback. Intersecting/touching cut contours and very small features can still prevent an exact sharp cut; procedural generation retries unsuitable planes and can return fewer pieces.

Repair and cutting run on a worker; mesh reads and CPU pose capture still run as a single main-thread operation. Bake expensive assets ahead of gameplay. The integration suite constructs its non-manifold inputs directly, exercising repair, baking and runtime fracture without an external modeling tool.

Caps use the vendored [LibTessDotNet](https://github.com/speps/LibTessDotNet) tessellator with even-odd winding. The cutter restores collinear boundary points so repeated cuts remain watertight. Its source and license live inside the addon; installation requires no additional packages. Baking uses the same cutter and geometry limits. Blender Cell Fracture remains an option for Voronoi fracture and modeling workflows.

## Fracture presets

In the inspector, choose **New FractureOptions** under **Fracture Options**. Expand it to adjust the settings; save it as a `.tres` to reuse on other objects. These controls apply to procedural bursts and baking. Sharp gameplay hits retain their exact cutting plane and use only **Interior UV Scale** from the preset. Imported/pre-baked shard scenes keep their existing geometry; rebake to change their pattern.

| Option | Effect |
| --- | --- |
| Pattern: Random | Repeated random planes, starting at the configured origin or hit position. |
| Pattern: Slices | Parallel cuts perpendicular to **Axis**, useful for planks and slabs. |
| Pattern: Radial | Wedges through the origin, around **Axis**, useful for impact/starburst patterns. |
| Pattern: Grid | Split each largest piece along its longest local bounding-box axis. |
| Axis | Mesh-local direction for slices; rotation axis for radial wedges. Default up. |
| Origin | Position in the mesh bounding box, normalized to 0–1 per component; default `(0.5, 0.5, 0.5)`. Gameplay hits override it. Grid uses piece centres. |
| Origin Bias | Pull later random cuts toward the origin, from 0 (piece centre) to 1 (origin). Default 0.2; affects Random only. |
| Size Variation | Seeded cut-position jitter; 0 gives more regular sizes. |
| Direction Variation | Seeded angular variation; 0 preserves the pattern directions. |
| Minimum Volume Ratio | Smallest accepted shard as a fraction of the original solid volume; default 0.0001. Higher values prevent tiny debris and can lower the final count. |
| Noise | Optional Godot `FastNoiseLite` resource: Simplex, Simplex Smooth, Cellular, Perlin, Value Cubic, or Value, with its normal frequency, fractal, cellular, and domain-warp controls. |
| Noise Influence | Noise contribution to plane position/direction; set 0 to retain regular planes while using noise only for roughness. |
| Surface Roughness | Faceted displacement inside each cap triangle, relative to the current piece's longest dimension. Default 0 keeps flat caps. Paired caps share vertices; boundary edges stay fixed. Displacement backs off where faces intersect the exterior. |
| Interior UV Scale | Texture tiling for newly created cap faces; default 1. |

Patterns repeatedly split the largest eligible piece; they are not Voronoi cells or a physical crack simulation. Noise and variation deliberately perturb the regular slice/grid/radial patterns. Noise samples use mesh-local units: try frequency **1–3** for a mesh a few units wide; the noise resource's default 0.01 is usually too broad at that scale. Noise values are clamped to −1…1 before affecting geometry. See [Godot's FastNoiseLite options](https://docs.godotengine.org/en/4.6/classes/class_fastnoiselite.html).

Roughness adds triangles and exterior-intersection checks; keep it small and bake expensive presets. It makes faceted interior surfaces, rather than continuous noise-displaced Boolean cuts. Very thin/tiny cap regions may stay flat. Convex collision approximates the visible roughness, as with other concave geometry.

```csharp
destructible.FracturePieces = 24;
destructible.FractureSeed = 42;
destructible.FractureOptions = new FractureOptions
{
    Pattern = FracturePattern.Radial,
    Axis = Vector3.Up,
    SizeVariation = 0.15f,
    MinimumVolumeRatio = 0.005f,
    SurfaceRoughness = 0.02f,
    Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 1.5f, Seed = 42 }
};
```

## Concave collision

Enable **Decompose Collision** for hollow or concave pieces. Godot's V-HACD helper creates several convex shapes on each rigid body. Baking saves every hull; runtime loading and scale changes reuse those points. Partial cuts also replace compound hulls on dynamic remainders. Static remainders continue to use triangle collision.

Without decomposition, each piece uses one convex hull. With decomposition, defaults are eight hulls maximum, 100,000 voxels, maximum concavity 0.002, and mesh normalization enabled. **Decomposition Settings** accepts Godot's settings resource for customization. When creating that resource, set **Max Convex Hulls** above one and lower **Max Concavity**; Godot's resource defaults intentionally produce a single hull.

V-HACD approximates the surface, so small holes or thin features can be lost and seams can remain between hulls. Check important collision gaps in your asset. More hulls increase physics work. Decomposition is a blocking native operation and can exceed the frame budget by a wide margin: bake concave assets ahead of gameplay.

## Bake in Godot

1. Configure the source mesh/model (or imported fragments), fragment count, seed, optional fracture preset, source preparation, materials, and collision settings. For rigs, put the model in the desired pose first.
2. Set **Save Path**, then click **Generate Shards** in the inspector.
3. Wait for the saved-path message and assign that scene to **Pre Generated Shards**.

Baking writes mesh resources and convex hulls into a `.tscn`, without deleting or hiding the original. Runtime settings are applied when the baked data is used. Scale is baked into geometry when necessary; baked convex points can be scaled without regenerating the source hull. **Preload Shards** reads fragment resource data ahead of time, without instantiating all rigid bodies.

Fragment scenes use the meshes and materials stored in their resources; scripts that create geometry in `_Ready` are not run while reading them. Inherited scenes use a temporary instance for reading.

For C# tooling, `await destructible.BakeAsync()` returns the saved path. File/packing errors are reported; pending baking is cancelled if the node leaves the tree.

## Other settings

| Setting | Meaning |
| --- | --- |
| Fragmented Material | Override all shard surfaces; otherwise keep source materials. |
| Decompose Collision / Decomposition Settings | Opt into compound convex hulls for concave pieces; configure V-HACD quality when needed. |
| Shard Shadows | Cast shadows from debris; disable for small fragments to reduce GPU work. |
| Clean Collision Mesh | Remove duplicate/interior vertices during hull generation. |
| Simplify Collision Mesh | More generation work for simpler hulls; useful when baking. |
| Shard Mass | Mass per shard, default 1; not automatically derived from volume. |
| Linear/Angular Dampening and Mode | Damping and Combine/Replace behavior for each body. |
| Collision Layers/Layer Masks | Physics filters for activated fragments. |
| Fade Delay / Shrink Delay | Start delay, followed by a two-second animation; each shard frees itself after its animations. |
| Particle Fade | Alpha-hash fade for standard materials; otherwise alpha blending. |

Custom shader materials use instance transparency for fading, which requires Forward+. Shrinking works on every renderer. Shard cleanup still occurs if a custom shader does not support a visible fade.

## Verify

```sh
dotnet build
# Use the Godot .NET executable; some installations call it godot-mono.
godot-mono --headless --path . --editor --import --quit
godot-mono --headless --path . res://Tests/tests.tscn
# Optional preparation timing/allocation and node spawning samples:
godot-mono --headless --path . res://Tests/tests.tscn -- --benchmark
# Optional frame interval and rendered GPU samples:
godot-mono --headless --path . --script res://Tests/compare.gd -- --new
godot-mono --path . --rendering-method forward_plus --script res://Tests/render_profile.gd -- --shards=1024 --dense
```

CI builds with .NET 8 and runs the integration scene in Godot .NET 4.7.2, matching the project SDK. The addon is also tested in an isolated Godot 4.6 / SDK 4.6.0 project. Tests exercise automatic non-manifold preparation, open sheets, repair warnings, strict rejection, multi-mesh models, current rig/shape/modifier poses, unchanged originals after baking or failure, repeated posed partial cuts, custom meshes, all patterns/noise types, rough mating caps, thin walls, quantity/minimum-volume controls, reusable presets, frame budgets, compound hulls and actual collision gaps, imported and baked scenes, partial cuts, cancellation, and independent lifetimes.
