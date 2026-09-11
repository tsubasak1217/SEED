// ============================================================================
//  MissionBase.cs
//  ミッション判定クラスの共通土台（購読の後始末とサブ目標の管理）。
// ============================================================================

using System.Collections.Generic;

/// <summary>
/// ミッション判定クラスの共通土台
/// 【購読の後始末とサブ目標管理の唯一の実装】。
///
/// 【責務】
///  1. イベント購読をまとめて持ち、<see cref="End"/> で確実に解除する
///  2. サブ目標（<see cref="MissionObjective"/>）のリストを持つ
///  3. 達成フラグ（<see cref="IsCleared"/>）の立て方を 1 か所へ集約する
///
/// 【なぜ基底クラスにするか】
/// ミッションは SEEDScript ではないため、<c>this.On(...)</c> の自動解除が使えない。
/// 各ミッションが個別に Subscribe / Dispose を書くと、1 つ書き漏らしただけで
/// 「終わったはずのミッションがイベントを拾い続ける」という追いにくい不具合になる。
/// 購読の入口と出口をここへ 1 本化する。
///
/// 【派生クラスの約束】
///  - 購読は必ず <see cref="Subscribe"/> 経由で張る（直接 SEED.Events.Subscribe を呼ばない）
///  - <see cref="OnEnd"/> を override したら base 呼び出しは不要（購読解除は基底が行う）
///  - 達成したら <see cref="MarkCleared"/> を呼ぶ（IsCleared を直接立てない）
/// </summary>
public abstract class MissionBase : IMission
{
    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>張ってあるイベント購読（<see cref="End"/> ですべて解除する）。</summary>
    private readonly List<SEED.EventSubscription> subscriptions = new();

    /// <summary>サブ目標の実体（派生クラスが <see cref="AddObjective"/> で足す）。</summary>
    private readonly List<MissionObjective> objectives = new();

    // ─── IMission の実装 ─────────────────────────────────────

    /// <summary>このミッションの種類（派生クラスが答える）。</summary>
    public abstract MissionKind Kind { get; }

    /// <summary>達成したか。</summary>
    public bool IsCleared { get; private set; }

    /// <summary>パネル下段に出す進捗の 1 行（既定は「進捗を出さない」）。</summary>
    public virtual string ProgressText => string.Empty;

    /// <summary>パネルに並べるサブ目標のチェック。</summary>
    public IReadOnlyList<MissionObjective> Objectives => objectives;

    /// <summary>
    /// ミッションを開始する。派生クラスの実装は <see cref="OnBegin"/> にある。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    public void Begin(MissionContext ctx)
    {
        IsCleared = false;
        OnBegin(ctx);
    }

    /// <summary>
    /// 毎フレームの判定。達成済みになったら以降は何もしない
    /// （クリア演出中に判定が走り続けて二重に台本を仕込むのを防ぐ）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    public void Update(MissionContext ctx, float unscaledDelta)
    {
        if (IsCleared) { return; }
        OnUpdate(ctx, unscaledDelta);
    }

    /// <summary>
    /// ミッションを終える。購読を必ず解除してから派生クラスの後始末を呼ぶ。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    public void End(MissionContext ctx)
    {
        ClearSubscriptions();
        OnEnd(ctx);
    }

    /// <summary>
    /// 失敗して途中からやり直す。既定ではサブ目標を未達成へ戻すだけ。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    public void Restart(MissionContext ctx)
    {
        IsCleared = false;
        for (int i = 0; i < objectives.Count; i++) { objectives[i].Reset(); }
        OnRestart(ctx);
    }

    // ─── 派生クラスが実装する部分 ───────────────────────────

    /// <summary>開始時の処理（台本の仕込み・サブ目標の作成・購読）。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected abstract void OnBegin(MissionContext ctx);

    /// <summary>毎フレームの判定（達成したら <see cref="MarkCleared"/> を呼ぶ）。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    protected virtual void OnUpdate(MissionContext ctx, float unscaledDelta) { }

    /// <summary>終了時の後始末（生成した目印の破棄など）。購読解除は基底が済ませている。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected virtual void OnEnd(MissionContext ctx) { }

    /// <summary>やり直し時の追加処理（台本の再仕込みなど）。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected virtual void OnRestart(MissionContext ctx) { }

    // ─── 派生クラス向けの道具 ───────────────────────────────

    /// <summary>
    /// 達成済みにする【達成フラグを立てる唯一の入口】。
    /// </summary>
    protected void MarkCleared() { IsCleared = true; }

    /// <summary>
    /// サブ目標を 1 件足して返す（派生クラスは戻り値を控えて達成時に Complete する）。
    /// </summary>
    /// <param name="label">サブ目標の表示名。</param>
    /// <returns>追加したサブ目標。</returns>
    protected MissionObjective AddObjective(string label)
    {
        var objective = new MissionObjective(label);
        objectives.Add(objective);
        return objective;
    }

    /// <summary>すべてのサブ目標が達成済みか（サブ目標が 0 件なら false）。</summary>
    /// <returns>1 件以上あり、すべて達成済みなら true。</returns>
    protected bool AllObjectivesDone()
    {
        if (objectives.Count == 0) { return false; }
        for (int i = 0; i < objectives.Count; i++)
        {
            if (!objectives[i].Done) { return false; }
        }
        return true;
    }

    /// <summary>
    /// 引数なしイベントを購読する【購読の唯一の入口】。
    /// 張った購読は <see cref="End"/> で自動的に解除される。
    /// </summary>
    /// <param name="eventName">購読するイベント名（FishingEvents の定数）。</param>
    /// <param name="handler">受け取ったときに呼ぶ処理。</param>
    protected void Subscribe(string eventName, System.Action handler)
    {
        if (string.IsNullOrWhiteSpace(eventName)) { return; }
        subscriptions.Add(SEED.Events.Subscribe(eventName, handler));
    }

    /// <summary>
    /// 文字列引数つきイベントを購読する【購読の唯一の入口（文字列版）】。
    /// </summary>
    /// <param name="eventName">購読するイベント名（FishingEvents の定数）。</param>
    /// <param name="handler">受け取ったときに呼ぶ処理（引数はイベントの値）。</param>
    protected void Subscribe(string eventName, System.Action<string> handler)
    {
        if (string.IsNullOrWhiteSpace(eventName)) { return; }
        subscriptions.Add(SEED.Events.Subscribe(eventName, handler));
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>張ってあるイベント購読をすべて解除する【解除の唯一の出口】。</summary>
    private void ClearSubscriptions()
    {
        for (int i = 0; i < subscriptions.Count; i++) { subscriptions[i]?.Dispose(); }
        subscriptions.Clear();
    }
}
