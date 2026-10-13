using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal sealed class WindowCandidate
{
    public string Title;
    public string Exe;
    public string ProcessPath;
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
    private readonly TableLayoutPanel layout = new TableLayoutPanel();
    private readonly DataGridView configuredApps = new DataGridView();
    private readonly TabControl tabs = new TabControl();
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
        Size = new Size(960, 650);
        MinimumSize = new Size(780, 530);
        AutoScaleMode = AutoScaleMode.Dpi;

        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(16);
        layout.ColumnCount = 1;
        layout.RowCount = 5;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(layout);

        var help = new Label { Dock = DockStyle.Fill, Text = "已配置软件直接从配置文件加载，未运行的软件也会显示。可在表格中编辑，再保存并应用。\n需要添加正在运行的软件时，切换到“窗口探测”。私人配置仅保存到 local。", AutoSize = false };
        layout.Controls.Add(help, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var refresh = new Button { Text = "刷新窗口列表", AutoSize = true, Height = 32 };
        refresh.Click += delegate { RunAction(RefreshWindows); };
        var add = new Button { Text = "加入最小化托盘", AutoSize = true, Height = 32 };
        add.Click += delegate { RunAction(AddSelected); };
        toggleEditor.Text = "展开配置文件";
        toggleEditor.AutoSize = true;
        toggleEditor.Height = 32;
        toggleEditor.Click += delegate {
            editorPanel.Visible = !editorPanel.Visible;
            layout.RowStyles[3].Height = editorPanel.Visible ? 250 : 0;
            toggleEditor.Text = editorPanel.Visible ? "收起配置文件" : "展开配置文件";
        };
        var loadConfig = new Button { Text = "加载配置", AutoSize = true, Height = 32 };
        loadConfig.Click += delegate { ReloadFromDisk(); };
        var saveConfig = new Button { Text = "保存并应用", AutoSize = true, Height = 32 };
        saveConfig.Click += delegate { RunAction(delegate { configuredApps.EndEdit(); SaveText(editor.Text); status.Text = "表格配置已保存并应用。"; }); };
        toolbar.Controls.AddRange(new Control[] { loadConfig, saveConfig, toggleEditor });
        layout.Controls.Add(toolbar, 0, 1);

        windows.Dock = DockStyle.Fill;
        windows.View = View.Details;
        windows.CheckBoxes = true;
        windows.FullRowSelect = true;
        windows.GridLines = true;
        windows.Columns.Add("软件进程", 170);
        windows.Columns.Add("当前窗口标题", 430);
        windows.Columns.Add("配置状态", 170);
        tabs.Dock = DockStyle.Fill;
        var configuredPage = new TabPage("已配置软件");
        var discoveryPage = new TabPage("窗口探测");
        tabs.TabPages.AddRange(new[] { configuredPage, discoveryPage });
        layout.Controls.Add(tabs, 0, 2);
        configuredApps.Dock = DockStyle.Fill;
        configuredApps.AllowUserToAddRows = false;
        configuredApps.AllowUserToDeleteRows = false;
        configuredApps.RowHeadersVisible = false;
        configuredApps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        configuredApps.MultiSelect = false;
        configuredApps.EditMode = DataGridViewEditMode.EditOnEnter;
        configuredApps.BackgroundColor = Color.White;
        configuredApps.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
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
        var configuredToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
        var editPaths = new Button { Text = "编辑路径与筛选...", AutoSize = true };
        editPaths.Click += delegate { RunAction(EditSelectedApplication); };
        configuredToolbar.Controls.Add(editPaths);
        configuredPage.Controls.Add(configuredApps);
        configuredPage.Controls.Add(configuredToolbar);
        var discoveryToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
        discoveryToolbar.Controls.AddRange(new Control[] { refresh, add });
        discoveryPage.Controls.Add(windows);
        discoveryPage.Controls.Add(discoveryToolbar);
        tabs.SelectedIndexChanged += delegate {
            if (tabs.SelectedIndex == 1 && !scanned) RunAction(RefreshWindows);
            else if (tabs.SelectedIndex == 0) ShowConfigurationCount();
        };

        editorPanel.Dock = DockStyle.Fill;
        editorPanel.Visible = false;
        var editorButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
        var save = new Button { Text = "保存并应用配置", AutoSize = true };
        save.Click += delegate { RunAction(delegate { configuredApps.EndEdit(); SaveText(editor.Text); status.Text = "配置已保存并应用。"; }); };
        var reread = new Button { Text = "重新读取配置", AutoSize = true };
        reread.Click += delegate { ReloadFromDisk(); };
        editorButtons.Controls.AddRange(new Control[] { save, reread });
        editor.Dock = DockStyle.Fill;
        editor.Multiline = true;
        editor.ScrollBars = ScrollBars.Both;
        editor.WordWrap = false;
        editor.AcceptsTab = true;
        editor.Font = new Font("Consolas", 10F);
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
        editorPanel.Controls.Add(editorButtons);
        layout.Controls.Add(editorPanel, 0, 3);
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(status, 0, 4);
        ReadConfig();
        Shown += delegate { ShowConfigurationCount(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (editor.Text != NormalizeLines(loadedText) && MessageBox.Show(this, "有未保存的配置编辑，放弃并关闭？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
        };
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
        if (tabs.SelectedIndex == 0) ShowConfigurationCount();
    }

    private void ShowConfigurationCount()
    {
        status.Text = "已加载 " + configuredApps.Rows.Count + " 项配置（包含未运行或禁用的软件）。表格修改后需保存并应用。";
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
            tabs.SelectedIndex = 0;
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
            tabs.SelectedIndex = 0;
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
        if (tabs.SelectedIndex == 1) RefreshWindows();
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (ArgumentException) { MessageBox.Show(this, "配置格式无效，请确认检查间隔为大于 0 的整数。", Text); }
        catch (InvalidOperationException) { MessageBox.Show(this, "配置已被其他程序修改。请先重新读取配置。", Text); }
        catch (Exception) { MessageBox.Show(this, "操作未完成，请检查文件权限和配置内容。", Text); }
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
        Size = new Size(720, 340);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var keys = new[] { "Path", "ProcessPath", "TitleContains", "IconPath" };
        var labels = new[] { "启动文件路径", "匹配进程路径", "窗口标题包含", "图标来源文件" };
        for (var index = 0; index < keys.Length; index++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            layout.Controls.Add(new Label { Text = labels[index], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, index);
            var box = new TextBox { Text = ini.Get(section, keys[index], ""), Dock = DockStyle.Fill, Margin = new Padding(3, 10, 3, 3) };
            fields.Add(keys[index], box);
            layout.Controls.Add(box, 1, index);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        notifications.Checked = ini.GetBool(section, "ShowNotifications", false);
        layout.Controls.Add(notifications, 1, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var apply = new Button { Text = "应用到表格", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { apply, cancel });
        layout.Controls.Add(buttons, 1, 5);
        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = cancel;
    }
}
