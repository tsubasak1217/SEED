// ============================================================
//  LocaleTableModel.cs — 文字列表のモデル（行 = キー・列 = 言語。WPF に依らない純粋な計算）
//
//  【役割】
//  多言語の置き場（assets://locale）の index.json と言語の表（<code>.json）を読み、
//  文字列表のパネル（Panels/LocalizationPanel）が出す行と列を組み立て、編集を受けて書き出す中身を作る。
//  ファイルの読み書きはしない（文字列で受け取り、書く中身を返す。ディスクとのやり取りは IO/LocaleFolderIO）。
//
//  【実行中と同じ読み方】（docs/localization.md §2・§15）
//  言語の一覧・既定の言語は LocaleIndex、複数形のまとまり・重なりの警告は LocaleTable（どちらも SEED.Localization の
//  Model。scripting と同じソースを使う）で読む。形を保つ読み書きだけ LocaleJsonDocument / LocaleJsonWriter が受け持つ。
//
//  【ファイルの分け方】
//    LocaleTableModel.cs           … 読み込み・言語の列・未保存・警告（このファイル）
//    LocaleTableModel.Rows.cs      … 行の組み立て（キーの並び・複数形・未訳の数え方）
//    LocaleTableModel.Keys.cs      … キーの編集（升目・追加・名前の変更・削除・複数形の形の追加）
//    LocaleTableModel.Languages.cs … 言語の編集（追加・一覧から外す・既定・fallback・名前）
//    LocaleTableModel.Save.cs      … 保存で書く中身
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表のモデル（行 = キー・列 = 言語）。</summary>
public sealed partial class LocaleTableModel
{
    /// <summary>言語の一覧（index.json）。</summary>
    private readonly LocaleIndexDocument _index;

    /// <summary>言語の列（index.json の順）。</summary>
    private readonly List<LocaleLanguageColumn> _columns = new();

    /// <summary>言語の表のファイルを読む関数（言語を足すとき、既にあるファイルを読むのに使う）。</summary>
    private readonly Func<string, LocaleFileContent> _readTable;

    /// <summary>モデルを作る（Load から）。</summary>
    private LocaleTableModel(LocaleIndexDocument index, Func<string, LocaleFileContent> readTable)
    {
        _index = index;
        _readTable = readTable;
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>
    /// 置き場のファイルからモデルを作る。
    /// </summary>
    /// <param name="index">index.json を読んだ結果。</param>
    /// <param name="readTable">言語のコード → その表（&lt;code&gt;.json）を読んだ結果。</param>
    /// <returns>モデル（index.json が読めなければ <see cref="IsReadOnly"/>）。</returns>
    public static LocaleTableModel Load(LocaleFileContent index, Func<string, LocaleFileContent> readTable)
    {
        // index.json は無ければ空の一覧として作る（言語を足せば保存で作る）。読めないときは読み取り専用の空の一覧
        var indexDocument = LocaleIndexDocument.Load(index.Exists ? index.Text ?? string.Empty : null);
        var model = new LocaleTableModel(indexDocument, readTable)
        {
            IndexLoadError = index.Exists && index.Text is null
                ? $"{LocalePaths.IndexFileName} を読めません: {index.Error}"
                : indexDocument.IsValid
                    ? string.Empty
                    : $"{LocalePaths.IndexFileName} は JSON として読めません（直すまで編集・保存しません）: {indexDocument.Document.Error}",
        };

        string newLine = indexDocument.Document.NewLine;
        foreach (var language in indexDocument.Languages)
            model._columns.Add(LocaleLanguageColumn.Load(language.Code, readTable(language.Code), newLine));
        return model;
    }

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>index.json を読めなかった理由（読めたら空。あればモデル全体が読み取り専用）。</summary>
    public string IndexLoadError { get; private init; } = string.Empty;

    /// <summary>index.json のファイルがあるか。</summary>
    public bool IndexExists => _index.FileExists;

    /// <summary>読み取り専用か（index.json を読めない。言語の表 1 つだけが読めないときはその列だけが読み取り専用）。</summary>
    public bool IsReadOnly => IndexLoadError.Length > 0;

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty => _index.IsDirty || _columns.Any(c => c.IsDirty);

    /// <summary>既定の言語のコード（言語が 0 件なら空）。</summary>
    public string DefaultCode => _index.DefaultCode;

    /// <summary>言語（列の並び）。</summary>
    public IReadOnlyList<LocaleLanguageInfo> Languages =>
        _index.Languages.Select(language =>
        {
            var column = FindColumn(language.Code);
            return new LocaleLanguageInfo(
                language.Code,
                language.Name,
                language.Fallback,
                string.Equals(language.Code, _index.DefaultCode, StringComparison.OrdinalIgnoreCase),
                language.Code + LocalePaths.TableExtension,
                column?.FileExists ?? false,
                column?.LoadError ?? string.Empty);
        }).ToList();

    /// <summary>
    /// 読み方の警告（index.json と言語の表。実行中に Debug.LogWarning されるのと同じ文言）と、読めなかった理由。
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (IndexLoadError.Length > 0) warnings.Add(IndexLoadError);
            warnings.AddRange(_index.Canonical.Warnings);
            foreach (var column in _columns)
            {
                if (column.LoadError.Length > 0) warnings.Add(column.LoadError);
                warnings.AddRange(column.Table.Warnings);
            }
            return warnings;
        }
    }

    /// <summary>言語の列を引く（大文字小文字を区別しない）。</summary>
    private LocaleLanguageColumn? FindColumn(string code) =>
        _columns.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>書き換えてよい列（読めた表）。</summary>
    private IEnumerable<LocaleLanguageColumn> EditableColumns => _columns.Where(c => !c.IsReadOnly);
}
