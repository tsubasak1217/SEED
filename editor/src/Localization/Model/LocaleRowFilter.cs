// ============================================================
//  LocaleRowFilter.cs — 文字列表の行の絞り込み（検索・未訳だけ。純粋な計算）
//
//  検索はキーと各言語の文のどちらかに含まれれば当たり（大文字小文字を区別しない）。
//  「未訳だけ」は未訳の升目（LocaleCellStates.IsMissing）が 1 つでもある行だけ。
// ============================================================

using System;
using SEEDEditor.Localization.Model;

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の行の絞り込み。</summary>
public static class LocaleRowFilter
{
    /// <summary>行が絞り込みに当たるか。</summary>
    /// <param name="row">行。</param>
    /// <param name="search">検索の文字（空・空白だけなら絞らない）。</param>
    /// <param name="missingOnly">未訳の升目のある行だけにするか。</param>
    /// <returns>当たれば true。</returns>
    public static bool Matches(LocaleRow row, string? search, bool missingOnly)
    {
        if (missingOnly && !row.HasMissing) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        string needle = search.Trim();
        if (row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var cell in row.Cells)
        {
            if (cell.Text is not null && cell.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
