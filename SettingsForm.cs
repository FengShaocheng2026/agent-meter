using Microsoft.Win32;

namespace AgentMeter;

internal sealed class SettingsForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(30, 30, 30);
    private static readonly Color CardBackground = Color.FromArgb(43, 43, 43);
    private static readonly Color PrimaryText = Color.FromArgb(245, 245, 245);
    private static readonly Color SecondaryText = Color.FromArgb(175, 175, 175);
    private static readonly Color SuccessText = Color.FromArgb(110, 210, 145);
    private static readonly Color ErrorText = Color.FromArgb(255, 145, 120);

    private readonly ToggleSwitch startWithWindows = new()
    {
        AccessibleName = "登录 Windows 后自动启动 AgentMeter",
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Location = new Point(530, 42)
    };
    private readonly Label statusLabel = new()
    {
        ForeColor = SecondaryText,
        Location = new Point(24, 250),
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        AutoEllipsis = true,
        AutoSize = false,
        Size = new Size(480, 40),
        Text = "更改会立即生效"
    };
    private readonly Icon productIcon = ProductIcon.Load();
    private bool revertingChange;

    public SettingsForm()
    {
        Text = "AgentMeter 设置";
        Icon = productIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 360);
        MinimumSize = new Size(560, 340);
        BackColor = WindowBackground;
        ForeColor = PrimaryText;

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = PrimaryText,
            Location = new Point(20, 18),
            Text = "设置"
        };
        var subtitle = new Label
        {
            AutoSize = true,
            ForeColor = SecondaryText,
            Location = new Point(23, 60),
            Text = "管理 AgentMeter 在这台电脑上的行为"
        };
        var card = new Panel
        {
            BackColor = CardBackground,
            Location = new Point(20, 105),
            Size = new Size(600, 120),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        var settingTitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = PrimaryText,
            Location = new Point(18, 22),
            Text = "登录时启动"
        };
        var description = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = SecondaryText,
            Location = new Point(18, 62),
            Size = new Size(490, 36),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            Text = "登录当前 Windows 用户后自动运行 AgentMeter"
        };
        var closeButton = new Button
        {
            Text = "完成",
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(58, 58, 58),
            ForeColor = PrimaryText,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(524, 306),
            Size = new Size(96, 32),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            UseVisualStyleBackColor = false
        };
        closeButton.FlatAppearance.BorderSize = 0;

        card.Controls.Add(settingTitle);
        card.Controls.Add(description);
        card.Controls.Add(startWithWindows);
        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(card);
        Controls.Add(statusLabel);
        Controls.Add(closeButton);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        LoadStartupState();
        startWithWindows.CheckedChanged += OnStartupChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            productIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    public static void SelfTest()
    {
        using var form = new SettingsForm();
        _ = form.Handle;
        foreach (var scale in new[] { 1f, 2f })
        {
            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();
            AssertControlsFit(form);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
        }
    }

    private static void AssertControlsFit(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (!parent.ClientRectangle.Contains(control.Bounds))
            {
                throw new InvalidOperationException($"Settings layout self-test failed: {control.GetType().Name} is outside its parent.");
            }

            AssertControlsFit(control);
        }
    }

    private void LoadStartupState()
    {
        try
        {
            startWithWindows.Checked = StartupManager.IsEnabled();
        }
        catch (Exception exception)
        {
            startWithWindows.Enabled = false;
            statusLabel.ForeColor = ErrorText;
            statusLabel.Text = $"无法读取自启动状态：{exception.Message}";
        }
    }

    private void OnStartupChanged(object? sender, EventArgs eventArgs)
    {
        if (revertingChange)
        {
            return;
        }

        var enabled = startWithWindows.Checked;
        try
        {
            StartupManager.SetEnabled(enabled);
            statusLabel.ForeColor = SuccessText;
            statusLabel.Text = enabled ? "已开启登录时启动" : "已关闭登录时启动";
        }
        catch (Exception exception)
        {
            revertingChange = true;
            startWithWindows.Checked = !enabled;
            revertingChange = false;
            statusLabel.ForeColor = ErrorText;
            statusLabel.Text = "更改未保存";
            MessageBox.Show(
                this,
                $"无法更改自启动设置。\n\n{exception.Message}",
                "AgentMeter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        AutoSize = false;
        Size = new Size(52, 28);
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Parent?.BackColor ?? Color.FromArgb(43, 43, 43));

        var track = new RectangleF(1, 2, Width - 2, Height - 4);
        using var trackPath = MeterRenderer.RoundedRectangle(track, track.Height / 2);
        using var trackBrush = new SolidBrush(
            Checked ? Color.FromArgb(0, 120, 212) : Color.FromArgb(92, 92, 92));
        graphics.FillPath(trackBrush, trackPath);

        var thumbSize = Height - 10;
        var thumbX = Checked ? Width - thumbSize - 5 : 5;
        using var thumbBrush = new SolidBrush(Color.White);
        graphics.FillEllipse(thumbBrush, thumbX, 5, thumbSize, thumbSize);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, ClientRectangle, ForeColor, BackColor);
        }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentMeter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的启动项注册表。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法确定 AgentMeter 的程序路径。");
        }

        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
