# Repository evaluation and destruction improvements

The existing addon already covered imported fragment scenes and scene baking. The main bottleneck was that every mesh upload, convex hull, body insertion, and later material setup was effectively performed in a burst. The worker only queued the expensive main-thread operations. The revised implementation adds a shared creation budget, node debris, a mesh cutter with hole caps, and optional compound convex collision while preserving `Destroy(...)` and existing serialized configuration names.

## Findings and fixes

| Finding | Consequence | Change |
| --- | --- | --- |
| `Task.Run` completed after scheduling deferred callbacks, without awaiting their work. | Returning the shard tree did not guarantee generation/saving had finished; expensive callbacks still accumulated into one frame. | Explicit queued work with completion tasks, all native resource/tree operations on the main thread. |
| Only direct child mesh positions were read; local rotation, nested transforms, and inherited scale were omitted. | Fragment placement and collision could differ from the intact object. | Full transform traversal; scale/shear baked into geometry; rigid body bases remain unscaled. |
| `_Ready` replaced the configured container and retained detached fragment scenes. | The inspector setting was ignored; editor imports reported leaked nodes/resources. | Respect the setting, read resource data, immediately free fallback temporary trees, and avoid editor preloading. |
| Configuration warnings discarded `Append` results and returned the base array. | Missing inputs and bad containers were never reported. | Build and return a real warning list, with null-safe parent checks. |
| Missing/non-standard materials prevented initialization before the impulse. | Null exceptions or stationary shards. | Physics initialization is independent of material fading. |
| Fading tweened to white, duplicated material data eagerly, and failed with both animations disabled. | Colours changed, unnecessary allocations, invalid empty tweens. | Preserve colours and surfaces; lazy shallow material copies; no tween/await per shard; disabled animation leaves persistent debris. |
| Each shard freed its parent after its own animation. | One shard deleted every sibling, including later-spawned shards. | Individual cleanup; empty group cleanup only after generation completes. |
| Baking reused the destruction path and kept a persistent save flag; pack/save errors were ignored. | Confusing subsequent behavior and leaked generated trees. | Separate cancellable baking, checked error results, recursive owners, and deterministic temporary-tree cleanup. |
| No guard against repeated calls or deleting the source during asynchronous work. | Duplicate generation and stale-node access. | Busy/completion guards and cancellation tied to tree lifetime. |
| CI installed .NET 6 for a .NET 8 project and had no actual engine tests. | The configured build could not validate the addon. | .NET 8, Godot .NET matching the project SDK, and a runnable integration test scene. |

The implementation also normalizes impulse directions, applies impulses on the first valid physics integration, reads baked hulls without instantiating entire body scenes, scales existing convex points where possible, and avoids transparent materials before the fade begins. The original mass-per-fragment model is retained for compatibility. The old Node-based `DestructibleUtils.CreateShards` helper and its object/scene settings were replaced by stateless helpers; external code calling that helper directly needs updating. `Destroy(...)` and the existing serialized inspector field names remain compatible.

## Performance comparison

Local Godot .NET 4.6, Linux, headless renderer, 128 separate box fragments, shared standard material, identity object transform, one warm-up and three recorded runs. The comparison script includes creation and six subsequent process frames, so delayed impulse/material work is included in the peak interval. Baseline runs use the original revision `18fbae8`; revised runs use eight work items per frame and a two-millisecond time budget.

| Path | Time until original is removed | Largest process-frame interval | Nodes per destruction group |
| --- | --- | --- | --- |
| Original | 18.4–18.6 ms | 16.7–16.9 ms | 385 |
| Revised nodes | 228.1–234.6 ms | 7.36–7.40 ms | 385 |
| Revised servers | 228.0–235.0 ms | 7.33–7.39 ms | 1 |

The frame interval improved by roughly 56%, while completion latency increased because creation and activation are spread over multiple frames. Both backends measured similarly here: at this size, server creation is not a demonstrated speed win. The optional server implementation was removed after this comparison, following the preference for simple node code. The historical server measurements are retained here to explain that decision. These intervals include the engine's headless pacing and are not isolated CPU times or rendered-game FPS. Reprofile in a real level with the intended physics backend, materials, fragment counts, and renderer.

Reproduce revised samples after building/importing:

```sh
godot-mono --headless --path . --script res://Tests/compare.gd -- --new
```

To compare the baseline, copy `Tests/compare.gd` to a temporary location, use a checkout of `18fbae8`, build/import it, and run the same script without `--new`. The optional `--benchmark` argument on `Tests/tests.tscn` samples the node path with cleanup disabled.

## Node design and frame-budget limits

Every shard is a normal `RigidBody3D` with a mesh and one or more `CollisionShape3D` children. Keeping one path avoids duplicated activation, cleanup, and rendering code. Node contacts and scripts remain available. Godot's scene system already uses rendering and physics servers; bypassing nodes does not remove mesh uploads, draw calls, hull generation, or body simulation. [Godot server documentation](https://docs.godotengine.org/en/4.6/tutorials/performance/using_servers.html).

The worker uses snapshot triangle data, managed geometry math, and an optional private read-only `FastNoiseLite` sampler. Each job duplicates its noise resource on the main thread before starting; Inspector edits and concurrent objects cannot mutate that copy. Noise sampling is a const mathematical operation in Godot, without rendering/physics work. Scene-tree access and mesh/resource creation remain on the main thread, avoiding dependence on separate-thread project settings. [Godot noise implementation](https://github.com/godotengine/godot/blob/4.6-stable/modules/noise/fastnoise_lite.cpp), [Godot thread-safety documentation](https://docs.godotengine.org/en/4.6/tutorials/performance/thread_safe_apis.html).

A frame budget cannot interrupt a native call. Snapshot extraction, a single upload/hull, and fallback instantiation of an inherited/custom fragment scene can still exceed it. Baking, lower-detail meshes, and fewer physical fragments remain the strongest ways to control these costs. The shared queue uses FIFO scheduling and the strictest pending budget; it bounds aggregate work but does not provide per-object fairness.

## Caps, hollow geometry, and collision

The cutter clips triangles against planes and builds contours from the actual open edges. Existing faces on a cut plane belong to the side containing their solid; tangent planes do not release zero-volume shards. Caps go through the double-precision GLU tessellator in vendored LibTessDotNet 1.1.15 with even-odd winding. This supports concave outlines, multiple holes, nested solid islands, and separate contours. Nearly collinear outlines are simplified for tessellation and their original boundary points restored afterward. That preserves the exterior's edges and prevents T-junctions during repeated cuts. Source and license are included in the addon, keeping installation self-contained. [LibTessDotNet source](https://github.com/speps/LibTessDotNet/tree/v1.1.15).

Exterior normals, UV/UV2, colours, and materials remain intact; tangents are regenerated. Bursts repeatedly cut the largest piece with a deterministic seed. Sharp hits make an exact plane cut; partial cuts replace the selected source mesh and collider while releasing the smaller piece. Force and sharpness remain gameplay controls. A material stress/crack model should stay separate from this geometric cutter.

**Decompose Collision** opts into Godot's V-HACD helper, producing several convex shapes on a rigid body. Bakes save every hull. Resource loading preserves aligned compound hulls without instantiating a whole shard tree, and scaling transforms the cached hull points. Dynamic partial remainders replace their previous hulls; static remainders use triangle collision. Defaults cap output at eight hulls, use 100,000 voxels and maximum concavity 0.002, and normalize the mesh. An optional Godot settings resource exposes further quality controls. [Godot decomposition API](https://docs.godotengine.org/en/4.6/classes/class_meshinstance3d.html#class-meshinstance3d-method-create-multiple-convex-collisions).

Normalization matters: with the same low concavity and hull limit, the axis-aligned L-shaped test mesh returned one hull without normalization and two with it. Two hulls preserve its notch in physics ray queries. Preparation took about 140 ms locally, far beyond a 2 ms frame budget. Decomposition is a blocking native operation, so concave assets should normally be baked. Its voxel approximation can lose thin features, fill small gaps, or leave seams between hulls; increasing quality and checking relevant collision features is still necessary. A single hull remains the inexpensive default.

The core cutter needs closed triangle solids with consistent winding, up to 100,000 source triangles. The Destructible node now prepares many open/non-manifold sources automatically and snapshots current skeleton/blend-shape poses. Geometric self-intersections are not comprehensively detected; forced reconstruction is available, with approximation warnings. Intersecting/touching cut contours and arbitrary Boolean operations remain limitations. Baking removes gameplay fracture/upload/hull preparation when scale is unchanged, while sharing these cutter limits. This is a Godot-native plane-fracture tool; Blender Cell Fracture's Voronoi and modeling workflows remain useful for more complex jobs.

## Custom meshes and fracture controls

The cutter accepts custom/imported meshes with readable triangle arrays, rather than a list of supported primitive shapes. The preparation step resolves many topology/winding problems; the cutter’s tolerance and contour restrictions still apply. A model root automatically combines all visible descendant meshes, keeping transforms and active materials. Explicit Source Mesh selects one mesh. Partial cuts replace a multi-mesh model with one static combined remainder, which subsequent cuts use. A deformed, skewed custom ArrayMesh is now tested with all four patterns. Concave and hollow coverage remains in the existing geometry tests.

`FractureOptions` is a reusable Godot Resource. It offers random planes, parallel slices, radial wedges and grid cuts; local axis and normalized origin; origin bias for random cuts; position/direction variation; minimum volume; an optional FastNoiseLite resource; faceted surface roughness; and interior UV tiling. The existing serialized `FracturePieces` and `FractureSeed` names are retained. Desired quantity now supports 2–256 pieces. Blunt hits still derive their target from force/sharpness, capped by that quantity. Bakes and bursts warn when geometric limits or minimum volume prevent the requested count. Exact sharp cuts preserve the supplied plane regardless of burst settings.

The splitter caches each piece's volume, tries the largest eligible piece, and skips pieces after repeated failed cuts. This avoids repeatedly calculating and sorting every piece's volume or letting one unsplittable piece consume all attempts. Minimum volume is relative to the original solid volume, so it can deliberately reduce the final count. Procedural planes that meet unsuitable contours are retried without losing already generated geometry; source validation still fails before modifying the live object.

FastNoiseLite supplies six noise algorithms and its existing fractal/domain-warp options. Sampling uses mesh-local coordinates; the frequency needs to match the asset size. Values are clamped before geometry use because some cellular modes exceed the usual −1…1 range. Noise changes cut positions/normals and optional roughness, rather than implementing a noise Boolean or Voronoi fracture. Both fracture and noise seeds contribute to repeatability. [Godot FastNoiseLite documentation](https://docs.godotengine.org/en/4.6/classes/class_fastnoiselite.html).

Roughness lifts an interior point of each cap triangle and fans three faces around it. Perimeter edges stay fixed; opposite caps use exactly the same positions and reversed winding. Local triangle size limits displacement, and exterior-intersection checks back it off on thin/concave regions. Regions too small to displace safely remain flat. It preserves volume between mating shards, but increases triangle count and cutting cost. It is disabled by default; baking is preferable for large, rough assets. Convex collision retains its usual approximation limits. The demo exposes pattern/noise selection, quantity, seed, roughness and hollow geometry directly.

## Non-manifold sources and current poses

Preparation first validates the unmodified triangle snapshot. Suitable meshes skip repair entirely. Unsuitable input receives a temporary cleanup: weld coincident vertices, remove duplicate faces, reconcile connected face winding, and tessellate planar boundary loops with holes. A bounded distance-field reconstruction is the fallback, rather than trying to infer exact Boolean intent from broken topology. Three-axis signed crossing votes fill likely interiors; unsigned distance gives open surfaces finite thickness. Marching tetrahedra supplies a closed surface, and a small triangle BVH accelerates distance sampling and attribute projection. Reconstructed output is validated before cutting. No native dependency is added.

Repair emits warnings and leaves source resources intact. Resolution defaults to 64 cells along the longest dimension (16–96); shell thickness defaults to about one cell. Reconstruction can alter surface detail, texture coordinates, volume and small gaps, and merge nearby shells. Force Solid Remesh handles closed overlapping input that a simple edge check cannot identify. The limits are 100,000 source/reconstructed triangles and two million grid samples. Disabling Repair Non Manifold restores strict rejection. Neither this method nor the existing cutter promises exact results on every mesh.

An earlier user-supplied `NonManifoldTest.tscn` contained a small closed Cube and the much larger Cube_010 railgun mesh. Selecting only the first descendant missed most of the model. The main mesh has open boundary edges, inconsistent winding and edges shared by four faces. Combining both meshes and using default reconstruction bakes eight fragments, about 33,000 total triangles, in roughly 3.2 seconds locally on Godot .NET 4.7.2. Both baking and runtime fracture passed on that asset; rendered before/after previews retain the model’s recognizable silhouette and material regions. Thin features and textured seams remain approximate. Baking is preferable for this preparation cost.

Current-pose capture applies normalized/relative blend shapes and four/eight-weight skin deformation to copied arrays. Named or indexed Skin binds map to the current bone pose; skinning follows shape deformation. SkeletonUpdated observation captures the final modifier/IK result before Godot restores its base animation poses. This works in headless tests as well as rendered scenes, without GPU readback or swapping the source Mesh/Skin. Baking leaves mesh references, skin, pose, shape values and animation intact, eliminating the need for a temporary bake/undo sequence. Already static data follows its original path. [Godot skeleton update signal](https://docs.godotengine.org/en/4.6/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated), [Godot skin and mesh APIs](https://docs.godotengine.org/en/4.6/classes/class_meshinstance3d.html).

The captured pose stays fixed during asynchronous preparation; only the object’s rigid transform is refreshed at replacement. Successful partial cuts clear live deformation and freeze the posed remainder, stopping AnimationPlayers/AnimationTrees under the object root. Failed preparation leaves those animations untouched. GPU shader/VAT and soft-body deformation are outside this path. Pose reads/skinning remain one main-thread operation; expensive source preparation and cuts run on a worker. Frame scheduling cannot interrupt a single pose snapshot.

## Rendered GPU stress test

Godot .NET 4.6, Forward+ / Vulkan, NVIDIA RTX 3070 Ti (driver 615.71.09), 1920×1080, V-Sync disabled. The reproducible synthetic scene creates 1,024 node shards with unique meshes, a shared standard material, and one directional light. The dense meshes submit about 17.0 million visible triangles per frame; shadows add 3,859 draw calls. Bodies are frozen and collision disabled to isolate rendering. Each case warms up for 90 frames and samples 180. Fades hold standard materials at alpha 0.5; these measure steady rendering cost, excluding the one-time fade material allocations.

| Case | Median GPU | GPU p95 | Median rendering CPU | Shadow draw calls |
| --- | --- | --- | --- | --- |
| Opaque, debris shadows on | 11.48 ms | 13.88 ms | 1.60 ms | 3,859 |
| Opaque, debris shadows off | 3.71 ms | 4.79 ms | 0.64 ms | 4 |
| Alpha hash, debris shadows on | 11.50 ms | 12.84 ms | 2.12 ms | 3,859 |
| Alpha blend | 2.88 ms | 3.99 ms | 0.89 ms | 4 |
| Lower mesh detail, debris shadows off | 0.37 ms | 0.38 ms | 0.51 ms | 4 |

The alpha-blend result has different rendering behavior: transparent debris no longer submits shadow passes, so it is not a like-for-like shadowed comparison. Lower-detail meshes reduce visible triangles to about 295,000. GPU timings come from viewport timestamp measurements; rendering CPU sums viewport rendering time and frame setup time. Frame intervals include other engine work and are saved separately in [the raw results](render-profile.json). GPU clocks and scene composition affect these results; this synthetic scene identifies useful controls rather than predicting a particular game's FPS. [Godot render timing API](https://docs.godotengine.org/en/4.6/classes/class_renderingserver.html#class-renderingserver-method-viewport-get-measured-render-time-gpu).

The useful controls are **Shard Shadows**, modest visual mesh detail, fragment count, and debris lifetime. The node/server choice does not change the geometry or shadow work. The frame queue addresses creation spikes; baking addresses geometry/collision preparation; neither removes steady GPU cost.

```sh
godot-mono --path . --rendering-method forward_plus --script res://Tests/render_profile.gd -- --shards=1024 --dense
```

Run without `--dense` for a smaller mesh workload, or adjust `--shards` (128–2,048). Use a real display/GPU; headless rendering cannot provide meaningful GPU timings.

## Final code review

Source capture and repair share one path for baking and gameplay. Each asynchronous operation keeps its original cancellation token, preventing reparenting from reviving old work. Sources and colliders are rechecked before replacement. Skeleton observation is restored on tree re-entry, and final pose buffers are reused. Partial collision ambiguity is reported before spending time cutting. Fractured fires after the busy flag clears, permitting a follow-up hit from its callback. Fire-and-forget calls emit FractureFailed so the demo can recover its controls when a cut does not complete.

Loading fragment material overrides now preserves the original mesh resource and stores those overrides alongside fragment data. Creating a body applies their normal precedence; it no longer rereads, rebuilds or uploads a mesh just to change its material. Fading handles an overall material override as well as individual surfaces, while preserving the shared source material. Small finite object scales are accepted rather than treating a small determinant as zero. Geometry loops reuse buffers instead of allocating arrays, polygons and LINQ results for each grid cell or triangle. Vendor source/license comments are retained; first-party comments focus on constraints that are not obvious from the code.

Local Godot .NET 4.7.2, Linux/headless, one warm-up and three recorded runs, 32-cell forced repair of a 32-segment/16-ring sphere (30,564 output triangles). Both measurements produced 30,564 output triangles and passed the geometry checks. Allocations are managed bytes on the calling thread, rather than total process/native memory; times include validation and vary with CPU load.

| Measurement | Before review | After review |
| --- | --- | --- |
| Managed allocation per repair | 57.46 MB | 25.90 MB |
| Repair time | 1.87–1.89 s | 1.80–1.81 s |
| 128-node spawning peak process interval | 7.34–7.36 ms | 7.27–7.38 ms |

The demonstrated improvement is lower allocation (about 55%). Node spawning remains close to the earlier measurements; no server backend is added. The operation is still substantial worker computation, and uploads/decomposition still have indivisible native costs. Baking remains the practical choice for expensive geometry. Reproduce with `godot-mono --headless --path . res://Tests/tests.tscn -- --benchmark` after building/importing.

## Validation

All 197 integration checks pass in Godot .NET 4.7.2. Non-manifold baking and runtime fracture use generated fixtures, so this suite no longer depends on the external railgun/Blender files. Godot .NET 4.6 is also checked in an isolated project built with SDK 4.6.0. Debug and export builds complete without compiler warnings, and editor import is clean. The demo is rendered with the Compatibility renderer and visually inspected. Integration tests cover irregular custom ArrayMeshes, all four patterns and six noise types, private noise copies, deterministic presets, 100-piece generation and a 256-piece limit, minimum volume, mating rough caps, thin-wall backoff, interior UV scale, resource serialization, preset baking/runtime activation, exact sharp cuts despite noisy presets, watertight caps, volume and winding, hollow shells, multiple holes, nested islands, repeated hollow cuts after mesh upload, tangent/coplanar cuts, curved primitives, automatic duplicate/winding/planar-hole repair, distance-field open-sheet/non-manifold reconstruction, strict-mode rejection, valid-mesh fast paths, multi-mesh transforms/materials/remainders, current skeleton/blend-shape/IK poses, original preservation after baking/failure, reparented modifier rigs, repeated posed cuts, cancellation during shard creation, small valid scales, shared mesh material overrides and their precedence, overall-material fading, failure notifications, shared frame budgets, non-uniform transforms, physics impulses, duplicate calls, static and rigid partial cuts, compound hull baking/loading/scaling and actual collision gaps, imported GLB scenes, cancellation, reparenting, material overrides, save paths, and independent lifetimes.
