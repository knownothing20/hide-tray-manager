using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class ProcessSnapshot
{
    public static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    // Process.GetProcessesByName enumerates the full process table internally.
    // One native snapshot avoids repeating that work for each configured app.
    public static Dictionary<string, List<Process>> FindByName(IEnumerable<string> names)
    {
        var result = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (!result.ContainsKey(name)) result.Add(name, new List<Process>());
        if (result.Count == 0) return result;

        using (var snapshot = CreateToolhelp32Snapshot(2, 0))
        {
            if (snapshot.IsInvalid) return result;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                List<Process> matches;
                if (!result.TryGetValue(Path.GetFileNameWithoutExtension(entry.ExeFile), out matches)) continue;
                try { matches.Add(Process.GetProcessById((int)entry.ProcessId)); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            } while (Process32Next(snapshot, ref entry));
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
