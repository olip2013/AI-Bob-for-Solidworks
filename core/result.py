"""The result envelope.

Every modifying operation returns a Result so the calling model can see what
actually happened — success/failure plus any rebuild errors SolidWorks reported
— rather than assuming the call worked. This mirrors the tool-schema contract in
PLAN.md: `{ success, errors[], rebuild_errors[] }` alongside operation-specific
output carried in `data`.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass
class Result:
    success: bool
    data: dict[str, Any] = field(default_factory=dict)
    errors: list[str] = field(default_factory=list)
    rebuild_errors: list[str] = field(default_factory=list)

    @classmethod
    def ok(cls, **data: Any) -> "Result":
        return cls(success=True, data=data)

    @classmethod
    def fail(cls, *errors: str, **data: Any) -> "Result":
        return cls(success=False, data=data, errors=list(errors))

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "Result":
        r = cls(success=bool(d.get("success", False)))
        r.errors = d.get("errors", [])
        r.rebuild_errors = d.get("rebuild_errors", [])
        r.data = {k: v for k, v in d.items()
                  if k not in ("success", "errors", "rebuild_errors")}
        return r

    def to_dict(self) -> dict[str, Any]:
        out: dict[str, Any] = {
            "success": self.success,
            "errors": self.errors,
            "rebuild_errors": self.rebuild_errors,
        }
        out.update(self.data)
        return out
