namespace SEEDEditor.Panels.Hierarchy;

/// <summary>
/// ヒエラルキーの差分同期の後、選択をどう直すかの種類。
/// </summary>
public enum SelectionRestoreKind
{
    /// <summary>何も選んでいなかった（何もしない）。</summary>
    Nothing,

    /// <summary>同じアクターが同じ DFS ID のまま居る（行の選択だけ合わせ直す）。</summary>
    Keep,

    /// <summary>同じアクター（安定キーが一致）が別の DFS ID へ移った（ID を差し替え、インスペクタにも知らせる）。</summary>
    Moved,

    /// <summary>選んでいたアクターが消えた（選択を外す。古い ID で別のアクターを掴まない）。</summary>
    Lost,
}

/// <summary>
/// 差分同期の後の選択の直し方（WPF 非依存の判定。<c>HierarchyPanel.RestoreSelectionAfterSync</c> が使い、
/// HierarchySyncTests が単体で確かめる）。
///
/// <para>
/// DFS ID は「シーン内の何番目か」でしかないので、手前のアクターが増減すると同じ ID が別のアクターを指す。
/// 同期の前に控えた安定キー（ルートからの名前の道筋＋同名兄弟の出現番号）で引き直し、ID が変わっていたら
/// <see cref="SelectionRestoreKind.Moved"/> として **インスペクタにも新しい ID を知らせる**
/// （2026-10-03。docs/reviews/2026-10-02_code_review.md の #1。知らせないとインスペクタが古い ID を持ったまま、
/// 次の値の編集がその ID に今いる別のアクターへ当たる）。
/// </para>
/// </summary>
/// <param name="Kind">直し方の種類。</param>
/// <param name="NewId">直した後の選択の DFS ID（<see cref="SelectionRestoreKind.Lost"/> と <see cref="SelectionRestoreKind.Nothing"/> は -1）。</param>
public readonly record struct SelectionRestorePlan(SelectionRestoreKind Kind, int NewId)
{
    /// <summary>未選択を表す DFS ID。</summary>
    public const int NoSelection = -1;

    /// <summary>
    /// 直し方を決める。
    /// </summary>
    /// <param name="selectedId">同期の前に選んでいた DFS ID（未選択は負）。</param>
    /// <param name="selectedKeyBeforeSync">同期の前に選んでいた行の安定キー（取れなければ空文字）。</param>
    /// <param name="nodeIdForKey">その安定キーを同期後の木で引いた DFS ID（見つからなければ null）。</param>
    public static SelectionRestorePlan Decide(int selectedId, string selectedKeyBeforeSync, int? nodeIdForKey)
    {
        if (selectedId < 0) return new(SelectionRestoreKind.Nothing, NoSelection);
        if (nodeIdForKey is int id)
            return id == selectedId
                ? new(SelectionRestoreKind.Keep, id)
                : new(SelectionRestoreKind.Moved, id);
        // キーが取れていたのに見つからない = 消えた。キーが無い（行が無かった）ときは従来どおり ID を保つ
        return selectedKeyBeforeSync.Length > 0
            ? new(SelectionRestoreKind.Lost, NoSelection)
            : new(SelectionRestoreKind.Keep, selectedId);
    }

    /// <summary>インスペクタへ新しい DFS ID を知らせるべきか（同じアクターの ID が変わったときだけ）。</summary>
    public bool NotifiesInspector => Kind == SelectionRestoreKind.Moved;
}
