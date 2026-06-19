// CopilotPanel.cs — WinForms UserControl hosted in the SolidWorks Task Pane.
//
// Layout:
//   ┌─────────────────────────────┐
//   │  chat history (scrollable)  │
//   ├─────────────────────────────┤
//   │  [input box]     [Send ▶]   │
//   └─────────────────────────────┘

using System.Drawing;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorksCopilot.Claude;
using SolidWorksCopilot.Scripting;

namespace SolidWorksCopilot.UI;

// Plain WinForms control hosted inside CopilotForm. Constructed directly
// (not via COM), so no COM/ActiveX attributes are needed.
public class CopilotPanel : UserControl
{
    private readonly ISldWorks _swApp;
    private readonly ClaudeClient _claude;

    private RichTextBox _chatBox  = null!;
    private TextBox     _inputBox = null!;
    private Button      _sendBtn  = null!;
    private Label       _statusLbl = null!;

    public CopilotPanel(ISldWorks swApp)
    {
        _swApp  = swApp;
        _claude = new ClaudeClient(swApp);
        Build();
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void Build()
    {
        BackColor = Color.FromArgb(30, 30, 30);
        Dock      = DockStyle.Fill;
        Padding   = new Padding(6);

        // Chat history
        _chatBox = new RichTextBox
        {
            Dock      = DockStyle.Fill,
            ReadOnly  = true,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font      = new Font("Segoe UI", 9f),
            BorderStyle = BorderStyle.None,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
        };

        // Status line
        _statusLbl = new Label
        {
            Dock      = DockStyle.Bottom,
            Height    = 18,
            ForeColor = Color.FromArgb(120, 120, 120),
            Font      = new Font("Segoe UI", 7.5f),
            Text      = "Ready",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(2, 0, 0, 0),
        };

        // Input row
        var inputPanel = new Panel
        {
            Dock   = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(0, 4, 0, 0),
        };

        _sendBtn = new Button
        {
            Text      = "Send",
            Dock      = DockStyle.Right,
            Width     = 60,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f),
        };
        _sendBtn.FlatAppearance.BorderSize = 0;
        _sendBtn.Click += OnSend;

        _inputBox = new TextBox
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                OnSend(null, EventArgs.Empty);
            }
        };

        inputPanel.Controls.Add(_inputBox);
        inputPanel.Controls.Add(_sendBtn);

        Controls.Add(_chatBox);
        Controls.Add(_statusLbl);
        Controls.Add(inputPanel);

        AppendSystem("AI Bob is ready. Describe a part or ask me to modify the active model.");
    }

    // ── Send handler ──────────────────────────────────────────────────────────

    private async void OnSend(object? sender, EventArgs e)
    {
        var text = _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _inputBox.Clear();
        SetBusy(true);
        AppendUser(text);

        try
        {
            await _claude.ChatAsync(text, AppendAssistant, OnScriptReady);
        }
        catch (Exception ex)
        {
            AppendError($"Error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Script execution callback (called by ClaudeClient on tool use) ────────

    private async Task<string> OnScriptReady(string code)
    {
        SetStatus("Running script…");
        AppendCode(code);
        var result = await ScriptRunner.RunAsync(code, _swApp);
        if (result.Success)
            AppendSystem($"✓ Script executed successfully.");
        else
            AppendError($"Script error: {result.Error}");
        return result.Success ? "success" : $"error: {result.Error}";
    }

    // ── Chat display helpers ──────────────────────────────────────────────────

    private void AppendUser(string msg)      => Append("You",      msg,   Color.FromArgb(100, 180, 255));
    private void AppendAssistant(string msg) => Append("AI Bob",   msg,   Color.FromArgb(100, 220, 130));
    private void AppendSystem(string msg)    => Append("System",   msg,   Color.FromArgb(160, 160, 160));
    private void AppendError(string msg)     => Append("Error",    msg,   Color.FromArgb(255, 100, 100));

    private void AppendCode(string code)
    {
        if (_chatBox.InvokeRequired) { _chatBox.Invoke(() => AppendCode(code)); return; }
        _chatBox.SelectionStart  = _chatBox.TextLength;
        _chatBox.SelectionColor  = Color.FromArgb(200, 200, 100);
        _chatBox.SelectionFont   = new Font("Consolas", 8f);
        _chatBox.AppendText($"[Script]\n{code.Trim()}\n\n");
        _chatBox.SelectionColor  = Color.FromArgb(220, 220, 220);
        _chatBox.SelectionFont   = new Font("Segoe UI", 9f);
        _chatBox.ScrollToCaret();
    }

    private void Append(string role, string msg, Color roleColor)
    {
        if (_chatBox.InvokeRequired) { _chatBox.Invoke(() => Append(role, msg, roleColor)); return; }
        _chatBox.SelectionStart  = _chatBox.TextLength;
        _chatBox.SelectionColor  = roleColor;
        _chatBox.SelectionFont   = new Font("Segoe UI", 9f, FontStyle.Bold);
        _chatBox.AppendText($"{role}: ");
        _chatBox.SelectionColor  = Color.FromArgb(220, 220, 220);
        _chatBox.SelectionFont   = new Font("Segoe UI", 9f);
        _chatBox.AppendText($"{msg}\n\n");
        _chatBox.ScrollToCaret();
    }

    private void SetBusy(bool busy)
    {
        if (_sendBtn.InvokeRequired) { _sendBtn.Invoke(() => SetBusy(busy)); return; }
        _sendBtn.Enabled  = !busy;
        _inputBox.Enabled = !busy;
        SetStatus(busy ? "Thinking…" : "Ready");
    }

    private void SetStatus(string s)
    {
        if (_statusLbl.InvokeRequired) { _statusLbl.Invoke(() => _statusLbl.Text = s); return; }
        _statusLbl.Text = s;
    }
}
