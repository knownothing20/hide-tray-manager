using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Hide Tray Manager")]
[assembly: System.Reflection.AssemblyDescription("Manage Windows application windows from the system tray.")]
[assembly: System.Reflection.AssemblyProduct("Hide Tray Manager")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 高 DPI 屏上按真实 DPI 渲染（清晰），布局由窗体 AutoScaleMode.Dpi 缩放。
        User32.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayProxyContext());
    }
}

internal sealed class TrayProxyContext : ApplicationContext
{
    private readonly string configPath;
    private readonly NotifyIcon managerIcon;
    private readonly ContextMenuStrip managerMenu;
    private readonly Timer timer;
    private readonly List<AppTray> appTrays = new List<AppTray>();
    private int checkIntervalMs = 700;
    private ConfigurationForm configurationForm;
    private Dictionary<string, List<Process>> processCache = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch snapshotClock = Stopwatch.StartNew();
    private long lastSnapshotMs = -3000;

    public TrayProxyContext()
    {
        configPath = AppConfiguration.GetPath(AppDomain.CurrentDomain.BaseDirectory);
        AppConfiguration.EnsureExists(configPath);
        LoadConfig();

        managerMenu = new ContextMenuStrip();
        managerIcon = new NotifyIcon();
        managerIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        managerIcon.Text = "Hide Tray Manager";
        managerIcon.ContextMenuStrip = managerMenu;
        managerIcon.Visible = true;
        BuildManagerMenu();
        // 托盘菜单打开前按最新运行状态重建（应用增减/启停即时反映）。
        managerMenu.Opening += delegate { BuildManagerMenu(); };

        timer = new Timer();
        timer.Interval = checkIntervalMs;
        timer.Tick += delegate { Tick(); };
        timer.Enabled = appTrays.Count > 0;

        foreach (var tray in appTrays)
            tray.StartIfConfigured();

        // 支持 --config 参数直接打开配置窗口（也便于自动化验证界面）。
        if (Environment.GetCommandLineArgs().Any(arg => arg.Equals("--config", StringComparison.OrdinalIgnoreCase)))
            OpenConfiguration();
    }

    private void LoadConfig()
    {
        var text = File.ReadAllText(configPath, Encoding.UTF8);
        AppConfiguration.Validate(text);
        var ini = IniFile.Parse(text);
        ClearProcessCache();
        foreach (var tray in appTrays)
            tray.Dispose();
        appTrays.Clear();

        checkIntervalMs = ini.GetInt("General", "CheckIntervalMs", 700);

        foreach (var section in ini.SectionNames.Where(s => !s.Equals("General", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ini.GetBool(section, "Enabled", true))
                continue;

            var path = ini.Get(section, "Path", "");
            var exe = ini.Get(section, "Exe", "");
            if (string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(path))
                exe = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(exe))
                continue;

            appTrays.Add(new AppTray(new ManagedApp
            {
                Section = section,
                Name = ini.Get(section, "Name", section),
                Path = path,
                Exe = exe,
                ProcessPath = ini.Get(section, "ProcessPath", ""),
                TitleContains = ini.Get(section, "TitleContains", ""),
                AutoStart = ini.GetBool(section, "AutoStart", false),
                HideOnLaunch = ini.GetBool(section, "HideOnLaunch", true),
                HideWhenMinimized = ini.GetBool(section, "HideWhenMinimized", true),
                DisableCloseButton = ini.GetBool(section, "DisableCloseButton", true),
                ShowNotifications = ini.GetBool(section, "ShowNotifications", false),
                IconPath = ini.Get(section, "IconPath", "")
            }));
        }

        if (timer != null)
        {
            timer.Interval = checkIntervalMs;
            timer.Enabled = appTrays.Count > 0;
        }
        if (managerMenu != null)
            BuildManagerMenu();
    }

    private void Tick()
    {
        if (appTrays.Count == 0)
            return;
        // Query only configured names and share results between matching rules.
        // Discover new processes at most once per three seconds. Window state
        // still follows the configured poll interval while target apps run.
        if (snapshotClock.ElapsedMilliseconds - lastSnapshotMs >= 3000)
        {
            var updated = ProcessSnapshot.FindByName(appTrays.Select(tray => tray.ProcessName));
            ClearProcessCache();
            processCache = updated;
            lastSnapshotMs = snapshotClock.ElapsedMilliseconds;
        }
        foreach (var tray in appTrays)
            tray.Tick(processCache);
        timer.Interval = processCache.Values.SelectMany(matches => matches).Any(ProcessSnapshot.IsRunning)
            ? checkIntervalMs : Math.Max(checkIntervalMs, 3000);
        UpdateManagerMenu();
    }

    private void ClearProcessCache()
    {
        foreach (var process in processCache.Values.SelectMany(matches => matches))
            process.Dispose();
        processCache.Clear();
        lastSnapshotMs = -3000;
    }

    private void BuildManagerMenu()
    {
        managerMenu.Items.Clear();
        // 每次打开菜单时按当前运行状态重建应用区：只显示正在运行的托管软件，
        // 未运行的不显示；每项点击即切换显示/隐藏。
        var running = appTrays.Where(tray => tray.Status != AppStatus.NotRunning).ToList();
        if (running.Count == 0)
        {
            managerMenu.Items.Add(new ToolStripMenuItem("没有运行中的托管软件") { Enabled = false });
        }
        else
        {
            foreach (var tray in running)
            {
                var captured = tray;
                var item = new ToolStripMenuItem(captured.MenuLabel);
                item.Click += delegate { captured.ToggleWindow(); };
                managerMenu.Items.Add(item);
            }
        }

        managerMenu.Items.Add(new ToolStripSeparator());
        managerMenu.Items.Add("配置与窗口探测...", null, delegate { OpenConfiguration(); });
        managerMenu.Items.Add("打开配置文件", null, delegate { Process.Start("notepad.exe", "\"" + configPath + "\""); });
        managerMenu.Items.Add("重新加载配置", null, delegate {
            try { LoadConfig(); }
            catch (Exception) { MessageBox.Show("配置加载失败，请检查配置格式和检查间隔。", "Hide Tray Manager"); }
        });
        managerMenu.Items.Add(new ToolStripSeparator());
        managerMenu.Items.Add("退出 Hide Tray Manager", null, delegate { ExitThread(); });
    }

    private void OpenConfiguration()
    {
        if (configurationForm == null || configurationForm.IsDisposed)
            configurationForm = new ConfigurationForm(configPath, LoadConfig);
        configurationForm.Show();
        if (configurationForm.WindowState == FormWindowState.Minimized)
            configurationForm.WindowState = FormWindowState.Normal;
        configurationForm.Activate();
    }

    private void UpdateManagerMenu()
    {
        var runningCount = appTrays.Count(tray => tray.Status != AppStatus.NotRunning);
        var tooltip = runningCount == 0
            ? "Hide Tray Manager · 无运行中的应用"
            : TruncateTooltip("Hide Tray Manager · " + runningCount + " 个应用运行中");
        if (managerIcon.Text != tooltip)
            managerIcon.Text = tooltip;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (configurationForm != null)
                configurationForm.Dispose();
            ClearProcessCache();
            timer.Dispose();
            foreach (var tray in appTrays)
                tray.Dispose();
            managerIcon.Visible = false;
            managerIcon.Dispose();
            managerMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string TruncateTooltip(string value)
    {
        return value.Length <= 63 ? value : value.Substring(0, 63);
    }
}

internal sealed class AppTray : IDisposable
{
    private readonly ManagedApp app;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly HashSet<int> launchHiddenProcessIds = new HashSet<int>();
    private readonly Dictionary<IntPtr, IntPtr> originalExtendedStyles = new Dictionary<IntPtr, IntPtr>();
    private DateTime hideNoticeUntil = DateTime.MinValue;
    private AppStatus status = AppStatus.NotRunning;

    public AppTray(ManagedApp app)
    {
        this.app = app;

        menu = new ContextMenuStrip();
        notifyIcon = new NotifyIcon();
        notifyIcon.Icon = LoadIcon();
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.MouseClick += NotifyIconMouseClick;
        BuildMenu();
        UpdateStatus();
    }

    public string StatusLine
    {
        get { return app.Name + "：" + GetStatusText(); }
    }

    public AppStatus Status { get { return status; } }

    /// <summary>管理器菜单里的条目文案：应用名 + 当前状态，点击切换显示/隐藏。</summary>
    public string MenuLabel { get { return app.Name + "（" + GetStatusText() + "）"; } }

    public void ToggleWindow() { Toggle(); }

    public string ProcessName { get { return Path.GetFileNameWithoutExtension(app.Exe); } }

    public void StartIfConfigured()
    {
        if (app.AutoStart && !IsRunning())
            StartApp();
    }

    public void Tick(Dictionary<string, List<Process>> processesByName)
    {
        List<Process> matchingProcesses;
        var processIds = processesByName.TryGetValue(Path.GetFileNameWithoutExtension(app.Exe), out matchingProcesses)
            ? matchingProcesses.Where(process => ProcessSnapshot.IsRunning(process) && IsManagedProcess(process)).Select(process => process.Id).ToList()
            : new List<int>();
        if (processIds.Count == 0)
        {
            if (notifyIcon.Visible)
                notifyIcon.Visible = false;
            launchHiddenProcessIds.Clear();
            originalExtendedStyles.Clear();
            status = AppStatus.NotRunning;
            UpdateStatus();
            return;
        }

        var managedWindowsByProcess = new Dictionary<int, List<IntPtr>>();
        foreach (var pid in processIds)
            managedWindowsByProcess[pid] = FindManagedWindows(pid, true).ToList();

        var hasManagedWindow = managedWindowsByProcess.Values.Any(windows => windows.Count > 0);
        if (notifyIcon.Visible != hasManagedWindow)
            notifyIcon.Visible = hasManagedWindow;
        if (!hasManagedWindow)
        {
            status = AppStatus.BackgroundOnly;
            UpdateStatus();
            return;
        }

        foreach (var pid in processIds)
        {
            var windows = managedWindowsByProcess[pid];
            if (windows.Count == 0)
                continue;

            if (app.HideOnLaunch && !launchHiddenProcessIds.Contains(pid))
            {
                foreach (var hwnd in windows)
                    HideWindowToTray(hwnd);
                launchHiddenProcessIds.Add(pid);
                ShowHiddenNotice();
                continue;
            }

            foreach (var hwnd in windows)
            {
                if (app.DisableCloseButton)
                    User32.DisableCloseButton(hwnd);

                if (app.HideWhenMinimized && User32.IsIconic(hwnd))
                {
                    HideWindowToTray(hwnd);
                    ShowHiddenNotice();
                }
            }
        }

        foreach (var oldPid in launchHiddenProcessIds.Where(pid => !processIds.Contains(pid)).ToList())
            launchHiddenProcessIds.Remove(oldPid);

        status = managedWindowsByProcess.Values.SelectMany(windows => windows).Any(User32.IsWindowVisible)
            ? AppStatus.Visible : AppStatus.Hidden;
        UpdateStatus();
    }

    private void NotifyIconMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            Toggle();
    }

    private void Toggle()
    {
        if (GetStatus() == AppStatus.Visible)
            Hide();
        else
            Show();
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(StatusLine) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示窗口", null, delegate { Show(); });
        menu.Items.Add("隐藏到托盘", null, delegate { Hide(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 " + app.Name, null, delegate { CloseApp(); });
    }

    private void UpdateStatus()
    {
        var tooltip = TruncateTooltip(app.Name + " - " + GetStatusText());
        if (notifyIcon.Text != tooltip)
            notifyIcon.Text = tooltip;
        if (menu.Items.Count > 0)
        {
            var statusLine = StatusLine;
            if (menu.Items[0].Text != statusLine)
                menu.Items[0].Text = statusLine;
        }
    }

    private string GetStatusText()
    {
        switch (status)
        {
            case AppStatus.Visible:
                return "窗口可见";
            case AppStatus.Hidden:
                return "已隐藏到托盘";
            case AppStatus.BackgroundOnly:
                return "后台运行";
            default:
                return "未运行";
        }
    }

    private AppStatus GetStatus()
    {
        if (!IsRunning())
            return AppStatus.NotRunning;

        var hasHiddenWindow = false;
        foreach (var pid in GetProcessIds())
        {
            foreach (var hwnd in FindManagedWindows(pid, true))
            {
                if (User32.IsWindowVisible(hwnd))
                    return AppStatus.Visible;
                hasHiddenWindow = true;
            }
        }

        return hasHiddenWindow ? AppStatus.Hidden : AppStatus.BackgroundOnly;
    }

    private void RefreshStatus()
    {
        status = GetStatus();
        UpdateStatus();
    }

    private void Show()
    {
        foreach (var pid in GetProcessIds())
        {
            foreach (var hwnd in FindManagedWindows(pid, true))
            {
                RestoreWindowFromTray(hwnd);
                User32.SetForegroundWindow(hwnd);
            }
            launchHiddenProcessIds.Add(pid);
        }
        RefreshStatus();
    }

    private void Hide()
    {
        var hidden = false;
        foreach (var pid in GetProcessIds())
        {
            foreach (var hwnd in FindManagedWindows(pid, true))
            {
                HideWindowToTray(hwnd);
                hidden = true;
            }
            launchHiddenProcessIds.Add(pid);
        }
        if (hidden)
            ShowHiddenNotice();
        RefreshStatus();
    }

    private void CloseApp()
    {
        var closeRequested = false;
        foreach (var pid in GetProcessIds())
        {
            foreach (var hwnd in FindManagedWindows(pid, true))
            {
                User32.PostMessage(hwnd, User32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closeRequested = true;
            }
        }

        if (closeRequested)
            return;

        foreach (var pid in GetProcessIds())
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(pid);
                process.CloseMainWindow();
            }
            catch
            {
                // A background-only process may not expose a closable main window.
            }
            finally
            {
                if (process != null)
                    process.Dispose();
            }
        }
    }

    private void StartApp()
    {
        try
        {
            var startInfo = new ProcessStartInfo();
            if (!string.IsNullOrWhiteSpace(app.Path) && File.Exists(app.Path))
            {
                startInfo.FileName = app.Path;
                startInfo.WorkingDirectory = Path.GetDirectoryName(app.Path);
            }
            else
            {
                startInfo.FileName = app.Exe;
            }
            Process.Start(startInfo);
        }
        catch
        {
            // Some apps, including this launcher, must be started by their own updater.
        }
    }

    private IEnumerable<IntPtr> FindManagedWindows(int pid, bool includeHidden)
    {
        var results = new List<IntPtr>();
        User32.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            int windowPid;
            User32.GetWindowThreadProcessId(hwnd, out windowPid);
            if (windowPid != pid)
                return true;

            if (!includeHidden && !User32.IsWindowVisible(hwnd))
                return true;

            if (IsManagedWindow(hwnd))
                results.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return results;
    }

    private bool IsManagedWindow(IntPtr hwnd)
    {
        var title = User32.GetWindowText(hwnd);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (title == "MediaContextNotificationWindow" ||
            title == "SystemResourceNotifyWindow" ||
            title == "CiceroUIWndFrame" ||
            title == "MSCTFIME UI" ||
            title == "Default IME" ||
            title.StartsWith("GDI+ Window", StringComparison.OrdinalIgnoreCase))
            return false;

        var style = User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE).ToInt64();
        if ((style & User32.WS_CAPTION) == 0 && (style & User32.WS_SYSMENU) == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(app.TitleContains) &&
            title.IndexOf(app.TitleContains, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
    }

    private void HideWindowToTray(IntPtr hwnd)
    {
        if (!originalExtendedStyles.ContainsKey(hwnd))
        {
            var originalStyle = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE);
            originalExtendedStyles[hwnd] = originalStyle;
            var trayStyle = new IntPtr((originalStyle.ToInt64() & ~User32.WS_EX_APPWINDOW) | User32.WS_EX_TOOLWINDOW);
            User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, trayStyle);
            User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_FRAMECHANGED);
        }

        User32.ShowWindow(hwnd, User32.SW_HIDE);
    }

    private void RestoreWindowFromTray(IntPtr hwnd)
    {
        IntPtr originalStyle;
        if (originalExtendedStyles.TryGetValue(hwnd, out originalStyle))
        {
            User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, originalStyle);
            User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_FRAMECHANGED);
            originalExtendedStyles.Remove(hwnd);
        }

        User32.ShowWindow(hwnd, User32.SW_RESTORE);
    }

    private bool IsRunning()
    {
        return GetProcessIds().Count > 0;
    }

    private List<int> GetProcessIds()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.Exe));
        try
        {
            return processes.Where(IsManagedProcess).Select(p => p.Id).ToList();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private bool IsManagedProcess(Process process)
    {
        if (string.IsNullOrWhiteSpace(app.ProcessPath))
            return true;

        try
        {
            var expectedPath = Path.GetFullPath(app.ProcessPath).TrimEnd('\\');
            var actualPath = Path.GetFullPath(process.MainModule.FileName).TrimEnd('\\');
            return string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private Icon LoadIcon()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath))
                return CreateProxyIcon(Icon.ExtractAssociatedIcon(app.IconPath));

            if (!string.IsNullOrWhiteSpace(app.Path) && File.Exists(app.Path))
                return CreateProxyIcon(Icon.ExtractAssociatedIcon(app.Path));
        }
        catch
        {
        }
        return CreateProxyIcon(SystemIcons.Application);
    }

    private static Icon CreateProxyIcon(Icon sourceIcon)
    {
        const int size = 32;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(sourceIcon, new Rectangle(0, 0, size, size));

            var badgeRect = new Rectangle(21, 1, 10, 10);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(155, 0, 0, 0)))
                graphics.FillEllipse(shadowBrush, new Rectangle(badgeRect.X, badgeRect.Y + 1, badgeRect.Width, badgeRect.Height));
            using (var badgeBrush = new SolidBrush(Color.FromArgb(255, 238, 54, 54)))
                graphics.FillEllipse(badgeBrush, badgeRect);
            using (var borderPen = new Pen(Color.White, 2))
                graphics.DrawEllipse(borderPen, badgeRect);
        }

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        User32.DestroyIcon(handle);
        bitmap.Dispose();
        return icon;
    }

    private void ShowHiddenNotice()
    {
        if (!app.ShowNotifications || DateTime.UtcNow < hideNoticeUntil)
            return;

        hideNoticeUntil = DateTime.UtcNow.AddSeconds(4);
        notifyIcon.BalloonTipTitle = app.Name + " hidden to tray";
        notifyIcon.BalloonTipText = "Click the tray icon to restore the window.";
        notifyIcon.ShowBalloonTip(2500);
    }

    private static string TruncateTooltip(string value)
    {
        return value.Length <= 63 ? value : value.Substring(0, 63);
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
    }
}

internal sealed class ManagedApp
{
    public string Section;
    public string Name;
    public string Path;
    public string Exe;
    public string ProcessPath;
    public string TitleContains;
    public bool AutoStart;
    public bool HideOnLaunch;
    public bool HideWhenMinimized;
    public bool DisableCloseButton;
    public bool ShowNotifications;
    public string IconPath;
}

internal enum AppStatus
{
    NotRunning,
    Visible,
    Hidden,
    BackgroundOnly
}

internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> sections =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> SectionNames { get { return sections.Keys; } }

    public static IniFile Load(string path)
    {
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        var current = "";
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = line.Substring(1, line.Length - 2).Trim();
                if (!ini.sections.ContainsKey(current))
                    ini.sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var pos = line.IndexOf('=');
            if (pos <= 0 || current.Length == 0)
                continue;

            ini.sections[current][line.Substring(0, pos).Trim()] = line.Substring(pos + 1).Trim();
        }
        return ini;
    }

    public string Get(string section, string key, string defaultValue)
    {
        Dictionary<string, string> values;
        string value;
        return sections.TryGetValue(section, out values) && values.TryGetValue(key, out value) ? value : defaultValue;
    }

    public bool GetBool(string section, string key, bool defaultValue)
    {
        var value = Get(section, key, defaultValue ? "1" : "0").Trim().ToLowerInvariant();
        return value == "1" || value == "true" || value == "yes" || value == "on";
    }

    public int GetInt(string section, string key, int defaultValue)
    {
        int value;
        return int.TryParse(Get(section, key, defaultValue.ToString()), out value) ? value : defaultValue;
    }
}

internal static class User32
{
    public const int SW_HIDE = 0;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);

    [DllImport("user32.dll")]
    public static extern bool EnableMenuItem(IntPtr hMenu, uint item, uint enable);

    [DllImport("user32.dll")]
    public static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static string GetWindowText(IntPtr hWnd)
    {
        var builder = new StringBuilder(512);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static void DisableCloseButton(IntPtr hWnd)
    {
        var menu = GetSystemMenu(hWnd, false);
        if (menu == IntPtr.Zero)
            return;

        EnableMenuItem(menu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED);
        DrawMenuBar(hWnd);
    }
}
