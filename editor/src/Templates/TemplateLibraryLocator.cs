// ============================================================
//  TemplateLibraryLocator.cs — テンプレートライブラリの置き場所解決
//
//  【役割】
//  「エンジンが配っているテンプレート一式（templates/）がどこにあるか」を
//  1 か所で決める。呼び出し側はパスの組み立てを一切知らなくてよい。
//
//  【なぜプロジェクトの外にあるのか】
//  templates/ は「新規作成の見本」であって、特定ゲームの資産ではない。
//  プロジェクト機構（<ProjectRoot>/<Name>.seedproj + assets/ + plugins/）の導入で
//  assets/ は「そのゲームが実際に使う物だけ」を置く場所になったため、
//  見本はエンジン側（リポジトリ直下 templates/）へ出し、
//  必要な物だけをプロジェクトへコピーして使う形にした。
//
//  【探索の順番】（MainWindow.ResolveRuntimePath と同じ流儀）
//   0. 環境変数 SEED_TEMPLATE_LIBRARY（実在するフォルダのときだけ採用）
//   1. 開発時: exe から ..\..\..\..\templates（= リポジトリ直下の templates/）
//   2. リリース時: exe の隣の templates/
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリ（エンジン同梱の templates/ フォルダ）の場所を解決する。
/// 状態を持たない静的ユーティリティ。
/// </summary>
public static class TemplateLibraryLocator
{
    // ============================================================
    //  探索のパラメータ（データとして 1 か所に置く）
    // ============================================================

    /// <summary>
    /// ライブラリの置き場所を明示指定する環境変数名。
    /// 別のテンプレート集を差し替えて試すとき、および自動テストで使う。
    /// </summary>
    public const string OverrideEnvVar = "SEED_TEMPLATE_LIBRARY";

    /// <summary>ライブラリのフォルダ名（開発時・リリース時で共通）。</summary>
    public const string LibraryFolderName = "templates";

    /// <summary>
    /// 開発時に exe の場所（editor/bin/&lt;構成&gt;/&lt;TFM&gt;/）から
    /// リポジトリ直下へ遡るための相対パス。
    /// </summary>
    private const string RepositoryRootRelativePath = @"..\..\..\..";

    // ============================================================
    //  解決
    // ============================================================

    /// <summary>
    /// テンプレートライブラリのルートフォルダを解決する。
    /// </summary>
    /// <returns>
    /// 実在するライブラリルートの絶対パス。どこにも無ければ null
    /// （＝ライブラリ機能を無効表示にするための合図）。
    /// </returns>
    public static string? Resolve()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (candidate.Length == 0) continue;
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// 解決を試み、見つからない場合でも「本来ここにあるはず」のパスを返す。
    /// エラーメッセージに「どこを探したか」を出すためのもの。
    /// </summary>
    /// <returns>ライブラリルートの絶対パス（実在は保証しない）。</returns>
    public static string ResolveOrExpectedPath() =>
        Resolve() ?? Path.GetFullPath(ExeAdjacentPath());

    /// <summary>
    /// 探索する候補を優先順に列挙する。
    /// ここを読めば「どこを見ているか」が分かるように、判定と順番をこの 1 か所へ集約する。
    /// </summary>
    /// <returns>候補パス（実在チェックは呼び出し側で行う）。</returns>
    private static System.Collections.Generic.IEnumerable<string> EnumerateCandidates()
    {
        // 0) 環境変数による明示指定を最優先する
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        yield return string.IsNullOrWhiteSpace(overridePath) ? "" : overridePath.Trim();

        // 1) 開発時: editor/bin/<構成>/<TFM>/ → リポジトリ直下の templates/
        yield return Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, RepositoryRootRelativePath, LibraryFolderName));

        // 2) リリース時: exe の隣の templates/
        yield return ExeAdjacentPath();
    }

    /// <summary>exe と同じフォルダに置かれたライブラリのパスを組み立てる。</summary>
    /// <returns>exe 隣の templates/ の絶対パス。</returns>
    private static string ExeAdjacentPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LibraryFolderName);
}
