// ScriptGlobals.cs — variables injected into every Roslyn script execution.
//
// Anything declared here is available as a top-level name in the C# scripts
// that Claude generates (e.g. `SwApp.ActiveDoc`).

using SolidWorks.Interop.sldworks;

namespace SolidWorksCopilot.Scripting;

public class ScriptGlobals
{
    /// <summary>The running SolidWorks application (ISldWorks).</summary>
    public ISldWorks SwApp { get; init; } = null!;
}
