from __future__ import annotations

import time
from typing import Any
import torch
from comfy_api.latest import io

from .progress import Progress
from .image_utils import ImageUtils
from .logger import Logger
from .tiling import TileUtils
from .stitching import StitchingPipeline

class SeedVR2ImageUpscaler(io.ComfyNode):
    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SeedVR2ImageUpscaler",
            display_name="SeedVR2 Image Upscaler",
            category="SEEDVR2",
            description="Tiled upscaling node that wraps SeedVR2 for memory-efficient processing",
            inputs=[
                io.Image.Input("image",
                    tooltip=("Input image to upscale.")
                ),
                io.Custom("SEEDVR2_DIT").Input("dit",
                    tooltip="DiT model configuration from 'SeedVR2 (Down)Load DiT Model' node"
                ),
                io.Custom("SEEDVR2_VAE").Input("vae",
                    tooltip="VAE model configuration from 'SeedVR2 (Down)Load VAE Model' node"
                ),
                io.Int.Input("seed",
                    default=42,
                    min=0,
                    max=2**32 - 1,
                    step=1,
                    tooltip=(
                        "Random seed for reproducible generation (default: 42).\n"
                        "Same seed with same inputs produces identical output."
                    )
                ),
                io.Int.Input("resolution",
                    default=1080,
                    min=16,
                    step=2,
                    tooltip=(
                        "Target resolution for the shortest edge in pixels (default: 1080).\n"
                        "Automatically maintains aspect ratio of input.\n"
                        "Even values required for optimal processing."
                    )
                ),
                io.Int.Input("tile_size",
                    default=512,
                    min=64,
                    max=8192,
                    step=8,
                    tooltip=(
                        "Square tile size in pixels (applied to both width and height).\n"
                        "Smaller tiles use less VRAM but may show more seams."
                    )
                ),
                io.Int.Input("mask_blur",
                    default=0,
                    min=0,
                    max=64,
                    step=1,
                    tooltip=(
                        "Tile edge blending.\n"
                        "0=multi-band frequency separation (best detail)\n"
                        "1-3=minimal blur\n"
                        "4+=traditional blur."
                    )
                ),
                io.Int.Input("tile_overlap",
                    default=32,
                    min=0,
                    max=8192,
                    step=8,
                    tooltip=(
                        "Overlap between tiles in pixels.\n"
                        "Higher values reduce seams but increase processing time.\n"
                        "Recommended: 32-64."
                    )
                ),
                io.Int.Input("tile_upscale_resolution",
                    default=0,
                    min=0,
                    max=8192,
                    step=8,
                    optional=True,
                    tooltip=(
                        "Resolution for upscaling each tile.\n"
                        "Set 0 to auto-infer from tile_size and output scale.\n"
                        "Higher=better quality but more VRAM."
                    )
                ),
                io.Combo.Input("tiling_strategy",
                    options=["Linear", "Chess"],
                    default="Linear",
                    tooltip=(
                        "Tile processing order.\n"
                        "Chess=checkerboard pattern for better blending,\n"
                        "Linear=row-by-row (faster)."
                    )
                ),
                io.Float.Input("anti_aliasing_strength",
                    default=0.0,
                    min=0.0,
                    max=1.0,
                    step=0.05,
                    optional=True,
                    tooltip=(
                        "Edge-aware smoothing strength (default: 0.0, disabled).\n"
                        "Adds subtle smoothing to image edges.\n"
                        "Range: 0.0 (no smoothing) to 1.0 (maximum smoothing).\n"
                        "Can help with certain types of artifacts."
                    )
                ),
                io.Combo.Input("blending_method",
                    options=["auto", "multiband", "bilateral", "content_aware", "linear", "simple"],
                    default="auto",
                    tooltip=(
                        "Blending algorithm.\n"
                        "auto (mask_blur based)\n"
                        "multiband (Laplacian pyramid/frequency separation)\n"
                        "bilateral (edge-preserving filter)\n"
                        "content_aware (structure-adaptive)\n"
                        "linear (alpha blend)\n"
                        "simple (pixel averaging)."
                    )
                ),
                io.Combo.Input("color_correction",
                    options=["lab", "wavelet", "wavelet_adaptive", "hsv", "adain", "none"],
                    default="lab",
                    tooltip=(
                        "Corrects color shifts in upscaled output to match original input (default: lab).\n"
                        "The upscaling process may alter colors; this applies color grading to restore them.\n"
                        "\n"
                        "• lab: Perceptual color matching with detail preservation (recommended)\n"
                        "• wavelet: Frequency-based natural colors, preserves fine details\n"
                        "• wavelet_adaptive: Wavelet base with targeted saturation correction\n"
                        "• hsv: Hue-conditional saturation matching\n"
                        "• adain: Statistical style transfer approach\n"
                        "• none: No color correction applied"
                    )
                ),
                io.Float.Input("input_noise_scale",
                    default=0.0,
                    min=0.0,
                    max=1.0,
                    step=0.001,
                    optional=True,
                    tooltip=(
                        "Input noise injection scale (default: 0.0, disabled).\n"
                        "Adds controlled variation to input images before encoding.\n"
                        "Range: 0.0 (no noise) to 1.0 (maximum noise).\n"
                        "Can help with certain types of artifacts."
                    )
                ),
                io.Combo.Input("offload_device",
                    options=SeedVR2ImageUpscaler._get_device_list(include_none=True, include_cpu=True),
                    default="cpu",
                    optional=True,
                    tooltip=(
                        "Device for storing intermediate tensors between processing phases (default: cpu).\n"
                        "• 'none': Keep all tensors on inference device (fastest but highest VRAM usage)\n"
                        "• 'cpu': Offload to system RAM (recommended for long videos, slower transfers)\n"
                        "• 'cuda:X': Offload to another GPU (good balance if available, faster than CPU)"
                    )
                ),
                io.Boolean.Input("enable_debug",
                    default=False,
                    optional=True,
                    tooltip=(
                        "Enable detailed debug logging (default: False).\n"
                        "Shows memory usage, timing information, and processing details.\n"
                        "Useful for troubleshooting errors and performance issues."
                    )
                ),
            ],
            outputs=[
                io.Image.Output(
                    tooltip="Upscaled image with color correction applied. Format (RGB/RGBA) matches input. Range [0, 1] normalized for ComfyUI compatibility."
                )
            ]
        )

    @classmethod
    def execute(
        cls,
        image: torch.Tensor,
        dit: dict[str, Any],
        vae: dict[str, Any],
        seed: int = 42,
        resolution: int = 1080,
        tile_size: int = 512,
        mask_blur: int = 0,
        tile_overlap: int = 32,
        tile_upscale_resolution: int = 0,
        tiling_strategy: str = "Linear",
        anti_aliasing_strength: float = 0.0,
        blending_method: str = "auto",
        color_correction: str = "wavelet",
        input_noise_scale: float = 0.0,
        offload_device: str = "none",
        enable_debug: bool = False,
    ) -> io.NodeOutput:
        progress: Progress | None = None
        tile_upscale_resolution = int(tile_upscale_resolution)

        try:
            start_time = time.perf_counter()
            pil_image = ImageUtils.tensor_to_pil(image)
            upscale_factor = resolution / min(pil_image.width, pil_image.height)
            output_width = int(pil_image.width * upscale_factor)
            output_height = int(pil_image.height * upscale_factor)
            resolved_tile_upscale_resolution = SeedVR2ImageUpscaler._resolve_tile_upscale_resolution(
                tile_upscale_resolution, tile_size, upscale_factor
            )

            main_tiles = TileUtils.generate_tiles(pil_image, tile_size, tile_overlap, tiling_strategy)
            progress = Progress(len(main_tiles), enable_debug=enable_debug)
            progress.initialize_websocket_progress()

            Logger.log(
                f"Input={pil_image.width}x{pil_image.height}, Output={output_width}x{output_height}, "
                f"Tiles={len(main_tiles)}, TileSize={tile_size}x{tile_size}, "
                f"Overlap={tile_overlap}, Strategy={tiling_strategy}, Blend={blending_method}, "
                f"TileUpscale={resolved_tile_upscale_resolution}"
                f"{' (auto)' if tile_upscale_resolution <= 0 else ''}, "
                f"InputNoise={input_noise_scale:.4f}, Offload={offload_device}"
            )
    
            output_image = StitchingPipeline.process_and_stitch(
                tiles=main_tiles,
                width=output_width,
                height=output_height,
                dit_config=dit,
                vae_config=vae,
                seed=seed,
                tile_upscale_resolution=resolved_tile_upscale_resolution,
                upscale_factor=upscale_factor,
                mask_blur=mask_blur,
                progress=progress,
                original_image=pil_image,
                anti_aliasing_strength=anti_aliasing_strength,
                blending_method=blending_method,
                color_correction=color_correction,
                input_noise_scale=input_noise_scale,
                offload_device=offload_device,
                enable_debug=enable_debug,
            )

            progress.finalize_websocket_progress()
            Logger.log(f"Completed tiling upscale in {time.perf_counter() - start_time:.2f}s")

            return (ImageUtils.pil_to_tensor(output_image),)

        except Exception as e:
            if progress is not None:
                progress.finalize_websocket_progress()
            raise e

    @staticmethod
    def _get_device_list(include_none: bool = False, include_cpu: bool = False) -> list[str]:
        devs = []
        has_cuda = False
        has_mps = False

        try:
            if torch.cuda.is_available():
                devs += [f"cuda:{i}" for i in range(torch.cuda.device_count())]
                has_cuda = True
        except Exception:
            pass

        try:
            if hasattr(torch.backends, 'mps') and torch.backends.mps.is_available():
                devs.append("mps")
                has_mps = True
        except Exception:
            pass
        
        result = []
        if include_none:
            result.append("none")

        if include_cpu and (has_cuda or not has_mps):
            result.append("cpu")

        result.extend(devs)
        
        return result if result else []

    @staticmethod
    def _resolve_tile_upscale_resolution(tile_upscale_resolution: int, tile_size: int, upscale_factor: float) -> int:
        min_resolution = 64
        max_resolution = 8192
        step = 8

        if tile_upscale_resolution <= 0:
            tile_upscale_resolution = int(round(tile_size * upscale_factor))

        tile_upscale_resolution = max(min_resolution, min(max_resolution, tile_upscale_resolution))
        tile_upscale_resolution = int(round(tile_upscale_resolution / step) * step)

        return max(min_resolution, min(max_resolution, tile_upscale_resolution))
