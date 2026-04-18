using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using SEEDEditor.Viewport;

namespace SEEDEditor;

public partial class MainWindow : Window
{
    // Rust ランタイムの実行ファイルパス（自身と同じディレクトリを基準に解決）
    private static readonly string RuntimeExePath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SEED.exe");

    private RuntimeViewport? _viewport;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ── イベントハンドラ ─────────────────────────────────────────

    /// <summary>Play ボタン: HwndHost を生成して Rust ランタイムを起動する。</summary>
    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_viewport is { IsRunning: true }) return;

        // 既存の viewport があれば破棄する
        RemoveViewport();

        _viewport = new RuntimeViewport(RuntimeExePath);
        ViewportBorder.Child = _viewport;

        BtnPlay.IsEnabled = false;
        BtnStop.IsEnabled = true;
    }

    /// <summary>Stop ボタン: Rust プロセスを終了し HwndHost を破棄する。</summary>
    private void OnStop(object sender, RoutedEventArgs e)
    {
        RemoveViewport();

        BtnPlay.IsEnabled = true;
        BtnStop.IsEnabled = false;
    }

    /// <summary>ウィンドウを閉じるときにランタイムプロセスも終了させる。</summary>
    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        RemoveViewport();
    }

    // ── プライベートメソッド ─────────────────────────────────────

    private void RemoveViewport()
    {
        if (_viewport is null) return;

        _viewport.StopRuntime();
        ViewportBorder.Child = null; // HwndHost.DestroyWindowCore が呼ばれる
        _viewport = null;
    }
}
