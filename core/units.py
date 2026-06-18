"""Unit conversion.

The SOLIDWORKS COM API is unit-agnostic at the call boundary: it always takes
and returns **meters** for length and **radians** for angle, no matter what the
document's display units are set to. Every coordinate/length we hand to a COM
call must therefore be converted from the user-facing unit (mm or in) to meters
first, and every value we read back must be converted out of meters.

Centralizing this here keeps the conversion in exactly one place — the single
most common source of "the geometry is 1000x too big" bugs in SolidWorks
automation.
"""

from __future__ import annotations

import math

Units = str  # "mm" | "in"

_MM_PER_M = 1000.0
_IN_PER_M = 39.3700787401575


def to_meters(value: float, units: Units) -> float:
    """Convert a user-facing length into meters for a COM call."""
    if units == "mm":
        return value / _MM_PER_M
    if units == "in":
        return value / _IN_PER_M
    raise ValueError(f"unknown units: {units!r} (expected 'mm' or 'in')")


def from_meters(value: float, units: Units) -> float:
    """Convert a length read back from COM (meters) into user-facing units."""
    if units == "mm":
        return value * _MM_PER_M
    if units == "in":
        return value * _IN_PER_M
    raise ValueError(f"unknown units: {units!r} (expected 'mm' or 'in')")


def deg_to_rad(degrees: float) -> float:
    return math.radians(degrees)


def rad_to_deg(radians: float) -> float:
    return math.degrees(radians)
