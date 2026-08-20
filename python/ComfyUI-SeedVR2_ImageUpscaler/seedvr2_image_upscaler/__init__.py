from comfy_api.latest import ComfyExtension, io

from .image_upscaler import SeedVR2ImageUpscaler


class SeedVR2ImageUpscalerExtension(ComfyExtension):
    async def get_node_list(self) -> list[type[io.ComfyNode]]:
        return [
            SeedVR2ImageUpscaler,
        ]


async def comfy_entrypoint() -> ComfyExtension:
    return SeedVR2ImageUpscalerExtension()


__all__ = [
    "SeedVR2ImageUpscaler",
    "comfy_entrypoint",
]
