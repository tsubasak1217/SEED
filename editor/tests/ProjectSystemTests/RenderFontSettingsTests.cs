using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace ProjectSystemTests;

/// <summary>
/// project_settings.json の "render" 節（描画の構成）と "font" 節（文字の距離場）の読み書き、構成の一覧、
/// ランタイムの正典（Rust のソース）との突き合わせ（2026-10-02。プロジェクト設定の画面に欄を足したとき）。
///
/// 検証の柱:
///   1. 節が無い旧ファイル → 既定（full・mtsdf/ink_trap）で読み、保存しても節を書かない
///   2. 節あり → 値を読み、型の違う値・知らないキー・オブジェクトでない節は保つ
///   3. 既定値（画面で選んだ既定の構成・既定の距離場）はキーを書かない／旗は 3 状態（構成のまま＝キーなし・true・false）
///   4. 構成の一覧（埋め込みの render_profiles.json）と組み込みの既定の一覧・実効の構成の決め方
///   5. キー名・値の綴り・既定がランタイムの flags.rs / resolve.rs / catalog.rs / field_settings.rs / glyph_field.rs /
///      edge_color.rs と一致する
/// </summary>
public static class RenderFontSettingsTests
{
    /// <summary>project_settings.json のファイル名。</summary>
    private const string SettingsFileName = "project_settings.json";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 描画の構成（render 節）──
        harness.Add("render / font 節が無い旧ファイルは既定（full・mtsdf/ink_trap）で読み、保存しても節を書かない", MissingSectionsAreDefaults);
        harness.Add("render 節は profile・旗・memory_hint を読み、型の違う値・知らないキーは保つ（往復）",          RenderSectionRoundTrip);
        harness.Add("render 節の旗は 3 状態（構成のまま＝キーなし / true / false）、空になれば節ごと消える",       RenderFlagTriState);
        harness.Add("render 節: 既定の構成を選ぶと profile を書かない・表に無い旗は拒む",                         RenderDefaultProfileIsNotWritten);
        harness.Add("render 節がオブジェクトでなければ保存で失わず、画面で選ぶと置き換える",                      RenderUnreadableSectionIsKept);
        harness.Add("埋め込みの構成の一覧は full / ui を警告なしで持ち、組み込みの既定の一覧と食い違わない",       ProfileCatalogEmbeddedMatchesFallback);
        harness.Add("構成の定義の書式違いは構成なし＋警告（重複・名前なし・読めない旗は飛ばす）、読めなければ組み込みの一覧", ProfileCatalogRejectsBrokenDefinitions);
        harness.Add("実効の構成: 既定 ← 構成の名前（知らない名前は既定のまま）← 旗の上書き、3D なしは 3D の資源も止まる", ProfileResolveOrder);
        harness.Add("描画の構成のキー・値はランタイム（flags.rs / resolve.rs / catalog.rs）と一致する",          RenderKeysMatchRuntime);

        // ── 文字の距離場（font 節）──
        harness.Add("font 節は値を読み（別名・大文字も正規化）、読めない値・知らないキーは保つ（往復）",          FontSectionRoundTrip);
        harness.Add("font 節: 画面で既定値を選ぶとキーを書かず、節も消える。既定以外は書く",                     FontDefaultIsNotWritten);
        harness.Add("font 節がオブジェクトでなければ保存で失わない",                                               FontUnreadableSectionIsKept);
        harness.Add("文字の距離場のキー・値・既定はランタイム（field_settings.rs / glyph_field.rs / edge_color.rs）と一致する", FontKeysMatchRuntime);
    }

    // ============================================================
    //  共通
    // ============================================================

    /// <summary>JSON を書いたファイルを読み込む。</summary>
    /// <param name="temp">一時フォルダ。</param>
    /// <param name="json">中身。</param>
    /// <returns>（パス, 読んだ設定）。</returns>
    private static (string Path, ProjectSettingsData Data) LoadFrom(TempDir temp, string json)
    {
        var path = temp.Combine(SettingsFileName);
        File.WriteAllText(path, json);
        return (path, ProjectSettingsData.LoadFrom(path));
    }

    /// <summary>保存したファイルの JSON を読む。</summary>
    /// <param name="path">パス。</param>
    /// <returns>JSON の文書（呼び出し側で Dispose）。</returns>
    private static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    // ============================================================
    //  描画の構成（render 節）
    // ============================================================

    /// <summary>節が無い旧ファイルは既定で読み、保存しても節を増やさない。</summary>
    private static void MissingSectionsAreDefaults()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp, "{ \"game_name\": \"Old\", \"deferred\": true }");
        Check.True(data.Render is null, "render 節は null（既定）");
        Check.True(data.Font is null, "font 節は null（既定）");

        var resolved = RenderProfileCatalog.Resolve(RenderProfileCatalog.Current, data.Render);
        Check.Equal(RenderProfileCatalog.FallbackProfileName, resolved.Name, "既定の構成は full");
        Check.True(resolved.Flags.IsFull, "full はすべて用意する（従来どおり）");

        data.SaveTo(path);
        using var doc = ReadJson(path);
        Check.True(!doc.RootElement.TryGetProperty(RenderProfileSettings.SectionKey, out _), "render 節を書かない");
        Check.True(!doc.RootElement.TryGetProperty(FontFieldSettings.SectionKey, out _), "font 節を書かない");
        Check.True(doc.RootElement.TryGetProperty("deferred", out _), "知らないトップレベルのキーは残る");
    }

    /// <summary>値を読み、ランタイムが読めない値・知らないキーは保つ。書き戻しは型の値（真偽）で、順は profile → 旗 → memory_hint。</summary>
    private static void RenderSectionRoundTrip()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp,
            "{ \"game_name\": \"G\", \"render\": { \"nope\": true, \"profile\": \"ui\", \"post\": true, \"gi\": \" FALSE \", " +
            "\"memory_hint\": \" Memory_Usage \", \"scene_3d\": 3, \"bindless\": \"maybe\" } }");
        var render = data.Render;
        Check.True(render is not null, "render 節を読む");
        Check.Equal("ui", render!.Profile, "profile");
        Check.Equal(true, render.GetFlagOverride(RenderProfileFlagCatalog.PostKey), "post（真偽）");
        Check.Equal(false, render.GetFlagOverride(RenderProfileFlagCatalog.GiKey), "gi（文字列の \"false\" もランタイムと同じく読む）");
        Check.Equal(RenderProfileFlagCatalog.MemoryHintMemoryUsage, render.MemoryHint, "memory_hint（空白・大文字は正規化）");
        Check.True(render.GetFlagOverride(RenderProfileFlagCatalog.Scene3DKey) is null, "読めない旗（数）は上書きにしない");
        Check.True(render.ExtraData.ContainsKey("scene_3d") && render.ExtraData.ContainsKey("bindless") && render.ExtraData.ContainsKey("nope"),
            "読めない値・知らないキーは保つ");

        var resolved = RenderProfileCatalog.Resolve(RenderProfileCatalog.Current, render);
        Check.Equal("ui", resolved.Name, "実効の構成");
        Check.True(resolved.Flags.Get(RenderProfileFlagCatalog.PostKey), "post の上書きが効く");
        Check.True(!resolved.Flags.Get(RenderProfileFlagCatalog.Scene3DKey), "読めない scene_3d は捨てて構成（ui）のまま");

        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            var section = doc.RootElement.GetProperty(RenderProfileSettings.SectionKey);
            var keys = section.EnumerateObject().Select(p => p.Name).ToList();
            Check.Equal(RenderProfileSettings.ProfileKey, keys[0], "profile が先頭");
            Check.Equal("ui", section.GetProperty("profile").GetString(), "profile");
            Check.Equal(JsonValueKind.True, section.GetProperty("post").ValueKind, "post は真偽で書く");
            Check.Equal(JsonValueKind.False, section.GetProperty("gi").ValueKind, "gi は真偽で書く（文字列から正規化）");
            Check.Equal("memory_usage", section.GetProperty("memory_hint").GetString(), "memory_hint は正規化した綴り");
            Check.Equal(3, section.GetProperty("scene_3d").GetInt32(), "読めない値はそのまま書き戻す");
            Check.Equal("maybe", section.GetProperty("bindless").GetString(), "読めない文字列もそのまま");
            Check.Equal(JsonValueKind.True, section.GetProperty("nope").ValueKind, "知らないキーも書き戻す");
            Check.Equal(keys.Count, keys.Distinct().Count(), "同じキーを 2 回書かない");
        }
        var reloaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal("ui", reloaded.Render?.Profile, "読み戻し: profile");
        Check.Equal(true, reloaded.Render?.GetFlagOverride(RenderProfileFlagCatalog.PostKey), "読み戻し: post");
        Check.Equal("G", reloaded.GameName, "他の設定");
    }

    /// <summary>旗の 3 状態と、空になった節の省略。</summary>
    private static void RenderFlagTriState()
    {
        using var temp = new TempDir();
        var path = temp.Combine(SettingsFileName);
        var data = new ProjectSettingsData { Render = new RenderProfileSettings() };

        data.Render.SetFlagOverride(RenderProfileFlagCatalog.PostKey, true);
        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.Equal(JsonValueKind.True, doc.RootElement.GetProperty("render").GetProperty("post").ValueKind, "有効 → true");
        }

        data.Render!.SetFlagOverride(RenderProfileFlagCatalog.PostKey, false);
        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.Equal(JsonValueKind.False, doc.RootElement.GetProperty("render").GetProperty("post").ValueKind, "無効 → false");
        }

        data.Render!.SetFlagOverride(RenderProfileFlagCatalog.PostKey, null);
        Check.True(data.Render.IsEmpty, "構成のまま → 上書きなし（節は空）");
        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.True(!doc.RootElement.TryGetProperty(RenderProfileSettings.SectionKey, out _), "空の節は書かない");
        }
        Check.True(ProjectSettingsData.LoadFrom(path).Render is null, "読み戻すと null（既定）");

        // 読めなかった値を画面で選び直すと、型の値で置き換える（同じキーを 2 回書かない）
        var (path2, loaded) = LoadFrom(temp, "{ \"render\": { \"post\": 3 } }");
        loaded.Render!.SetFlagOverride(RenderProfileFlagCatalog.PostKey, true);
        loaded.SaveTo(path2);
        var text = File.ReadAllText(path2);
        Check.Equal(1, Regex.Matches(text, "\"post\"").Count, "post は 1 回だけ");
        Check.Equal(true, ProjectSettingsData.LoadFrom(path2).Render?.GetFlagOverride(RenderProfileFlagCatalog.PostKey), "型の値として読める");
    }

    /// <summary>既定の構成の判定（画面は既定の構成を選ぶと profile を書かない）と、表に無い旗の拒否。</summary>
    private static void RenderDefaultProfileIsNotWritten()
    {
        var catalog = RenderProfileCatalog.Current;
        Check.True(catalog.IsDefault(" FULL "), "既定の構成は大文字小文字・空白を問わず判定する");
        Check.True(!catalog.IsDefault(RenderProfileCatalog.UiProfileName), "ui は既定でない");

        using var temp = new TempDir();
        var path = temp.Combine(SettingsFileName);
        var settings = new RenderProfileSettings();
        settings.SetProfile(RenderProfileCatalog.UiProfileName);
        Check.Equal("ui", settings.Profile, "既定以外は書く");
        // 画面は既定の構成を選ぶと SetProfile(null) を呼ぶ（ProjectSettingsWindow.Render.cs の OnRenderProfileChanged）
        settings.SetProfile(catalog.IsDefault(RenderProfileCatalog.FallbackProfileName) ? null : RenderProfileCatalog.FallbackProfileName);
        Check.True(settings.Profile is null && settings.IsEmpty, "既定の構成を選ぶとキーを書かない");
        settings.SetProfile("   ");
        Check.True(settings.Profile is null, "空白だけの名前は既定");
        new ProjectSettingsData { Render = settings }.SaveTo(path);
        using var doc = ReadJson(path);
        Check.True(!doc.RootElement.TryGetProperty(RenderProfileSettings.SectionKey, out _), "何も無ければ節を書かない");

        var threw = false;
        try { settings.SetFlagOverride("nope", true); }
        catch (ArgumentException) { threw = true; }
        Check.True(threw, "表に無い旗は ArgumentException");
        Check.True(settings.IsEmpty, "拒んだ旗は入らない");
    }

    /// <summary>オブジェクトでない節は保存で失わず、画面で選ぶと型の節で置き換える。</summary>
    private static void RenderUnreadableSectionIsKept()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp, "{ \"game_name\": \"Keep\", \"render\": \"ui\" }");
        Check.Equal("Keep", data.GameName, "他の設定は読める");
        Check.True(data.Render?.UnreadableSection is not null, "オブジェクトでない節を覚える");
        Check.True(data.Render!.Profile is null, "型の値としては読まない（ランタイムも警告して無視）");
        Check.Equal(RenderProfileCatalog.FallbackProfileName, RenderProfileCatalog.Resolve(RenderProfileCatalog.Current, data.Render).Name,
            "実効は既定");

        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.Equal("ui", doc.RootElement.GetProperty("render").GetString(), "そのまま書き戻す");
        }

        var reloaded = ProjectSettingsData.LoadFrom(path);
        reloaded.Render!.SetProfile(RenderProfileCatalog.UiProfileName);
        reloaded.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.Equal("ui", doc.RootElement.GetProperty("render").GetProperty("profile").GetString(), "選ぶとオブジェクトに置き換える");
        }
    }

    /// <summary>埋め込みの一覧（ランタイムと同じ render_profiles.json）と、組み込みの既定の一覧の一致。</summary>
    private static void ProfileCatalogEmbeddedMatchesFallback()
    {
        var catalog = RenderProfileCatalog.Current;
        Check.True(!catalog.IsFallback, "埋め込みの定義を読めている");
        Check.True(catalog.Warnings.Count == 0, $"警告なし: {string.Join(" / ", catalog.Warnings)}");
        Check.Equal(RenderProfileCatalog.FallbackProfileName, catalog.DefaultProfile, "既定は full");

        var full = catalog.Find(RenderProfileCatalog.FallbackProfileName);
        Check.True(full is not null && full.Flags.IsFull, "full はすべて用意する");
        var ui = catalog.Find(" UI ");
        Check.True(ui is not null, "ui がある（名前は大文字小文字・空白を問わない）");
        foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
        {
            Check.True(!ui!.Flags.Get(flag.Key), $"ui は {flag.Key} を止める");
        }
        Check.Equal(RenderProfileFlagCatalog.MemoryHintMemoryUsage, ui!.Flags.MemoryHint, "ui は memory_usage");
        foreach (var profile in catalog.Profiles)
        {
            Check.True(profile.Label.Length > 0 && profile.Description.Length > 0, $"{profile.Name} に表示名と説明がある");
        }

        // 組み込みの既定の一覧（読めないときだけ使う）は、定義の同じ名前の構成と同じ表示名・旗
        foreach (var fallback in RenderProfileCatalog.FallbackProfiles)
        {
            var defined = catalog.Find(fallback.Name);
            Check.True(defined is not null, $"組み込みの {fallback.Name} が定義にもある");
            Check.Equal(defined!.Label, fallback.Label, $"{fallback.Name} の表示名");
            foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
            {
                Check.Equal(defined.Flags.Get(flag.Key), fallback.Flags.Get(flag.Key), $"{fallback.Name} の {flag.Key}");
            }
            Check.Equal(defined.Flags.MemoryHint, fallback.Flags.MemoryHint, $"{fallback.Name} の memory_hint");
        }
    }

    /// <summary>書式違い・重複・名前なし・読めない旗。読めなければ組み込みの一覧へ切り替える。</summary>
    private static void ProfileCatalogRejectsBrokenDefinitions()
    {
        var broken = RenderProfileCatalog.Parse("{");
        Check.True(broken.Profiles.Count == 0 && broken.Warnings.Count > 0, "JSON の誤りは構成なし＋警告");
        Check.Equal(0, RenderProfileCatalog.Parse("{\"format_version\": 9, \"profiles\": []}").Profiles.Count, "版の違い");
        Check.Equal(0, RenderProfileCatalog.Parse("{\"format_version\": 1}").Profiles.Count, "配列なし");

        var partial = RenderProfileCatalog.Parse(
            "{\"format_version\": 1, \"default_profile\": \"a\", \"profiles\": [" +
            "{\"name\": \"a\", \"flags\": {\"gi\": false, \"post\": \"FALSE\", \"memory_hint\": \"low\", \"oops\": 1}}," +
            "{\"name\": \"A\", \"flags\": {\"gi\": true}}, {\"label\": \"名前なし\"}, {\"name\": \"b\", \"flags\": 3} ]}");
        Check.Equal(2, partial.Profiles.Count, "重複・名前なしは飛ばす（a と b）");
        var a = partial.Find("a")!;
        Check.True(!a.Flags.Get(RenderProfileFlagCatalog.GiKey) && !a.Flags.Get(RenderProfileFlagCatalog.PostKey), "読める旗は読む");
        Check.Equal(RenderProfileFlagCatalog.DefaultMemoryHint, a.Flags.MemoryHint, "知らない memory_hint は捨てる");
        Check.True(partial.Find("b")!.Flags.IsFull, "flags がオブジェクトでなければ既定の旗");
        Check.Equal(5, partial.Warnings.Count,
            $"警告は memory_hint・oops・重複・名前なし・flags の型の 5 件: {string.Join(" / ", partial.Warnings)}");

        var missingDefault = RenderProfileCatalog.Parse("{\"format_version\": 1, \"default_profile\": \"zzz\", \"profiles\": [{\"name\": \"a\"}]}");
        Check.True(missingDefault.DefaultDefinition is null && missingDefault.Warnings.Count == 1, "既定の構成が一覧に無ければ警告");
        Check.Equal(RenderProfileCatalog.FallbackProfileName, RenderProfileCatalog.Resolve(missingDefault, null).Name,
            "既定の構成が無ければ full として見積もる（ランタイムと同じ）");

        var fallback = RenderProfileCatalog.Fallback(["理由"]);
        Check.True(fallback.IsFallback, "組み込みの一覧の印");
        Check.True(fallback.Find(RenderProfileCatalog.FallbackProfileName) is not null && fallback.Find(RenderProfileCatalog.UiProfileName) is not null,
            "組み込みの一覧は full と ui");
        Check.True(fallback.Warnings.Count == 2 && fallback.Warnings[0] == "理由", "読めなかった理由と切り替えの警告を出す");
    }

    /// <summary>実効の構成の決め方（ランタイムの resolve.rs と同じ順）。</summary>
    private static void ProfileResolveOrder()
    {
        var catalog = RenderProfileCatalog.Current;
        Check.True(RenderProfileCatalog.Resolve(catalog, null).Flags.IsFull, "節なし → full");

        var ui = new RenderProfileSettings();
        ui.SetProfile(" UI ");
        var resolvedUi = RenderProfileCatalog.Resolve(catalog, ui);
        Check.Equal("ui", resolvedUi.Name, "名前は一覧の綴りへ");
        Check.True(!resolvedUi.Flags.Get(RenderProfileFlagCatalog.Scene3DKey), "ui は 3D を描かない");

        ui.SetFlagOverride(RenderProfileFlagCatalog.PostKey, true);
        var withPost = RenderProfileCatalog.Resolve(catalog, ui);
        Check.True(withPost.Flags.Get(RenderProfileFlagCatalog.PostKey), "旗の上書きが効く");
        Check.True(!withPost.ProfileFlags.Get(RenderProfileFlagCatalog.PostKey), "構成そのものの値（上書き前）は別に持つ");

        var unknown = new RenderProfileSettings();
        unknown.SetProfile("tiny");
        Check.Equal(RenderProfileCatalog.FallbackProfileName, RenderProfileCatalog.Resolve(catalog, unknown).Name, "知らない名前は既定のまま");

        // scene_3d だけを止めても、3D だけの資源（影・GI・bindless・RT・デファード）は実効で止まる（flags.rs の allocates_*）
        var no3D = new RenderProfileSettings();
        no3D.SetFlagOverride(RenderProfileFlagCatalog.Scene3DKey, false);
        var flags = RenderProfileCatalog.Resolve(catalog, no3D).Flags;
        foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
        {
            var expected = flag.Key != RenderProfileFlagCatalog.Scene3DKey && !flag.RequiresScene3D;
            Check.Equal(expected, flags.IsEffectivelyEnabled(flag.Key), $"scene_3d=false のときの {flag.Key} の実効");
            Check.True(flag.Key == RenderProfileFlagCatalog.Scene3DKey || flags.Get(flag.Key), $"{flag.Key} の値そのものは有効のまま");
        }
        Check.True(flags.Describe().Contains("3D のシーン 無効"), $"要約に出る: {flags.Describe()}");
    }

    /// <summary>キー名・値・既定・3D に依る旗がランタイムのソースと一致する。</summary>
    private static void RenderKeysMatchRuntime()
    {
        const string flagsRs = "runtime/src/engine/core/renderer/render_profile/flags.rs";
        const string resolveRs = "runtime/src/engine/core/renderer/render_profile/resolve.rs";
        const string catalogRs = "runtime/src/engine/core/renderer/render_profile/catalog.rs";
        var flagsSource = RustSourceText.Read(flagsRs);
        var flagConstants = RustSourceText.StrConstants(flagsSource);

        // KNOWN_FLAG_KEYS の並び（KEY_* の名前）→ 綴り。エディタの表（真偽の旗 → memory_hint）と同じ順・同じ綴り
        var known = Regex.Match(flagsSource, @"KNOWN_FLAG_KEYS\s*:\s*\[&str;\s*\d+\]\s*=\s*\[(?<items>[^\]]*)\]");
        Check.True(known.Success, $"{flagsRs} に KNOWN_FLAG_KEYS が見つかりません");
        var runtimeKeys = Regex.Matches(known.Groups["items"].Value, @"\w+")
            .Select(m => RustSourceText.Require(flagConstants, m.Value, flagsRs))
            .ToList();
        var editorKeys = RenderProfileFlagCatalog.ToggleFlags.Select(f => f.Key).Append(RenderProfileFlagCatalog.MemoryHintKey).ToList();
        Check.Equal(string.Join(",", runtimeKeys), string.Join(",", editorKeys), "旗のキーの並び（KNOWN_FLAG_KEYS）");

        // memory_hint の綴りと既定
        Check.Equal(RustSourceText.Require(flagConstants, "MEMORY_HINT_PERFORMANCE", flagsRs), RenderProfileFlagCatalog.MemoryHintPerformance, "performance");
        Check.Equal(RustSourceText.Require(flagConstants, "MEMORY_HINT_MEMORY_USAGE", flagsRs), RenderProfileFlagCatalog.MemoryHintMemoryUsage, "memory_usage");
        Check.Equal("Performance", RustSourceText.DefaultVariant(flagsSource, flagsRs), "MemoryHint の既定は Performance");
        Check.Equal(RenderProfileFlagCatalog.MemoryHintPerformance, RenderProfileFlagCatalog.DefaultMemoryHint, "エディタの既定も performance");

        // 3D のシーンを描かないと止まる旗（`self.<旗> && self.scene_3d`）
        var requiresScene3D = Regex.Matches(flagsSource, @"self\.(?<flag>\w+)\s*&&\s*self\.scene_3d")
            .Select(m => m.Groups["flag"].Value).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        Check.True(requiresScene3D.Count > 0, $"{flagsRs} から 3D に依る旗を読めない（書き方が変わった？）");
        var editorRequires = RenderProfileFlagCatalog.ToggleFlags.Where(f => f.RequiresScene3D).Select(f => f.Key)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Check.Equal(string.Join(",", requiresScene3D), string.Join(",", editorRequires), "3D のシーンに依る旗（allocates_* など）");

        // 節・構成の名前のキー、既定の構成の名前、定義の書式の版
        var resolveConstants = RustSourceText.StrConstants(RustSourceText.Read(resolveRs));
        Check.Equal(RustSourceText.Require(resolveConstants, "RENDER_KEY", resolveRs), RenderProfileSettings.SectionKey, "render 節のキー");
        Check.Equal(RustSourceText.Require(resolveConstants, "PROFILE_KEY", resolveRs), RenderProfileSettings.ProfileKey, "profile のキー");
        var catalogSource = RustSourceText.Read(catalogRs);
        Check.Equal(RustSourceText.Require(RustSourceText.StrConstants(catalogSource), "FALLBACK_PROFILE_NAME", catalogRs),
            RenderProfileCatalog.FallbackProfileName, "既定の構成の名前（定義に無いとき）");
        Check.True(RustSourceText.IntConstants(catalogSource).TryGetValue("PROFILE_FORMAT_VERSION", out var version), "PROFILE_FORMAT_VERSION を読む");
        Check.Equal(version, RenderProfileCatalog.FormatVersion, "構成の定義の書式の版");
    }

    // ============================================================
    //  文字の距離場（font 節）
    // ============================================================

    /// <summary>値を読み（別名・大文字も正規化）、読めない値・知らないキーは保つ。手で書いた既定値も残す。</summary>
    private static void FontSectionRoundTrip()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp,
            "{ \"font\": { \"distance_field\": \" SDF \", \"msdf_coloring\": \"Ink-Trap\", \"preload\": [\"あ\"] } }");
        Check.Equal(FontFieldCatalog.DistanceFieldSdf, data.Font?.DistanceField, "distance_field（空白・大文字は正規化）");
        Check.Equal(FontFieldCatalog.ColoringInkTrap, data.Font?.MsdfColoring, "msdf_coloring（別名 ink-trap は ink_trap）");
        Check.True(data.Font!.ExtraData.ContainsKey("preload"), "知らないキーは保つ");

        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            var section = doc.RootElement.GetProperty(FontFieldSettings.SectionKey);
            Check.Equal("sdf", section.GetProperty("distance_field").GetString(), "distance_field");
            Check.Equal("ink_trap", section.GetProperty("msdf_coloring").GetString(), "手で書いた既定値は選び直さない限り残す");
            Check.Equal(JsonValueKind.Array, section.GetProperty("preload").ValueKind, "知らないキーを書き戻す");
        }

        var (path2, unreadable) = LoadFrom(temp, "{ \"font\": { \"distance_field\": \"bitmap\", \"msdf_coloring\": 1 } }");
        Check.True(unreadable.Font!.DistanceField is null && unreadable.Font.MsdfColoring is null, "読めない値は未設定");
        Check.Equal(FontFieldCatalog.DefaultDistanceField, unreadable.Font.EffectiveDistanceField, "実効は既定（ランタイムと同じ）");
        unreadable.SaveTo(path2);
        using (var doc = ReadJson(path2))
        {
            var section = doc.RootElement.GetProperty(FontFieldSettings.SectionKey);
            Check.Equal("bitmap", section.GetProperty("distance_field").GetString(), "読めない値はそのまま書き戻す");
            Check.Equal(1, section.GetProperty("msdf_coloring").GetInt32(), "型の違う値もそのまま");
        }
    }

    /// <summary>画面で既定値を選ぶとキーを書かない（節も消える）。既定以外は書く。読めない値は選び直すと置き換える。</summary>
    private static void FontDefaultIsNotWritten()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp, "{ \"font\": { \"distance_field\": \"bitmap\", \"msdf_coloring\": \"simple\" } }");
        var font = data.Font!;

        font.SetDistanceField(FontFieldCatalog.DistanceFieldMtsdf);
        font.SetColoring(FontFieldCatalog.ColoringInkTrap);
        Check.True(font.DistanceField is null && font.MsdfColoring is null, "既定値は null（書かない）");
        Check.True(font.IsEmpty, "読めなかった値も選び直しで消える");
        data.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            Check.True(!doc.RootElement.TryGetProperty(FontFieldSettings.SectionKey, out _), "空になった節は書かない");
        }

        var fresh = new ProjectSettingsData { Font = new FontFieldSettings() };
        fresh.Font.SetDistanceField(FontFieldCatalog.DistanceFieldSdf);
        fresh.Font.SetColoring(FontFieldCatalog.ColoringSimple);
        fresh.SaveTo(path);
        using (var doc = ReadJson(path))
        {
            var section = doc.RootElement.GetProperty(FontFieldSettings.SectionKey);
            Check.Equal("sdf", section.GetProperty("distance_field").GetString(), "既定以外の距離場は書く");
            Check.Equal("simple", section.GetProperty("msdf_coloring").GetString(), "既定以外の色分けは書く");
        }
        var reloaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal(FontFieldCatalog.DistanceFieldSdf, reloaded.Font?.EffectiveDistanceField, "読み戻し");
        Check.Equal(FontFieldCatalog.ColoringSimple, reloaded.Font?.EffectiveColoring, "読み戻し");
    }

    /// <summary>オブジェクトでない節は保存で失わない。</summary>
    private static void FontUnreadableSectionIsKept()
    {
        using var temp = new TempDir();
        var (path, data) = LoadFrom(temp, "{ \"game_name\": \"Keep\", \"font\": \"sdf\" }");
        Check.Equal("Keep", data.GameName, "他の設定は読める");
        Check.True(data.Font?.UnreadableSection is not null && data.Font.DistanceField is null, "オブジェクトでない節を覚える");
        data.SaveTo(path);
        using var doc = ReadJson(path);
        Check.Equal("sdf", doc.RootElement.GetProperty("font").GetString(), "そのまま書き戻す");
    }

    /// <summary>キー名・値の綴り・別名・既定がランタイムのソースと一致する。</summary>
    private static void FontKeysMatchRuntime()
    {
        const string settingsRs = "runtime/src/engine/core/font/field_settings.rs";
        const string glyphFieldRs = "runtime/src/engine/core/font/glyph_field.rs";
        const string edgeColorRs = "runtime/src/engine/core/font/msdf/edge_color.rs";

        var keys = RustSourceText.StrConstants(RustSourceText.Read(settingsRs));
        Check.Equal(RustSourceText.Require(keys, "FONT_KEY", settingsRs), FontFieldSettings.SectionKey, "font 節のキー");
        Check.Equal(RustSourceText.Require(keys, "DISTANCE_FIELD_KEY", settingsRs), FontFieldSettings.DistanceFieldKey, "distance_field のキー");
        Check.Equal(RustSourceText.Require(keys, "COLORING_KEY", settingsRs), FontFieldSettings.ColoringKey, "msdf_coloring のキー");

        CheckEnumMirror(RustSourceText.Read(glyphFieldRs), glyphFieldRs,
            FontFieldCatalog.ParseDistanceField, FontFieldCatalog.DistanceFieldChoices, FontFieldCatalog.DefaultDistanceField);
        CheckEnumMirror(RustSourceText.Read(edgeColorRs), edgeColorRs,
            FontFieldCatalog.ParseColoring, FontFieldCatalog.ColoringChoices, FontFieldCatalog.DefaultColoring);
    }

    /// <summary>
    /// Rust の enum（parse の腕・as_str の腕・#[default]）とエディタの写し（読み方・選択肢・既定）が一致することを確かめる。
    /// </summary>
    /// <param name="source">Rust のソース。</param>
    /// <param name="file">出どころ。</param>
    /// <param name="parse">エディタの読み方。</param>
    /// <param name="choices">エディタの選択肢。</param>
    /// <param name="editorDefault">エディタの既定。</param>
    private static void CheckEnumMirror(
        string source, string file, Func<string?, string?> parse, IReadOnlyList<FontFieldChoice> choices, string editorDefault)
    {
        var asStr = RustSourceText.AsStrArms(source);
        var parseArms = RustSourceText.ParseArms(source);
        Check.True(asStr.Count > 0 && parseArms.Count > 0, $"{file} の as_str / parse の腕を読めない（書き方が変わった？）");

        // ランタイムが受け付ける綴り（別名を含む）は、エディタも同じ値へ読む
        foreach (var (word, variant) in parseArms)
        {
            Check.True(asStr.TryGetValue(variant, out var canonical), $"{file}: {variant} の as_str が無い");
            Check.Equal(canonical, parse(word), $"{file}: \"{word}\" の読み方");
            Check.Equal(canonical, parse($" {word.ToUpperInvariant()} "), $"{file}: \"{word}\" は大文字・空白を問わない");
        }
        Check.True(parse("知らない") is null, $"{file}: 知らない綴りは null");

        // 画面の選択肢 = ランタイムの綴りの全部（過不足なし）、既定 = #[default]
        Check.Equal(string.Join(",", asStr.Values.OrderBy(v => v, StringComparer.Ordinal)),
            string.Join(",", choices.Select(c => c.Value).OrderBy(v => v, StringComparer.Ordinal)), $"{file}: 選択肢の綴り");
        Check.Equal(asStr[RustSourceText.DefaultVariant(source, file)], editorDefault, $"{file}: 既定");
        Check.Equal(editorDefault, choices[0].Value, $"{file}: 既定は選択肢の先頭");
    }
}
