"""core — Python SolidWorks COM automation library.

Standalone and AI-free: this layer knows nothing about MCP or Claude. It wraps
the SolidWorks COM API (via pywin32) behind a small, testable surface that the
mcp_server/ layer composes into tools.

Public surface:
    connect()            -> SolidWorksSession
    SolidWorksSession    live connection to a running/launched SOLIDWORKS
    Part                 a single open part document
    units                mm/in <-> meters conversion helpers
"""

from .connection import connect, SolidWorksSession
from .part import Part
from .result import Result

__all__ = ["connect", "SolidWorksSession", "Part", "Result"]
