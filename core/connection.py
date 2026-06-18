"""Connecting to SOLIDWORKS over COM.

Two paths:
  * attach to an already-running instance (preferred — does not disturb the
    user's session), via the running object table;
  * otherwise launch a fresh instance via Dispatch.

Both yield a SolidWorksSession wrapping ISldWorks.
"""

from __future__ import annotations

import pythoncom
import win32com.client

from . import _typed

PROG_ID = "SldWorks.Application"


class SolidWorksError(RuntimeError):
    """Raised when SOLIDWORKS cannot be reached or a COM call fails fatally."""


class SolidWorksSession:
    """A live connection to the SOLIDWORKS application object (ISldWorks)."""

    def __init__(self, sw_app):
        self.app = sw_app

    @property
    def version(self) -> str:
        # RevisionNumber resolves as a method under early binding and as a
        # property under late binding; tolerate both.
        try:
            rev = self.app.RevisionNumber
            return str(rev() if callable(rev) else rev)
        except Exception:  # noqa: BLE001 - best-effort metadata only
            return "unknown"

    def open_parts(self) -> list:
        """Currently open ModelDoc2 documents (any type)."""
        docs = []
        doc = self.app.GetFirstDocument()
        while doc is not None:
            docs.append(doc)
            doc = doc.GetNext()
        return docs


def _try_attach():
    """Attach to a running SOLIDWORKS instance, or return None if none is up."""
    try:
        return win32com.client.GetActiveObject(PROG_ID)
    except pythoncom.com_error:
        return None


def _launch(visible: bool):
    try:
        app = win32com.client.Dispatch(PROG_ID)
    except pythoncom.com_error as exc:  # noqa: PERF203
        raise SolidWorksError(
            f"could not launch SOLIDWORKS ({PROG_ID}). Is it installed?"
        ) from exc
    app.Visible = visible
    return app


def connect(launch_if_needed: bool = True, visible: bool = True) -> SolidWorksSession:
    """Return a SolidWorksSession.

    Tries to attach to a running instance first. If none is running and
    ``launch_if_needed`` is True, starts one (this can take a minute or two on a
    cold start while SOLIDWORKS boots).
    """
    app = _try_attach()
    if app is None:
        if not launch_if_needed:
            raise SolidWorksError("SOLIDWORKS is not running and launch is disabled")
        app = _launch(visible)
    return SolidWorksSession(app)
