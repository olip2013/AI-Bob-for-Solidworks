"""Part — a single open part document and the operations on it.

This wraps ModelDoc2 plus the sketch/feature managers. It owns an in-process
registry mapping the opaque string IDs handed back to callers (sketch_id,
entity_id, dimension_id, feature_id) onto the live COM objects, so a stateless
caller (the MCP server) can refer to geometry across calls.

Geometry references note (PLAN.md, "Key technical risks"): indices into the
feature tree break silently after upstream edits. Where we hand back a feature
or dimension we key it by its SolidWorks *name* (which we force to be semantic),
not by index, so a later modify_dimension stays valid across rebuilds.

All public methods that change the model return a Result and rebuild+validate
before returning.
"""

from __future__ import annotations

import glob
import itertools
import os
from typing import Any

from . import _typed, units
from .result import Result

# swSketchSegments / selection type constants we rely on. Hardcoded rather than
# importing a type library so core/ stays importable without a SW typelib build.
_PLANE_FEATURE_NAMES = {
    "Front": "Front Plane",
    "Top": "Top Plane",
    "Right": "Right Plane",
}

# swDocPART = 1 ; swUserPreferenceIntegerValue_e: swUnitsLinear = 0
_SW_DOC_PART = 1


def _find_stock_part_template() -> str | None:
    """Locate a stock Part template when no default is configured.

    Looks under %ProgramData%\\SOLIDWORKS\\<version>\\templates for the plain
    Part.PRTDOT, preferring the newest version directory.
    """
    base = os.path.join(os.environ.get("ProgramData", r"C:\ProgramData"),
                        "SOLIDWORKS")
    matches = glob.glob(os.path.join(base, "**", "Part.PRTDOT"), recursive=True)
    # Newest install dir first (e.g. SOLIDWORKS 2024 > 2023).
    matches.sort(reverse=True)
    return matches[0] if matches else None


class Part:
    def __init__(self, session, model, units_str: units.Units):
        self.session = session
        self.app = session.app
        self.model = model  # ModelDoc2
        self.units = units_str
        self._ids = itertools.count(1)
        # registries: string id -> COM object
        self._sketches: dict[str, Any] = {}
        self._sketch_names: dict[str, str] = {}  # sketch_id -> feature name
        self._entities: dict[str, Any] = {}
        self._dimensions: dict[str, str] = {}  # dim_id -> semantic name
        self._features: dict[str, str] = {}     # feat_id -> feature name
        self.part_id = self._new_id("part")

    # ------------------------------------------------------------------ ids
    def _new_id(self, prefix: str) -> str:
        return f"{prefix}_{next(self._ids)}"

    # ----------------------------------------------------------- factories
    @classmethod
    def create(cls, session, template: str | None, units_str: units.Units) -> "Part":
        """Create a new part document and wrap it."""
        app = session.app
        if template:
            tmpl = template
        else:
            # Prefer the registered default part template (swDefaultTemplatePart=0).
            # Many installs leave this unset, so fall back to the stock template
            # shipped under %ProgramData%\SOLIDWORKS\<version>\templates.
            tmpl = app.GetUserPreferenceStringValue(0) or _find_stock_part_template()
        if not tmpl:
            raise RuntimeError(
                "no part template found; set one in Tools > Options > Default "
                "Templates, or pass template= explicitly"
            )
        model = app.NewDocument(tmpl, 0, 0, 0)
        if model is None:
            raise RuntimeError(f"NewDocument failed for template {tmpl!r}")
        # The doc's default interface is IModelDoc; cast to IModelDoc2 so the
        # feature/sketch/dimension methods we need resolve correctly.
        model = _typed.cast(model, "IModelDoc2")
        return cls(session, model, units_str)

    # -------------------------------------------------------------- sketch
    def create_sketch(self, plane: str | dict) -> Result:
        ext = self.model.Extension
        if isinstance(plane, str):
            name = _PLANE_FEATURE_NAMES.get(plane)
            if not name:
                return Result.fail(f"unknown plane {plane!r}")
            # SelectByID2(name, type, x, y, z, append, mark, callout, selectOption)
            selected = ext.SelectByID2(
                name, "PLANE", 0.0, 0.0, 0.0, False, 0, None, 0)
        else:
            face_ref = plane.get("face_ref")
            selected = ext.SelectByID2(
                face_ref, "FACE", 0.0, 0.0, 0.0, False, 0, None, 0)
        if not selected:
            return Result.fail(f"could not select plane {plane!r}")

        self.model.SketchManager.InsertSketch(True)
        sketch = self.model.SketchManager.ActiveSketch
        if sketch is None:
            return Result.fail("sketch did not become active")
        sketch_id = self._new_id("sketch")
        self._sketches[sketch_id] = sketch
        # Capture the sketch's feature name now so we can re-select it by name
        # for the extrude — a rebuild between here and then deselects/closes it,
        # and name-based selection survives that (PLAN.md: avoid index refs).
        self._sketch_names[sketch_id] = self._newest_sketch_name()
        return Result.ok(sketch_id=sketch_id)

    def _newest_sketch_name(self) -> str | None:
        """Name of the most recently created sketch (last ProfileFeature)."""
        name = None
        feat = _typed.cast(self.model.FirstFeature(), "IFeature")
        while feat is not None:
            if feat.GetTypeName2() == "ProfileFeature":
                name = feat.Name
            feat = _typed.cast(feat.GetNextFeature(), "IFeature")
        return name

    def _require_active_sketch(self, sketch_id: str):
        sketch = self._sketches.get(sketch_id)
        if sketch is None:
            return None, Result.fail(f"unknown sketch_id {sketch_id!r}")
        # Ensure this sketch is the active one before adding geometry.
        if self.model.SketchManager.ActiveSketch is None:
            # Re-enter edit by selecting and inserting; name-based for stability.
            return sketch, None
        return sketch, None

    # ---------------------------------------------------------- primitives
    def add_line(self, sketch_id: str, start, end) -> Result:
        sketch, err = self._require_active_sketch(sketch_id)
        if err:
            return err
        sm = self.model.SketchManager
        seg = sm.CreateLine(
            units.to_meters(start[0], self.units),
            units.to_meters(start[1], self.units),
            0.0,
            units.to_meters(end[0], self.units),
            units.to_meters(end[1], self.units),
            0.0,
        )
        if seg is None:
            return Result.fail("CreateLine returned nothing")
        entity_id = self._new_id("entity")
        self._entities[entity_id] = seg
        return Result.ok(entity_id=entity_id)

    def add_rectangle(self, sketch_id: str, corner1, corner2) -> Result:
        sketch, err = self._require_active_sketch(sketch_id)
        if err:
            return err
        sm = self.model.SketchManager
        segs = sm.CreateCornerRectangle(
            units.to_meters(corner1[0], self.units),
            units.to_meters(corner1[1], self.units),
            0.0,
            units.to_meters(corner2[0], self.units),
            units.to_meters(corner2[1], self.units),
            0.0,
        )
        if segs is None:
            return Result.fail("CreateCornerRectangle returned nothing")
        entity_ids = []
        for seg in segs:
            eid = self._new_id("entity")
            self._entities[eid] = seg
            entity_ids.append(eid)
        return Result.ok(entity_ids=entity_ids)

    def add_circle(self, sketch_id: str, center, radius: float) -> Result:
        sketch, err = self._require_active_sketch(sketch_id)
        if err:
            return err
        sm = self.model.SketchManager
        cx = units.to_meters(center[0], self.units)
        cy = units.to_meters(center[1], self.units)
        r = units.to_meters(radius, self.units)
        # CreateCircleByRadius(cx, cy, cz, radius)
        seg = sm.CreateCircleByRadius(cx, cy, 0.0, r)
        if seg is None:
            return Result.fail("CreateCircleByRadius returned nothing")
        entity_id = self._new_id("entity")
        self._entities[entity_id] = seg
        return Result.ok(entity_id=entity_id)

    def add_arc(self, sketch_id, center, radius, start_angle, end_angle) -> Result:
        sketch, err = self._require_active_sketch(sketch_id)
        if err:
            return err
        sm = self.model.SketchManager
        cx = units.to_meters(center[0], self.units)
        cy = units.to_meters(center[1], self.units)
        r = units.to_meters(radius, self.units)
        a0 = units.deg_to_rad(start_angle)
        a1 = units.deg_to_rad(end_angle)
        import math

        start = (cx + r * math.cos(a0), cy + r * math.sin(a0))
        end = (cx + r * math.cos(a1), cy + r * math.sin(a1))
        # CreateArc(cx,cy,cz, sx,sy,sz, ex,ey,ez, direction) dir 1=CCW
        seg = sm.CreateArc(cx, cy, 0.0, start[0], start[1], 0.0,
                           end[0], end[1], 0.0, 1)
        if seg is None:
            return Result.fail("CreateArc returned nothing")
        entity_id = self._new_id("entity")
        self._entities[entity_id] = seg
        return Result.ok(entity_id=entity_id)

    # ---------------------------------------------------------- dimensions
    def add_dimension(self, entity_id, dimension_type, value, name) -> Result:
        """Dimension an entity, set its value, and force a semantic name.

        Forced naming (PLAN.md risk #2) is what makes modify_dimension reliable:
        every dimension is renamed away from D1@Sketch1 to the caller-supplied
        semantic name immediately on creation.
        """
        if not name:
            return Result.fail("name is required for add_dimension")
        seg = self._entities.get(entity_id)
        if seg is None:
            return Result.fail(f"unknown entity_id {entity_id!r}")

        self.model.ClearSelection2(True)
        skseg = _typed.cast(seg, "ISketchSegment")
        if not skseg.Select4(False, None):  # (append, callout=null dispatch)
            return Result.fail("could not select entity for dimensioning")

        # AddDimension2 places a display dimension near the given point. The
        # location only affects label placement, not the measured value.
        disp_dim = self.model.AddDimension2(0.0, 0.0, 0.0)
        if disp_dim is None:
            return Result.fail("AddDimension2 failed (is the entity dimensionable?)")

        disp_dim = _typed.cast(disp_dim, "IDisplayDimension")
        dim = _typed.cast(disp_dim.GetDimension2(0), "IDimension")
        # Set the value in meters/radians.
        if dimension_type == "angle":
            sys_value = units.deg_to_rad(value)
        else:
            sys_value = units.to_meters(value, self.units)
        # SetSystemValue3(value, config option=2 (this config), names) -> status
        dim.SetSystemValue3(sys_value, 2, None)

        # Force the semantic name. Dimension.Name is the short name (no @sketch).
        try:
            dim.Name = name
        except Exception as exc:  # noqa: BLE001
            return Result.fail(f"could not rename dimension to {name!r}: {exc}")

        dim_id = self._new_id("dim")
        self._dimensions[dim_id] = name
        rebuild_errors = self._rebuild()
        current = units.from_meters(dim.SystemValue, self.units) \
            if dimension_type != "angle" else units.rad_to_deg(dim.SystemValue)
        res = Result.ok(dimension_id=dim_id, current_value=current)
        res.rebuild_errors = rebuild_errors
        res.success = not rebuild_errors
        return res

    def modify_dimension(self, dimension_name: str, new_value: float) -> Result:
        """Change a dimension by its semantic name and rebuild."""
        # Parameter("name@feature") addresses the dimension. We stored the short
        # name; SolidWorks resolves "name" if unique, else "name@Sketch1".
        param = self.model.Parameter(dimension_name)
        if param is None:
            # Try to find a fully-qualified match.
            return Result.fail(f"dimension {dimension_name!r} not found")
        old = units.from_meters(param.SystemValue, self.units)
        param.SystemValue = units.to_meters(new_value, self.units)
        rebuild_errors = self._rebuild()
        res = Result.ok(old_value=old, new_value=new_value)
        res.rebuild_errors = rebuild_errors
        res.success = not rebuild_errors
        return res

    # ------------------------------------------------------------ features
    def _extrude(self, sketch_id, depth, direction, cut: bool,
                 profile_selection=None) -> Result:
        sketch = self._sketches.get(sketch_id)
        if sketch is None:
            return Result.fail(f"unknown sketch_id {sketch_id!r}")

        # Close the sketch if it is still being edited.
        if self.model.SketchManager.ActiveSketch is not None:
            self.model.SketchManager.InsertSketch(True)

        # Select the sketch by name. We can't rely on the post-close selection
        # because add_dimension's rebuild may have already closed and deselected
        # it. Name-based selection is stable across that.
        self.model.ClearSelection2(True)
        sk_name = self._sketch_names.get(sketch_id)
        if sk_name:
            self.model.Extension.SelectByID2(
                sk_name, "SKETCH", 0.0, 0.0, 0.0, False, 0, None, 0)

        depth_m = units.to_meters(depth, self.units)
        through_all = direction == "through_all"
        # swEndCondBlind = 0, swEndCondThroughAll = 1
        end_cond = 1 if through_all else 0

        fm = self.model.FeatureManager
        if cut:
            feature = fm.FeatureCut4(
                True, False, False, end_cond, 0, depth_m, 0.0,
                False, False, False, False, 0.0, 0.0,
                False, False, False, False, False,
                True, True, True, True, False, 0, 0.0, False,
            )
        else:
            feature = fm.FeatureExtrusion3(
                True, False, False, end_cond, 0, depth_m, 0.0,
                False, False, False, False, 0.0, 0.0,
                False, False, False, False, True, True, True,
                0, 0.0, False,
            )
        if feature is None:
            return Result.fail("extrude returned no feature (check sketch is closed)")

        feature = _typed.cast(feature, "IFeature")
        feat_id = self._new_id("feature")
        self._features[feat_id] = feature.Name
        rebuild_errors = self._rebuild()
        res = Result.ok(feature_id=feat_id, feature_name=feature.Name)
        res.rebuild_errors = rebuild_errors
        res.success = not rebuild_errors
        return res

    def extrude_boss(self, sketch_id, depth, direction, profile_selection=None):
        return self._extrude(sketch_id, depth, direction, cut=False,
                             profile_selection=profile_selection)

    def extrude_cut(self, sketch_id, depth, direction, profile_selection=None):
        return self._extrude(sketch_id, depth, direction, cut=True,
                             profile_selection=profile_selection)

    # --------------------------------------------------------------- state
    def get_model_state(self) -> Result:
        """Serialize feature tree + dimensions for the LLM to reason about."""
        sketches = []
        features = []
        dimensions = []

        feat = _typed.cast(self.model.FirstFeature(), "IFeature")
        while feat is not None:
            ftype = feat.GetTypeName2()
            if ftype == "ProfileFeature":  # a sketch
                sk = _typed.cast(feat.GetSpecificFeature2(), "ISketch")
                segs = sk.GetSketchSegments() if sk else None
                sketches.append({
                    "name": feat.Name,
                    # GetConstrainedStatus: 1 == fully defined.
                    "fully_defined": bool(sk.GetConstrainedStatus() == 1) if sk else None,
                    "entity_count": len(segs) if segs else 0,
                })
            else:
                features.append({
                    "name": feat.Name,
                    "type": ftype,
                    "suppressed": bool(feat.IsSuppressed()),
                })
            # Walk display dimensions on this feature.
            dd = _typed.cast(feat.GetFirstDisplayDimension(), "IDisplayDimension")
            while dd is not None:
                dim = _typed.cast(dd.GetDimension2(0), "IDimension")
                if dim is not None:
                    is_angle = dim.GetType() == 1  # swDimensionType angular ~ 1
                    val = (units.rad_to_deg(dim.SystemValue) if is_angle
                           else units.from_meters(dim.SystemValue, self.units))
                    dimensions.append({
                        "name": dim.Name,
                        "value": round(val, 6),
                        "owner_feature": feat.Name,
                    })
                dd = _typed.cast(feat.GetNextDisplayDimension(dd), "IDisplayDimension")
            feat = _typed.cast(feat.GetNextFeature(), "IFeature")

        return Result.ok(sketches=sketches, features=features, dimensions=dimensions)

    # ------------------------------------------------------------- rebuild
    def _rebuild(self) -> list[str]:
        """Force a rebuild and collect feature errors. Empty list == clean."""
        # EditRebuild3 returns True on success.
        self.model.EditRebuild3()
        errors: list[str] = []
        feat = _typed.cast(self.model.FirstFeature(), "IFeature")
        while feat is not None:
            try:
                # GetErrorCode2(out warning) -> (error_code, warning). Nonzero
                # error code means this feature failed to rebuild.
                state = feat.GetErrorCode2(0)
                code = state[0] if isinstance(state, (tuple, list)) else state
                if code:
                    errors.append(f"{feat.Name}: error code {code}")
            except Exception:  # noqa: BLE001 - not all features expose this
                pass
            feat = _typed.cast(feat.GetNextFeature(), "IFeature")
        return errors
