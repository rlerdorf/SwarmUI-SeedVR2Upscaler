# SwarmUI SeedVR2 Upscaler Extension

Integrates [SeedVR2](https://github.com/rhymes-ai/SeedVR2) AI upscaler into SwarmUI's Refiner Upscale Method dropdown, with automatic VRAM detection, quality presets, and 2-step upscaling for reduced artifacts.

## Features

- **One-Click Install**: Install the SeedVR2 ComfyUI node directly from SwarmUI
- **Auto VRAM Detection**: Automatically selects the best model based on your GPU's available VRAM
- **Quality Presets**: Fast, Balanced, Quality, and Max Quality options
- **2-Step Upscaling**: Downscales image before upscaling to reduce artifacts (enabled by default)
- **Tiled VAE**: Process large images in tiles to reduce VRAM usage
- **Block Swap**: Offload transformer blocks to CPU for additional VRAM savings
- **VRAM Cleanup**: Automatically unloads the main generation model before upscaling (requires KJNodes)

## Requirements

1. **SeedVR2 ComfyUI Node**: [ComfyUI-SeedVR2_VideoUpscaler](https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler) - can be installed with one click from the SeedVR2 Upscaler parameter group in SwarmUI
2. **KJNodes** (recommended): Install [ComfyUI-KJNodes](https://github.com/kijai/ComfyUI-KJNodes) for automatic VRAM cleanup before upscaling

## Installation

### Via SwarmUI Extension Manager
1. Go to **Server** → **Extensions** tab
2. Find "SeedVR2Upscaler" in the available extensions list
3. Click **Install**
4. Restart SwarmUI

### Manual Installation
```bash
cd SwarmUI/src/Extensions
git clone https://github.com/rlerdorf/SwarmUI-SeedVR2Upscaler
```

Then rebuild and restart SwarmUI:
- **Linux/Mac**: Run `./launch-linux.sh` (it will detect the new extension and rebuild automatically)
- **Windows**: Run `launch-windows.bat` (it will detect the new extension and rebuild automatically)

Or manually rebuild:
```bash
dotnet build src/SwarmUI.csproj --configuration Release -o ./src/bin/live_release
```

## Usage

1. In the **Generate** tab, expand the **Refiner** section
2. Set **Refiner Upscale** to your desired upscale factor (e.g., 1.25x)
3. Set **Refiner Upscale Method** to one of the SeedVR2 options:
   - **Auto (VRAM-based)**: Detects GPU VRAM and picks optimal settings
   - **Fast (3B Q4)**: Fastest, lowest VRAM (~8GB)
   - **Balanced (3B FP8)**: Good balance of speed and quality (~12GB)
   - **Quality (7B FP8)**: High quality (~20GB)
   - **Max Quality (7B Sharp FP16)**: Best quality (~24GB+)

## Presets

| Preset | Model | Block Swap | Tiled VAE | VRAM Required |
|--------|-------|------------|-----------|---------------|
| Fast | 3B Q4 | 20 | Yes | ~8GB |
| Balanced | 3B FP8 | 12 | No | ~12GB |
| Quality | 7B FP8 | 16 | Yes | ~20GB |
| Max Quality | 7B Sharp FP16 | 0 | No | ~24GB+ |

## Advanced Parameters

Found under the **SeedVR2 Upscaler** group in advanced settings:

| Parameter | Description | Default |
|-----------|-------------|---------|
| **SeedVR2 Resolution** | Override target resolution (shortest edge) | Auto-calculated |
| **SeedVR2 Block Swap** | Transformer blocks to offload to CPU (0-36) | Preset-based |
| **SeedVR2 Color Correction** | Color correction method (lab, wavelet, hsv, adain, none) | lab |
| **SeedVR2 2-Step Mode** | Downscale before upscaling to reduce artifacts | Enabled |
| **SeedVR2 Pre-Downscale** | Downscale factor for 2-step mode (0.25-0.9) | 0.5 |
| **SeedVR2 Tiled VAE** | Process in tiles to reduce VRAM | Preset-based |
| **SeedVR2 Max Resolution** | Cap maximum output resolution | 20480 |
| **SeedVR2 Latent Noise** | Add noise in latent space for detail variation | 0.0 |
| **SeedVR2 Cache Model** | Keep models loaded between generations | Disabled |

## How 2-Step Mode Works

```
Original Image (e.g., 1920x2560)
        ↓
  [ImageScaleBy] (scale_by=0.5)
        ↓
 Downscaled Image (960x1280)
        ↓
  [SeedVR2VideoUpscaler]
        ↓
 Final Upscaled Image (target resolution)
```

By downscaling the image first, SeedVR2 receives a cleaner input with less existing detail to work around, allowing it to generate finer details during upscaling and reducing artifacts.

## Auto VRAM Detection

When using "Auto (VRAM-based)", the extension queries your GPU and selects:

| GPU VRAM | Model Selected | Block Swap | Tiled VAE |
|----------|----------------|------------|-----------|
| 24GB+ | 7B Sharp FP16 | 0 | No |
| 20-24GB | 7B FP8 | 8 | No |
| 16-20GB | 7B Q4 | 16 | Yes |
| 12-16GB | 3B FP8 | 12 | Yes |
| 8-12GB | 3B Q4 | 20 | Yes |
| <8GB | 3B Q4 | 28 | Yes |

## License

MIT License

## Credits

- [SeedVR2](https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler) by NumZ
- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) by mcmonkey
