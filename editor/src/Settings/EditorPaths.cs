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
    /// 構成フォルダ名（エディタルート直下）。
    /// 「利用者が書き換える設定」ではなく「リポジトリに入れて配る定義」の置き場。
    /// settings/ と分ける理由: settings/ は実行中に書き換わり git 管理外の値も混ざるが、
    /// config/ は読み取り専用の定義でレビュー対象（例: ランタイムのビルド構成一覧）。
    /// </summary>
    private const string CONFIG_DIR_NAME = "config";

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

    /// <summary>解決済みの構成フォルダ（存在しなければ null）。</summary>
    private static readonly Lazy<string?> _configDir = new(ResolveConfigDir);

    /// <summary>
    /// エディタ構成フォルダ（editor/config）の絶対パス。
    /// リポジトリに入っている読み取り専用の定義ファイルの置き場
    /// （現在はランタイムのビルド構成一覧 runtime_build_configs.json）。
    ///
    /// <para>
    /// 見つからない場合は null を返す（settings/ と違って作らない）。
    /// 中身はリポジトリから配られるものなので、空のフォルダを作っても意味が無く、
    /// 読み手はそれぞれの組み込み既定へフォールバックすべきだから。
    /// </para>
    /// </summary>
    public static string? ConfigDir => _configDir.Value;

    /// <summary>
    /// 構成フォルダを解決する。
    ///   ① 開発配置: editor/bin/&lt;Cfg&gt;/&lt;tfm&gt;/ から 3 階層上の editor/config
    ///   ② 配布配置: exe の隣の config/
    /// どちらも無ければ null。
    /// </summary>
    private static string? ResolveConfigDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var devDir = Path.GetFullPath(Path.Combine(baseDir, EDITOR_ROOT_RELATIVE, CONFIG_DIR_NAME));
        if (Directory.Exists(devDir)) return devDir;

        var distDir = Path.GetFullPath(Path.Combine(baseDir, CONFIG_DIR_NAME));
        if (Directory.Exists(distDir)) return distDir;

        return null;
    }
}
