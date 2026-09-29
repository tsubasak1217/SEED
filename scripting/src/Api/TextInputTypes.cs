namespace SEED;

// ============================================================
//  TextInputTypes.cs — 文字入力 API（SEED.TextInput）の型（W2-6a。正典は docs/ui_text_input.md §7）
//
//  番号は Rust 側 runtime/src/engine/core/text_input（config.rs の TextInputKind・TextInputAction、session.rs の EVENT_*）と
//  一致させる。アクションの番号は Android の EditorInfo.IME_ACTION_* と同じ。
// ============================================================

/// <summary>入力の種類（ソフトキーボードの種類と、受け付ける文字の絞り込み）。</summary>
public enum TextInputKind
{
    /// <summary>文字（日本語の変換を含む）。Android は文字のキーボード、PC は IME を許可する。</summary>
    Text = 0,
    /// <summary>数字だけ（0〜9。全角の数字は半角へ直し、ほかの文字は捨てる）。Android は数字のキーボード、PC は IME を使わない。</summary>
    Number = 1,
}

/// <summary>完了などのアクション（ソフトキーボードのアクションのボタン・PC の Enter。値は Android の IME_ACTION_*）。</summary>
public enum TextInputAction
{
    /// <summary>決めない（IME に任せる）。</summary>
    Unspecified = 0,
    /// <summary>アクションなし（Enter は何もしない）。</summary>
    None = 1,
    /// <summary>移動。</summary>
    Go = 2,
    /// <summary>検索。</summary>
    Search = 3,
    /// <summary>送信。</summary>
    Send = 4,
    /// <summary>次へ。</summary>
    Next = 5,
    /// <summary>完了（既定）。</summary>
    Done = 6,
    /// <summary>前へ。</summary>
    Previous = 7,
}

/// <summary>入力欄の場の出来事の種類。</summary>
public enum TextInputEventKind
{
    /// <summary>本文が変わった（変換中の文字の変化も含む＝1 文字ごと）。</summary>
    TextChanged = 1,
    /// <summary>本文は同じで、選択・カーソル・変換中の区間だけが変わった。</summary>
    SelectionChanged = 2,
    /// <summary>完了などのアクション（<see cref="TextInputEvent.Action"/>）。</summary>
    Action = 3,
    /// <summary>貼り付けを禁止した欄で貼り付けを止めた。</summary>
    PasteBlocked = 4,
    /// <summary>ソフトキーボードが出た・高さが変わった（<see cref="TextInputEvent.KeyboardHeight"/>）。</summary>
    KeyboardShown = 5,
    /// <summary>ソフトキーボードが隠れた。</summary>
    KeyboardHidden = 6,
}

/// <summary>入力欄の場の出来事。</summary>
public readonly struct TextInputEvent
{
    /// <summary>種類。</summary>
    public TextInputEventKind Kind { get; }
    /// <summary>添えの値（アクションの番号・キーボードの高さの画素）。</summary>
    private readonly int _argument;

    internal TextInputEvent(TextInputEventKind kind, int argument)
    {
        Kind = kind;
        _argument = argument;
    }

    /// <summary>アクション（<see cref="TextInputEventKind.Action"/> のとき）。</summary>
    public TextInputAction Action => Kind == TextInputEventKind.Action ? (TextInputAction)_argument : TextInputAction.Unspecified;

    /// <summary>キーボードの高さ（画面の画素。<see cref="TextInputEventKind.KeyboardShown"/> のとき）。</summary>
    public int KeyboardHeight => Kind == TextInputEventKind.KeyboardShown ? _argument : 0;

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        TextInputEventKind.Action => $"Action({Action})",
        TextInputEventKind.KeyboardShown => $"KeyboardShown({KeyboardHeight}px)",
        _ => Kind.ToString(),
    };
}

/// <summary>入力欄にフォーカスを当てるときの設定（<see cref="TextInput.Begin"/>）。</summary>
public sealed class TextInputOptions
{
    /// <summary>入力の種類（既定は文字）。</summary>
    public TextInputKind Kind { get; init; } = TextInputKind.Text;
    /// <summary>完了などのアクション（既定は完了）。</summary>
    public TextInputAction Action { get; init; } = TextInputAction.Done;
    /// <summary>最大の長さ（見た目の文字＝書記素の数。0 以下 = 制限なし。変換中は超えてよく、確定したら切り詰める）。</summary>
    public int MaxLength { get; init; }
    /// <summary>貼り付けを許すか（false: PC の Ctrl+V・Shift+Insert を捨て、Android は IME からの一度の大きな挿入を戻す）。</summary>
    public bool AllowPaste { get; init; } = true;
    /// <summary>コピー・切り取りを許すか（false: PC の Ctrl+C・Ctrl+X を捨てる）。</summary>
    public bool AllowCopy { get; init; } = true;

    /// <summary>旗: 貼り付けを禁止（Rust 側 config.rs の FLAG_DISALLOW_PASTE）。</summary>
    internal const int FlagDisallowPaste = 1;
    /// <summary>旗: コピーを禁止（Rust 側 FLAG_DISALLOW_COPY）。</summary>
    internal const int FlagDisallowCopy = 2;

    /// <summary>FFI の旗。</summary>
    internal int Flags => (AllowPaste ? 0 : FlagDisallowPaste) | (AllowCopy ? 0 : FlagDisallowCopy);
}

/// <summary>入力欄の場の状態（添字は string の添字＝UTF-16 の単位）。</summary>
public readonly struct TextInputState
{
    /// <summary>「無い」の添字（変換中の区間が無い）。</summary>
    public const int NoIndex = -1;

    /// <summary>本文（変換中の文字も含む）。</summary>
    public string Text { get; }
    /// <summary>選択の起点。</summary>
    public int SelectionStart { get; }
    /// <summary>選択の動く端（カーソル）。</summary>
    public int SelectionEnd { get; }
    /// <summary>変換中の区間の始め（無ければ -1）。</summary>
    public int CompositionStart { get; }
    /// <summary>変換中の区間の終わり（無ければ -1）。</summary>
    public int CompositionEnd { get; }
    /// <summary>版（状態が変わるたびに増える）。</summary>
    public int Revision { get; }

    internal TextInputState(string text, int selectionStart, int selectionEnd, int compositionStart, int compositionEnd, int revision)
    {
        Text = text;
        SelectionStart = selectionStart;
        SelectionEnd = selectionEnd;
        CompositionStart = compositionStart;
        CompositionEnd = compositionEnd;
        Revision = revision;
    }

    /// <summary>カーソルの位置（選択の動く端）。</summary>
    public int Caret => SelectionEnd;
    /// <summary>選択の前の端。</summary>
    public int SelectionMin => System.Math.Min(SelectionStart, SelectionEnd);
    /// <summary>選択の後ろの端。</summary>
    public int SelectionMax => System.Math.Max(SelectionStart, SelectionEnd);
    /// <summary>選択があるか。</summary>
    public bool HasSelection => SelectionStart != SelectionEnd;
    /// <summary>変換中か（日本語の未確定の文字がある）。</summary>
    public bool IsComposing => CompositionStart >= 0 && CompositionEnd > CompositionStart;
}
