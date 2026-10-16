using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Matdol;

public static class StartupTrace
{
    public static bool On;
    private static readonly StringBuilder _log = new();

    public static void Mark(string what)
    {
        if (!On) return;
        try
        {
            double ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            _log.AppendLine($"{ms,8:0} ms  {what}");
        }
        catch { }
    }

    public static void Flush()
    {
        if (!On || _log.Length == 0) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "matdol-startup.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" + _log);
            _log.Clear();
        }
        catch { }
    }
}
