// ============================================================
//  HierarchyPanel.TemplateActors.cs — 「アクタを追加」→「テンプレートアクタ...」
//
//  【役割】（HierarchyPanel の部分クラス）
//  右クリックの「アクタを追加」に「テンプレートアクタ...」を足し、押されたら
//  「どこへ入れるか」（TemplateActorTarget）を作って MainWindow へ知らせる。
//  窓を開くのは MainWindow（MainWindow.TemplateActors.cs）で、ここは窓を知らない。
//
//  【追加先】
//  - 空白の右クリック → ルート（2D の部品はランタイムが Canvas の規則で置く）
//  - ノードの右クリック → そのノードの子（2D/3D の規則は既存の「子として追加」と同じ）
//  窓が開いている間に DFS 番号がずれても追えるよう、安定キー（名前パス）も渡し、
//  追加の直前に TryRefreshTemplateActorTarget で引き直す。
// ============================================================

using System;
using System.Linq;
using System.Windows.Controls;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Panels;

public partial class HierarchyPanel
{
    /// <summary>「アクタを追加」の中の項目名（窓が開くので「...」を付ける）。</summary>
    private const string TemplateActorMenuHeader = "テンプレートアクタ...";

    /// <summary>
    /// 「テンプレートアクタ...」が押された（MainWindow が受けて窓を開く）。
    /// 引数は追加先（右クリックした場所）。
    /// </summary>
    public event Action<TemplateActorTarget>? TemplateActorPickerRequested;

    /// <summary>
    /// 「アクタを追加」の中に置く「テンプレートアクタ...」の項目を作る。
    /// </summary>
    /// <param name="parent">追加先の親（空白の右クリックなら null = ルート）。</param>
    /// <returns>メニュー項目。</returns>
    private MenuItem BuildTemplateActorMenuItem(ActorNode? parent)
    {
        var item = new MenuItem
        {
            Header  = TemplateActorMenuHeader,
            ToolTip = "UI 部品などの、コンポーネントとスクリプトが付いたアクタを選んで追加します（プレハブにはしません）",
        };
        item.Click += (_, _) => TemplateActorPickerRequested?.Invoke(CreateTemplateActorTarget(parent));
        return item;
    }

    /// <summary>
    /// ルート（空白の右クリック・シーンビューの右クリック）へ入れる追加先を作る。
    /// </summary>
    /// <returns>いま表示中のタブのルートを指す追加先。</returns>
    public TemplateActorTarget CreateRootTemplateActorTarget() => CreateTemplateActorTarget(null);

    /// <summary>
    /// 追加先を作る。
    /// </summary>
    /// <param name="parent">親のノード（null ならルート）。</param>
    /// <returns>追加先。</returns>
    private TemplateActorTarget CreateTemplateActorTarget(ActorNode? parent) =>
        parent is null
            ? new TemplateActorTarget
            {
                WorldLine       = _activeWorldLine,
                IsCanvasEditTab = _isSceneCanvasEditMode,
            }
            : new TemplateActorTarget
            {
                WorldLine       = _activeWorldLine,
                ParentDfs       = parent.Id,
                ParentName      = parent.Name,
                ParentStableKey = parent.StableKey,
                ParentIs2D      = parent.Is2D,
                ParentHasCanvas = parent.HasCanvas,
                IsCanvasEditTab = _isSceneCanvasEditMode,
            };

    /// <summary>
    /// 追加先をいまのツリーで引き直す（テンプレートアクタの窓が追加の直前に呼ぶ）。
    /// DFS 番号はツリーの編集でずれるので、右クリックの時点の安定キーでノードを探し直す。
    /// </summary>
    /// <param name="target">右クリックの時点の追加先。</param>
    /// <returns>引き直した追加先、または見失った理由。</returns>
    public TemplateActorTargetRefresh TryRefreshTemplateActorTarget(TemplateActorTarget target)
    {
        // 別のタブ（アクタ編集・キャンバス編集）へ切り替わっていたら、同じ名前の別の木を掴まない
        if (target.WorldLine != _activeWorldLine)
            return new(null, "追加先のタブが切り替わりました。ヒエラルキーで右クリックし直してください");

        if (target.IsRoot) return new(CreateTemplateActorTarget(null), null);

        // 安定キーが振られていない（ツリーの反映前に右クリックした等）ときは、番号と名前の一致で代える
        var node = string.IsNullOrEmpty(target.ParentStableKey)
            ? GetAllNodes(_roots).FirstOrDefault(n => n.Id == target.ParentDfs && n.Name == target.ParentName)
            : GetAllNodes(_roots).FirstOrDefault(n => n.StableKey == target.ParentStableKey);
        if (node is null)
            return new(null, $"追加先の「{target.ParentName}」が見つかりません（名前の変更・移動・削除）。" +
                             "ヒエラルキーで右クリックし直してください");
        return new(CreateTemplateActorTarget(node), null);
    }
}
