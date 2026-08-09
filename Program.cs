using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AgentMeter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            CodexQuotaReader.SelfTest();
            MeterRenderer.SelfTest();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TaskbarMeterForm());
    }
}

internal sealed class TaskbarMeterForm : Form
{
    private readonly ToolTip toolTip = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 60_000 };
    private readonly CodexQuotaReader quotaReader = new();
    private readonly QuotaDetailsForm detailsForm;
    private QuotaDisplayState state = QuotaDisplayState.CreateLoading();
    private bool hovered;
    private bool refreshing;

    public TaskbarMeterForm()
    {
        Text = "AgentMeter";
        AccessibleName = state.Tooltip;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;
        DoubleBuffered = true;

        detailsForm = new QuotaDetailsForm();
        detailsForm.RefreshRequested += async (_, _) => await RefreshQuotaAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开额度详情", null, (_, _) => ToggleDetails());
        menu.Items.Add("立即刷新", null, async (_, _) => await RefreshQuotaAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = menu;

        toolTip.InitialDelay = 250;
        toolTip.ReshowDelay = 100;
        toolTip.AutoPopDelay = 10000;
        UpdateTooltip();

        refreshTimer.Tick += async (_, _) => await RefreshQuotaAsync();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        try
        {
            AttachToTaskbar();
            refreshTimer.Start();
            BeginInvoke(async () => await RefreshQuotaAsync());
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法挂载到 Windows 任务栏。\n\n{exception.Message}",
                "AgentMeter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        MeterRenderer.Draw(eventArgs.Graphics, ClientSize, state, hovered);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        hovered = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs eventArgs)
    {
        base.OnMouseClick(eventArgs);
        if (eventArgs.Button == MouseButtons.Left)
        {
            ToggleDetails();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            toolTip.Dispose();
            detailsForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RefreshQuotaAsync()
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        detailsForm.SetRefreshing(true);
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
            detailsForm.SetRefreshing(false);
            ApplyState();
        }
    }

    private void ApplyState()
    {
        AccessibleName = state.Tooltip;
        detailsForm.ApplyState(state);
        UpdateTooltip();
        Invalidate();
    }

    private void ToggleDetails()
    {
        if (detailsForm.Visible)
        {
            detailsForm.Hide();
            return;
        }

        detailsForm.ApplyState(state);
        var meterBounds = RectangleToScreen(ClientRectangle);
        var screen = Screen.FromRectangle(meterBounds).WorkingArea;
        var x = Math.Clamp(meterBounds.Left, screen.Left + 8, screen.Right - detailsForm.Width - 8);
        var y = Math.Max(screen.Top + 8, meterBounds.Top - detailsForm.Height - 8);
        detailsForm.Location = new Point(x, y);
        detailsForm.Show();
        detailsForm.Activate();
    }

    private void UpdateTooltip() => toolTip.SetToolTip(this, state.Tooltip);

    private void AttachToTaskbar()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            throw new Win32Exception("找不到主任务栏窗口 Shell_TrayWnd。");
        }

        var style = NativeMethods.GetWindowStyle(Handle);
        NativeMethods.SetWindowStyle(Handle, (style & ~NativeMethods.WsPopup) | NativeMethods.WsChild);

        Marshal.SetLastPInvokeError(0);
        var previousParent = NativeMethods.SetParent(Handle, taskbar);
        var error = Marshal.GetLastPInvokeError();
        if (previousParent == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error);
        }

        if (!NativeMethods.GetClientRect(taskbar, out var taskbarRect))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var height = taskbarRect.Bottom - taskbarRect.Top;
        var width = (int)Math.Round(height * 2.25);
        var margin = Math.Max(8, (int)Math.Round(height * 0.14));
        if (!NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTop,
                margin,
                0,
                width,
                height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

internal sealed class QuotaDetailsForm : Form
{
    private QuotaDisplayState state = QuotaDisplayState.CreateLoading();
    private bool refreshing;
    private Rectangle refreshBounds;

    public QuotaDetailsForm()
    {
        Text = "Codex 额度";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(35, 35, 35);
        DoubleBuffered = true;
        Cursor = Cursors.Default;
        _ = Handle;
        Hide();
        ApplyState(state);
    }

    public event EventHandler? RefreshRequested;

    public void ApplyState(QuotaDisplayState newState)
    {
        state = newState;
        var rowCount = Math.Max(1, state.Snapshot?.Windows.Count ?? 0);
        var scale = GetDpiScale();
        ClientSize = new Size(
            (int)Math.Round(480 * scale),
            (int)Math.Round((88 + rowCount * 112 + 80) * scale));
        AccessibleName = state.Tooltip;
        Invalidate();
    }

    public void SetRefreshing(bool value)
    {
        refreshing = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var scale = graphics.DpiX / 96f;

        using var titleFont = new Font("Segoe UI Semibold", 13.5f);
        using var bodyFont = new Font("Segoe UI", 10.5f);
        using var valueFont = new Font("Segoe UI Semibold", 11.5f);
        using var textBrush = new SolidBrush(Color.FromArgb(240, 240, 240));
        using var mutedBrush = new SolidBrush(Color.FromArgb(175, 175, 175));
        using var separator = new Pen(Color.FromArgb(65, 255, 255, 255));

        graphics.DrawString("Codex 额度", titleFont, textBrush, 28 * scale, 24 * scale);
        DrawConnectionStatus(graphics, bodyFont, scale);

        var top = 88f * scale;
        if (state.Snapshot is { } snapshot)
        {
            foreach (var window in snapshot.Windows)
            {
                DrawQuotaRow(graphics, window, top, scale, bodyFont, valueFont, textBrush, mutedBrush);
                top += 112 * scale;
            }
        }
        else
        {
            using var unavailableBrush = new SolidBrush(Color.FromArgb(255, 190, 105));
            graphics.DrawString(
                state.Loading ? "正在读取本机 Codex 额度…" : "无法读取 Codex 额度，不显示缓存或模拟数据。",
                bodyFont,
                state.Loading ? mutedBrush : unavailableBrush,
                new RectangleF(28 * scale, top + 16 * scale, ClientSize.Width - 56 * scale, 58 * scale));
            top += 112 * scale;
        }

        graphics.DrawLine(separator, 28 * scale, top + 4 * scale, ClientSize.Width - 28 * scale, top + 4 * scale);
        var source = state.Snapshot is { } current
            ? $"本机 Codex · {FormatFreshness(current.UpdatedAt)}"
            : "本机 Codex · 已断开";
        graphics.DrawString(source, bodyFont, mutedBrush, 28 * scale, top + 31 * scale);

        refreshBounds = new Rectangle(
            ClientSize.Width - (int)Math.Round(104 * scale),
            (int)Math.Round(top + 21 * scale),
            (int)Math.Round(76 * scale),
            (int)Math.Round(40 * scale));
        using var refreshBackground = new SolidBrush(Color.FromArgb(48, 255, 255, 255));
        using var refreshPath = MeterRenderer.RoundedRectangle(refreshBounds, 7 * scale);
        graphics.FillPath(refreshBackground, refreshPath);
        var refreshText = refreshing ? "刷新中" : "刷新";
        var refreshSize = graphics.MeasureString(refreshText, bodyFont);
        graphics.DrawString(
            refreshText,
            bodyFont,
            textBrush,
            refreshBounds.Left + (refreshBounds.Width - refreshSize.Width) / 2,
            refreshBounds.Top + (refreshBounds.Height - refreshSize.Height) / 2);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        Cursor = refreshBounds.Contains(eventArgs.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs eventArgs)
    {
        base.OnMouseClick(eventArgs);
        if (eventArgs.Button == MouseButtons.Left && refreshBounds.Contains(eventArgs.Location) && !refreshing)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrawConnectionStatus(Graphics graphics, Font font, float scale)
    {
        var connected = state.Snapshot is not null;
        var color = connected ? Color.FromArgb(100, 210, 135) : Color.FromArgb(255, 184, 95);
        var label = connected ? "实时" : state.Loading ? "连接中" : "断开";
        using var brush = new SolidBrush(color);
        var size = graphics.MeasureString(label, font);
        var labelX = ClientSize.Width - 28 * scale - size.Width;
        graphics.FillEllipse(brush, labelX - 14 * scale, 32 * scale, 7 * scale, 7 * scale);
        graphics.DrawString(label, font, brush, labelX, 24 * scale);
    }

    private static void DrawQuotaRow(
        Graphics graphics,
        QuotaWindow window,
        float top,
        float scale,
        Font bodyFont,
        Font valueFont,
        Brush textBrush,
        Brush mutedBrush)
    {
        graphics.DrawString(window.Label, bodyFont, mutedBrush, 28 * scale, top);
        var percentText = $"{window.RemainingPercent}%";
        var percentSize = graphics.MeasureString(percentText, valueFont);
        graphics.DrawString(percentText, valueFont, textBrush, 452 * scale - percentSize.Width, top);

        var track = new Rectangle(
            (int)Math.Round(28 * scale),
            (int)Math.Round(top + 38 * scale),
            (int)Math.Round(424 * scale),
            Math.Max(8, (int)Math.Round(8 * scale)));
        using var trackBrush = new SolidBrush(Color.FromArgb(75, 255, 255, 255));
        using var valueBrush = new SolidBrush(MeterRenderer.StatusColor(window.RemainingPercent));
        using var trackPath = MeterRenderer.RoundedRectangle(track, 4 * scale);
        graphics.FillPath(trackBrush, trackPath);
        if (window.RemainingPercent > 0)
        {
            var value = new Rectangle(
                track.X,
                track.Y,
                Math.Max((int)Math.Round(7 * scale), track.Width * window.RemainingPercent / 100),
                track.Height);
            using var valuePath = MeterRenderer.RoundedRectangle(value, 4 * scale);
            graphics.FillPath(valueBrush, valuePath);
        }

        graphics.DrawString($"已用 {window.UsedPercent}%", bodyFont, mutedBrush, 28 * scale, top + 63 * scale);
        var resetText = $"{FormatReset(window.ResetsAt)}重置";
        var resetSize = graphics.MeasureString(resetText, bodyFont);
        graphics.DrawString(resetText, bodyFont, mutedBrush, 452 * scale - resetSize.Width, top + 63 * scale);
    }

    private float GetDpiScale()
    {
        using var graphics = CreateGraphics();
        return Math.Max(1f, graphics.DpiX / 96f);
    }

    private static string FormatReset(DateTimeOffset reset)
    {
        var local = reset.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm ")
            : local.ToString("M 月 d 日 ");
    }

    private static string FormatFreshness(DateTimeOffset updatedAt)
    {
        var age = DateTimeOffset.Now - updatedAt;
        return age < TimeSpan.FromMinutes(1) ? "刚刚更新" : $"{Math.Max(1, (int)age.TotalMinutes)} 分钟前更新";
    }
}

internal static class MeterRenderer
{
    public static void Draw(Graphics graphics, Size size, QuotaDisplayState state, bool hovered)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var scale = size.Height / 48f;
        using var backgroundPath = RoundedRectangle(
            new RectangleF(2f * scale, 5f * scale, size.Width - 4f * scale, size.Height - 10f * scale),
            7f * scale);
        using var background = new SolidBrush(hovered ? Color.FromArgb(58, 58, 58) : Color.FromArgb(42, 42, 42));
        graphics.FillPath(background, backgroundPath);

        var iconSize = 22f * scale;
        var iconBounds = new RectangleF(8f * scale, (size.Height - iconSize) / 2f, iconSize, iconSize);
        using var trackPen = new Pen(Color.FromArgb(90, 255, 255, 255), 2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var valuePen = new Pen(StatusColor(state.Metric?.RemainingPercent), 2.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(trackPen, iconBounds, 135, 270);
        if (state.Metric is { } metric)
        {
            graphics.DrawArc(valuePen, iconBounds, 135, Math.Max(7, 270f * metric.RemainingPercent / 100f));
        }

        var number = state.Metric is { } current ? $"{current.RemainingPercent}%" : state.Loading ? "…" : "—";
        using var numberFont = new Font("Segoe UI Semibold", 17f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var contextFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        using var mutedBrush = new SolidBrush(Color.FromArgb(185, 205, 215));
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var textLeft = iconBounds.Right + 7f * scale;
        var numberSize = graphics.MeasureString(number, numberFont, PointF.Empty, format);
        var numberTop = (size.Height - numberSize.Height) / 2f;
        graphics.DrawString(number, numberFont, brush, new PointF(textLeft, numberTop), format);

        if (state.Metric is { } window)
        {
            var contextTop = numberTop + numberSize.Height - graphics.MeasureString(window.ShortLabel, contextFont).Height;
            graphics.DrawString(window.ShortLabel, contextFont, mutedBrush, new PointF(textLeft + numberSize.Width + 5f * scale, contextTop), format);
        }
    }

    public static void SelfTest()
    {
        var available = QuotaDisplayState.Available(new QuotaSnapshot(
            [new QuotaWindow("1 周", "周", 1, 99, DateTimeOffset.Now.AddDays(7))],
            DateTimeOffset.Now));
        foreach (var state in new[] { available, QuotaDisplayState.CreateLoading(), QuotaDisplayState.Unavailable("test") })
        {
            using var bitmap = new Bitmap(190, 84);
            using var graphics = Graphics.FromImage(bitmap);
            Draw(graphics, bitmap.Size, state, hovered: false);
            Draw(graphics, bitmap.Size, state, hovered: true);
        }
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, float radius) =>
        RoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color StatusColor(int? remaining) => remaining switch
    {
        >= 50 => Color.FromArgb(82, 195, 120),
        >= 20 => Color.FromArgb(235, 170, 70),
        >= 0 => Color.FromArgb(245, 95, 95),
        _ => Color.FromArgb(150, 150, 150)
    };
}

internal sealed record QuotaDisplayState(QuotaSnapshot? Snapshot, bool Loading, string? Error)
{
    public QuotaWindow? Metric => Snapshot?.Windows.OrderBy(window => window.RemainingPercent).FirstOrDefault();

    public string Tooltip => Snapshot is { } snapshot
        ? string.Join("\n", snapshot.Windows.Select(window =>
              $"Codex {window.Label}剩余 {window.RemainingPercent}% | {window.ResetsAt.ToLocalTime():M 月 d 日 HH:mm} 重置"))
          + $"\n本机 Codex | {snapshot.UpdatedAt:HH:mm:ss} 更新"
        : Loading ? "正在读取本机 Codex 额度" : $"Codex 额度暂不可用 | {Error}";

    public static QuotaDisplayState Available(QuotaSnapshot snapshot) => new(snapshot, false, null);
    public static QuotaDisplayState CreateLoading() => new(null, true, null);
    public static QuotaDisplayState Unavailable(string error) => new(null, false, error);
}

internal static class NativeMethods
{
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const long WsChild = 0x40000000L;
    public const long WsPopup = 0x80000000L;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public static readonly IntPtr HwndTop = IntPtr.Zero;

    private const int GwlStyle = -16;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public static long GetWindowStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, GwlStyle).ToInt64()
        : GetWindowLong32(window, GwlStyle);

    public static void SetWindowStyle(IntPtr window, long style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(window, GwlStyle, new IntPtr(style));
        }
        else
        {
            SetWindowLong32(window, GwlStyle, (int)style);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int newValue);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
