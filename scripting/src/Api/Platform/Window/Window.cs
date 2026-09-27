namespace SEED.Platform;

/// <summary>
/// アプリの画面（窓）の振る舞い（W1-4a の <see cref="SetShowWhenLocked"/>・W1-6 の <see cref="SetKeepScreenOn"/> と
/// <see cref="SetSystemBarsVisible"/>）。Android ではメインプロセスが Activity の窓を UI スレッドで切り替えて答える（IPC なし。
/// どれもすぐ返り、切り替えは少し後に効く）。デスクトップの模擬は受け付けて状態を記録し、ログに残すだけ（ウィンドウには何もしない）。
/// </summary>
public static class Window
{
    /// <summary>画面のモジュール。</summary>
    private const string Module = "window";

    /// <summary>ロック画面の上に出す＋画面を点ける の切り替え。</summary>
    private const string MethodSetShowWhenLocked = "set_show_when_locked";

    /// <summary>画面を点けたままにする の切り替え（W1-6）。</summary>
    private const string MethodSetKeepScreenOn = "set_keep_screen_on";

    /// <summary>システムバーを出す・隠す の切り替え（W1-6）。</summary>
    private const string MethodSetSystemBarsVisible = "set_system_bars_visible";

    /// <summary>切り替えの命令の引数: 入れるか。</summary>
    private const string KeyOn = "on";

    /// <summary><see cref="Platform.LastError"/>: 操作する画面（Activity）が無い（Android）。</summary>
    public const string ErrorNoActivity = "no_activity";

    /// <summary>
    /// ロック画面の上に出す＋画面を点ける（Android の setShowWhenLocked・setTurnScreenOn）を切り替える。ロックは解除しない。
    /// 目覚ましの鳴動で起動したとき（<see cref="LaunchKind.Alarm"/>）はエンジンが上げているので、鳴動を片付けたら false で下ろす
    /// （下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ない）。下ろし忘れても、ランチャー・最近のタスクから
    /// 開き直したときはエンジンが下ろす（W1-6）。すぐ返る（切り替えは UI スレッドで少し後）。
    /// </summary>
    /// <param name="on">上げるなら true。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SetShowWhenLocked(bool on) =>
        Platform.TryInvoke(Module, MethodSetShowWhenLocked, PlatformJson.BoolObject(KeyOn, on), out _);

    /// <summary>
    /// 画面を点けたままにする（Android の窓の FLAG_KEEP_SCREEN_ON）を切り替える（W1-6）。アプリが前面に見えている間だけ効き、
    /// 利用者の電源ボタンでの消灯は止めない。権限は要らない。既定は off（端末の画面の消灯の設定に従う）。
    /// </summary>
    /// <param name="on">点けたままにするなら true。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SetKeepScreenOn(bool on) =>
        Platform.TryInvoke(Module, MethodSetKeepScreenOn, PlatformJson.BoolObject(KeyOn, on), out _);

    /// <summary>
    /// システムバー（ステータスバー・ナビゲーションバー）を出すか隠すかを切り替える（W1-6）。起動時の既定はプロジェクト設定の
    /// android.system_bars（visible / hidden）で、これで切り替えると以後はその状態になる（隠しているときは端からのスワイプで一時的に出せ、
    /// 画面へ戻ると隠し直す）。安全領域（<see cref="SEED.Screen.SafeArea"/>）はこれに追従する（出ているバーの分が外れる。次のフレーム以降）。
    /// </summary>
    /// <param name="on">出すなら true、隠すなら false。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SetSystemBarsVisible(bool on) =>
        Platform.TryInvoke(Module, MethodSetSystemBarsVisible, PlatformJson.BoolObject(KeyOn, on), out _);
}
