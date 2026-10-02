// ============================================================
//  PreviewInsertTarget.cs — プレビューの差し込み先
//
//  【役割】
//  「どのノードの下へ、どんな形（枠の有無・底上げ）でプレビューを差し込むか」を運ぶ値。
//  ヒエラルキーの右クリック（右クリックしたノードの直下・枠なし）と、
//  インスペクタの差し込み先の案内（ScreenStack・ModalHost・PopupPlane。PreviewHostCatalog の解決結果）の
//  2 か所で作られ、MainWindow.ScreenPreview.cs が送る直前に親を引き直してから PREVIEW_PREFAB にする。
//
//  【差し込み先の見失い】
//  親は DFS 番号で送るが、番号は木の編集でずれる（窓を開いている間など）。そこで作った時点の
//  安定キー（ルートからの名前パス。ActorNode.StableKey）も持ち、送る直前にヒエラルキーで引き直す
//  （HierarchyPanel.TryResolvePreviewParent。テンプレートアクタの TryRefreshTemplateActorTarget と同じ規則）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

namespace SEEDEditor.Preview;

/// <summary>
/// 差し込み先の親をいまの木で引き直した結果（HierarchyPanel.TryResolvePreviewParent）。
/// </summary>
/// <param name="ParentDfs">いまの木での親の DFS 番号（見失ったら null）。</param>
/// <param name="Reason">見失った理由（利用者向けの文。引き直せたら null）。</param>
public readonly record struct PreviewParentResolution(int? ParentDfs, string? Reason);

/// <summary>
/// プレビューの差し込み先。
/// </summary>
public sealed record PreviewInsertTarget
{
    /// <summary>窓の見出しの頭。</summary>
    public const string LabelPrefix = "差し込み先: ";

    /// <summary>見出しの「親 / 差し込む子」の区切り。</summary>
    public const string LabelPathSeparator = " / ";

    /// <summary>枠つきで差し込むときに見出しの末尾へ付ける印。</summary>
    public const string LabelFramedSuffix = "（枠つき）";

    /// <summary>世界線（ヒエラルキーが表示中のタブ。シーンは 0）。</summary>
    public uint WorldLine { get; init; }

    /// <summary>親の DFS 番号（作った時点。送る直前に引き直す）。</summary>
    public int ParentDfs { get; init; }

    /// <summary>親の名前（見失ったときの文言と、安定キーが無いときの照合に使う）。</summary>
    public string ParentName { get; init; } = "";

    /// <summary>親の安定キー（ルートからの名前パス。送る直前に引き直すため）。</summary>
    public string ParentStableKey { get; init; } = "";

    /// <summary>親の下で差し込む子のパス（'/' 区切りの名前。空 = 親の直下）。</summary>
    public string Under { get; init; } = "";

    /// <summary>枠のプレハブ（null = 枠なし）。</summary>
    public string? Frame { get; init; }

    /// <summary>枠の中で中身を入れる子のパス（空 = 枠の直下）。</summary>
    public string FrameBody { get; init; } = "";

    /// <summary>根のレイヤーの底上げ（0 = 付けない）。</summary>
    public int LayerBias { get; init; }

    /// <summary>中身のプレハブ（null = 窓で選ぶ）。</summary>
    public string? Prefab { get; init; }

    /// <summary>窓の見出し（例「差し込み先: RootStack / Screens（枠つき）」）。</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// 中身を決めて、ランタイムへ送る命令の中身にする。
    /// </summary>
    /// <param name="prefab">中身のプレハブ（窓で選んだもの、または <see cref="Prefab"/>）。</param>
    /// <returns>命令の中身。</returns>
    public ScreenPreviewRequest ToRequest(string prefab) => new(prefab, Under, Frame, FrameBody, LayerBias);

    /// <summary>
    /// 窓の見出しを作る。
    /// </summary>
    /// <param name="parentName">親の名前。</param>
    /// <param name="under">差し込む子のパス（空なら親の直下なので出さない）。</param>
    /// <param name="framed">枠つきで差し込むか。</param>
    /// <returns>例「差し込み先: RootStack / Screens（枠つき）」「差し込み先: Canvas」。</returns>
    public static string DescribeLabel(string parentName, string under, bool framed)
    {
        var label = LabelPrefix + parentName;
        if (!string.IsNullOrEmpty(under)) label += LabelPathSeparator + under;
        if (framed) label += LabelFramedSuffix;
        return label;
    }
}
