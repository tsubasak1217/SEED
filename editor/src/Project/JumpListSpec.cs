// ============================================================
//  JumpListSpec.cs — タスクバーのジャンプリスト（右クリックメニュー）に載せる
//  「最近」欄の項目を、最近の一覧から組み立てる純粋ロジック
//
//  WPF の JumpList / JumpPath 型には依存しない（コンソールのテストから検証できるようにするため）。
//  実際に OS へ登録するのは ProjectJumpList（WPF 側）。
//
//  Visual Studio の「最近使ったもの」と同じ使い勝手を狙う:
//   - 項目は .seedproj そのもの（JumpPath）として登録する。クリックすると .seedproj の関連付け
//     （SEEDEditor.exe）で開き、右クリックには Windows 標準の「フォルダーの場所を開く」が付く。
//   - 表示名は Windows が .seedproj のファイル名から決める（JumpPath は表示名を持てない）。
// ============================================================
using System;
using System.Collections.Generic;

namespace SEEDEditor.Project;

/// <summary>
/// ジャンプリストの 1 項目（WPF 非依存の仕様）。
/// </summary>
/// <param name="Title">プロジェクトの表示名（ログ・スタート画面向け。JumpPath には載らない）。</param>
/// <param name="ProjectFilePath">開く .seedproj の絶対パス（JumpPath に登録する実体）。</param>
public sealed record JumpListItemSpec(
    string Title,
    string ProjectFilePath);

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

            result.Add(new JumpListItemSpec(Title: title, ProjectFilePath: path));
        }
        return result;
    }
}
