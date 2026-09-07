// ============================================================================
//  TutorialDialogueSlot.cs
//  チュートリアルの台詞が「どの場面で出るか」を表す列挙型。
// ============================================================================

/// <summary>
/// 台詞の差し込み位置【台詞とミッションを結びつけるキー】。
///
/// 台詞（<see cref="TutorialDialogue"/>）はミッション ID とこの枠の組で引かれる。
/// 同じ組の台詞が複数あれば、リストに並んだ順に 1 枚ずつ送られる。
/// </summary>
public enum TutorialDialogueSlot
{
    /// <summary>ミッションを始める前の説明（複数枚可）。</summary>
    Intro,

    /// <summary>ミッションを達成したあとの一言（複数枚可）。</summary>
    Clear,

    /// <summary>漂流物「ひるませ」を拾ったときの説明。</summary>
    DriftStun,

    /// <summary>漂流物「魚の回復」を拾ったときの説明。</summary>
    DriftFishRecover,

    /// <summary>漂流物「糸の回復」を拾ったときの説明。</summary>
    DriftLineRecover,

    /// <summary>締めの演出中に出す台詞。</summary>
    Cutscene,
}
