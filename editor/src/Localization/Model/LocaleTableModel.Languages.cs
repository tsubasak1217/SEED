// ============================================================
//  LocaleTableModel.Languages.cs — 言語の編集（追加・一覧から外す・既定・fallback・名前）
//
//  【約束】（docs/localization.md §15）
//    - 言語の追加は index.json の末尾に { code, name, fallback: null } を足し、表 <code>.json を作る。
//      置き場に同じ名前の表が既にあれば、それを読んで使う（一覧から外した言語を足し直すと元の表が戻る）
//    - 言語を一覧から外しても表のファイルは消さない（index.json に載らない表は実行中に読まれない）。
//      外した言語を指す fallback は null に、既定の言語だったら残りの先頭を既定にする
//    - fallback は自分自身を指せない（輪は実行中に止まるが意味が無いので断る）
// ============================================================

using System;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

public sealed partial class LocaleTableModel
{
    /// <summary>
    /// 言語を足す（index.json の末尾に足し、表の列を作る）。
    /// </summary>
    /// <param name="code">言語のコード（書きそろえる）。</param>
    /// <param name="name">言語の名前（空ならコード）。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult AddLanguage(string code, string? name)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        if (!LocaleCodeRules.TryValidate(code, out var normalized, out var reason)) return LocaleEditResult.Fail(reason);
        if (_index.Find(normalized) is not null) return LocaleEditResult.Fail($"言語「{normalized}」は既にあります");

        _index.AddLanguage(normalized, name);

        // ── 表の列: 置き場に同じ名前の表があれば読む。無ければ空の表を作る（保存でファイルを作る）──
        var content = _readTable(normalized);
        var column = LocaleLanguageColumn.Load(normalized, content, _index.Document.NewLine);
        if (!content.Exists) column.MarkNew();
        _columns.Add(column);
        return LocaleEditResult.Done;
    }

    /// <summary>
    /// 言語を一覧から外す（index.json から消す。表のファイルは消さない）。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult RemoveLanguage(string code)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var column = FindColumn(code);
        if (column is null || !_index.RemoveLanguage(code)) return LocaleEditResult.Fail($"言語「{code}」は一覧にありません");
        _columns.Remove(column);
        return LocaleEditResult.Done;
    }

    /// <summary>既定の言語を変える。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult SetDefaultLanguage(string code)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var language = _index.Find(code);
        if (language is null) return LocaleEditResult.Fail($"言語「{code}」は一覧にありません");
        if (string.Equals(language.Code, _index.DefaultCode, StringComparison.OrdinalIgnoreCase)) return LocaleEditResult.Unchanged;
        _index.SetDefault(language.Code);
        return LocaleEditResult.Done;
    }

    /// <summary>
    /// 言語の fallback（表に無いキーを次に探す言語）を変える。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="fallback">次に探す言語（null で既定の言語へ直接落とす）。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult SetFallback(string code, string? fallback)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var language = _index.Find(code);
        if (language is null) return LocaleEditResult.Fail($"言語「{code}」は一覧にありません");

        string? target = null;
        if (fallback is not null)
        {
            var found = _index.Find(fallback);
            if (found is null) return LocaleEditResult.Fail($"言語「{fallback}」は一覧にありません");
            if (string.Equals(found.Code, language.Code, StringComparison.OrdinalIgnoreCase))
                return LocaleEditResult.Fail("自分自身は fallback にできません");
            target = found.Code;
        }

        if (string.Equals(language.Fallback, target, StringComparison.OrdinalIgnoreCase)) return LocaleEditResult.Unchanged;
        _index.SetFallback(language.Code, target);
        return LocaleEditResult.Done;
    }

    /// <summary>言語の名前（その言語での呼び名）を変える。</summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="name">名前（空はだめ）。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult SetLanguageName(string code, string name)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var language = _index.Find(code);
        if (language is null) return LocaleEditResult.Fail($"言語「{code}」は一覧にありません");
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return LocaleEditResult.Fail("言語の名前を入力してください");
        if (string.Equals(language.Name, trimmed, StringComparison.Ordinal)) return LocaleEditResult.Unchanged;
        _index.SetName(language.Code, trimmed);
        return LocaleEditResult.Done;
    }

    /// <summary>言語の探す順（その言語 → fallback … → 既定の言語。LocaleIndex.BuildChain）。見出しのツールチップ用。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>探す順のコード。</returns>
    public System.Collections.Generic.IReadOnlyList<string> FallbackChain(string code) => _index.Canonical.BuildChain(code);
}
