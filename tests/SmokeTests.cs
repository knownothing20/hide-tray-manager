using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class SmokeTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        if (args.Length > 0 && args[0] == "--test-window")
        {
            var window = new Form { Text = "Hide Tray Manager Synthetic Test", Size = new Size(420, 180) };
            var timeout = new System.Windows.Forms.Timer { Interval = 60000 };
            timeout.Tick += delegate { window.Close(); };
            timeout.Start();
            Application.Run(window);
            timeout.Dispose();
            return 0;
        }
        try
        {
            Run();
            Console.WriteLine("PASS: configuration, discovery, minimize/restore, editor and idle polling");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAIL: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Run()
    {
        var testDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Guid.NewGuid().ToString("N"));
        var configPath = AppConfiguration.GetPath(testDirectory);
        AppConfiguration.EnsureExists(configPath);
        Check(configPath.EndsWith(Path.Combine("local", "HideTrayManager.ini")), "local configuration path");
        var original = File.ReadAllText(configPath);
        AppConfiguration.EnsureExists(configPath);
        Check(File.ReadAllText(configPath) == original, "existing config must be preserved");
        Check(!IniFile.Parse(original).GetBool("ExampleApp", "Enabled", true), "safe disabled default");
        bool invalidRejected = false;
        try { AppConfiguration.Validate("[General]\nCheckIntervalMs=0\n"); }
        catch (ArgumentException) { invalidRejected = true; }
        Check(invalidRejected, "zero polling interval must be rejected");
        var offlineText = "[General]\r\nCheckIntervalMs=700\r\n; Preserve this comment\r\n[OfflineApp]\r\nName=Offline Example\r\nExe=MissingSyntheticProcess.exe\r\nEnabled=0\r\nCustomOption=keep\r\n[OtherApp]\r\nExe=OtherSyntheticProcess.exe\r\n";
        var editedOffline = AppConfiguration.UpdateValue(offlineText, "OfflineApp", "Enabled", "1");
        editedOffline = AppConfiguration.UpdateValue(editedOffline, "OfflineApp", "HideWhenMinimized", "1");
        Check(editedOffline.Contains("; Preserve this comment") && editedOffline.Contains("CustomOption=keep"), "table updates preserve comments and unknown options");
        Check(IniFile.Parse(editedOffline).GetBool("OfflineApp", "HideWhenMinimized", false), "table inserts missing values in the correct section");
        Check(IniFile.Parse(editedOffline).Get("OtherApp", "Exe", "") == "OtherSyntheticProcess.exe", "other software configuration preserved");

        Process helper = null;
        AppTray tray = null;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            helper = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--test-window", UseShellExecute = false
            });
            for (var attempt = 0; attempt < 100; attempt++)
            {
                helper.Refresh();
                hwnd = helper.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
                Thread.Sleep(50);
            }
            Check(hwnd != IntPtr.Zero, "synthetic test window opened");
            var candidate = WindowDiscovery.Scan().FirstOrDefault(item => item.Title == "Hide Tray Manager Synthetic Test");
            Check(candidate != null, "window discovery finds the synthetic window");
            Check(!string.IsNullOrWhiteSpace(candidate.ProcessPath), "discovery obtains executable path");
            var snapshot = ProcessSnapshot.FindByName(new[] { helper.ProcessName, helper.ProcessName.ToUpperInvariant(), "MissingSyntheticProcess" });
            try
            {
                Check(snapshot.Count == 2, "snapshot shares queries for duplicate process names");
                Check(snapshot[helper.ProcessName].Any(process => process.Id == helper.Id), "native snapshot finds the target process");
                Check(snapshot["MissingSyntheticProcess"].Count == 0, "native snapshot returns no unrelated processes");
            }
            finally { foreach (var process in snapshot.Values.SelectMany(matches => matches)) process.Dispose(); }
            int added;
            var updated = AppConfiguration.AddWindows(original, new[] { candidate, candidate }, out added);
            Check(added == 1, "duplicate discovered software must be added once");
            var ini = IniFile.Parse(updated);
            Check(ini.GetBool("App1", "HideWhenMinimized", false), "minimize to tray enabled");
            Check(!ini.GetBool("App1", "HideOnLaunch", true), "do not hide immediately");
            Check(!ini.GetBool("App1", "AutoStart", true), "do not automatically launch");
            Check(ini.Get("App1", "TitleContains", "missing") == "", "do not persist window titles");
            Check(AppConfiguration.IsConfigured(ini, candidate), "added software marked configured");

            tray = new AppTray(new ManagedApp {
                Name = "Synthetic Test", Exe = candidate.Exe, ProcessPath = candidate.ProcessPath,
                HideOnLaunch = false, HideWhenMinimized = true, DisableCloseButton = false
            });
            var processes = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase) {
                { Path.GetFileNameWithoutExtension(candidate.Exe), new List<Process> { helper } }
            };
            tray.Tick(processes);
            Check(User32.IsWindowVisible(hwnd), "adding software preserves visible window");
            User32.ShowWindow(hwnd, 6);
            Thread.Sleep(100);
            tray.Tick(processes);
            Check(!User32.IsWindowVisible(hwnd), "minimized window hidden to tray");
            typeof(AppTray).GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tray, null);
            Check(User32.IsWindowVisible(hwnd) && !User32.IsIconic(hwnd), "tray action restores window");
        }
        finally
        {
            if (hwnd != IntPtr.Zero) User32.ShowWindow(hwnd, User32.SW_RESTORE);
            if (tray != null) tray.Dispose();
            if (helper != null)
            {
                helper.CloseMainWindow();
                if (!helper.WaitForExit(3000)) helper.Kill();
                helper.Dispose();
            }
        }

        int reloads = 0;
        using (var form = new ConfigurationForm(configPath, delegate { reloads++; }))
        {
            var editor = (TextBox)typeof(ConfigurationForm).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            Check(editor.Lines.Length > 5, "configuration editor displays separate lines");
            var toggle = (Button)typeof(ConfigurationForm).GetField("toggleEditor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            var panel = (Panel)typeof(ConfigurationForm).GetField("editorPanel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            form.Show();
            Application.DoEvents();
            var list = (ListView)typeof(ConfigurationForm).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            // Replace discovered titles before rendering; this image is local-only test output.
            list.Items.Clear();
            list.Items.Add(new ListViewItem(new[] { "ExampleApp.exe", "Example Application", "未加入" }));
            var grid = (DataGridView)typeof(ConfigurationForm).GetField("configuredApps", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            var tabs = (TabControl)typeof(ConfigurationForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            Check(tabs.SelectedIndex == 0 && grid.Rows.Count == 1, "configured software table is the default view");
            Check((string)grid.Rows[0].Tag == "ExampleApp", "disabled offline software loaded into the table");
            grid.Rows[0].Cells["HideWhenMinimized"].Value = false;
            Check(!IniFile.Parse(editor.Text).GetBool("ExampleApp", "HideWhenMinimized", true), "table checkbox updates the configuration text");
            editor.Text = offlineText;
            Check(grid.Rows.Count == 2 && (string)grid.Rows[0].Cells["Name"].Value == "Offline Example", "text edits reload all configured software including offline apps");
            grid.Rows[0].Cells["Name"].Value = "Renamed Example";
            Check(IniFile.Parse(editor.Text).Get("OfflineApp", "Name", "") == "Renamed Example", "table name edits update the correct section");
            Check(editor.Text.Contains("CustomOption=keep"), "table editing retains unknown options");
            editor.Text = original;
            Check(editor.Lines.Length > 5, "pasted LF configuration is normalized for multiline display");
            toggle.PerformClick();
            Application.DoEvents();
            Check(panel.Visible, "editor expands");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configuration-preview.png"));
            }
            var saveMethod = typeof(ConfigurationForm).GetMethod("SaveText", BindingFlags.Instance | BindingFlags.NonPublic);
            saveMethod.Invoke(form, new object[] { "[General]\r\nCheckIntervalMs=900\r\n" });
            Check(reloads == 1 && File.ReadAllText(configPath).Contains("900"), "editor save applies configuration");
            File.WriteAllText(configPath, "[General]\r\nCheckIntervalMs=800\r\n", Encoding.UTF8);
            bool conflictRejected = false;
            try { saveMethod.Invoke(form, new object[] { "[General]\r\nCheckIntervalMs=1000\r\n" }); }
            catch (TargetInvocationException exception) { conflictRejected = exception.InnerException is InvalidOperationException; }
            Check(conflictRejected && File.ReadAllText(configPath).Contains("800"), "external edits preserved");
            toggle.PerformClick();
            Check(!panel.Visible, "editor collapses");
            form.Close();
        }

        var managerConfig = AppConfiguration.GetPath(AppDomain.CurrentDomain.BaseDirectory);
        AppConfiguration.EnsureExists(managerConfig);
        using (var manager = new TrayProxyContext())
        {
            var timer = (System.Windows.Forms.Timer)typeof(TrayProxyContext).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Check(!timer.Enabled, "polling disabled when no applications configured");
            var load = typeof(TrayProxyContext).GetMethod("LoadConfig", BindingFlags.Instance | BindingFlags.NonPublic);
            var tick = typeof(TrayProxyContext).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
            File.WriteAllText(managerConfig, "[General]\nCheckIntervalMs=700\n[Missing]\nExe=MissingSyntheticProcess.exe\nEnabled=1\n");
            load.Invoke(manager, null);
            tick.Invoke(manager, null);
            Check(timer.Enabled && timer.Interval == 3000, "missing targets use slower polling");
            File.WriteAllText(managerConfig, "[General]\nCheckIntervalMs=700\n[Running]\nExe=" + Path.GetFileName(Assembly.GetExecutingAssembly().Location) + "\nEnabled=1\nHideOnLaunch=0\nHideWhenMinimized=0\nDisableCloseButton=0\nTitleContains=Unmatched Synthetic Title\n");
            load.Invoke(manager, null);
            tick.Invoke(manager, null);
            Check(timer.Interval == 700, "running targets keep requested window check interval");
            var cacheField = typeof(TrayProxyContext).GetField("processCache", BindingFlags.Instance | BindingFlags.NonPublic);
            var cached = cacheField.GetValue(manager);
            tick.Invoke(manager, null);
            Check(ReferenceEquals(cached, cacheField.GetValue(manager)), "reuse process snapshot between discovery intervals");
        }
    }
}
