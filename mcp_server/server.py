"""SolidWorks Generator MCP server.

Run directly (stdio transport) for use from Claude Desktop / Claude Code / Cursor:

    python -m mcp_server.server

The tool surface mirrors PLAN.md's tool schema v1. Each modifying tool returns
the result envelope { success, errors, rebuild_errors, ... } so the model can
self-correct.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

# Allow running as a script (python mcp_server/server.py) as well as a module.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from mcp.server.fastmcp import FastMCP  # noqa: E402

import core  # noqa: E402

mcp = FastMCP("solidworks-generator")

# ---- live state ---------------------------------------------------------
# A single SolidWorks session and the parts opened during this server's life.
_session: core.SolidWorksSession | None = None
_parts: dict[str, core.Part] = {}


def _get_session() -> core.SolidWorksSession:
    global _session
    if _session is None:
        _session = core.connect(launch_if_needed=True, visible=True)
    return _session


def _get_part(part_id: str) -> core.Part:
    part = _parts.get(part_id)
    if part is None:
        raise ValueError(f"unknown part_id {part_id!r}; call create_part first")
    return part


# ---- tools --------------------------------------------------------------
@mcp.tool()
def create_part(template: str | None = None, units: str = "mm") -> dict[str, Any]:
    """Create a new single part document. units: 'mm' or 'in'."""
    session = _get_session()
    part = core.Part.create(session, template, units)
    _parts[part.part_id] = part
    return {"success": True, "errors": [], "rebuild_errors": [],
            "part_id": part.part_id}


@mcp.tool()
def create_sketch(part_id: str, plane: str) -> dict[str, Any]:
    """Start a sketch on 'Front' | 'Top' | 'Right' (or a face_ref string)."""
    return _get_part(part_id).create_sketch(plane).to_dict()


@mcp.tool()
def add_line(part_id: str, sketch_id: str, start: list[float],
             end: list[float]) -> dict[str, Any]:
    """Add a line from start [x,y] to end [x,y] (in document units)."""
    return _get_part(part_id).add_line(sketch_id, start, end).to_dict()


@mcp.tool()
def add_rectangle(part_id: str, sketch_id: str, corner1: list[float],
                  corner2: list[float]) -> dict[str, Any]:
    """Add a corner rectangle between two opposite corners [x,y]."""
    return _get_part(part_id).add_rectangle(sketch_id, corner1, corner2).to_dict()


@mcp.tool()
def add_circle(part_id: str, sketch_id: str, center: list[float],
               radius: float) -> dict[str, Any]:
    """Add a circle at center [x,y] with the given radius."""
    return _get_part(part_id).add_circle(sketch_id, center, radius).to_dict()


@mcp.tool()
def add_arc(part_id: str, sketch_id: str, center: list[float], radius: float,
            start_angle: float, end_angle: float) -> dict[str, Any]:
    """Add an arc (angles in degrees, CCW)."""
    return _get_part(part_id).add_arc(
        sketch_id, center, radius, start_angle, end_angle).to_dict()


@mcp.tool()
def add_dimension(part_id: str, entity_id: str, dimension_type: str,
                  value: float, name: str) -> dict[str, Any]:
    """Dimension an entity and give it a REQUIRED semantic name.

    dimension_type: 'length' | 'radius' | 'diameter' | 'angle' | 'distance'.
    The name is mandatory and becomes the handle for modify_dimension.
    """
    return _get_part(part_id).add_dimension(
        entity_id, dimension_type, value, name).to_dict()


@mcp.tool()
def extrude_boss(part_id: str, sketch_id: str, depth: float,
                 direction: str = "blind",
                 profile_selection: str | None = None) -> dict[str, Any]:
    """Extrude a boss. direction: 'blind' | 'through_all' | 'up_to_surface'."""
    return _get_part(part_id).extrude_boss(
        sketch_id, depth, direction, profile_selection).to_dict()


@mcp.tool()
def extrude_cut(part_id: str, sketch_id: str, depth: float,
                direction: str = "blind",
                profile_selection: str | None = None) -> dict[str, Any]:
    """Cut-extrude. direction: 'blind' | 'through_all' | 'up_to_surface'."""
    return _get_part(part_id).extrude_cut(
        sketch_id, depth, direction, profile_selection).to_dict()


@mcp.tool()
def modify_dimension(part_id: str, dimension_name: str,
                     new_value: float) -> dict[str, Any]:
    """Change a named dimension's value and rebuild. Powers parametric edits."""
    return _get_part(part_id).modify_dimension(dimension_name, new_value).to_dict()


@mcp.tool()
def get_model_state(part_id: str) -> dict[str, Any]:
    """Return sketches, features, and named dimensions of the current part."""
    return _get_part(part_id).get_model_state().to_dict()


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
