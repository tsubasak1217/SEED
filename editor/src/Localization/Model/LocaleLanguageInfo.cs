// ============================================================
//  LocaleLanguageInfo.cs — 文字列表の列の見出しに出す言語 1 つ（値だけの型。LocaleTableModel.Languages が作る）
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の列の見出しに出す言語 1 つ。</summary>
/// <param name="Code">言語のコード（index.json の書きそろえたコード）。</param>
/// <param name="Name">言語の名前（その言語での呼び名）。</param>
/// <param name="Fallback">表に無いキーを次に探す言語（無ければ null＝既定の言語へ直接）。</param>
/// <param name="IsDefault">既定の言語か。</param>
/// <param name="FileName">表のファイル名（"en.json"）。</param>
/// <param name="FileExists">表のファイルがあるか（無ければ保存で作る）。</param>
/// <param name="LoadError">読めなかった理由（読めたら空。あれば列は読み取り専用）。</param>
public sealed record LocaleLanguageInfo(
    string Code,
    string Name,
    string? Fallback,
    bool IsDefault,
    string FileName,
    bool FileExists,
    string LoadError)
{
    /// <summary>読み取り専用か（読めなかった表）。</summary>
    public bool IsReadOnly => LoadError.Length > 0;
}
