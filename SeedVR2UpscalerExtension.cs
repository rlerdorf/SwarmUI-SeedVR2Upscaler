using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SeedVR2Upscaler;

/// <summary>Extension for SeedVR2 video/image upscaling integration.</summary>
public class SeedVR2UpscalerExtension : Extension
{
    /// <summary>Registered parameter for SeedVR2 model / preset selection.</summary>
    public static T2IRegisteredParam<string> SeedVR2Model;

    /// <summary>Registered parameter for SeedVR2 upscale factor (applied after the normal workflow output).</summary>
    public static T2IRegisteredParam<double> SeedVR2UpscaleBy;

    /// <summary>Registered parameter for SeedVR2 block swap count.</summary>
    public static T2IRegisteredParam<int> SeedVR2BlockSwap;

    /// <summary>Registered parameter for SeedVR2 color correction method.</summary>
    public static T2IRegisteredParam<string> SeedVR2ColorCorrection;

    /// <summary>Registered parameter for SeedVR2 2-step mode (downscale before upscale).</summary>
    public static T2IRegisteredParam<bool> SeedVR2TwoStepMode;

    /// <summary>Registered parameter for SeedVR2 pre-downscale factor.</summary>
    public static T2IRegisteredParam<double> SeedVR2PreDownscale;

    /// <summary>Registered parameter for SeedVR2 tiled VAE mode.</summary>
    public static T2IRegisteredParam<bool> SeedVR2TiledVAE;

    /// <summary>Registered parameter for SeedVR2 latent noise scale.</summary>
    public static T2IRegisteredParam<double> SeedVR2LatentNoiseScale;

    /// <summary>Registered parameter for SeedVR2 model caching.</summary>
    public static T2IRegisteredParam<bool> SeedVR2CacheModel;

    /// <summary>Registered parameter for SeedVR2 video batch size.</summary>
    public static T2IRegisteredParam<int> SeedVR2VideoBatchSize;

    /// <summary>Registered parameter for SeedVR2 temporal overlap for video.</summary>
    public static T2IRegisteredParam<int> SeedVR2TemporalOverlap;

    /// <summary>Registered parameter for SeedVR2 uniform batch size mode.</summary>
    public static T2IRegisteredParam<bool> SeedVR2UniformBatchSize;

    /// <summary>Registered parameter for SeedVR2 video file path (for upscaling existing videos).</summary>
    public static T2IRegisteredParam<string> SeedVR2VideoFile;

    /// <summary>Registered parameter for SeedVR2 image file path (for upscaling existing images).</summary>
    public static T2IRegisteredParam<string> SeedVR2ImageFile;

    /// <summary>Parameter group for SeedVR2 settings.</summary>
    public static T2IParamGroup SeedVR2Group;

    /// <summary>Model filename mapping from UI selection to actual model files.</summary>
    public static readonly Dictionary<string, string> DiTModelMap = new()
    {
        ["seedvr2-3b-q4"] = "seedvr2_ema_3b-Q4_K_M.gguf",
        ["seedvr2-3b-q8"] = "seedvr2_ema_3b-Q8_0.gguf",
        ["seedvr2-3b-fp8"] = "seedvr2_ema_3b_fp8_e4m3fn.safetensors",
        ["seedvr2-3b-fp16"] = "seedvr2_ema_3b_fp16.safetensors",
        ["seedvr2-7b-q4"] = "seedvr2_ema_7b-Q4_K_M.gguf",
        ["seedvr2-7b-fp8"] = "seedvr2_ema_7b_fp8_e4m3fn_mixed_block35_fp16.safetensors",
        ["seedvr2-7b-fp16"] = "seedvr2_ema_7b_fp16.safetensors",
        ["seedvr2-7b-sharp-q4"] = "seedvr2_ema_7b_sharp-Q4_K_M.gguf",
        ["seedvr2-7b-sharp-fp8"] = "seedvr2_ema_7b_sharp_fp8_e4m3fn_mixed_block35_fp16.safetensors",
        ["seedvr2-7b-sharp-fp16"] = "seedvr2_ema_7b_sharp_fp16.safetensors",
    };

    /// <summary>Quality preset configurations: (modelKey, blockSwap, tiledVAE).</summary>
    public static readonly Dictionary<string, (string ModelKey, int BlockSwap, bool TiledVAE)> QualityPresets = new()
    {
        ["seedvr2-preset-fast"] = ("seedvr2-3b-q4", 20, true),
        ["seedvr2-preset-balanced"] = ("seedvr2-3b-fp8", 12, false),
        ["seedvr2-preset-quality"] = ("seedvr2-7b-fp8", 16, true),
        ["seedvr2-preset-max"] = ("seedvr2-7b-sharp-fp16", 0, false),
    };

    /// <inheritdoc/>
    public override void OnPreInit()
    {
        Logs.Info("SeedVR2 Upscaler Extension loading...");
    }

    /// <inheritdoc/>
    public override void OnInit()
    {
        // Register feature mapping for node detection
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2VideoUpscaler"] = "seedvr2_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2LoadDiTModel"] = "seedvr2_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2LoadVAEModel"] = "seedvr2_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2TorchCompileSettings"] = "seedvr2_upscaler";

        // Register KJNodes feature for VRAM cleanup support
        ComfyUIBackendExtension.NodeToFeatureMap["VRAM_Debug"] = "kjnodes";

        // Register installable feature for the SeedVR2 ComfyUI node
        InstallableFeatures.RegisterInstallableFeature(new("SeedVR2 Video Upscaler", "seedvr2_upscaler", "https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler", "numz", "This will install the SeedVR2 Video Upscaler ComfyUI node by numz.\nDo you wish to install?"));

        // Add the JS file for the install button
        ScriptFiles.Add("assets/seedvr2_install.js");

        SeedVR2Group = new("SeedVR2 Upscaler", Toggles: true, Open: false, IsAdvanced: false,
            Description: "Implements <a class=\"translate\" href=\"https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler\">https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler</a>\n" +
            "IMAGE UPSCALING: Enable this group and set 'SeedVR2 Upscale By' to your desired scale.\n" +
            "VIDEO GENERATION: Also enable 'SeedVR2 Video Batch Size' to upscale videos during generation.\n" +
            "EXISTING MEDIA: Select an image/video in history and click 'SeedVR2 Upscale'.");

        SeedVR2Model = T2IParamTypes.Register<string>(new(
            "SeedVR2 Model",
            "Which SeedVR2 model/preset to use.\nPresets auto-configure optimal settings based on typical VRAM constraints.\nAuto will detect VRAM and select a configuration.",
            "seedvr2-auto",
            GetValues: _ => [
                "seedvr2-auto///Auto (VRAM-based)",
                "seedvr2-preset-fast///Fast (3B Q4)",
                "seedvr2-preset-balanced///Balanced (3B FP8)",
                "seedvr2-preset-quality///Quality (7B FP8)",
                "seedvr2-preset-max///Max Quality (7B Sharp FP16)"
            ],
            Group: SeedVR2Group,
            OrderPriority: 0
        ));

        SeedVR2UpscaleBy = T2IParamTypes.Register<double>(new(
            "SeedVR2 Upscale By",
            "How much to upscale the final decoded image by before/while running SeedVR2.\n" +
            "1.0 keeps the same size (detail enhancement pass).\n" +
            "The slider UI is capped at 4.0, but you can type a higher value if desired.",
            "1",
            IgnoreIf: "1",
            Min: 1, Max: 16, ViewMax: 4, Step: 0.25,
            ViewType: ParamViewType.SLIDER,
            Group: SeedVR2Group,
            OrderPriority: 1
        ));

        SeedVR2BlockSwap = T2IParamTypes.Register<int>(new(
            "SeedVR2 Block Swap",
            "Number of transformer blocks to swap to CPU for VRAM optimization.\n0 = disabled, higher = more VRAM savings but slower.\n3B model max: 32, 7B model max: 36.\nNote: Presets auto-configure this value.",
            "0", Min: 0, Max: 36, Step: 1,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 2
        ));

        SeedVR2ColorCorrection = T2IParamTypes.Register<string>(new(
            "SeedVR2 Color Correction",
            "Color correction method for SeedVR2 upscaling.\nNone uses SeedVR2's native output. Other options adjust colors to match the original image.",
            "none",
            GetValues: _ => ["none", "lab", "wavelet", "wavelet_adaptive", "hsv", "adain"],
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 3
        ));

        SeedVR2TwoStepMode = T2IParamTypes.Register<bool>(new(
            "SeedVR2 2-Step Mode",
            "Enable 2-step upscaling: downscales the image first, then upscales with SeedVR2.\nThis can reduce artifacts by giving SeedVR2 a cleaner input to work with.",
            "true",
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 4
        ));

        SeedVR2PreDownscale = T2IParamTypes.Register<double>(new(
            "SeedVR2 Pre-Downscale",
            "Factor to downscale the image before SeedVR2 upscaling (when 2-step mode is enabled).\n0.5 means halve the resolution before upscaling.",
            "0.5", Min: 0.25, Max: 0.9, Step: 0.05,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 5
        ));

        SeedVR2TiledVAE = T2IParamTypes.Register<bool>(new(
            "SeedVR2 Tiled VAE",
            "Enable tiled VAE encoding/decoding for large images.\nReduces VRAM usage but may be slightly slower.\nNote: Presets auto-configure this value.",
            "false",
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 6
        ));

        SeedVR2LatentNoiseScale = T2IParamTypes.Register<double>(new(
            "SeedVR2 Latent Noise",
            "Amount of noise to add in the latent space during upscaling.\nCan enhance detail generation. 0 = no noise.",
            "0.0", Min: 0.0, Max: 1.0, Step: 0.05,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 9
        ));

        SeedVR2CacheModel = T2IParamTypes.Register<bool>(new(
            "SeedVR2 Cache Model",
            "Keep SeedVR2 models loaded in VRAM between generations.\nFaster for batch processing but uses more VRAM.",
            "false",
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 10
        ));

        // Video-specific parameters
        SeedVR2VideoBatchSize = T2IParamTypes.Register<int>(new(
            "SeedVR2 Video Batch Size",
            "Number of frames to process in each batch for video upscaling.\nLower = less VRAM, higher = faster processing.\nRecommended: 17-33 for most GPUs.",
            "33", Min: 1, Max: 64, Step: 1,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 11
        ));

        SeedVR2TemporalOverlap = T2IParamTypes.Register<int>(new(
            "SeedVR2 Temporal Overlap",
            "Number of overlapping frames between batches for temporal consistency.\nHigher = smoother transitions but slower.",
            "3", Min: 0, Max: 16, Step: 1,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 12
        ));

        SeedVR2UniformBatchSize = T2IParamTypes.Register<bool>(new(
            "SeedVR2 Uniform Batch Size",
            "Use uniform batch sizes for all video chunks.\nEnable for more consistent results.",
            "true",
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 13
        ));

        // Hidden parameters - used internally by the SeedVR2 Upscale button in output history
        // VisibleNormally: false hides them from the UI
        SeedVR2VideoFile = T2IParamTypes.Register<string>(new(
            "SeedVR2 Video File",
            "Internal parameter for video file upscaling.",
            "",
            FeatureFlag: "seedvr2_upscaler",
            ChangeWeight: 2,
            VisibleNormally: false
        ));

        SeedVR2ImageFile = T2IParamTypes.Register<string>(new(
            "SeedVR2 Image File",
            "Internal parameter for image file upscaling.",
            "",
            FeatureFlag: "seedvr2_upscaler",
            ChangeWeight: 2,
            VisibleNormally: false
        ));

        // Add workflow generation steps
        // Priority -2: Image file upscaling (runs first, creates entire workflow if image file is set)
        WorkflowGenerator.AddStep(GenerateSeedVR2ImageFileWorkflow, -2);
        // Priority -1: Video file upscaling (runs first, creates entire workflow if video file is set)
        WorkflowGenerator.AddStep(GenerateSeedVR2VideoFileWorkflow, -1);
        // Priority 6: For images only - after VAEDecode (1) and Segmentation/FaceRefine (5), before SaveImage (10)
        // This ensures face refinement happens at original resolution, then SeedVR2 upscales the result
        WorkflowGenerator.AddStep(GenerateSeedVR2Workflow, 6);
        // Priority 15: For video generation - after video generation and save at priority 11
        // Uses ReplaceNodeConnection to update the existing save node to use upscaled frames
        WorkflowGenerator.AddStep(GenerateSeedVR2VideoGenerationWorkflow, 15);

        Logs.Info("SeedVR2 Upscaler Extension loaded successfully.");
    }

    /// <summary>Detects available GPU VRAM and returns the best model configuration.</summary>
    /// <returns>Tuple of (modelKey, blockSwap, tiledVAE) based on detected VRAM.</returns>
    public static (string ModelKey, int BlockSwap, bool TiledVAE) DetectVRAMAndSelectModel()
    {
        try
        {
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is null || gpus.Length == 0)
            {
                Logs.Warning("SeedVR2 Auto: Could not detect GPU VRAM, defaulting to 3B FP8 with block swap");
                return ("seedvr2-3b-fp8", 16, true);
            }

            // Use the GPU with most VRAM
            NvidiaUtil.NvidiaInfo bestGpu = gpus.OrderByDescending(g => g.TotalMemory.InBytes).First();
            double vramGiB = bestGpu.TotalMemory.GiB;

            Logs.Info($"SeedVR2 Auto: Detected GPU '{bestGpu.GPUName}' with {vramGiB:F1} GiB VRAM");

            // Select model based on VRAM thresholds
            // Note: These are conservative estimates accounting for base model already loaded
            if (vramGiB >= 24)
            {
                // 24GB+: Can run 7B Sharp FP16 without block swap
                Logs.Info("SeedVR2 Auto: Selected 7B Sharp FP16 (max quality)");
                return ("seedvr2-7b-sharp-fp16", 0, false);
            }
            else if (vramGiB >= 20)
            {
                // 20-24GB: 7B FP8 with light block swap
                Logs.Info("SeedVR2 Auto: Selected 7B FP8 with block swap 8");
                return ("seedvr2-7b-fp8", 8, false);
            }
            else if (vramGiB >= 16)
            {
                // 16-20GB: 7B Q4 or 3B FP16 with block swap
                Logs.Info("SeedVR2 Auto: Selected 7B Q4 with block swap 16");
                return ("seedvr2-7b-q4", 16, true);
            }
            else if (vramGiB >= 12)
            {
                // 12-16GB: 3B FP8 with moderate block swap
                Logs.Info("SeedVR2 Auto: Selected 3B FP8 with block swap 12");
                return ("seedvr2-3b-fp8", 12, true);
            }
            else if (vramGiB >= 8)
            {
                // 8-12GB: 3B Q4 with heavy block swap
                Logs.Info("SeedVR2 Auto: Selected 3B Q4 with block swap 20");
                return ("seedvr2-3b-q4", 20, true);
            }
            else
            {
                // <8GB: 3B Q4 with maximum block swap, tiled VAE
                Logs.Warning($"SeedVR2 Auto: Low VRAM ({vramGiB:F1} GiB) - using 3B Q4 with maximum optimization");
                return ("seedvr2-3b-q4", 28, true);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"SeedVR2 Auto: Error detecting VRAM: {ex.Message}, defaulting to 3B FP8");
            return ("seedvr2-3b-fp8", 16, true);
        }
    }

    /// <summary>Generates the SeedVR2 workflow nodes for image upscaling only.</summary>
    /// <remarks>
    /// Runs at priority 6: after VAEDecode (1) and Segmentation/FaceRefine (5), before SaveImage (10).
    /// For video generation upscaling, see GenerateSeedVR2VideoGenerationWorkflow at priority 15.
    /// </remarks>
    public static void GenerateSeedVR2Workflow(WorkflowGenerator g)
    {
        // Skip if video file mode is active (handled by GenerateSeedVR2VideoFileWorkflow)
        if (g.UserInput.TryGet(SeedVR2VideoFile, out string videoFile) && !string.IsNullOrEmpty(videoFile))
        {
            return;
        }

        // Skip if image file mode is active (handled by GenerateSeedVR2ImageFileWorkflow)
        if (g.UserInput.TryGet(SeedVR2ImageFile, out string imageFile) && !string.IsNullOrEmpty(imageFile))
        {
            return;
        }

        // Skip video generation mode - handled by GenerateSeedVR2VideoGenerationWorkflow at priority 15
        // (Video generation happens at priority 11, so we can't intercept it here at priority 6)
        if (g.UserInput.TryGet(SeedVR2VideoBatchSize, out _))
        {
            return;
        }

        // Only activate if the group toggle is enabled
        if (!g.UserInput.TryGet(SeedVR2Model, out string modelChoice))
        {
            return;
        }

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 upscaler selected, but SeedVR2 nodes are not installed in ComfyUI. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // Ensure we have an image to upscale
        if (g.FinalImageOut is null)
        {
            return;
        }

        // Determine model variant and settings from selection
        string modelKey = modelChoice.Before("///");
        int blockSwap;
        bool tiledVAE;
        bool isPresetOrAuto = false;

        if (modelKey == "seedvr2-auto")
        {
            // Auto mode: detect VRAM and select best configuration
            (modelKey, blockSwap, tiledVAE) = DetectVRAMAndSelectModel();
            isPresetOrAuto = true;
        }
        else if (QualityPresets.TryGetValue(modelKey, out var preset))
        {
            // Quality preset: use predefined configuration
            modelKey = preset.ModelKey;
            blockSwap = preset.BlockSwap;
            tiledVAE = preset.TiledVAE;
            isPresetOrAuto = true;
            Logs.Info($"SeedVR2: Using preset with model={modelKey}, blockSwap={blockSwap}, tiledVAE={tiledVAE}");
        }
        else
        {
            // Manual selection: use user's parameter values
            blockSwap = g.UserInput.Get(SeedVR2BlockSwap, 0);
            tiledVAE = g.UserInput.Get(SeedVR2TiledVAE, false);
        }

        // Allow user to override preset settings if they explicitly set the parameters
        if (isPresetOrAuto)
        {
            // Only override if user explicitly enabled the parameter (Toggleable parameters)
            if (g.UserInput.TryGet(SeedVR2BlockSwap, out int userBlockSwap))
            {
                blockSwap = userBlockSwap;
            }
            if (g.UserInput.TryGet(SeedVR2TiledVAE, out bool userTiledVAE))
            {
                tiledVAE = userTiledVAE;
            }
        }

        // Get the actual model filename
        if (!DiTModelMap.TryGetValue(modelKey, out string ditModel))
        {
            ditModel = "seedvr2_ema_3b_fp8_e4m3fn.safetensors"; // Fallback
            Logs.Warning($"SeedVR2: Unknown model key '{modelKey}', falling back to 3B FP8");
        }

        // Calculate target resolution based on upscale factor
        double upscaleFactor = g.UserInput.Get(T2IParamTypes.RefinerUpscale, 1.0);
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.0);
        int baseWidth = (int)Math.Round(g.UserInput.GetImageWidth() * upscaleFactor);
        int baseHeight = (int)Math.Round(g.UserInput.GetImageHeight() * upscaleFactor);
        int targetWidth = (int)Math.Round(baseWidth * seedvrUpscaleBy);
        int targetHeight = (int)Math.Round(baseHeight * seedvrUpscaleBy);
        // SeedVR2 uses the shortest edge as the resolution target
        int resolution = Math.Min(targetWidth, targetHeight);

        // Get other optional parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        bool twoStepMode = g.UserInput.Get(SeedVR2TwoStepMode, false);
        double preDownscale = g.UserInput.Get(SeedVR2PreDownscale, 0.5);
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);

        // Determine if using 3B or 7B model and cap block swap accordingly
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            Logs.Warning($"SeedVR2: Block swap {blockSwap} exceeds max {maxBlocks} for {(is7BModel ? "7B" : "3B")} model, capping");
            blockSwap = maxBlocks;
        }

        string modeInfo = twoStepMode ? $" [2-Step: downscale {preDownscale}x first]" : "";
        string configInfo = $"model={ditModel}, upscale={seedvrUpscaleBy:0.###}, blockSwap={blockSwap}, tiledVAE={tiledVAE}";
        Logs.Info($"SeedVR2: Upscaling {baseWidth}x{baseHeight} -> resolution={resolution}{modeInfo} ({configInfo})");

        // Determine offload device based on blockswap setting
        string offloadDevice = blockSwap > 0 ? "cpu" : "none";

        // Create SeedVR2LoadDiTModel node
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = "cuda:0",
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,  // Also swap I/O components if using block swap
            ["offload_device"] = offloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = "sdpa"
        };

        // Add VRAM cleanup node to unload main generation model before SeedVR2
        // This frees up VRAM from the Flux model so SeedVR2 has enough room
        if (g.Features.Contains("kjnodes"))
        {
            string vramCleanupNode = g.CreateNode("VRAM_Debug", new JObject()
            {
                ["empty_cache"] = true,
                ["gc_collect"] = true,
                ["unload_all_models"] = true,
                ["image_pass"] = g.FinalImageOut  // Pass through the image to maintain workflow connection
            });
            // Update FinalImageOut to use the passthrough from VRAM_Debug
            g.FinalImageOut = new JArray() { vramCleanupNode, 1 };  // output index 1 is image_pass
        }

        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // Create SeedVR2LoadVAEModel node with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = "cuda:0",
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = tiledVAE ? "cpu" : "none",
            ["cache_model"] = cacheModel
        };
        if (tiledVAE)
        {
            vaeLoaderInputs["encode_tile_size"] = 1024;
            vaeLoaderInputs["encode_tile_overlap"] = 128;
            vaeLoaderInputs["decode_tile_size"] = 1024;
            vaeLoaderInputs["decode_tile_overlap"] = 128;
        }
        string vaeLoaderNode = g.CreateNode("SeedVR2LoadVAEModel", vaeLoaderInputs);

        // Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // 2-Step Mode: Downscale the image first before feeding to SeedVR2
        JArray imageInputForUpscaler = g.FinalImageOut;
        if (twoStepMode)
        {
            string downscaleNode = g.CreateNode("ImageScaleBy", new JObject()
            {
                ["image"] = g.FinalImageOut,
                ["upscale_method"] = "lanczos",
                ["scale_by"] = preDownscale
            });
            imageInputForUpscaler = new JArray() { downscaleNode, 0 };
        }

        // Create SeedVR2VideoUpscaler node (for single image)
        JObject upscalerInputs = new JObject()
        {
            ["image"] = imageInputForUpscaler,
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["max_resolution"] = resolution,
            ["batch_size"] = 1,
            ["uniform_batch_size"] = false,
            ["temporal_overlap"] = 0,
            ["prepend_frames"] = 0,
            ["color_correction"] = colorCorrection,
            ["input_noise_scale"] = 0.0,
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };

        Logs.Info($"SeedVR2: Upscaling image to resolution {resolution} (model={ditModel}, blockSwap={blockSwap})");

        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);

        // Update final image output to point to upscaler output
        g.FinalImageOut = new JArray() { upscalerNode, 0 };
    }

    /// <summary>Generates SeedVR2 workflow for upscaling existing image files.</summary>
    /// <remarks>
    /// Runs at priority -2 (before video). When SeedVR2ImageFile is set, creates a complete
    /// image upscaling workflow that loads, upscales, and saves the image.
    /// </remarks>
    public static void GenerateSeedVR2ImageFileWorkflow(WorkflowGenerator g)
    {
        // Only activate if image file path is set
        if (!g.UserInput.TryGet(SeedVR2ImageFile, out string imageFile) || string.IsNullOrEmpty(imageFile))
        {
            return;
        }

        Logs.Info($"SeedVR2 Image File Mode: Processing image '{imageFile}'");

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 Image File upscaling requires SeedVR2 nodes. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // Expand path (support ~ for home directory)
        if (imageFile.StartsWith("~/"))
        {
            imageFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + imageFile[1..];
        }

        // Resolve Output/ paths to actual file system paths using user's output directory
        User user = g.UserInput.SourceSession?.User;
        if (user is not null && imageFile.StartsWith("Output/"))
        {
            string relativePath = imageFile["Output/".Length..];
            string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
            imageFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
        }

        // Validate the file exists
        if (!System.IO.File.Exists(imageFile))
        {
            throw new SwarmUserErrorException($"SeedVR2 Image File not found: {imageFile}");
        }

        // Get image dimensions to calculate upscaled resolution
        int origWidth = 0, origHeight = 0;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(imageFile);
            origWidth = image.Width;
            origHeight = image.Height;
            Logs.Info($"SeedVR2 Image File: Source dimensions {origWidth}x{origHeight}");
        }
        catch (Exception ex)
        {
            Logs.Warning($"SeedVR2 Image File: Could not read image dimensions: {ex.Message}");
        }

        // Determine model variant and settings from SeedVR2Model parameter
        string modelChoice = g.UserInput.Get(SeedVR2Model, "seedvr2-preset-balanced");
        string modelKey = modelChoice.Before("///");
        if (string.IsNullOrEmpty(modelKey) || !modelKey.StartsWith("seedvr2-"))
        {
            modelKey = "seedvr2-preset-balanced";
        }

        int blockSwap;
        bool tiledVAE;
        bool isPresetOrAuto = false;

        if (modelKey == "seedvr2-auto")
        {
            (modelKey, blockSwap, tiledVAE) = DetectVRAMAndSelectModel();
            isPresetOrAuto = true;
        }
        else if (QualityPresets.TryGetValue(modelKey, out var preset))
        {
            modelKey = preset.ModelKey;
            blockSwap = preset.BlockSwap;
            tiledVAE = preset.TiledVAE;
            isPresetOrAuto = true;
        }
        else
        {
            blockSwap = g.UserInput.Get(SeedVR2BlockSwap, 0);
            tiledVAE = g.UserInput.Get(SeedVR2TiledVAE, false);
        }

        // Allow user overrides
        if (isPresetOrAuto)
        {
            if (g.UserInput.TryGet(SeedVR2BlockSwap, out int userBlockSwap))
            {
                blockSwap = userBlockSwap;
            }
            if (g.UserInput.TryGet(SeedVR2TiledVAE, out bool userTiledVAE))
            {
                tiledVAE = userTiledVAE;
            }
        }

        // Get actual model filename
        if (!DiTModelMap.TryGetValue(modelKey, out string ditModel))
        {
            ditModel = "seedvr2_ema_3b_fp8_e4m3fn.safetensors";
            Logs.Warning($"SeedVR2 Image File: Unknown model key '{modelKey}', falling back to 3B FP8");
        }

        // Calculate target resolution based on upscale factor
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.5);
        int resolution;
        if (origWidth > 0 && origHeight > 0)
        {
            int targetWidth = (int)Math.Round(origWidth * seedvrUpscaleBy);
            int targetHeight = (int)Math.Round(origHeight * seedvrUpscaleBy);
            resolution = Math.Min(targetWidth, targetHeight);
        }
        else
        {
            // Fallback if we couldn't read dimensions
            resolution = (int)(1024 * seedvrUpscaleBy);
        }

        // Get other parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);
        bool twoStepMode = g.UserInput.Get(SeedVR2TwoStepMode, false);
        double preDownscale = g.UserInput.Get(SeedVR2PreDownscale, 0.5);

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            blockSwap = maxBlocks;
        }

        string offloadDevice = blockSwap > 0 ? "cpu" : "none";

        Logs.Info($"SeedVR2 Image File: model={ditModel}, blockSwap={blockSwap}, upscaleBy={seedvrUpscaleBy}, resolution={resolution}");

        // === Create workflow nodes ===

        // 1. LoadImage node - load the existing image file
        string loadImageNode = g.CreateNode("LoadImage", new JObject()
        {
            ["image"] = imageFile
        });

        // 2. SeedVR2LoadDiTModel - load the upscaler model
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = "cuda:0",
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = offloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = "sdpa"
        };
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // 3. SeedVR2LoadVAEModel - load VAE with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = "cuda:0",
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = tiledVAE ? "cpu" : "none",
            ["cache_model"] = cacheModel
        };
        if (tiledVAE)
        {
            vaeLoaderInputs["encode_tile_size"] = 1024;
            vaeLoaderInputs["encode_tile_overlap"] = 128;
            vaeLoaderInputs["decode_tile_size"] = 1024;
            vaeLoaderInputs["decode_tile_overlap"] = 128;
        }
        string vaeLoaderNode = g.CreateNode("SeedVR2LoadVAEModel", vaeLoaderInputs);

        // 4. Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // 5. Optional 2-Step Mode: Downscale the image first
        JArray imageInputForUpscaler = new JArray() { loadImageNode, 0 };
        if (twoStepMode)
        {
            string downscaleNode = g.CreateNode("ImageScaleBy", new JObject()
            {
                ["image"] = imageInputForUpscaler,
                ["upscale_method"] = "lanczos",
                ["scale_by"] = preDownscale
            });
            imageInputForUpscaler = new JArray() { downscaleNode, 0 };
        }

        // 6. SeedVR2VideoUpscaler - upscale the image
        JObject upscalerInputs = new JObject()
        {
            ["image"] = imageInputForUpscaler,
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["max_resolution"] = resolution,
            ["batch_size"] = 1,  // Single image
            ["uniform_batch_size"] = false,
            ["temporal_overlap"] = 0,
            ["prepend_frames"] = 0,
            ["color_correction"] = colorCorrection,
            ["input_noise_scale"] = 0.0,
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };
        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);

        // 7. Set FinalImageOut
        g.FinalImageOut = new JArray() { upscalerNode, 0 };

        // 8. Create SaveImage node since we're skipping further steps
        g.CreateNode("SaveImage", new JObject()
        {
            ["images"] = g.FinalImageOut,
            ["filename_prefix"] = "SeedVR2_upscaled"
        });

        // Mark workflow as complete - skip all other generation steps
        g.SkipFurtherSteps = true;

        Logs.Info($"SeedVR2 Image File: Complete workflow created - loading '{imageFile}', upscaling {seedvrUpscaleBy}x to resolution {resolution}");
    }

    /// <summary>Generates SeedVR2 workflow for upscaling existing video files.</summary>
    /// <remarks>
    /// Runs at priority -1 (first). When SeedVR2VideoFile is set, creates a complete
    /// video upscaling workflow that loads, upscales, and saves the video.
    /// </remarks>
    public static void GenerateSeedVR2VideoFileWorkflow(WorkflowGenerator g)
    {
        // Only activate if video file path is set
        if (!g.UserInput.TryGet(SeedVR2VideoFile, out string videoFile) || string.IsNullOrEmpty(videoFile))
        {
            return;
        }

        Logs.Info($"SeedVR2 Video File Mode: Processing video '{videoFile}'");

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 Video File upscaling requires SeedVR2 nodes. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // Expand path (support ~ for home directory)
        if (videoFile.StartsWith("~/"))
        {
            videoFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + videoFile[1..];
        }

        // Resolve Output/ paths to actual file system paths using user's output directory
        User user = g.UserInput.SourceSession?.User;
        if (user is not null && videoFile.StartsWith("Output/"))
        {
            string relativePath = videoFile["Output/".Length..];
            string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
            videoFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
        }

        // Validate the file exists
        if (!System.IO.File.Exists(videoFile))
        {
            throw new SwarmUserErrorException($"SeedVR2 Video File not found: {videoFile}");
        }

        // Get video parameters
        int videoBatchSize = g.UserInput.Get(SeedVR2VideoBatchSize, 33);
        int temporalOverlap = g.UserInput.Get(SeedVR2TemporalOverlap, 3);
        bool uniformBatchSize = g.UserInput.Get(SeedVR2UniformBatchSize, true);

        Logs.Info($"SeedVR2 Video File: batch_size={videoBatchSize}, temporal_overlap={temporalOverlap}, uniform_batch={uniformBatchSize}");

        // Determine model variant and settings from SeedVR2Model parameter
        string modelChoice = g.UserInput.Get(SeedVR2Model, "seedvr2-preset-balanced");
        string modelKey = modelChoice.Before("///");
        if (string.IsNullOrEmpty(modelKey) || !modelKey.StartsWith("seedvr2-"))
        {
            modelKey = "seedvr2-preset-balanced";  // Default to balanced preset for video file mode
        }

        int blockSwap;
        bool tiledVAE;
        bool isPresetOrAuto = false;

        if (modelKey == "seedvr2-auto")
        {
            (modelKey, blockSwap, tiledVAE) = DetectVRAMAndSelectModel();
            isPresetOrAuto = true;
        }
        else if (QualityPresets.TryGetValue(modelKey, out var preset))
        {
            modelKey = preset.ModelKey;
            blockSwap = preset.BlockSwap;
            tiledVAE = preset.TiledVAE;
            isPresetOrAuto = true;
        }
        else
        {
            blockSwap = g.UserInput.Get(SeedVR2BlockSwap, 32);  // Default to 32 for video
            tiledVAE = g.UserInput.Get(SeedVR2TiledVAE, true);  // Default to tiled for video
        }

        // Allow user overrides
        if (isPresetOrAuto)
        {
            if (g.UserInput.TryGet(SeedVR2BlockSwap, out int userBlockSwap))
            {
                blockSwap = userBlockSwap;
            }
            if (g.UserInput.TryGet(SeedVR2TiledVAE, out bool userTiledVAE))
            {
                tiledVAE = userTiledVAE;
            }
        }

        // Get actual model filename
        if (!DiTModelMap.TryGetValue(modelKey, out string ditModel))
        {
            ditModel = "seedvr2_ema_3b_fp16.safetensors";
            Logs.Warning($"SeedVR2 Video File: Unknown model key '{modelKey}', falling back to 3B FP16");
        }

        // Get resolution settings - use SeedVR2UpscaleBy to calculate target
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 2.0);  // Default 2x for video
        // Resolution will be calculated dynamically based on input video and upscale factor
        // For now, use a sensible default that will be overridden by the actual video dimensions
        int resolution = (int)(540 * seedvrUpscaleBy);  // Assume 540p input, scale up

        // Get other parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            blockSwap = maxBlocks;
        }

        string offloadDevice = blockSwap > 0 ? "cpu" : "none";

        Logs.Info($"SeedVR2 Video File: model={ditModel}, blockSwap={blockSwap}, upscaleBy={seedvrUpscaleBy}, colorCorrection={colorCorrection}");

        // === Create workflow nodes ===

        // 1. LoadVideo node - load the existing video file
        string loadVideoNode = g.CreateNode("LoadVideo", new JObject()
        {
            ["file"] = videoFile
        });

        // 2. SeedVR2LoadDiTModel - load the upscaler model
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = "cuda:0",
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = offloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = "sdpa"
        };
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // 3. SeedVR2LoadVAEModel - load VAE with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = "cuda:0",
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = tiledVAE ? "cpu" : "none",
            ["cache_model"] = cacheModel
        };
        if (tiledVAE)
        {
            vaeLoaderInputs["encode_tile_size"] = 1024;
            vaeLoaderInputs["encode_tile_overlap"] = 128;
            vaeLoaderInputs["decode_tile_size"] = 1024;
            vaeLoaderInputs["decode_tile_overlap"] = 128;
        }
        string vaeLoaderNode = g.CreateNode("SeedVR2LoadVAEModel", vaeLoaderInputs);

        // 4. Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // 5. SeedVR2VideoUpscaler - upscale the video frames
        JObject upscalerInputs = new JObject()
        {
            ["image"] = new JArray() { loadVideoNode, 0 },  // frames output
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["max_resolution"] = 0,  // No limit
            ["batch_size"] = videoBatchSize,
            ["uniform_batch_size"] = uniformBatchSize,
            ["temporal_overlap"] = temporalOverlap,
            ["prepend_frames"] = 0,
            ["color_correction"] = colorCorrection,
            ["input_noise_scale"] = 0.0,
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };
        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);

        // 6. Get audio and fps from loaded video
        JArray videoAudio = new JArray() { loadVideoNode, 1 };  // audio output
        JArray videoFps = new JArray() { loadVideoNode, 2 };    // fps output

        // 7. CreateVideo - combine upscaled frames with original audio
        string createVideoNode = g.CreateNode("CreateVideo", new JObject()
        {
            ["images"] = new JArray() { upscalerNode, 0 },
            ["audio"] = videoAudio,
            ["fps"] = videoFps
        });

        // 8. SaveVideo - save the upscaled result
        // Convert SwarmUI video format to ComfyUI SaveVideo format
        string swarmVideoFormat = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
        string container = "mp4";
        string codec = "auto";
        if (swarmVideoFormat.Contains("-"))
        {
            string[] parts = swarmVideoFormat.Split('-');
            codec = parts[0];
            container = parts[1];
        }
        else
        {
            container = swarmVideoFormat;
        }
        // ComfyUI SaveVideo only supports "mp4" or "auto" containers
        if (container != "mp4" && container != "auto")
        {
            container = "mp4";
        }
        // ComfyUI SaveVideo only supports "h264" or "auto" codecs
        if (codec != "h264" && codec != "auto")
        {
            codec = "auto";
        }
        g.CreateNode("SaveVideo", new JObject()
        {
            ["video"] = new JArray() { createVideoNode, 0 },
            ["filename_prefix"] = "video/SeedVR2_upscaled",
            ["format"] = container,
            ["codec"] = codec
        });

        // Mark workflow as complete - skip all other generation steps
        g.FinalImageOut = new JArray() { upscalerNode, 0 };
        g.SkipFurtherSteps = true;

        Logs.Info($"SeedVR2 Video File: Complete workflow created - loading '{videoFile}', upscaling {seedvrUpscaleBy}x, saving as {codec}-{container}");
    }

    /// <summary>Generates SeedVR2 workflow for upscaling video generation output.</summary>
    /// <remarks>
    /// Runs at priority 15: after video generation and save (priority 11).
    /// Uses ReplaceNodeConnection to update the existing save node to use upscaled frames.
    /// </remarks>
    public static void GenerateSeedVR2VideoGenerationWorkflow(WorkflowGenerator g)
    {
        // Skip if video file mode is active (handled by GenerateSeedVR2VideoFileWorkflow)
        if (g.UserInput.TryGet(SeedVR2VideoFile, out string videoFile) && !string.IsNullOrEmpty(videoFile))
        {
            return;
        }

        // Skip if image file mode is active (handled by GenerateSeedVR2ImageFileWorkflow)
        if (g.UserInput.TryGet(SeedVR2ImageFile, out string imageFile) && !string.IsNullOrEmpty(imageFile))
        {
            return;
        }

        // Only activate if video batch size is enabled (indicates video generation mode)
        if (!g.UserInput.TryGet(SeedVR2VideoBatchSize, out int videoBatchSize))
        {
            return;
        }

        // Only activate if the SeedVR2 group toggle is enabled
        if (!g.UserInput.TryGet(SeedVR2Model, out string modelChoice))
        {
            return;
        }

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 upscaler selected for video, but SeedVR2 nodes are not installed in ComfyUI. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // Ensure we have video frames to upscale
        if (g.FinalImageOut is null)
        {
            Logs.Warning("SeedVR2 Video: No FinalImageOut available to upscale");
            return;
        }

        // Store the original video frames reference for ReplaceNodeConnection
        JArray originalVideoFrames = g.FinalImageOut;

        // Determine model variant and settings from selection
        string modelKey = modelChoice.Before("///");
        int blockSwap;
        bool tiledVAE;
        bool isPresetOrAuto = false;

        if (modelKey == "seedvr2-auto")
        {
            (modelKey, blockSwap, tiledVAE) = DetectVRAMAndSelectModel();
            isPresetOrAuto = true;
        }
        else if (QualityPresets.TryGetValue(modelKey, out var preset))
        {
            modelKey = preset.ModelKey;
            blockSwap = preset.BlockSwap;
            tiledVAE = preset.TiledVAE;
            isPresetOrAuto = true;
            Logs.Info($"SeedVR2 Video: Using preset with model={modelKey}, blockSwap={blockSwap}, tiledVAE={tiledVAE}");
        }
        else
        {
            blockSwap = g.UserInput.Get(SeedVR2BlockSwap, 0);
            tiledVAE = g.UserInput.Get(SeedVR2TiledVAE, false);
        }

        // Allow user to override preset settings
        if (isPresetOrAuto)
        {
            if (g.UserInput.TryGet(SeedVR2BlockSwap, out int userBlockSwap))
            {
                blockSwap = userBlockSwap;
            }
            if (g.UserInput.TryGet(SeedVR2TiledVAE, out bool userTiledVAE))
            {
                tiledVAE = userTiledVAE;
            }
        }

        // Get the actual model filename
        if (!DiTModelMap.TryGetValue(modelKey, out string ditModel))
        {
            ditModel = "seedvr2_ema_3b_fp8_e4m3fn.safetensors";
            Logs.Warning($"SeedVR2 Video: Unknown model key '{modelKey}', falling back to 3B FP8");
        }

        // Calculate target resolution based on upscale factor
        double upscaleFactor = g.UserInput.Get(T2IParamTypes.RefinerUpscale, 1.0);
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.0);
        int baseWidth = (int)Math.Round(g.UserInput.GetImageWidth() * upscaleFactor);
        int baseHeight = (int)Math.Round(g.UserInput.GetImageHeight() * upscaleFactor);
        int targetWidth = (int)Math.Round(baseWidth * seedvrUpscaleBy);
        int targetHeight = (int)Math.Round(baseHeight * seedvrUpscaleBy);
        int resolution = Math.Min(targetWidth, targetHeight);

        // Get video-specific parameters
        int temporalOverlap = g.UserInput.Get(SeedVR2TemporalOverlap, 3);
        bool uniformBatchSize = g.UserInput.Get(SeedVR2UniformBatchSize, true);

        // Get other parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            Logs.Warning($"SeedVR2 Video: Block swap {blockSwap} exceeds max {maxBlocks} for {(is7BModel ? "7B" : "3B")} model, capping");
            blockSwap = maxBlocks;
        }

        Logs.Info($"SeedVR2 Video: Upscaling video frames to resolution {resolution} (model={ditModel}, batch_size={videoBatchSize}, temporal_overlap={temporalOverlap})");

        // Determine offload device
        string offloadDevice = blockSwap > 0 ? "cpu" : "none";

        // Add VRAM cleanup node to unload main generation model before SeedVR2
        JArray imageInputForUpscaler = originalVideoFrames;
        if (g.Features.Contains("kjnodes"))
        {
            string vramCleanupNode = g.CreateNode("VRAM_Debug", new JObject()
            {
                ["empty_cache"] = true,
                ["gc_collect"] = true,
                ["unload_all_models"] = true,
                ["image_pass"] = originalVideoFrames
            });
            imageInputForUpscaler = new JArray() { vramCleanupNode, 1 };
        }

        // Create SeedVR2LoadDiTModel node
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = "cuda:0",
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = offloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = "sdpa"
        };
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // Create SeedVR2LoadVAEModel node
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = "cuda:0",
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = tiledVAE ? "cpu" : "none",
            ["cache_model"] = cacheModel
        };
        if (tiledVAE)
        {
            vaeLoaderInputs["encode_tile_size"] = 1024;
            vaeLoaderInputs["encode_tile_overlap"] = 128;
            vaeLoaderInputs["decode_tile_size"] = 1024;
            vaeLoaderInputs["decode_tile_overlap"] = 128;
        }
        string vaeLoaderNode = g.CreateNode("SeedVR2LoadVAEModel", vaeLoaderInputs);

        // Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // Create SeedVR2VideoUpscaler node
        JObject upscalerInputs = new JObject()
        {
            ["image"] = imageInputForUpscaler,
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["max_resolution"] = 0,  // No limit for video
            ["batch_size"] = videoBatchSize,
            ["uniform_batch_size"] = uniformBatchSize,
            ["temporal_overlap"] = temporalOverlap,
            ["prepend_frames"] = 0,
            ["color_correction"] = colorCorrection,
            ["input_noise_scale"] = 0.0,
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };

        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);
        JArray upscalerOutput = new JArray() { upscalerNode, 0 };

        Logs.Info($"SeedVR2 Video: Created upscaler node '{upscalerNode}'");

        // Find and update the SwarmSaveAnimationWS node that was created at priority 11
        // We can't use ReplaceNodeConnection because it would also replace our own input, creating a cycle
        string originalNodeId = originalVideoFrames[0].ToString();
        int originalOutputIndex = originalVideoFrames[1].Value<int>();
        bool foundSaveNode = false;

        g.RunOnNodesOfClass("SwarmSaveAnimationWS", (nodeId, nodeData) =>
        {
            JArray imagesInput = nodeData["inputs"]?["images"] as JArray;
            if (imagesInput != null &&
                imagesInput[0].ToString() == originalNodeId &&
                imagesInput[1].Value<int>() == originalOutputIndex)
            {
                // Update this save node to use the upscaled output
                nodeData["inputs"]["images"] = upscalerOutput;
                Logs.Info($"SeedVR2 Video: Updated save node '{nodeId}' to use upscaled frames");
                foundSaveNode = true;
            }
        });

        // Also check for video boomerang nodes that might be in the chain
        g.RunOnNodesOfClass("SwarmVideoBoomerang", (nodeId, nodeData) =>
        {
            JArray imagesInput = nodeData["inputs"]?["images"] as JArray;
            if (imagesInput != null &&
                imagesInput[0].ToString() == originalNodeId &&
                imagesInput[1].Value<int>() == originalOutputIndex)
            {
                nodeData["inputs"]["images"] = upscalerOutput;
                Logs.Info($"SeedVR2 Video: Updated boomerang node '{nodeId}' to use upscaled frames");
            }
        });

        if (!foundSaveNode)
        {
            Logs.Warning("SeedVR2 Video: Could not find save node to update - upscaling may not be applied");
        }

        // Update FinalImageOut for any subsequent steps
        g.FinalImageOut = upscalerOutput;

        Logs.Info($"SeedVR2 Video: Video upscaling workflow complete");
    }
}
