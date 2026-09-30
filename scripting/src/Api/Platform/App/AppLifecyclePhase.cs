namespace SEED.Platform;

/// <summary>
/// 前面・背面の知らせの段階（<see cref="AppLifecycleEvent.Phase"/>・<see cref="PlatformDiagnostics.SimulateLifecycle"/>。2026-10-01）。
/// </summary>
public enum AppLifecyclePhase
{
    /// <summary>前面へ戻った（<see cref="App.ResumedEvent"/>。Android の onResume。デスクトップの模擬は窓がフォーカスを得た）。</summary>
    Resumed,

    /// <summary>前面を離れた（<see cref="App.PausedEvent"/>。Android の onPause。デスクトップの模擬は窓がフォーカスを失った）。</summary>
    Paused,
}
