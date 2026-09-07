using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SEEDEditor;

/// <summary>
/// SEED エディタの WPF アプリケーションエントリポイント。
/// UI スレッド未処理例外・非 UI スレッド未処理例外・未観測タスク例外を捕捉し crash.log に書き出す。
/// </summary>
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

    /// <summary>
    /// コマンドライン引数（--headless / --scene）を MainWindow 生成より前に解析する。
    ///
    /// StartupUri による MainWindow のインスタンス化は base.OnStartup の中で起きるため、
    /// ここで解析しておけば MainWindow のコンストラクタから
    /// <see cref="Headless.EditorStartupOptions"/> を参照できる。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        Headless.EditorStartupOptions.Parse(e.Args);
        base.OnStartup(e);
    }
}

