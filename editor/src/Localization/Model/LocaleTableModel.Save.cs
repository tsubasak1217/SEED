// ============================================================
//  LocaleTableModel.Save.cs — 保存で書く中身
//
//  【約束】（docs/localization.md §15）
//    - 変わったファイルだけを書く（開いて保存しただけで手書きのファイルの書式が変わらないように）
//    - 書くのは入れ子の JSON（LocaleJsonWriter。インデント 2・読んだときの改行・最後に改行）。UTF-8・BOM 無しで書くのは
//      ディスクへ書く側（IO/LocaleFolderIO）の約束
//    - 読めなかったファイル（読み取り専用）は書かない
// ============================================================

using System.Collections.Generic;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

public sealed partial class LocaleTableModel
{
    /// <summary>保存で書くファイル（変わったものだけ。index.json → 言語の表の順）。</summary>
    /// <returns>書くファイル。</returns>
    public IReadOnlyList<LocaleSaveFile> PendingWrites()
    {
        var files = new List<LocaleSaveFile>();
        if (IsReadOnly) return files;
        if (_index.IsDirty) files.Add(new LocaleSaveFile(LocalePaths.IndexFileName, _index.Write()));
        foreach (var column in _columns)
        {
            if (column.IsDirty && !column.IsReadOnly) files.Add(new LocaleSaveFile(column.FileName, column.Document.Write()));
        }
        return files;
    }

    /// <summary>書き終えた（今の中身を「保存済み」にする）。</summary>
    public void MarkSaved()
    {
        if (_index.IsDirty) _index.MarkSaved();
        foreach (var column in _columns)
        {
            if (column.IsDirty && !column.IsReadOnly) column.MarkSaved();
        }
    }
}
