// ============================================================
//  LocaleLanguageColumn.cs — 文字列表の列 1 つ（言語 1 つの表 <code>.json の編集の状態）
//
//  【持つもの】
//    - 形を保った文書（LocaleJsonDocument。書き換えはここへ）
//    - 実行中と同じ読み方の結果（LocaleTable.Parse。複数形のまとまり・重なりの警告。書き換えのたびに読み直す）
//    - 未保存か（最後に読んだ・保存した中身と今の書き出しを比べる。同じ値に戻せば未保存も消える）
//  読めなかった表（壊れた JSON・開けないファイル）は読み取り専用にして保存しない（空から書き直して消さないため）。
// ============================================================

using System;
using SEED.Localization;
using SEEDEditor.Localization.Json;

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の列 1 つ（言語 1 つの表）。</summary>
internal sealed class LocaleLanguageColumn
{
    /// <summary>最後に読んだ・保存した中身（未保存の判定に使う）。</summary>
    private string _savedText;

    /// <summary>言語のコード（index.json の書きそろえたコード）。</summary>
    public string Code { get; }

    /// <summary>表のファイル名（"en.json"）。</summary>
    public string FileName => Code + LocalePaths.TableExtension;

    /// <summary>形を保った文書。</summary>
    public LocaleJsonDocument Document { get; }

    /// <summary>実行中と同じ読み方の結果。</summary>
    public LocaleTable Table { get; private set; }

    /// <summary>ファイルがあったか（無ければ保存で作る）。</summary>
    public bool FileExists { get; private set; }

    /// <summary>読めなかった理由（読めたら空）。</summary>
    public string LoadError { get; }

    /// <summary>読み取り専用か（読めなかった表）。</summary>
    public bool IsReadOnly => LoadError.Length > 0;

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>列を作る（Load から）。</summary>
    private LocaleLanguageColumn(string code, LocaleJsonDocument document, string savedText, bool fileExists, string loadError)
    {
        Code = code;
        Document = document;
        _savedText = savedText;
        FileExists = fileExists;
        LoadError = loadError;
        Table = LocaleSafeParse.Table(savedText, FileName, out _);
    }

    /// <summary>
    /// 読んだ結果から列を作る（無いファイルは空の表、読めない・壊れたファイルは読み取り専用の空の表）。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="content">読んだ結果。</param>
    /// <param name="newLineForNew">新しく作るときの改行。</param>
    /// <returns>列。</returns>
    public static LocaleLanguageColumn Load(string code, LocaleFileContent content, string newLineForNew)
    {
        if (!content.Exists)
        {
            var empty = LocaleJsonDocument.CreateEmpty(newLineForNew);
            return new LocaleLanguageColumn(code, empty, empty.Write(), fileExists: false, loadError: string.Empty);
        }
        if (content.Text is null)
        {
            // 開けなかった（ほかのアプリが書き込み中など）。空の表として見せ、保存しない
            var unreadable = LocaleJsonDocument.CreateEmpty(newLineForNew);
            return new LocaleLanguageColumn(code, unreadable, unreadable.Write(), fileExists: true,
                loadError: $"{code}{LocalePaths.TableExtension} を読めません: {content.Error}");
        }

        var document = LocaleJsonDocument.Parse(content.Text);
        string error = document.IsValid
            ? string.Empty
            : $"{code}{LocalePaths.TableExtension} は JSON として読めません（直すまで編集・保存しません）: {document.Error}";
        return new LocaleLanguageColumn(code, document, content.Text, fileExists: true, loadError: error);
    }

    /// <summary>書き換えの後: 実行中と同じ読み方で読み直し、未保存かを決める。</summary>
    public void Refresh()
    {
        string text = Document.Write();
        Table = LocaleSafeParse.Table(text, FileName, out _);
        IsDirty = !string.Equals(text, _savedText, StringComparison.Ordinal);
    }

    /// <summary>保存した（今の中身を「保存済み」にする）。</summary>
    public void MarkSaved()
    {
        _savedText = Document.Write();
        FileExists = true;
        IsDirty = false;
    }

    /// <summary>
    /// 新しく足した言語の列を「未保存」にする（ファイルが無いので、中身が空でも保存で必ず作る）。
    /// 保存済みの中身を空文字にしておくと、どんな書き出しとも食い違うので保存まで未保存のままになる。
    /// </summary>
    public void MarkNew()
    {
        _savedText = string.Empty;
        IsDirty = true;
    }
}
