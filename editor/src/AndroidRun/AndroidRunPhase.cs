// ============================================================
//  AndroidRunPhase.cs — エディタからの Android の実行の状態・止め方・終わり方の値
//
//  【状態機械】（遷移の正典は AndroidRunStateMachine.cs）
//    Idle ──実行──▶ Building ──起動に成功──▶ Running ──停止／アプリの終了──▶ Stopping ──▶ Idle
//                    │  └──────停止───────────────────────────────────────▶ Stopping ──▶ Idle
//                    └──失敗・logcat の終わり（パイプラインが戻った）──────────────────────▶ Idle
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

namespace SEEDEditor.AndroidRun;

/// <summary>Android の実行の状態。</summary>
public enum AndroidRunPhase
{
    /// <summary>動いていない。</summary>
    Idle,

    /// <summary>準備・ビルド・インストール・起動の途中（アプリはまだ起動していない）。</summary>
    Building,

    /// <summary>端末でアプリが動いている（logcat を流している）。</summary>
    Running,

    /// <summary>止めている途中（子プロセスの終了・アプリの停止を待っている）。</summary>
    Stopping,
}

/// <summary>止める理由。</summary>
public enum AndroidRunStopReason
{
    /// <summary>止めていない（パイプラインが自分で終わった）。</summary>
    None,

    /// <summary>停止ボタン。</summary>
    User,

    /// <summary>端末でアプリが終わった（戻るキー・クラッシュ等。pidof で見つける）。</summary>
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
