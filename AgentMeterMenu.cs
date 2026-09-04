namespace AgentMeter;

internal static class AgentMeterMenu
{
    public static ContextMenuStrip Create(
        Action toggleDetails,
        Func<Task> refreshRequested,
        Action settingsRequested,
        Action exitRequested)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            Padding = new Padding(4)
        };

        menu.Items.Add("打开额度详情", null, (_, _) => toggleDetails());
        menu.Items.Add("立即刷新", null, async (_, _) => await refreshRequested());
        menu.Items.Add("设置…", null, (_, _) => settingsRequested());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitRequested());
        return menu;
    }
}

internal static class ProductIcon
{
    public static Icon Load()
    {
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
    }

    public static void SelfTest()
    {
        using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? throw new InvalidOperationException("AgentMeter 可执行文件未包含产品图标。");
        _ = icon.Handle;
    }
}
