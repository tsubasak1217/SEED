// ============================================================
//  PreviewHostCatalogFormat.cs — 差し込み先の案内の表（screen_preview_hosts.json）の読み込み用の形
//
//  【役割】
//  editor/config/screen_preview_hosts.json をそのまま写した入れ物（デシリアライズ専用）。
//  アプリからは PreviewHostCatalog（読み込み・照合・解決）を使う。正典は docs/editor_screen_preview.md §5。
//
//  【欄の null】
//  書かれていない欄は null のまま受ける（null と空文字・0・false を区別するため）。
//  既定の当て方は PreviewHostCatalog.ResolveSlot が決める。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SEEDEditor.Preview;

/// <summary>
/// screen_preview_hosts.json の全体。
/// </summary>
public sealed class PreviewHostCatalogFile
{
    /// <summary>書式の版（このコードが理解するのは <see cref="PreviewHostCatalog.SupportedFormatVersion"/>）。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>ヒエラルキーの右クリックから出すプレビューの底上げ（無ければ組み込みの既定）。</summary>
    [JsonPropertyName("default_layer_bias")]
    public int? DefaultLayerBias { get; set; }

    /// <summary>差し込み先の案内を出すスクリプトの一覧。</summary>
    [JsonPropertyName("hosts")]
    public List<PreviewHostFile>? Hosts { get; set; }
}

/// <summary>
/// 案内を出すスクリプト 1 つ分。
/// </summary>
public sealed class PreviewHostFile
{
    /// <summary>スクリプトのクラス名（名前空間・パス・拡張子は見ない。大小文字も見ない）。</summary>
    [JsonPropertyName("script")]
    public string? Script { get; set; }

    /// <summary>案内の見出し（例「画面のスタック」）。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>差し込み先（1 行ずつの案内）。</summary>
    [JsonPropertyName("slots")]
    public List<PreviewSlotFile>? Slots { get; set; }
}

/// <summary>
/// 差し込み先 1 つ分（インスペクタの案内の 1 行）。
/// </summary>
public sealed class PreviewSlotFile
{
    /// <summary>行の見出し（例「根の画面」）。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>行の説明（ツールチップ）。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>差し込む子のパスを持つ欄の名前（[SerializeField] の名前）。</summary>
    [JsonPropertyName("under_field")]
    public string? UnderField { get; set; }

    /// <summary>欄の値が無いときの差し込む子のパス（空 = 親の直下）。</summary>
    [JsonPropertyName("under_default")]
    public string? UnderDefault { get; set; }

    /// <summary>中身のプレハブを持つ欄の名前。</summary>
    [JsonPropertyName("prefab_field")]
    public string? PrefabField { get; set; }

    /// <summary>欄の値が無いときの中身のプレハブ（無ければボタンを出さない）。</summary>
    [JsonPropertyName("prefab_default")]
    public string? PrefabDefault { get; set; }

    /// <summary>枠のプレハブを持つ欄の名前。</summary>
    [JsonPropertyName("frame_field")]
    public string? FrameField { get; set; }

    /// <summary>欄の値が無いときの枠のプレハブ（無ければ枠なし）。</summary>
    [JsonPropertyName("frame_default")]
    public string? FrameDefault { get; set; }

    /// <summary>枠の中で中身を入れる子のパス（空 = 枠の直下）。</summary>
    [JsonPropertyName("frame_body")]
    public string? FrameBody { get; set; }

    /// <summary>「安全領域の中に入れるか」を持つ欄の名前（false なら枠の直下へ）。</summary>
    [JsonPropertyName("safe_area_field")]
    public string? SafeAreaField { get; set; }

    /// <summary>欄の値が無い・読めないときの「安全領域の中に入れるか」。</summary>
    [JsonPropertyName("safe_area_default")]
    public bool? SafeAreaDefault { get; set; }

    /// <summary>根のレイヤーの底上げ（固定値）。</summary>
    [JsonPropertyName("layer_bias")]
    public int? LayerBias { get; set; }

    /// <summary>底上げを持つ欄の名前（値が正ならそれを使う）。</summary>
    [JsonPropertyName("layer_bias_field")]
    public string? LayerBiasField { get; set; }

    /// <summary>欄の値が 0・無し・読めないときの底上げ。</summary>
    [JsonPropertyName("layer_bias_default")]
    public int? LayerBiasDefault { get; set; }

    /// <summary>「選ぶ...」（窓でプレハブを選ぶ）を出すか。</summary>
    [JsonPropertyName("pick")]
    public bool? Pick { get; set; }
}
