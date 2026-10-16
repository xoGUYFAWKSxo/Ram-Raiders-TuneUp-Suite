using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TuneUpEngine;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace TuneUpUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        UiLog.Info($"[UI] Starting, version {typeof(App).Assembly.GetName().Version}.");

        // An unexpected error used to close the window with no trace. Now it's logged, and errors
        // on the UI thread are reported without taking the whole app down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            UiLog.Error($"[UI] Unhandled exception: {args.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            UiLog.Error($"[UI] Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiLog.Error($"[UI] Unhandled exception: {e.Exception}");
        MessageBox.Show(
            $"Something went wrong:\n{e.Exception.Message}\n\nThe details were saved to:\n{UiLog.CurrentFile ?? UiLog.Directory}",
            "Galactic Tune-Up", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

// The UI's log: %LocalAppData%\GalacticTuneUp\logs\ui-yyyyMMdd.log (per user, since the UI
// runs as the signed-in user). The engine keeps its own log in ProgramData.
public static class UiLog
{
    private static readonly FileLog Log = new(Directory, "ui");

    public static string Directory => Path.Combine(
        AppFolders.Root(Environment.SpecialFolder.LocalApplicationData), "logs");

    public static string? CurrentFile => Log.Enabled ? Log.CurrentFile : null;

    public static void Info(string message) => Log.Write("INFO", message);
    public static void Warn(string message) => Log.Write("WARN", message);
    public static void Error(string message) => Log.Write("ERROR", message);
}
