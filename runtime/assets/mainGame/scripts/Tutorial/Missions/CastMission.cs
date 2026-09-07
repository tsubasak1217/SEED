// ============================================================================
//  CastMission.cs
//  「構えから仕掛けを投げる」ミッションの判定。
// ============================================================================

/// <summary>
/// 構えから仕掛けを投げるミッション【キャストの練習】。
///
/// 【判定】
/// <c>fishing.cast</c>（振り抜いて投げた瞬間）を受けたらクリア。
///
/// 【やり直しについて】
/// 途中で左クリックを離すと構えが解けて待機へ戻るが、それは釣り本体の通常挙動であり、
/// このミッションは何もしない（＝自然に「構えからやり直し」になる）。
/// 案内の文言だけを構えの有無で切り替えて、いま何をすべきかが常に読めるようにする。
///
/// 【ルール】
/// 投げに集中させるため、移動はデータ側で許可しない
/// （<see cref="TutorialMission.allowMove"/> を false にする）。
/// </summary>
public sealed class CastMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>構えていないときに出す案内。</summary>
    private const string HintNotReady = "左クリックで構えよう";

    /// <summary>構えているときに出す案内。</summary>
    private const string HintReady = "マウスを左から右へ振ろう";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>いま構えているか（ready_begin / ready_end で切り替わる）。</summary>
    private bool isReady;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Cast;

    /// <summary>いま何をすればよいかの案内。</summary>
    public override string ProgressText => isReady ? HintReady : HintNotReady;

    /// <summary>構えの出入りと、投げの成立を購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        Subscribe(FishingEvents.ReadyBegin, () => isReady = true);
        Subscribe(FishingEvents.ReadyEnd,   () => isReady = false);
        Subscribe(FishingEvents.Cast,       MarkCleared);
    }
}
