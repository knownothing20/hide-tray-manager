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

        var help = new Label { Dock = DockStyle.Fill, Text = "勾选软件，加入“最小化隐藏到托盘”。新增配置不会立即隐藏窗口或自动启动软件。\n按应用进程管理其窗口；探测结果和本机路径仅保存到 local 配置。", AutoSize = false };
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
        toolbar.Controls.AddRange(new Control[] { refresh, add, toggleEditor });
        layout.Controls.Add(toolbar, 0, 1);

        windows.Dock = DockStyle.Fill;
        windows.View = View.Details;
        windows.CheckBoxes = true;
        windows.FullRowSelect = true;
        windows.GridLines = true;
        windows.Columns.Add("软件进程", 170);
        windows.Columns.Add("当前窗口标题", 430);
        windows.Columns.Add("配置状态", 170);
        layout.Controls.Add(windows, 0, 2);

        editorPanel.Dock = DockStyle.Fill;
        editorPanel.Visible = false;
        var editorButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
        var save = new Button { Text = "保存并应用配置", AutoSize = true };
        save.Click += delegate { RunAction(delegate { SaveText(editor.Text); status.Text = "配置已保存并应用。"; }); };
        var reread = new Button { Text = "重新读取配置", AutoSize = true };
        reread.Click += delegate {
            if (editor.Text != NormalizeLines(loadedText) && MessageBox.Show(this, "放弃未保存的编辑并重新读取？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;
            RunAction(delegate { ReadConfig(); RefreshWindows(); });
        };
        editorButtons.Controls.AddRange(new Control[] { save, reread });
        editor.Dock = DockStyle.Fill;
        editor.Multiline = true;
        editor.ScrollBars = ScrollBars.Both;
        editor.WordWrap = false;
        editor.AcceptsTab = true;
        editor.Font = new Font("Consolas", 10F);
        editorPanel.Controls.Add(editor);
        editorPanel.Controls.Add(editorButtons);
        layout.Controls.Add(editorPanel, 0, 3);
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(status, 0, 4);
        ReadConfig();
        Shown += delegate { RunAction(RefreshWindows); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (editor.Text != NormalizeLines(loadedText) && MessageBox.Show(this, "有未保存的配置编辑，放弃并关闭？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
        };
    }

    private void ReadConfig()
    {
        loadedText = File.ReadAllText(configPath, Encoding.UTF8);
        editor.Text = NormalizeLines(loadedText);
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
            SaveText(text);
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
        RefreshWindows();
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (ArgumentException) { MessageBox.Show(this, "配置格式无效，请确认检查间隔为大于 0 的整数。", Text); }
        catch (InvalidOperationException) { MessageBox.Show(this, "配置已被其他程序修改。请先重新读取配置。", Text); }
        catch (Exception) { MessageBox.Show(this, "操作未完成，请检查文件权限和配置内容。", Text); }
    }
}
