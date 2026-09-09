using System;

namespace SEEDEditor.Reload;

/// <summary>
/// 自動再読込の対象種別。
///
/// スクリプト（.cs のホットリロード）とシーン（.scene の読み直し）では
/// Play 中に適用してよいかどうかの規則が異なるため、判定の入力として区別する。
/// </summary>
public enum AutoReloadKind
{
    /// <summary>ユーザースクリプト（.cs）のホットリロード。</summary>
    Script,

    /// <summary>今開いているシーン（.scene）の外部変更の取り込み。</summary>
    Scene,
}

/// <summary>
/// 自動再読込の判定に必要な範囲だけを表すエディタの再生状態。
///
/// <see cref="SEEDEditor.Runtime.EditorState"/> は Idle / Building / Launching も持つが、
/// 本判定に必要なのは「Play 相当か否か」だけであり、また判定ロジックを WPF・ランタイム
/// 非依存にして単体テストできるようにしたいため、専用の最小な列挙を持つ。
/// EditorState からの変換は注入側（MainWindow）で行う。
/// </summary>
public enum PlaybackState
{
    /// <summary>編集中（Play していない）。Idle / Building / Launching もここに含める。</summary>
    Edit,

    /// <summary>Play 実行中。</summary>
    Play,

    /// <summary>Play 中の一時停止。ワールドは Play のまま生きているので Play と同じ扱い。</summary>
    Pause,
}

/// <summary>
/// 変更を検出したときに取るべき行動。
/// </summary>
public enum AutoReloadDecision
{
    /// <summary>今すぐ適用する（再読込を実行する）。</summary>
    ApplyNow,

    /// <summary>今は適用せず保留する。Play 停止（Edit 復帰）時に改めて適用する。</summary>
    Defer,

    /// <summary>適用も保留もしない（＝設定オフ。ユーザーが自動反映を望んでいない）。</summary>
    Drop,
}

/// <summary>
/// 自動再読込を「今適用してよいか」を決める純粋な判定ロジック。
///
/// 【なぜ切り出すか】
/// Play 中にスクリプトのホットリロードが走ると、ランタイムは全スクリプトインスタンスを
/// 作り直すため OnStart が再実行される。その結果、
///   - チュートリアル・進行状態が最初からやり直しになる
///   - OnStart で Instantiate している補助アクタ（PauseMenu / ResultPanel / BeatIcon 等）が
///     二重生成されて重くなる
///   - static シングルトンが古いハンドルを保持したままになり、一部が描画されなくなる
/// といった実害が出る。シーンの再読込に至ってはワールドごと作り直すためさらに破壊的。
///
/// そこで「状態 × 種別 × 設定 → 適用 / 保留 / 破棄」の判断だけを、
/// ファイル監視・IPC・UI から切り離した純関数として置き、単体テストで固定する。
/// （テスト: editor/tests/AutoReloadPolicyTests）
///
/// 【規則】
///   - 設定トグルがオフ                     → Drop（従来どおり手動反映のみ）
///   - Edit 中                              → ApplyNow
///   - Play / Pause 中の Scene              → 必ず Defer（Play 中のシーン再読込は禁止）
///   - Play / Pause 中の Script             → 設定「Play 中もスクリプトを即時反映する」が
///                                            オンなら ApplyNow、既定（オフ）なら Defer
/// </summary>
public static class AutoReloadPolicy
{
    // ── ステータス文言（UI 文言を 1 箇所に集約する）────────────────

    /// <summary>Play 中にスクリプト変更を保留したときの通知文言。</summary>
    public const string MessageScriptDeferred =
        "スクリプトの変更を検出しました（Play 停止時に反映）";

    /// <summary>Play 中にシーンの外部変更を保留したときの通知文言。</summary>
    public const string MessageSceneDeferred =
        "シーンの外部変更を検出（Play 停止時に再読み込み）";

    // ── 判定 ──────────────────────────────────────────────────

    /// <summary>
    /// 再生状態が「Play 相当」か（ワールドが実行中で、作り直すと進行が壊れる状態か）。
    /// </summary>
    /// <param name="state">現在の再生状態。</param>
    /// <returns>Play または Pause なら true。</returns>
    public static bool IsPlaying(PlaybackState state)
        => state is PlaybackState.Play or PlaybackState.Pause;

    /// <summary>
    /// 変更を検出したときの行動を決める。
    /// </summary>
    /// <param name="kind">変更の種別（スクリプト / シーン）。</param>
    /// <param name="state">現在のエディタ再生状態。</param>
    /// <param name="autoReloadEnabled">その種別の自動再読込設定がオンか。</param>
    /// <param name="applyScriptsDuringPlay">
    /// 設定「Play 中もスクリプトを即時反映する」の値。<see cref="AutoReloadKind.Scene"/> では無視される
    /// （シーン再読込は Play 中には決して行わない）。
    /// </param>
    /// <returns>取るべき行動。</returns>
    public static AutoReloadDecision Decide(
        AutoReloadKind kind,
        PlaybackState  state,
        bool           autoReloadEnabled,
        bool           applyScriptsDuringPlay)
    {
        // 1. 自動反映そのものがオフなら、保留もしない。
        //    保留すると「オフにしたのに Play 停止時に勝手に反映された」になるため。
        if (!autoReloadEnabled) return AutoReloadDecision.Drop;

        // 2. Edit 中はいつでも適用してよい。
        if (!IsPlaying(state)) return AutoReloadDecision.ApplyNow;

        // 3. Play 中。シーンは例外なく保留する（ワールドごと作り直すため）。
        if (kind == AutoReloadKind.Scene) return AutoReloadDecision.Defer;

        // 4. Play 中のスクリプト。既定は保留。設定を明示的にオンにした人だけ即時反映する。
        return applyScriptsDuringPlay
            ? AutoReloadDecision.ApplyNow
            : AutoReloadDecision.Defer;
    }

    /// <summary>
    /// Play が終了して Edit へ戻ったときに、保留していた変更を適用してよいかを決める。
    ///
    /// 保留中でも、その間にユーザーが設定をオフにしていたら適用しない
    /// （<see cref="Decide"/> と同じ「オフなら何もしない」規則を復帰時にも通す）。
    /// </summary>
    /// <param name="hasPending">保留中の変更があるか。</param>
    /// <param name="autoReloadEnabled">その種別の自動再読込設定がオンか。</param>
    /// <returns>適用するなら ApplyNow、保留が無い / 設定オフなら Drop。</returns>
    public static AutoReloadDecision DecideOnReturnToEdit(bool hasPending, bool autoReloadEnabled)
    {
        if (!hasPending)          return AutoReloadDecision.Drop;
        if (!autoReloadEnabled)   return AutoReloadDecision.Drop;
        return AutoReloadDecision.ApplyNow;
    }
}
