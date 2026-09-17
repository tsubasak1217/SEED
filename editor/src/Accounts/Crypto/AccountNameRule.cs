// ============================================================
//  AccountNameRule.cs — アカウント名の規則（純関数）
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）の「名前」の規則を、
//  **サーバへ送る前に** 同じ基準で判定する。
//    ・1〜32 文字
//    ・英数字・`_`・`-`・`.` と日本語
//    ・前後の空白なし
//
//  【なぜ純関数として切り出すのか】
//  名前は JWT の `sub` とロックの所有者表示に使われ、**作成後に変更できない**。
//  規則を画面のイベントハンドラへ散らすと、作成画面と参加画面で判定がずれ、
//  「作れたのに参加できない名前」が生まれる。判定は 1 か所・テスト済みにする。
//
//  【文字数の数え方（契約に書かれていないので、ここで決めて明記する）】
//  **Unicode コードポイント**で数える。UTF-16 の要素数で数えると、
//  サロゲートペア（絵文字・CJK 拡張 B 以降）が 2 文字に見えてしまう。
//  サーバ側（Rust）の `chars().count()` と同じ数え方になる。
//
//  【使える日本語の範囲（契約に書かれていないので、ここで決めて明記する）】
//  ひらがな・カタカナ（長音符を含む）・CJK 統合漢字（拡張 A を含む）・
//  漢字の繰り返し記号（々〆〇）。半角カナ・全角英数・記号は入れない
//  （見た目が同じで別の文字になる「なりすまし」を減らすため）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;
using System.Text;

namespace SEEDEditor.Accounts.Crypto;

/// <summary>
/// 名前の検証結果。
/// </summary>
/// <param name="IsValid">規則を満たすか。</param>
/// <param name="Error">満たさない場合の利用者向けメッセージ（満たすときは空）。</param>
public readonly record struct AccountNameCheck(bool IsValid, string Error)
{
    /// <summary>規則を満たしている結果。</summary>
    public static AccountNameCheck Valid => new(true, string.Empty);

    /// <summary>理由つきで満たしていない結果を作る。</summary>
    /// <param name="error">利用者向けメッセージ。</param>
    public static AccountNameCheck Invalid(string error) => new(false, error);
}

/// <summary>
/// アカウント名の規則（純粋な判定のみ）。
/// </summary>
public static class AccountNameRule
{
    // ── 使える記号 ──────────────────────────────────────────

    /// <summary>名前に使える ASCII 記号。</summary>
    private const string ALLOWED_SYMBOLS = "_-.";

    // ── 使える日本語の範囲（開始・終了とも含む）────────────

    /// <summary>漢字の繰り返し記号など（々〆〇）の開始。</summary>
    private const int CJK_SYMBOL_BEGIN = 0x3005;

    /// <summary>漢字の繰り返し記号など（々〆〇）の終了。</summary>
    private const int CJK_SYMBOL_END = 0x3007;

    /// <summary>ひらがなの開始。</summary>
    private const int HIRAGANA_BEGIN = 0x3041;

    /// <summary>ひらがなの終了（ゖ・濁点・゛゜を含む）。</summary>
    private const int HIRAGANA_END = 0x309F;

    /// <summary>カタカナの開始。</summary>
    private const int KATAKANA_BEGIN = 0x30A0;

    /// <summary>カタカナの終了（長音符 ー = 0x30FC を含む）。</summary>
    private const int KATAKANA_END = 0x30FF;

    /// <summary>CJK 統合漢字 拡張 A の開始。</summary>
    private const int CJK_EXT_A_BEGIN = 0x3400;

    /// <summary>CJK 統合漢字 拡張 A の終了。</summary>
    private const int CJK_EXT_A_END = 0x4DBF;

    /// <summary>CJK 統合漢字の開始。</summary>
    private const int CJK_BEGIN = 0x4E00;

    /// <summary>CJK 統合漢字の終了。</summary>
    private const int CJK_END = 0x9FFF;

    /// <summary>
    /// 名前が規則を満たすか調べる。
    /// </summary>
    /// <param name="name">調べる名前（利用者の入力そのまま）。</param>
    /// <returns>判定結果。</returns>
    public static AccountNameCheck Check(string? name)
    {
        // 空・null は最初に弾く（以降で null 判定を繰り返さないため）。
        if (string.IsNullOrEmpty(name))
            return AccountNameCheck.Invalid(AccountMessages.NAME_EMPTY);

        // 前後の空白は「見えない違い」を生むので、Trim せずに拒否する。
        // （こちらで黙って落とすと、利用者が入力したものと保存名がずれる。）
        if (name.Length != name.Trim().Length)
            return AccountNameCheck.Invalid(AccountMessages.NAME_HAS_SURROUNDING_SPACE);

        // 全部が空白のときは「空」として案内する方が分かりやすい。
        if (name.Trim().Length == 0)
            return AccountNameCheck.Invalid(AccountMessages.NAME_EMPTY);

        // 長さはコードポイントで数える（サロゲートペアを 1 文字とする）。
        var length = CountCodePoints(name);
        if (length < AccountSettings.NAME_MIN_LENGTH)
            return AccountNameCheck.Invalid(AccountMessages.NAME_EMPTY);
        if (length > AccountSettings.NAME_MAX_LENGTH)
        {
            return AccountNameCheck.Invalid(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.NAME_TOO_LONG_FORMAT,
                AccountSettings.NAME_MAX_LENGTH, length));
        }

        // 1 文字ずつ許可された範囲か調べ、最初に見つかった違反を返す
        // （全部並べても直せないので、直すべき 1 文字だけを示す）。
        foreach (var rune in name.EnumerateRunes())
        {
            if (IsAllowed(rune)) continue;

            return AccountNameCheck.Invalid(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.NAME_INVALID_CHARACTER_FORMAT, rune.ToString()));
        }

        return AccountNameCheck.Valid;
    }

    /// <summary>
    /// 名前が規則を満たすかだけを真偽で返す（理由が要らない場面用）。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    public static bool IsValid(string? name) => Check(name).IsValid;

    /// <summary>
    /// 1 文字（コードポイント）が名前に使えるか判定する。
    /// </summary>
    /// <param name="rune">判定する文字。</param>
    private static bool IsAllowed(Rune rune)
    {
        var value = rune.Value;

        // ASCII の英数字。Rune.IsLetterOrDigit を使うと全角英数や
        // 他言語の文字まで通ってしまうため、範囲を明示する。
        if (value is >= 'a' and <= 'z') return true;
        if (value is >= 'A' and <= 'Z') return true;
        if (value is >= '0' and <= '9') return true;

        // 許可された ASCII 記号。
        if (value <= char.MaxValue && ALLOWED_SYMBOLS.IndexOf((char)value) >= 0) return true;

        // 日本語（上のコメントに書いた範囲だけ）。
        if (value is >= CJK_SYMBOL_BEGIN and <= CJK_SYMBOL_END) return true;
        if (value is >= HIRAGANA_BEGIN    and <= HIRAGANA_END)  return true;
        if (value is >= KATAKANA_BEGIN    and <= KATAKANA_END)  return true;
        if (value is >= CJK_EXT_A_BEGIN   and <= CJK_EXT_A_END) return true;
        if (value is >= CJK_BEGIN         and <= CJK_END)       return true;

        return false;
    }

    /// <summary>
    /// 文字列の Unicode コードポイント数を数える。
    /// </summary>
    /// <param name="value">数える文字列。</param>
    private static int CountCodePoints(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }
}
