namespace SEED.Platform;

/// <summary>
/// 戻るの手ぶりの段階（<see cref="BackGestureEvent.Phase"/>・<see cref="PlatformDiagnostics.SimulateBackGesture"/>。
/// 予測型の戻る。W2 の手直し P1-3）。イベントの名前（<see cref="App.BackStartedEvent"/> ほか）と 1 対 1。
/// </summary>
public enum BackGesturePhase
{
    /// <summary>始まった（<see cref="App.BackStartedEvent"/>。Android 14 以上）。</summary>
    Started = 0,
    /// <summary>進んだ（<see cref="App.BackProgressedEvent"/>。Android 14 以上。毎フレーム）。</summary>
    Progressed = 1,
    /// <summary>取り消された（<see cref="App.BackCancelledEvent"/>。Android 14 以上）。</summary>
    Cancelled = 2,
    /// <summary>確定した（<see cref="App.BackInvokedEvent"/>。Android 13 以上。続けて Escape が届く）。</summary>
    Invoked = 3,
}
