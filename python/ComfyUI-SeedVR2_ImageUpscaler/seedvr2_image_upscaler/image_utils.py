from __future__ import annotations

import torch
import numpy as np
from PIL import Image


class ImageUtils:
    @staticmethod
    def tensor_to_pil(tensor: torch.Tensor) -> Image.Image:
        """Convert a tensor to PIL Image.

        Args:
            tensor: Input tensor with shape (1, H, W, C) or (H, W, C)

        Returns:
            PIL Image in RGB mode
        """
        # Only squeeze the batch dimension (dim 0), not all dimensions
        # This prevents accidentally removing spatial dimensions of size 1
        if tensor.dim() == 4:
            tensor = tensor.squeeze(0)  # Remove batch dimension only: (1, H, W, C) -> (H, W, C)
        image_np = tensor.mul(255).clamp(0, 255).byte().numpy()
        return Image.fromarray(image_np, "RGB")
        
    @staticmethod
    def pil_to_tensor(image: Image.Image) -> torch.Tensor:
        return torch.from_numpy(np.array(image).astype(np.float32) / 255.0).unsqueeze(0)
