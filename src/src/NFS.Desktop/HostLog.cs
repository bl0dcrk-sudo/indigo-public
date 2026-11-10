namespace NFS.Desktop;

/// <summary>Console log; errors also go to <c>NFS.Desktop.log</c> next to the exe (a WinExe started from Explorer has
/// no console, so a startup failure would otherwise vanish without a trace).</summary>
internal static class HostLog
{
    public static readonly string ErrorLogPath = Path.Combine(AppContext.BaseDirectory, "NFS.Desktop.log");

    public static void Info(string s) { Console.Out.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); Console.Out.Flush(); }

    public static void Error(string s)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] ERROR {s}";
        Console.Error.WriteLine(line); Console.Error.Flush();
        try { File.AppendAllText(ErrorLogPath, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
