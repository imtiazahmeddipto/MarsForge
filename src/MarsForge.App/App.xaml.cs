using System;
using System.Windows;
using System.Windows.Threading;

namespace MarsForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Core.Log.FilePath = System.IO.Path.Combine(System.IO.Directory.Exists(AppFiles.Lib) ? AppFiles.Lib : AppFiles.AppFolder,
                                                   "MarsForge.log");                                   // in the lib folder
        Core.Log.Write("MarsForge started");
        AppFiles.Setup();
        DispatcherUnhandledException += OnError;
        base.OnStartup(e);
    }

    static void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Core.Log.Error("Unhandled", e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "MarsForge", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
