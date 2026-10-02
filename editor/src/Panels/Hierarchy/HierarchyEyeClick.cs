namespace SEEDEditor.Panels.Hierarchy;

/// <summary>
/// ヒエラルキーの行の目アイコン（表示 / 非表示の切り替え）の判定に要る、行のノードの値（WPF 非依存）。
/// 実体は <c>HierarchyPanel.ActorNode</c>（本番）と、HierarchySyncTests のフェイクノード。
/// </summary>
public interface IHierarchyEyeTarget
{
    /// <summary>今のアクタの DFS 番号（木の増減でずれる）。</summary>
    int Id { get; }

    /// <summary>アクタ自身の表示フラグ（祖先を考えない生の値）。押したときはこれを反転して送る。</summary>
    bool SelfVisible { get; }

    /// <summary>画面プレビュー（保存されない表示用のアクタ）の行か。</summary>
    bool IsPreview { get; }
}

/// <summary>
/// ヒエラルキーの行の目アイコンを押したときに送る値を決める（WPF 非依存の純粋な判定）。
///
/// <para>
/// 【押したときの行のノードから読む（2026-10-03 の 2 回目のレビュー #12）】
/// 差分更新（<see cref="HierarchyTreeSync"/>）は安定キーが同じ行を使い回し、行（TreeViewItem）の Tag だけを新しいノードへ差し替える。
/// 見出し（目アイコンを含む）は見た目が変わらなければ作り直さない（作り直しはアイコンの Visual を作るので、Play 中の生成・破棄で重い）。
/// そのため、見出しを作ったときのノードをクリックのラムダで握ると、手前の増減（削除・貼り付け・Undo・プレビューの出し入れ）の後に
/// 古い DFS 番号を送り、今その番号にいる **別のアクタ** の表示を切り替えて保存させていた。
/// 呼び出し側は必ず「押した時点で行が束ねているノード」を渡す（HierarchyPanel は見出しの論理上の親 = 行の Tag から読む）。
/// </para>
/// </summary>
public static class HierarchyEyeClick
{
    /// <summary>
    /// 押した行のノードから、送る表示切替（対象の DFS 番号と、設定する表示フラグ）を決める。
    /// </summary>
    /// <param name="rowNode">押した時点で行が束ねているノード（行から外れた見出しなら null）。</param>
    /// <returns>
    /// 送る値。送らないときは null:
    /// 行から外れた見出し（名前の変更の入力中に差し替わった古い見出しなど）・画面プレビューの行
    /// （保存されない表示用のアクタ。表示を切り替える編集として積ませない）。
    /// 表示フラグはアクタ自身のフラグの反転（実効表示を反転すると、祖先が非表示のとき自分のフラグを戻せなくなる）。
    /// </returns>
    public static (int Id, bool Visible)? Decide(IHierarchyEyeTarget? rowNode)
    {
        if (rowNode is null) return null;
        if (rowNode.IsPreview) return null;
        return (rowNode.Id, !rowNode.SelfVisible);
    }
}
