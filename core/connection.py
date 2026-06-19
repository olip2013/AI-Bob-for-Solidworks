"""Connect to SolidWorks via the C# bridge process."""

from __future__ import annotations

from .bridge import get_bridge


class SolidWorksError(RuntimeError):
    """Raised when SolidWorks cannot be reached."""


class SolidWorksSession:
    """Thin handle returned by connect(). Owns no COM state — the bridge does."""

    def __init__(self, version: str) -> None:
        self._version = version

    @property
    def version(self) -> str:
        return self._version


def connect(launch_if_needed: bool = True, visible: bool = True) -> SolidWorksSession:
    """Start the C# bridge (if needed) and attach to SolidWorks.

    On first call this compiles and launches the bridge, which then attaches
    to a running SolidWorks instance or starts one.  Subsequent calls in the
    same process reuse the already-running bridge.
    """
    bridge = get_bridge()
    result = bridge.send(op="connect")
    if not result.get("success"):
        errs = result.get("errors", ["unknown error"])
        raise SolidWorksError(f"Could not connect to SolidWorks: {errs[0]}")
    return SolidWorksSession(version=result.get("version", "unknown"))
