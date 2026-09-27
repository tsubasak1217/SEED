namespace SEED;

/// <summary>
/// 描き方の方針（<see cref="Redraw.Policy"/>。プロジェクト設定 project_settings.json の <c>render_policy</c> と同じ値）。
/// 数値は Rust 側 runtime/src/engine/core/redraw/policy.rs の POLICY_CODE_* と一致させる。
/// </summary>
public enum RedrawPolicy
{
    /// <summary>毎フレーム描く（既定。ゲーム向け。今までの動き）。</summary>
    Continuous = 0,
    /// <summary>描く理由があるときだけ描く（止まっている画面の多いアプリ向け。理由の無いフレームが続いたら描画を止める）。</summary>
    OnDemand = 1,
}

/// <summary>
/// 描画の要求（「描く理由」の申告。W2-10a。正典は docs/redraw_policy.md）。
///
/// <para><b>いつ効くか</b><br/>
/// 方針が <see cref="RedrawPolicy.OnDemand"/>（プロジェクト設定の <c>render_policy: "on_demand"</c> か
/// <see cref="Policy"/> の上書き）で、Play の間だけ効く。エンジンは入力・ジェスチャー・アニメーション・パーティクル・
/// SEED.Platform のイベント・IPC などを自分で「描く理由」にし、理由の無いフレームが続いたら（既定 10 回）描画を止めて眠る。
/// 止めている間は <b>Update などのフェーズが呼ばれない</b>。エンジンが知らない動き（スクリプトで動かす演出・
/// SEED.Draw の図形のアニメーション・時計の表示）は、ここで申告する。
/// 方針が <see cref="RedrawPolicy.Continuous"/>（既定）なら毎フレーム描くので、呼んでも何も変わらない。
/// </para>
///
/// <para><b>止めていた時間</b><br/>
/// 止めていた時間はゲームの時間（<see cref="Time.DeltaTime"/>・<see cref="Time.ElapsedTime"/>・Unscaled を含む）に入らない
/// （起きた最初のフレームの DeltaTime は 1/60 秒で切り詰められる）。時刻で何かをするなら、実時間
/// （<c>System.DateTime.Now</c> など）で判定し、次に起きる時刻を <see cref="RequestAfter"/> で申告する。
/// </para>
///
/// <example>
/// <code>
/// // 時計の表示: 次の秒の変わり目に起きて描き直す
/// public override void Update(ref NativeFrameContext ctx)
/// {
///     var now = System.DateTime.Now;
///     clockText.Content = now.ToString("HH:mm:ss");
///     SEED.Redraw.RequestAfter(1f - now.Millisecond / 1000f);
/// }
///
/// // 0.3 秒の押下の演出: 演出の間だけ描き続ける
/// public override void OnPointerDown() { SEED.Redraw.KeepAlive(0.3f); }
///
/// // センサーを読む画面・鳴動の画面: この画面がある間は毎フレーム描く
/// public override void OnStart()   { SEED.Redraw.SetContinuous(true); }
/// public override void OnDestroy() { SEED.Redraw.SetContinuous(false); }
/// </code>
/// </example>
/// </summary>
public static class Redraw
{
    /// <summary>
    /// 次の 1 フレームを描く（描画を止めていれば起こす）。どのスレッドから呼んでもよい
    /// （<c>async</c> の続き＝通信の完了などから、画面を更新させたいときに使える）。
    /// </summary>
    public static void Request() => ScriptHost.RedrawOp(ScriptHost.RedrawOpRequest, 0f);

    /// <summary>
    /// <paramref name="seconds"/> 秒の後（実時間）に 1 フレームを描く。描画を止めている間はその時刻に起きる。
    /// 複数回呼んだらいちばん早い予定が効く（予定は時刻が来て描いたら消える）。0 は次のフレーム。
    /// </summary>
    /// <param name="seconds">秒（0 以上。上限は 1 日で、それより先はその時刻に起きて決め直す）</param>
    /// <returns>受け付けたら true（NaN・負の値は false で何もしない）</returns>
    public static bool RequestAfter(float seconds) =>
        ScriptHost.RedrawOp(ScriptHost.RedrawOpRequestAfter, seconds) == ScriptHost.RedrawResultOk;

    /// <summary>
    /// <paramref name="seconds"/> 秒の間（実時間）は描き続ける（部品のアニメーション・演出の間に使う）。
    /// 延ばすだけで縮めない（短い KeepAlive を後から呼んでも、先の長い期限のまま）。
    /// </summary>
    /// <param name="seconds">秒（0 以上。0 は何もしない）</param>
    /// <returns>受け付けたら true（NaN・負の値は false で何もしない）</returns>
    public static bool KeepAlive(float seconds) =>
        ScriptHost.RedrawOp(ScriptHost.RedrawOpKeepAlive, seconds) == ScriptHost.RedrawResultOk;

    /// <summary>
    /// true の間は常に毎フレーム描く（ゲームの画面・センサーを読む画面・鳴動の画面など、毎フレーム Update が要る画面）。
    /// false で外す。Play の開始・停止で false に戻る。
    /// </summary>
    public static void SetContinuous(bool continuous) =>
        ScriptHost.RedrawOp(ScriptHost.RedrawOpSetContinuous, continuous ? 1f : 0f);

    /// <summary><see cref="SetContinuous"/>(true) の中か。</summary>
    public static bool IsContinuous => ScriptHost.RedrawOp(ScriptHost.RedrawOpIsContinuous, 0f) == 1;

    /// <summary>
    /// 描き方の方針（get/set）。読むと今の方針（上書き → プロジェクト設定の順）。
    /// 書くとプロジェクト設定を実行中だけ上書きする（Play の開始・停止で外れる。<see cref="ResetPolicy"/> でも外せる）。
    /// FFI が使えないときは <see cref="RedrawPolicy.Continuous"/> を返し、書き込みは何もしない。
    /// </summary>
    public static RedrawPolicy Policy
    {
        get
        {
            int code = ScriptHost.RedrawOp(ScriptHost.RedrawOpGetPolicy, 0f);
            return code == (int)RedrawPolicy.OnDemand ? RedrawPolicy.OnDemand : RedrawPolicy.Continuous;
        }
        set => ScriptHost.RedrawOp(ScriptHost.RedrawOpSetPolicy, (float)(int)value);
    }

    /// <summary><see cref="Policy"/> の上書きを外して、プロジェクト設定の方針へ戻す。</summary>
    public static void ResetPolicy() =>
        ScriptHost.RedrawOp(ScriptHost.RedrawOpSetPolicy, ScriptHost.RedrawPolicyClearOverride);
}
