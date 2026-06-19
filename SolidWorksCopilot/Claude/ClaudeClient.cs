// ClaudeClient.cs — Calls the Anthropic Messages API with tool use.
//
// The model is given one tool: execute_solidworks_script.
// When Claude wants to create or modify geometry it calls that tool with C# code.
// We execute the code via Roslyn and feed the result back so Claude can confirm
// success or recover from errors.
//
// API key: read from %APPDATA%\AiBob\config.txt (one line: sk-ant-...)
// or from the ANTHROPIC_API_KEY environment variable.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolidWorks.Interop.sldworks;

namespace SolidWorksCopilot.Claude;

public class ClaudeClient
{
    private const string ApiUrl   = "https://api.anthropic.com/v1/messages";
    private const string Model    = "claude-sonnet-4-6";
    private const int    MaxTurns = 10; // tool-use round trips per user message

    private readonly ISldWorks   _swApp;
    private readonly HttpClient  _http;
    private readonly string      _apiKey;
    private readonly List<JsonObject> _history = new();

    public ClaudeClient(ISldWorks swApp)
    {
        _swApp  = swApp;
        _apiKey = LoadApiKey();
        _http   = new HttpClient();
        _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Send a user message. Streams text back via <paramref name="onText"/> and
    /// invokes <paramref name="onScript"/> each time Claude calls the script tool.
    /// </summary>
    public async Task ChatAsync(
        string userMessage,
        Action<string> onText,
        Func<string, Task<string>> onScript)
    {
        _history.Add(new JsonObject
        {
            ["role"]    = "user",
            ["content"] = userMessage
        });

        for (int turn = 0; turn < MaxTurns; turn++)
        {
            var response = await CallApiAsync();
            var stopReason = response["stop_reason"]?.GetValue<string>();
            var content    = response["content"]!.AsArray();

            // Collect text blocks and tool-use blocks from this response.
            var toolUses = new List<(string id, string code)>();
            foreach (var block in content)
            {
                var type = block!["type"]!.GetValue<string>();
                if (type == "text")
                {
                    var txt = block["text"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(txt)) onText(txt);
                }
                else if (type == "tool_use")
                {
                    var id   = block["id"]!.GetValue<string>();
                    var code = block["input"]!["code"]!.GetValue<string>();
                    toolUses.Add((id, code));
                }
            }

            // Add assistant turn to history.
            _history.Add(new JsonObject
            {
                ["role"]    = "assistant",
                ["content"] = JsonNode.Parse(JsonSerializer.Serialize(content))!
            });

            if (stopReason == "end_turn" || toolUses.Count == 0)
                break;

            // Execute each script and feed results back.
            var toolResults = new JsonArray();
            foreach (var (id, code) in toolUses)
            {
                var outcome = await onScript(code);
                toolResults.Add(new JsonObject
                {
                    ["type"]        = "tool_result",
                    ["tool_use_id"] = id,
                    ["content"]     = outcome
                });
            }

            _history.Add(new JsonObject
            {
                ["role"]    = "user",
                ["content"] = toolResults
            });

            if (stopReason == "end_turn") break;
        }
    }

    // ── API call ──────────────────────────────────────────────────────────────

    private async Task<JsonObject> CallApiAsync()
    {
        var body = new JsonObject
        {
            ["model"]      = Model,
            ["max_tokens"] = 4096,
            ["system"]     = SystemPrompt(),
            ["tools"]      = BuildTools(),
            ["messages"]   = JsonNode.Parse(JsonSerializer.Serialize(_history))!
        };

        var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(body.ToJsonString(),
                                        Encoding.UTF8, "application/json")
        };
        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Claude API error {resp.StatusCode}: {json}");

        return JsonNode.Parse(json)!.AsObject();
    }

    // ── Tool definition ───────────────────────────────────────────────────────

    private static JsonArray BuildTools() => new()
    {
        new JsonObject
        {
            ["name"]        = "execute_solidworks_script",
            ["description"] = "Execute a C# script inside SolidWorks. " +
                              "The script has access to the global variable `SwApp` " +
                              "(ISldWorks). Use it to create parts, sketches, features, " +
                              "dimensions, and any other SolidWorks operation. " +
                              "Return value is ignored; throw an exception on failure.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["code"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "Valid C# code. Has access to SwApp (ISldWorks), " +
                                          "SolidWorks.Interop.sldworks, and swconst namespaces. " +
                                          "All lengths must be in METERS. Always call " +
                                          "model.EditRebuild3() and model.GraphicsRedraw2() at the end."
                    }
                },
                ["required"] = new JsonArray { "code" }
            }
        }
    };

    // ── System prompt ─────────────────────────────────────────────────────────

    private string SystemPrompt() => $"""
        You are AI Bob, an AI copilot embedded in SolidWorks as a Task Pane add-in.
        You help mechanical engineers create and modify 3D parts through natural language.

        ## How you work
        When the user asks you to create or modify geometry, call the
        `execute_solidworks_script` tool with C# code that uses the SolidWorks COM API.
        You can make multiple tool calls in a conversation to build up a part step by step.

        ## Global variable
        `SwApp` — type ISldWorks. The running SolidWorks application.
        `SwApp.ActiveDoc` — the current open document (cast to IModelDoc2).

        ## Critical rules
        - ALL dimensions are in **METERS** internally (50 mm = 0.05, 10 mm = 0.01).
        - Always call `model.EditRebuild3()` and `model.GraphicsRedraw2()` at the end.
        - Always create **parametric, fully-dimensioned** geometry.
        - Prefer `CreateCornerRectangle` over four separate `CreateLine` calls.
        - After `InsertSketch(true)` (which opens AND closes the sketch), re-select
          the sketch by name before extruding.
        - Throw a descriptive exception if something fails.

        ## Common patterns

        ### New part
        ```csharp
        // Find template
        string tmpl = System.IO.Directory
            .GetFiles(@"C:\ProgramData\SOLIDWORKS", "Part.PRTDOT",
                       System.IO.SearchOption.AllDirectories)
            .OrderByDescending(x => x).First();
        var model = (IModelDoc2)SwApp.NewDocument(tmpl, 0, 0, 0);
        ```

        ### Sketch on Front Plane, rectangle, dimension, extrude
        ```csharp
        // Open sketch on Front Plane
        model.Extension.SelectByID2("Front Plane","PLANE",0,0,0,false,0,null,0);
        model.SketchManager.InsertSketch(true);

        // Rectangle (all in meters)
        model.SketchManager.CreateCornerRectangle(0, 0, 0, 0.05, 0.03, 0); // 50x30mm

        // Add horizontal dimension to bottom edge
        model.ClearSelection2(true);
        model.Extension.SelectByID2("Line1","SKETCHSEGMENT",0,-0.015,0,false,0,null,0);
        var dd = (IDisplayDimension)model.AddDimension2(0.025, -0.02, 0);
        var dim = (IDimension)dd.GetDimension2(0);
        dim.SetSystemValue3(0.05, 2, "");  // 50mm
        dim.Name = "width";

        // Close sketch
        model.SketchManager.InsertSketch(true);

        // Select sketch and extrude 10mm
        model.ClearSelection2(true);
        model.Extension.SelectByID2("Sketch1","SKETCH",0,0,0,false,0,null,0);
        model.FeatureManager.FeatureExtrusion3(
            true,false,false,0,0,0.01,0,false,false,false,false,
            0,0,false,false,false,false,true,true,true,0,0,false);

        model.EditRebuild3();
        model.GraphicsRedraw2();
        ```

        ### Modify a dimension
        ```csharp
        var model = (IModelDoc2)SwApp.ActiveDoc;
        // Walk feature tree to find dim by name
        var feat = (IFeature)model.FirstFeature();
        while (feat != null) {{
            var dd = (IDisplayDimension)feat.GetFirstDisplayDimension();
            while (dd != null) {{
                var dim = (IDimension)dd.GetDimension2(0);
                if (dim?.Name == "width") {{
                    dim.SetSystemValue3(0.08, 2, "");  // change to 80mm
                    break;
                }}
                dd = (IDisplayDimension)feat.GetNextDisplayDimension(dd);
            }}
            feat = (IFeature)feat.GetNextFeature();
        }}
        model.EditRebuild3();
        model.GraphicsRedraw2();
        ```

        ### Hole on a face
        ```csharp
        // Select a face, open sketch, draw circle, cut-extrude
        model.Extension.SelectByID2("","FACE",x,y,z,false,0,null,0);
        model.SketchManager.InsertSketch(true);
        model.SketchManager.CreateCircleByRadius(cx, cy, 0, 0.005); // 5mm radius
        model.SketchManager.InsertSketch(true);
        model.Extension.SelectByID2("Sketch2","SKETCH",0,0,0,false,0,null,0);
        model.FeatureManager.FeatureCut4(
            true,false,false,1,0,0,0,false,false,false,false,0,0,
            false,false,false,false,false,true,true,true,true,false,0,0,false);
        ```

        ## Useful enums
        - `swEndCondBlind = 0`, `swEndCondThroughAll = 1`
        - Plane names: "Front Plane", "Top Plane", "Right Plane"
        - `SetSystemValue3(value, 2, "")` — sets for this configuration

        ## Tone
        Be concise and friendly. Before scripting, confirm you understand the
        user's intent if it is ambiguous. After a successful script, summarise
        what was created in one sentence.

        Current date: {DateTime.Now:yyyy-MM-dd}
        """;

    // ── API key loader ────────────────────────────────────────────────────────

    private static string LoadApiKey()
    {
        // 1. Environment variable
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // 2. Config file at %APPDATA%\AiBob\config.txt
        var cfg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiBob", "config.txt");
        if (File.Exists(cfg))
        {
            var key = File.ReadAllText(cfg).Trim();
            if (!string.IsNullOrWhiteSpace(key)) return key;
        }

        throw new InvalidOperationException(
            "No Anthropic API key found.\n\n" +
            "Set the ANTHROPIC_API_KEY environment variable, OR\n" +
            $"create the file: {cfg}\n" +
            "and paste your sk-ant-... key as the only line.");
    }
}
