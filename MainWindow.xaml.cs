using System.Windows;
using System.Windows.Threading;
using WorkerService1.OfflineQueue;

namespace WorkerService1;

public partial class MainWindow : Window
{
    private readonly AttendanceLogStore _attendanceLog;
    private readonly DispatcherTimer _refreshTimer;

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
    }

    private void RefreshData()
    {
        var recent = _attendanceLog.GetRecent(100);
        AttendanceGrid.ItemsSource = recent;
        StatusText.Text = $"Status: Running — last refreshed {DateTime.Now:T}";
    }
}