using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Tiff;
using SwarmUI.Utils;
using ISImage = SixLabors.ImageSharp.Image;
using Image = SwarmUI.Utils.Image;

namespace SeedVR2Upscaler;

/// <summary>Extension for SeedVR2 video/image upscaling integration.</summary>
public class SeedVR2UpscalerExtension : Extension
{
    /// <summary>Registered parameter for SeedVR2 model / preset selection.</summary>
    public static T2IRegisteredParam<string> SeedVR2Model;

    /// <summary>Registered parameter for SeedVR2 upscale factor (applied after the normal workflow output).</summary>
    public static T2IRegisteredParam<double> SeedVR2UpscaleBy;

    /// <summary>Registered parameter for SeedVR2 target resolution (shortest edge).</summary>
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

    /// <summary>Registered parameter for SeedVR2 latent noise scale.</summary>
    public static T2IRegisteredParam<double> SeedVR2LatentNoiseScale;

    /// <summary>Registered parameter for SeedVR2 input noise scale.</summary>
    public static T2IRegisteredParam<double> SeedVR2InputNoiseScale;

    /// <summary>Registered parameter for SeedVR2 model caching.</summary>
    public static T2IRegisteredParam<bool> SeedVR2CacheModel;

    /// <summary>Registered parameter for SeedVR2 VAE offload device (required when model caching is enabled).</summary>
    public static T2IRegisteredParam<string> SeedVR2VAEOffloadDevice;

    /// <summary>Registered parameter for SeedVR2 DiT attention backend selection.</summary>
    public static T2IRegisteredParam<string> SeedVR2AttentionMode;

    /// <summary>Registered parameter for SeedVR2 video batch size.</summary>
    public static T2IRegisteredParam<int> SeedVR2VideoBatchSize;

    /// <summary>Registered parameter for SeedVR2 temporal overlap for video.</summary>
    public static T2IRegisteredParam<int> SeedVR2TemporalOverlap;

    /// <summary>Registered parameter for SeedVR2 uniform batch size mode.</summary>
    public static T2IRegisteredParam<bool> SeedVR2UniformBatchSize;

    /// <summary>Registered parameter for SeedVR2 image tile size (applies to both width and height).</summary>
    public static T2IRegisteredParam<int> SeedVR2ImageTileSize;

    /// <summary>Registered parameter for SeedVR2 image tile mask blur.</summary>
    public static T2IRegisteredParam<int> SeedVR2ImageMaskBlur;

    /// <summary>Registered parameter for SeedVR2 image tile overlap.</summary>
    public static T2IRegisteredParam<int> SeedVR2ImageTileOverlap;

    /// <summary>Registered parameter for SeedVR2 image tile upscale resolution.</summary>
    public static T2IRegisteredParam<int> SeedVR2ImageTileUpscaleResolution;

    /// <summary>Registered parameter for SeedVR2 image tiling strategy.</summary>
    public static T2IRegisteredParam<string> SeedVR2ImageTilingStrategy;

    /// <summary>Registered parameter for SeedVR2 image anti-aliasing strength.</summary>
    public static T2IRegisteredParam<double> SeedVR2ImageAntiAliasingStrength;

    /// <summary>Registered parameter for SeedVR2 image blending method.</summary>
    public static T2IRegisteredParam<string> SeedVR2ImageBlendingMethod;

    /// <summary>Registered parameter for enabling torch.compile() on the DiT model.</summary>
    public static T2IRegisteredParam<bool> SeedVR2TorchCompile;

    /// <summary>Internal marker parameter indicating the image-upscaler subgroup is enabled.</summary>
    public static T2IRegisteredParam<bool> SeedVR2UseImageUpscalerNode;

    /// <summary>Registered parameter for SeedVR2 video file path (for upscaling existing videos).</summary>
    public static T2IRegisteredParam<string> SeedVR2VideoFile;

    /// <summary>Registered parameter for SeedVR2 image file path (for upscaling existing images).</summary>
    public static T2IRegisteredParam<string> SeedVR2ImageFile;

    /// <summary>Registered parameter for which pipeline stage SeedVR2 upscaling runs at in multi-stage (T2I→I2V) workflows.</summary>
    public static T2IRegisteredParam<string> SeedVR2UpscaleStage;

    /// <summary>Parameter group for SeedVR2 settings.</summary>
    public static T2IParamGroup SeedVR2Group;

    /// <summary>Subgroup for optional SeedVR2 image-upscaler node settings.</summary>
    public static T2IParamGroup SeedVR2ImageUpscalerGroup;

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

    /// <summary>Supported model file extensions for discovery.</summary>
    private static readonly string[] SupportedModelExtensions = [".safetensors", ".gguf"];

    /// <summary>Temporary storage for source EXIF profiles, keyed by request ID. Avoids serialization into image metadata.</summary>
    private static readonly ConcurrentDictionary<long, byte[]> PendingSourceExif = new();

    /// <summary>Tracks workflow generators where the pre-video image upscale (priority 6) actually ran, so priority 15 can skip.</summary>
    private static readonly ConditionalWeakTable<WorkflowGenerator, object> PreVideoUpscaleRan = new();

    /// <summary>Gets the SwarmUI model directory path for SeedVR2 models.</summary>
    public static string GetSeedVR2ModelDirectory()
    {
        return Path.Combine(Program.ServerSettings.Paths.ActualModelRoot, "seedvr2");
    }

    /// <summary>Scans the seedvr2 model directory and adds any new model files to DiTModelMap.</summary>
    public static void ScanForDiscoveredModels()
    {
        string modelDir = GetSeedVR2ModelDirectory();

        if (!Directory.Exists(modelDir))
        {
            Directory.CreateDirectory(modelDir);
            Logs.Info($"SeedVR2: Created model directory at {modelDir}");
            return;
        }

        HashSet<string> knownFiles = new(DiTModelMap.Values, StringComparer.OrdinalIgnoreCase);
        var discoveredCount = 0;

        foreach (string file in Directory.GetFiles(modelDir))
        {
            string fileName = Path.GetFileName(file);

            if (!SupportedModelExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) || knownFiles.Contains(fileName))
            {
                continue;
            }

            // Use filename as both key and value for discovered models
            DiTModelMap[fileName] = fileName;
            discoveredCount++;
        }

        if (discoveredCount > 0)
        {
            Logs.Info($"SeedVR2: Discovered {discoveredCount} additional model(s) in {modelDir}");
        }
    }

    /// <summary>Gets the list of model dropdown values, including discovered models.</summary>
    public static List<string> GetModelDropdownValues()
    {
        List<string> values =
        [
            // Presets (auto-configure settings)
            "seedvr2-auto///Auto (VRAM-based)",
            "seedvr2-preset-fast///Preset: Fast (3B Q4)",
            "seedvr2-preset-balanced///Preset: Balanced (3B FP8)",
            "seedvr2-preset-quality///Preset: Quality (7B FP8)",
            "seedvr2-preset-max///Preset: Max Quality (7B Sharp FP16)",
            // 3B Models - Faster, lower VRAM
            "seedvr2-3b-fp16///3B FP16 (Best quality, ~16GB)",
            "seedvr2-3b-fp8///3B FP8 (Good quality, ~12GB)",
            "seedvr2-3b-q8///3B GGUF Q8 (Good quality, ~10GB)",
            "seedvr2-3b-q4///3B GGUF Q4 (Acceptable quality, ~8GB)",
            // 7B Models - Higher quality, higher VRAM
            "seedvr2-7b-fp16///7B FP16 (Best quality, ~28GB)",
            "seedvr2-7b-fp8///7B FP8 Mixed (Good quality, ~20GB)",
            "seedvr2-7b-q4///7B GGUF Q4 (Acceptable quality, ~12GB)",
            // 7B Sharp Models - Enhanced detail
            "seedvr2-7b-sharp-fp16///7B Sharp FP16 (Best detail, ~28GB)",
            "seedvr2-7b-sharp-fp8///7B Sharp FP8 Mixed (Good detail, ~20GB)",
            "seedvr2-7b-sharp-q4///7B Sharp GGUF Q4 (Acceptable detail, ~12GB)",
        ];

        // Add discovered models (where key == value, meaning user-added files)
        HashSet<string> staticKeys = new(values.Select(v => v.Before("///")));
        foreach (var kvp in DiTModelMap.Where(kvp => kvp.Key == kvp.Value && !staticKeys.Contains(kvp.Key)))
        {
            string displayName = Path.GetFileNameWithoutExtension(kvp.Key);
            values.Add($"{kvp.Key}///{displayName} (User Model)");
        }

        return values;
    }

    /// <summary>Resolves a model key to the actual model filename.</summary>
    public static string ResolveModelFilename(string modelKey)
    {
        return DiTModelMap.TryGetValue(modelKey, out string ditModel) ? ditModel : modelKey;
    }

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
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2ImageUpscaler"] = "seedvr2_image_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2LoadDiTModel"] = "seedvr2_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2LoadVAEModel"] = "seedvr2_upscaler";
        ComfyUIBackendExtension.NodeToFeatureMap["SeedVR2TorchCompileSettings"] = "seedvr2_upscaler";

        // Register KJNodes feature for VRAM cleanup support
        ComfyUIBackendExtension.NodeToFeatureMap["VRAM_Debug"] = "kjnodes";

        // Register "seedvr2" folder path so ComfyUI can find models in SwarmUI's model directory
        if (!ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Contains("seedvr2"))
        {
            ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Add("seedvr2");
        }

        ScanForDiscoveredModels();

        // Register installable feature for the SeedVR2 ComfyUI node
        InstallableFeatures.RegisterInstallableFeature(new("SeedVR2 Video Upscaler", "seedvr2_upscaler", "https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler", "numz", "This will install the SeedVR2 Video Upscaler ComfyUI node by numz.\nDo you wish to install?"));
        InstallableFeatures.RegisterInstallableFeature(new("SeedVR2 Image Upscaler", "seedvr2_image_upscaler", "https://github.com/jtreminio/ComfyUI-SeedVR2_ImageUpscaler", "jtreminio", "This will install the SeedVR2 Image Upscaler ComfyUI node by jtreminio.\nDo you wish to install?"));

        // Add the JS file for the install button
        ScriptFiles.Add("assets/seedvr2_install.js");

        SeedVR2Group = new("SeedVR2 Upscaler", Toggles: true, Open: false, IsAdvanced: false,
            Description: "Implements <a class=\"translate\" href=\"https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler\">https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler</a>\n" +
            "IMAGE UPSCALING: Enable this group and set 'SeedVR2 Upscale By' to your desired scale.\n" +
            "VIDEO GENERATION: Video mode uses the video-specific SeedVR2 options (batch size, overlap, etc.) and routes to the video upscaler node.\n" +
            "EXISTING MEDIA: Select an image/video in history and click 'SeedVR2 Upscale'.");

        SeedVR2ImageUpscalerGroup = new("SeedVR2 Image Upscaler Node", Toggles: true, Open: false, IsAdvanced: true, Parent: SeedVR2Group,
            Description: "Image-specific SeedVR2 upscaling with tiling.\nThis is SLOWER but allows you to generate images larger than you normally can.");

        SeedVR2UseImageUpscalerNode = T2IParamTypes.Register<bool>(new(
            "SeedVR2 Use Image Upscaler Node",
            "Internal marker for the SeedVR2 Image Upscaler subgroup toggle state.",
            "true",
            FeatureFlag: "seedvr2_image_upscaler",
            Group: SeedVR2ImageUpscalerGroup,
            VisibleNormally: false,
            HideFromMetadata: true,
            DoNotPreview: true,
            OrderPriority: 0
        ));

        SeedVR2Model = T2IParamTypes.Register<string>(new(
            "SeedVR2 Preset Model",
            "Which SeedVR2 model to use.\n" +
            "Presets auto-configure Block Swap and Tiled VAE for typical VRAM constraints.\n" +
            "Auto detects your GPU VRAM and selects an appropriate configuration.\n" +
            "Individual models let you choose exactly which model file to use.\n" +
            "User models can be added to Models/seedvr2/ and will appear automatically.",
            "seedvr2-auto",
            GetValues: _ => GetModelDropdownValues(),
            Group: SeedVR2Group,
            OrderPriority: 0
        ));

        SeedVR2UpscaleBy = T2IParamTypes.Register<double>(new(
            "SeedVR2 Upscale By",
            "How much to upscale the final decoded image by before/while running SeedVR2.\n" +
            "1.0 keeps the same size (detail enhancement pass).\n" +
            "The slider UI is capped at 4.0, but you can type a higher value if desired.\n" +
            "Ignored if 'SeedVR2 Resolution' is set.",
            "1",
            IgnoreIf: "1",
            Min: 1, Max: 16, ViewMax: 4, Step: 0.25,
            ViewType: ParamViewType.SLIDER,
            Group: SeedVR2Group,
            OrderPriority: 1
        ));

        SeedVR2Resolution = T2IParamTypes.Register<int>(new(
            "SeedVR2 Resolution",
            "Target resolution for the shortest edge of the output image.\n" +
            "Set to 0 to use 'Upscale By' factor instead.\n" +
            "Example: 1080 will upscale so the shortest edge is 1080 pixels.",
            "0",
            IgnoreIf: "0",
            Min: 0, Max: 8192, ViewMax: 4096, Step: 8,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: SeedVR2Group,
            OrderPriority: 1.5
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
            "lab",
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
            Toggleable: true,
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

        SeedVR2InputNoiseScale = T2IParamTypes.Register<double>(new(
            "SeedVR2 Input Noise",
            "Amount of noise to add to the input image before encoding.\n" +
            "Higher values = more creative freedom, lower faithfulness to original.\n" +
            "Can help reduce certain artifacts. 0 = no noise (default).",
            "0.0", Min: 0.0, Max: 1.0, Step: 0.05,
            Toggleable: true, IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 9.1
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

        SeedVR2AttentionMode = T2IParamTypes.Register<string>(new(
            "SeedVR2 Attention Mode",
            "Attention computation backend for the SeedVR2 DiT model.\n" +
            "sdpa is recommended - stable and works everywhere.\n" +
            "Flash/Sage options require extra Python packages and compatible GPUs.",
            "sdpa",
            IgnoreIf: "sdpa",
            GetValues: _ => [
                "sdpa///sdpa (Default / Most compatible)",
                "flash_attn_2///flash_attn_2 (Flash Attention 2)",
                "flash_attn_3///flash_attn_3 (Flash Attention 3)",
                "sageattn_2///sageattn_2 (SageAttention 2)",
                "sageattn_3///sageattn_3 (SageAttention 3)"
            ],
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 10.05
        ));

        SeedVR2TorchCompile = T2IParamTypes.Register<bool>(new(
            "SeedVR2 Torch Compile",
            "Enable torch.compile() for the DiT model.\n" +
            "Speeds up repeat runs after an initial compilation delay.\n" +
            "Requires Cache Model to be enabled so the compiled model persists between runs.\n" +
            "Requires a torch.compile-capable backend such as CUDA/ROCm and PyTorch 2.0+.",
            "false",
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            DependNonDefault: SeedVR2CacheModel.Type.ID,
            OrderPriority: 10.15
        ));

        SeedVR2VAEOffloadDevice = T2IParamTypes.Register<string>(new(
            "SeedVR2 VAE Offload Device",
            "Controls the 'offload_device' used by BOTH SeedVR2 model loader nodes (DiT + VAE).\n" +
            "This affects where models are stored when not actively processing.\n" +
            "Required for caching and for DiT BlockSwap: must not be 'none'.\n" +
            "If you leave this setting disabled, SwarmUI will pick a reasonable device automatically when required.\n" +
            "Device list is detected locally (no ComfyUI dependency). Common values include: cpu, cuda:0, cuda:1, mps.",
            "none",
            IgnoreIf: "none",
            GetValues: SeedVR2DeviceUtils.GetSeedVR2OffloadDeviceValues,
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            DependNonDefault: SeedVR2CacheModel.Type.ID,
            Toggleable: true,
            OrderPriority: 10.1
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

        SeedVR2UpscaleStage = T2IParamTypes.Register<string>(new(
            "SeedVR2 Upscale Stage",
            "Controls when SeedVR2 upscaling runs in multi-stage pipelines (e.g. Text-to-Image → Image-to-Video).\n" +
            "'After Video' (default): upscale the final video frames after generation.\n" +
            "'Before Video': upscale the intermediate image before it is fed to the video model.\n" +
            "Only relevant when a Video Model is selected. For image-only or video-only workflows this setting has no effect.",
            "after_video",
            GetValues: _ => [
                "after_video///After Video (Default)",
                "before_video///Before Video"
            ],
            IsAdvanced: true,
            FeatureFlag: "seedvr2_upscaler",
            Group: SeedVR2Group,
            OrderPriority: 13.5
        ));

        // Image-specific parameters (used by SeedVR2 image upscaler nodes)
        (SeedVR2ImageTileSize,
            SeedVR2ImageMaskBlur,
            SeedVR2ImageTileOverlap,
            SeedVR2ImageTileUpscaleResolution,
            SeedVR2ImageTilingStrategy,
            SeedVR2ImageAntiAliasingStrength,
            SeedVR2ImageBlendingMethod) = SeedVR2ImageUpscaler.RegisterParams(SeedVR2ImageUpscalerGroup);

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

        // Register event handlers for EXIF metadata preservation
        T2IEngine.PostGenerateEvent += HandleSeedVR2PostGeneration;
        T2IEngine.PostBatchEvent += HandleSeedVR2PostBatch;

        Logs.Info("SeedVR2 Upscaler Extension loaded successfully.");
    }

    /// <inheritdoc/>
    public override void OnShutdown()
    {
        T2IEngine.PostGenerateEvent -= HandleSeedVR2PostGeneration;
        T2IEngine.PostBatchEvent -= HandleSeedVR2PostBatch;
    }

    /// <summary>Detects available GPU VRAM and returns the best model configuration.</summary>
	/// <returns>Tuple of (modelKey, blockSwap, tiledVAE) based on detected VRAM.</returns>
	public static (string ModelKey, int BlockSwap, bool TiledVAE) DetectVRAMAndSelectModel()
	{
		try
		{
			// Try NVIDIA first
			NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
			if (gpus is not null && gpus.Length > 0)
			{
				// Use the GPU with most VRAM
				NvidiaUtil.NvidiaInfo bestGpu = gpus.OrderByDescending(g => g.TotalMemory.InBytes).First();
				double vramGiB = bestGpu.TotalMemory.GiB;

				Logs.Info($"SeedVR2 Auto: Detected NVIDIA GPU '{bestGpu.GPUName}' with {vramGiB:F1} GiB VRAM");
				return SelectModelByVram(vramGiB);
			}

			// If no NVIDIA GPUs found, try AMD/ROCm
			double? amdVram = SeedVR2DeviceUtils.DetectAmdVramGiB();
			if (amdVram.HasValue)
			{
				double vramGiB = amdVram.Value;
				Logs.Info($"SeedVR2 Auto: Detected AMD GPU with {vramGiB:F1} GiB VRAM");
				return SelectModelByVram(vramGiB);
			}

			// If both failed, fall back to default
			Logs.Warning("SeedVR2 Auto: Could not detect GPU VRAM (no NVIDIA or AMD GPUs found), defaulting to 3B FP8");
			return ("seedvr2-3b-fp8", 16, true);
		}
		catch (Exception ex)
		{
			Logs.Warning($"SeedVR2 Auto: Error detecting VRAM: {ex.Message}, defaulting to 3B FP8");
			return ("seedvr2-3b-fp8", 16, true);
		}
	}

	/// <summary>Selects model configuration based on VRAM amount. Shared logic for NVIDIA and AMD.</summary>
	private static (string ModelKey, int BlockSwap, bool TiledVAE) SelectModelByVram(double vramGiB)
	{
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

    /// <summary>Returns true if the current generation request is producing video output.</summary>
    private static bool IsSeedVR2VideoGenerationRequest(WorkflowGenerator g)
    {
        if (g.IsVideoModel())
        {
            return true;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out _))
        {
            return true;
        }
        if (g.UserInput.Get(T2IParamTypes.Prompt, "").Contains("<extend:"))
        {
            return true;
        }
        return false;
    }

    /// <summary>Returns true when the optional image-upscaler subgroup is enabled and the node is available.</summary>
    private static bool ShouldUseSeedVR2ImageUpscalerNode(WorkflowGenerator g)
    {
        if (!(g.UserInput.TryGet(SeedVR2UseImageUpscalerNode, out bool useImageUpscalerNode) && useImageUpscalerNode))
        {
            return false;
        }

        if (SeedVR2ImageUpscaler.IsAvailable(g))
        {
            return true;
        }

        Logs.Warning("SeedVR2: Image upscaler subgroup enabled, but SeedVR2ImageUpscaler node is unavailable. Falling back to SeedVR2VideoUpscaler.");
        return false;
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

        // For multi-stage pipelines (T2I→I2V), check whether the user wants upscaling before or after video.
        // "before_video" means we upscale the image here at priority 6 before the I2V step at priority 11.
        // "after_video" (default) defers to GenerateSeedVR2VideoGenerationWorkflow at priority 15.
        if (IsSeedVR2VideoGenerationRequest(g))
        {
            string stage = g.UserInput.Get(SeedVR2UpscaleStage, "after_video").Before("///");
            if (stage != "before_video")
            {
                return;
            }
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
        if (g.CurrentMedia is null)
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
        string ditModel = ResolveModelFilename(modelKey);

        // Calculate target resolution based on upscale factor
        double upscaleFactor = g.UserInput.Get(T2IParamTypes.RefinerUpscale, 1.0);
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.0);
        int baseWidth = (int)Math.Round(g.UserInput.GetImageWidth() * upscaleFactor);
        int baseHeight = (int)Math.Round(g.UserInput.GetImageHeight() * upscaleFactor);
        // Check if user specified a direct resolution target
        int resolution;
        bool directResolutionMode = g.UserInput.TryGet(SeedVR2Resolution, out int userResolution) && userResolution > 0;
        if (directResolutionMode)
        {
            resolution = userResolution;
        }
        else
        {
            int targetWidth = (int)Math.Round(baseWidth * seedvrUpscaleBy);
            int targetHeight = (int)Math.Round(baseHeight * seedvrUpscaleBy);
            // SeedVR2 uses the shortest edge as the resolution target
            resolution = Math.Min(targetWidth, targetHeight);
        }

        // Get other optional parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        bool twoStepMode = g.UserInput.Get(SeedVR2TwoStepMode, false);
        double preDownscale = g.UserInput.Get(SeedVR2PreDownscale, 0.5);
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);
        string attentionMode = g.UserInput.Get(SeedVR2AttentionMode, "sdpa").Before("///");
        bool ditNeedsOffload = cacheModel || blockSwap > 0;
        string ditOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: ditNeedsOffload, requireNotNoneReason: "DiT caching / Block Swap");
        string vaeOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: cacheModel, requireNotNoneReason: "VAE caching");

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

        // Create SeedVR2LoadDiTModel node
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,  // Also swap I/O components if using block swap
            ["offload_device"] = ditOffloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = attentionMode
        };
        if (cacheModel && g.UserInput.Get(SeedVR2TorchCompile, false))
        {
            string compileNode = g.CreateNode("SeedVR2TorchCompileSettings", new JObject()
            {
                ["backend"] = "inductor",
                ["mode"] = "default",
                ["fullgraph"] = false,
                ["dynamic"] = false,
                ["dynamo_cache_size_limit"] = 64,
                ["dynamo_recompile_limit"] = 128
            });
            ditLoaderInputs["torch_compile_args"] = new JArray() { compileNode, 0 };
        }

        // Add VRAM cleanup node to unload main generation model before SeedVR2
        // This frees up VRAM from the Flux model so SeedVR2 has enough room
        if (g.Features.Contains("kjnodes"))
        {
            string vramCleanupNode = g.CreateNode("VRAM_Debug", new JObject()
            {
                ["empty_cache"] = true,
                ["gc_collect"] = true,
                ["unload_all_models"] = true,
                ["image_pass"] = g.CurrentMedia.Path  // Pass through the image to maintain workflow connection
            });
            // Update CurrentMedia to use the passthrough from VRAM_Debug
            g.CurrentMedia = new WGNodeData(new JArray() { vramCleanupNode, 1 }, g, WGNodeData.DT_IMAGE, g.CurrentCompat());  // output index 1 is image_pass
        }

        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // Create SeedVR2LoadVAEModel node with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = vaeOffloadDevice,
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
        JArray imageInputForUpscaler = g.CurrentMedia.Path;
        if (twoStepMode)
        {
            string downscaleNode = g.CreateNode("ImageScaleBy", new JObject()
            {
                ["image"] = g.CurrentMedia.Path,
                ["upscale_method"] = "lanczos",
                ["scale_by"] = preDownscale
            });
            imageInputForUpscaler = new JArray() { downscaleNode, 0 };
        }

        string upscalerNode;
        if (ShouldUseSeedVR2ImageUpscalerNode(g))
        {
            upscalerNode = SeedVR2ImageUpscaler.CreateNode(g, imageInputForUpscaler, ditLoaderNode, vaeLoaderNode, seed, resolution, colorCorrection, vaeOffloadDevice);
        }
        else
        {
            JObject upscalerInputs = new JObject()
            {
                ["image"] = imageInputForUpscaler,
                ["dit"] = new JArray() { ditLoaderNode, 0 },
                ["vae"] = new JArray() { vaeLoaderNode, 0 },
                ["seed"] = seed,
                ["resolution"] = resolution,
                ["max_resolution"] = 0,
                ["batch_size"] = 1,
                ["uniform_batch_size"] = false,
                ["temporal_overlap"] = 0,
                ["prepend_frames"] = 0,
                ["color_correction"] = colorCorrection,
                ["input_noise_scale"] = g.UserInput.Get(SeedVR2InputNoiseScale, 0.0),
                ["latent_noise_scale"] = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0),
                ["offload_device"] = vaeOffloadDevice,
                ["enable_debug"] = false
            };
            upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);
        }

        // Update final image output to point to upscaler output
        g.CurrentMedia = new WGNodeData(new JArray() { upscalerNode, 0 }, g, WGNodeData.DT_IMAGE, g.CurrentCompat());

        // Mark that the pre-video upscale ran so priority 15 can skip
        if (IsSeedVR2VideoGenerationRequest(g))
        {
            PreVideoUpscaleRan.Add(g, g);
        }
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

        // Check if this is a data URL (drag & dropped image) or a file path
        bool isDataUrl = imageFile.StartsWith("data:");
        ImageFile sourceImage = null;

        if (isDataUrl)
        {
            Logs.Info($"SeedVR2 Image File Mode: Processing data URL image");
        }
        else
        {
            Logs.Info($"SeedVR2 Image File Mode: Processing image '{imageFile}'");
        }

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 Image File upscaling requires SeedVR2 nodes. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // The image is always embedded as base64 (see below) rather than referencing a host file path,
        // so this backend feature (and unrestricted custom nodes) is required.
        if (!g.Features.Contains("comfy_loadimage_b64") || WorkflowGenerator.RestrictCustomNodes)
        {
            throw new SwarmUserErrorException("SeedVR2 Image File upscaling requires SwarmUI's ComfyUI backend nodes (comfy_loadimage_b64) and cannot run with restricted custom nodes.");
        }

        // Get image dimensions to calculate upscaled resolution
        int origWidth = 0, origHeight = 0;

        if (isDataUrl)
        {
            // Parse the data URL into an ImageFile
            try
            {
                sourceImage = ImageFile.FromDataString(imageFile);
                (origWidth, origHeight) = sourceImage.GetResolution();
                Logs.Info($"SeedVR2 Image File: Data URL source dimensions {origWidth}x{origHeight}");

                // Preserve original image metadata from data URL
                ApplySourceImageMetadata(sourceImage, g.UserInput);
            }
            catch (Exception ex)
            {
                throw new SwarmUserErrorException($"SeedVR2 Image File: Could not parse data URL: {ex.Message}");
            }
        }
        else
        {
            // Expand path (support ~ for home directory)
            if (imageFile.StartsWith("~/"))
            {
                imageFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + imageFile[1..];
            }

            // Resolve Output/ or View/ paths to actual file system paths using user's output directory
            User user = g.UserInput.SourceSession?.User;
            if (user is not null)
            {
                string relativePath = null;
                if (imageFile.StartsWith("Output/"))
                {
                    relativePath = imageFile["Output/".Length..];
                }
                else if (imageFile.StartsWith("View/"))
                {
                    // View/{user_id}/path format - strip View/{user_id}/ prefix
                    string afterView = imageFile["View/".Length..];
                    int slashIndex = afterView.IndexOf('/');
                    if (slashIndex > 0)
                    {
                        relativePath = afterView[(slashIndex + 1)..];
                    }
                }

                if (relativePath is not null)
                {
                    string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
                    imageFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
                }
            }

            // Validate the file exists
            if (!System.IO.File.Exists(imageFile))
            {
                throw new SwarmUserErrorException($"SeedVR2 Image File not found: {imageFile}");
            }

            // Read the file into memory so it can be embedded directly in the workflow (via base64),
            // rather than handing ComfyUI a host filesystem path - LoadImage is sandboxed to its own
            // input/ directory and rejects paths outside of it (also required for remote backends).
            try
            {
                byte[] rawData = System.IO.File.ReadAllBytes(imageFile);
                sourceImage = new Image(rawData, MediaType.GetByExtension(imageFile.AfterLast('.').ToLowerFast()));
            }
            catch (Exception ex)
            {
                throw new SwarmUserErrorException($"SeedVR2 Image File: Could not read image: {ex.Message}");
            }

            // Get image dimensions from the loaded data
            try
            {
                (origWidth, origHeight) = sourceImage.GetResolution();
                Logs.Info($"SeedVR2 Image File: Source dimensions {origWidth}x{origHeight}");
            }
            catch (Exception ex)
            {
                Logs.Warning($"SeedVR2 Image File: Could not read image dimensions: {ex.Message}");
            }

            // Preserve original image metadata - load source params and apply to input
            // This ensures the output has the original generation params, not current UI values
            ApplySourceImageMetadata(imageFile, g.UserInput);
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
        string ditModel = ResolveModelFilename(modelKey);

        // Calculate target resolution based on upscale factor or direct resolution setting
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.5);
        int resolution;
        // Check if user specified a direct resolution target
        bool directResolutionMode = g.UserInput.TryGet(SeedVR2Resolution, out int userResolution) && userResolution > 0;
        if (directResolutionMode)
        {
            resolution = userResolution;
        }
        else if (origWidth > 0 && origHeight > 0)
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
        string attentionMode = g.UserInput.Get(SeedVR2AttentionMode, "sdpa").Before("///");
        bool ditNeedsOffload = cacheModel || blockSwap > 0;
        string ditOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: ditNeedsOffload, requireNotNoneReason: "DiT caching / Block Swap");
        string vaeOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: cacheModel, requireNotNoneReason: "VAE caching");

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            blockSwap = maxBlocks;
        }

        Logs.Info($"SeedVR2 Image File: model={ditModel}, blockSwap={blockSwap}, upscaleBy={seedvrUpscaleBy}, resolution={resolution}");

        // === Create workflow nodes ===

        // 1. Load the image - both data URLs and file paths are embedded as base64 via SwarmUI's
        //    LoadImage helper (SwarmLoadImageB64), so ComfyUI never sees a host filesystem path.
        if (sourceImage is null)
        {
            throw new SwarmUserErrorException("SeedVR2 Image File: could not load source image.");
        }
        string loadImageNode = g.LoadImage(sourceImage, "${seedvr2_source_image}", false).Path[0].ToString();

        // 2. Add VRAM cleanup node to unload any existing models before SeedVR2
        // This frees up VRAM so SeedVR2 has enough room to load its models
        JArray loadedImageOutput = new JArray() { loadImageNode, 0 };
        if (g.Features.Contains("kjnodes"))
        {
            string vramCleanupNode = g.CreateNode("VRAM_Debug", new JObject()
            {
                ["empty_cache"] = true,
                ["gc_collect"] = true,
                ["unload_all_models"] = true,
                ["image_pass"] = loadedImageOutput
            });
            loadedImageOutput = new JArray() { vramCleanupNode, 1 };  // output index 1 is image_pass
        }

        // 3. SeedVR2LoadDiTModel - load the upscaler model
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = ditOffloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = attentionMode
        };
        if (cacheModel && g.UserInput.Get(SeedVR2TorchCompile, false))
        {
            string compileNode = g.CreateNode("SeedVR2TorchCompileSettings", new JObject()
            {
                ["backend"] = "inductor",
                ["mode"] = "default",
                ["fullgraph"] = false,
                ["dynamic"] = false,
                ["dynamo_cache_size_limit"] = 64,
                ["dynamo_recompile_limit"] = 128
            });
            ditLoaderInputs["torch_compile_args"] = new JArray() { compileNode, 0 };
        }
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // 4. SeedVR2LoadVAEModel - load VAE with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = vaeOffloadDevice,
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

        // 5. Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // 6. Optional 2-Step Mode: Downscale the image first
        JArray imageInputForUpscaler = loadedImageOutput;
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

        // 6. Upscale the image (optional image node when subgroup is enabled, otherwise SeedVR2VideoUpscaler fallback)
        string upscalerNode;
        if (ShouldUseSeedVR2ImageUpscalerNode(g))
        {
            upscalerNode = SeedVR2ImageUpscaler.CreateNode(g, imageInputForUpscaler, ditLoaderNode, vaeLoaderNode, seed, resolution, colorCorrection, vaeOffloadDevice);
        }
        else
        {
            JObject upscalerInputs = new JObject()
            {
                ["image"] = imageInputForUpscaler,
                ["dit"] = new JArray() { ditLoaderNode, 0 },
                ["vae"] = new JArray() { vaeLoaderNode, 0 },
                ["seed"] = seed,
                ["resolution"] = resolution,
                ["max_resolution"] = 0,
                ["batch_size"] = 1,  // Single image
                ["uniform_batch_size"] = false,
                ["temporal_overlap"] = 0,
                ["prepend_frames"] = 0,
                ["color_correction"] = colorCorrection,
                ["input_noise_scale"] = g.UserInput.Get(SeedVR2InputNoiseScale, 0.0),
                ["latent_noise_scale"] = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0),
                ["offload_device"] = vaeOffloadDevice,
                ["enable_debug"] = false
            };
            upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);
        }

        // 7. Set CurrentMedia
        g.CurrentMedia = new WGNodeData(new JArray() { upscalerNode, 0 }, g, WGNodeData.DT_IMAGE, g.CurrentCompat());

        // 8. Extract source EXIF before replacing data URL (for EXIF preservation)
        // Store in static dictionary (not ExtraMeta) to avoid serialization into image metadata
        long requestId = g.UserInput.UserRequestId;
        if (isDataUrl && sourceImage is not null)
        {
            try
            {
                ExifProfile sourceExif = sourceImage.ToIS.Metadata.ExifProfile;
                if (sourceExif is not null && sourceExif.Values.Any())
                {
                    PendingSourceExif[requestId] = sourceExif.ToByteArray();
                    Logs.Info($"SeedVR2 EXIF: Captured {sourceExif.Values.Count()} tags from data URL (request {requestId})");
                }
                else
                {
                    Logs.Info($"SeedVR2 EXIF: No EXIF data found in data URL source image");
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"SeedVR2: Could not extract EXIF from data URL: {ex.Message}");
            }
            // Clear data URL from metadata (data URLs are huge and shouldn't be stored)
            g.UserInput.Set(SeedVR2ImageFile, "[data URL image]");
        }
        else if (!isDataUrl)
        {
            // Extract EXIF from file path
            try
            {
                using ISImage sourceImg = ISImage.Load(imageFile);
                ExifProfile sourceExif = sourceImg.Metadata.ExifProfile;
                if (sourceExif is not null && sourceExif.Values.Any())
                {
                    PendingSourceExif[requestId] = sourceExif.ToByteArray();
                    Logs.Info($"SeedVR2 EXIF: Captured {sourceExif.Values.Count()} tags from file (request {requestId})");
                }
                else
                {
                    Logs.Info($"SeedVR2 EXIF: No EXIF data found in source file {imageFile}");
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"SeedVR2: Could not extract EXIF from file: {ex.Message}");
            }
        }

        // 9. Create save node using proper SwarmUI method for metadata handling (fixes issue #12)
        g.CurrentMedia.SaveOutput(g.CurrentVae, null);

        // Mark workflow as complete - skip all other generation steps
        g.SkipFurtherSteps = true;

        string sourceDesc = isDataUrl ? "data URL image" : $"'{imageFile}'";
        Logs.Info($"SeedVR2 Image File: Complete workflow created - loading {sourceDesc}, upscaling {seedvrUpscaleBy}x to resolution {resolution}");
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

        // The video is always embedded as base64 (see below) rather than referencing a host file path,
        // so this backend feature (and unrestricted custom nodes) is required.
        if (!g.Features.Contains("comfy_loadimage_b64") || WorkflowGenerator.RestrictCustomNodes)
        {
            throw new SwarmUserErrorException("SeedVR2 Video File upscaling requires SwarmUI's ComfyUI backend nodes (comfy_loadimage_b64) and cannot run with restricted custom nodes.");
        }

        // Expand path (support ~ for home directory)
        if (videoFile.StartsWith("~/"))
        {
            videoFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + videoFile[1..];
        }

        // Resolve Output/ or View/ paths to actual file system paths using user's output directory
        User user = g.UserInput.SourceSession?.User;
        if (user is not null)
        {
            string relativePath = null;
            if (videoFile.StartsWith("Output/"))
            {
                relativePath = videoFile["Output/".Length..];
            }
            else if (videoFile.StartsWith("View/"))
            {
                // View/{user_id}/path format - strip View/{user_id}/ prefix
                string afterView = videoFile["View/".Length..];
                int slashIndex = afterView.IndexOf('/');
                if (slashIndex > 0)
                {
                    relativePath = afterView[(slashIndex + 1)..];
                }
            }

            if (relativePath is not null)
            {
                string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
                videoFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
            }
        }

        // Validate the file exists
        if (!System.IO.File.Exists(videoFile))
        {
            throw new SwarmUserErrorException($"SeedVR2 Video File not found: {videoFile}");
        }

        // Read the file into memory so it can be embedded directly in the workflow (via base64),
        // rather than handing ComfyUI a host filesystem path - LoadVideo is sandboxed to its own
        // input/ directory and rejects paths outside of it (also required for remote backends).
        VideoFile sourceVideo;
        try
        {
            byte[] rawVideoData = System.IO.File.ReadAllBytes(videoFile);
            sourceVideo = new VideoFile(rawVideoData, MediaType.GetByExtension(videoFile.AfterLast('.').ToLowerFast()));
        }
        catch (Exception ex)
        {
            throw new SwarmUserErrorException($"SeedVR2 Video File: Could not read video: {ex.Message}");
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

        string ditModel = ResolveModelFilename(modelKey);

        // Get resolution settings - use SeedVR2UpscaleBy to calculate target
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 2.0);  // Default 2x for video
        // Check if user specified a direct resolution target
        int resolution;
        if (g.UserInput.TryGet(SeedVR2Resolution, out int userResolution) && userResolution > 0)
        {
            resolution = userResolution;
        }
        else
        {
            // Resolution will be calculated dynamically based on input video and upscale factor
            // For now, use a sensible default that will be overridden by the actual video dimensions
            resolution = (int)(540 * seedvrUpscaleBy);  // Assume 540p input, scale up
        }

        // Get other parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);
        string attentionMode = g.UserInput.Get(SeedVR2AttentionMode, "sdpa").Before("///");
        bool ditNeedsOffload = cacheModel || blockSwap > 0;
        string ditOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: ditNeedsOffload, requireNotNoneReason: "DiT caching / Block Swap");
        string vaeOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: cacheModel, requireNotNoneReason: "VAE caching");

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            blockSwap = maxBlocks;
        }

        Logs.Info($"SeedVR2 Video File: model={ditModel}, blockSwap={blockSwap}, upscaleBy={seedvrUpscaleBy}, colorCorrection={colorCorrection}");

        // === Create workflow nodes ===

        // 1. Load the video - embedded as base64 via SwarmUI's LoadVideo helper (SwarmLoadVideoB64 +
        //    GetVideoComponents internally), so ComfyUI never sees a host filesystem path.
        WGNodeData videoData = g.LoadVideo(sourceVideo, "${seedvr2_source_video}", false);
        JArray loadedVideoFrames = videoData.Path;
        JArray videoAudio = videoData.AttachedAudio?.Path;
        JArray videoFps = (JArray)videoData.FPS;

        // 3. Add VRAM cleanup node to unload any existing models before SeedVR2
        // This frees up VRAM so SeedVR2 has enough room to load its models
        if (g.Features.Contains("kjnodes"))
        {
            string vramCleanupNode = g.CreateNode("VRAM_Debug", new JObject()
            {
                ["empty_cache"] = true,
                ["gc_collect"] = true,
                ["unload_all_models"] = true,
                ["image_pass"] = loadedVideoFrames
            });
            loadedVideoFrames = new JArray() { vramCleanupNode, 1 };  // output index 1 is image_pass
        }

        // 4. SeedVR2LoadDiTModel - load the upscaler model
        JObject ditLoaderInputs = new JObject()
        {
            ["model"] = ditModel,
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = ditOffloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = attentionMode
        };
        if (cacheModel && g.UserInput.Get(SeedVR2TorchCompile, false))
        {
            string compileNode = g.CreateNode("SeedVR2TorchCompileSettings", new JObject()
            {
                ["backend"] = "inductor",
                ["mode"] = "default",
                ["fullgraph"] = false,
                ["dynamic"] = false,
                ["dynamo_cache_size_limit"] = 64,
                ["dynamo_recompile_limit"] = 128
            });
            ditLoaderInputs["torch_compile_args"] = new JArray() { compileNode, 0 };
        }
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // 4. SeedVR2LoadVAEModel - load VAE with optional tiling
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = vaeOffloadDevice,
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

        // 5. Get seed from user input
        long seed = g.UserInput.Get(T2IParamTypes.Seed, 42);

        // 6. SeedVR2VideoUpscaler - upscale the video frames
        JObject upscalerInputs = new JObject()
        {
            ["image"] = loadedVideoFrames,  // frames output (via VRAM cleanup if available)
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
            ["input_noise_scale"] = g.UserInput.Get(SeedVR2InputNoiseScale, 0.0),
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };
        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);

        // 7. CreateVideo - combine upscaled frames with original audio
        string createVideoNode = g.CreateNode("CreateVideo", new JObject()
        {
            ["images"] = new JArray() { upscalerNode, 0 },
            ["audio"] = videoAudio,
            ["fps"] = videoFps
        });

        // 9. SaveVideo - save the upscaled result
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
        g.CurrentMedia = new WGNodeData(new JArray() { upscalerNode, 0 }, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
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
        
        // Only activate for generation requests that produce video output.
        if (!IsSeedVR2VideoGenerationRequest(g))
        {
            return;
        }

        // Only activate if the SeedVR2 group toggle is enabled
        if (!g.UserInput.TryGet(SeedVR2Model, out string modelChoice))
        {
            return;
        }

        // Skip if the priority-6 step already upscaled the intermediate image for this generator.
        // This is more reliable than inferring from model type, since some models may or may
        // not have a decodable image at priority 6 depending on pipeline configuration.
        string stage = g.UserInput.Get(SeedVR2UpscaleStage, "after_video").Before("///");
        if (stage == "before_video" && PreVideoUpscaleRan.TryGetValue(g, out _))
        {
            return;
        }

        // Verify feature is available
        if (!g.Features.Contains("seedvr2_upscaler"))
        {
            throw new SwarmUserErrorException("SeedVR2 upscaler selected for video, but SeedVR2 nodes are not installed in ComfyUI. Please install the ComfyUI-SeedVR2_VideoUpscaler custom node.");
        }

        // Ensure we have video frames to upscale
        if (g.CurrentMedia is null)
        {
            Logs.Warning("SeedVR2 Video: No CurrentMedia available to upscale");
            return;
        }

        // Store the original video frames reference for ReplaceNodeConnection
        JArray originalVideoFrames = g.CurrentMedia.Path;

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
        string ditModel = ResolveModelFilename(modelKey);

        // Calculate target resolution based on upscale factor
        double upscaleFactor = g.UserInput.Get(T2IParamTypes.RefinerUpscale, 1.0);
        double seedvrUpscaleBy = g.UserInput.Get(SeedVR2UpscaleBy, 1.0);
        int baseWidth = (int)Math.Round(g.UserInput.GetImageWidth() * upscaleFactor);
        int baseHeight = (int)Math.Round(g.UserInput.GetImageHeight() * upscaleFactor);
        // Check if user specified a direct resolution target
        int resolution;
        if (g.UserInput.TryGet(SeedVR2Resolution, out int userResolution) && userResolution > 0)
        {
            resolution = userResolution;
        }
        else
        {
            int targetWidth = (int)Math.Round(baseWidth * seedvrUpscaleBy);
            int targetHeight = (int)Math.Round(baseHeight * seedvrUpscaleBy);
            resolution = Math.Min(targetWidth, targetHeight);
        }

        // Get video-specific parameters
        int temporalOverlap = g.UserInput.Get(SeedVR2TemporalOverlap, 3);
        bool uniformBatchSize = g.UserInput.Get(SeedVR2UniformBatchSize, true);

        // Get other parameters
        string colorCorrection = g.UserInput.Get(SeedVR2ColorCorrection, "none");
        double latentNoiseScale = g.UserInput.Get(SeedVR2LatentNoiseScale, 0.0);
        bool cacheModel = g.UserInput.Get(SeedVR2CacheModel, false);
        string attentionMode = g.UserInput.Get(SeedVR2AttentionMode, "sdpa").Before("///");
        bool ditNeedsOffload = cacheModel || blockSwap > 0;
        string ditOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: ditNeedsOffload, requireNotNoneReason: "DiT caching / Block Swap");
        string vaeOffloadDevice = SeedVR2DeviceUtils.ResolveSeedVR2OffloadDevice(g, SeedVR2VAEOffloadDevice, requireNotNone: cacheModel, requireNotNoneReason: "VAE caching");

        // Cap block swap for model type
        bool is7BModel = modelKey.Contains("-7b-");
        int maxBlocks = is7BModel ? 36 : 32;
        if (blockSwap > maxBlocks)
        {
            Logs.Warning($"SeedVR2 Video: Block swap {blockSwap} exceeds max {maxBlocks} for {(is7BModel ? "7B" : "3B")} model, capping");
            blockSwap = maxBlocks;
        }

        int videoBatchSize = g.UserInput.Get(SeedVR2VideoBatchSize, 33);
        Logs.Info($"SeedVR2 Video: Upscaling video frames to resolution {resolution} (model={ditModel}, batch_size={videoBatchSize}, temporal_overlap={temporalOverlap})");

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
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["blocks_to_swap"] = blockSwap,
            ["swap_io_components"] = blockSwap > 0,
            ["offload_device"] = ditOffloadDevice,
            ["cache_model"] = cacheModel,
            ["attention_mode"] = attentionMode
        };
        if (cacheModel && g.UserInput.Get(SeedVR2TorchCompile, false))
        {
            string compileNode = g.CreateNode("SeedVR2TorchCompileSettings", new JObject()
            {
                ["backend"] = "inductor",
                ["mode"] = "default",
                ["fullgraph"] = false,
                ["dynamic"] = false,
                ["dynamo_cache_size_limit"] = 64,
                ["dynamo_recompile_limit"] = 128
            });
            ditLoaderInputs["torch_compile_args"] = new JArray() { compileNode, 0 };
        }
        string ditLoaderNode = g.CreateNode("SeedVR2LoadDiTModel", ditLoaderInputs);

        // Create SeedVR2LoadVAEModel node
        JObject vaeLoaderInputs = new JObject()
        {
            ["model"] = "ema_vae_fp16.safetensors",
            ["device"] = SeedVR2DeviceUtils.GetPrimaryComputeDevice(),
            ["encode_tiled"] = tiledVAE,
            ["decode_tiled"] = tiledVAE,
            ["offload_device"] = vaeOffloadDevice,
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
            ["input_noise_scale"] = g.UserInput.Get(SeedVR2InputNoiseScale, 0.0),
            ["latent_noise_scale"] = latentNoiseScale,
            ["offload_device"] = "cpu",
            ["enable_debug"] = false
        };

        string upscalerNode = g.CreateNode("SeedVR2VideoUpscaler", upscalerInputs);
        JArray upscalerOutput = new JArray() { upscalerNode, 0 };

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

        // Update CurrentMedia for any subsequent steps
        g.CurrentMedia = new WGNodeData(upscalerOutput, g, WGNodeData.DT_IMAGE, g.CurrentCompat());

        Logs.Info($"SeedVR2 Video: Video upscaling workflow complete");
    }

    /// <summary>Resolves an image path to an absolute file system path.</summary>
    /// <param name="imagePath">The image path (may be Output/, View/, or absolute).</param>
    /// <param name="user">The user for resolving output directories.</param>
    /// <returns>The resolved absolute file path, or null if it cannot be resolved.</returns>
    private static string ResolveImagePath(string imagePath, User user)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return null;
        }

        // Expand home directory
        if (imagePath.StartsWith("~/"))
        {
            imagePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + imagePath[1..];
        }

        // Resolve Output/ or View/ paths using user's output directory
        if (user is not null)
        {
            string relativePath = null;
            if (imagePath.StartsWith("Output/"))
            {
                relativePath = imagePath["Output/".Length..];
            }
            else if (imagePath.StartsWith("View/"))
            {
                // View/{user_id}/path format - strip View/{user_id}/ prefix
                string afterView = imagePath["View/".Length..];
                int slashIndex = afterView.IndexOf('/');
                if (slashIndex > 0)
                {
                    relativePath = afterView[(slashIndex + 1)..];
                }
            }

            if (relativePath is not null)
            {
                string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
                return UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
            }
        }

        return imagePath;
    }

    /// <summary>Extracts ExifProfile from source image (file path or data URL).</summary>
    /// <param name="sourceImagePath">The source image path or data URL.</param>
    /// <param name="user">The user for resolving paths.</param>
    /// <returns>A cloned ExifProfile from the source, or null if extraction fails.</returns>
    private static ExifProfile ExtractSourceExif(string sourceImagePath, User user)
    {
        try
        {
            if (sourceImagePath.StartsWith("data:"))
            {
                ImageFile img = ImageFile.FromDataString(sourceImagePath);
                return img.ToIS.Metadata.ExifProfile?.DeepClone();
            }

            // Resolve Output/View paths
            string resolvedPath = ResolveImagePath(sourceImagePath, user);
            if (string.IsNullOrEmpty(resolvedPath) || !System.IO.File.Exists(resolvedPath))
            {
                return null;
            }

            using ISImage image = ISImage.Load(resolvedPath);
            return image.Metadata.ExifProfile?.DeepClone();
        }
        catch (Exception ex)
        {
            Logs.Debug($"SeedVR2: Could not extract source EXIF: {ex.Message}");
            return null;
        }
    }

    /// <summary>Set of SeedVR2 parameter IDs that should be preserved from the current request, not overwritten by source metadata.</summary>
    /// <remarks>IDs are cleaned by T2IParamTypes.CleanTypeName which keeps only lowercase letters.</remarks>
    private static readonly HashSet<string> SeedVR2ParamIds =
    [
        "seedvrpresetmodel", "seedvrupscaleby", "seedvrresolution", "seedvrblockswap",
        "seedvrcolorcorrection", "seedvrstepmode", "seedvrtwostepmode", "seedvrpredownscale", "seedvrtiledvae",
        "seedvrlatentnoise", "seedvrinputnoise", "seedvrlatentnoisescale", "seedvrinputnoisescale", "seedvrcontrolaftergenerate",
        "seedvrcachemodel", "seedvrattentionmode", "seedvrtorchcompile",
        "seedvrimagetilesize", "seedvrimagemaskblur", "seedvrimagetileoverlap", "seedvrtilepadding", "seedvrimagetileupscaleresolution",
        "seedvrimageblendingmethod", "seedvrimagetilingstrategy", "seedvrimageantialiasingstrength",
        "seedvrvaeoffloaddevice", "seedvrvideobatchsize", "seedvrtemporaloverlap", "seedvruniformbatchsize",
        "seedvrvideofile", "seedvrimagefile"
    ];

    /// <summary>Set of parameter IDs to skip when applying source metadata - not used in file upscaling or could cause validation errors.</summary>
    /// <remarks>IDs are cleaned by T2IParamTypes.CleanTypeName which keeps only lowercase letters.</remarks>
    private static readonly HashSet<string> SkipSourceMetadataParams =
    [
        // Model params - may reference models that no longer exist (fixes issue #13)
        "model", "refinermodel", "videomodel", "videoswapmodel", "loras", "loraweights", "loratencweights",
        // Resolution params - determined by source image, not original generation
        "width", "height", "aspectratio", "sidelength", "altresolutionheightmult", "rawresolution",
        // Backend params - not relevant for file upscaling
        "internalbackendtype", "exactbackendid"
    ];

    /// <summary>Applies source image metadata to the user input, preserving original generation params.</summary>
    /// <remarks>
    /// For file upscaling, the output should preserve the original image's generation metadata (prompt, model, etc.)
    /// instead of using the current UI values. Only SeedVR2-specific params are kept from the current request.
    /// </remarks>
    /// <param name="imagePath">The path to the source image file.</param>
    /// <param name="input">The user input to modify.</param>
    private static void ApplySourceImageMetadata(string imagePath, T2IParamInput input)
    {
        try
        {
            byte[] imageBytes = System.IO.File.ReadAllBytes(imagePath);
            string extension = System.IO.Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
            MediaType mediaType = MediaType.GetByExtension(extension) ?? MediaType.ImagePng;
            ImageFile imageFile = new Image(imageBytes, mediaType);
            ApplySourceImageMetadataCore(imageFile, input);
        }
        catch (Exception ex)
        {
            Logs.Debug($"SeedVR2: Could not load source image metadata from file: {ex.Message}");
        }
    }

    /// <summary>Applies source image metadata to the user input from an ImageFile object.</summary>
    /// <param name="sourceImage">The source image.</param>
    /// <param name="input">The user input to modify.</param>
    private static void ApplySourceImageMetadata(ImageFile sourceImage, T2IParamInput input)
    {
        try
        {
            ApplySourceImageMetadataCore(sourceImage, input);
        }
        catch (Exception ex)
        {
            Logs.Debug($"SeedVR2: Could not load source image metadata from data URL: {ex.Message}");
        }
    }

    /// <summary>Core implementation for applying source image metadata.</summary>
    private static void ApplySourceImageMetadataCore(ImageFile sourceImage, T2IParamInput input)
    {
        JObject sourceParams = sourceImage.GetSUIMetadata();
        if (sourceParams is null || sourceParams.Count == 0)
        {
            Logs.Info("SeedVR2 Metadata: No source metadata found in image, using current UI values");
            return;
        }

        int appliedCount = 0;
        foreach (JProperty prop in sourceParams.Properties())
        {
            // Use the same normalization as T2IParamTypes.CleanTypeName (lowercase letters only)
            string paramId = T2IParamTypes.CleanTypeName(prop.Name);

            // Skip SeedVR2 params - we want to use current request values for those
            if (SeedVR2ParamIds.Contains(paramId))
            {
                continue;
            }

            // Skip params that are not used in file upscaling or could cause validation errors (issue #13)
            if (SkipSourceMetadataParams.Contains(paramId))
            {
                continue;
            }

            // Skip if no param type exists for this ID
            if (!T2IParamTypes.TryGetType(prop.Name, out T2IParamType paramType, input))
            {
                continue;
            }

            // Apply the original value to the input
            try
            {
                string valueStr = prop.Value?.ToString();
                if (!string.IsNullOrEmpty(valueStr))
                {
                    input.Set(paramType, valueStr);
                    appliedCount++;
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"SeedVR2 Metadata: Could not apply param '{prop.Name}': {ex.Message}");
            }
        }

        Logs.Info($"SeedVR2 Metadata: Applied {appliedCount} original params from source image");
    }

    /// <summary>Handles PostGenerateEvent - currently unused but reserved for future enhancements.</summary>
    /// <param name="p">The post-generation event parameters.</param>
    private static void HandleSeedVR2PostGeneration(T2IEngine.PostGenerationEventParams p)
    {
        // EXIF is now extracted during workflow generation in GenerateSeedVR2ImageFileWorkflow
        // This handler is kept for future enhancements
    }

    /// <summary>Handles PostBatchEvent to apply source EXIF metadata to output files.</summary>
    /// <param name="p">The post-batch event parameters.</param>
    private static void HandleSeedVR2PostBatch(T2IEngine.PostBatchEventParams p)
    {
        // Check if we have stored source EXIF for this request
        long requestId = p.UserInput.UserRequestId;
        if (!PendingSourceExif.TryRemove(requestId, out byte[] exifBytes))
        {
            return;
        }

        Logs.Info($"SeedVR2 EXIF: Processing request {requestId} with {exifBytes.Length} bytes of EXIF data");

        // Reconstruct ExifProfile from bytes
        ExifProfile sourceExif;
        try
        {
            sourceExif = new ExifProfile(exifBytes);
            Logs.Info($"SeedVR2 EXIF: Reconstructed profile with {sourceExif.Values.Count()} tags");
        }
        catch (Exception ex)
        {
            Logs.Warning($"SeedVR2 EXIF: Could not reconstruct EXIF profile: {ex.Message}");
            return;
        }

        // Get the user's output directory to find saved files
        User user = p.UserInput.SourceSession?.User;
        if (user is null)
        {
            Logs.Warning("SeedVR2 EXIF: No user session found");
            return;
        }

        // Find files that are currently being saved by this session
        // StillSavingFiles maps file paths to their save tasks
        string userOutputDir = System.IO.Path.GetFullPath(Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory));
        Logs.Info($"SeedVR2 EXIF: Checking StillSavingFiles ({Session.StillSavingFiles.Count} entries) for files in {userOutputDir}");
        List<string> filesToProcess = [];
        foreach (var kvp in Session.StillSavingFiles)
        {
            string filePath = kvp.Key;
            Logs.Verbose($"SeedVR2 EXIF: Checking file {filePath}");
            // Check if this file belongs to the current user's output directory
            if (filePath.StartsWith(userOutputDir))
            {
                filesToProcess.Add(filePath);
            }
        }

        Logs.Info($"SeedVR2 EXIF: Found {filesToProcess.Count} files to process");

        if (filesToProcess.Count == 0)
        {
            Logs.Warning("SeedVR2 EXIF: No files found in StillSavingFiles for this user's output directory");
            return;
        }

        // Apply EXIF to each file after it finishes saving
        foreach (string filePath in filesToProcess)
        {
            if (Session.StillSavingFiles.TryGetValue(filePath, out Task<byte[]> saveTask))
            {
                saveTask.ContinueWith(_ =>
                {
                    // Small delay to ensure file is fully written
                    Task.Delay(100).Wait();
                    ApplySourceExifToFile(filePath, sourceExif);
                });
            }
        }
    }

    /// <summary>Applies source EXIF metadata to a saved image file, preserving SwarmUI's UserComment.</summary>
    /// <param name="filePath">The path to the saved image file.</param>
    /// <param name="sourceExif">The source EXIF profile to apply.</param>
    private static void ApplySourceExifToFile(string filePath, ExifProfile sourceExif)
    {
        try
        {
            if (!System.IO.File.Exists(filePath))
            {
                Logs.Debug($"SeedVR2 EXIF: File does not exist: {filePath}");
                return;
            }

            // Only apply EXIF to JPEG/TIFF files (PNG uses different metadata)
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".tif" && ext != ".tiff")
            {
                Logs.Debug($"SeedVR2 EXIF: Skipping non-EXIF format: {ext}");
                return;
            }

            // Load image, modify EXIF, save back
            byte[] imageBytes = System.IO.File.ReadAllBytes(filePath);
            using ISImage image = ISImage.Load(imageBytes);

            // Preserve SwarmUI's UserComment (contains generation metadata)
            string userComment = null;
            if (image.Metadata.ExifProfile?.TryGetValue(ExifTag.UserComment, out var uc) ?? false)
            {
                userComment = uc.Value.Text;
            }

            // Clone source EXIF and apply
            ExifProfile merged = sourceExif.DeepClone();

            // Restore UserComment with SwarmUI metadata
            if (!string.IsNullOrEmpty(userComment))
            {
                merged.SetValue(ExifTag.UserComment, userComment);
            }

            // Apply merged EXIF
            image.Metadata.ExifProfile = merged;

            // Save with appropriate encoder based on extension
            using var outputStream = new System.IO.FileStream(filePath, System.IO.FileMode.Create);
            if (ext == ".jpg" || ext == ".jpeg")
            {
                image.SaveAsJpeg(outputStream);
            }
            else if (ext == ".tif" || ext == ".tiff")
            {
                image.SaveAsTiff(outputStream);
            }

            Logs.Info($"SeedVR2 EXIF: Applied {sourceExif.Values.Count()} camera EXIF tags to {System.IO.Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            Logs.Warning($"SeedVR2 EXIF: Failed to apply source EXIF to {filePath}: {ex.Message}");
        }
    }
}
