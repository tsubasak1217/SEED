using System;

namespace SEED;

/// <summary>
/// キーコード。スクリプトの入力判定（<see cref="Input"/>）で使う。
///
/// 【重要】数値は Rust 側 runtime/.../scripting/input_bridge.rs の対応表と
/// 必ず一致させること（ずれるとキー判定が壊れる）。
/// </summary>
public enum KeyCode
{
    // ── アルファベット（A=0 .. Z=25）──
    A = 0, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    // ── 数字キー（メインキーボード上段。Alpha0=26 .. Alpha9=35）──
    Alpha0 = 26, Alpha1, Alpha2, Alpha3, Alpha4,
    Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
    // ── ファンクションキー（F1=36 .. F12=47）──
    F1 = 36, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    // ── 矢印キー（48..51）──
    UpArrow = 48, DownArrow, LeftArrow, RightArrow,
    // ── 特殊キー（52..63）──
    Space = 52, Enter, Escape, Tab, Backspace, Delete,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
}

/// <summary>
/// マウスボタン。数値は Rust 側 input_bridge.rs の対応表と一致させること。
/// </summary>
public enum MouseButton
{
    /// <summary>左ボタン。</summary>
    Left = 0,
    /// <summary>右ボタン。</summary>
    Right = 1,
    /// <summary>中ボタン（ホイールクリック）。</summary>
    Middle = 2,
}

/// <summary>
/// キーボード・マウス入力の静的アクセサ。エンジンの入力状態を FFI 経由で参照する。
///
/// 判定は 3 種類: 押している間（GetKey）・押した瞬間（GetKeyDown）・離した瞬間（GetKeyUp）。
/// ゲームロジックのフェーズ（Update 等）内でのみ有効な値を返す。
/// </summary>
public static class Input
{
    // ── 判定種別（Rust 側 input_bridge.rs の INPUT_KIND_* と一致させる）──
    private const int KindPress = 0;   // 押されている間
    private const int KindTrigger = 1; // 押された瞬間
    private const int KindRelease = 2; // 離された瞬間

    // ── マウス状態種別（Rust 側 MOUSE_STATE_* と一致させる）──
    private const int MousePositionKind = 0;
    private const int MouseDeltaKind = 1;
    private const int MouseScrollKind = 2;
    private const int MouseCanvasPositionKind = 3;
    private const int MousePositionDeltaKind = 4;

    // ── カーソルロック操作種別（Rust 側 CURSOR_LOCK_* と一致させる）──
    private const int CursorLockGet = 0;
    private const int CursorLockSet = 1;

    // ── キーボード ───────────────────────────────────────────

    /// <summary>キーが押されている間 true。</summary>
    public static bool GetKey(KeyCode key) => ScriptHost.InputKey(KindPress, (uint)key);

    /// <summary>キーが押された瞬間のフレームだけ true。</summary>
    public static bool GetKeyDown(KeyCode key) => ScriptHost.InputKey(KindTrigger, (uint)key);

    /// <summary>キーが離された瞬間のフレームだけ true。</summary>
    public static bool GetKeyUp(KeyCode key) => ScriptHost.InputKey(KindRelease, (uint)key);

    // ── マウスボタン ─────────────────────────────────────────

    /// <summary>マウスボタンが押されている間 true。</summary>
    public static bool GetMouseButton(MouseButton button) => ScriptHost.InputMouse(KindPress, (uint)button);

    /// <summary>マウスボタンが押された瞬間のフレームだけ true。</summary>
    public static bool GetMouseButtonDown(MouseButton button) => ScriptHost.InputMouse(KindTrigger, (uint)button);

    /// <summary>マウスボタンが離された瞬間のフレームだけ true。</summary>
    public static bool GetMouseButtonUp(MouseButton button) => ScriptHost.InputMouse(KindRelease, (uint)button);

    // ── マウス状態 ───────────────────────────────────────────

    /// <summary>マウスのスクリーン座標（ピクセル。左上原点）。</summary>
    public static Vector2 MousePos => ScriptHost.InputMouseVec2(MousePositionKind);

    /// <summary>マウスの相対移動量（今フレーム）。</summary>
    public static Vector2 MouseMove => ScriptHost.InputMouseVec2(MouseDeltaKind);

    /// <summary>マウスホイールのスクロール量（今フレーム。上=正）。</summary>
    public static float MouseScroll => ScriptHost.InputMouseScroll(MouseScrollKind);

    /// <summary>マウスのスクリーン座標（ピクセル。左上原点）。<see cref="MousePos"/> の別名。</summary>
    public static Vector2 MousePosition => MousePos;

    /// <summary>
    /// マウスの前フレーム比移動量（ピクセル。右=+X / 下=+Y）。
    ///
    /// カーソル座標の差分から求めるため、エディタに埋め込まれた Play でも**必ず取れる**
    /// （<see cref="MouseMove"/> は OS の Raw Input 由来で、埋め込み時に届かないことがある）。
    /// マウスジェスチャ（引いて振る等）の判定にはこちらを使うこと。
    /// カーソルが画面端で止まると 0 になる点だけ注意。
    /// </summary>
    public static Vector2 MouseDelta => ScriptHost.InputMouseVec2(MousePositionDeltaKind);

    /// <summary>
    /// マウスのキャンバス座標。スクリーンスペースキャンバスの座標系
    /// （画面中央が原点・Y 下向き・1 単位 = 1px）で返す。
    ///
    /// UI のポインタ判定（OnPointerEnter 等）が使うのと**同じ座標系**なので、
    /// CanvasTransform.Position と直接比較できる。
    /// キャンバス世界線でない・Play 外では (0, 0)。
    /// </summary>
    public static Vector2 MousePositionCanvas => ScriptHost.InputMouseVec2(MouseCanvasPositionKind);

    // ── カーソルロック ───────────────────────────────────────

    /// <summary>
    /// カーソルロック（相対マウスモード）。
    ///
    /// ロック中はカーソルが**非表示**になり、毎フレームビューポート中央へ戻される。
    /// そのため <see cref="MouseDelta"/> は画面端でクランプされず動き続け、
    /// ボタンを押さないマウスジェスチャ（引く／押す等）を安定して取れる。
    /// エディタ埋め込み Play では ClipCursor でカーソルがビューポートに閉じ込められ、
    /// 端に当たると <see cref="MouseDelta"/> が 0 になるため、ジェスチャ判定中は
    /// これを true にすること。
    ///
    /// ロック中は <see cref="MousePos"/> / <see cref="MousePositionCanvas"/> は
    /// 中央付近に張り付くので**意味を持たない**（UI のヒット判定には使えない）。
    ///
    /// Play を停止すると自動的に解除される（解除し忘れでカーソルが消えたままにならない）。
    /// 設定はフレーム末にエンジンへ適用されるが、getter は同一フレーム内でも
    /// 設定した値を返す。
    /// </summary>
    public static bool CursorLocked
    {
        get => ScriptHost.InputCursorLock(CursorLockGet, 0) != 0;
        set => ScriptHost.InputCursorLock(CursorLockSet, value ? 1 : 0);
    }

    /// <summary>カーソルロックの設定（<see cref="CursorLocked"/> の別名）。</summary>
    public static void SetCursorLock(bool locked) => CursorLocked = locked;

    // ── 簡易軸入力 ───────────────────────────────────────────

    /// <summary>
    /// WASD / 矢印キーから水平・垂直の移動入力を [-1, 1] で返す簡易ヘルパ。
    /// x: A/←=-1, D/→=+1、y: S/↓=-1, W/↑=+1。斜め入力は正規化しない。
    /// </summary>
    public static Vector2 MoveAxis()
    {
        float x = 0f, y = 0f;
        if (GetKey(KeyCode.A) || GetKey(KeyCode.LeftArrow))  x -= 1f;
        if (GetKey(KeyCode.D) || GetKey(KeyCode.RightArrow)) x += 1f;
        if (GetKey(KeyCode.S) || GetKey(KeyCode.DownArrow))  y -= 1f;
        if (GetKey(KeyCode.W) || GetKey(KeyCode.UpArrow))    y += 1f;
        return new Vector2(x, y);
    }
}
