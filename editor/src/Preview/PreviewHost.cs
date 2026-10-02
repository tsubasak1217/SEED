// ============================================================
//  PreviewHost.cs — 差し込み先の案内（スクリプト 1 つ分・差し込み先 1 つ分・解決した結果）
//
//  【役割】
//  screen_preview_hosts.json（PreviewHostCatalogFormat の形）を、前後の空白・空文字を整えた
//  変わらない値にしたもの。PreviewHostCatalog が作り、インスペクタ（InspectorPanel.Preview.cs）が使う。
//    - PreviewHost         … 案内を出すスクリプト 1 つ（ScreenStack・ModalHost・PopupPlane…）
//    - PreviewHostSlot     … 差し込み先の定義 1 つ（「根の画面」「画面」「ダイアログ」…）
//    - ResolvedPreviewSlot … 定義にシーンの欄の値（[SerializeField]）を当てて決めた、実際に送る形
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Preview;

/// <summary>
/// 差し込み先の案内を出すスクリプト 1 つ分。
/// </summary>
/// <param name="Script">表に書かれたスクリプトのクラス名（照合は <see cref="ShortName"/> で行う）。</param>
/// <param name="ShortName">照合に使う短いクラス名（名前空間・パス・拡張子を除いたもの）。</param>
/// <param name="Label">案内の見出し（例「画面のスタック」）。</param>
/// <param name="Slots">差し込み先の並び（インスペクタの案内の行の順）。</param>
public sealed record PreviewHost(
    string Script,
    string ShortName,
    string Label,
    IReadOnlyList<PreviewHostSlot> Slots);

/// <summary>
/// 差し込み先の定義 1 つ分（表の 1 行。欄の値はまだ当てていない）。
/// 欄の名前（*Field）は無ければ null、既定値は無ければ null（文字列の既定は空文字）。
/// </summary>
public sealed record PreviewHostSlot
{
    /// <summary>行の見出し。</summary>
    public string Label { get; init; } = "";

    /// <summary>行の説明（ツールチップ）。</summary>
    public string Description { get; init; } = "";

    /// <summary>差し込む子のパスを持つ欄の名前（無ければ null）。</summary>
    public string? UnderField { get; init; }

    /// <summary>欄の値が無いときの差し込む子のパス（空 = 親の直下）。</summary>
    public string UnderDefault { get; init; } = "";

    /// <summary>中身のプレハブを持つ欄の名前（無ければ null）。</summary>
    public string? PrefabField { get; init; }

    /// <summary>欄の値が無いときの中身のプレハブ（無ければ null）。</summary>
    public string? PrefabDefault { get; init; }

    /// <summary>枠のプレハブを持つ欄の名前（無ければ null）。</summary>
    public string? FrameField { get; init; }

    /// <summary>欄の値が無いときの枠のプレハブ（無ければ null = 枠なし）。</summary>
    public string? FrameDefault { get; init; }

    /// <summary>枠の中で中身を入れる子のパス（空 = 枠の直下）。</summary>
    public string FrameBody { get; init; } = "";

    /// <summary>「安全領域の中に入れるか」を持つ欄の名前（無ければ null）。</summary>
    public string? SafeAreaField { get; init; }

    /// <summary>欄の値が無い・読めないときの「安全領域の中に入れるか」（無ければ null = 入れる）。</summary>
    public bool? SafeAreaDefault { get; init; }

    /// <summary>根のレイヤーの底上げの固定値（無ければ null）。</summary>
    public int? LayerBias { get; init; }

    /// <summary>底上げを持つ欄の名前（無ければ null）。</summary>
    public string? LayerBiasField { get; init; }

    /// <summary>欄の値が 0・無し・読めないときの底上げ（無ければ null）。</summary>
    public int? LayerBiasDefault { get; init; }

    /// <summary>「選ぶ...」を出すか。</summary>
    public bool Pick { get; init; }
}

/// <summary>
/// 差し込み先の定義にシーンの欄の値を当てて決めた、実際に送る形。
/// </summary>
/// <param name="Label">行の見出し。</param>
/// <param name="Description">行の説明（ツールチップ）。</param>
/// <param name="Under">差し込む子のパス（空 = 親の直下）。</param>
/// <param name="Prefab">既定の中身のプレハブ（無ければ null = 既定のボタンを出さない）。</param>
/// <param name="Frame">枠のプレハブ（null = 枠なし）。</param>
/// <param name="FrameBody">枠の中で中身を入れる子のパス（空 = 枠の直下。枠なしなら空）。</param>
/// <param name="LayerBias">根のレイヤーの底上げ（0 = 付けない）。</param>
/// <param name="CanPick">「選ぶ...」を出すか。</param>
public sealed record ResolvedPreviewSlot(
    string Label,
    string Description,
    string Under,
    string? Prefab,
    string? Frame,
    string FrameBody,
    int LayerBias,
    bool CanPick);
