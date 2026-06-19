"""Part — thin Python façade over the C# SolidWorksBridge.

All geometry operations are delegated to the bridge via JSON. This file owns
no COM state; the bridge process holds all COM object references keyed by the
string IDs we pass back to callers.
"""

from __future__ import annotations

import itertools
from typing import Any

from .bridge import get_bridge
from .result import Result


_counter = itertools.count(1)


def _new_part_id() -> str:
    return f"part_{next(_counter)}"


class Part:
    def __init__(self, part_id: str, units_str: str) -> None:
        self.part_id = part_id
        self.units = units_str
        self._bridge = get_bridge()

    # ── factory ──────────────────────────────────────────────────────────────
    @classmethod
    def create(cls, session: Any, template: str | None,
               units_str: str) -> "Part":
        part_id = _new_part_id()
        bridge  = get_bridge()
        r = bridge.send(op="create_part", part_id=part_id,
                        template=template, units=units_str)
        if not r.get("success"):
            raise RuntimeError(
                r.get("errors", ["create_part failed"])[0])
        return cls(part_id, units_str)

    # ── internal helper ───────────────────────────────────────────────────────
    def _send(self, op: str, **kwargs) -> Result:
        raw = self._bridge.send(op=op, part_id=self.part_id, **kwargs)
        return Result.from_dict(raw)

    # ── sketch ────────────────────────────────────────────────────────────────
    def create_sketch(self, plane: str | dict) -> Result:
        if isinstance(plane, dict):
            plane = plane.get("face_ref", "Front")
        return self._send("create_sketch", plane=plane)

    # ── primitives ────────────────────────────────────────────────────────────
    def add_line(self, sketch_id: str, start, end) -> Result:
        return self._send("add_line", sketch_id=sketch_id,
                          start=list(start), end=list(end))

    def add_rectangle(self, sketch_id: str, corner1, corner2) -> Result:
        return self._send("add_rectangle", sketch_id=sketch_id,
                          corner1=list(corner1), corner2=list(corner2))

    def add_circle(self, sketch_id: str, center, radius: float) -> Result:
        return self._send("add_circle", sketch_id=sketch_id,
                          center=list(center), radius=radius)

    def add_arc(self, sketch_id: str, center, radius: float,
                start_angle: float, end_angle: float) -> Result:
        return self._send("add_arc", sketch_id=sketch_id,
                          center=list(center), radius=radius,
                          start_angle=start_angle, end_angle=end_angle)

    # ── dimensions ────────────────────────────────────────────────────────────
    def add_dimension(self, entity_id: str, dimension_type: str,
                      value: float, name: str) -> Result:
        return self._send("add_dimension", entity_id=entity_id,
                          type=dimension_type, value=value, name=name)

    def modify_dimension(self, dimension_name: str, new_value: float) -> Result:
        return self._send("modify_dimension",
                          name=dimension_name, new_value=new_value)

    # ── features ──────────────────────────────────────────────────────────────
    def extrude_boss(self, sketch_id: str, depth: float,
                     direction: str = "blind",
                     profile_selection: str | None = None) -> Result:
        return self._send("extrude_boss", sketch_id=sketch_id,
                          depth=depth, direction=direction)

    def extrude_cut(self, sketch_id: str, depth: float,
                    direction: str = "blind",
                    profile_selection: str | None = None) -> Result:
        return self._send("extrude_cut", sketch_id=sketch_id,
                          depth=depth, direction=direction)

    # ── state ─────────────────────────────────────────────────────────────────
    def get_model_state(self) -> Result:
        return self._send("get_model_state")
