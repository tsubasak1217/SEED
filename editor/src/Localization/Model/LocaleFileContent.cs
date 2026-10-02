// ============================================================
//  LocaleFileContent.cs — 置き場のファイル 1 つを読んだ結果（値だけの型）
//
//  「無い」と「読めない」を分けるための型。読めないファイル（ほかのアプリが書き込み中で開けない等）を
//  無いものとして扱うと、保存でその言語の表を空から書き直してしまう。読めない表は読み取り専用にする。
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>置き場のファイル 1 つを読んだ結果。</summary>
/// <param name="Exists">ファイルがあるか。</param>
/// <param name="Text">中身（無い・読めないなら null）。</param>
/// <param name="Error">読めなかった理由（読めた・無いなら空）。</param>
public readonly record struct LocaleFileContent(bool Exists, string? Text, string Error)
{
    /// <summary>ファイルが無い。</summary>
    public static LocaleFileContent Missing { get; } = new(false, null, string.Empty);

    /// <summary>読めた。</summary>
    /// <param name="text">中身。</param>
    /// <returns>結果。</returns>
    public static LocaleFileContent Read(string text) => new(true, text, string.Empty);

    /// <summary>あるが読めなかった。</summary>
    /// <param name="error">理由。</param>
    /// <returns>結果。</returns>
    public static LocaleFileContent Unreadable(string error) => new(true, null, error);
}
