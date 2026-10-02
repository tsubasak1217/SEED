// ============================================================
//  LocaleEditResult.cs — 文字列表の編集 1 回の結果（値だけの型）
//
//  編集の関数（LocaleTableModel の SetCell・AddKey など）は例外を投げず、できなかった理由をここに入れて返す。
//  パネルは失敗の文言をそのまま利用者へ出す。
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>編集 1 回の結果。</summary>
/// <param name="Succeeded">できたか（変わらなかったときも true）。</param>
/// <param name="Changed">中身が変わったか。</param>
/// <param name="Message">できなかった理由（できたら空）。</param>
public readonly record struct LocaleEditResult(bool Succeeded, bool Changed, string Message)
{
    /// <summary>変えた。</summary>
    public static LocaleEditResult Done { get; } = new(true, true, string.Empty);

    /// <summary>変える必要が無かった（同じ値など）。</summary>
    public static LocaleEditResult Unchanged { get; } = new(true, false, string.Empty);

    /// <summary>できなかった。</summary>
    /// <param name="message">理由。</param>
    /// <returns>結果。</returns>
    public static LocaleEditResult Fail(string message) => new(false, false, message);
}
