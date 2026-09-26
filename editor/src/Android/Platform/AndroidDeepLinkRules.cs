// ============================================================
//  AndroidDeepLinkRules.cs — ディープリンク 1 件（android.deep_links の要素）の検査（W1-2）
//
//  【規則の出どころ】（2026-09-27 に確かめた）
//    ・scheme / host は小文字（Android の照合は大文字小文字を区別する。SDK の attrs_manifest.xml の scheme・host の説明と
//      developer.android.com「<data>」）
//    ・scheme が無いと他の欄はすべて無視され、host が無いと path の欄は無視される（developer.android.com「<data>」）
//      → scheme は必須、path_prefix を書くなら host も必須（誤り）。黙って「全部のパスに合う」にしない
//    ・autoVerify（Android App Links の検証）は VIEW・BROWSABLE・DEFAULT と https / http のスキームとホストがある
//      intent-filter だけが対象で、独自のスキームは検証できない（developer.android.com「Verify Android App Links」）→ 注意
//  誤り（errors）はビルドを止め、プロジェクト設定ウィンドウの保存も止める。注意（warnings）はログと画面に出すだけ。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Platform;

/// <summary>ディープリンク 1 件の検査。</summary>
public static class AndroidDeepLinkRules
{
    /// <summary>Android App Links で検証できるスキーム。</summary>
    public static readonly IReadOnlyList<string> VerifiableSchemes = new[] { "https", "http" };

    /// <summary>重なりの判定のキーの区切り（どの欄にも現れない制御文字。IdentityKey）。</summary>
    private const string IdentityKeySeparator = "\u001f";

    /// <summary>パスの前半の書き始め（URI のパスは / で始まる）。</summary>
    public const string PathPrefixStart = "/";

    /// <summary>マニフェストのマージが置き換える記法の始まり（${applicationId} 等。値に入れると置き換えの誤りになる）。</summary>
    private const string ManifestPlaceholderStart = "${";

    /// <summary>パスの前半に書けない文字（クエリ・フラグメントはパスの照合に入らない）。</summary>
    private static readonly char[] PathPrefixForbiddenChars = { '?', '#' };

    /// <summary>スキームの形（RFC 3986 の scheme を小文字に限ったもの）。</summary>
    private static readonly Regex SchemePattern = new("^[a-z][a-z0-9+.-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// ホストの形（小文字英数字・_・- の区切りを . でつないだもの。先頭の *. か * だけも可）。
    /// 独自のスキームで open_alarm のような名前を使えるよう、DNS より少し広くしている。
    /// </summary>
    private static readonly Regex HostPattern = new(@"^(\*|(\*\.)?[a-z0-9_-]+(\.[a-z0-9_-]+)*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 1 件を検査して、誤りと注意を足す。
    /// </summary>
    /// <param name="link">ディープリンク。</param>
    /// <param name="number">何件目か（1 から。メッセージ用）。</param>
    /// <param name="errors">誤りの足し先（ビルド・保存を止める）。</param>
    /// <param name="warnings">注意の足し先。</param>
    public static void Check(AndroidDeepLinkSetting link, int number, ICollection<string> errors, ICollection<string> warnings)
    {
        var label = $"ディープリンク {number} 件目（{link.Describe()}）";
        var scheme = Trimmed(link.Scheme);
        var host = Trimmed(link.Host);
        var pathPrefix = Trimmed(link.PathPrefix);

        // ── scheme（必須・小文字）──
        if (scheme is null)
        {
            errors.Add($"{label}: scheme がありません（例 https・wakeorpay。scheme が無いと Android は他の欄をすべて無視します）。");
        }
        else if (!SchemePattern.IsMatch(scheme))
        {
            errors.Add(HasUpperCase(scheme)
                ? $"{label}: scheme \"{scheme}\" は小文字で書いてください（Android の照合は大文字小文字を区別します）。"
                : $"{label}: scheme \"{scheme}\" の形が違います（英字で始まり、英小文字・数字・+ . - だけ）。");
        }

        // ── host（任意・小文字）──
        if (host is not null && !HostPattern.IsMatch(host))
        {
            errors.Add(HasUpperCase(host)
                ? $"{label}: host \"{host}\" は小文字で書いてください（Android の照合は大文字小文字を区別します）。"
                : $"{label}: host \"{host}\" の形が違います（例 example.com・*.example.com。ポート・パス・空白は書けません）。");
        }

        // ── path_prefix（任意。/ で始まる・host が要る）──
        if (pathPrefix is not null)
        {
            if (!pathPrefix.StartsWith(PathPrefixStart, StringComparison.Ordinal))
            {
                errors.Add($"{label}: path_prefix \"{pathPrefix}\" は / で始めてください（例 /alarm）。");
            }
            if (pathPrefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || pathPrefix.IndexOfAny(PathPrefixForbiddenChars) >= 0
                || pathPrefix.Contains(ManifestPlaceholderStart, StringComparison.Ordinal))
            {
                errors.Add($"{label}: path_prefix \"{pathPrefix}\" に空白・? ・#・${{ は書けません（パスの前半だけを書きます）。");
            }
            if (host is null)
            {
                errors.Add($"{label}: path_prefix を使うなら host も書いてください（host が無いと Android は path を照合に使わず、すべてのパスに合ってしまいます）。");
            }
        }

        // ── auto_verify（Android App Links。https / http とホストが要る）──
        if (link.AutoVerify && (scheme is null || !VerifiableSchemes.Contains(scheme, StringComparer.Ordinal) || host is null))
        {
            warnings.Add($"{label}: auto_verify は https / http のスキームとホストがあるときだけ検証されます（独自のスキームは検証できません）。" +
                         "intent-filter には android:autoVerify=\"true\" を書きますが、検証は行われません。");
        }
    }

    /// <summary>
    /// 同じ URL の形（scheme・host・path_prefix・auto_verify がすべて同じ）かどうかのキー（重なりを除くため）。
    /// </summary>
    /// <param name="link">ディープリンク。</param>
    /// <returns>キー。</returns>
    public static string IdentityKey(AndroidDeepLinkSetting link) =>
        string.Join(IdentityKeySeparator, Trimmed(link.Scheme) ?? string.Empty, Trimmed(link.Host) ?? string.Empty,
            Trimmed(link.PathPrefix) ?? string.Empty, link.AutoVerify ? AndroidPlatformFeatureCatalog.XmlTrue : AndroidPlatformFeatureCatalog.XmlFalse);

    /// <summary>前後の空白を落とす（空なら null）。</summary>
    /// <param name="text">値。</param>
    /// <returns>落とした値。</returns>
    public static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>英大文字を含むか。</summary>
    private static bool HasUpperCase(string text) => text.Any(char.IsUpper);
}
