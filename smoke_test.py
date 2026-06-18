"""Live smoke test of the core/ round trip — no MCP, no Claude.

Boots (or attaches to) SOLIDWORKS, then drives the full MVP path described in
PLAN.md's first milestone:

    create part -> sketch on Front -> rectangle -> dimension it ->
    extrude boss -> read state back -> modify a dimension -> read state again

Run:  python smoke_test.py

This is the fastest way to validate the COM layer against a real install. It
leaves the part open and visible so you can eyeball the geometry.
"""

from __future__ import annotations

import json

import core


def show(label, result):
    d = result.to_dict() if hasattr(result, "to_dict") else result
    print(f"\n=== {label} ===")
    print(json.dumps(d, indent=2, default=str))
    return d


def main() -> int:
    print("Connecting to SOLIDWORKS (may launch it; cold start can take ~1-2 min)...")
    session = core.connect(launch_if_needed=True, visible=True)
    print(f"Connected. Revision: {session.version}")

    part = core.Part.create(session, template=None, units_str="mm")
    print(f"Created part: {part.part_id}")

    r = show("create_sketch(Front)", part.create_sketch("Front"))
    sketch_id = r["sketch_id"]

    r = show("add_rectangle 50x30", part.add_rectangle(
        sketch_id, [0, 0], [50, 30]))
    # Dimension one horizontal edge as the width.
    width_edge = r["entity_ids"][0]

    show("add_dimension width=50",
         part.add_dimension(width_edge, "length", 50, "width"))

    show("extrude_boss depth=10",
         part.extrude_boss(sketch_id, 10, "blind"))

    show("get_model_state", part.get_model_state())

    show("modify_dimension width -> 80",
         part.modify_dimension("width", 80))

    show("get_model_state (after edit)", part.get_model_state())

    print("\nDone. The part is left open for inspection.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
