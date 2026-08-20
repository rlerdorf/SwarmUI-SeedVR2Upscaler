from __future__ import annotations

import torch
from typing import TYPE_CHECKING, Literal
from .logger import Logger

if TYPE_CHECKING:
    from src.interfaces.video_upscaler import SeedVR2VideoUpscaler


class SeedVR2Adapter:
    @staticmethod
    def get_upscaler_class() -> type["SeedVR2VideoUpscaler"]:
        import nodes

        for node_class in nodes.NODE_CLASS_MAPPINGS.values():
            if getattr(node_class, "__name__", "") == "SeedVR2VideoUpscaler":
                return node_class

        raise RuntimeError(
            "SeedVR2VideoUpscaler node not found. "
            "Please install ComfyUI-SeedVR2_VideoUpscaler v2.5 or later."
        )

    @staticmethod
    def execute_seedvr2(
        *,
        images: torch.Tensor,
        dit_config: dict[str, object],
        vae_config: dict[str, object],
        seed: int,
        resolution: int = 1080,
        batch_size: int = 1,
        color_correction: Literal["lab", "wavelet", "wavelet_adaptive", "hsv", "adain", None] = "wavelet",
        input_noise_scale: float = 0.0,
        offload_device: str = "none",
        enable_debug: bool = False,
    ) -> torch.Tensor:
        """Execute SeedVR2 upscaling on a batch of images.

        Args:
            images: Input images tensor (N, H, W, C) in [0, 1] range
            dit_config: DiT model configuration from SeedVR2LoadDiTModel node
            vae_config: VAE model configuration from SeedVR2LoadVAEModel node
            seed: Random seed for reproducibility
            resolution: Target resolution for the shortest edge
            batch_size: Number of frames to process together
            color_correction: Color correction method
            input_noise_scale: Input noise injection scale [0.0-1.0]
            offload_device: Device to offload intermediate tensors
            enable_debug: Enable upstream debug logging

        Returns:
            Upscaled images tensor (N, H', W', C) in [0, 1] range
        """
        if enable_debug:
            Logger.log(
                f"[debug] Invoking SeedVR2VideoUpscaler: batch_size={batch_size}, resolution={resolution}, "
                f"input_noise_scale={input_noise_scale:.2f}, offload_device={offload_device}"
            )

        result = SeedVR2Adapter.get_upscaler_class().execute(
            image=images,
            dit=dit_config,
            vae=vae_config,
            seed=seed,
            resolution=resolution,
            batch_size=batch_size,
            color_correction=color_correction,
            input_noise_scale=input_noise_scale,
            offload_device=offload_device,
            enable_debug=enable_debug,
        )

        if hasattr(result, "values"):
            return result.values[0] if isinstance(result.values, (list, tuple)) else result.values
        elif hasattr(result, "__getitem__"):
            return result[0]

        return result
