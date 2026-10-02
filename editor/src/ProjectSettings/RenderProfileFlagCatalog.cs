// ============================================================
//  RenderProfileFlagCatalog.cs — 描画の構成（render.profile）の旗の表（キー・表示名・説明・値の読み方）
//
//  【役割】
//  project_settings.json の "render" 節で上書きできる旗を、エディタが扱う表として持つ。
//    真偽の旗   … scene_3d / deferred / shadows / gi / bindless / ray_tracing / post / picking
//    方針の旗   … memory_hint（"performance" / "memory_usage"）
//  キー名・取り得る値・読み方の正典はランタイムの runtime/src/engine/core/renderer/render_profile/flags.rs
//  （KNOWN_FLAG_KEYS・apply_flag・read_bool・MemoryHint::parse）。ここはその写しなので、ずれたら
//  editor/tests/ProjectSystemTests（flags.rs を読んで突き合わせる）が落ちる。
//
//  【表に持つもの】
//  画面に出す表示名・「無効にするとどうなるか」の説明・「3D のシーンを描かないと個々の旗によらず止まるか」
//  （flags.rs の allocates_* / requests_ray_tracing / allows_deferred の規則）。旗を足すときは表に 1 行足すだけでよい
//  （画面・読み書き・実効の判断はこの表を回す）。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>真偽の旗 1 つの表の行。</summary>
/// <param name="Key">JSON のキー（flags.rs の KEY_*）。</param>
/// <param name="Label">画面に出す表示名。</param>
/// <param name="DisabledDescription">無効（false）にするとどうなるか（画面の説明）。</param>
/// <param name="RequiresScene3D">
/// 3D のシーンを描かない（scene_3d = false）と、この旗が有効でも止まるか
/// （flags.rs の allocates_shadows / allocates_gi / allocates_bindless / requests_ray_tracing / allows_deferred）。
/// </param>
public sealed record RenderProfileToggleFlag(string Key, string Label, string DisabledDescription, bool RequiresScene3D);

/// <summary>memory_hint の選択肢 1 つ。</summary>
/// <param name="Value">JSON に書く値（flags.rs の MEMORY_HINT_*）。</param>
/// <param name="Label">画面に出す表示名。</param>
/// <param name="Description">説明。</param>
public sealed record RenderProfileMemoryHintChoice(string Value, string Label, string Description);

/// <summary>描画の構成の旗の表と、旗の値の読み方（flags.rs の写し）。</summary>
public static class RenderProfileFlagCatalog
{
    // ── キー（flags.rs の KEY_* と一致させる）────────────────────

    /// <summary>3D のシーンを描くかのキー（flags.rs の KEY_SCENE_3D）。</summary>
    public const string Scene3DKey = "scene_3d";

    /// <summary>デファードの可否のキー（flags.rs の KEY_DEFERRED）。</summary>
    public const string DeferredKey = "deferred";

    /// <summary>シャドウマップの確保のキー（flags.rs の KEY_SHADOWS）。</summary>
    public const string ShadowsKey = "shadows";

    /// <summary>GI のアトラスの確保のキー（flags.rs の KEY_GI）。</summary>
    public const string GiKey = "gi";

    /// <summary>bindless の確保のキー（flags.rs の KEY_BINDLESS）。</summary>
    public const string BindlessKey = "bindless";

    /// <summary>レイトレーシングの機能の要求のキー（flags.rs の KEY_RAY_TRACING）。</summary>
    public const string RayTracingKey = "ray_tracing";

    /// <summary>後処理の可否のキー（flags.rs の KEY_POST）。</summary>
    public const string PostKey = "post";

    /// <summary>Play 中のピッキングの ID バッファの確保のキー（flags.rs の KEY_PICKING）。</summary>
    public const string PickingKey = "picking";

    /// <summary>GPU メモリの確保の方針のキー（flags.rs の KEY_MEMORY_HINT）。</summary>
    public const string MemoryHintKey = "memory_hint";

    // ── memory_hint の値（flags.rs の MEMORY_HINT_* と一致させる）──────

    /// <summary>memory_hint の値: 大きな塊でまとめて確保する（wgpu の既定）。</summary>
    public const string MemoryHintPerformance = "performance";

    /// <summary>memory_hint の値: 小さな塊で確保する（メモリを節約する）。</summary>
    public const string MemoryHintMemoryUsage = "memory_usage";

    /// <summary>memory_hint の既定（flags.rs の MemoryHint の #[default]）。</summary>
    public const string DefaultMemoryHint = MemoryHintPerformance;

    // ── JSON の真偽の文字列（flags.rs の read_bool が読む綴り）──────────

    /// <summary>文字列で書いた真（起動オプションの キー=値 と同じ綴り。大文字小文字・前後の空白は問わない）。</summary>
    private const string TrueText = "true";

    /// <summary>文字列で書いた偽。</summary>
    private const string FalseText = "false";

    // ── 表 ───────────────────────────────────────────────────

    /// <summary>
    /// 真偽の旗の表（flags.rs の KNOWN_FLAG_KEYS の memory_hint 以外と同じ順。画面・書き出しもこの順）。
    /// 既定はすべて「用意する」（true＝full と同じ）。旗は「止める」向きにだけ効く。
    /// </summary>
    public static readonly IReadOnlyList<RenderProfileToggleFlag> ToggleFlags =
    [
        new(Scene3DKey, "3D のシーン",
            "3D のシーン（モデル・地形・水・天球・SEED.Draw3D）を描かない（あれば起動ログに 1 回警告）。" +
            "影・GI・bindless・レイトレーシング・G-Buffer も個々の旗によらず止まる。キャンバス・文字・図形・スプライトは描く",
            RequiresScene3D: false),
        new(DeferredKey, "G-Buffer（デファード）",
            "前方描画にする（G-Buffer を確保しない。AO・反射・SSGI も止まる）",
            RequiresScene3D: true),
        new(ShadowsKey, "シャドウマップ",
            "シャドウマップを 1x1 の置き場だけにし、影を描かない",
            RequiresScene3D: true),
        new(GiKey, "GI（DDGI）",
            "GI のアトラスを 1x1 の置き場だけにし、GI は平坦な環境光にする",
            RequiresScene3D: true),
        new(BindlessKey, "bindless",
            "bindless の機能をデバイスへ求めず、テクスチャ配列とメガバッファ（224 MiB）を作らない",
            RequiresScene3D: true),
        new(RayTracingKey, "レイトレーシング",
            "レイトレーシングの機能（RT 影・RT 反射・DDGI の更新）をデバイスへ求めない",
            RequiresScene3D: true),
        new(PostKey, "後処理",
            "ブルーム・ビネット・FXAA を止める（トーンマップは残る）",
            RequiresScene3D: false),
        new(PickingKey, "Play のピッキング",
            "エディタに接続していない Play（単体の起動・パッケージ・Android）で、ID パスを描くとき" +
            "（図鑑のサムネイルの撮影など）もピッキングの ID バッファを作らない",
            RequiresScene3D: false),
    ];

    /// <summary>memory_hint の選択肢（既定が先頭）。</summary>
    public static readonly IReadOnlyList<RenderProfileMemoryHintChoice> MemoryHintChoices =
    [
        new(MemoryHintPerformance, "performance（大きな塊）",
            "大きな塊でまとめて確保する（wgpu の既定。full の既定）"),
        new(MemoryHintMemoryUsage, "memory_usage（小さな塊）",
            "小さな塊で確保する（使い切らない塊を持たない。2D/UI だけのアプリ向け）"),
    ];

    /// <summary>memory_hint の表示名（画面の見出し）。</summary>
    public const string MemoryHintLabel = "GPU メモリの確保";

    // ── 引き方 ───────────────────────────────────────────────

    /// <summary>真偽の旗のキーか。</summary>
    /// <param name="key">キー。</param>
    /// <returns>表にある真偽の旗なら true。</returns>
    public static bool IsToggleKey(string key) => FindToggle(key) is not null;

    /// <summary>真偽の旗の表の行を引く（無ければ null。キーは大文字小文字を区別する＝ランタイムと同じ）。</summary>
    /// <param name="key">キー。</param>
    /// <returns>表の行。</returns>
    public static RenderProfileToggleFlag? FindToggle(string key) =>
        ToggleFlags.FirstOrDefault(flag => string.Equals(flag.Key, key, StringComparison.Ordinal));

    /// <summary>memory_hint の選択肢を引く（無ければ null）。</summary>
    /// <param name="value">正規化した値。</param>
    /// <returns>選択肢。</returns>
    public static RenderProfileMemoryHintChoice? FindMemoryHint(string? value) =>
        MemoryHintChoices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal));

    // ── 値の読み方（flags.rs と同じ規則）────────────────────────

    /// <summary>
    /// 真偽の旗の値を読む（JSON の true / false と、文字列の "true" / "false"。flags.rs の read_bool と同じ）。
    /// 読めなければ null（ランタイムはその旗だけ捨てて警告する）。
    /// </summary>
    /// <param name="value">JSON の値。</param>
    /// <returns>読めた真偽。</returns>
    public static bool? ReadToggle(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.String => NormalizeWord(value.GetString()) switch
        {
            TrueText  => true,
            FalseText => false,
            _         => null,
        },
        _ => null,
    };

    /// <summary>
    /// memory_hint の値を読む（文字列だけ。前後の空白・大文字小文字は区別しない。flags.rs の MemoryHint::parse と同じ）。
    /// 読めなければ null。
    /// </summary>
    /// <param name="value">JSON の値。</param>
    /// <returns>正規化した値（<see cref="MemoryHintPerformance"/> / <see cref="MemoryHintMemoryUsage"/>）。</returns>
    public static string? ReadMemoryHint(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? NormalizeMemoryHint(value.GetString()) : null;

    /// <summary>memory_hint の文字列を正規化する（知っている値ならその綴り、知らなければ null）。</summary>
    /// <param name="text">入力。</param>
    /// <returns>正規化した値。</returns>
    public static string? NormalizeMemoryHint(string? text)
    {
        var word = NormalizeWord(text);
        return FindMemoryHint(word)?.Value;
    }

    /// <summary>前後の空白を落として小文字にする（ランタイムの trim + to_ascii_lowercase と同じ扱い）。</summary>
    /// <param name="text">入力。</param>
    /// <returns>正規化した文字列（null は空）。</returns>
    private static string NormalizeWord(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant();
}
