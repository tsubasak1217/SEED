// ============================================================
//  LocaleGridRow.cs — 文字列表の行 1 つの表示用の入れ物（DataGrid の 1 行）
//
//  キーの列（Key）・種類の列（KindLabel）・言語ごとの升目（Cells。列は Cells[i].Text で結ぶ）。
//  行の組み立て直しのたびに作り直すと、編集中の升目・スクロールの位置が失われるので、キーの並びが
//  変わらない書き換え（升目の編集）では Apply で中身だけを入れ直す。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.Localization.Model;

namespace SEEDEditor.Panels.Localization;

/// <summary>文字列表の行 1 つ（表示用）。</summary>
public sealed class LocaleGridRow
{
    /// <summary>升目（言語の一覧の順）。</summary>
    private readonly List<LocaleGridCell> _cells = new();

    /// <summary>モデルの行（最後に入れたもの）。</summary>
    public LocaleRow Source { get; private set; }

    /// <summary>キー。</summary>
    public string Key => Source.Key;

    /// <summary>種類の列の文言（普通のキーは空）。</summary>
    public string KindLabel => Source.Kind switch
    {
        LocaleRowKind.PluralForm => LocalizationPanelMessages.KindPluralForm,
        LocaleRowKind.PluralPlain => LocalizationPanelMessages.KindPluralPlain,
        _ => string.Empty,
    };

    /// <summary>升目（列は Cells[i].Text で結ぶ）。</summary>
    public IReadOnlyList<LocaleGridCell> Cells => _cells;

    /// <summary>行を作る。</summary>
    /// <param name="row">モデルの行。</param>
    /// <param name="commit">升目の編集の確定をパネルへ渡す関数。</param>
    public LocaleGridRow(LocaleRow row, Action<LocaleGridCell, string?> commit)
    {
        Source = row;
        foreach (var cell in row.Cells) _cells.Add(new LocaleGridCell(row.Key, cell, commit));
    }

    /// <summary>
    /// 同じキー・同じ言語の数のモデルの行を入れ直す（升目の中身だけ。違えば false で何もしない）。
    /// </summary>
    /// <param name="row">モデルの行。</param>
    /// <returns>入れ直せたら true。</returns>
    public bool Apply(LocaleRow row)
    {
        if (!string.Equals(row.Key, Source.Key, StringComparison.Ordinal) || row.Cells.Count != _cells.Count) return false;
        Source = row;
        for (int i = 0; i < _cells.Count; i++) _cells[i].Apply(row.Cells[i]);
        return true;
    }
}
