namespace AgentMeter;

internal sealed class AgentMeterApplicationContext : ApplicationContext
{
    private readonly CodexQuotaReader quotaReader = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer taskbarTimer = new() { Interval = 2_000 };
    private readonly Dictionary<IntPtr, TaskbarMeterForm> meterForms = [];
    private readonly Dictionary<IntPtr, string> reportedFailures = [];
    private readonly HashSet<IntPtr> notifiedFailures = [];
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private Icon trayGraphic;
    private QuotaDisplayState state = QuotaDisplayState.CreateLoading();
    private string? reconciliationFailure;
    private bool refreshing;
    private bool exiting;

    public AgentMeterApplicationContext()
    {
        refreshTimer.Tick += async (_, _) => await RefreshQuotaAsync();
        taskbarTimer.Tick += OnTaskbarTimerTick;

        trayMenu = AgentMeterMenu.Create(
            ToggleDetailsFromTray,
            RefreshQuotaAsync,
            () => ShowSettings(null),
            ExitApplication);
        trayGraphic = ProductIcon.Load();
        trayIcon = new NotifyIcon
        {
            ContextMenuStrip = trayMenu,
            Icon = trayGraphic,
            Text = CreateTrayText(),
            Visible = true
        };
        trayIcon.MouseUp += OnTrayMouseUp;

        ReconcileTaskbars();
        refreshTimer.Start();
        taskbarTimer.Start();
        Application.Idle += OnInitialIdle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnInitialIdle;
            refreshTimer.Dispose();
            taskbarTimer.Dispose();
            CloseAllMeters();
            trayIcon.Visible = false;
            trayIcon.MouseUp -= OnTrayMouseUp;
            trayIcon.Dispose();
            trayMenu.Dispose();
            trayGraphic.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnInitialIdle(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= OnInitialIdle;
        _ = RefreshQuotaAsync();
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            trayMenu.Show(Cursor.Position);
        }
    }

    private void OnTaskbarTimerTick(object? sender, EventArgs eventArgs)
    {
        try
        {
            ReconcileTaskbars();
            reconciliationFailure = null;
        }
        catch (Exception exception)
        {
            if (reconciliationFailure == exception.Message)
            {
                return;
            }

            reconciliationFailure = exception.Message;
            MessageBox.Show(
                $"无法检查 Windows 任务栏变化。AgentMeter 将继续运行并稍后重试。\n\n{exception.Message}",
                "AgentMeter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshQuotaAsync()
    {
        if (refreshing || exiting)
        {
            return;
        }

        refreshing = true;
        ApplyStateToMeters();
        try
        {
            state = QuotaDisplayState.Available(await quotaReader.ReadAsync());
        }
        catch (Exception exception)
        {
            state = QuotaDisplayState.Unavailable(exception.Message);
        }
        finally
        {
            refreshing = false;
            if (!exiting)
            {
                ApplyStateToMeters();
            }
        }
    }

    private void ReconcileTaskbars()
    {
        if (exiting)
        {
            return;
        }

        var taskbars = NativeMethods.FindTaskbarWindows().ToHashSet();
        foreach (var removedHandle in reportedFailures.Keys.Where(handle => !taskbars.Contains(handle)).ToList())
        {
            reportedFailures.Remove(removedHandle);
            notifiedFailures.Remove(removedHandle);
        }

        foreach (var staleHandle in meterForms.Keys.Where(handle => !taskbars.Contains(handle)).ToList())
        {
            RemoveMeter(staleHandle);
        }

        foreach (var taskbar in taskbars)
        {
            if (meterForms.TryGetValue(taskbar, out var existing))
            {
                try
                {
                    existing.UpdateTaskbarBounds();
                    reportedFailures.Remove(taskbar);
                    notifiedFailures.Remove(taskbar);
                }
                catch (Exception exception)
                {
                    RemoveMeter(taskbar);
                    ReportTaskbarFailure(taskbar, exception);
                }

                continue;
            }

            TaskbarMeterForm? meter = null;
            try
            {
                meter = new TaskbarMeterForm(
                    taskbar,
                    state,
                    RefreshQuotaAsync,
                    ShowSettings,
                    ExitApplication);
                meter.Show();
                meter.AttachToTaskbar();
                meter.ApplyState(state, refreshing);
                meterForms.Add(taskbar, meter);
                reportedFailures.Remove(taskbar);
                notifiedFailures.Remove(taskbar);
            }
            catch (Exception exception)
            {
                meter?.Dispose();
                ReportTaskbarFailure(taskbar, exception);
            }
        }

        NotifyReportedFailures();
    }

    private void ReportTaskbarFailure(IntPtr taskbar, Exception exception)
    {
        if (!reportedFailures.TryGetValue(taskbar, out var previous) || previous != exception.Message)
        {
            notifiedFailures.Remove(taskbar);
        }

        reportedFailures[taskbar] = exception.Message;
    }

    private void NotifyReportedFailures()
    {
        if (meterForms.Count == 0)
        {
            return;
        }

        var pending = reportedFailures
            .Where(failure => !notifiedFailures.Contains(failure.Key))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var failure in pending)
        {
            notifiedFailures.Add(failure.Key);
        }

        var detail = string.Join("\n", pending.Select(failure => $"• {failure.Value}"));
        MessageBox.Show(
            $"有 {pending.Count} 个显示器的任务栏无法显示 AgentMeter。程序将继续重试。\n\n{detail}",
            "AgentMeter",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void ApplyStateToMeters()
    {
        foreach (var meter in meterForms.Values)
        {
            meter.ApplyState(state, refreshing);
        }

        trayIcon.Text = CreateTrayText();
    }

    private void ToggleDetailsFromTray()
    {
        var targetScreen = Screen.FromPoint(Cursor.Position);
        var target = meterForms.Values.FirstOrDefault(meter => meter.IsOnScreen(targetScreen))
            ?? meterForms.Values.FirstOrDefault();
        target?.ToggleDetails();
    }

    private static void ShowSettings(TaskbarMeterForm? owner)
    {
        using var settings = new SettingsForm();
        if (owner is null)
        {
            settings.StartPosition = FormStartPosition.CenterScreen;
            settings.ShowDialog();
        }
        else
        {
            settings.ShowDialog(owner);
        }
    }

    private string CreateTrayText()
    {
        var firstLine = state.Tooltip.Split('\n')[0];
        return firstLine.Length <= 63 ? firstLine : firstLine[..63];
    }

    private void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        refreshTimer.Stop();
        taskbarTimer.Stop();
        trayIcon.Visible = false;
        CloseAllMeters();
        ExitThread();
    }

    private void RemoveMeter(IntPtr taskbar)
    {
        if (!meterForms.Remove(taskbar, out var meter))
        {
            return;
        }

        meter.Close();
        meter.Dispose();
    }

    private void CloseAllMeters()
    {
        foreach (var meter in meterForms.Values.ToList())
        {
            meter.Close();
            meter.Dispose();
        }

        meterForms.Clear();
    }
}
