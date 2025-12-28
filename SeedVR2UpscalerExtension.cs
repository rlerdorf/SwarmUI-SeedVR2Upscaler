using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SeedVR2Upscaler;

/// <summary>Extension for SeedVR2 video/image upscaling integration.</summary>
public class SeedVR2UpscalerExtension : Extension
{
    /// <summary>Registered parameter for SeedVR2 target resolution.</summary>
    public static T2IRegisteredParam<int> SeedVR2Resolution;

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

    /// <summary>Registered parameter for SeedVR2 max resolution cap.</summary>
    public static T2IRegisteredParam<int> SeedVR2MaxResolution;

    /// <summary>Registered parameter for SeedVR2 latent noise scale.</summary>
    public static T2IRegisteredParam<double> SeedVR2LatentNoiseScale;

    /// <summary>Registered parameter for SeedVR2 model caching.</summary>
    public static T2IRegisteredParam<bool> SeedVR2CacheModel;

    /// <summary>Parameter group for SeedVR2 advanced settings.</summary>
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

        // Add SeedVR2 to upscaler models list
        ComfyUIBackendExtension.UpscalerModels.Add("seedvr2-auto///SeedVR2: Auto (VRAM-based)");
        ComfyUIBackendExtension.UpscalerModels.Add("seedvr2-preset-fast///SeedVR2: Fast (3B Q4)");
        ComfyUIBackendExtension.UpscalerModels.Add("seedvr2-preset-balanced///SeedVR2: Balanced (3B FP8)");
        ComfyUIBackendExtension.UpscalerModels.Add("seedvr2-preset-quality///SeedVR2: Quality (7B FP8)");
        ComfyUIBackendExtension.UpscalerModels.Add("seedvr2-preset-max///SeedVR2: Max Quality (7B Sharp FP16)");

        // Create parameter group for advanced settings
        SeedVR2Group = new("SeedVR2 Upscaler", Toggles: false, Open: false, IsAdvanced: true,
            Description: "Advanced settings for the SeedVR2 AI upscaler.\n" +
            "To USE SeedVR2: Go to Refine/Upscale group, set 'Refiner Upscale' to your desired scale (e.g. 1.25x), then select a SeedVR2 option from 'Refiner Upscale Method'.\n" +
            "The quality presets (Fast, Balanced, Quality, Max) auto-configure optimal settings for your VRAM.\n" +
            "You only need to adjust settings here if you want to override preset defaults (e.g. force different block swap, enable tiled VAE, or change color correction).");

        // Register advanced parameters
        SeedVR2Resolution = T2IParamTypes.Register<int>(new(
            "SeedVR2 Resolution",
            "Target resolution for shortest edge when using SeedVR2 upscaler.\nDefaults to calculated value from upscale factor if not set.",
            "1080", Min: 256, Max: 4096, Step: 8,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
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

        SeedVR2MaxResolution = T2IParamTypes.Register<int>(new(
            "SeedVR2 Max Resolution",
            "Maximum allowed output resolution (shortest edge).\nCaps the upscaled image size to prevent runaway resolution.",
            "4096", Min: 1024, Max: 8192, Step: 256,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 7
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

        // Add workflow generation step
        // Priority 6: After VAEDecode (1) and Segmentation/FaceRefine (5), before SaveImage (10)
        // This ensures face refinement happens at original resolution, then SeedVR2 upscales the result
        WorkflowGenerator.AddStep(GenerateSeedVR2Workflow, 6);

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

    /// <summary>Generates the SeedVR2 workflow nodes when SeedVR2 upscaler is selected.</summary>
    public static void GenerateSeedVR2Workflow(WorkflowGenerator g)
    {
        // Only activate if using SeedVR2 upscale method
        string upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "");
        if (!upscaleMethod.StartsWith("seedvr2-"))
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
        string modelKey = upscaleMethod.Before("///");
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
        int baseWidth = g.UserInput.GetImageWidth();
        int baseHeight = g.UserInput.GetImageHeight();
        int targetWidth = (int)Math.Round(baseWidth * upscaleFactor);
        int targetHeight = (int)Math.Round(baseHeight * upscaleFactor);
        // SeedVR2 uses shortest edge as the resolution target
        int calculatedResolution = Math.Min(targetWidth, targetHeight);

        // Allow user override of resolution, otherwise use calculated
        int resolution = g.UserInput.TryGet(SeedVR2Resolution, out int userResolution) ? userResolution : calculatedResolution;

        // Get other optional parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        bool twoStepMode = g.UserInput.Get(SeedVR2TwoStepMode, false);
        double preDownscale = g.UserInput.Get(SeedVR2PreDownscale, 0.5);
        int maxResolution = g.UserInput.Get(SeedVR2MaxResolution, 4096);
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
        string configInfo = $"model={ditModel}, blockSwap={blockSwap}, tiledVAE={tiledVAE}";
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

        // Create SeedVR2VideoUpscaler node
        JObject upscalerInputs = new JObject()
        {
            ["image"] = imageInputForUpscaler,
            ["dit"] = new JArray() { ditLoaderNode, 0 },
            ["vae"] = new JArray() { vaeLoaderNode, 0 },
            ["seed"] = seed,
            ["resolution"] = resolution,
            ["max_resolution"] = maxResolution,
            ["batch_size"] = 1,  // Use 1 for single images
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

        // Update final image output to point to upscaler output
        g.FinalImageOut = new JArray() { upscalerNode, 0 };
    }
}
