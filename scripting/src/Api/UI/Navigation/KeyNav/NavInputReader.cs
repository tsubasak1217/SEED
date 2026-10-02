namespace SEED.UI;

// ============================================================
//  NavInputReader.cs — 方向キー・パッドの入力を 1 フレーム分読む（UiNavigator が持つ。2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  読む先は設定（UiNavigationOptions.InputSource）:
//    - InputMap のアクション: 方向 = GetAction（押している間。condition Press）と GetActionStart（押した瞬間）、
//      スティック = GetVector2（Axis2D。Y の正 = 上）、決定・キャンセル = GetActionStart
//    - 素の入力（SEED.Input）: 方向 = 矢印キー、決定 = Enter・Space、キャンセル = Escape
//  向きは NavInputMath でまとめ（同時押し・スティックの閾値）、NavRepeatClock で連続移動（キーリピート）にする。
//  指・マウスで触った（マウスのボタンを押した・指が触れ始めた）ことも読む（枠を隠す）。
// ============================================================

/// <summary>1 フレームの方向キー・パッドの入力（読んだ結果）。</summary>
internal readonly struct NavFrameInput
{
    /// <summary>このフレームに動く向き（連続移動の時計を通った後。None = 動かない）。</summary>
    public readonly FocusDirection Move;
    /// <summary>決定を押した瞬間。</summary>
    public readonly bool Submit;
    /// <summary>キャンセルを押した瞬間（アクションか、素の入力の Escape）。</summary>
    public readonly bool Cancel;
    /// <summary>このフレームに Escape を押した（戻るの段は BackDispatcher.PollBackKey へ回す＝二重に配らない）。</summary>
    public readonly bool EscapeDown;
    /// <summary>指・マウスで触った。</summary>
    public readonly bool Pointer;

    /// <summary>読んだ値で作る。</summary>
    public NavFrameInput(FocusDirection move, bool submit, bool cancel, bool escapeDown, bool pointer)
    {
        Move = move;
        Submit = submit;
        Cancel = cancel;
        EscapeDown = escapeDown;
        Pointer = pointer;
    }
}

/// <summary>方向キー・パッドの入力を読む（向きの前の状態と連続移動の時計を持つ）。</summary>
internal sealed class NavInputReader
{
    /// <summary>連続移動の時計。</summary>
    private readonly NavRepeatClock _clock = new();
    /// <summary>前のフレームのキー・D-pad の向き。</summary>
    private FocusDirection _digital;
    /// <summary>前のフレームのスティックの向き。</summary>
    private FocusDirection _stick;

    /// <summary>
    /// 1 フレーム分読む。
    /// </summary>
    /// <param name="owner">InputMap を探すアクター（UiNavigator のアクター）。</param>
    /// <param name="options">設定。</param>
    /// <param name="dt">前のフレームからの秒（連続移動の時計。時間の倍率に左右されない秒を渡す）。</param>
    public NavFrameInput Read(GameObject owner, UiNavigationOptions options, float dt)
    {
        var map = owner.GetComponent<InputMap>();
        bool hasMap = map is { IsValid: true };
        bool useMap = hasMap && options.InputSource != NavInputSource.Raw;
        bool useRaw = options.InputSource switch
        {
            NavInputSource.Auto => !hasMap,
            NavInputSource.InputMap => false,
            _ => true,
        };

        // 方向（押している・押し始めた）
        var held = FocusDirectionSet.None;
        var pressed = FocusDirectionSet.None;
        if (useRaw)
        {
            ReadKey(KeyCode.UpArrow, FocusDirectionSet.Up, ref held, ref pressed);
            ReadKey(KeyCode.DownArrow, FocusDirectionSet.Down, ref held, ref pressed);
            ReadKey(KeyCode.LeftArrow, FocusDirectionSet.Left, ref held, ref pressed);
            ReadKey(KeyCode.RightArrow, FocusDirectionSet.Right, ref held, ref pressed);
        }
        var stick = Vector2.Zero;
        if (useMap && map is { } m)
        {
            ReadAction(m, options.UpAction, FocusDirectionSet.Up, ref held, ref pressed);
            ReadAction(m, options.DownAction, FocusDirectionSet.Down, ref held, ref pressed);
            ReadAction(m, options.LeftAction, FocusDirectionSet.Left, ref held, ref pressed);
            ReadAction(m, options.RightAction, FocusDirectionSet.Right, ref held, ref pressed);
            if (!string.IsNullOrEmpty(options.MoveAction)) stick = m.GetVector2(options.MoveAction);
        }
        _digital = NavInputMath.ResolveDigital(held, pressed, _digital);
        _stick = NavInputMath.StickDirection(stick, _stick);
        var move = _clock.Update(NavInputMath.Combine(_digital, _stick), dt);

        // 決定・キャンセル（押した瞬間）
        bool submit = useRaw && (Input.GetKeyDown(KeyCode.Enter) || Input.GetKeyDown(KeyCode.Space));
        bool escape = Input.GetKeyDown(KeyCode.Escape);
        bool cancel = useRaw && escape;
        if (useMap && map is { } a)
        {
            submit |= ActionStarted(a, options.SubmitAction);
            cancel |= ActionStarted(a, options.CancelAction);
        }
        return new NavFrameInput(move, submit, cancel, escape, PointerTouched());
    }

    /// <summary>連続移動を数え直す（文字入力の間。押したままのキーで入力の後に動き出さない）。</summary>
    public void ResetRepeat()
    {
        _clock.Reset();
        _digital = FocusDirection.None;
        _stick = FocusDirection.None;
    }

    /// <summary>素のキーの押している・押し始めた。</summary>
    private static void ReadKey(KeyCode key, FocusDirectionSet flag, ref FocusDirectionSet held, ref FocusDirectionSet pressed)
    {
        bool down = Input.GetKeyDown(key);
        if (down) pressed |= flag;
        if (down || Input.GetKey(key)) held |= flag;
    }

    /// <summary>アクションの押している・押し始めた（名前が空なら読まない）。</summary>
    private static void ReadAction(InputMap map, string name, FocusDirectionSet flag, ref FocusDirectionSet held, ref FocusDirectionSet pressed)
    {
        if (string.IsNullOrEmpty(name)) return;
        bool started = map.GetActionStart(name);
        if (started) pressed |= flag;
        if (started || map.GetAction(name)) held |= flag;
    }

    /// <summary>アクションが成立した瞬間か（名前が空なら false）。</summary>
    private static bool ActionStarted(InputMap map, string name) => !string.IsNullOrEmpty(name) && map.GetActionStart(name);

    /// <summary>指・マウスで触ったか（マウスのどれかのボタンを押した・指が触れ始めた）。</summary>
    private static bool PointerTouched()
    {
        if (Input.GetMouseButtonDown(MouseButton.Left) || Input.GetMouseButtonDown(MouseButton.Right)
            || Input.GetMouseButtonDown(MouseButton.Middle)) return true;
        int count = Input.TouchCount;
        for (int i = 0; i < count; i++)
            if (Input.GetTouch(i).Phase == TouchPhase.Began) return true;
        return false;
    }
}
