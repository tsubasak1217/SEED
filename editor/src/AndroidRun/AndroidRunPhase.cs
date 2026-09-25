// ============================================================
//  AndroidRunPhase.cs — エディタからの Android の実行の状態・止め方・終わり方・IPC の状態の値
//
//  【状態機械】（遷移の正典は AndroidRunStateMachine.cs）
//    Idle ──実行──▶ Building ──起動に成功──▶ Running ⇄ Paused ──停止／アプリの終了──▶ Stopping ──▶ Idle
//                    │  └──────停止──────────────────────────────────────────────▶ Stopping ──▶ Idle
//                    └──失敗・logcat の終わり（パイプラインが戻った）──────────────────────────────▶ Idle
//  Running ⇄ Paused は端末のアプリとの IPC（TCP。段階D-1）がつながっているときだけ（AndroidIpcStatus.Connected）。
//  Paused の間は端末のシーンの写しを取り出す（AndroidPauseSnapshotStatus。docs/android.md §20.17）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

namespace SEEDEditor.AndroidRun;

/// <summary>
/// 一時停止中の端末のシーンの写し（SNAPSHOT_SCENE で書き出させて run-as で PC へ取り出したもの）の状態。
/// Paused の間だけ意味を持ち、Paused を出る（再開・停止・切断・アプリの終了）と None に戻る。
/// </summary>
public enum AndroidPauseSnapshotStatus
{
    /// <summary>取り出していない（一時停止していない）。</summary>
    None,

    /// <summary>取り出している途中（端末が書き出す → PC へ写す）。</summary>
    Fetching,

    /// <summary>取り出せた（PC のファイルがある。エディタがシーンパネルに閲覧専用で出す）。</summary>
    Ready,

    /// <summary>取り出せなかった（理由は AndroidPauseSnapshotState.Note。一時停止は続いている）。</summary>
    Failed,
}

/// <summary>Android の実行の状態。</summary>
public enum AndroidRunPhase
{
    /// <summary>動いていない。</summary>
    Idle,

    /// <summary>準備・ビルド・インストール・起動の途中（アプリはまだ起動していない）。</summary>
    Building,

    /// <summary>端末でアプリが動いている（logcat を流している）。</summary>
    Running,

    /// <summary>端末のアプリを一時停止している（IPC で PAUSE を送った。段階D-1。logcat・アプリの見張りは続ける）。</summary>
    Paused,

    /// <summary>止めている途中（子プロセスの終了・アプリの停止を待っている）。</summary>
    Stopping,
}

/// <summary>
/// 端末のアプリとの IPC（adb forward ＋ TCP。段階D-1）の状態。Running / Paused の間だけ意味を持つ。
/// </summary>
public enum AndroidIpcStatus
{
    /// <summary>使わない（実行していない・ポート 0 の指定で起動オプションを渡していない）。</summary>
    Off,

    /// <summary>つないでいる途中（起動の直後・切れた後のつなぎ直し）。</summary>
    Connecting,

    /// <summary>つながっている（実行バーから一時停止・再開を送れる）。</summary>
    Connected,

    /// <summary>つながらなかった（古い APK・時間切れ等。理由は AndroidRunSnapshot.IpcNote）。</summary>
    Unavailable,
}

/// <summary>止める理由。</summary>
public enum AndroidRunStopReason
{
    /// <summary>止めていない（パイプラインが自分で終わった）。</summary>
    None,

    /// <summary>停止ボタン。</summary>
    User,

    /// <summary>
    /// 端末でアプリが終わった（最近のタスクから消した・強制停止・クラッシュ等。pidof で見つける。戻るキーでは終わらない。
    /// 段階D-1 から、IPC が切れたときにもすぐ pidof で確かめて見つける）。
    /// </summary>
    AppExited,

    /// <summary>エディタを閉じる。</summary>
    Shutdown,
}

/// <summary>1 回の実行の終わり方（Output パネルへ出す最後の 1 行の種類）。</summary>
public enum AndroidRunOutcome
{
    /// <summary>起動した後に停止ボタンで止めた（アプリも止める）。</summary>
    StoppedByUser,

    /// <summary>起動する前に停止ボタンで止めた（ビルドの中止。アプリには触らない）。</summary>
    BuildCanceled,

    /// <summary>アプリ側で終わった。</summary>
    AppExited,

    /// <summary>logcat が自分で終わった（端末が外れた・adb が終了した等）。</summary>
    LogcatEnded,

    /// <summary>失敗した（理由はパイプラインの結果）。</summary>
    Failed,

    /// <summary>エディタを閉じるなどで中断した。</summary>
    Canceled,
}
