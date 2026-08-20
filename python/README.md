# Vendored Python nodes

`ComfyUI-SeedVR2_ImageUpscaler/` is a vendored copy of
[jtreminio/ComfyUI-SeedVR2_ImageUpscaler](https://github.com/jtreminio/ComfyUI-SeedVR2_ImageUpscaler)
(MIT license, see `ComfyUI-SeedVR2_ImageUpscaler/LICENSE`), itself a fork of
[moonwhaler/comfyui-seedvr2-tilingupscaler](https://github.com/moonwhaler/comfyui-seedvr2-tilingupscaler).

## Why this is vendored instead of installed

SwarmUI core also bundles a copy of this same package directly (as a "DLNode", under
`src/BuiltinExtensions/ComfyUIBackend/DLNodes/ComfyUI-SeedVR2_ImageUpscaler`), which is what
actually provides the `SeedVR2ImageUpscaler` ComfyUI node today - this extension's own
`InstallableFeature` for it (see `SeedVR2UpscalerExtension.cs`) currently never fires, since
core's copy already satisfies that feature flag first.

That upstream/core copy has a bug: `_batch_upscale_tiles` in `stitching.py` color-corrects
each tile independently against only its own local crop. Tiles with different content (e.g.
skin vs. background) drift to different color statistics, and the mismatch shows up as a
visible, often smeared, discolored patch right at the tile boundary once stitched - most
visible on limbs/extremities, since those are more likely to straddle a tile edge than a
centered face or torso.

The copy here has that fixed: per-tile color correction is disabled, and the requested
correction is instead applied once, globally, after stitching, against the existing
`_create_base_image` reference (see `_match_color_lab` in `stitching.py`). PRs with the same
fix are open against both jtreminio's and moonwhaler's repos; this vendored copy exists so the
fix is live immediately rather than waiting on either to merge, and stays live even if SwarmUI
core's own DLNodes copy gets out of sync or reverted.

## How it takes effect

`SeedVR2UpscalerExtension.OnInit()` registers this folder with
`ComfyUISelfStartBackend.CustomNodePaths`. ComfyUI loads custom node roots in order
(`DLNodes;ExtraNodes;{CustomNodePaths}` - see `ComfyUISelfStartBackend.cs`), and
`load_custom_node()` does a plain `NODE_CLASS_MAPPINGS[name] = node_cls` overwrite with no
duplicate check against nodes loaded earlier in the same pass. Since `CustomNodePaths` loads
after `DLNodes`, this copy's `SeedVR2ImageUpscaler` registration deterministically wins over
core's.

This has no effect on SwarmUI core's *native* SeedVR2 support (`CreateSeedVR2Restore` /
`RunSeedVR2Stage` in `WorkflowGenerator.cs`), which is built entirely from separate primitive
nodes (`SeedVR2Preprocess`, `SeedVR2Conditioning`, `SeedVR2TemporalChunk`,
`SeedVR2TemporalMerge`, `SeedVR2PostProcessing`) defined in ComfyUI's own
`comfy_extras/nodes_seedvr.py` and loaded through a different code path
(`init_builtin_extra_nodes`, not `init_external_custom_nodes`). Native support never
references `SeedVR2ImageUpscaler` at all.

## Updating

If jtreminio's or moonwhaler's PR merges (or any other upstream change is worth pulling in),
re-sync this folder from the upstream repo and re-apply the `_match_color_lab` /
`_batch_upscale_tiles` changes if they haven't landed yet.
