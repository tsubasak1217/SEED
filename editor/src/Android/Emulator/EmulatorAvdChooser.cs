// ============================================================
//  EmulatorAvdChooser.cs — 起動する AVD を決める（純粋な処理）
//
//  【決め方】
//    1. 指定があれば（エディタの設定 android.emulator_avd・SeedAndroid の --avd・設定 JSON の "avd"）それ。
//       emulator -list-avds に無ければエラー（勝手に別の AVD を起動しない）
//    2. 指定が無ければ、一覧に開発用の既定の AVD（seed_pixel6_api35。docs/android.md §2）があればそれ
//    3. それも無ければ一覧の先頭
//    4. 一覧が空ならエラー（Android Studio の Device Manager で作るよう促す）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Emulator;

/// <summary>AVD の決め方の結果。</summary>
/// <param name="Name">起動する AVD（決められなければ null）。</param>
/// <param name="Reason">その AVD にした理由（ログ用。決められなければ空）。</param>
/// <param name="Error">決められない理由（決まったら null）。</param>
public sealed record EmulatorAvdChoice(string? Name, string Reason, string? Error);

/// <summary>起動する AVD を決める。</summary>
public static class EmulatorAvdChooser
{
    /// <summary>指定が無いときに優先する AVD（開発用の AVD。docs/android.md §2 の開発用端末）。</summary>
    public const string PreferredAvdName = "seed_pixel6_api35";

    /// <summary>指定の AVD を使う理由。</summary>
    private const string ConfiguredReason = "指定の AVD（エディタの設定 android.emulator_avd／SeedAndroid の --avd）";

    /// <summary>既定の AVD を使う理由の書式（{0}=AVD）。</summary>
    private const string PreferredReasonFormat = "指定なし: 開発用の既定の AVD {0}";

    /// <summary>一覧の先頭を使う理由の書式（{0}=既定の AVD）。</summary>
    private const string FirstReasonFormat = "指定なし: {0} が無いため emulator -list-avds の先頭";

    /// <summary>指定の AVD が無いときのエラーの書式（{0}=指定、{1}=一覧）。</summary>
    private const string ConfiguredMissingFormat =
        "指定の AVD {0} がありません（ある AVD: {1}）。エディタの設定 android.emulator_avd（SeedAndroid は --avd）を直すか、Android Studio の Device Manager で作ってください。";

    /// <summary>AVD が 1 つも無いときのエラー。</summary>
    private const string NoAvdError =
        "エミュレータの AVD が 1 つもありません。Android Studio の Device Manager で AVD（x86_64 のシステムイメージ）を作るか、実機を USB でつないでください。";

    /// <summary>一覧が空のときの表記。</summary>
    private const string NoneText = "なし";

    /// <summary>一覧の区切り。</summary>
    private const string ListSeparator = ", ";

    /// <summary>
    /// 起動する AVD を決める。
    /// </summary>
    /// <param name="configured">指定の AVD（null・空なら指定なし）。</param>
    /// <param name="available">emulator -list-avds の一覧。</param>
    /// <returns>決めた結果。</returns>
    public static EmulatorAvdChoice Choose(string? configured, IReadOnlyList<string> available)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var wanted = configured.Trim();
            // AVD の名前はフォルダ名（Windows では大文字小文字を区別しない）。一覧の表記のまま起動する
            var match = available.FirstOrDefault(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
            return match is not null
                ? new EmulatorAvdChoice(match, ConfiguredReason, null)
                : new EmulatorAvdChoice(null, string.Empty,
                    string.Format(ConfiguredMissingFormat, wanted, available.Count == 0 ? NoneText : string.Join(ListSeparator, available)));
        }

        if (available.Count == 0) return new EmulatorAvdChoice(null, string.Empty, NoAvdError);
        var preferred = available.FirstOrDefault(name => string.Equals(name, PreferredAvdName, StringComparison.OrdinalIgnoreCase));
        return preferred is not null
            ? new EmulatorAvdChoice(preferred, string.Format(PreferredReasonFormat, preferred), null)
            : new EmulatorAvdChoice(available[0], string.Format(FirstReasonFormat, PreferredAvdName), null);
    }
}
