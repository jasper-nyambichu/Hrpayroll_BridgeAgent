using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WorkerService1.OfflineQueue;
using Forms = System.Windows.Forms;

namespace WorkerService1;

public partial class MainWindow : Window
{
    private readonly AttendanceLogStore _attendanceLog;
    private readonly DispatcherTimer _refreshTimer;
    private Forms.NotifyIcon? _trayIcon;
    private bool _reallyExit;

    public MainWindow(AttendanceLogStore attendanceLog)
    {
        InitializeComponent();
        _attendanceLog = attendanceLog;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += (_, _) => RefreshData();
        _refreshTimer.Start();

        RefreshData();
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "HrPayroll Bridge Agent"
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            // Closing the window (the X button) minimizes to tray instead
            // of exiting — the engine keeps running in the background.
            // Only the tray menu's "Exit" genuinely terminates the app.
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon?.Dispose();
        base.OnClosing(e);

        // Genuine exit: stop the hosted engine cleanly, then shut WPF down.
        Program.AppHost?.StopAsync().GetAwaiter().GetResult();
        System.Windows.Application.Current.Shutdown();
    }

    private void RefreshData()
    {
        var recent = _attendanceLog.GetRecent(100);
        AttendanceGrid.ItemsSource = recent;
        StatusText.Text = $"Status: Running — last refreshed {DateTime.Now:T}";
    }
}