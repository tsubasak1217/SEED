using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SEEDEditor;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText("crash.log", e.ExceptionObject?.ToString() ?? "(null)"); }
            catch { }
        };
        DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText("crash.log", "DISPATCHER: " + e.Exception?.ToString() ?? "(null)"); }
            catch { }
            e.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { File.AppendAllText("crash.log", "TASK: " + e.Exception?.ToString() ?? "(null)"); }
            catch { }
        };
    }
}

