// SolidWorksBridge — long-lived JSON command bridge to SolidWorks COM.
//
// Reads one JSON command per line from stdin, executes it against SolidWorks
// via native .NET COM interop, and writes one JSON result per line to stdout.
// The Python MCP server (core/bridge.py) spawns this process once and keeps
// it alive for the session, sharing the COM connection across all tool calls.
//
// Protocol:
//   stdin  → {"op":"create_part","part_id":"part_1","units":"mm",...}
//   stdout ← {"success":true,"errors":[],"rebuild_errors":[],"part_id":"part_1"}

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

// ── Global state ──────────────────────────────────────────────────────────────
ISldWorks? swApp = null;
var models   = new Dictionary<string, IModelDoc2>();
var unitMap  = new Dictionary<string, string>();          // part_id → "mm"|"in"
var sketches = new Dictionary<string, ISketch>();         // sketch_id → ISketch
var skNames  = new Dictionary<string, string>();          // sketch_id → feature name
var entities = new Dictionary<string, ISketchSegment>(); // entity_id → segment
var featNames= new Dictionary<string, string>();          // feat_id → feature name
int _seq = 0;
string NewId(string prefix) => $"{prefix}_{++_seq}";

// ── Unit helpers ──────────────────────────────────────────────────────────────
static double ToM(double v, string u)   => u == "mm" ? v / 1000.0 : v / 39.3700787401575;
static double FromM(double v, string u) => u == "mm" ? v * 1000.0 : v * 39.3700787401575;
static double D2R(double d) => d * Math.PI / 180.0;
static double R2D(double r) => r * 180.0 / Math.PI;

// ── Plane name lookup ─────────────────────────────────────────────────────────
static string PlaneName(string p) => p switch
{
    "Front" => "Front Plane",
    "Top"   => "Top Plane",
    "Right" => "Right Plane",
    _       => p
};

// ── Feature-tree helpers ──────────────────────────────────────────────────────
IDimension? FindDimension(IModelDoc2 m, string shortName)
{
    var f = (IFeature?)m.FirstFeature();
    while (f != null)
    {
        var dd = (IDisplayDimension?)f.GetFirstDisplayDimension();
        while (dd != null)
        {
            var dim = (IDimension?)dd.GetDimension2(0);
            if (dim?.Name == shortName) return dim;
            dd = (IDisplayDimension?)f.GetNextDisplayDimension(dd);
        }
        f = (IFeature?)f.GetNextFeature();
    }
    return null;
}

string? NewestSketchName(IModelDoc2 m)
{
    string? name = null;
    var f = (IFeature?)m.FirstFeature();
    while (f != null)
    {
        if (f.GetTypeName2() == "ProfileFeature") name = f.Name;
        f = (IFeature?)f.GetNextFeature();
    }
    return name;
}

List<string> Rebuild(IModelDoc2 m)
{
    m.EditRebuild3();
    var errs = new List<string>();
    var f = (IFeature?)m.FirstFeature();
    while (f != null)
    {
        try
        {
            int code = f.GetErrorCode2(out _);
            if (code != 0) errs.Add($"{f.Name}: error {code}");
        }
        catch { }
        f = (IFeature?)f.GetNextFeature();
    }
    return errs;
}

// ── Template search ───────────────────────────────────────────────────────────
static string? FindTemplate()
{
    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SOLIDWORKS");
    if (!Directory.Exists(root)) return null;
    return Directory.GetFiles(root, "Part.PRTDOT", SearchOption.AllDirectories)
                    .OrderByDescending(x => x)
                    .FirstOrDefault();
}

// ── Result builders ───────────────────────────────────────────────────────────
static JsonObject Ok(params (string k, JsonNode? v)[] fields)
{
    var o = new JsonObject
    {
        ["success"]        = true,
        ["errors"]         = new JsonArray(),
        ["rebuild_errors"] = new JsonArray()
    };
    foreach (var (k, v) in fields) o[k] = v;
    return o;
}

static JsonObject Fail(string err) => new JsonObject
{
    ["success"]        = false,
    ["errors"]         = new JsonArray(JsonValue.Create(err)!),
    ["rebuild_errors"] = new JsonArray()
};

static JsonObject OkRebuild(List<string> rErrs, params (string k, JsonNode? v)[] fields)
{
    var o = new JsonObject
    {
        ["success"]        = rErrs.Count == 0,
        ["errors"]         = new JsonArray(),
        ["rebuild_errors"] = new JsonArray(rErrs.Select(e => JsonValue.Create(e)!).ToArray())
    };
    foreach (var (k, v) in fields) o[k] = v;
    return o;
}

// ── Command handlers ──────────────────────────────────────────────────────────

JsonObject CmdConnect(JsonNode _)
{
    if (swApp != null)
        return Ok(("version", JsonValue.Create(swApp.RevisionNumber)));

    // Try attaching to a running instance first; launch fresh if none.
    try { swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
    catch
    {
        var t = Type.GetTypeFromProgID("SldWorks.Application");
        if (t == null) return Fail("SolidWorks not found — is it installed?");
        swApp = (ISldWorks)Activator.CreateInstance(t)!;
        swApp.Visible = true;
    }
    return Ok(("version", JsonValue.Create(swApp.RevisionNumber)));
}

JsonObject CmdCreatePart(JsonNode cmd)
{
    if (swApp == null) return Fail("not connected — send 'connect' first");
    var partId = cmd["part_id"]!.GetValue<string>();
    var units  = cmd["units"]?.GetValue<string>() ?? "mm";

    // Prefer explicitly passed template, then SW user preference, then ProgramData glob.
    var tmpl = cmd["template"]?.GetValue<string>()
               ?? swApp.GetUserPreferenceStringValue(
                      (int)swUserPreferenceStringValue_e.swDefaultTemplatePart)
               ?? FindTemplate();
    if (string.IsNullOrWhiteSpace(tmpl))
        return Fail("No part template found; set one in Tools > Options > Default Templates.");

    var m = (IModelDoc2?)swApp.NewDocument(tmpl, 0, 0, 0);
    if (m == null) return Fail($"NewDocument failed for template: {tmpl}");

    models[partId]  = m;
    unitMap[partId] = units;
    return Ok(("part_id", JsonValue.Create(partId)));
}

JsonObject CmdCreateSketch(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");

    var plane = cmd["plane"]!.GetValue<string>();
    bool sel  = m.Extension.SelectByID2(PlaneName(plane), "PLANE", 0, 0, 0, false, 0, null, 0);
    if (!sel) return Fail($"could not select plane '{plane}'");

    m.SketchManager.InsertSketch(true);
    var sk = (ISketch?)m.SketchManager.ActiveSketch;
    if (sk == null) return Fail("sketch did not become active");

    var sid = NewId("sketch");
    sketches[sid] = sk;
    skNames[sid]  = NewestSketchName(m) ?? sid;
    return Ok(("sketch_id", JsonValue.Create(sid)));
}

JsonObject CmdAddLine(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u  = unitMap[partId];
    var s  = cmd["start"]!.AsArray();
    var e  = cmd["end"]!.AsArray();
    double x1 = ToM(s[0]!.GetValue<double>(), u), y1 = ToM(s[1]!.GetValue<double>(), u);
    double x2 = ToM(e[0]!.GetValue<double>(), u), y2 = ToM(e[1]!.GetValue<double>(), u);

    var seg = (ISketchSegment?)m.SketchManager.CreateLine(x1, y1, 0, x2, y2, 0);
    if (seg == null) return Fail("CreateLine returned null");
    var eid = NewId("entity");
    entities[eid] = seg;
    return Ok(("entity_id", JsonValue.Create(eid)));
}

JsonObject CmdAddRectangle(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u  = unitMap[partId];
    var c1 = cmd["corner1"]!.AsArray();
    var c2 = cmd["corner2"]!.AsArray();
    double x1 = ToM(c1[0]!.GetValue<double>(), u), y1 = ToM(c1[1]!.GetValue<double>(), u);
    double x2 = ToM(c2[0]!.GetValue<double>(), u), y2 = ToM(c2[1]!.GetValue<double>(), u);

    var raw = m.SketchManager.CreateCornerRectangle(x1, y1, 0, x2, y2, 0);
    if (raw == null) return Fail("CreateCornerRectangle returned null");

    var arr  = (object[])raw;
    var eids = new JsonArray();
    foreach (var s in arr)
    {
        var eid = NewId("entity");
        entities[eid] = (ISketchSegment)s;
        eids.Add(JsonValue.Create(eid));
    }
    return Ok(("entity_ids", eids));
}

JsonObject CmdAddCircle(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u  = unitMap[partId];
    var c  = cmd["center"]!.AsArray();
    double cx = ToM(c[0]!.GetValue<double>(), u), cy = ToM(c[1]!.GetValue<double>(), u);
    double r  = ToM(cmd["radius"]!.GetValue<double>(), u);

    var seg = (ISketchSegment?)m.SketchManager.CreateCircleByRadius(cx, cy, 0, r);
    if (seg == null) return Fail("CreateCircleByRadius returned null");
    var eid = NewId("entity");
    entities[eid] = seg;
    return Ok(("entity_id", JsonValue.Create(eid)));
}

JsonObject CmdAddArc(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u  = unitMap[partId];
    var c  = cmd["center"]!.AsArray();
    double cx = ToM(c[0]!.GetValue<double>(), u), cy = ToM(c[1]!.GetValue<double>(), u);
    double r  = ToM(cmd["radius"]!.GetValue<double>(), u);
    double a0 = D2R(cmd["start_angle"]!.GetValue<double>());
    double a1 = D2R(cmd["end_angle"]!.GetValue<double>());
    double sx = cx + r * Math.Cos(a0), sy = cy + r * Math.Sin(a0);
    double ex = cx + r * Math.Cos(a1), ey = cy + r * Math.Sin(a1);

    var seg = (ISketchSegment?)m.SketchManager.CreateArc(cx, cy, 0, sx, sy, 0, ex, ey, 0, 1);
    if (seg == null) return Fail("CreateArc returned null");
    var eid = NewId("entity");
    entities[eid] = seg;
    return Ok(("entity_id", JsonValue.Create(eid)));
}

JsonObject CmdAddDimension(JsonNode cmd)
{
    var partId   = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u        = unitMap[partId];
    var entityId = cmd["entity_id"]!.GetValue<string>();
    if (!entities.TryGetValue(entityId, out var seg)) return Fail($"unknown entity_id {entityId}");
    var dimType  = cmd["type"]!.GetValue<string>();
    double value = cmd["value"]!.GetValue<double>();
    var name     = cmd["name"]!.GetValue<string>();

    m.ClearSelection2(true);
    seg.Select4(false, null);

    var dispDim = (IDisplayDimension?)m.AddDimension2(0, 0, 0);
    if (dispDim == null) return Fail("AddDimension2 returned null — entity may not be dimensionable");
    var dim = (IDimension?)dispDim.GetDimension2(0);
    if (dim == null) return Fail("GetDimension2 returned null");

    bool isAngle = dimType == "angle";
    double sys   = isAngle ? D2R(value) : ToM(value, u);
    dim.SetSystemValue3(sys, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, "");
    dim.Name = name;

    var rErrs   = Rebuild(m);
    double curr = isAngle ? R2D(dim.SystemValue) : FromM(dim.SystemValue, u);
    return OkRebuild(rErrs,
        ("dimension_id",    JsonValue.Create(NewId("dim"))),
        ("current_value",   JsonValue.Create(curr)));
}

JsonObject CmdModifyDimension(JsonNode cmd)
{
    var partId   = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u        = unitMap[partId];
    var name     = cmd["name"]!.GetValue<string>();
    double newV  = cmd["new_value"]!.GetValue<double>();

    var dim = FindDimension(m, name);
    if (dim == null) return Fail($"dimension '{name}' not found");

    // Use dynamic to call SW's GetType() without clashing with .NET object.GetType().
    bool isAngle = false;
    try { dynamic d = dim; isAngle = (int)d.GetType() == 1; } catch { }

    double oldV  = isAngle ? R2D(dim.SystemValue) : FromM(dim.SystemValue, u);
    double newSys = isAngle ? D2R(newV) : ToM(newV, u);
    dim.SetSystemValue3(newSys, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, "");

    var rErrs = Rebuild(m);
    return OkRebuild(rErrs,
        ("old_value", JsonValue.Create(oldV)),
        ("new_value", JsonValue.Create(newV)));
}

JsonObject CmdExtrude(JsonNode cmd, bool cut)
{
    var partId   = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u        = unitMap[partId];
    var sketchId = cmd["sketch_id"]!.GetValue<string>();
    if (!skNames.TryGetValue(sketchId, out var skName)) return Fail($"unknown sketch_id {sketchId}");
    double depth = ToM(cmd["depth"]!.GetValue<double>(), u);
    var dir      = cmd["direction"]?.GetValue<string>() ?? "blind";
    int endCond  = dir == "through_all" ? 1 : 0; // swEndCondThroughAll=1, swEndCondBlind=0

    // Close the sketch if still open (add_dimension rebuild may have closed it already).
    if (m.SketchManager.ActiveSketch != null)
        m.SketchManager.InsertSketch(true);

    // Re-select by feature name — more stable than relying on post-close selection state.
    m.ClearSelection2(true);
    m.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null, 0);

    object? featObj = cut
        ? m.FeatureManager.FeatureCut4(
            true, false, false, endCond, 0, depth, 0.0,
            false, false, false, false, 0.0, 0.0,
            false, false, false, false, false,
            true, true, true, true, false, 0, 0.0, false)
        : m.FeatureManager.FeatureExtrusion3(
            true, false, false, endCond, 0, depth, 0.0,
            false, false, false, false, 0.0, 0.0,
            false, false, false, false, true, true, true,
            0, 0.0, false);

    if (featObj == null) return Fail("extrude returned null — check the sketch is valid and closed");
    var feat   = (IFeature)featObj;
    var featId = NewId("feature");
    featNames[featId] = feat.Name;

    var rErrs = Rebuild(m);
    return OkRebuild(rErrs,
        ("feature_id",   JsonValue.Create(featId)),
        ("feature_name", JsonValue.Create(feat.Name)));
}

JsonObject CmdGetModelState(JsonNode cmd)
{
    var partId = cmd["part_id"]!.GetValue<string>();
    if (!models.TryGetValue(partId, out var m)) return Fail($"unknown part_id {partId}");
    var u = unitMap[partId];

    var skList   = new JsonArray();
    var featList = new JsonArray();
    var dimList  = new JsonArray();

    var f = (IFeature?)m.FirstFeature();
    while (f != null)
    {
        var ftype = f.GetTypeName2();
        if (ftype == "ProfileFeature")
        {
            var sk   = (ISketch?)f.GetSpecificFeature2();
            var segs = sk != null ? (object[]?)sk.GetSketchSegments() : null;
            skList.Add(new JsonObject
            {
                ["name"]         = f.Name,
                ["fully_defined"] = sk != null ? (bool?)(sk.GetConstrainedStatus() == 1) : null,
                ["entity_count"] = segs?.Length ?? 0
            });
        }
        else
        {
            featList.Add(new JsonObject
            {
                ["name"]       = f.Name,
                ["type"]       = ftype,
                ["suppressed"] = f.IsSuppressed()
            });
        }

        var dd = (IDisplayDimension?)f.GetFirstDisplayDimension();
        while (dd != null)
        {
            var dim = (IDimension?)dd.GetDimension2(0);
            if (dim != null)
            {
                bool isAng = false;
                try { dynamic d = dim; isAng = (int)d.GetType() == 1; } catch { }
                double val = isAng ? R2D(dim.SystemValue) : FromM(dim.SystemValue, u);
                dimList.Add(new JsonObject
                {
                    ["name"]          = dim.Name,
                    ["value"]         = Math.Round(val, 6),
                    ["owner_feature"] = f.Name
                });
            }
            dd = (IDisplayDimension?)f.GetNextDisplayDimension(dd);
        }
        f = (IFeature?)f.GetNextFeature();
    }

    return Ok(
        ("sketches",   skList),
        ("features",   featList),
        ("dimensions", dimList));
}

// ── Main command loop ─────────────────────────────────────────────────────────
while (Console.ReadLine() is string line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject result;
    try
    {
        var node = JsonNode.Parse(line)!;
        var op   = node["op"]!.GetValue<string>();
        result = op switch
        {
            "connect"           => CmdConnect(node),
            "create_part"       => CmdCreatePart(node),
            "create_sketch"     => CmdCreateSketch(node),
            "add_line"          => CmdAddLine(node),
            "add_rectangle"     => CmdAddRectangle(node),
            "add_circle"        => CmdAddCircle(node),
            "add_arc"           => CmdAddArc(node),
            "add_dimension"     => CmdAddDimension(node),
            "modify_dimension"  => CmdModifyDimension(node),
            "extrude_boss"      => CmdExtrude(node, cut: false),
            "extrude_cut"       => CmdExtrude(node, cut: true),
            "get_model_state"   => CmdGetModelState(node),
            _                   => Fail($"unknown op: {op}")
        };
    }
    catch (Exception ex)
    {
        result = new JsonObject
        {
            ["success"]        = false,
            ["errors"]         = new JsonArray(JsonValue.Create(ex.ToString())!),
            ["rebuild_errors"] = new JsonArray()
        };
    }
    Console.WriteLine(result.ToJsonString());
    Console.Out.Flush();
}
