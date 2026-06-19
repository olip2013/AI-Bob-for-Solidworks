using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorksCopilot.UI;

namespace SolidWorksCopilot;

[ComVisible(true)]
[Guid("8F4A2E1D-3C7B-4F9A-8E5D-2A6B1C3D4E5F")]
[ClassInterface(ClassInterfaceType.None)]
public class SwAddin : ISwAddin
{
    public static ISldWorks? SwApp { get; private set; }

    private CopilotForm? _form;

    static SwAddin()
    {
        // SolidWorks loads this assembly from our bin folder, but its default
        // probing path is SolidWorks' own directory — so dependency DLLs
        // (Roslyn, System.Text.Json, etc.) won't be found unless we resolve
        // them ourselves from alongside this assembly.
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location)!;
                var dll = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
            }
            catch { return null; }
        };
        Log("static ctor: assembly resolver installed");
    }

    public SwAddin() => Log("instance ctor: object created by COM");

    public bool ConnectToSW(object ThisSW, int cookie)
    {
        try
        {
            Log("ConnectToSW: start");
            SwApp = (ISldWorks)ThisSW;
            Log($"ConnectToSW: got ISldWorks, rev={SwApp.RevisionNumber()}");

            _form = new CopilotForm(SwApp);
            _form.Show();
            Log("ConnectToSW: form shown — success");
            return true;
        }
        catch (Exception ex)
        {
            Log("ConnectToSW: EXCEPTION\n" + ex);
            return false;
        }
    }

    public bool DisconnectFromSW()
    {
        try
        {
            _form?.Close();
            _form?.Dispose();
            _form = null;
        }
        catch (Exception ex)
        {
            Log("DisconnectFromSW: EXCEPTION\n" + ex);
        }
        return true;
    }

    internal static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "aibob_log.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss}  {msg}\n");
        }
        catch { }
    }
}
