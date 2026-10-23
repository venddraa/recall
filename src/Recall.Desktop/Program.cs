using Avalonia;
using System.Diagnostics;
using Recall.Core.Diagnostics;
using Recall.Core.Storage;

namespace Recall.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var log = CreateLocalLog();
        if (log is not null)
        {
            Trace.Listeners.Add(log);
            log.SessionStarted();
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Recall terminated unexpectedly: {0}", exception);
            throw;
        }
        finally
        {
            if (log is not null)
            {
                Trace.Listeners.Remove(log);
                log.SessionEnded();
            }
        }
    }

    private static LocalFileTraceListener? CreateLocalLog()
    {
        try
        {
            return new(RecallDataPath.GetLogFilePath(Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "Recall" })
            .LogToTrace();
    }
}
