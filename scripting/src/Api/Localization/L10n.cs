using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEED.Localization;

// ============================================================
//  L10n.cs — 多言語（ローカライズ）の静的な窓口（正典 docs/localization.md・docs/scripting_api.md §7.21）
//
//  【使い方】
//      string title = SEED.Localization.L10n.Get("menu.start");                       // 今の言語の文
//      string hello = L10n.Get("greeting", ("name", playerName));                     // {name} の差し込み
//      string score = L10n.Format("score", 1200);                                     // {0} の差し込み
//      string coins = L10n.Plural("coins", coinCount);                                // 複数形（数は {n}）
//      L10n.SetLanguage("en");                                                        // 切り替え（SaveData の l10n.language へ保存）
//      this.On(L10n.Changed, (string code) => Refresh());                            // 切り替え・読み直しの知らせ（寿命に追従）
//  【データ】assets://locale/index.json（言語の一覧）と assets://locale/<言語>.json（キー → 文）。置き場は Configure で変える。
//  【中身】LocaleCatalog（純粋な本体）に、エンジンの読み込み元（SEED.Assets）・保存先（SEED.SaveData）・警告（Debug.LogWarning）・
//  端末の言語（CultureInfo.CurrentUICulture。Android の Invariant では null）をつないだもの。最初に使われたときに読む。
//  【知らせ】言語が替わった・表を読み直したとき、SEED.Events へ Changed（"l10n.changed"）を今の言語のコードの string で発火する。
//  引数なしの購読（this.On(L10n.Changed, () => …)）には届かない（SEED.Events は引数の型が合う購読だけを呼ぶ）。
//  【スクリプトの読み直し】ホットリロード・事前コンパイル DLL の読み込みで捨てる（ResetForReload。次に使われたときに読み直す）。
//  エディタに埋め込んだ Play の開始・停止ではスクリプトを読み直さないので、前の Play の言語・表が残る（SEED.UI のテーマと同じ）。
// ============================================================

/// <summary>多言語（ローカライズ）の静的な窓口。</summary>
public static class L10n
{
    /// <summary>
    /// 言語が切り替わった・表を読み直したときに SEED.Events へ発火するイベントの名前（引数は今の言語のコードの string）。
    /// <c>this.On(L10n.Changed, (string code) => …)</c> で購読する。
    /// </summary>
    public const string Changed = "l10n.changed";

    /// <summary>既定の置き場（"assets://locale"）。</summary>
    public const string DefaultRoot = LocalePaths.DefaultRoot;

    /// <summary>選んだ言語を保存する SaveData のキー（"l10n.language"）。</summary>
    public const string SaveKey = LocaleCatalog.LanguageSaveKey;

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.Localization]";

    /// <summary>本体（null = まだ作っていない。最初に使われたときに作る）。</summary>
    private static LocaleCatalog? _catalog;

    /// <summary>本体（無ければエンジンの読み込み元・保存先で作る）。</summary>
    private static LocaleCatalog Catalog => _catalog ??= CreateCatalog();

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>今の言語のコード（"ja"。言語の一覧が無ければ空）。</summary>
    public static string Language => Catalog.Language;

    /// <summary>今の言語（一覧の 1 つ。言語の一覧が無ければ null）。</summary>
    public static LocaleLanguage? CurrentLanguage => Catalog.Index.Find(Catalog.Language);

    /// <summary>言語の一覧（index.json の順。言語を選ぶ画面に並べる）。</summary>
    public static IReadOnlyList<LocaleLanguage> Languages => Catalog.Languages;

    /// <summary>既定の言語のコード（index.json の default）。</summary>
    public static string DefaultLanguage => Catalog.Index.DefaultCode;

    /// <summary>
    /// 端末（OS）の表示の言語（"ja-JP" など。<see cref="CultureInfo.CurrentUICulture"/> から）。
    /// Invariant の環境（Android の CoreCLR）・取れない環境では null。
    /// </summary>
    public static string? SystemLanguage => LocaleCulture.DetectSystemLanguage();

    /// <summary>言語を保存していない（端末の言語に従っている）か。</summary>
    public static bool IsFollowingSystem => Catalog.IsFollowingSystem;

    /// <summary>今の言語の書式の文化（Invariant の環境では不変文化＝名前が空）。</summary>
    public static CultureInfo Culture => Catalog.Culture;

    /// <summary>キーを探す順（今の言語 → fallback の連鎖 → 既定の言語）。</summary>
    public static IReadOnlyList<string> FallbackChain => Catalog.FallbackChain;

    /// <summary>置き場（"assets://locale"）。</summary>
    public static string Root => Catalog.Root;

    /// <summary>欠けていたキー（今の言語にしてから・読み直してから。翻訳の漏れの確かめに使う）。</summary>
    public static IReadOnlyCollection<string> MissingKeys => Catalog.MissingKeys;

    /// <summary>
    /// どの言語の表にも無いキーを引いたときに返す文の方針
    /// （既定は <c>Application.IsDebugAllowed</c> なら Marked〈"[key]"〉、そうでなければ Key〈キーそのまま〉）。
    /// </summary>
    public static MissingKeyPolicy MissingPolicy
    {
        get => Catalog.MissingPolicy;
        set => Catalog.MissingPolicy = value;
    }

    // ============================================================
    //  置き場・切り替え・読み直し
    // ============================================================

    /// <summary>
    /// 置き場を変える（"assets://mygame/locale"。空なら既定）。読み込み済みなら読み直して <see cref="Changed"/> を知らせる。
    /// 起動のスクリプトの OnStart で、ほかのスクリプトが文を引く前に呼ぶとよい。
    /// </summary>
    /// <param name="root">置き場。</param>
    public static void Configure(string root)
    {
        if (Catalog.Configure(root)) RaiseChanged();
    }

    /// <summary>
    /// 言語を切り替える（表を読み替えて <see cref="Changed"/> を知らせる）。<paramref name="save"/> なら SaveData の
    /// <see cref="SaveKey"/> へ保存して書き出す（次の起動もこの言語）。今の言語と同じなら知らせない。
    /// </summary>
    /// <param name="code">言語のコード（"en"。"en-US" のような地域つきは一覧の言語へ当てる）。</param>
    /// <param name="save">保存するか（false = この実行の間だけ）。</param>
    /// <returns>一覧にある言語なら true（無ければ切り替えず警告）。</returns>
    public static bool SetLanguage(string code, bool save = true)
    {
        var result = Catalog.SetLanguage(code, save);
        if (result == LocaleSwitchResult.Changed) RaiseChanged();
        return result != LocaleSwitchResult.Unknown;
    }

    /// <summary>
    /// 保存した言語を消して端末の言語（当たらなければ既定の言語）へ戻す（言語を選ぶ画面の「端末に合わせる」）。
    /// 替われば <see cref="Changed"/> を知らせる。
    /// </summary>
    public static void FollowSystemLanguage()
    {
        if (Catalog.FollowSystemLanguage() == LocaleSwitchResult.Changed) RaiseChanged();
    }

    /// <summary>
    /// データファイルが書き換わっていたら読み直して <see cref="Changed"/> を知らせる（ホットリロード。既定では誰も呼ばない。
    /// LocalizationReloader を置くと開発中だけ数秒ごとに呼ぶ）。PAK 同梱のファイルは変わらない扱い。
    /// </summary>
    /// <returns>読み直したら true。</returns>
    public static bool PollChanges()
    {
        if (!Catalog.PollChanges()) return false;
        RaiseChanged();
        return true;
    }

    /// <summary>index.json と言語の表を読み直して <see cref="Changed"/> を知らせる（今の言語を保つ）。</summary>
    public static void Reload()
    {
        Catalog.Reload();
        RaiseChanged();
    }

    // ============================================================
    //  文を引く
    // ============================================================

    /// <summary>キーの文（無ければ欠けの方針どおりの文。キーごとに 1 回だけ警告）。</summary>
    /// <param name="key">キー（"menu.start"）。</param>
    /// <returns>文。</returns>
    public static string Get(string key) => Catalog.Get(key, ReadOnlySpan<(string Name, object? Value)>.Empty);

    /// <summary>キーの文に名前つきの値を差し込む（{name}・{name:書式}。{0} は渡した順でも引ける）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="args">名前と値（<c>("name", playerName)</c>）。</param>
    /// <returns>文。</returns>
    public static string Get(string key, params (string name, object? value)[] args) => Catalog.Get(key, args);

    /// <summary>キーの文に順の値を差し込む（{0}・{1}・{0:N0}）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="args">値（渡した順が番号）。</param>
    /// <returns>文。</returns>
    public static string Format(string key, params object?[] args) => Catalog.Get(key, Positional(args));

    /// <summary>
    /// 複数形の文（今の言語の規則で zero / one / two / few / many / other を選ぶ。数は {n}）。
    /// 0 ならどの言語でも zero の子を先に探す。形の変わらない言語は普通のキー 1 文でもよい。
    /// </summary>
    /// <param name="key">複数形のまとまりの名前（"coins"）。</param>
    /// <param name="n">数。</param>
    /// <param name="args">ほかの差し込みの名前と値。</param>
    /// <returns>文。</returns>
    public static string Plural(string key, long n, params (string name, object? value)[] args) => Catalog.Plural(key, n, args);

    /// <summary>キーが探す順のどれかの表にあるか（警告しない）。</summary>
    /// <param name="key">キー。</param>
    /// <returns>あれば true。</returns>
    public static bool Has(string key) => Catalog.Has(key);

    /// <summary>キーの文を引く（無ければ false。警告しない・欠けの方針の文も返さない）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="text">文（無ければ空）。</param>
    /// <returns>あれば true。</returns>
    public static bool TryGet(string key, out string text) => Catalog.TryGet(key, out text);

    // ============================================================
    //  数・日付・時刻
    // ============================================================

    /// <summary>区切りつきの数（今の言語の文化。"1,234.5"・"1.234,5"。Invariant の環境では "1,234.5"）。</summary>
    /// <param name="value">数。</param>
    /// <param name="digits">小数の桁（0〜15）。</param>
    /// <returns>書いた数。</returns>
    public static string FormatNumber(double value, int digits = 0) => Catalog.FormatNumber(value, digits);

    /// <summary>
    /// 日付（.NET の日付の書式を今の言語の文化で。既定は短い日付 "d"）。Android（Invariant）では月・曜日の名前が英語になるので、
    /// 言語ごとの書式は表に書いて渡す（<c>L10n.FormatDate(date, L10n.Get("format.date"))</c>）。
    /// </summary>
    /// <param name="date">日付。</param>
    /// <param name="pattern">書式。</param>
    /// <returns>書いた日付。</returns>
    public static string FormatDate(DateTime date, string pattern = LocaleCulture.DefaultDatePattern) => Catalog.FormatDate(date, pattern);

    /// <summary>時刻（.NET の時刻の書式を今の言語の文化で。既定は短い時刻 "t"）。</summary>
    /// <param name="time">時刻。</param>
    /// <param name="pattern">書式。</param>
    /// <returns>書いた時刻。</returns>
    public static string FormatTime(TimeOnly time, string pattern = LocaleCulture.DefaultTimePattern) => Catalog.FormatTime(time, pattern);

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>
    /// スクリプトの読み直し（ホットリロード・事前コンパイル DLL の読み込み）で本体を捨てる（ScriptBridge が呼ぶ）。
    /// 次に使われたときに置き場は既定・言語は決まり方の順で読み直す（書き換えたデータファイルもそこで読む）。
    /// </summary>
    internal static void ResetForReload() => _catalog = null;

    /// <summary>エンジンの読み込み元・保存先・警告・端末の言語で本体を作る。</summary>
    private static LocaleCatalog CreateCatalog() => new(
        new AssetLocaleSource(),
        new SaveDataLocaleStore(),
        Warn,
        LocaleCulture.DetectSystemLanguage)
    {
        MissingPolicy = MissingKeyText.DefaultPolicy(Application.IsDebugAllowed),
    };

    /// <summary>警告をログへ出す。</summary>
    private static void Warn(string message) => Debug.LogWarning($"{LogPrefix} {message}");

    /// <summary>描き直しを頼んで、SEED.Events へ <see cref="Changed"/> を今の言語のコードで発火する。</summary>
    private static void RaiseChanged()
    {
        Redraw.Request();
        Events.Raise(Changed, Catalog.Language);
    }

    /// <summary>順の値を名前の無い組へ（{0} は番号で引く。名前は空なので {name} には当たらない）。</summary>
    private static (string Name, object? Value)[] Positional(object?[]? args)
    {
        if (args is null || args.Length == 0) return Array.Empty<(string Name, object? Value)>();
        var pairs = new (string Name, object? Value)[args.Length];
        for (int i = 0; i < args.Length; i++) pairs[i] = (string.Empty, args[i]);
        return pairs;
    }
}
