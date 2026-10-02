// ============================================================
//  LocaleIndexDocument.cs — 言語の一覧（index.json）を編集する（純粋な計算）
//
//  【持つもの】
//    - 形を保った文書（LocaleJsonDocument。説明の鍵・知らない鍵・言語の要素の中の鍵も書き戻しで残す）
//    - 実行中と同じ読み方の結果（LocaleIndex.Parse。コードの書きそろえ・重なり・fallback の確かめ・既定の言語）
//  画面に出す言語の並び・既定の言語・警告は LocaleIndex（実行中の見え方）から取り、書き換えは文書の項目へ行う。
//  言語の要素は「"languages" の何番目か」で引く（LocaleIndex と同じく、同じコードは先のものを使う）。
//
//  【書き換え】言語の追加（末尾に code・name・fallback=null）・削除（後ろの要素の番号を詰める）・
//  既定の言語・fallback・名前。書き換えのたびに書き出して LocaleIndex で読み直す（実行中の見え方と食い違わない）。
//  鍵の名前は LocaleIndex の定数（KeyDefault・KeyLanguages・KeyCode …）をそのまま使う。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using SEED.Localization;
using SEEDEditor.Localization.Json;

namespace SEEDEditor.Localization.Model;

/// <summary>言語の一覧（index.json）の編集。</summary>
public sealed class LocaleIndexDocument
{
    /// <summary>言語の要素の道筋の長さ（"languages" / 番号 / 鍵）。</summary>
    private const int LanguageFieldPathLength = 3;

    /// <summary>道筋の中の言語の番号の位置。</summary>
    private const int LanguageNumberDepth = 1;

    /// <summary>道筋の中の言語の鍵の位置。</summary>
    private const int LanguageFieldDepth = 2;

    /// <summary>形を保った文書。</summary>
    private readonly LocaleJsonDocument _document;

    /// <summary>最後に読んだ・保存した中身（未保存の判定に使う）。</summary>
    private string _savedText;

    /// <summary>実行中と同じ読み方の結果。</summary>
    public LocaleIndex Canonical { get; private set; }

    /// <summary>形を保った文書（改行の流儀を新しい言語の表へ引き継ぐのに使う）。</summary>
    public LocaleJsonDocument Document => _document;

    /// <summary>ファイルがあったか（無ければ保存で作る）。</summary>
    public bool FileExists { get; private set; }

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>JSON として読めたか（読めなければ書き換え・保存をしない）。</summary>
    public bool IsValid => _document.IsValid;

    /// <summary>言語（実行中と同じ並び・書きそろえたコード）。</summary>
    public IReadOnlyList<LocaleLanguage> Languages => Canonical.Languages;

    /// <summary>既定の言語のコード（言語が 0 件なら空）。</summary>
    public string DefaultCode => Canonical.DefaultCode;

    /// <summary>文書から作る。</summary>
    private LocaleIndexDocument(LocaleJsonDocument document, string savedText, bool fileExists)
    {
        _document = document;
        _savedText = savedText;
        FileExists = fileExists;
        Canonical = LocaleSafeParse.Index(savedText, LocalePaths.IndexFileName);
    }

    /// <summary>index.json の中身から作る（無いファイルは null で渡す＝空の一覧として作り、保存で作る）。</summary>
    /// <param name="text">中身（無ければ null）。</param>
    /// <param name="newLineForNew">新しく作るときの改行。</param>
    /// <returns>言語の一覧。</returns>
    public static LocaleIndexDocument Load(string? text, string? newLineForNew = null)
    {
        if (text is null)
        {
            var empty = LocaleJsonDocument.CreateEmpty(newLineForNew);
            // 無いファイルは「空の一覧」を書いたことにする（何か足せば未保存になる）
            return new LocaleIndexDocument(empty, empty.Write(), fileExists: false);
        }
        return new LocaleIndexDocument(LocaleJsonDocument.Parse(text), text, fileExists: true);
    }

    // ============================================================
    //  引く
    // ============================================================

    /// <summary>言語をコードで引く（大文字小文字・"_" と "-" を区別しない。LocaleIndex.Find）。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>言語（無ければ null）。</returns>
    public LocaleLanguage? Find(string code) => Canonical.Find(code);

    /// <summary>
    /// 言語の要素の番号を引く（"languages" の中で、書きそろえたコードが同じ最初の要素）。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>番号（無ければ -1）。</returns>
    private int FindElement(string code)
    {
        string normalized = LocaleIndex.Normalize(code);
        for (int i = 0; i < _document.Count; i++)
        {
            var entry = _document.Entries[i];
            if (!IsLanguageField(entry.Path, LocaleIndex.KeyCode) || entry.Kind != LocaleJsonWriter.EntryKind.String) continue;
            if (!string.Equals(LocaleIndex.Normalize(entry.Value), normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseNumber(entry.Path[LanguageNumberDepth], out int number)) return number;
        }
        return -1;
    }

    /// <summary>言語の要素の数（"languages" の番号の最大 + 1）。</summary>
    private int ElementCount()
    {
        int count = 0;
        foreach (var entry in _document.Entries)
        {
            if (entry.Path.Count < LanguageFieldPathLength - 1) continue;
            if (!string.Equals(entry.Path[0], LocaleIndex.KeyLanguages, StringComparison.Ordinal)) continue;
            if (TryParseNumber(entry.Path[LanguageNumberDepth], out int number)) count = Math.Max(count, number + 1);
        }
        return count;
    }

    /// <summary>道筋が「languages の n 番目の field」か。</summary>
    private static bool IsLanguageField(IReadOnlyList<string> path, string field) =>
        path.Count == LanguageFieldPathLength
        && string.Equals(path[0], LocaleIndex.KeyLanguages, StringComparison.Ordinal)
        && string.Equals(path[LanguageFieldDepth], field, StringComparison.Ordinal);

    /// <summary>番号の鍵を数にする（"0" → 0）。</summary>
    private static bool TryParseNumber(string segment, out int number) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    /// <summary>言語の要素の鍵の道筋（["languages", "n", field]）。</summary>
    private static string[] FieldPath(int element, string field) =>
        new[] { LocaleIndex.KeyLanguages, element.ToString(CultureInfo.InvariantCulture), field };

    // ============================================================
    //  書き換え
    // ============================================================

    /// <summary>言語を末尾に足す（code・name・fallback=null。一覧が空なら既定の言語にもする）。</summary>
    /// <param name="code">書きそろえたコード（呼び出し側で確かめ済み）。</param>
    /// <param name="name">言語の名前（空ならコード）。</param>
    public void AddLanguage(string code, string? name)
    {
        bool first = Languages.Count == 0;
        int element = ElementCount();

        // 足す位置: 言語の末尾 → 空の配列（[]）の位置 → ファイルの末尾
        int at = _document.LastIndexUnder(new[] { LocaleIndex.KeyLanguages }) + 1;
        int placeholder = _document.RemovePlaceholdersAlong(new[] { LocaleIndex.KeyLanguages });
        if (at <= 0) at = placeholder >= 0 ? placeholder : _document.Count;

        _document.Insert(at, LocaleJsonWriter.Entry.Text(FieldPath(element, LocaleIndex.KeyCode), code));
        _document.Insert(at + 1, LocaleJsonWriter.Entry.Text(FieldPath(element, LocaleIndex.KeyName), string.IsNullOrWhiteSpace(name) ? code : name.Trim()));
        _document.Insert(at + 2, LocaleJsonWriter.Entry.Untranslated(FieldPath(element, LocaleIndex.KeyFallback)));

        if (first) SetDefaultEntry(code);
        Refresh();
    }

    /// <summary>
    /// 言語を一覧から外す（要素の項目を消し、後ろの要素の番号を詰める）。その言語を指す fallback は null にし、
    /// 既定の言語だったら残りの先頭を既定にする。言語の表のファイルは消さない（呼び出し側の約束）。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>外したら true（一覧に無ければ false）。</returns>
    public bool RemoveLanguage(string code)
    {
        int element = FindElement(code);
        if (element < 0) return false;
        string removedCode = Find(code)?.Code ?? LocaleIndex.Normalize(code);
        bool wasDefault = string.Equals(DefaultCode, removedCode, StringComparison.OrdinalIgnoreCase);

        // ── 要素の項目を消し、後ろの要素の番号を 1 つ詰める ──
        int firstRemoved = -1;
        for (int i = _document.Count - 1; i >= 0; i--)
        {
            var path = _document.Entries[i].Path;
            if (path.Count < LanguageFieldPathLength - 1) continue;
            if (!string.Equals(path[0], LocaleIndex.KeyLanguages, StringComparison.Ordinal)) continue;
            if (!TryParseNumber(path[LanguageNumberDepth], out int number)) continue;

            if (number == element)
            {
                _document.RemoveAt(i);
                firstRemoved = i;
            }
            else if (number > element)
            {
                var renumbered = new string[path.Count];
                for (int k = 0; k < path.Count; k++) renumbered[k] = path[k];
                renumbered[LanguageNumberDepth] = (number - 1).ToString(CultureInfo.InvariantCulture);
                _document.Replace(i, _document.Entries[i].WithPath(renumbered));
            }
        }

        // ── 最後の言語を外したら空の配列を残す（ファイルの形を保つ）──
        if (_document.LastIndexUnder(new[] { LocaleIndex.KeyLanguages }) < 0 && firstRemoved >= 0)
            _document.Insert(firstRemoved, new LocaleJsonWriter.Entry(new[] { LocaleIndex.KeyLanguages }, LocaleJsonWriter.EntryKind.Raw, LocaleJsonDocument.EmptyArrayLiteral));

        // ── 外した言語を指す fallback は null（既定の言語へ直接落ちる）にする ──
        for (int i = 0; i < _document.Count; i++)
        {
            var entry = _document.Entries[i];
            if (!IsLanguageField(entry.Path, LocaleIndex.KeyFallback) || entry.Kind != LocaleJsonWriter.EntryKind.String) continue;
            if (string.Equals(LocaleIndex.Normalize(entry.Value), removedCode, StringComparison.OrdinalIgnoreCase))
                _document.Replace(i, LocaleJsonWriter.Entry.Untranslated(entry.Path));
        }

        Refresh();

        // ── 既定の言語だったら残りの先頭へ（残りが無ければ default の鍵を消す）──
        if (wasDefault)
        {
            if (Languages.Count > 0) SetDefaultEntry(Languages[0].Code);
            else RemoveDefaultEntry();
            Refresh();
        }
        return true;
    }

    /// <summary>既定の言語を変える。</summary>
    /// <param name="code">一覧の言語のコード。</param>
    public void SetDefault(string code)
    {
        SetDefaultEntry(Find(code)?.Code ?? code);
        Refresh();
    }

    /// <summary>言語の fallback（表に無いキーを次に探す言語）を変える。</summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="fallback">次に探す言語のコード（null で既定の言語へ直接落とす）。</param>
    public void SetFallback(string code, string? fallback)
    {
        int element = FindElement(code);
        if (element < 0) return;
        var path = FieldPath(element, LocaleIndex.KeyFallback);
        SetField(element, fallback is null
            ? LocaleJsonWriter.Entry.Untranslated(path)
            : LocaleJsonWriter.Entry.Text(path, Find(fallback)?.Code ?? fallback));
        Refresh();
    }

    /// <summary>言語の名前を変える。</summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="name">名前（その言語での呼び名）。</param>
    public void SetName(string code, string name)
    {
        int element = FindElement(code);
        if (element < 0) return;
        SetField(element, LocaleJsonWriter.Entry.Text(FieldPath(element, LocaleIndex.KeyName), name));
        Refresh();
    }

    /// <summary>保存した（今の中身を「保存済み」にする）。</summary>
    public void MarkSaved()
    {
        _savedText = _document.Write();
        FileExists = true;
        IsDirty = false;
    }

    /// <summary>今の中身を書き出したもの（保存で書く）。</summary>
    /// <returns>JSON の文字列。</returns>
    public string Write() => _document.Write();

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>言語の要素の鍵を書く（あれば置き換え、無ければ要素の末尾に足す）。</summary>
    private void SetField(int element, LocaleJsonWriter.Entry entry)
    {
        int existing = _document.FindPath(entry.Path);
        if (existing >= 0)
        {
            _document.Replace(existing, entry);
            return;
        }
        int last = _document.LastIndexUnder(new[] { LocaleIndex.KeyLanguages, element.ToString(CultureInfo.InvariantCulture) });
        _document.Insert(last < 0 ? _document.Count : last + 1, entry);
    }

    /// <summary>"default" の鍵を書く（無ければ "languages" の前に足す）。</summary>
    private void SetDefaultEntry(string code)
    {
        var path = new[] { LocaleIndex.KeyDefault };
        var entry = LocaleJsonWriter.Entry.Text(path, code);
        int existing = _document.FindPath(path);
        if (existing >= 0)
        {
            _document.Replace(existing, entry);
            return;
        }

        int firstLanguage = -1;
        for (int i = 0; i < _document.Count; i++)
        {
            if (string.Equals(_document.Entries[i].Path[0], LocaleIndex.KeyLanguages, StringComparison.Ordinal))
            {
                firstLanguage = i;
                break;
            }
        }
        _document.Insert(firstLanguage < 0 ? _document.Count : firstLanguage, entry);
    }

    /// <summary>"default" の鍵を消す。</summary>
    private void RemoveDefaultEntry()
    {
        int existing = _document.FindPath(new[] { LocaleIndex.KeyDefault });
        if (existing >= 0) _document.RemoveAt(existing);
    }

    /// <summary>書き換えの後: 実行中と同じ読み方で読み直し、未保存かを決める。</summary>
    private void Refresh()
    {
        string text = _document.Write();
        Canonical = LocaleSafeParse.Index(text, LocalePaths.IndexFileName);
        IsDirty = !string.Equals(text, _savedText, StringComparison.Ordinal);
    }
}
