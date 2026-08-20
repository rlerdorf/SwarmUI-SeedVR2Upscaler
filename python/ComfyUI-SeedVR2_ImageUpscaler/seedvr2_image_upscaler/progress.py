from __future__ import annotations

from comfy.utils import ProgressBar
from .logger import Logger

class Progress:
    def __init__(self, total_steps: int, sub_steps_per_tile: int = 3, enable_debug: bool = False) -> None:
        self.total_steps = max(int(total_steps), 0)
        self.sub_steps_per_tile = max(int(sub_steps_per_tile), 1)
        self.total_sub_steps = max(self.total_steps * self.sub_steps_per_tile, 1)
        self.current_step = 0
        self.current_sub_step = 0
        self.enable_debug = enable_debug
        self._last_percent = -1
        self._progress_bar = ProgressBar(100)

        Logger.log(f"Starting upscale process: tiles={self.total_steps}")
        self._emit_progress(force=True)

    def update(self, sub_progress_step: int | None = None) -> None:
        step = 0

        if sub_progress_step is not None:
            step = max(0, min(int(sub_progress_step), self.sub_steps_per_tile))
        else:
            self.current_step = min(self.total_steps, self.current_step + 1)

        self.current_sub_step = min(
            self.total_sub_steps,
            self.current_step * self.sub_steps_per_tile + step,
        )

        if self.enable_debug:
            Logger.log(
                f"[debug] Progress: tile={self.current_step}/{self.total_steps}, step={self.current_sub_step}/{self.total_sub_steps}"
            )

        self._emit_progress()

    def update_sub_progress(self, step_name: str, step_number: int) -> None:
        step = max(0, min(int(step_number), self.sub_steps_per_tile))
        self.current_sub_step = min(
            self.total_sub_steps,
            self.current_step * self.sub_steps_per_tile + step,
        )

        if self.enable_debug:
            Logger.log(
                f"[debug] {step_name}, tile={self.current_step}/{self.total_steps}, step={step}/{self.sub_steps_per_tile}"
            )

        self._emit_progress()

    def initialize_websocket_progress(self) -> None:
        self.current_step = 0
        self.current_sub_step = 0
        self._emit_progress(force=True)

    def finalize_websocket_progress(self) -> None:
        self.current_step = self.total_steps
        self.current_sub_step = self.total_sub_steps
        self._emit_progress(force=True)
        Logger.log(f"Upscale completed successfully: tiles={self.total_steps}")

    def _emit_progress(self, force: bool = False) -> None:
        percent = int((self.current_sub_step / self.total_sub_steps) * 100)
        percent = max(0, min(100, percent))

        if not force and percent == self._last_percent:
            return

        self._last_percent = percent
        self._progress_bar.update_absolute(percent, 100)
