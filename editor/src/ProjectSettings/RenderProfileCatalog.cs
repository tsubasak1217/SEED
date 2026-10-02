// ============================================================
//  RenderProfileCatalog.cs — 描画の構成（render profile）の一覧（runtime/config/render_profiles.json を読む）
//
//  【役割】
//  プロジェクト設定「描画の構成」の選択肢（名前・表示名・説明・旗）と、既定の構成の名前を出す。
//  定義ファイルはランタイムと同じ runtime/config/render_profiles.json を、エディタのビルドで埋め込みリソースとして
//  取り込む（SEEDEditor.csproj の EmbeddedResource。論理名 <see cref="ResourceName"/>）。ランタイムも同じファイルを
//  ビルド時に埋め込む（render_profile/catalog.rs の include_str!）ので、エディタに出る一覧とランタイムが使う中身は
//  食い違わない（描画品質プリセットの RenderQualityPresetCatalog と同じ流儀）。
//
//  【読み方】ランタイムの catalog.rs の ProfileCatalog::parse と同じ規則:
//    - JSON の誤り・版（format_version）の違い・profiles の配列なし → 構成なし＋警告
//    - 名前の無い・空・重複した構成は読み飛ばして警告。読めない旗はその旗だけ捨てて警告
//    - 既定の構成（default_profile）が一覧に無ければ警告（実効は full＝すべて用意する）
//
//  【読めないとき】（埋め込みが無い・書式の誤りで構成が 1 つも無い）
//  組み込みの既定の一覧（full / ui。<see cref="FallbackProfiles"/>）へ切り替え、警告を出す（画面に出す）。
//  組み込みの一覧の中身が JSON と食い違わないことは ProjectSystemTests が確かめる。
//
//  【実効の構成】<see cref="Resolve"/> はランタイムの resolve.rs（起動オプションを除く）と同じ順で重ねる:
//    定義の既定の構成 ← project_settings.json の render.profile（知らない名前は既定のまま）← 旗の上書き
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>描画の構成 1 つ（定義ファイルの 1 要素）。</summary>
/// <param name="Name">名前（設定に書くもの。例 ui）。</param>
/// <param name="Label">表示名。</param>
/// <param name="Description">説明。</param>
/// <param name="Flags">旗（書かれていない旗は既定＝用意する）。</param>
public sealed record RenderProfileDefinition(string Name, string Label, string Description, RenderProfileFlagValues Flags);

/// <summary>描画の構成の一覧（読んだ結果）。</summary>
/// <param name="DefaultProfile">既定の構成の名前（project_settings.json に render.profile が無いとき）。</param>
/// <param name="Profiles">定義順の構成。</param>
/// <param name="Warnings">読み取りの警告（画面に出す）。</param>
/// <param name="IsFallback">定義を読めず、組み込みの既定の一覧を使っているか。</param>
public sealed record RenderProfileCatalogData(
    string DefaultProfile,
    IReadOnlyList<RenderProfileDefinition> Profiles,
    IReadOnlyList<string> Warnings,
    bool IsFallback)
{
    /// <summary>名前で構成を引く（前後の空白・大文字小文字は区別しない＝ランタイムの find と同じ。無ければ null）。</summary>
    /// <param name="name">構成の名前。</param>
    /// <returns>構成。</returns>
    public RenderProfileDefinition? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim();
        return Profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>既定の構成（一覧に無ければ null。そのときランタイムは full＝すべて用意する で動く）。</summary>
    public RenderProfileDefinition? DefaultDefinition => Find(DefaultProfile);

    /// <summary>名前が既定の構成を指すか（前後の空白・大文字小文字は区別しない）。</summary>
    /// <param name="name">構成の名前。</param>
    /// <returns>既定の構成なら true。</returns>
    public bool IsDefault(string? name) =>
        name is not null && string.Equals(name.Trim(), DefaultProfile, StringComparison.OrdinalIgnoreCase);
}

/// <summary>実効の描画の構成（エディタの見積り。ランタイムの RenderProfile から起動オプションを除いたもの）。</summary>
/// <param name="Name">構成の名前。</param>
/// <param name="Definition">選ばれた構成の定義（一覧に無ければ null）。</param>
/// <param name="ProfileFlags">構成そのものの旗（上書き前）。</param>
/// <param name="Flags">上書きを重ねた実効の旗。</param>
public sealed record ResolvedRenderProfile(
    string Name,
    RenderProfileDefinition? Definition,
    RenderProfileFlagValues ProfileFlags,
    RenderProfileFlagValues Flags);

/// <summary>描画の構成の一覧（埋め込みの runtime/config/render_profiles.json）。</summary>
public static class RenderProfileCatalog
{
    /// <summary>埋め込みリソースの論理名（SEEDEditor.csproj・テストの csproj と一致させる）。</summary>
    public const string ResourceName = "SEEDEditor.render_profiles.json";

    /// <summary>このエディタが読める構成の定義の書式の版（ランタイムの PROFILE_FORMAT_VERSION と一致）。</summary>
    public const int FormatVersion = 1;

    /// <summary>定義に既定の構成が無いときの名前（ランタイムの FALLBACK_PROFILE_NAME と一致。full＝すべて用意する）。</summary>
    public const string FallbackProfileName = "full";

    /// <summary>組み込みの既定の一覧の「2D/UI だけ」の名前（render_profiles.json の ui と一致）。</summary>
    public const string UiProfileName = "ui";

    // ── 定義ファイルの欄（ランタイムの catalog.rs と一致）────────────

    /// <summary>書式の版の欄。</summary>
    private const string FormatVersionKey = "format_version";

    /// <summary>既定の構成の名前の欄。</summary>
    private const string DefaultProfileKey = "default_profile";

    /// <summary>構成の配列の欄。</summary>
    private const string ProfilesKey = "profiles";

    /// <summary>構成の名前の欄。</summary>
    private const string NameKey = "name";

    /// <summary>構成の表示名の欄。</summary>
    private const string LabelKey = "label";

    /// <summary>構成の説明の欄。</summary>
    private const string DescriptionKey = "description";

    /// <summary>構成の旗の欄。</summary>
    private const string FlagsKey = "flags";

    /// <summary>一覧（初めて使うときに 1 回だけ読む）。</summary>
    private static readonly Lazy<RenderProfileCatalogData> BuiltIn = new(LoadBuiltIn);

    /// <summary>埋め込みの一覧（読めなければ組み込みの既定の一覧と警告）。</summary>
    public static RenderProfileCatalogData Current => BuiltIn.Value;

    /// <summary>
    /// 組み込みの既定の一覧（定義ファイルを読めないときだけ使う。中身は render_profiles.json の full / ui と同じにする。
    /// 食い違いは ProjectSystemTests が見張る）。
    /// </summary>
    public static IReadOnlyList<RenderProfileDefinition> FallbackProfiles { get; } =
    [
        new(FallbackProfileName, "3D あり（従来どおり）",
            "3D の描画資源（影・GI・bindless・レイトレーシング・G-Buffer・後処理・ピッキング）をすべて使える状態にする。既定。",
            RenderProfileFlagValues.Full),
        new(UiProfileName, "2D/UI だけ",
            "キャンバス（2D）・文字・図形・スプライトだけを描くアプリ向け。3D のシーンを描かず、3D の描画資源を作らない。" +
            "GPU メモリも小さな塊で確保する。",
            RenderProfileFlagCatalog.ToggleFlags
                .Aggregate(RenderProfileFlagValues.Full, (flags, flag) => flags.WithToggle(flag.Key, false))
                .WithMemoryHint(RenderProfileFlagCatalog.MemoryHintMemoryUsage)),
    ];

    /// <summary>
    /// 組み込みの既定の一覧を返す（読めなかった理由を警告の先頭に置く）。
    /// </summary>
    /// <param name="reasons">読めなかった理由。</param>
    /// <returns>組み込みの一覧。</returns>
    public static RenderProfileCatalogData Fallback(IEnumerable<string> reasons)
    {
        var warnings = reasons.ToList();
        warnings.Add($"描画の構成の定義（render_profiles.json）を読めないため、組み込みの既定の一覧（{FallbackProfileName} / {UiProfileName}）を出しています");
        return new RenderProfileCatalogData(FallbackProfileName, FallbackProfiles, warnings, IsFallback: true);
    }

    /// <summary>構成の定義の JSON を読む（ランタイムの ProfileCatalog::parse と同じ規則）。</summary>
    /// <param name="json">定義ファイルの中身。</param>
    /// <returns>一覧（書式として読めなければ構成なし＋警告）。</returns>
    public static RenderProfileCatalogData Parse(string json)
    {
        var warnings = new List<string>();
        var profiles = new List<RenderProfileDefinition>();
        var defaultProfile = FallbackProfileName;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // ── 版 ──
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(FormatVersionKey, out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != FormatVersion)
            {
                warnings.Add($"描画の構成の定義の版が読めません（このエディタは {FormatVersion}）");
                return new RenderProfileCatalogData(defaultProfile, profiles, warnings, IsFallback: false);
            }

            // ── 既定の構成の名前 ──
            if (root.TryGetProperty(DefaultProfileKey, out var defaultName) && defaultName.ValueKind == JsonValueKind.String)
            {
                defaultProfile = (defaultName.GetString() ?? string.Empty).Trim();
            }

            // ── 構成の配列 ──
            if (!root.TryGetProperty(ProfilesKey, out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                warnings.Add($"描画の構成の定義に {ProfilesKey} の配列がありません");
                return new RenderProfileCatalogData(defaultProfile, profiles, warnings, IsFallback: false);
            }

            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var definition = ReadProfile(entry, index, profiles, warnings);
                if (definition is not null) profiles.Add(definition);
                index++;
            }
        }
        catch (JsonException ex)
        {
            warnings.Add($"描画の構成の定義を JSON として読めません: {ex.Message}");
            return new RenderProfileCatalogData(defaultProfile, profiles, warnings, IsFallback: false);
        }

        // 既定の構成が一覧に無ければ警告する（ランタイムは full＝すべて用意する で動く）
        if (!profiles.Any(p => string.Equals(p.Name, defaultProfile, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"既定の構成 '{defaultProfile}' が定義にありません（{FallbackProfileName} として動きます）");
        }
        return new RenderProfileCatalogData(defaultProfile, profiles, warnings, IsFallback: false);
    }

    /// <summary>
    /// 実効の構成を決める（ランタイムの resolve_render_profile から起動オプションを除いたものと同じ順）。
    /// 定義の既定の構成 ← 設定の構成の名前（知らない名前は既定のまま）← 旗の上書き。
    /// </summary>
    /// <param name="catalog">構成の一覧。</param>
    /// <param name="settings">project_settings.json の render 節（無ければ null）。</param>
    /// <returns>実効の構成。</returns>
    public static ResolvedRenderProfile Resolve(RenderProfileCatalogData catalog, RenderProfileSettings? settings)
    {
        // ① 定義の既定（一覧に無ければ full＝すべて用意する）
        var definition = catalog.DefaultDefinition;
        var name       = definition?.Name ?? FallbackProfileName;

        // ② 設定の構成の名前（知らない名前はランタイムが警告して既定のまま）
        if (settings?.Profile is { } wanted && catalog.Find(wanted) is { } chosen)
        {
            definition = chosen;
            name       = chosen.Name;
        }

        var profileFlags = definition?.Flags ?? RenderProfileFlagValues.Full;

        // ③ 旗の上書き（表の順に重ねる。読めなかった値は ExtraData にあり、ランタイムも捨てる）
        var flags = profileFlags;
        if (settings is not null)
        {
            foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
            {
                if (settings.GetFlagOverride(flag.Key) is bool value) flags = flags.WithToggle(flag.Key, value);
            }
            if (settings.MemoryHint is { } hint) flags = flags.WithMemoryHint(hint);
        }
        return new ResolvedRenderProfile(name, definition, profileFlags, flags);
    }

    /// <summary>構成 1 つを読む（読めなければ警告を足して null）。</summary>
    /// <param name="entry">配列の要素。</param>
    /// <param name="index">配列の中の位置（警告用）。</param>
    /// <param name="known">先に読んだ構成（重複の検出用）。</param>
    /// <param name="warnings">警告の足し先。</param>
    /// <returns>構成。</returns>
    private static RenderProfileDefinition? ReadProfile(
        JsonElement entry, int index, IReadOnlyList<RenderProfileDefinition> known, List<string> warnings)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty(NameKey, out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            warnings.Add($"{index} 番目の構成に名前がありません（読み飛ばしました）");
            return null;
        }
        var name = (nameElement.GetString() ?? string.Empty).Trim();
        if (name.Length == 0 || known.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"構成の名前 '{name}' が空か重複しています（読み飛ばしました）");
            return null;
        }

        // 旗（書かれていない旗は既定＝用意する。読めない旗はその旗だけ捨てる）
        var flags = RenderProfileFlagValues.Full;
        if (entry.TryGetProperty(FlagsKey, out var flagObject))
        {
            if (flagObject.ValueKind == JsonValueKind.Object)
            {
                flags = OverlayFlags(flags, flagObject, $"構成 {name}", warnings);
            }
            else
            {
                warnings.Add($"構成 {name}: {FlagsKey} がオブジェクトではありません");
            }
        }
        return new RenderProfileDefinition(name, ReadText(entry, LabelKey), ReadText(entry, DescriptionKey), flags);
    }

    /// <summary>旗のオブジェクトを重ねる（flags.rs の overlay_flags と同じ。読めない旗は警告して捨てる）。</summary>
    /// <param name="flags">元の旗。</param>
    /// <param name="flagObject">旗のオブジェクト。</param>
    /// <param name="context">警告の頭に付ける出どころ。</param>
    /// <param name="warnings">警告の足し先。</param>
    /// <returns>重ねた旗。</returns>
    private static RenderProfileFlagValues OverlayFlags(
        RenderProfileFlagValues flags, JsonElement flagObject, string context, List<string> warnings)
    {
        foreach (var property in flagObject.EnumerateObject())
        {
            if (RenderProfileFlagCatalog.IsToggleKey(property.Name))
            {
                if (RenderProfileFlagCatalog.ReadToggle(property.Value) is bool value)
                {
                    flags = flags.WithToggle(property.Name, value);
                }
                else
                {
                    warnings.Add($"{context}: {property.Name} は true / false で指定してください");
                }
            }
            else if (property.Name == RenderProfileFlagCatalog.MemoryHintKey)
            {
                if (RenderProfileFlagCatalog.ReadMemoryHint(property.Value) is { } hint)
                {
                    flags = flags.WithMemoryHint(hint);
                }
                else
                {
                    warnings.Add($"{context}: {property.Name} の値は知りません（" +
                                 $"{RenderProfileFlagCatalog.MemoryHintPerformance} / {RenderProfileFlagCatalog.MemoryHintMemoryUsage}）");
                }
            }
            else
            {
                warnings.Add($"{context}: 知らない旗 {property.Name} を読み飛ばしました");
            }
        }
        return flags;
    }

    /// <summary>文字列の欄を読む（無い・文字列でなければ空）。</summary>
    /// <param name="entry">オブジェクト。</param>
    /// <param name="key">欄。</param>
    /// <returns>文字列。</returns>
    private static string ReadText(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>埋め込みリソースから一覧を読む（無い・構成が 1 つも読めなければ組み込みの既定の一覧）。</summary>
    /// <returns>一覧。</returns>
    private static RenderProfileCatalogData LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return Fallback([$"埋め込みの {ResourceName} が見つかりません（エディタのビルドに runtime/config/render_profiles.json が入っていない）"]);
        }
        using var reader = new StreamReader(stream);
        var parsed = Parse(reader.ReadToEnd());
        return parsed.Profiles.Count == 0 ? Fallback(parsed.Warnings) : parsed;
    }
}
