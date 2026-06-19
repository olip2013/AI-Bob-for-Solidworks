// SwAddin.cs — COM add-in entry point.
//
// SolidWorks loads this class when SW starts (or when the user enables the add-in
// in Tools > Add-Ins). It creates a Task Pane tab containing the chat UI.
//
// Registration: run install.ps1 once as Administrator after building.

using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorksCopilot.UI;

namespace SolidWorksCopilot;

[ComVisible(true)]
[Guid("8F4A2E1D-3C7B-4F9A-8E5D-2A6B1C3D4E5F")]
[ClassInterface(ClassInterfaceType.None)]
public class SwAddin : ISwAddIn
{
    public static ISldWorks? SwApp { get; private set; }

    private ITaskpaneView? _taskPane;
    private CopilotPanel?  _panel;

    // ── ISwAddIn ─────────────────────────────────────────────────────────────

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        SwApp = (ISldWorks)ThisSW;

        // Task Pane icon: empty string uses SW's default icon.
        _taskPane = SwApp.CreateTaskpaneView2("", "AI Bob");
        if (_taskPane == null) return false;

        _panel = new CopilotPanel(SwApp);

        // AddControl3 hosts a WinForms control handle inside the Task Pane.
        _taskPane.AddControl3(_panel, false, "");

        return true;
    }

    public bool DisconnectFromSW()
    {
        _taskPane?.DeleteView();
        _panel?.Dispose();
        return true;
    }
}
