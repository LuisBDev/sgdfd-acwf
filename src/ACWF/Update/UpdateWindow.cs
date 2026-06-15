using global::System.Drawing;
using global::System.Windows.Forms;
using ACWF.WebSocket;

namespace ACWF.Update;

/// <summary>
/// Ventana WinForms no-modal que muestra el estado del update y permite al usuario aplicar un update pendiente.
/// Se abre desde el context menu del tray Icon.
/// </summary>
public sealed class UpdateWindow : Form
{
    private readonly UpdateService _updateService;
    private readonly ISessionGate _sessionGate;

    private readonly Label _lblCurrentVersion;
    private readonly Label _lblAvailableVersion;
    private readonly ProgressBar _pbDownload;
    private readonly Label _lblStatus;
    private readonly Button _btnApply;
    private readonly global::System.Windows.Forms.Timer _refreshTimer;

    public UpdateWindow(UpdateService updateService, ISessionGate sessionGate, string currentVersion)
    {
        _updateService = updateService;
        _sessionGate = sessionGate;

        Text = "ACWF — Update";
        Width = 400;
        Height = 220;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 5,
            ColumnCount = 1
        };

        _lblCurrentVersion = new Label { Text = $"Current version: {currentVersion}", AutoSize = true };
        _lblAvailableVersion = new Label { Text = "Available version: Checking...", AutoSize = true };
        _pbDownload = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 340 };
        _lblStatus = new Label { Text = string.Empty, AutoSize = true, ForeColor = Color.DarkRed };
        _btnApply = new Button { Text = "Apply now", Enabled = false, Width = 120 };

        layout.Controls.Add(_lblCurrentVersion);
        layout.Controls.Add(_lblAvailableVersion);
        layout.Controls.Add(_pbDownload);
        layout.Controls.Add(_lblStatus);
        layout.Controls.Add(_btnApply);

        Controls.Add(layout);

        _btnApply.Click += OnApplyClicked;

        _refreshTimer = new global::System.Windows.Forms.Timer { Interval = 250 };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        int progress = _updateService.LastProgress;
        _pbDownload.Value = Math.Min(progress, 100);

        if (_updateService.HasPendingUpdate)
        {
            _lblStatus.Text = string.Empty;
            _btnApply.Enabled = !_sessionGate.IsActive;
        }
        else
        {
            _btnApply.Enabled = false;
        }
    }

    private void OnApplyClicked(object? sender, EventArgs e)
    {
        if (_sessionGate.IsActive)
        {
            _lblStatus.Text = "A signing session is in progress. Please wait until it completes.";
            return;
        }

        _updateService.ApplyUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
