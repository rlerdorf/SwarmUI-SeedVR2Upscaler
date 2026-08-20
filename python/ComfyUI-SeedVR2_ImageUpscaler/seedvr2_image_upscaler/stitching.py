"""Stitching algorithms for blending upscaled tiles."""

from __future__ import annotations

import time

import torch
import numpy as np
from PIL import Image
from scipy import ndimage
from collections import defaultdict
from typing import Any
from .logger import Logger

try:
    import cv2
    HAS_OPENCV = True
except ImportError:
    HAS_OPENCV = False
    print("Warning: OpenCV not available. Bilateral filtering will use Gaussian approximation.")

from .image_utils import ImageUtils
from .seedvr2_adapter import SeedVR2Adapter


class StitchingPipeline:
    @staticmethod
    def _align_resolution(resolution: int, min_resolution: int = 256, max_resolution: int = 4096, step: int = 32) -> int:
        """Clamp and align a resolution value to model-friendly multiples."""
        resolution = max(min_resolution, min(max_resolution, int(resolution)))
        return int(round(resolution / step) * step)

    @staticmethod
    def _resolve_group_upscale_resolution(
        tile_size: tuple[int, int],
        upscale_factor: float,
        fallback_resolution: int,
    ) -> int:
        """Derive target resolution from tile dimensions for scale-consistent group processing."""
        if upscale_factor <= 0:
            return StitchingPipeline._align_resolution(fallback_resolution)
        target = int(round(min(tile_size) * upscale_factor))
        if target <= 0:
            target = int(fallback_resolution)
        return StitchingPipeline._align_resolution(target)

    @staticmethod
    def _edge_distance_map(height: int, width: int) -> np.ndarray:
        """Distance from each pixel to the closest tile edge."""
        if height <= 0 or width <= 0:
            return np.zeros((0, 0), dtype=np.float32)
        x = np.arange(width, dtype=np.float32)
        y = np.arange(height, dtype=np.float32)
        dist_x = np.minimum(x, width - 1 - x)
        dist_y = np.minimum(y, height - 1 - y)
        return np.minimum(dist_y[:, np.newaxis], dist_x[np.newaxis, :]).astype(np.float32)

    @staticmethod
    def _edge_weight_map(height: int, width: int, min_weight: float = 0.1) -> np.ndarray:
        """Edge-distance weights for blending."""
        if height <= 0 or width <= 0:
            return np.zeros((0, 0), dtype=np.float32)
        dist = StitchingPipeline._edge_distance_map(height, width)
        denom = float(max(height, width))
        if denom <= 0:
            return np.full((height, width), min_weight, dtype=np.float32)
        return np.maximum(min_weight, dist / denom).astype(np.float32)

    @staticmethod
    def _blend_weighted_region(
        output_region: np.ndarray,
        weight_region: np.ndarray,
        tile_region: np.ndarray,
        tile_weight: np.ndarray,
    ) -> tuple[np.ndarray, np.ndarray]:
        """Blend a tile region into output using per-pixel weights."""
        new_weight = weight_region + tile_weight
        safe_weight = np.maximum(new_weight, 1e-8)
        blended = (
            output_region * weight_region[:, :, np.newaxis]
            + tile_region * tile_weight[:, :, np.newaxis]
        ) / safe_weight[:, :, np.newaxis]

        empty_mask = weight_region <= 0
        if np.any(empty_mask):
            blended[empty_mask] = tile_region[empty_mask]

        return blended, new_weight

    @staticmethod
    def _get_optimal_batch_size(num_tiles: int) -> int:
        """Calculate optimal batch size following 4n+1 pattern (1, 5, 9, 13, 17, 21...)"""
        if num_tiles <= 1:
            return 1
        # Find largest 4n+1 that doesn't exceed num_tiles
        n = (num_tiles - 1) // 4
        return 4 * n + 1


    @staticmethod
    def _create_base_image(
        original_image: Image.Image,
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> Image.Image:
        """Create base image for stitching by upscaling the original at low resolution."""
        base_resolution = min(512, tile_upscale_resolution // 2)
        Logger.log(f"Creating base image: resolution={base_resolution}, offload_device={offload_device}")

        base_tensor = ImageUtils.pil_to_tensor(original_image)
        base_upscaled = SeedVR2Adapter.execute_seedvr2(
            images=base_tensor,
            dit_config=dit_config,
            vae_config=vae_config,
            seed=seed,
            resolution=base_resolution,
            batch_size=1,
            color_correction=color_correction,
            input_noise_scale=input_noise_scale,
            offload_device=offload_device,
            enable_debug=enable_debug,
        )
        base_pil = ImageUtils.tensor_to_pil(base_upscaled)
        return base_pil.resize((width, height), Image.LANCZOS)


    @staticmethod
    def _batch_upscale_tiles(
        tiles: list[dict[str, object]],
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float | None = None,
        progress: Any | None = None,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> list[Image.Image]:
        """Batch process tiles by grouping them by size for optimal performance.

        Per-tile color correction is intentionally disabled here (see the call to
        execute_seedvr2 below): each tile-batch would otherwise be color-matched
        independently against only its own local crop, so tiles with different
        content (e.g. skin vs. background) drift to different color statistics and
        show up as a visible seam once stitched. Color correction against a single,
        globally-consistent reference is applied once at the end of
        process_and_stitch instead.
        """
        # Group tiles by their dimensions
        tiles_by_size = defaultdict(list)
        for idx, tile_info in enumerate(tiles):
            tile_size = (tile_info["tile"].width, tile_info["tile"].height)
            tiles_by_size[tile_size].append((idx, tile_info))

        # Process each size group with optimal batch sizes
        upscaled_tiles = [None] * len(tiles)
        tiles_processed_count = 0

        for tile_size, tile_group in tiles_by_size.items():
            num_tiles_in_group = len(tile_group)
            group_resolution = tile_upscale_resolution
            if upscale_factor is not None:
                group_resolution = StitchingPipeline._resolve_group_upscale_resolution(
                    tile_size,
                    float(upscale_factor),
                    int(tile_upscale_resolution),
                )
            processed_tiles = 0
            Logger.log(
                f"Tile group {tile_size[0]}x{tile_size[1]}, count={num_tiles_in_group}, "
                f"resolution={group_resolution}"
            )
            group_indices: list[int] = []
            group_tensor_batches: list[torch.Tensor] = []

            # Process this size group in optimal sub-batches
            while processed_tiles < num_tiles_in_group:
                remaining = num_tiles_in_group - processed_tiles
                batch_size = StitchingPipeline._get_optimal_batch_size(remaining)
                Logger.log(f"Upscaling sub-batch: size={batch_size}, remaining={remaining}")

                # Get tiles for this sub-batch
                sub_batch = tile_group[processed_tiles:processed_tiles + batch_size]

                # Collect tensors for this sub-batch
                tile_tensors = [ImageUtils.pil_to_tensor(tile_info["tile"]) for _, tile_info in sub_batch]
                batch_tensor = torch.cat(tile_tensors, dim=0)

                # Update progress before processing
                if progress:
                    progress.update_sub_progress(f"AI Upscaling ({tiles_processed_count + 1}/{len(tiles)})", 1)

                # Process this sub-batch
                upscaled_batch = SeedVR2Adapter.execute_seedvr2(
                    images=batch_tensor,
                    dit_config=dit_config,
                    vae_config=vae_config,
                    seed=seed,
                    resolution=group_resolution,
                    batch_size=batch_size,
                    color_correction=None,
                    input_noise_scale=input_noise_scale,
                    offload_device=offload_device,
                    enable_debug=enable_debug,
                )

                # Keep tensors and convert once per size-group.
                group_tensor_batches.append(upscaled_batch.detach().cpu())
                for original_idx, _ in sub_batch:
                    group_indices.append(original_idx)
                    tiles_processed_count += 1
                    # Update progress after each tile
                    if progress:
                        progress.update_sub_progress(f"AI Upscaling ({tiles_processed_count}/{len(tiles)})", 1)

                processed_tiles += batch_size

            if group_tensor_batches:
                group_tensor = torch.cat(group_tensor_batches, dim=0)
                group_uint8 = group_tensor.mul(255).clamp(0, 255).byte().numpy()
                for group_idx, original_idx in enumerate(group_indices):
                    upscaled_tiles[original_idx] = Image.fromarray(group_uint8[group_idx], "RGB")

        return upscaled_tiles


    @staticmethod
    def _prepare_tile_for_stitching(
        tile_info: dict[str, object],
        ai_upscaled_tile: Image.Image,
        upscale_factor: float,
    ) -> dict[str, object]:
        """Prepare an upscaled tile for stitching by resizing, positioning, and cropping.

        This function handles both regular overlap padding (for blending adjacent tiles)
        and memory padding (added for GPU efficiency). Memory padding must be completely
        removed as it contains reflected/extended content that shouldn't be in the output.
        """
        # Get the original tile size (before memory padding was added)
        original_width, original_height = tile_info["original_tile_size"]

        # The AI upscaled the full tile (including memory padding)
        # We need to first crop out the memory padding, then handle overlap padding

        # Get memory padding info
        mem_left_pad, mem_top_pad, mem_right_pad, mem_bottom_pad = tile_info.get("memory_padding", (0, 0, 0, 0))

        # Calculate the upscaled dimensions
        # The upscaled tile corresponds to the full padded tile (with memory padding)
        full_tile_width = tile_info["tile"].width
        full_tile_height = tile_info["tile"].height

        # Resize the upscaled tile to match the expected output size for the full tile
        target_full_width = int(full_tile_width * upscale_factor)
        target_full_height = int(full_tile_height * upscale_factor)
        if ai_upscaled_tile.size == (target_full_width, target_full_height):
            resized_tile = ai_upscaled_tile
        else:
            resized_tile = ai_upscaled_tile.resize((target_full_width, target_full_height), Image.LANCZOS)

        # First, crop out the memory padding (scale the memory padding amounts)
        scaled_mem_right = int(mem_right_pad * upscale_factor)
        scaled_mem_bottom = int(mem_bottom_pad * upscale_factor)

        # The original content (without memory padding) is in the top-left portion
        original_content_width = int(original_width * upscale_factor)
        original_content_height = int(original_height * upscale_factor)

        # Crop to remove memory padding - keep only the original content area
        if scaled_mem_right > 0 or scaled_mem_bottom > 0:
            resized_tile = resized_tile.crop((0, 0, original_content_width, original_content_height))

        # Now handle the regular overlap padding for blending
        # Calculate positioning
        paste_x = int(tile_info["position"][0] * upscale_factor)
        paste_y = int(tile_info["position"][1] * upscale_factor)
        final_tile_width = int(tile_info["actual_size"][0] * upscale_factor)
        final_tile_height = int(tile_info["actual_size"][1] * upscale_factor)

        # Calculate scaled overlap padding
        left_pad, top_pad, right_pad, bottom_pad = tile_info["padding"]

        scaled_left_pad = int(left_pad * upscale_factor)
        scaled_top_pad = int(top_pad * upscale_factor)
        scaled_right_pad = int(right_pad * upscale_factor)
        scaled_bottom_pad = int(bottom_pad * upscale_factor)

        # Keep half the padding on ALL sides to create overlap for blending
        keep_left = scaled_left_pad // 2 if left_pad > 0 else 0
        keep_top = scaled_top_pad // 2 if top_pad > 0 else 0
        keep_right = scaled_right_pad // 2 if right_pad > 0 else 0
        keep_bottom = scaled_bottom_pad // 2 if bottom_pad > 0 else 0

        # Crop the tile - keep partial overlap padding on all sides for blending
        crop_box = (
            scaled_left_pad - keep_left,
            scaled_top_pad - keep_top,
            min(scaled_left_pad + final_tile_width + keep_right, resized_tile.width),
            min(scaled_top_pad + final_tile_height + keep_bottom, resized_tile.height)
        )
        cropped_tile = resized_tile.crop(crop_box)

        # Adjust paste position to account for kept left/top padding
        paste_x_adjusted = max(0, paste_x - keep_left)
        paste_y_adjusted = max(0, paste_y - keep_top)

        return {
            "cropped_tile": cropped_tile,
            "paste_x": paste_x_adjusted,
            "paste_y": paste_y_adjusted,
            "keep_padding": (keep_left, keep_top, keep_right, keep_bottom),
        }


    @staticmethod
    def _build_laplacian_pyramid(image: np.ndarray, levels: int = 4) -> list[np.ndarray]:
        """Build a Laplacian pyramid for multi-band blending.

        Args:
            image: numpy array (H, W, C)
            levels: number of pyramid levels

        Returns:
            List of Laplacian pyramid levels (finest to coarsest)
        """
        gaussian_pyramid = [image.astype(np.float32)]

        # Build Gaussian pyramid
        for i in range(levels):
            down = ndimage.zoom(gaussian_pyramid[-1], (0.5, 0.5, 1), order=1)
            gaussian_pyramid.append(down)

        # Build Laplacian pyramid
        laplacian_pyramid = []
        for i in range(levels):
            # Upscale the next level
            size = gaussian_pyramid[i].shape
            upscaled = ndimage.zoom(gaussian_pyramid[i + 1],
                                (size[0] / gaussian_pyramid[i + 1].shape[0],
                                    size[1] / gaussian_pyramid[i + 1].shape[1],
                                    1), order=1)
            # Laplacian = Gaussian - upscaled(next_gaussian)
            laplacian = gaussian_pyramid[i] - upscaled
            laplacian_pyramid.append(laplacian)

        # Add the smallest Gaussian as the last level
        laplacian_pyramid.append(gaussian_pyramid[-1])

        return laplacian_pyramid


    @staticmethod
    def _collapse_laplacian_pyramid(laplacian_pyramid: list[np.ndarray]) -> np.ndarray:
        """Collapse a Laplacian pyramid back to an image.

        Args:
            laplacian_pyramid: List of Laplacian levels (finest to coarsest)

        Returns:
            Reconstructed image as numpy array
        """
        # Start with the coarsest level
        image = laplacian_pyramid[-1]

        # Reconstruct from coarse to fine
        for i in range(len(laplacian_pyramid) - 2, -1, -1):
            # Upscale current image to match next level size
            size = laplacian_pyramid[i].shape
            upscaled = ndimage.zoom(image,
                                (size[0] / image.shape[0],
                                    size[1] / image.shape[1],
                                    1), order=1)
            # Add the Laplacian details
            image = upscaled + laplacian_pyramid[i]

        return image


    @staticmethod
    def _apply_bilateral_filter(
        image: Image.Image | np.ndarray,
        d: int = 9,
        sigma_color: float = 75,
        sigma_space: float = 75,
    ) -> np.ndarray:
        """Apply bilateral filtering for edge-preserving smoothing.

        Args:
            image: PIL Image or numpy array
            d: Diameter of pixel neighborhood
            sigma_color: Filter sigma in color space
            sigma_space: Filter sigma in coordinate space

        Returns:
            Filtered image as numpy array
        """
        if isinstance(image, Image.Image):
            img_array = np.array(image)
        else:
            img_array = image

        if HAS_OPENCV:
            # Use OpenCV's optimized bilateral filter
            filtered = cv2.bilateralFilter(img_array, d, sigma_color, sigma_space)
        else:
            # Fallback: Use Gaussian approximation
            filtered = img_array.astype(np.float32)

            # Apply edge-aware smoothing using gradients
            for channel in range(3):
                channel_data = filtered[:, :, channel]

                # Detect edges
                sobel_x = ndimage.sobel(channel_data, axis=1)
                sobel_y = ndimage.sobel(channel_data, axis=0)
                edge_magnitude = np.sqrt(sobel_x**2 + sobel_y**2)

                # Normalize and invert (smooth where no edges)
                if edge_magnitude.max() > 0:
                    edge_magnitude = edge_magnitude / edge_magnitude.max()
                smoothing_weight = 1.0 - edge_magnitude

                # Apply adaptive Gaussian smoothing
                smoothed = ndimage.gaussian_filter(channel_data, sigma=sigma_space / 10.0)
                filtered[:, :, channel] = channel_data * edge_magnitude + smoothed * smoothing_weight

            filtered = np.clip(filtered, 0, 255).astype(np.uint8)

        return filtered


    @staticmethod
    def _compute_structure_tensor(image: np.ndarray, sigma: float = 1.5) -> tuple[np.ndarray, np.ndarray]:
        """Compute structure tensor for content-aware blending.

        Args:
            image: numpy array (H, W, C)
            sigma: Gaussian smoothing sigma for structure tensor

        Returns:
            edge_strength: Edge strength map (H, W)
            coherence: Local structure coherence (H, W)
        """
        # Convert to grayscale for structure analysis
        if len(image.shape) == 3:
            gray = np.mean(image, axis=2)
        else:
            gray = image

        # Compute gradients
        Ix = ndimage.sobel(gray, axis=1)
        Iy = ndimage.sobel(gray, axis=0)

        # Compute structure tensor components
        Ixx = ndimage.gaussian_filter(Ix * Ix, sigma)
        Iyy = ndimage.gaussian_filter(Iy * Iy, sigma)
        Ixy = ndimage.gaussian_filter(Ix * Iy, sigma)

        # Compute eigenvalues for edge strength and coherence
        trace = Ixx + Iyy
        det = Ixx * Iyy - Ixy * Ixy

        # Eigenvalues: lambda = (trace ± sqrt(trace^2 - 4*det)) / 2
        discriminant = np.maximum(trace * trace - 4 * det, 0)
        lambda1 = (trace + np.sqrt(discriminant)) / 2
        lambda2 = (trace - np.sqrt(discriminant)) / 2

        # Edge strength (larger eigenvalue)
        edge_strength = lambda1

        # Coherence (anisotropy measure)
        coherence = np.zeros_like(trace)
        mask = lambda1 > 1e-5
        coherence[mask] = (lambda1[mask] - lambda2[mask]) / (lambda1[mask] + lambda2[mask])

        # Normalize
        if edge_strength.max() > 0:
            edge_strength = edge_strength / edge_strength.max()
        coherence = np.clip(coherence, 0, 1)

        return edge_strength, coherence


    @staticmethod
    def process_and_stitch(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        mask_blur: int,
        progress,
        original_image: Image.Image,
        anti_aliasing_strength: float = 0.0,
        blending_method: str = "auto",
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> Image.Image:
        """Main stitching function that chooses the appropriate method based on settings.

        Args:
            tiles: List of tile info dictionaries
            width: Output image width
            height: Output image height
            dit_config: DiT model configuration
            vae_config: VAE model configuration
            seed: Random seed
            tile_upscale_resolution: Resolution for upscaling tiles
            upscale_factor: Scale factor for output
            mask_blur: Blur radius for mask blending
            progress: Progress tracker
            original_image: Original input image
            anti_aliasing_strength: Anti-aliasing strength (0-1)
            blending_method: Blending method to use
            color_correction: Color correction method for SeedVR2
            input_noise_scale: Input noise injection scale [0.0-1.0]
            offload_device: Device to offload intermediate tensors
            enable_debug: Enable extension-specific debug logs

        Returns:
            Stitched output image
        """
        stitch_start = time.perf_counter()

        # Auto mode: choose based on mask_blur value
        if blending_method == "auto":
            if mask_blur == 0:
                blending_method = "simple"
            elif mask_blur <= 2:
                blending_method = "linear"
            else:
                blending_method = "linear"

        print(f"[SeedVR2 Tiling] Using {blending_method} blending method...", flush=True)
        Logger.log(
            f"Stitching setup: tiles={len(tiles)}, output={width}x{height}, "
            f"tile_upscale_resolution={tile_upscale_resolution}, input_noise_scale={input_noise_scale:.4f}, "
            f"offload_device={offload_device}"
        )

        # Common kwargs for all blending methods
        kwargs = {
            "tiles": tiles,
            "width": width,
            "height": height,
            "dit_config": dit_config,
            "vae_config": vae_config,
            "seed": seed,
            "tile_upscale_resolution": tile_upscale_resolution,
            "upscale_factor": upscale_factor,
            "progress": progress,
            "original_image": original_image,
            "color_correction": color_correction,
            "input_noise_scale": input_noise_scale,
            "offload_device": offload_device,
            "enable_debug": enable_debug,
        }

        # Route to appropriate blending function
        if blending_method == "multiband":
            result = StitchingPipeline._process_and_stitch_multiband(**kwargs)
        elif blending_method == "bilateral":
            result = StitchingPipeline._process_and_stitch_bilateral(**kwargs, mask_blur=mask_blur)
        elif blending_method == "content_aware":
            result = StitchingPipeline._process_and_stitch_content_aware(**kwargs, mask_blur=mask_blur)
        elif blending_method == "simple":
            result = StitchingPipeline._process_and_stitch_zero_blur(**kwargs)
        else:  # "linear" or default
            result = StitchingPipeline._process_and_stitch_blended(**kwargs, mask_blur=mask_blur)

        # Harmonize color across tile seams. Tiles are upscaled with per-tile
        # color correction disabled (see _batch_upscale_tiles) because matching
        # each tile against only its own local crop makes tiles with different
        # content (e.g. skin vs. background) drift to different color statistics,
        # producing a visible seam once stitched. Apply the requested correction
        # once instead, globally, against a single low-resolution reference
        # upscale of the whole image.
        if color_correction != "none":
            reference = StitchingPipeline._create_base_image(
                original_image,
                width,
                height,
                dit_config,
                vae_config,
                seed,
                tile_upscale_resolution,
                color_correction,
                input_noise_scale,
                offload_device,
                enable_debug,
            )
            result = StitchingPipeline._match_color_lab(result, reference)

        # Apply anti-aliasing if requested
        if anti_aliasing_strength > 0:
            result = StitchingPipeline._apply_edge_aware_antialiasing(result, anti_aliasing_strength)

        Logger.log(f"Stitching complete in {time.perf_counter() - stitch_start:.2f}s")

        return result


    @staticmethod
    def _apply_edge_aware_antialiasing(image: Image.Image, strength: float) -> Image.Image:
        """Apply edge-aware anti-aliasing using Sobel edge detection."""
        img_array = np.array(image, dtype=np.float32)
        smoothed = np.zeros_like(img_array)

        for channel in range(3):
            channel_data = img_array[:, :, channel]

            # Apply Sobel filters to detect edges
            sobel_x = ndimage.sobel(channel_data, axis=1)
            sobel_y = ndimage.sobel(channel_data, axis=0)

            # Calculate edge magnitude
            edge_magnitude = np.sqrt(sobel_x**2 + sobel_y**2)

            # Normalize edge magnitude to 0-1
            if edge_magnitude.max() > 0:
                edge_magnitude = edge_magnitude / edge_magnitude.max()

            # Create inverse edge map
            smoothing_mask = 1.0 - edge_magnitude
            smoothing_mask = 1.0 - (smoothing_mask * strength)

            # Apply Gaussian smoothing
            sigma = 0.5 + (strength * 1.5)
            smoothed_channel = ndimage.gaussian_filter(channel_data, sigma=sigma)

            # Selective blend
            smoothed[:, :, channel] = channel_data * smoothing_mask + smoothed_channel * (1.0 - smoothing_mask)

        smoothed = np.clip(smoothed, 0, 255).astype(np.uint8)
        return Image.fromarray(smoothed)


    @staticmethod
    def _match_color_lab(image: Image.Image, reference: Image.Image) -> Image.Image:
        """Match image's color statistics to reference in LAB space (Reinhard et al. 2001).

        Used as a single global color-harmonization pass after stitching, so every
        tile ends up graded against the same reference instead of each tile's own
        (independently drifting) local crop.
        """
        if not HAS_OPENCV:
            return image

        src = np.array(image.convert("RGB"), dtype=np.float32) / 255.0
        ref = reference.convert("RGB")
        if ref.size != image.size:
            ref = ref.resize(image.size, Image.LANCZOS)
        ref_arr = np.array(ref, dtype=np.float32) / 255.0

        src_lab = cv2.cvtColor(src, cv2.COLOR_RGB2LAB)
        ref_lab = cv2.cvtColor(ref_arr, cv2.COLOR_RGB2LAB)

        matched_lab = np.empty_like(src_lab)
        for channel in range(3):
            src_channel = src_lab[:, :, channel]
            ref_channel = ref_lab[:, :, channel]
            src_std = max(float(src_channel.std()), 1e-6)
            matched_lab[:, :, channel] = (
                (src_channel - src_channel.mean()) * (ref_channel.std() / src_std) + ref_channel.mean()
            )

        matched_rgb = cv2.cvtColor(matched_lab, cv2.COLOR_LAB2RGB)
        matched_rgb = np.clip(matched_rgb * 255.0, 0, 255).astype(np.uint8)
        return Image.fromarray(matched_rgb)


    @staticmethod
    def _process_and_stitch_multiband(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        progress,
        original_image: Image.Image,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> Image.Image:
        """Multi-band blending using Laplacian pyramids for frequency-separated stitching."""
        # Create base image
        base_image = StitchingPipeline._create_base_image(
            original_image,
            width,
            height,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Batch process and upscale tiles
        upscaled_tiles = StitchingPipeline._batch_upscale_tiles(
            tiles,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            upscale_factor,
            progress,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Build Laplacian pyramid for base image
        base_array = np.array(base_image, dtype=np.float32)
        pyramid_levels = 4
        output_pyramid = StitchingPipeline._build_laplacian_pyramid(base_array, pyramid_levels)

        # Process each tile and blend into pyramid
        for tile_idx, tile_info in enumerate(tiles):
            ai_upscaled_tile = upscaled_tiles[tile_idx]

            progress.update_sub_progress("Resizing & Positioning", 2)
            prepared = StitchingPipeline._prepare_tile_for_stitching(tile_info, ai_upscaled_tile, upscale_factor)
            cropped_tile = prepared["cropped_tile"]
            paste_x_adjusted = prepared["paste_x"]
            paste_y_adjusted = prepared["paste_y"]

            progress.update_sub_progress("Multi-band Blending", 3)

            # Build Laplacian pyramid for this tile
            tile_array = np.array(cropped_tile, dtype=np.float32)

            # Create full-size tile array
            full_tile_array = np.zeros((height, width, 3), dtype=np.float32)
            end_x = min(paste_x_adjusted + tile_array.shape[1], width)
            end_y = min(paste_y_adjusted + tile_array.shape[0], height)

            tile_height = end_y - paste_y_adjusted
            tile_width = end_x - paste_x_adjusted
            full_tile_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = tile_array[:tile_height, :tile_width]

            # Build pyramid for this tile
            tile_pyramid = StitchingPipeline._build_laplacian_pyramid(full_tile_array, pyramid_levels)

            # Create blending mask
            mask = np.zeros((height, width), dtype=np.float32)
            mask[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = 1.0

            # Apply feathering to mask based on overlap
            feather_size = min(32, tile_width // 4, tile_height // 4)
            if feather_size > 0:
                local_mask = np.minimum(
                    1.0,
                    StitchingPipeline._edge_distance_map(tile_height, tile_width) / float(feather_size),
                )
                mask[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = local_mask

            # Build pyramid for mask
            mask_pyramid = []
            current_mask = mask
            for i in range(pyramid_levels + 1):
                mask_pyramid.append(current_mask)
                if i < pyramid_levels:
                    current_mask = ndimage.zoom(current_mask, 0.5, order=1)

            # Blend each level of the pyramid
            blended_pyramid = []
            for level in range(len(tile_pyramid)):
                mask_level = mask_pyramid[level]
                level_height, level_width = output_pyramid[level].shape[:2]
                if mask_level.shape[0] != level_height or mask_level.shape[1] != level_width:
                    mask_level = ndimage.zoom(mask_level,
                                            (level_height / mask_level.shape[0],
                                            level_width / mask_level.shape[1]), order=1)

                mask_level = mask_level[:, :, np.newaxis]
                blended = output_pyramid[level] * (1 - mask_level) + tile_pyramid[level] * mask_level
                blended_pyramid.append(blended)

            output_pyramid = blended_pyramid
            progress.update()

        # Collapse pyramid to final image
        output_array = StitchingPipeline._collapse_laplacian_pyramid(output_pyramid)
        output_array = np.clip(output_array, 0, 255).astype(np.uint8)
        return Image.fromarray(output_array)


    @staticmethod
    def _process_and_stitch_bilateral(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        mask_blur: int,
        progress,
        original_image: Image.Image,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> Image.Image:
        """Bilateral filtering-based stitching for edge-preserving blending."""
        # Create base image
        base_image = StitchingPipeline._create_base_image(
            original_image,
            width,
            height,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        output_image = base_image.copy()
        output_array = np.array(output_image, dtype=np.float32)
        weight_array = np.zeros((height, width), dtype=np.float32)

        # Batch process and upscale tiles
        upscaled_tiles = StitchingPipeline._batch_upscale_tiles(
            tiles,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            upscale_factor,
            progress,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Process each tile
        for tile_idx, tile_info in enumerate(tiles):
            ai_upscaled_tile = upscaled_tiles[tile_idx]

            progress.update_sub_progress("Resizing & Positioning", 2)
            prepared = StitchingPipeline._prepare_tile_for_stitching(tile_info, ai_upscaled_tile, upscale_factor)
            cropped_tile = prepared["cropped_tile"]
            paste_x_adjusted = prepared["paste_x"]
            paste_y_adjusted = prepared["paste_y"]

            progress.update_sub_progress("Bilateral Filtering", 3)

            # Apply bilateral filter to tile
            tile_array = StitchingPipeline._apply_bilateral_filter(cropped_tile, d=9, sigma_color=75, sigma_space=75)
            tile_array = tile_array.astype(np.float32)

            # Define region
            end_x = min(paste_x_adjusted + tile_array.shape[1], width)
            end_y = min(paste_y_adjusted + tile_array.shape[0], height)
            tile_height = end_y - paste_y_adjusted
            tile_width = end_x - paste_x_adjusted
            if tile_height <= 0 or tile_width <= 0:
                progress.update()
                continue

            output_region = output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            weight_region = weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            tile_region = tile_array[:tile_height, :tile_width]
            tile_weight = StitchingPipeline._edge_weight_map(tile_height, tile_width, min_weight=0.1)

            blended_region, new_weight = StitchingPipeline._blend_weighted_region(
                output_region,
                weight_region,
                tile_region,
                tile_weight,
            )
            output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = blended_region
            weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = new_weight

            progress.update()

        output_array = np.clip(output_array, 0, 255).astype(np.uint8)
        return Image.fromarray(output_array)


    @staticmethod
    def _process_and_stitch_content_aware(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        mask_blur: int,
        progress,
        original_image: Image.Image,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
    ) -> Image.Image:
        """Content-aware stitching using structure tensor for adaptive blending."""
        # Create base image
        base_image = StitchingPipeline._create_base_image(
            original_image,
            width,
            height,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        output_array = np.array(base_image, dtype=np.float32)
        weight_array = np.zeros((height, width), dtype=np.float32)

        # Compute global structure for base image
        base_edge_strength, base_coherence = StitchingPipeline._compute_structure_tensor(output_array)

        # Batch process and upscale tiles
        upscaled_tiles = StitchingPipeline._batch_upscale_tiles(
            tiles,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            upscale_factor,
            progress,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Process each tile
        for tile_idx, tile_info in enumerate(tiles):
            ai_upscaled_tile = upscaled_tiles[tile_idx]

            progress.update_sub_progress("Resizing & Positioning", 2)
            prepared = StitchingPipeline._prepare_tile_for_stitching(tile_info, ai_upscaled_tile, upscale_factor)
            cropped_tile = prepared["cropped_tile"]
            paste_x_adjusted = prepared["paste_x"]
            paste_y_adjusted = prepared["paste_y"]

            progress.update_sub_progress("Content-Aware Blending", 3)

            tile_array = np.array(cropped_tile, dtype=np.float32)
            tile_edge_strength, _tile_coherence = StitchingPipeline._compute_structure_tensor(tile_array)

            end_x = min(paste_x_adjusted + tile_array.shape[1], width)
            end_y = min(paste_y_adjusted + tile_array.shape[0], height)
            tile_height = end_y - paste_y_adjusted
            tile_width = end_x - paste_x_adjusted
            if tile_height <= 0 or tile_width <= 0:
                progress.update()
                continue

            output_region = output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            weight_region = weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            tile_region = tile_array[:tile_height, :tile_width]

            local_edge = base_edge_strength[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            local_coherence = base_coherence[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            tile_edge = tile_edge_strength[:tile_height, :tile_width]

            tile_weight = 1.0 - local_coherence * 0.5
            tile_weight = tile_weight * (1.0 + np.maximum(tile_edge - local_edge, 0.0))
            tile_weight = tile_weight * StitchingPipeline._edge_weight_map(
                tile_height,
                tile_width,
                min_weight=0.1,
            )
            tile_weight = tile_weight.astype(np.float32, copy=False)

            blended_region, new_weight = StitchingPipeline._blend_weighted_region(
                output_region,
                weight_region,
                tile_region,
                tile_weight,
            )
            output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = blended_region
            weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = new_weight

            progress.update()

        output_array = np.clip(output_array, 0, 255).astype(np.uint8)
        return Image.fromarray(output_array)


    @staticmethod
    def _process_and_stitch_zero_blur(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        progress,
        original_image: Image.Image,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
        ) -> Image.Image:
        """Zero-blur stitching that preserves maximum detail through precise pixel averaging."""
        # Create base image
        base_image = StitchingPipeline._create_base_image(
            original_image,
            width,
            height,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        output_array = np.array(base_image, dtype=np.float32)
        weight_array = np.zeros((height, width), dtype=np.float32)

        # Batch process and upscale tiles
        upscaled_tiles = StitchingPipeline._batch_upscale_tiles(
            tiles,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            upscale_factor,
            progress,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Process each upscaled tile for stitching
        for tile_idx, tile_info in enumerate(tiles):
            ai_upscaled_tile = upscaled_tiles[tile_idx]

            progress.update_sub_progress("Resizing & Positioning", 2)
            prepared = StitchingPipeline._prepare_tile_for_stitching(tile_info, ai_upscaled_tile, upscale_factor)
            cropped_tile = prepared["cropped_tile"]
            paste_x_adjusted = prepared["paste_x"]
            paste_y_adjusted = prepared["paste_y"]

            tile_array = np.array(cropped_tile, dtype=np.float32)

            progress.update_sub_progress("Seamless Blending", 3)

            end_x = min(paste_x_adjusted + tile_array.shape[1], width)
            end_y = min(paste_y_adjusted + tile_array.shape[0], height)

            tile_height = end_y - paste_y_adjusted
            tile_width = end_x - paste_x_adjusted
            if tile_height <= 0 or tile_width <= 0:
                progress.update()
                continue

            output_region = output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            weight_region = weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x]
            tile_region = tile_array[:tile_height, :tile_width]

            new_weight = weight_region + 1.0
            output_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = (
                output_region * weight_region[:, :, np.newaxis] + tile_region
            ) / new_weight[:, :, np.newaxis]
            weight_array[paste_y_adjusted:end_y, paste_x_adjusted:end_x] = new_weight

            progress.update()

        output_array = np.clip(output_array, 0, 255).astype(np.uint8)
        return Image.fromarray(output_array)


    @staticmethod
    def _process_and_stitch_blended(
        tiles: list[dict[str, object]],
        width: int,
        height: int,
        dit_config: dict[str, Any],
        vae_config: dict[str, Any],
        seed: int,
        tile_upscale_resolution: int,
        upscale_factor: float,
        mask_blur: int,
        progress,
        original_image: Image.Image,
        color_correction: str = "lab",
        input_noise_scale: float = 0.0,
        offload_device: str = "cpu",
        enable_debug: bool = False,
        ) -> Image.Image:
        """Standard blended stitching with user-controlled blur."""
        # Create base image
        base_image = StitchingPipeline._create_base_image(
            original_image,
            width,
            height,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        output_rgba = base_image.convert("RGBA")
        mask_cache: dict[tuple[object, ...], Image.Image] = {}
        use_dest_alpha_composite = True

        # Batch process and upscale tiles
        upscaled_tiles = StitchingPipeline._batch_upscale_tiles(
            tiles,
            dit_config,
            vae_config,
            seed,
            tile_upscale_resolution,
            upscale_factor,
            progress,
            color_correction,
            input_noise_scale,
            offload_device,
            enable_debug,
        )

        # Process each upscaled tile for stitching
        for tile_idx, tile_info in enumerate(tiles):
            ai_upscaled_tile = upscaled_tiles[tile_idx]

            progress.update_sub_progress("Resizing & Positioning", 2)
            prepared = StitchingPipeline._prepare_tile_for_stitching(tile_info, ai_upscaled_tile, upscale_factor)
            cropped_tile = prepared["cropped_tile"]
            paste_x_adjusted = prepared["paste_x"]
            paste_y_adjusted = prepared["paste_y"]
            keep_left, keep_top, keep_right, keep_bottom = prepared["keep_padding"]

            progress.update_sub_progress("Mask Blending", 3)

            # Create mask with user-specified blur
            actual_crop_width = cropped_tile.width
            actual_crop_height = cropped_tile.height
            padding_info = tuple(int(v) for v in tile_info["padding"])
            keep_padding = (keep_left, keep_top, keep_right, keep_bottom)
            mask_key = (
                actual_crop_width,
                actual_crop_height,
                int(mask_blur),
                padding_info,
                keep_padding,
            )

            tile_mask = mask_cache.get(mask_key)
            if tile_mask is None:
                tile_mask = StitchingPipeline._create_precise_tile_mask(
                    actual_crop_width,
                    actual_crop_height,
                    mask_blur,
                    padding_info,
                    keep_left,
                    keep_top,
                    keep_right,
                    keep_bottom,
                )
                mask_cache[mask_key] = tile_mask

            tile_rgba = cropped_tile.convert("RGBA")
            tile_rgba.putalpha(tile_mask)

            if use_dest_alpha_composite:
                try:
                    output_rgba.alpha_composite(
                        tile_rgba,
                        dest=(paste_x_adjusted, paste_y_adjusted),
                    )
                except TypeError:
                    use_dest_alpha_composite = False

            if not use_dest_alpha_composite:
                tile_canvas = Image.new("RGBA", output_rgba.size, (0, 0, 0, 0))
                tile_canvas.paste(tile_rgba, (paste_x_adjusted, paste_y_adjusted))
                output_rgba.alpha_composite(tile_canvas)

            progress.update()

        return output_rgba.convert("RGB")


    @staticmethod
    def _create_precise_tile_mask(
        width: int,
        height: int,
        blur_radius: int,
        padding_info: tuple[int, int, int, int],
        keep_left: int = 0,
        keep_top: int = 0,
        keep_right: int = 0,
        keep_bottom: int = 0,
    ) -> Image.Image:
        """Create smart blending mask with proper overlap handling on all sides."""
        left_pad, top_pad, right_pad, bottom_pad = padding_info
        mask_array = np.full((height, width), 255, dtype=np.uint8)

        if blur_radius > 0 and width > 0 and height > 0:
            overlap_width_left = keep_left * 2 if keep_left > 0 else 0
            overlap_width_top = keep_top * 2 if keep_top > 0 else 0
            overlap_width_right = keep_right * 2 if keep_right > 0 else 0
            overlap_width_bottom = keep_bottom * 2 if keep_bottom > 0 else 0

            if left_pad > 0 and overlap_width_left > 0:
                left_extent = min(width, overlap_width_left)
                x = np.arange(left_extent, dtype=np.int32)
                fade_left = ((255 * x) // overlap_width_left).astype(np.uint8)
                mask_array[:, :left_extent] = np.minimum(
                    mask_array[:, :left_extent],
                    fade_left[np.newaxis, :],
                )

            if top_pad > 0 and overlap_width_top > 0:
                top_extent = min(height, overlap_width_top)
                y = np.arange(top_extent, dtype=np.int32)
                fade_top = ((255 * y) // overlap_width_top).astype(np.uint8)
                mask_array[:top_extent, :] = np.minimum(
                    mask_array[:top_extent, :],
                    fade_top[:, np.newaxis],
                )

            if right_pad > 0 and overlap_width_right > 0:
                start_x = width - overlap_width_right
                effective_start_x = max(0, start_x)
                x = np.arange(effective_start_x, width, dtype=np.int32)
                distance = x - start_x
                fade_right = ((255 * (overlap_width_right - distance)) // overlap_width_right).astype(np.uint8)
                mask_array[:, effective_start_x:width] = np.minimum(
                    mask_array[:, effective_start_x:width],
                    fade_right[np.newaxis, :],
                )

            if bottom_pad > 0 and overlap_width_bottom > 0:
                start_y = height - overlap_width_bottom
                effective_start_y = max(0, start_y)
                y = np.arange(effective_start_y, height, dtype=np.int32)
                distance = y - start_y
                fade_bottom = ((255 * (overlap_width_bottom - distance)) // overlap_width_bottom).astype(np.uint8)
                mask_array[effective_start_y:height, :] = np.minimum(
                    mask_array[effective_start_y:height, :],
                    fade_bottom[:, np.newaxis],
                )

        return Image.fromarray(mask_array)
