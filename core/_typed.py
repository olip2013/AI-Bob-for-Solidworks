"""Early-binding helpers for the SOLIDWORKS COM API.

Why this exists: pywin32's *late* (dynamic) binding can't tell a no-argument
method (`feature.GetTypeName2()`) from a property, and the running SOLIDWORKS
objects don't expose type information, so `EnsureDispatch`/`CastTo` both fail on
them. The reliable path is to generate early-binding wrappers from the SW type
library once (makepy), then wrap each raw dispatch pointer with the correct
generated interface class ourselves.

`cast(obj, "IFeature")` takes any object SOLIDWORKS handed back (always a generic
IDispatch) and returns it wrapped in the typed class, so method-vs-property
resolution is correct. Every child object pulled out of the tree must be cast to
the interface whose methods you intend to call.
"""

from __future__ import annotations

import os

from win32com.client import gencache

# SldWorks 2024 type library identity (from the generated gen_py module name:
# 83A33D31-27C5-11CE-BFD4-00400513BB57 x0 x32 x0).
_SLDWORKS_TLB_GUID = "{83A33D31-27C5-11CE-BFD4-00400513BB57}"
_SLDWORKS_TLB_MAJOR = 32
_SLDWORKS_TLB_MINOR = 0

# Stock type-library path used to (re)generate the wrappers if the gen_py cache
# is empty on a fresh machine.
_SLDWORKS_TLB_PATH = (
    r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\sldworks.tlb"
)

_module = None


def _load_module():
    global _module
    if _module is not None:
        return _module
    try:
        _module = gencache.GetModuleForTypelib(
            _SLDWORKS_TLB_GUID, 0, _SLDWORKS_TLB_MAJOR, _SLDWORKS_TLB_MINOR
        )
    except Exception:
        _module = None
    if _module is None or not hasattr(_module, "IModelDoc2"):
        # Generate from the .tlb if the cache hasn't been built yet.
        if os.path.exists(_SLDWORKS_TLB_PATH):
            gencache.EnsureModule(
                _SLDWORKS_TLB_GUID, 0, _SLDWORKS_TLB_MAJOR, _SLDWORKS_TLB_MINOR
            )
            _module = gencache.GetModuleForTypelib(
                _SLDWORKS_TLB_GUID, 0, _SLDWORKS_TLB_MAJOR, _SLDWORKS_TLB_MINOR
            )
    if _module is None or not hasattr(_module, "IModelDoc2"):
        raise RuntimeError(
            "SOLIDWORKS type library wrappers unavailable. Run:\n"
            f'  python -m win32com.client.makepy "{_SLDWORKS_TLB_PATH}"'
        )
    return _module


def cast(obj, interface_name: str):
    """Wrap a raw SOLIDWORKS dispatch object in its typed interface class.

    ``obj`` is anything returned from a COM call (generic IDispatch). Returns a
    typed instance on which no-arg methods resolve correctly.
    """
    if obj is None:
        return None
    mod = _load_module()
    iface = getattr(mod, interface_name, None)
    if iface is None:
        raise RuntimeError(f"unknown SOLIDWORKS interface {interface_name!r}")
    # Pass the underlying PyIDispatch, not the dynamic CDispatch wrapper.
    raw = getattr(obj, "_oleobj_", obj)
    return iface(raw)
