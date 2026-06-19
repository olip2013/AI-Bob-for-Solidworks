"""Manages the long-lived SolidWorksBridge C# subprocess.

The bridge is started once per Python process and shared across all Part
instances. It holds the SolidWorks COM connection and all in-process COM
object references. Python sends one JSON line per command, C# responds with
one JSON line per result.
"""

from __future__ import annotations

import json
import subprocess
import threading
from pathlib import Path

_BRIDGE_PROJECT = Path(__file__).resolve().parent.parent / "SolidWorksBridge"

_bridge: "Bridge | None" = None
_lock = threading.Lock()


def get_bridge() -> "Bridge":
    global _bridge
    with _lock:
        if _bridge is None or not _bridge.alive:
            _bridge = Bridge()
    return _bridge


class Bridge:
    def __init__(self) -> None:
        # stderr is inherited so dotnet build output appears in the MCP log.
        try:
            self._proc = subprocess.Popen(
                ["dotnet", "run", "--project", str(_BRIDGE_PROJECT)],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                bufsize=1,
            )
        except FileNotFoundError:
            raise RuntimeError(
                "dotnet not found — install the .NET 8 SDK from "
                "https://dotnet.microsoft.com/download/dotnet/8.0"
            ) from None
        self._lock = threading.Lock()

    @property
    def alive(self) -> bool:
        return self._proc.poll() is None

    def send(self, **kwargs) -> dict:
        with self._lock:
            if not self.alive:
                raise RuntimeError("SolidWorksBridge process has exited unexpectedly")
            line = json.dumps(kwargs) + "\n"
            self._proc.stdin.write(line)  # type: ignore[union-attr]
            self._proc.stdin.flush()      # type: ignore[union-attr]
            response = self._proc.stdout.readline()  # type: ignore[union-attr]
            if not response:
                raise RuntimeError("SolidWorksBridge closed stdout with no response")
            return json.loads(response)

    def close(self) -> None:
        try:
            if self._proc.stdin:
                self._proc.stdin.close()
            self._proc.wait(timeout=5)
        except Exception:
            self._proc.kill()
