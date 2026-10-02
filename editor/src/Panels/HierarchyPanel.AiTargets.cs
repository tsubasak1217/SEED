// ============================================================
//  HierarchyPanel.AiTargets.cs — AI ツール（MCP）の宛先を、右クリックと同じ形で作る（HierarchyPanel の部分クラス）
//
//  【役割】
//  MCP の seed_preview / seed_template_actor は、宛先を DFS 番号（または名前パスから引いた DFS 番号）で受け取る。
//  右クリックのメニューと同じ「差し込み先・追加先」の値（世界線・名前・安定キー・2D か・Canvas を持つか）を
//  ここで作り、送る直前の引き直し（TryResolvePreviewParent / TryRefreshTemplateActorTarget）まで
//  UI とまったく同じ道筋を通るようにする。作り方そのものは右クリックの private 関数をそのまま使う。
// ============================================================

using SEEDEditor.Preview;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Panels;

public partial class HierarchyPanel
{
    /// <summary>
    /// DFS 番号のノードが表示中のタブの木にあるか（AI ツールが宛先を確かめる）。
    /// </summary>
    /// <param name="dfs">DFS 番号。</param>
    /// <returns>あれば true。</returns>
    public bool ContainsNode(int dfs) => FindNode(_roots, dfs) is not null;

    /// <summary>
    /// DFS 番号のノードの直下へプレビューを入れる差し込み先を作る
    /// （右クリック「プレハブをプレビュー」と同じ: 枠なし・底上げは表の default_layer_bias）。
    /// </summary>
    /// <param name="dfs">親の DFS 番号（表示中のタブの木）。</param>
    /// <param name="prefab">中身のプレハブ。</param>
    /// <returns>差し込み先。ノードが木に無ければ null。</returns>
    public PreviewInsertTarget? TryCreatePreviewTargetFor(int dfs, string prefab) =>
        FindNode(_roots, dfs) is { } node ? CreatePreviewTarget(node, prefab) : null;

    /// <summary>
    /// DFS 番号のノードの子としてテンプレートアクタを入れる追加先を作る（右クリック「テンプレートアクタ...」と同じ）。
    /// </summary>
    /// <param name="dfs">親の DFS 番号（表示中のタブの木）。</param>
    /// <returns>追加先。ノードが木に無ければ null。</returns>
    public TemplateActorTarget? TryCreateTemplateActorTargetFor(int dfs) =>
        FindNode(_roots, dfs) is { } node ? CreateTemplateActorTarget(node) : null;
}
