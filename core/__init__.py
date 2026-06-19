"""core — SolidWorks automation via the C# SolidWorksBridge process.

The COM layer lives entirely in the C# bridge (SolidWorksBridge/Program.cs).
Python communicates with it over JSON stdin/stdout, which eliminates the
pywin32 dispatch issues (EnsureDispatch, CastTo, early-binding hacks) we had
when driving COM directly from Python.

Public surface (unchanged from the old pywin32 version):
    connect()            -> SolidWorksSession
    SolidWorksSession    lightweight handle; bridge owns the COM state
    Part                 a single open part document
    Result               { success, errors, rebuild_errors, **data }
"""

from .connection import connect, SolidWorksSession
from .part import Part
from .result import Result

__all__ = ["connect", "SolidWorksSession", "Part", "Result"]
