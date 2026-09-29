using System;
using System.Collections.Generic;

namespace SEED;

// ============================================================
//  TextInput.cs — 文字入力の受け口（W2-6a。正典は docs/ui_text_input.md §7）
//
//  入力欄（SEED.UI.TextField など）がフォーカスを受けたときに Begin で「場」を始め、毎フレーム TakeEvents・TryGetState で
//  変化を読み、フォーカスを外すときに End する。場は同時に 1 つ（新しく Begin すると前の場は終わる）。
//  本文・選択・変換中の区間はエンジンが持ち、プラットフォームの差（PC の IME とキー、Android の IME）はエンジンが吸収する。
//  添字は string の添字（UTF-16 の単位）。
// ============================================================

/// <summary>
/// 文字入力の受け口（入力欄の場）。部品を作らずに直接使うときの窓口（ふつうは <c>SEED.UI.TextField</c> を使う）。
/// <example>
/// <code>
/// int session = TextInput.Begin(new TextInputOptions { Kind = TextInputKind.Number }, "30");
/// // 毎フレーム
/// foreach (var e in TextInput.TakeEvents(session))
///     if (e.Kind == TextInputEventKind.TextChanged && TextInput.TryGetState(session, out var s)) Debug.Log(s.Text);
/// // フォーカスを外すとき
/// TextInput.End(session);
/// </code>
/// </example>
/// </summary>
public static class TextInput
{
    /// <summary>添字の「末尾」（Begin・SetText の選択の既定）。</summary>
    public const int EndOfText = -1;

    /// <summary>1 回に取り出す出来事の上限（Rust 側の上限 256 より小さく、1 フレームで溜まる数より十分大きい）。</summary>
    private const int MaxEventsPerTake = 64;

    // GetState の outInts の並び（Rust 側 text_input_bridge.rs の get_state と一致させる）
    private const int StateSelectionStart = 0;
    private const int StateSelectionEnd = 1;
    private const int StateCompositionStart = 2;
    private const int StateCompositionEnd = 3;
    private const int StateRevision = 4;
    // 出来事・キーボードの outInts の並び（番号・添え ／ 見えているか・高さ）
    private const int PairFirst = 0;
    private const int PairSecond = 1;

    /// <summary>
    /// 場を始める（入力欄にフォーカスを当てる。ソフトキーボードを出し、PC は IME を許可する。前の場は終わる）。
    /// </summary>
    /// <param name="options">設定（null なら既定＝文字・完了）。</param>
    /// <param name="text">初めの本文（数字だけの欄は数字以外を捨てる）。</param>
    /// <param name="selectionStart">選択の起点（-1 = 末尾）。</param>
    /// <param name="selectionEnd">選択の動く端＝カーソル（-1 = 末尾）。</param>
    /// <returns>場の番号（1 以上。スクリプトの FFI が使えなければ 0）。</returns>
    public static int Begin(TextInputOptions? options, string text, int selectionStart = EndOfText, int selectionEnd = EndOfText)
    {
        options ??= new TextInputOptions();
        // 並びは Rust 側 text_input_bridge.rs の BEGIN_*（種類・アクション・最大の長さ・旗・選択の起点・動く端）
        Span<int> ints = stackalloc int[]
        {
            (int)options.Kind, (int)options.Action, Math.Max(0, options.MaxLength), options.Flags, selectionStart, selectionEnd,
        };
        int id =ScriptHost.TextInputCall(ScriptHost.TextInputOpBegin, 0, ints, text, Span<int>.Empty, Span<byte>.Empty);
        return id > 0 ? id : 0;
    }

    /// <summary>場を終える（キーボードを隠し、PC の IME の許可を外す。変換中の文字はそのまま確定扱い）。今の場でなければ false。</summary>
    public static bool End(int session) => Simple(ScriptHost.TextInputOpEnd, session);

    /// <summary>今の場の番号（無ければ 0）。</summary>
    public static int ActiveSession
    {
        get
        {
            int id = ScriptHost.TextInputCall(ScriptHost.TextInputOpActive, 0, ReadOnlySpan<int>.Empty, null, Span<int>.Empty, Span<byte>.Empty);
            return id > 0 ? id : 0;
        }
    }

    /// <summary>その場が今の場か（別の欄が Begin すると false になる）。</summary>
    public static bool IsActive(int session) => session > 0 && ActiveSession == session;

    /// <summary>場の状態を読む（今の場でなければ false）。</summary>
    public static bool TryGetState(int session, out TextInputState state)
    {
        state = default;
        Span<int> ints = stackalloc int[ScriptHost.TextInputStateInts];
        if (!ScriptHost.TextInputGetState(session, out var text, ints)) return false;
        // 並びは Rust 側 get_state（選択の起点・動く端・変換の始め・終わり・版）
        state = new TextInputState(text, ints[StateSelectionStart], ints[StateSelectionEnd],
            ints[StateCompositionStart], ints[StateCompositionEnd], ints[StateRevision]);
        return true;
    }

    /// <summary>本文と選択を置く（変換中の区間は外れる。欄の決まり〈数字だけ・最大の長さ〉も当たる）。</summary>
    public static bool SetText(int session, string text, int selectionStart = EndOfText, int selectionEnd = EndOfText)
    {
        Span<int> ints = stackalloc int[] { selectionStart, selectionEnd };
        return ScriptHost.TextInputCall(ScriptHost.TextInputOpSetText, session, ints, text, Span<int>.Empty, Span<byte>.Empty)
               == ScriptHost.TextInputResultOk;
    }

    /// <summary>選択（カーソル）を置く（start == end でカーソルだけ）。</summary>
    public static bool SetSelection(int session, int start, int end)
    {
        Span<int> ints = stackalloc int[] { start, end };
        return ScriptHost.TextInputCall(ScriptHost.TextInputOpSetSelection, session, ints, null, Span<int>.Empty, Span<byte>.Empty)
               == ScriptHost.TextInputResultOk;
    }

    /// <summary>積まれた出来事を古い順に取り出す（今の場でなければ空）。</summary>
    public static List<TextInputEvent> TakeEvents(int session)
    {
        var events = new List<TextInputEvent>();
        TakeEvents(session, events);
        return events;
    }

    /// <summary>積まれた出来事を古い順に <paramref name="into"/> へ足す（毎フレーム呼ぶときに入れ物を使い回す）。足した数。</summary>
    public static int TakeEvents(int session, List<TextInputEvent> into)
    {
        Span<int> buffer = stackalloc int[MaxEventsPerTake * ScriptHost.TextInputEventInts];
        int total = 0;
        while (true)
        {
            int count = ScriptHost.TextInputCall(ScriptHost.TextInputOpTakeEvents, session, ReadOnlySpan<int>.Empty, null, buffer, Span<byte>.Empty);
            if (count <= 0) return total;
            for (int i = 0; i < count; i++)
            {
                int at = i * ScriptHost.TextInputEventInts;
                into.Add(new TextInputEvent((TextInputEventKind)buffer[at + PairFirst], buffer[at + PairSecond]));
            }
            total += count;
            if (count < MaxEventsPerTake) return total;
        }
    }

    /// <summary>ソフトキーボードを出す（利用者が IME を閉じた後に欄をもう一度タップしたとき）。</summary>
    public static bool ShowKeyboard(int session) => Simple(ScriptHost.TextInputOpShowKeyboard, session);

    /// <summary>ソフトキーボードを隠す（フォーカスは残す）。</summary>
    public static bool HideKeyboard(int session) => Simple(ScriptHost.TextInputOpHideKeyboard, session);

    /// <summary>
    /// カーソルの矩形を知らせる（画面の画素。PC の IME の候補窓がこの矩形を避けて出る。Android は使わない）。
    /// </summary>
    public static bool SetCaretRect(int session, Rect screenRect)
    {
        Span<int> ints = stackalloc int[]
        {
            (int)MathF.Round(screenRect.x), (int)MathF.Round(screenRect.y),
            (int)MathF.Ceiling(screenRect.width), (int)MathF.Ceiling(screenRect.height),
        };
        return ScriptHost.TextInputCall(ScriptHost.TextInputOpSetCaretRect, session, ints, null, Span<int>.Empty, Span<byte>.Empty)
               == ScriptHost.TextInputResultOk;
    }

    /// <summary>ソフトキーボードが見えているか（PC は模擬〈SEED_SIM_KEYBOARD_HEIGHT〉があるときだけ true）。</summary>
    public static bool KeyboardVisible => KeyboardHeight > 0f;

    /// <summary>
    /// ソフトキーボードの高さ（画面の下端から。画面の画素＝<see cref="Screen.Height"/>・<c>CanvasTransform.LayoutRect</c> と同じ座標。
    /// 見えていなければ 0）。キーボードは画面に重なる（描画面は縮まない）ので、入力欄を避けさせるのはスクリプト。
    /// </summary>
    public static float KeyboardHeight
    {
        get
        {
            Span<int> ints = stackalloc int[ScriptHost.TextInputKeyboardInts];
            int result = ScriptHost.TextInputCall(ScriptHost.TextInputOpKeyboard, 0, ReadOnlySpan<int>.Empty, null, ints, Span<byte>.Empty);
            return result == ScriptHost.TextInputResultOk && ints[PairFirst] != 0 ? ints[PairSecond] : 0f;
        }
    }

    /// <summary>引数の無い op（End・ShowKeyboard・HideKeyboard）。</summary>
    private static bool Simple(int op, int session)
        => ScriptHost.TextInputCall(op, session, ReadOnlySpan<int>.Empty, null, Span<int>.Empty, Span<byte>.Empty)
           == ScriptHost.TextInputResultOk;
}
