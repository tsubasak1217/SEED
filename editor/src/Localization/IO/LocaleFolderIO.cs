// ============================================================
//  LocaleFolderIO.cs — 多言語の置き場のファイルの読み書き（ディスクとのやり取りだけ）
//
//  【読む】index.json と、そこに載った言語の表 <code>.json。「無い」と「読めない」を分けて LocaleTableModel へ渡す
//  （読めない表を無いものとして扱うと、保存で空から書き直して消してしまう）。
//  【書く】LocaleTableModel.PendingWrites の中身を、Assets/SafeFileWriter で UTF-8・BOM 無し・原子的に書く
//  （書く前に旧版を <assets>/.backup/ へ退避する。シーン・アニメーションの保存と同じ道）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SEEDEditor.Assets;
using SEEDEditor.Localization.Model;

namespace SEEDEditor.Localization.IO;

/// <summary>多言語の置き場のファイルの読み書き。</summary>
public static class LocaleFolderIO
{
    /// <summary>書くときの文字の符号化（UTF-8・BOM 無し）。</summary>
    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 置き場を読んでモデルを作る（フォルダや index.json が無くても空のモデルを返す）。
    /// </summary>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <returns>モデル。</returns>
    public static LocaleTableModel Load(string folder) =>
        LocaleTableModel.Load(
            ReadFile(LocaleFolderLocator.IndexPath(folder)),
            code => ReadFile(LocaleFolderLocator.TablePath(folder, code)));

    /// <summary>ファイルを読む（無い・読めないを分ける。例外を投げない）。</summary>
    /// <param name="path">絶対パス。</param>
    /// <returns>読んだ結果。</returns>
    public static LocaleFileContent ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return LocaleFileContent.Missing;
            // 書き込み中のファイルも読めるよう共有で開く（BOM があれば落とす）
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, FileEncoding, detectEncodingFromByteOrderMarks: true);
            return LocaleFileContent.Read(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocaleFileContent.Unreadable(ex.Message);
        }
    }

    /// <summary>
    /// 変わったファイルを書く（置き場が無ければ作る）。書き終えたらモデルを「保存済み」にする。
    /// 途中で失敗したら例外をそのまま投げる（書けたファイルの分も保存済みにしない。もう一度保存すれば書き直す）。
    /// </summary>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <param name="model">モデル。</param>
    /// <param name="assetsRoot">アセットのフォルダ（旧版の退避先 &lt;assets&gt;/.backup を決める。null ならフォルダの隣）。</param>
    /// <returns>書いたファイルの絶対パス。</returns>
    public static IReadOnlyList<string> Save(string folder, LocaleTableModel model, string? assetsRoot)
    {
        var written = new List<string>();
        var files = model.PendingWrites();
        if (files.Count == 0) return written;

        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            string path = Path.Combine(folder, file.FileName);
            SafeFileWriter.WriteAllTextAtomic(path, file.Text, assetsRoot, FileEncoding);
            written.Add(path);
        }
        model.MarkSaved();
        return written;
    }
}
