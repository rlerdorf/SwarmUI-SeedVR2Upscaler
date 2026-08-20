using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace SeedVR2Upscaler;

/// <summary>Image-upscaler specific registration and workflow helpers.</summary>
public static class SeedVR2ImageUpscaler
{
    /// <summary>Registers image-specific SeedVR2 params.</summary>
    public static (
        T2IRegisteredParam<int> TileSize,
        T2IRegisteredParam<int> MaskBlur,
        T2IRegisteredParam<int> TileOverlap,
        T2IRegisteredParam<int> TileUpscaleResolution,
        T2IRegisteredParam<string> TilingStrategy,
        T2IRegisteredParam<double> AntiAliasingStrength,
        T2IRegisteredParam<string> BlendingMethod
    ) RegisterParams(T2IParamGroup group)
    {
        T2IRegisteredParam<int> tileSize = T2IParamTypes.Register<int>(new(
            "SeedVR2 Image Tile Size",
            "Square tile size for both width and height.\n" +
            "Smaller tiles use less VRAM but may show more seams.",
            "512", Min: 64, Max: 8192, ViewMax: 2048, Step: 8,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 14
        ));

        T2IRegisteredParam<int> maskBlur = T2IParamTypes.Register<int>(new(
            "SeedVR2 Image Mask Blur",
            "Tile edge blending amount for tiled image upscaling.\n0 keeps maximum detail; higher values smooth seams more aggressively.",
            "0", Min: 0, Max: 64, Step: 1,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 15
        ));

        T2IRegisteredParam<int> tileOverlap = T2IParamTypes.Register<int>(new(
            "SeedVR2 Image Tile Overlap",
            "Overlap between neighboring tiles in pixels.\nHigher overlap reduces seam risk but increases processing cost.",
            "32", Min: 0, Max: 8192, ViewMax: 256, Step: 8,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 16
        ));

        T2IRegisteredParam<int> tileUpscaleResolution = T2IParamTypes.Register<int>(new(
            "SeedVR2 Image Tile Upscale Resolution",
            "Resolution for upscaling each tile.\n" +
            "Set to 0 to auto-calculate from tile size and target resolution.\n" +
            "Higher values can increase detail but require more VRAM.",
            "0", Min: 0, Max: 8192, ViewMax: 4096, Step: 8,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 17
        ));

        T2IRegisteredParam<string> tilingStrategy = T2IParamTypes.Register<string>(new(
            "SeedVR2 Image Tiling Strategy",
            "Tile processing order for tiled image upscaling.\nChess can improve blending consistency; Linear can be faster.",
            "Chess",
            GetValues: _ => ["Chess", "Linear"],
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 18
        ));

        T2IRegisteredParam<double> antiAliasingStrength = T2IParamTypes.Register<double>(new(
            "SeedVR2 Image Anti-Aliasing Strength",
            "Edge-aware smoothing strength for tiled image output.\n0 disables smoothing; small values (0.1-0.3) are typically subtle.",
            "0.0", Min: 0.0, Max: 1.0, Step: 0.05,
            Toggleable: true, IsAdvanced: true,
            ViewType: ParamViewType.SLIDER,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 19
        ));

        T2IRegisteredParam<string> blendingMethod = T2IParamTypes.Register<string>(new(
            "SeedVR2 Image Blending Method",
            "Tile blending algorithm for tiled image upscaling.\n'multiband' is highest quality, 'simple' is fastest.",
            "auto",
            GetValues: _ => ["auto", "multiband", "bilateral", "content_aware", "linear", "simple"],
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_image_upscaler",
            Group: group,
            OrderPriority: 20
        ));

        return (tileSize, maskBlur, tileOverlap, tileUpscaleResolution, tilingStrategy, antiAliasingStrength, blendingMethod);
    }

    /// <summary>Returns true if an image upscaler node is available.</summary>
    public static bool IsAvailable(WorkflowGenerator g)
    {
        return g.Features.Contains("seedvr2_image_upscaler");
    }

    /// <summary>Creates and returns a SeedVR2 image upscaler node.</summary>
    /// <remarks>
    /// Color correction is always sent as "none" here. The tiled node applies whatever method
    /// is requested independently per tile, matching each tile's output statistics to only that
    /// tile's own local crop - neighboring tiles with different content (eg skin vs. background)
    /// end up graded differently, and the feathered blend at the seam smears the two disagreeing
    /// gradings together into a visible discolored patch. Correction is instead applied once,
    /// globally, after generation - see <see cref="SeedVR2UpscalerExtension.HandleSeedVR2PostGeneration"/>.
    /// This also disables correction on the node's internal reference-image call, which is harmless
    /// since that reference is only used as a structural canvas for blending, not for color.
    /// </remarks>
    public static string CreateNode(WorkflowGenerator g, JArray imageInputForUpscaler, string ditLoaderNode, string vaeLoaderNode, long seed, int resolution, string tensorOffloadDevice)
    {
        JObject inputs = new()
        {
            ["image"] = imageInputForUpscaler,
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["tile_size"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageTileSize, 512),
            ["mask_blur"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageMaskBlur, 0),
            ["tile_overlap"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageTileOverlap, 32),
            ["tile_upscale_resolution"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageTileUpscaleResolution, 0),
            ["tiling_strategy"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageTilingStrategy, "Chess"),
            ["anti_aliasing_strength"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageAntiAliasingStrength, 0.0),
            ["blending_method"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2ImageBlendingMethod, "auto"),
            ["color_correction"] = "none",
            ["input_noise_scale"] = g.UserInput.Get(SeedVR2UpscalerExtension.SeedVR2InputNoiseScale, 0.0),
            ["offload_device"] = tensorOffloadDevice,
            ["enable_debug"] = false
        };

        return g.CreateNode("SeedVR2ImageUpscaler", inputs);
    }
}
