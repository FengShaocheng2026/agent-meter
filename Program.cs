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
            MeterRenderer.SelfTest();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TaskbarMeterForm());
    }
}

internal sealed class TaskbarMeterForm : Form
{
    private static readonly ProbeState[] States =
    [
        new(100, "Codex 剩余 100% | 1 周额度 | 8 月 16 日重置"),
        new(84, "Codex 剩余 84% | 1 周额度 | 8 月 16 日重置"),
        new(7, "Codex 剩余 7% | 1 周额度 | 8 月 16 日重置"),
        new(null, "Codex 额度暂不可用")
    ];

    private readonly ToolTip toolTip = new();
    private readonly ToolStripMenuItem systemStyleItem;
    private readonly ToolStripMenuItem accentStyleItem;
    private int stateIndex = 1;
    private MeterStyle meterStyle = MeterStyle.SystemMinimal;
    private bool hovered;

    public TaskbarMeterForm()
    {
        Text = "AgentMeter taskbar probe";
        AccessibleName = States[stateIndex].Tooltip;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;
        DoubleBuffered = true;

        var menu = new ContextMenuStrip();
        for (var index = 0; index < States.Length; index++)
        {
            var selectedIndex = index;
            menu.Items.Add($"显示 {States[index].Label}", null, (_, _) => ApplyState(selectedIndex));
        }

        menu.Items.Add(new ToolStripSeparator());
        systemStyleItem = new ToolStripMenuItem("样式：系统极简", null, (_, _) => ApplyStyle(MeterStyle.SystemMinimal));
        accentStyleItem = new ToolStripMenuItem("样式：状态强调", null, (_, _) => ApplyStyle(MeterStyle.StatusAccent));
        menu.Items.Add(systemStyleItem);
        menu.Items.Add(accentStyleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = menu;
        UpdateStyleChecks();

        toolTip.InitialDelay = 250;
        toolTip.ReshowDelay = 100;
        toolTip.AutoPopDelay = 10000;
        UpdateTooltip();
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
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法挂载到 Windows 任务栏。\n\n{exception.Message}",
                "AgentMeter 探针",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        MeterRenderer.Draw(eventArgs.Graphics, ClientSize, States[stateIndex], meterStyle, hovered);
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
            ApplyState((stateIndex + 1) % States.Length);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyState(int index)
    {
        stateIndex = index;
        AccessibleName = States[stateIndex].Tooltip;
        UpdateTooltip();
        Invalidate();
    }

    private void ApplyStyle(MeterStyle style)
    {
        meterStyle = style;
        UpdateStyleChecks();
        Invalidate();
    }

    private void UpdateStyleChecks()
    {
        systemStyleItem.Checked = meterStyle == MeterStyle.SystemMinimal;
        accentStyleItem.Checked = meterStyle == MeterStyle.StatusAccent;
    }

    private void UpdateTooltip() => toolTip.SetToolTip(this, States[stateIndex].Tooltip);

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
        var width = (int)Math.Round(height * 1.85);
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

internal sealed record ProbeState(int? Remaining, string Tooltip)
{
    public string Label => Remaining?.ToString() ?? "--";
}

internal enum MeterStyle
{
    SystemMinimal,
    StatusAccent
}

internal static class MeterRenderer
{
    public static void Draw(Graphics graphics, Size size, ProbeState state, MeterStyle style, bool hovered)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var scale = size.Height / 48f;
        if (style == MeterStyle.StatusAccent || hovered)
        {
            using var backgroundPath = RoundedRectangle(
                new RectangleF(2f * scale, 5f * scale, size.Width - 4f * scale, size.Height - 10f * scale),
                7f * scale);
            using var background = new SolidBrush(style == MeterStyle.StatusAccent
                ? Color.FromArgb(38, 38, 38)
                : Color.FromArgb(54, 54, 54));
            graphics.FillPath(background, backgroundPath);
        }

        var iconSize = 22f * scale;
        var iconBounds = new RectangleF(8f * scale, (size.Height - iconSize) / 2f, iconSize, iconSize);

        using var trackPen = new Pen(Color.FromArgb(90, 255, 255, 255), 2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var valuePen = new Pen(
            style == MeterStyle.StatusAccent ? StatusColor(state.Remaining) : Color.White,
            2.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(trackPen, iconBounds, 135, 270);
        if (state.Remaining is int remaining)
        {
            graphics.DrawArc(valuePen, iconBounds, 135, Math.Max(7, 270f * remaining / 100f));
        }

        var number = state.Label;
        using var numberFont = new Font("Segoe UI Semibold", 19f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var percentFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var textLeft = iconBounds.Right + 7f * scale;
        var numberSize = graphics.MeasureString(number, numberFont, PointF.Empty, format);
        var numberTop = (size.Height - numberSize.Height) / 2f;
        graphics.DrawString(number, numberFont, brush, new PointF(textLeft, numberTop), format);

        if (state.Remaining is not null)
        {
            var percentSize = graphics.MeasureString("%", percentFont, PointF.Empty, format);
            var percentTop = numberTop + numberSize.Height - percentSize.Height - 1f * scale;
            graphics.DrawString("%", percentFont, brush, new PointF(textLeft + numberSize.Width, percentTop), format);
        }
    }

    public static void SelfTest()
    {
        foreach (var state in new[]
                 {
                     new ProbeState(100, "test"),
                     new ProbeState(84, "test"),
                     new ProbeState(7, "test"),
                     new ProbeState(null, "test")
                 })
        {
            foreach (var style in Enum.GetValues<MeterStyle>())
            {
                using var bitmap = new Bitmap(155, 84);
                using var graphics = Graphics.FromImage(bitmap);
                Draw(graphics, bitmap.Size, state, style, hovered: false);
                Draw(graphics, bitmap.Size, state, style, hovered: true);
            }
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
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

    private static Color StatusColor(int? remaining) => remaining switch
    {
        >= 50 => Color.FromArgb(50, 205, 120),
        >= 20 => Color.FromArgb(255, 183, 77),
        >= 0 => Color.FromArgb(255, 93, 93),
        _ => Color.FromArgb(170, 170, 170)
    };
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
