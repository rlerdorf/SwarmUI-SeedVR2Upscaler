"""SeedVR2 Image Upscaler - ComfyUI custom node for memory-efficient image upscaling."""

import os
from PIL import Image
from .seedvr2_image_upscaler import comfy_entrypoint, SeedVR2ImageUpscaler

# Large stitched outputs can exceed Pillow's decompression-bomb threshold
# for legitimate upscaling jobs.
#
# Default behavior here is unlimited pixels.
# To enforce a limit, explicitly set:
#   SEEDVR2_MAX_IMAGE_PIXELS=<int>   (for example: 500000000)
# To force-disable checks via env value:
#   SEEDVR2_MAX_IMAGE_PIXELS=none
def _set_max_pixels():
    _max_pixels_env = os.getenv("SEEDVR2_MAX_IMAGE_PIXELS", "").strip()
    Image.MAX_IMAGE_PIXELS = None
    if not _max_pixels_env or _max_pixels_env.lower() in {"none", "disable", "unlimited", "0"}:
        return

    try:
        parsed_limit = int(_max_pixels_env)
        Image.MAX_IMAGE_PIXELS = parsed_limit if parsed_limit > 0 else None
    except ValueError:
        return

_set_max_pixels()

__all__ = ["comfy_entrypoint", "SeedVR2ImageUpscaler"]
