using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static class AppConfiguration
{
    public const string DefaultContent = @"[General]
CheckIntervalMs=700

; Enable and customize this example for your own application.
[ExampleApp]
Enabled=0
Name=Example App
Path=
Exe=ExampleApp.exe
ProcessPath=
TitleContains=
AutoStart=0
HideOnLaunch=1
HideWhenMinimized=1
DisableCloseButton=1
ShowNotifications=0
IconPath=
";

    public static string GetPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "local", "HideTrayManager.ini");
    }

    public static void EnsureExists(string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        if (!File.Exists(configPath))
            File.WriteAllText(configPath, DefaultContent, Encoding.UTF8);
    }

    public static void Validate(string text)
    {
        var ini = IniFile.Parse(text);
        int interval;
        if (!int.TryParse(ini.Get("General", "CheckIntervalMs", "700"), out interval) || interval <= 0)
            throw new ArgumentException("检查间隔必须是大于 0 的整数。");
    }

    public static bool IsConfigured(IniFile ini, WindowCandidate candidate)
    {
        return ini.SectionNames.Any(section =>
            ini.GetBool(section, "Enabled", true) &&
            string.Equals(ini.Get(section, "Exe", Path.GetFileName(ini.Get(section, "Path", ""))),
                candidate.Exe, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(ini.Get(section, "ProcessPath", "")) ||
             string.Equals(ini.Get(section, "ProcessPath", ""), candidate.ProcessPath, StringComparison.OrdinalIgnoreCase)));
    }

    public static string AddWindows(string text, IEnumerable<WindowCandidate> candidates, out int added)
    {
        Validate(text);
        added = 0;
        var result = new StringBuilder(text.TrimEnd());
        foreach (var candidate in candidates)
        {
            var ini = IniFile.Parse(result.ToString());
            if (IsConfigured(ini, candidate))
                continue;
            var section = "App";
            var suffix = 1;
            while (ini.SectionNames.Contains(section + suffix, StringComparer.OrdinalIgnoreCase))
                suffix++;
            result.AppendLine().AppendLine().AppendLine("[" + section + suffix + "]");
            result.AppendLine("Enabled=1");
            result.AppendLine("Name=" + SafeValue(Path.GetFileNameWithoutExtension(candidate.Exe)));
            result.AppendLine("Path=" + SafeValue(candidate.ProcessPath));
            result.AppendLine("Exe=" + SafeValue(candidate.Exe));
            result.AppendLine("ProcessPath=" + SafeValue(candidate.ProcessPath));
            result.AppendLine("TitleContains=");
            result.AppendLine("AutoStart=0");
            result.AppendLine("HideOnLaunch=0");
            result.AppendLine("HideWhenMinimized=1");
            result.AppendLine("DisableCloseButton=0");
            result.AppendLine("ShowNotifications=0");
            result.AppendLine("IconPath=");
            added++;
        }
        return result.ToString();
    }

    private static string SafeValue(string value)
    {
        return (value ?? "").Replace("\r", "").Replace("\n", "");
    }
}
