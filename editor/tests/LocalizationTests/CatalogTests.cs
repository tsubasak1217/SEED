using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  CatalogTests.cs — 多言語の表の本体（LocaleCatalog）: 探す順・欠けの方針・警告・起動の言語・保存のキー・切り替え・
//  端末に合わせる・書き換えの検知・置き場（Assets・SaveData の代わりに辞書の読み込み元と保存先を差し込む）
// ============================================================

/// <summary>多言語の表の本体のテスト。</summary>
internal static class CatalogTests
{
    /// <summary>言語の一覧のパス。</summary>
    private const string IndexPath = "assets://locale/index.json";
    /// <summary>日本語の表のパス。</summary>
    private const string JaPath = "assets://locale/ja.json";
    /// <summary>英語の表のパス。</summary>
    private const string EnPath = "assets://locale/en.json";
    /// <summary>ロシア語の表のパス。</summary>
    private const string RuPath = "assets://locale/ru.json";

    /// <summary>言語の一覧（ja が既定・en は ja へ・ru は en へ落ちる）。</summary>
    private const string IndexJson = """
        { "default": "ja", "languages": [
          { "code": "ja", "name": "日本語", "culture": "ja-JP" },
          { "code": "en", "name": "English", "fallback": "ja", "culture": "en-US" },
          { "code": "ru", "name": "Русский", "fallback": "en" } ] }
        """;

    /// <summary>日本語の表。</summary>
    private const string JaJson = """
        { "menu": { "start": "はじめる", "quit": "おわる" }, "only_ja": "日本語だけ", "untranslated": "未翻訳の文",
          "coins": "コイン {n} 枚", "items": { "zero": "なし", "other": "{n} 個" },
          "greet": "こんにちは {name}", "price": "{amount:N0} 円", "brace": "{{x}}",
          "hud": "所持金 {num} 円 {color}注意{/color}" }
        """;

    /// <summary>英語の表（untranslated は null ＝ 訳していない）。</summary>
    private const string EnJson = """
        { "menu": { "start": "Start" }, "coins": { "one": "{n} coin", "other": "{n} coins" },
          "greet": "Hello {name}", "price": "{amount:N0} yen", "untranslated": null }
        """;

    /// <summary>ロシア語の表（複数形だけ）。</summary>
    private const string RuJson = """
        { "coins": { "one": "{n} монета", "few": "{n} монеты", "many": "{n} монет", "other": "{n} монеты" } }
        """;

    /// <summary>テストの道具一式。</summary>
    private sealed record Rig(LocaleCatalog Catalog, MemoryLocaleSource Source, MemoryLocaleStore Store, WarningLog Log);

    /// <summary>ja・en・ru の表で本体を作る（system = 端末の言語）。</summary>
    private static Rig Make(string? system = null, string? saved = null)
    {
        var source = new MemoryLocaleSource()
            .Put(IndexPath, IndexJson)
            .Put(JaPath, JaJson)
            .Put(EnPath, EnJson)
            .Put(RuPath, RuJson);
        var store = new MemoryLocaleStore();
        if (saved is not null) store.Values[LocaleCatalog.LanguageSaveKey] = saved;
        var log = new WarningLog();
        var catalog = new LocaleCatalog(source, store, log.Add, () => system);
        return new Rig(catalog, source, store, log);
    }

    /// <summary>差し込みなし。</summary>
    private static ReadOnlySpan<(string Name, object? Value)> None => ReadOnlySpan<(string Name, object? Value)>.Empty;

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        // ── 起動の言語 ──────────────────────────────────────────
        h.Add("本体: 起動の言語は 保存した値 → 端末の言語 → 既定 の順（端末の \"en-US\" は en へ当てる）", () =>
        {
            Check.Equal("ja", Make().Catalog.Language, "どれも無ければ既定");
            Check.Equal("en", Make(system: "en-US").Catalog.Language, "端末の言語");
            Check.Equal("en", Make(system: "ja-JP", saved: "en").Catalog.Language, "保存した値が勝つ");
            Check.Equal("ja", Make(system: "fr-FR").Catalog.Language, "端末の言語が一覧に無ければ既定");
        });

        h.Add("本体: 一覧に無い保存した値は警告して端末の言語・既定へ", () =>
        {
            var rig = Make(system: "en-US", saved: "fr");
            Check.Equal("en", rig.Catalog.Language, "端末の言語");
            Check.Equal(1, rig.Log.CountContaining("保存した言語「fr」"), "警告");
            Check.Equal("fr", rig.Store.GetString(LocaleCatalog.LanguageSaveKey), "保存した値は消さない");
        });

        h.Add("本体: 最初に使われたときに 1 回だけ読む（作っただけでは読まない）", () =>
        {
            var rig = Make();
            Check.True(!rig.Catalog.IsLoaded, "まだ読まない");
            Check.Equal(0, rig.Source.ReadCounts.Count, "読んでいない");
            _ = rig.Catalog.Get("menu.start", None);
            _ = rig.Catalog.Get("menu.quit", None);
            Check.Equal(1, rig.Source.ReadCounts[IndexPath], "index.json は 1 回");
            Check.Equal(1, rig.Source.ReadCounts[JaPath], "ja.json は 1 回");
            Check.True(!rig.Source.ReadCounts.ContainsKey(EnPath), "探す順に無い言語は読まない");
        });

        // ── 探す順・欠け ────────────────────────────────────────
        h.Add("本体: 探す順（en → ja）で引く・null は訳していない扱いで次の言語", () =>
        {
            var rig = Make(system: "en");
            var c = rig.Catalog;
            Check.Equal("en,ja", string.Join(",", c.FallbackChain), "探す順");
            Check.Equal("Start", c.Get("menu.start", None), "en にある");
            Check.Equal("おわる", c.Get("menu.quit", None), "en に無いので ja");
            Check.Equal("未翻訳の文", c.Get("untranslated", None), "null は次の言語");
            Check.Equal(0, rig.Log.Messages.Count, "警告なし");
        });

        h.Add("本体: 欠けの方針（Marked・Key・Empty）と、警告はキーごとに 1 回だけ・Has / TryGet は警告しない", () =>
        {
            var rig = Make();
            var c = rig.Catalog;
            Check.Equal(MissingKeyPolicy.Marked, c.MissingPolicy, "本体の既定は Marked");
            Check.Equal("[nope]", c.Get("nope", None), "Marked");
            c.MissingPolicy = MissingKeyPolicy.Key;
            Check.Equal("nope", c.Get("nope", None), "Key");
            c.MissingPolicy = MissingKeyPolicy.Empty;
            Check.Equal("", c.Get("nope", None), "Empty");
            Check.Equal(1, rig.Log.CountContaining("「nope」"), "警告は 1 回");
            Check.True(c.MissingKeys.Contains("nope"), "欠けの一覧");
            Check.True(!c.Has("other_missing") && !c.TryGet("other_missing", out _), "Has・TryGet");
            Check.Equal(0, rig.Log.CountContaining("other_missing"), "Has・TryGet は警告しない");
            Check.True(!c.MissingKeys.Contains("other_missing"), "Has・TryGet は欠けに数えない");
        });

        h.Add("本体: 言語を切り替えると欠けを数え直す（新しい言語でもう 1 回警告）", () =>
        {
            var rig = Make();
            _ = rig.Catalog.Get("nope", None);
            rig.Catalog.SetLanguage("en");
            _ = rig.Catalog.Get("nope", None);
            Check.Equal(2, rig.Log.CountContaining("「nope」"), "切り替えの後にもう 1 回");
        });

        h.Add("本体: 欠けの方針の既定（デバッグを許す実行は Marked・配布用は Key）", () =>
        {
            Check.Equal(MissingKeyPolicy.Marked, MissingKeyText.DefaultPolicy(debugAllowed: true), "開発中");
            Check.Equal(MissingKeyPolicy.Key, MissingKeyText.DefaultPolicy(debugAllowed: false), "配布用");
            Check.Equal("[a.b]", MissingKeyText.Render(MissingKeyPolicy.Marked, "a.b"), "Marked の文");
        });

        // ── 差し込み ────────────────────────────────────────────
        h.Add("本体: 差し込みは今の言語の文化で書く・{{ }} は波かっこ・Text の記法は残す", () =>
        {
            var rig = Make(system: "en");
            var c = rig.Catalog;
            Check.Equal("Hello カニ", c.Get("greet", new (string, object?)[] { ("name", "カニ") }), "{name}");
            Check.Equal("1,234,567 yen", c.Get("price", new (string, object?)[] { ("amount", 1234567) }), "{amount:N0}");
            Check.True(c.TryGet("brace", out var brace) && brace == "{x}", "TryGet も二重の波かっこを戻す");
            Check.Equal("所持金 {num} 円 {color}注意{/color}", c.Get("hud", new (string, object?)[] { ("name", "x") }), "Text の記法を残す");
        });

        // ── 複数形 ──────────────────────────────────────────────
        h.Add("本体: 複数形（en の one / other・ja の 1 文・0 は zero・無い言語は次の言語の規則）", () =>
        {
            var rig = Make(system: "en");
            var c = rig.Catalog;
            Check.Equal("1 coin", c.Plural("coins", 1, None), "en one");
            Check.Equal("2 coins", c.Plural("coins", 2, None), "en other");
            Check.Equal("0 coins", c.Plural("coins", 0, None), "en の 0 は other（zero が無い）");
            Check.Equal("なし", c.Plural("items", 0, None), "en に無い → ja の zero");
            Check.Equal("3 個", c.Plural("items", 3, None), "en に無い → ja の other");
            Check.Equal("{n} coins", c.Get("coins", None), "まとまりの名前を Get で引くと other の形");
            Check.Equal("two coins", c.Plural("coins", 2, new (string, object?)[] { ("n", "two") }), "利用者の n が勝つ");
            c.SetLanguage("ja");
            Check.Equal("コイン 1 枚", c.Plural("coins", 1, None), "ja は普通のキー 1 文");
            Check.Equal("[nope]", c.Plural("nope", 1, None), "無ければ欠けの方針");
        });

        h.Add("本体: ロシア語の複数形（ru → en → ja の順で、それぞれの言語の規則）", () =>
        {
            var rig = Make(system: "ru-RU");
            var c = rig.Catalog;
            Check.Equal("ru,en,ja", string.Join(",", c.FallbackChain), "探す順");
            Check.Equal("1 монета", c.Plural("coins", 1, None), "one");
            Check.Equal("3 монеты", c.Plural("coins", 3, None), "few");
            Check.Equal("5 монет", c.Plural("coins", 5, None), "many");
            Check.Equal("21 монета", c.Plural("coins", 21, None), "21 は one");
            Check.Equal("5 個", c.Plural("items", 5, None), "ru・en に無い → ja の規則（other）");
        });

        // ── 切り替え・保存 ──────────────────────────────────────
        h.Add("本体: 切り替えは SaveData のキー l10n.language へ保存（同じ値は書き直さない・save:false は保存しない）", () =>
        {
            var rig = Make();
            var c = rig.Catalog;
            Check.Equal("l10n.language", LocaleCatalog.LanguageSaveKey, "保存のキー");
            Check.Equal(LocaleSwitchResult.Changed, c.SetLanguage("en"), "切り替えた");
            Check.Equal("en", rig.Store.GetString("l10n.language"), "保存した");
            Check.Equal("en", c.Language, "今の言語");
            Check.Equal(1, rig.Store.WriteCount, "1 回書いた");
            Check.Equal(LocaleSwitchResult.Unchanged, c.SetLanguage("EN-gb"), "同じ言語（en-GB → en）");
            Check.Equal(1, rig.Store.WriteCount, "同じ値は書き直さない");
            Check.Equal(LocaleSwitchResult.Changed, c.SetLanguage("ja", save: false), "保存しない切り替え");
            Check.Equal("en", rig.Store.GetString("l10n.language"), "保存した値はそのまま");
            Check.True(!c.IsFollowingSystem, "保存がある＝端末に従っていない");
        });

        h.Add("本体: 一覧に無い言語へは切り替えず保存もしない（警告）", () =>
        {
            var rig = Make();
            Check.Equal(LocaleSwitchResult.Unknown, rig.Catalog.SetLanguage("fr"), "Unknown");
            Check.Equal("ja", rig.Catalog.Language, "そのまま");
            Check.Equal(0, rig.Store.WriteCount, "保存しない");
            Check.Equal(1, rig.Log.CountContaining("一覧に無い言語です: fr"), "警告");
        });

        h.Add("本体: 端末に合わせる（保存を消して端末の言語へ・端末の言語が無ければ既定）", () =>
        {
            var rig = Make(system: "ja-JP", saved: "en");
            Check.Equal("en", rig.Catalog.Language, "保存した値で始まる");
            Check.Equal(LocaleSwitchResult.Changed, rig.Catalog.FollowSystemLanguage(), "端末の言語へ");
            Check.Equal("ja", rig.Catalog.Language, "ja");
            Check.Equal(null, rig.Store.GetString("l10n.language"), "保存を消した");
            Check.True(rig.Catalog.IsFollowingSystem, "端末に従っている");

            var unknownSystem = Make(system: null, saved: "en");
            Check.Equal(LocaleSwitchResult.Changed, unknownSystem.Catalog.FollowSystemLanguage(), "端末がわからない");
            Check.Equal("ja", unknownSystem.Catalog.Language, "既定の言語");
        });

        h.Add("本体: 切り替えで書式の文化と探す順が替わる（culture の省略はコード）", () =>
        {
            var rig = Make();
            rig.Catalog.SetLanguage("en");
            Check.Equal("en-US", rig.Catalog.Culture.Name, "culture");
            rig.Catalog.SetLanguage("ru");
            Check.Equal("ru", rig.Catalog.Culture.Name, "culture の省略はコード");
            Check.Equal("ru,en,ja", string.Join(",", rig.Catalog.FallbackChain), "探す順");
        });

        // ── 書き換えの検知 ──────────────────────────────────────
        h.Add("本体: PollChanges（変わらなければ読まない・表が変われば読み直す・今の言語を保つ）", () =>
        {
            var rig = Make(system: "en");
            var c = rig.Catalog;
            Check.True(!c.PollChanges(), "まだ読んでいなければ何もしない");
            Check.Equal("Start", c.Get("menu.start", None), "読む");
            Check.True(!c.PollChanges(), "変わっていない");
            Check.Equal(1, rig.Source.ReadCounts[EnPath], "読み直していない");
            rig.Source.Put(EnPath, """{ "menu": { "start": "Begin" } }""");
            Check.True(c.PollChanges(), "変わった");
            Check.Equal("Begin", c.Get("menu.start", None), "新しい文");
            Check.Equal("en", c.Language, "言語を保つ");
        });

        h.Add("本体: PollChanges（index.json の書き換えで言語が増える・印が取れないファイルは読み直さない）", () =>
        {
            var rig = Make();
            Check.Equal(3, rig.Catalog.Languages.Count, "最初は 3 言語");
            rig.Source.Put(IndexPath, """{ "default": "ja", "languages": [ { "code": "ja" }, { "code": "en" }, { "code": "ru" }, { "code": "de" } ] }""");
            Check.True(rig.Catalog.PollChanges(), "読み直す");
            Check.Equal(4, rig.Catalog.Languages.Count, "4 言語");

            var pak = Make();
            pak.Source.StampsUnknown = true;
            _ = pak.Catalog.Language;
            pak.Source.Put(JaPath, """{ "menu": { "start": "変えた" } }""");
            Check.True(!pak.Catalog.PollChanges(), "印が 0（PAK 同梱）は変わらない扱い");
            Check.Equal("はじめる", pak.Catalog.Get("menu.start", None), "前の文のまま");
        });

        // ── 置き場・壊れたデータ ────────────────────────────────
        h.Add("本体: 置き場（末尾の / を落とす・読み込み前は読まない・読み込み後に変えると読み直す）", () =>
        {
            var source = new MemoryLocaleSource()
                .Put("assets://game/locale/index.json", """{ "languages": [ { "code": "ja" } ] }""")
                .Put("assets://game/locale/ja.json", """{ "a": "別の置き場" }""");
            var c = new LocaleCatalog(source, new MemoryLocaleStore());
            Check.True(!c.Configure("assets://game/locale/"), "読み込み前は読み直さない");
            Check.Equal("assets://game/locale", c.Root, "末尾の / を落とす");
            Check.Equal("別の置き場", c.Get("a", None), "その置き場から読む");
            Check.True(!c.Configure("assets://game/locale"), "同じ置き場は読み直さない");
            Check.True(c.Configure(""), "既定の置き場へ戻すと読み直す");
            Check.Equal(LocalePaths.DefaultRoot, c.Root, "空は既定");
        });

        h.Add("本体: index.json が無い（言語 0 件）・言語の表が無い・壊れている → 警告して次の言語・欠けの方針", () =>
        {
            var none = new LocaleCatalog(new MemoryLocaleSource(), new MemoryLocaleStore(), new WarningLog().Add);
            Check.Equal("", none.Language, "言語なし");
            Check.Equal(0, none.Languages.Count, "一覧なし");
            Check.Equal("[x]", none.Get("x", None), "欠けの方針");

            var rig = Make(system: "en");
            rig.Source.Remove(EnPath);
            Check.Equal("はじめる", rig.Catalog.Get("menu.start", None), "en.json が無い → ja");
            Check.Equal(1, rig.Log.CountContaining("言語の表を読めません"), "警告");

            var broken = Make(system: "en");
            broken.Source.Put(EnPath, "{ broken");
            Check.Equal("はじめる", broken.Catalog.Get("menu.start", None), "壊れた en.json → ja");
            Check.Equal(1, broken.Log.CountContaining("JSON が壊れています"), "警告");
        });
    }
}
