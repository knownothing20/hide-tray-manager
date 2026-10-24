using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class WindowCandidate
{
    public string Title;
    public string Exe;
    public string ProcessPath;
}

/// <summary>配置窗口的深色主题：集中定义颜色与控件样式，和 SysFloat 的设置窗口同一观感。</summary>
internal static class UiTheme
{
    public static readonly Color Window = Color.FromArgb(32, 38, 49);
    public static readonly Color Chrome = Color.FromArgb(26, 31, 40);
    public static readonly Color Surface = Color.FromArgb(39, 46, 59);
    public static readonly Color GridBack = Color.FromArgb(38, 45, 58);
    public static readonly Color GridHeader = Color.FromArgb(28, 33, 43);
    public static readonly Color Line = Color.FromArgb(53, 62, 78);
    public static readonly Color Text = Color.FromArgb(241, 244, 249);
    public static readonly Color Muted = Color.FromArgb(166, 176, 193);
    public static readonly Color Accent = Color.FromArgb(72, 145, 239);
    public static readonly Color AccentHover = Color.FromArgb(91, 160, 248);

    public static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary ? Accent : Color.FromArgb(45, 53, 67);
        button.ForeColor = primary ? Color.White : Text;
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(55, 65, 81);
        button.Height = 32;
        button.AutoSize = true;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.GridColor = Line;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeader;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeader;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle.BackColor = GridBack;
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(230, 234, 242);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 64, 90);
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(41, 48, 62);
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Color.FromArgb(230, 234, 242);
        grid.RowTemplate.Height = 32;
    }

    public static GraphicsPath RoundPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void ApplyRoundedRegion(Control control, int radius)
    {
        var previous = control.Region;
        control.Region = new Region(RoundPath(new Rectangle(0, 0, control.Width, control.Height), radius));
        if (previous != null) previous.Dispose();
    }
}

internal static class WindowDiscovery
{
    public static List<WindowCandidate> Scan()
    {
        var result = new Dictionary<int, WindowCandidate>();
        int ownPid;
        using (var ownProcess = Process.GetCurrentProcess())
            ownPid = ownProcess.Id;
        User32.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
        {
            if (!User32.IsWindowVisible(hwnd))
                return true;
            var title = User32.GetWindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            var style = User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE).ToInt64();
            var extended = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE).ToInt64();
            if ((style & (User32.WS_CAPTION | User32.WS_SYSMENU)) == 0 ||
                (extended & User32.WS_EX_TOOLWINDOW) != 0)
                return true;
            int pid;
            User32.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid || result.ContainsKey(pid))
                return true;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    string path = "";
                    try { path = process.MainModule.FileName; }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                    result[pid] = new WindowCandidate {
                        Title = title, Exe = process.ProcessName + ".exe", ProcessPath = path
                    };
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return result.Values.OrderBy(candidate => candidate.Exe, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal sealed class ConfigurationForm : Form
{
    private readonly string configPath;
    private readonly Action reload;
    private readonly ListView windows = new ListView();
    private readonly TextBox editor = new TextBox();
    private readonly Label status = new Label();
    private readonly Panel editorPanel = new Panel();
    private readonly Button toggleEditor = new Button();
    private readonly DataGridView configuredApps = new DataGridView();
    private readonly Panel contentCard = new Panel();
    private readonly Panel configuredPage = new Panel();
    private readonly Panel discoveryPage = new Panel();
    private readonly Label pageTitle = new Label();
    private readonly Label pageDescription = new Label();
    private readonly FlowLayoutPanel actionsFlow = new FlowLayoutPanel();
    private readonly Button editPaths = new Button { Text = "编辑路径与筛选..." };
    private readonly Button removeButton = new Button { Text = "移除所选软件" };
    private readonly Button refreshButton = new Button { Text = "刷新窗口列表" };
    private readonly Button addButton = new Button { Text = "加入最小化托盘" };
    private readonly List<NavButton> navButtons = new List<NavButton>();
    private int activePage;
    private float uiScale = 1f;
    private bool loadingTable;
    private bool updatingEditor;
    private bool scanned;
    private string loadedText;

    public ConfigurationForm(string configPath, Action reload)
    {
        this.configPath = configPath;
        this.reload = reload;
        Text = "Hide Tray Manager · 配置与窗口探测";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 680);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        // 布局按 96 DPI 设计；进程已声明 DPI 感知，字体(point)随 DPI 自动放大，
        // 几何尺寸在控件树构建完成后按 uiScale 统一 Scale（纯代码窗体没有
        // 设计器基准，AutoScaleMode.Dpi 不生效）。
        AutoScaleMode = AutoScaleMode.None;
        BackColor = UiTheme.Window;
        ForeColor = UiTheme.Text;
        using (var graphics = CreateGraphics())
        {
            float dpi = Math.Max(1f, graphics.DpiX / 96f);
            var area = Screen.PrimaryScreen.WorkingArea;
            float fit = Math.Min(area.Width * 0.96f / 980f, area.Height * 0.94f / 680f);
            uiScale = Math.Max(0.8f, Math.Min(dpi, fit));
        }

        // 停靠布局按“后添加者先处理”：内容(Fill)最先加入=最后处理=填充剩余；
        // 标题栏最后加入=最先处理=横向占满全宽；侧栏在标题栏之下全高；底栏在侧栏右侧。
        // 顺序错了 Fill 会摊满全窗，把表格压到侧栏/底栏下面（显示不全的根因）。
        BuildContent();
        BuildBottomBar();
        BuildSidebar();
        BuildTitleBar();

        if (uiScale != 1f)
        {
            Scale(new SizeF(uiScale, uiScale));
            // CenterScreen 按缩放前的尺寸定位，放大后可能越界；缩放后重新居中。
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 2));
        }
        SizeChanged += delegate { UiTheme.ApplyRoundedRegion(this, (int)(12 * uiScale)); };
        ReadConfig();
        Shown += delegate { ShowPage(0); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (editor.Text != NormalizeLines(loadedText) && MessageBox.Show(this, "有未保存的配置编辑，放弃并关闭？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
        };
    }

    private void BuildTitleBar()
    {
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.Chrome };
        var title = new Label {
            Dock = DockStyle.Fill, Text = "  Hide Tray Manager · 配置",
            ForeColor = UiTheme.Text, TextAlign = ContentAlignment.MiddleLeft
        };
        title.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        var close = new Button {
            Text = "×", Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat,
            ForeColor = UiTheme.Muted, BackColor = UiTheme.Chrome, TabStop = false, Cursor = Cursors.Hand
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
        close.MouseEnter += delegate { close.ForeColor = Color.White; };
        close.MouseLeave += delegate { close.ForeColor = UiTheme.Muted; };
        close.Click += delegate { Close(); };
        titleBar.Controls.Add(title);
        titleBar.Controls.Add(close);
        Controls.Add(titleBar);
    }

    private NavButton CreateNavButton(string caption, int pageIndex)
    {
        var button = new NavButton { Dock = DockStyle.Top, Height = 46, Caption = caption };
        button.Click += delegate { ShowPage(pageIndex); };
        navButtons.Add(button);
        return button;
    }

    private void BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 168, BackColor = UiTheme.Chrome, Padding = new Padding(10, 14, 10, 10) };
        var footer = new Label {
            Dock = DockStyle.Bottom, Height = 40, Text = "私人配置仅保存到 local",
            ForeColor = Color.FromArgb(110, 119, 136), TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(8, 0, 0, 4)
        };
        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(CreateNavButton("已配置软件", 0));
        sidebar.Controls.Add(CreateNavButton("窗口探测", 1));
        Controls.Add(sidebar);
    }

    private void BuildBottomBar()
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = UiTheme.Chrome };
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = UiTheme.Muted;
        status.Padding = new Padding(16, 0, 12, 0);
        var actions = new FlowLayoutPanel {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 12, 12, 12), BackColor = UiTheme.Chrome,
            // Dock=Right 的流式面板必须 AutoSize，否则宽度停在默认值，
            // 高 DPI 缩放后按钮装不下会被裁掉。
            AutoSize = true
        };
        var loadConfig = new Button { Text = "加载配置", AutoSize = true };
        UiTheme.StyleButton(loadConfig, false);
        loadConfig.Click += delegate { ReloadFromDisk(); };
        toggleEditor.Text = "展开配置文件";
        UiTheme.StyleButton(toggleEditor, false);
        toggleEditor.Click += delegate {
            editorPanel.Visible = !editorPanel.Visible;
            editorPanel.Height = editorPanel.Visible ? (int)(272 * uiScale) : 0;
            toggleEditor.Text = editorPanel.Visible ? "收起配置文件" : "展开配置文件";
        };
        var saveConfig = new Button { Text = "保存并应用", AutoSize = true };
        UiTheme.StyleButton(saveConfig, true);
        saveConfig.Click += delegate { RunAction(delegate { configuredApps.EndEdit(); SaveText(editor.Text); status.Text = "表格配置已保存并应用。"; }); };
        actions.Controls.AddRange(new Control[] { loadConfig, toggleEditor, saveConfig });
        bottom.Controls.Add(status);
        bottom.Controls.Add(actions);
        Controls.Add(bottom);
    }

    private void BuildContent()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Window, Padding = new Padding(20, 14, 20, 12) };

        var header = new Panel { Dock = DockStyle.Top, Height = 72 };
        pageTitle.Dock = DockStyle.Top;
        pageTitle.Height = 34;
        pageTitle.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
        pageTitle.ForeColor = UiTheme.Text;
        pageDescription.Dock = DockStyle.Fill;
        pageDescription.ForeColor = UiTheme.Muted;
        pageDescription.TextAlign = ContentAlignment.BottomLeft;
        actionsFlow.Dock = DockStyle.Right;
        actionsFlow.Width = 300;
        actionsFlow.AutoSize = true;
        actionsFlow.WrapContents = false;
        actionsFlow.Padding = new Padding(0, 8, 0, 0);
        actionsFlow.BackColor = UiTheme.Window;
        UiTheme.StyleButton(editPaths, false);
        editPaths.Click += delegate { RunAction(EditSelectedApplication); };
        UiTheme.StyleButton(removeButton, false);
        removeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(96, 48, 48);
        removeButton.Click += delegate { RunAction(RemoveSelectedApplication); };
        UiTheme.StyleButton(refreshButton, false);
        refreshButton.Click += delegate { RunAction(RefreshWindows); };
        UiTheme.StyleButton(addButton, true);
        addButton.Click += delegate { RunAction(AddSelected); };
        header.Controls.Add(pageDescription);
        header.Controls.Add(pageTitle);
        header.Controls.Add(actionsFlow);

        BuildConfiguredPage();
        BuildDiscoveryPage();

        editorPanel.Dock = DockStyle.Bottom;
        editorPanel.Height = 0;
        editorPanel.Visible = false;
        editorPanel.BackColor = UiTheme.Surface;
        editorPanel.Padding = new Padding(1);
        editorPanel.SizeChanged += delegate { UiTheme.ApplyRoundedRegion(editorPanel, (int)(10 * uiScale)); };
        var editorHeader = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.GridHeader, Padding = new Padding(14, 0, 12, 0) };
        var editorTitle = new Label {
            Dock = DockStyle.Fill, Text = "配置文件（INI）", ForeColor = UiTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        var editorButtons = new FlowLayoutPanel { Dock = DockStyle.Right, WrapContents = false, BackColor = UiTheme.GridHeader, Padding = new Padding(0, 8, 0, 6), AutoSize = true };
        var reread = new Button { Text = "重新读取配置", AutoSize = true };
        UiTheme.StyleButton(reread, false);
        reread.Click += delegate { ReloadFromDisk(); };
        editorButtons.Controls.Add(reread);
        editorHeader.Controls.Add(editorTitle);
        editorHeader.Controls.Add(editorButtons);
        editor.Dock = DockStyle.Fill;
        editor.Multiline = true;
        editor.ScrollBars = ScrollBars.Both;
        editor.WordWrap = false;
        editor.AcceptsTab = true;
        editor.Font = new Font("Consolas", 10F);
        editor.BackColor = Color.FromArgb(27, 32, 43);
        editor.ForeColor = Color.FromArgb(230, 234, 242);
        editor.BorderStyle = BorderStyle.None;
        editor.Margin = new Padding(0);
        editor.TextChanged += delegate {
            if (updatingEditor) return;
            var normalized = NormalizeLines(editor.Text);
            if (normalized != editor.Text)
            {
                var position = editor.SelectionStart;
                SetDraft(normalized);
                editor.SelectionStart = Math.Min(position, editor.TextLength);
            }
            LoadConfigurationTable();
        };
        editorPanel.Controls.Add(editor);
        editorPanel.Controls.Add(editorHeader);

        contentCard.Dock = DockStyle.Fill;
        contentCard.BackColor = UiTheme.Surface;
        contentCard.Padding = new Padding(12);
        contentCard.SizeChanged += delegate { UiTheme.ApplyRoundedRegion(contentCard, (int)(10 * uiScale)); };
        configuredPage.Dock = DockStyle.Fill;
        discoveryPage.Dock = DockStyle.Fill;
        discoveryPage.Visible = false;
        contentCard.Controls.Add(discoveryPage);
        contentCard.Controls.Add(configuredPage);

        content.Controls.Add(contentCard);
        content.Controls.Add(editorPanel);
        content.Controls.Add(header);
        Controls.Add(content);
    }

    private void BuildConfiguredPage()
    {
        configuredApps.Dock = DockStyle.Fill;
        configuredApps.AllowUserToAddRows = false;
        configuredApps.AllowUserToDeleteRows = false;
        configuredApps.RowHeadersVisible = false;
        configuredApps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        configuredApps.MultiSelect = false;
        configuredApps.EditMode = DataGridViewEditMode.EditOnEnter;
        configuredApps.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        UiTheme.StyleGrid(configuredApps);
        AddColumn("Enabled", "启用", true, 45);
        AddColumn("Name", "软件名称", false, 160);
        AddColumn("Exe", "软件进程", false, 190);
        AddColumn("HideWhenMinimized", "最小化隐藏", true, 90);
        AddColumn("HideOnLaunch", "启动后隐藏", true, 90);
        AddColumn("AutoStart", "自动启动", true, 80);
        AddColumn("DisableCloseButton", "禁用关闭", true, 80);
        configuredApps.CurrentCellDirtyStateChanged += delegate {
            if (configuredApps.IsCurrentCellDirty)
                configuredApps.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        configuredApps.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
            if (loadingTable || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var row = configuredApps.Rows[e.RowIndex];
            var column = configuredApps.Columns[e.ColumnIndex];
            var value = row.Cells[e.ColumnIndex].Value;
            var text = column is DataGridViewCheckBoxColumn ? (Convert.ToBoolean(value) ? "1" : "0") : Convert.ToString(value);
            RunAction(delegate {
                SetDraft(AppConfiguration.UpdateValue(editor.Text, (string)row.Tag, column.Name, text));
                status.Text = "表格已修改，点击“保存并应用”使配置生效。";
            });
        };

        var gridMenu = new ContextMenuStrip();
        gridMenu.Items.Add("编辑路径与筛选...", null, delegate { RunAction(EditSelectedApplication); });
        gridMenu.Items.Add("移除所选软件", null, delegate { RunAction(RemoveSelectedApplication); });
        configuredApps.ContextMenuStrip = gridMenu;
        configuredPage.Controls.Add(configuredApps);
    }

    private void BuildDiscoveryPage()
    {
        windows.Dock = DockStyle.Fill;
        windows.View = View.Details;
        windows.CheckBoxes = true;
        windows.FullRowSelect = true;
        windows.GridLines = false;
        windows.BackColor = UiTheme.GridBack;
        windows.ForeColor = Color.FromArgb(230, 234, 242);
        windows.BorderStyle = BorderStyle.None;
        windows.Columns.Add("软件进程", 170);
        windows.Columns.Add("当前窗口标题", 430);
        windows.Columns.Add("配置状态", 170);
        discoveryPage.Controls.Add(windows);
    }

    private void ShowPage(int index)
    {
        activePage = index;
        for (var i = 0; i < navButtons.Count; i++)
            navButtons[i].Selected = i == index;
        configuredPage.Visible = index == 0;
        discoveryPage.Visible = index == 1;
        pageTitle.Text = index == 0 ? "已配置软件" : "窗口探测";
        pageDescription.Text = index == 0
            ? "表格直接读取配置文件，未运行或已禁用的软件也会列出；改动需保存并应用。"
            : "勾选要托管的窗口后加入；列表仅在进入本页或手动刷新时扫描。";
        actionsFlow.Controls.Clear();
        actionsFlow.Controls.AddRange(index == 0
            ? new Control[] { editPaths, removeButton }
            : new Control[] { refreshButton, addButton });
        if (index == 0) ShowConfigurationCount();
        else if (!scanned) RunAction(RefreshWindows);
        Invalidate(true);
    }

    private bool clampingToScreen;

    private const int WmNcHitTest = 0x84;
    private const int HitTestClient = 1;
    private const int HitTestCaption = 2;

    // 无边框窗口的标题条（顶部 44×uiScale 逻辑像素，右侧关闭按钮除外）通过
    // 命中测试返回 HTCAPTION，交给系统原生拖动——比 ReleaseCapture 消息 hack 稳。
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HitTestClient)
            {
                int screenX = (short)(m.LParam.ToInt32() & 0xFFFF);
                int screenY = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                var client = PointToClient(new Point(screenX, screenY));
                int barHeight = (int)(44 * uiScale);
                int closeButtonWidth = (int)(44 * uiScale);
                if (client.Y >= 0 && client.Y < barHeight && client.X < Width - closeButtonWidth)
                    m.Result = (IntPtr)HitTestCaption;
            }
            return;
        }
        base.WndProc(ref m);
    }

    // 无边框窗口可被拖到屏幕边缘外，导致表格最右列/按钮"显示不全"；
    // 位置变化时把窗口钳回当前屏幕的工作区内（Screen.FromControl 跨屏拖动时跟随目标屏）。
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (clampingToScreen || WindowState != FormWindowState.Normal || IsDisposed)
            return;
        var area = Screen.FromControl(this).WorkingArea;
        int targetLeft = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        int targetTop = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        if (targetLeft != Left || targetTop != Top)
        {
            clampingToScreen = true;
            try { Location = new Point(targetLeft, targetTop); }
            finally { clampingToScreen = false; }
        }
    }

    private void ReadConfig()
    {
        loadedText = File.ReadAllText(configPath, Encoding.UTF8);
        SetDraft(loadedText);
        LoadConfigurationTable();
        scanned = false;
    }

    private void AddColumn(string key, string label, bool checkbox, int width)
    {
        DataGridViewColumn column = checkbox ? (DataGridViewColumn)new DataGridViewCheckBoxColumn() : new DataGridViewTextBoxColumn();
        column.Name = key;
        column.HeaderText = label;
        column.FillWeight = width;
        column.MinimumWidth = checkbox ? 65 : 110;
        column.SortMode = DataGridViewColumnSortMode.NotSortable;
        configuredApps.Columns.Add(column);
    }

    private void LoadConfigurationTable()
    {
        loadingTable = true;
        try
        {
            configuredApps.Rows.Clear();
            var ini = IniFile.Parse(editor.Text);
            foreach (var section in ini.SectionNames.Where(name => !name.Equals("General", StringComparison.OrdinalIgnoreCase)))
            {
                var index = configuredApps.Rows.Add(
                    ini.GetBool(section, "Enabled", true), ini.Get(section, "Name", section),
                    ini.Get(section, "Exe", Path.GetFileName(ini.Get(section, "Path", ""))),
                    ini.GetBool(section, "HideWhenMinimized", true), ini.GetBool(section, "HideOnLaunch", true),
                    ini.GetBool(section, "AutoStart", false), ini.GetBool(section, "DisableCloseButton", true));
                configuredApps.Rows[index].Tag = section;
            }
        }
        finally { loadingTable = false; }
        if (activePage == 0 && status.Text.Length == 0) ShowConfigurationCount();
    }

    private void ShowConfigurationCount()
    {
        status.Text = "已加载 " + configuredApps.Rows.Count + " 项配置（包含未运行或禁用的软件）。修改后需保存并应用。";
    }

    private void SetDraft(string text)
    {
        updatingEditor = true;
        try { editor.Text = NormalizeLines(text); }
        finally { updatingEditor = false; }
        scanned = false;
    }

    private void ReloadFromDisk()
    {
        configuredApps.EndEdit();
        if (editor.Text != NormalizeLines(loadedText) && MessageBox.Show(this, "放弃未保存的编辑并加载磁盘配置？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        RunAction(delegate {
            ReadConfig();
            ShowPage(0);
            ShowConfigurationCount();
        });
    }

    private void EditSelectedApplication()
    {
        configuredApps.EndEdit();
        if (configuredApps.CurrentRow == null) return;
        var section = (string)configuredApps.CurrentRow.Tag;
        using (var dialog = new ApplicationDetailsForm(IniFile.Parse(editor.Text), section))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var text = editor.Text;
            foreach (var value in dialog.Values)
                text = AppConfiguration.UpdateValue(text, section, value.Key, value.Value);
            SetDraft(text);
            status.Text = "路径与筛选已修改，点击“保存并应用”使配置生效。";
        }
    }

    private void RemoveSelectedApplication()
    {
        configuredApps.EndEdit();
        if (configuredApps.CurrentRow == null)
        {
            status.Text = "请先在表格中选择要移除的软件。";
            return;
        }
        var section = (string)configuredApps.CurrentRow.Tag;
        var display = Convert.ToString(configuredApps.CurrentRow.Cells["Name"].Value);
        if (MessageBox.Show(this, "移除 “" + display + "” 的配置段？\n（只删除托管配置，不会结束进程）", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        SetDraft(AppConfiguration.RemoveSection(editor.Text, section));
        status.Text = "已移除 " + display + "，点击“保存并应用”生效。";
    }

    private static string NormalizeLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    private void RefreshWindows()
    {
        var ini = IniFile.Parse(editor.Text);
        windows.BeginUpdate();
        try
        {
            windows.Items.Clear();
            foreach (var candidate in WindowDiscovery.Scan())
            {
                var configured = AppConfiguration.IsConfigured(ini, candidate);
                var item = new ListViewItem(candidate.Exe) { Tag = candidate };
                item.SubItems.Add(candidate.Title);
                item.SubItems.Add(configured ? "已配置" : (string.IsNullOrEmpty(candidate.ProcessPath) ? "未加入（按进程名）" : "未加入"));
                windows.Items.Add(item);
            }
        }
        finally { windows.EndUpdate(); }
        scanned = true;
        status.Text = "检测到 " + windows.Items.Count + " 个软件。窗口列表仅在打开或手动刷新时扫描。";
    }

    private void AddSelected()
    {
        var selected = windows.CheckedItems.Cast<ListViewItem>().Select(item => (WindowCandidate)item.Tag).ToList();
        if (selected.Count == 0)
        {
            status.Text = "请先勾选需要加入的软件。";
            return;
        }
        int added;
        var text = AppConfiguration.AddWindows(editor.Text, selected, out added);
        if (added > 0)
        {
            SaveText(text);
            ShowPage(0);
        }
        status.Text = "已加入 " + added + " 个软件；已有配置自动跳过。新增软件在最小化后隐藏到托盘。";
    }

    private void SaveText(string text)
    {
        AppConfiguration.Validate(text);
        if (File.ReadAllText(configPath, Encoding.UTF8) != loadedText)
            throw new InvalidOperationException("配置已被其他程序修改，请重新读取后再保存。");
        var backupDirectory = Path.Combine(Path.GetDirectoryName(configPath), "backups");
        Directory.CreateDirectory(backupDirectory);
        File.Copy(configPath, Path.Combine(backupDirectory, "config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".ini"));
        File.WriteAllText(configPath, text, Encoding.UTF8);
        ReadConfig();
        reload();
        if (activePage == 1) RefreshWindows();
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (ArgumentException) { MessageBox.Show(this, "配置格式无效，请确认检查间隔为大于 0 的整数。", Text); }
        catch (InvalidOperationException) { MessageBox.Show(this, "配置已被其他程序修改。请先重新读取配置。", Text); }
        catch (Exception) { MessageBox.Show(this, "操作未完成，请检查文件权限和配置内容。", Text); }
    }

    /// <summary>侧边导航按钮：选中的页面显示高亮底色与左侧强调条。</summary>
    private sealed class NavButton : Control
    {
        private bool selected;

        public string Caption { get; set; }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public NavButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var background = selected ? UiTheme.Surface : UiTheme.Chrome;
            using (var brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, ClientRectangle);
            if (selected)
            {
                using (var accent = new SolidBrush(UiTheme.Accent))
                    e.Graphics.FillRectangle(accent, 0, 10, 4, Height - 20);
            }
            using (var textBrush = new SolidBrush(selected ? UiTheme.Text : UiTheme.Muted))
            {
                var format = new StringFormat { LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(Caption ?? "", new Font("Microsoft YaHei UI", 10F, selected ? FontStyle.Bold : FontStyle.Regular),
                    textBrush, new RectangleF(18, 0, Width - 22, Height), format);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!selected)
            {
                selected = true;
                Invalidate();
            }
        }
    }
}

internal sealed class ApplicationDetailsForm : Form
{
    private readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
    private readonly CheckBox notifications = new CheckBox { Text = "隐藏时显示通知", AutoSize = true };
    public IEnumerable<KeyValuePair<string, string>> Values
    {
        get {
            foreach (var field in fields) yield return new KeyValuePair<string, string>(field.Key, field.Value.Text);
            yield return new KeyValuePair<string, string>("ShowNotifications", notifications.Checked ? "1" : "0");
        }
    }

    public ApplicationDetailsForm(IniFile ini, string section)
    {
        Text = "编辑路径与筛选";
        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(720, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Window;
        ForeColor = UiTheme.Text;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 6, BackColor = UiTheme.Window };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var keys = new[] { "Path", "ProcessPath", "TitleContains", "IconPath" };
        var labels = new[] { "启动文件路径", "匹配进程路径", "窗口标题包含", "图标来源文件" };
        for (var index = 0; index < keys.Length; index++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.Controls.Add(new Label { Text = labels[index], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted }, 0, index);
            var box = new TextBox { Text = ini.Get(section, keys[index], ""), Dock = DockStyle.Fill, Margin = new Padding(3, 11, 3, 3), BackColor = Color.FromArgb(27, 32, 43), ForeColor = Color.FromArgb(230, 234, 242), BorderStyle = BorderStyle.FixedSingle };
            fields.Add(keys[index], box);
            layout.Controls.Add(box, 1, index);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        notifications.Checked = ini.GetBool(section, "ShowNotifications", false);
        notifications.ForeColor = UiTheme.Text;
        layout.Controls.Add(notifications, 1, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "应用到表格", AutoSize = true, DialogResult = DialogResult.OK };
        UiTheme.StyleButton(apply, true);
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        UiTheme.StyleButton(cancel, false);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);
        layout.Controls.Add(buttons, 1, 5);
        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = cancel;
    }
}
