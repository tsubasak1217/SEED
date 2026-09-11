// ============================================================
//  JumpListSpec.cs — タスクバーのジャンプリスト（右クリックメニュー）に載せる
//  「最近のプロジェクト」項目を、最近の一覧から組み立てる純粋ロジック
//
//  WPF の JumpList / JumpTask 型には依存しない（コンソールのテストから検証できるようにするため）。
//  実際に OS へ登録するのは ProjectJumpList（WPF 側）。
//
//  Visual Studio の「最近使ったもの」と同じ使い勝手を狙う:
//   - タスクバーの SEED アイコンを右クリック → 「最近」欄 → 項目クリックで .seedproj を引数に
//     SEEDEditor.exe が起動し、そのままエディタが開く（位置引数の .seedproj は
//     EditorStartupOptions が解釈する）。
// ============================================================
using System;
using System.Collections.Generic;

namespace SEEDEditor.Project;

/// <summary>
/// ジャンプリストの 1 項目（WPF 非依存の仕様）。
/// </summary>
/// <param name="Title">表示名（プロジェクトの表示名）。</param>
/// <param name="Description">ツールチップ（.seedproj の絶対パス）。</param>
/// <param name="ProjectFilePath">開く .seedproj の絶対パス。</param>
/// <param name="Arguments">SEEDEditor.exe へ渡すコマンドライン引数（引用符付きのパス）。</param>
public sealed record JumpListItemSpec(
    string Title,
    string Description,
    string ProjectFilePath,
    string Arguments);

/// <summary>
/// 最近のプロジェクト一覧 → ジャンプリスト項目の変換。
/// </summary>
public static class ProjectJumpListBuilder
{
    /// <summary>ジャンプリストに出す「最近」欄のカテゴリ名。</summary>
    public const string RECENT_CATEGORY = "最近";

    /// <summary>
    /// 「最近」欄に載せる最大件数。
    /// OS 側の既定上限（10 前後）に合わせる。多すぎるとメニューが縦に伸びて使いにくい。
    /// </summary>
    public const int MAX_RECENT_ITEMS = 10;

    /// <summary>スタート画面を開くタスクの表示名。</summary>
    public const string START_WINDOW_TASK_TITLE = "スタート画面を開く";

    /// <summary>スタート画面を開くタスクのツールチップ。</summary>
    public const string START_WINDOW_TASK_DESCRIPTION = "最近のプロジェクト一覧・新規作成";

    /// <summary>
    /// 最近の一覧からジャンプリスト項目を組み立てる。
    /// </summary>
    /// <param name="entries">最近のプロジェクト（新しい順）。</param>
    /// <param name="fileExists">.seedproj の実在判定（テストから差し替えられるよう注入）。</param>
    /// <param name="maxItems">最大件数。</param>
    /// <returns>実在するものだけを、順序を保ったまま最大件数まで並べた項目。</returns>
    public static IReadOnlyList<JumpListItemSpec> Build(
        IEnumerable<RecentProjectEntry> entries,
        Func<string, bool> fileExists,
        int maxItems = MAX_RECENT_ITEMS)
    {
        var result = new List<JumpListItemSpec>();
        // 同じパスが重複して並ばないようにする（大文字小文字は Windows のパス規則に合わせて無視）。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (result.Count >= maxItems) break;
            var path = entry.Path?.Trim() ?? string.Empty;
            if (path.Length == 0) continue;
            if (!seen.Add(path)) continue;
            // 消えたプロジェクトは載せない（クリックしてエラーになる項目を出さない）。
            if (!fileExists(path)) continue;

            var title = string.IsNullOrWhiteSpace(entry.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : entry.Name.Trim();

            result.Add(new JumpListItemSpec(
                Title:           title,
                Description:     path,
                ProjectFilePath: path,
                Arguments:       QuoteArgument(path)));
        }
        return result;
    }

    /// <summary>
    /// コマンドライン引数として安全に渡せるよう、パスを二重引用符で囲む
    /// （空白や日本語を含むパスをそのまま 1 引数として受け取らせる）。
    /// </summary>
    public static string QuoteArgument(string path) => "\"" + path + "\"";
}
