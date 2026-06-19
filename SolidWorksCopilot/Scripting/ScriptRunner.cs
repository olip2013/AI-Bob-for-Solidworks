// ScriptRunner.cs — executes Claude-generated C# via Roslyn scripting.
//
// Roslyn compiles and runs the code in-process, with the SolidWorks interop
// assemblies already loaded and SwApp injected as a global. No temp files,
// no separate process — the script runs in the same AppDomain as the add-in.

using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksCopilot.Scripting;

public record ScriptResult(bool Success, string? Error = null);

public static class ScriptRunner
{
    // Pre-built script options — shared across calls so Roslyn reuses metadata.
    private static readonly ScriptOptions _options = ScriptOptions.Default
        .WithReferences(
            typeof(ISldWorks).Assembly,                    // sldworks interop
            typeof(swDimensionType_e).Assembly,            // swconst interop
            typeof(object).Assembly,                       // mscorlib / System.Runtime
            typeof(System.Linq.Enumerable).Assembly,       // System.Linq
            typeof(System.IO.File).Assembly                // System.IO
        )
        .WithImports(
            "System",
            "System.Linq",
            "System.IO",
            "System.Collections.Generic",
            "SolidWorks.Interop.sldworks",
            "SolidWorks.Interop.swconst"
        );

    public static async Task<ScriptResult> RunAsync(string code, ISldWorks swApp)
    {
        try
        {
            var globals = new ScriptGlobals { SwApp = swApp };
            await CSharpScript.RunAsync(code, _options, globals,
                                        typeof(ScriptGlobals));
            return new ScriptResult(true);
        }
        catch (CompilationErrorException cex)
        {
            var msg = string.Join("\n", cex.Diagnostics.Select(d => d.ToString()));
            return new ScriptResult(false, $"Compilation error:\n{msg}");
        }
        catch (Exception ex)
        {
            return new ScriptResult(false, ex.Message);
        }
    }
}
