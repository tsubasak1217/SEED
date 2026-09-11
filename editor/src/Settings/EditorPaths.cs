// ============================================================
//  EditorPaths.cs — エディタ本体（エンジン側）のパス解決
//
//  【役割】
//  「プロジェクトに属さない」パスだけをここで解決する。
//  具体的にはエディタ自身の設定フォルダ（editor/settings）。
//  レイアウト・環境設定・最近開いたプロジェクト一覧はプロジェクトを跨いで
//  共有される情報なので、プロジェクトフォルダではなくここへ置く。
//
//  【なぜ MainWindow から切り出したのか】
//  従来は MainWindow の静的フィールドで解決していたが、スタート画面
//  （MainWindow を作る前）からも設定フォルダが要るようになった。
//  MainWindow の静的メンバへ触れると型初期化子が走り、pack:// URI の
//  BitmapImage 生成など WPF リソースに依存する初期化まで一緒に動いてしまう。
//  プロジェクト決定前に触れる必要のあるパスはこのクラスへ分離する。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Settings;

/// <summary>
/// エディタ本体のパス解決（プロジェクトに依存しないもの）。
/// </summary>
public static class EditorPaths
{
    /// <summary>設定フォルダ名（エディタルート直下）。</summary>
    private const string SETTINGS_DIR_NAME = "settings";

    /// <summary>
    /// 実行ファイルの置き場からエディタルートまで遡る相対パス。
    /// 開発ビルドは editor/bin/&lt;Cfg&gt;/net9.0-windows/ に出るため 3 階層。
    /// </summary>
    private const string EDITOR_ROOT_RELATIVE = @"..\..\..\";

    /// <summary>解決済みの設定フォルダ（初回アクセス時に 1 度だけ作成・確定する）。</summary>
    private static readonly Lazy<string> _settingsDir = new(ResolveSettingsDir);

    /// <summary>
    /// エディタ設定フォルダの絶対パス（無ければ作る）。
    /// レイアウト・環境設定・最近開いたプロジェクト一覧の置き場。
    /// </summary>
    public static string SettingsDir => _settingsDir.Value;

    /// <summary>設定フォルダを解決し、存在を保証する。</summary>
    private static string ResolveSettingsDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, EDITOR_ROOT_RELATIVE, SETTINGS_DIR_NAME));
        try { Directory.CreateDirectory(dir); }
        catch { /* 作れなくても読み書き時に再度失敗するだけ。起動は止めない */ }
        return dir;
    }
}
