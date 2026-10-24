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

    /// <summary>从配置文本中移除一个应用段（含其键值行）；[General] 不允许移除。</summary>
    public static string RemoveSection(string text, string section)
    {
        if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能移除 [General] 段。");
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var output = new List<string>();
        var inTarget = false;
        var found = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                inTarget = trimmed.Substring(1, trimmed.Length - 2).Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inTarget)
                {
                    found = true;
                    continue;
                }
            }
            if (inTarget)
                continue;
            output.Add(line);
        }
        if (!found)
            throw new ArgumentException("找不到对应的配置段。");
        // 收敛段落移除后留下的连续空行，保持配置文件整洁。
        while (output.Count > 0 && output[0].Trim().Length == 0)
            output.RemoveAt(0);
        while (output.Count > 0 && output[output.Count - 1].Trim().Length == 0)
            output.RemoveAt(output.Count - 1);
        for (var index = output.Count - 2; index >= 0; index--)
        {
            if (output[index].Trim().Length == 0 && index > 0 && output[index - 1].Trim().Length == 0)
                output.RemoveAt(index);
        }
        return string.Join("\r\n", output.ToArray()) + "\r\n";
    }

    public static string UpdateValue(string text, string section, string key, string value)
    {
        if (value.Contains("\r") || value.Contains("\n"))
            throw new ArgumentException("配置值不能包含换行。");
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        var current = "";
        var insertAt = -1;
        var replaced = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                current = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase))
                continue;
            insertAt = index + 1;
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
            var equals = lines[index].IndexOf('=');
            if (equals > 0 && lines[index].Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = lines[index].Substring(0, equals + 1) + value;
                replaced = true;
            }
        }
        if (insertAt < 0) throw new ArgumentException("找不到对应的配置段。");
        if (!replaced) lines.Insert(insertAt, key + "=" + value);
        return string.Join("\r\n", lines);
    }
}
