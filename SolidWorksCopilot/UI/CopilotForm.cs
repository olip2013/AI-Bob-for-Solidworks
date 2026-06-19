// CopilotForm.cs — floating modeless window that hosts the chat panel.
//
// Used instead of a Task Pane tab because Task Pane hosting requires the
// control to be registered as a full ActiveX control, which the .NET 8
// COM host does not set up. A plain WinForms Form has no such requirement.

using System.Drawing;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace SolidWorksCopilot.UI;

public class CopilotForm : Form
{
    public CopilotForm(ISldWorks swApp)
    {
        Text          = "AI Bob - CAD Copilot";
        Width         = 440;
        Height        = 640;
        StartPosition = FormStartPosition.Manual;
        // Dock to the right edge of the primary screen.
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location  = new Point(wa.Right - Width - 20, wa.Top + 80);
        BackColor = Color.FromArgb(30, 30, 30);
        ShowInTaskbar = true;
        MinimumSize   = new Size(320, 400);

        var panel = new CopilotPanel(swApp) { Dock = DockStyle.Fill };
        Controls.Add(panel);
    }
}
