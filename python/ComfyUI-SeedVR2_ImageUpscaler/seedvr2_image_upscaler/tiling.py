from __future__ import annotations

import numpy as np
from PIL import Image
from typing import Literal


class TileUtils:
    _GPU_DIM_ALIGNMENT = 16

    @staticmethod
    def _compute_axis_positions(length: int, tile_size: int) -> list[int]:
        """Compute axis-aligned tile starts while avoiding tiny remainder tiles at the boundary."""
        if length <= tile_size:
            return [0]

        positions = list(range(0, length, tile_size))
        if not positions:
            return [0]

        # Shift the final tile so it ends on the image boundary.
        positions[-1] = max(0, length - tile_size)

        deduped: list[int] = []
        for pos in positions:
            if not deduped or deduped[-1] != pos:
                deduped.append(pos)
        return deduped

    @staticmethod
    def generate_tiles(
        image: Image.Image,
        tile_size: int,
        tile_overlap: int,
        strategy: Literal["Linear", "Chess"] = "Linear",
    ) -> list[dict[str, object]]:
        """Generate square tiles with overlap based on the specified strategy."""
        if tile_size <= 64:
            tile_size = 64
        if tile_overlap < 0:
            tile_overlap = 0

        width, height = image.size
        x_positions = TileUtils._compute_axis_positions(width, tile_size)
        y_positions = TileUtils._compute_axis_positions(height, tile_size)

        positions: list[tuple[int, int]] = []
        if strategy == "Chess":
            even_positions: list[tuple[int, int]] = []
            odd_positions: list[tuple[int, int]] = []
            for y_idx, y in enumerate(y_positions):
                for x_idx, x in enumerate(x_positions):
                    if ((x_idx + y_idx) & 1) == 0:
                        even_positions.append((x, y))
                    else:
                        odd_positions.append((x, y))
            positions = even_positions + odd_positions
        else:
            positions = [(x, y) for y in y_positions for x in x_positions]

        return [
            TileUtils.get_tile_info(image, x, y, tile_size, tile_overlap)
            for x, y in positions
        ]

    @staticmethod
    def calculate_efficient_tile_size(width: int, height: int) -> tuple[int, int]:
        """Round dimensions up to GPU-friendly alignment without over-padding tiny tiles."""
        alignment = TileUtils._GPU_DIM_ALIGNMENT
        efficient_width = max(alignment, ((width + alignment - 1) // alignment) * alignment)
        efficient_height = max(alignment, ((height + alignment - 1) // alignment) * alignment)

        return efficient_width, efficient_height

    @staticmethod
    def get_tile_info(
        image: Image.Image,
        x: int,
        y: int,
        tile_size: int,
        tile_overlap: int,
    ) -> dict[str, object]:
        """Extract tile information and crop the tile with padding.

        Uses edge extension (reflection) for memory padding instead of solid color fill
        to avoid artificial edges that the AI upscaler would process as real content.
        """
        width, height = image.size

        # Calculate actual tile boundaries (may be smaller at edges)
        actual_tile_width = min(tile_size, width - x)
        actual_tile_height = min(tile_size, height - y)

        # Calculate padding (only add padding where there are adjacent tiles)
        left_pad = tile_overlap if x > 0 else 0
        top_pad = tile_overlap if y > 0 else 0
        right_pad = tile_overlap if x + tile_size < width else 0
        bottom_pad = tile_overlap if y + tile_size < height else 0

        # Create the padded crop box
        padded_box = (
            max(0, x - left_pad),
            max(0, y - top_pad),
            min(width, x + actual_tile_width + right_pad),
            min(height, y + actual_tile_height + bottom_pad),
        )

        tile = image.crop(padded_box)

        # Calculate efficient dimensions for GPU processing
        current_width, current_height = tile.size
        efficient_width, efficient_height = TileUtils.calculate_efficient_tile_size(current_width, current_height)

        # Add memory padding if needed for GPU efficiency
        memory_pad_right = efficient_width - current_width
        memory_pad_bottom = efficient_height - current_height

        if memory_pad_right > 0 or memory_pad_bottom > 0:
            tile = TileUtils._pad_tile_with_reflection(
                tile=tile,
                current_width=current_width,
                current_height=current_height,
                memory_pad_right=memory_pad_right,
                memory_pad_bottom=memory_pad_bottom,
                efficient_width=efficient_width,
                efficient_height=efficient_height,
            )

        return {
            "tile": tile,
            "position": (x, y),
            "actual_size": (actual_tile_width, actual_tile_height),
            "padding": (left_pad, top_pad, right_pad, bottom_pad),
            "memory_padding": (0, 0, memory_pad_right, memory_pad_bottom),
            "original_tile_size": (current_width, current_height),
        }

    @staticmethod
    def _pad_tile_with_reflection(
        tile: Image.Image,
        current_width: int,
        current_height: int,
        memory_pad_right: int,
        memory_pad_bottom: int,
        efficient_width: int,
        efficient_height: int,
    ) -> Image.Image:
        """Pad right/bottom edges using reflection, then edge-repeat if pad exceeds source size."""
        tile_array = np.asarray(tile)
        has_channels = tile_array.ndim == 3
        if not has_channels:
            tile_array = tile_array[:, :, np.newaxis]

        padded_array = np.empty(
            (efficient_height, efficient_width, tile_array.shape[2]),
            dtype=tile_array.dtype,
        )
        padded_array[:current_height, :current_width] = tile_array

        reflect_width = min(memory_pad_right, current_width)
        reflect_height = min(memory_pad_bottom, current_height)
        remaining_right = memory_pad_right - reflect_width
        remaining_bottom = memory_pad_bottom - reflect_height

        if reflect_width > 0:
            padded_array[:current_height, current_width:current_width + reflect_width] = (
                tile_array[:, current_width - reflect_width:current_width][:, ::-1]
            )
        if remaining_right > 0:
            padded_array[:current_height, current_width + reflect_width:efficient_width] = tile_array[:, -1:, :]

        if reflect_height > 0:
            padded_array[current_height:current_height + reflect_height, :current_width] = (
                tile_array[current_height - reflect_height:current_height, :][::-1, :]
            )
        if remaining_bottom > 0:
            padded_array[current_height + reflect_height:efficient_height, :current_width] = tile_array[-1:, :, :]

        if memory_pad_right > 0 and memory_pad_bottom > 0:
            corner = tile_array[
                current_height - reflect_height:current_height,
                current_width - reflect_width:current_width,
            ]
            padded_array[
                current_height:current_height + reflect_height,
                current_width:current_width + reflect_width,
            ] = corner[::-1, ::-1]

            if remaining_right > 0:
                corner_edge_col = padded_array[
                    current_height:current_height + reflect_height,
                    current_width + reflect_width - 1:current_width + reflect_width,
                ]
                padded_array[
                    current_height:current_height + reflect_height,
                    current_width + reflect_width:efficient_width,
                ] = np.repeat(corner_edge_col, remaining_right, axis=1)
            if remaining_bottom > 0:
                corner_edge_row = padded_array[
                    current_height + reflect_height - 1:current_height + reflect_height,
                    current_width:current_width + reflect_width,
                ]
                padded_array[
                    current_height + reflect_height:efficient_height,
                    current_width:current_width + reflect_width,
                ] = np.repeat(corner_edge_row, remaining_bottom, axis=0)
            if remaining_right > 0 and remaining_bottom > 0:
                padded_array[
                    current_height + reflect_height:efficient_height,
                    current_width + reflect_width:efficient_width,
                ] = tile_array[-1, -1]

        if not has_channels:
            padded_array = padded_array[:, :, 0]

        return Image.fromarray(padded_array)
