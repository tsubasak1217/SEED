using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  TextField.cs — 1 行の入力欄（W2-6b。正典は docs/ui_text_input.md §8）
//
//  【プレハブ】templates/ui/prefabs/text_field.actor（文字の欄）・number_input.actor（数値の欄。幅 112・数字は中央で大きめ）
//      TextField（Sprite = 枠〈角丸・枠線〉・CanvasGesture〈タップ・長押し〉・このスクリプト）
//      └─ Viewport（透明の Sprite・CanvasClip。左右の余白の内側。はみ出した文字を切る）
//         ├─ Selection（Sprite。選択の背景）
//         ├─ Content（Text。本文。記法は逃がして描く）
//         ├─ Composition（Sprite。変換中の文字の下線）
//         ├─ Caret（Sprite。点滅のカーソル）
//         └─ Placeholder（Text。例の文〈本文が空のとき〉）
//  【受け口】本文・選択・変換中の区間はエンジンの文字入力（SEED.TextInput）が持ち、フォーカスの間だけ場（session）を持つ。
//    毎フレーム出来事を取り出し、変わったら状態を読み直して見た目を当てる。PC は IME（日本語の変換）とキー、Android は IME
//    （文字・数字のキーボード・完了）がエンジンの側で吸収される。
//  【フォーカス】UiFocus（W2-7）の相手（IFocusable）。欄のタップでフォーカス（タップの位置へカーソル）・長押しで全選択、
//    欄の外のタップ（押して離すまで動かさない）でフォーカスを外す、完了（Done）でフォーカスを外す（UnfocusOnDone）。
//    戻る（BackDispatcher の Focus の層。IBackConsumer）: 必ずフォーカスを外す。キーボードが出ていれば〈または PC なら〉受ける。
//    Android でキーボードを閉じた後の戻るは、後ろに受ける層（ダイアログ・シート・覆い・画面のスタック）があれば回し（ダイアログは 2 回目で閉じる）、
//    無い根では受ける（2 回目はフォーカスを外すだけ・3 回目で背面へ。TextFieldBackPolicy）。欄の外のタップは OS に取り消された指を数えない
//    （戻るのジェスチャーの指。OutsideTapTracker）。
//  【見た目】状態 → 見た目は TextFieldLooks（純粋な計算）、置き場は TextFieldLayout（純粋な計算）。点滅は motion.caret_blink ごとに
//    切り替え、次の切り替えの時刻を Redraw.RequestAfter で申告する（render_policy: on_demand でも点滅する）。
//  【キーボードを避ける】KeyboardAvoider（スクロールの窓の末尾の余白と送り・ダイアログの持ち上げ）。
// ============================================================

/// <summary>1 行の入力欄。</summary>
public sealed class TextField : UiWidget, IFocusable, IBackConsumer
{
    // ── 子の名前 ──
    private const string ViewportChild = "Viewport";
    private const string ContentChild = "Viewport/Content";
    private const string PlaceholderChild = "Viewport/Placeholder";
    private const string CaretChild = "Viewport/Caret";
    private const string SelectionChild = "Viewport/Selection";
    private const string CompositionChild = "Viewport/Composition";

    /// <summary>Text の揃え（左）。</summary>
    private const string AlignLeft = "left";
    /// <summary>Text の揃え（中央）。</summary>
    private const string AlignCenter = "center";
    /// <summary>Text の縦の揃え（中央）。</summary>
    private const string AlignMiddle = "middle";
    /// <summary>中央の半分。</summary>
    private const float Half = 0.5f;
    /// <summary>カーソルの高さ（文字の大きさに対する倍率。行の高さの目安）。</summary>
    private const float CaretHeightEm = 1.25f;
    /// <summary>欄の外のタップとみなす指の動きの上限（dp。W2-2 のドラッグの閾値 8 dp と同じ）。</summary>
    private const float OutsideTapSlopDp = 8f;
    /// <summary>カーソルの矩形を送り直す差（画素。丸めの差では送らない）。</summary>
    private const float CaretRectEpsilon = 0.5f;
    /// <summary>添字の「無い」。</summary>
    private const int NoIndex = -1;

    // ── 設定（プレハブ・インスペクタ）──

    /// <summary>本文（フォーカスが無いときに見せる値。フォーカスの間はエンジンの本文に追従する）。</summary>
    [SerializeField(Label = "文字")]
    public string Text = "";
    /// <summary>例の文（本文が空のときに薄く出す。例「例：田中太郎」）。</summary>
    [SerializeField(Label = "例の文")]
    public string Placeholder = "";
    /// <summary>入力の種類（文字・数字だけ）。</summary>
    [SerializeField(Label = "種類")]
    public TextInputKind Kind = TextInputKind.Text;
    /// <summary>完了などのアクション（キーボードのボタン・PC の Enter）。</summary>
    [SerializeField(Label = "完了のアクション")]
    public TextInputAction Action = TextInputAction.Done;
    /// <summary>最大の長さ（書記素の数。0 = 制限なし）。</summary>
    [SerializeField(Label = "最大の長さ")]
    public int MaxLength;
    /// <summary>貼り付けを許すか（起床確認の文字入力は false）。</summary>
    [SerializeField(Label = "貼り付けを許す")]
    public bool AllowPaste = true;
    /// <summary>コピー・切り取りを許すか。</summary>
    [SerializeField(Label = "コピーを許す")]
    public bool AllowCopy = true;
    /// <summary>文字の揃え（数値の欄は中央）。</summary>
    [SerializeField(Label = "揃え")]
    public TextFieldAlign Align = TextFieldAlign.Left;
    /// <summary>文字の大きさのトークン（既定 text.field。数値の欄は text.field_number）。</summary>
    [SerializeField(Label = "文字の大きさ")]
    public string TextSize = TextFieldTokens.TextField;
    /// <summary>塗りのある種類（既定は枠だけ）。</summary>
    [SerializeField(Label = "塗り")]
    public bool Filled;
    /// <summary>フォーカスを得たら全選択する（数値の欄で打ち直しやすく）。</summary>
    [SerializeField(Label = "フォーカスで全選択")]
    public bool SelectAllOnFocus;
    /// <summary>完了（Done）でフォーカスを外す（キーボードも隠れる）。</summary>
    [SerializeField(Label = "完了でフォーカスを外す")]
    public bool UnfocusOnDone = true;
    /// <summary>キーボードを避ける（スクロールの窓・ダイアログ）。</summary>
    [SerializeField(Label = "キーボードを避ける")]
    public bool AvoidKeyboard = true;
    /// <summary>エラーの見た目（利用者の入力の誤りを見せる。スクリプトが立てる）。</summary>
    [SerializeField(Label = "エラー")]
    public bool HasError;
    /// <summary>
    /// 左右の内側の余白（キャンバスの単位。負 = テーマの size.field_padding。2026-10-02）。幅の狭い数値の欄で余白を詰める
    /// （以前はテーマ全体で 1 つで、幅 112 の欄に 7 桁が入らなかった。Wake or Pay の W3-2b (5)）。
    /// </summary>
    [SerializeField(Label = "左右の余白")]
    public float Padding = TextFieldLayout.ThemePadding;
    /// <summary>
    /// 選択を許すか（2026-10-02。既定 true。false で長押しの全選択・フォーカスで全選択・キーボードや IME の選択をさせない。
    /// Flutter の enableInteractiveSelection: false。Wake or Pay の W3-2b (6)。決め方は TextFieldSelectionPolicy）。
    /// 2026-10-03: false の欄はフォーカスで始める場へコピー・切り取りの禁止も渡す（AllowCopy によらない。畳む前の Ctrl+C も写さない）。
    /// </summary>
    [SerializeField(Label = "選択を許す")]
    public bool AllowSelection = true;

    // ── 知らせ ──

    /// <summary>本文が変わった（打鍵・変換中の文字の変化・貼り付け・スクリプトの SetText のたび。新しい本文）。</summary>
    public event Action<TextField, string>? TextChanged;
    /// <summary>完了などのアクション（キーボードのボタン・PC の Enter）。</summary>
    public event Action<TextField, TextInputAction>? Submitted;
    /// <summary>フォーカスを得た・失った。</summary>
    public event Action<TextField, bool>? FocusChanged;
    /// <summary>貼り付けを禁止した欄で貼り付けを止めた。</summary>
    public event Action<TextField>? PasteBlocked;

    // ── 状態 ──

    /// <summary>文字入力の場（フォーカスの間だけ。0 = 無い）。</summary>
    private int _session;
    /// <summary>最後に読んだ状態（フォーカスの間。無いときは本文の末尾にカーソル）。</summary>
    private TextInputState _state;
    /// <summary>カーソルの位置の並び（見せている本文の長さ + 1）。</summary>
    private float[] _stops = { 0f };
    /// <summary>横のスクロール（はみ出すとき）。</summary>
    private float _scroll;
    /// <summary>文字の左端（内側の枠の中。最後に当てた値。タップの位置の計算に使う）。</summary>
    private float _textStartX;
    /// <summary>左右の内側の余白（最後に当てた値）。</summary>
    private float _padding;
    /// <summary>点滅を数え直してからの秒。</summary>
    private float _blinkClock;
    /// <summary>カーソルが見える相か。</summary>
    private bool _caretOn = true;
    /// <summary>フォーカスを得るときに置くカーソル（タップの位置。無ければ -1）。</summary>
    private int _pendingCaret = NoIndex;
    /// <summary>フォーカスを得るときに全選択するか（長押し）。</summary>
    private bool _pendingSelectAll;
    /// <summary>欄の外のタップの見分け（押してから離すまで。OS に取り消された指は数えない）。</summary>
    private readonly OutsideTapTracker _outsideTap = new();
    /// <summary>最後に送ったカーソルの矩形（画面の画素）。</summary>
    private Rect _sentCaretRect = Rect.Zero;
    /// <summary>キーボードの避け方。</summary>
    private KeyboardAvoider? _avoider;
    /// <summary>取り出した出来事の入れ物（毎フレーム使い回す）。</summary>
    private readonly List<TextInputEvent> _events = new();
    /// <summary>欄の大きさ（レイアウトの大きさ）の見張り（変わったら中身を置き直す。2026-10-02）。</summary>
    private LayoutSizeWatch _fieldSize;

    /// <summary>フォーカスがあるか（文字入力の場を持っている）。</summary>
    public bool IsFocused => _session != 0;

    /// <summary>変換中か（日本語の未確定の文字がある）。</summary>
    public bool IsComposing => IsFocused && _state.IsComposing;

    /// <summary>今の状態（フォーカスが無ければ本文の末尾にカーソル）。</summary>
    public TextInputState State => _state;

    /// <inheritdoc />
    public GameObject FocusOwner => gameObject;

    // ── 操作 ──

    /// <summary>フォーカスを当てる（ソフトキーボードが出る）。今の範囲が後ろなら、前に出たときに当たる。</summary>
    public void Focus()
    {
        if (!IsEnabled || IsFocused) return;
        UiFocus.Request(this);
    }

    /// <summary>フォーカスを外す（キーボードが隠れる。変換中の文字はそのまま確定扱い）。</summary>
    public void Unfocus()
    {
        if (!IsFocused) return;
        UiFocus.Release(this);
        // 範囲の外から外されたとき（相手でなかった）にも場を閉じる
        if (IsFocused) EndSession();
    }

    /// <summary>
    /// 本文を置く（フォーカスの間は入力中の本文も差し替える。欄の決まり〈数字だけ・最大の長さ〉はフォーカスの間だけ当たる）。
    /// </summary>
    /// <param name="text">本文。</param>
    /// <param name="notify">TextChanged を知らせるか（既定は知らせない＝スクリプトからの設定）。</param>
    public void SetText(string text, bool notify = false)
    {
        text ??= string.Empty;
        if (IsFocused)
        {
            TextInput.SetText(_session, text);
            // 出来事（TextChanged）は次のフレームに届くが、スクリプトからの設定は知らせない
            ReadState();
            Text = _state.Text;
        }
        else
        {
            Text = text;
            _state = RestingState(text);
        }
        Refresh();
        if (notify) TextChanged?.Invoke(this, Text);
    }

    /// <summary>
    /// フォーカスが無いときだけ本文を置く（スライダなど外の値との双方向で、打っている最中の欄を外の値で上書きしない）。置いたら true。
    /// </summary>
    public bool SetTextUnlessFocused(string text)
    {
        if (IsFocused) return false;
        if (Text == text) return true;
        SetText(text);
        return true;
    }

    /// <summary>エラーの見た目を切り替える（枠とカーソルが color.error になる）。</summary>
    public void SetError(bool error)
    {
        if (HasError == error) return;
        HasError = error;
        Refresh();
    }

    /// <summary>すべてを選ぶ（フォーカスの間だけ。選択を許さない欄〈AllowSelection = false〉では何もしない）。</summary>
    public void SelectAll()
    {
        if (!IsFocused || !AllowSelection) return;
        TextInput.SetSelection(_session, 0, _state.Text.Length);
        ReadState();
        Refresh();
    }

    /// <summary>左右の内側の余白を変える（2026-10-02。負 = テーマの size.field_padding。見た目も変える）。</summary>
    /// <param name="padding">余白（キャンバスの単位）。</param>
    public void SetPadding(float padding)
    {
        if (Padding == padding) return;
        Padding = padding;
        Refresh();
    }

    /// <summary>
    /// 選択を許すかを変える（2026-10-02。false にすると今の選択も畳む）。コピー・切り取りの禁止（エンジンの場の設定）は場を始めるときに
    /// 渡すので、フォーカスの間に変えたときは次のフォーカスから効く（2026-10-03。今の選択はこの呼び出しで畳む）。
    /// </summary>
    /// <param name="allow">選択を許すか。</param>
    public void SetAllowSelection(bool allow)
    {
        if (AllowSelection == allow) return;
        AllowSelection = allow;
        if (CollapseDisallowedSelection()) Refresh();
    }

    // ── 部品の土台 ──

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        _avoider = new KeyboardAvoider(gameObject);
        _state = RestingState(Text);
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy()
    {
        if (IsFocused) EndSession();
        UiFocus.Release(this);
        _avoider?.Clear();
    }

    /// <inheritdoc />
    public void OnFocusChanged(bool focused)
    {
        if (focused && !IsFocused) BeginSession();
        else if (!focused && IsFocused) EndSession();
        else return;
        Refresh();
        FocusChanged?.Invoke(this, focused);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 必ずフォーカスを外し、受けるかは TextFieldBackPolicy で決める: キーボードが出ていた・PC（模擬を含む。閉じるソフトキーボードが無い）・
    /// 後ろに戻るを受ける層が無い（根。回すとアプリが背面へ回る）なら受ける。Android でキーボードを閉じた後の戻るは、後ろに層
    /// （ダイアログ・シート・覆い・画面のスタック）があればそこへ回す（ダイアログは 2 回目の戻るで閉じる）。
    /// </remarks>
    public bool HandleBack()
    {
        if (!IsFocused) return false;
        bool keyboardShown = TextInput.KeyboardVisible;
        bool device = SEED.Platform.Platform.IsSupported && !SEED.Platform.Platform.IsSimulated;
        Unfocus();
        return TextFieldBackPolicy.Consumes(keyboardShown, device, BackDispatcher.WouldHandleAfterFocus());
    }

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled) return;
        int caret = CaretIndexAtLocal(e.LocalPosition.x);
        if (!IsFocused)
        {
            _pendingCaret = caret;
            Focus();
            return;
        }
        // フォーカスの間のタップ: カーソルを移し、閉じられていたキーボードを出し直す（キーボードを避け直す）
        TextInput.SetSelection(_session, caret, caret);
        if (!TextInput.KeyboardVisible) TextInput.ShowKeyboard(_session);
        _avoider?.Invalidate();
        ResetBlink();
        ReadState();
        Refresh();
    }

    /// <inheritdoc />
    public override void OnGestureLongPress(GestureEvent e)
    {
        if (!IsEnabled) return;
        if (IsFocused)
        {
            // 選択を許さない欄の長押しは何もしない（カーソルはタップで動かす。2026-10-02）
            if (TextFieldSelectionPolicy.LongPressSelectsAll(AllowSelection)) SelectAll();
            return;
        }
        // 選択を許さない欄は、長押しでもタップと同じ（押した位置へカーソルを置いてフォーカス）
        if (TextFieldSelectionPolicy.LongPressSelectsAll(AllowSelection)) _pendingSelectAll = true;
        else _pendingCaret = CaretIndexAtLocal(e.LocalPosition.x);
        Focus();
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 欄の大きさ（レイアウトの大きさ。前のフレームの描画の値）が変わったら中身を置き直す（2026-10-02。コンテナが幅を伸ばした・
        // ダイアログが入力の枠の幅に合わせた。以前は次に描き直す出来事まで古い幅のままだった）
        WatchFieldSize();
        if (!IsFocused) return;
        if (!TextInput.IsActive(_session))
        {
            // 場を外から失った（別の場が始まった・Play の区切り）: フォーカスを手放す
            _session = 0;
            UiFocus.Release(this);
            _avoider?.Clear();
            Refresh();
            FocusChanged?.Invoke(this, false);
            return;
        }
        PumpEvents();
        if (!IsFocused) return;
        UpdateBlink();
        UpdateOutsideTap();
        // 欄の外のタップでフォーカスを外したフレームは、避け方を当て直さない（キーボードが隠れるのはフレームの末尾なので、
        // ここで当て直すと戻したばかりの末尾の余白をまた置いてしまう）
        if (!IsFocused) return;
        float gapPx = Theme.Number(TextFieldTokens.SizeKeyboardGap) * PxPerUnit();
        _avoider?.Update(AvoidKeyboard, TextInput.KeyboardHeight, gapPx);
    }

    // ── 場 ──

    /// <summary>場を始める（フォーカスを得た）。</summary>
    private void BeginSession()
    {
        // コピー・切り取りは「コピーを許す」かつ「選択を許す」欄だけ（選択を許さない欄は、C# が選択を畳む前に届いた Ctrl+A → Ctrl+C も
        // エンジンが捨てる。2026-10-03。レビュー #10。TextFieldSelectionPolicy.SessionAllowsCopy）
        var options = new TextInputOptions
        {
            Kind = Kind, Action = Action, MaxLength = MaxLength, AllowPaste = AllowPaste,
            AllowCopy = TextFieldSelectionPolicy.SessionAllowsCopy(AllowCopy, AllowSelection),
        };
        int start = TextInput.EndOfText, end = TextInput.EndOfText;
        // 全選択は選択を許す欄だけ（TextFieldSelectionPolicy。2026-10-02）
        if (TextFieldSelectionPolicy.SelectAllOnFocus(AllowSelection, SelectAllOnFocus, _pendingSelectAll))
        {
            start = 0;
            end = Text.Length;
        }
        else if (_pendingCaret >= 0)
        {
            start = _pendingCaret;
            end = _pendingCaret;
        }
        _pendingCaret = NoIndex;
        _pendingSelectAll = false;
        _session = TextInput.Begin(options, Text, start, end);
        if (_session == 0) return;
        _scroll = 0f;
        _sentCaretRect = Rect.Zero;
        _outsideTap.Reset();
        _avoider?.Invalidate();
        ResetBlink();
        ReadState();
        // 初めの本文にも欄の決まり（数字だけ等）が当たるので、本文を揃える
        if (_state.Text != Text)
        {
            Text = _state.Text;
            TextChanged?.Invoke(this, Text);
        }
    }

    /// <summary>場を終える（フォーカスを失った）。最後の本文を残す。</summary>
    private void EndSession()
    {
        if (TextInput.TryGetState(_session, out var last) && last.Text != Text)
        {
            Text = last.Text;
            TextChanged?.Invoke(this, Text);
        }
        TextInput.End(_session);
        _session = 0;
        _state = RestingState(Text);
        _scroll = 0f;
        _avoider?.Clear();
    }

    /// <summary>フォーカスの無いときの状態（本文の末尾にカーソル・変換なし）。</summary>
    private static TextInputState RestingState(string text)
        => new(text ?? string.Empty, (text ?? string.Empty).Length, (text ?? string.Empty).Length,
            TextInputState.NoIndex, TextInputState.NoIndex, 0);

    /// <summary>エンジンの状態を読み直す。</summary>
    private void ReadState()
    {
        if (TextInput.TryGetState(_session, out var state)) _state = state;
    }

    /// <summary>
    /// 選択を許さない欄に選択があれば、カーソルの位置（選択の動いた端）へ畳む（2026-10-02。TextFieldSelectionPolicy）。畳んだら true。
    /// </summary>
    private bool CollapseDisallowedSelection()
    {
        if (!IsFocused || TextFieldSelectionPolicy.CollapseTo(AllowSelection, _state) is not int caret) return false;
        TextInput.SetSelection(_session, caret, caret);
        ReadState();
        return true;
    }

    /// <summary>欄の大きさ（レイアウトの大きさ）が変わったら見た目を作り直す（2026-10-02）。</summary>
    private void WatchFieldSize()
    {
        if (gameObject.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return;
        if (_fieldSize.Update(t.LayoutSize)) Refresh();
    }

    /// <summary>出来事を取り出して、本文・アクション・貼り付けの禁止を知らせる。</summary>
    private void PumpEvents()
    {
        _events.Clear();
        if (TextInput.TakeEvents(_session, _events) == 0) return;
        bool changed = false;
        foreach (var e in _events)
        {
            switch (e.Kind)
            {
                case TextInputEventKind.TextChanged:
                case TextInputEventKind.SelectionChanged:
                    changed = true;
                    break;
                case TextInputEventKind.PasteBlocked:
                    PasteBlocked?.Invoke(this);
                    break;
                case TextInputEventKind.KeyboardShown:
                    _avoider?.Invalidate();
                    break;
            }
        }
        if (changed)
        {
            ReadState();
            // 選択を許さない欄: キーボード・IME が作った選択はカーソルの位置へ畳む（2026-10-02）
            CollapseDisallowedSelection();
            ResetBlink();
            Refresh();
            if (_state.Text != Text)
            {
                Text = _state.Text;
                TextChanged?.Invoke(this, Text);
            }
        }
        foreach (var e in _events)
        {
            if (e.Kind != TextInputEventKind.Action) continue;
            Submitted?.Invoke(this, e.Action);
            if (UnfocusOnDone && e.Action == TextInputAction.Done) Unfocus();
            if (!IsFocused) break;
        }
    }

    // ── 点滅・欄の外のタップ ──

    /// <summary>点滅を見える側から数え直す（打鍵・移動のたび）。</summary>
    private void ResetBlink()
    {
        _blinkClock = 0f;
        _caretOn = true;
    }

    /// <summary>点滅を進め、切り替わったら描き直し、次の切り替えの時刻を申告する。</summary>
    private void UpdateBlink()
    {
        float period = Theme.Number(TextFieldTokens.MotionCaretBlink);
        _blinkClock += Time.UnscaledDeltaTime;
        bool on = TextFieldLayout.CaretVisible(_blinkClock, period);
        if (on != _caretOn)
        {
            _caretOn = on;
            ApplyCaretVisibility();
            Redraw.Request();
        }
        float next = TextFieldLayout.UntilBlinkToggle(_blinkClock, period);
        if (float.IsFinite(next)) Redraw.RequestAfter(next);
    }

    /// <summary>
    /// 欄の外を押して、動かさずに離したらフォーカスを外す（別の欄・ボタンのタップはそちらが先に受ける）。
    /// OS に取り消された指（Android の戻るのジェスチャー・通知の引き下ろしが画面の端の指を奪ったとき）はタップと数えない
    /// （OutsideTapTracker。2026-09-30 の実機で、戻るのジェスチャーの指が欄の外のタップになり、戻るキーより先にフォーカスが外れた）。
    /// </summary>
    private void UpdateOutsideTap()
    {
        bool pressed = Input.GetMouseButtonDown(MouseButton.Left);
        bool released = Input.GetMouseButtonUp(MouseButton.Left);
        if (!pressed && !released) return;
        var position = Input.MousePosition;
        var input = new OutsideTapInput(pressed, released, position, ScreenRect().Contains(position), released && AnyTouchCanceled());
        float slopPx = OutsideTapSlopDp * Math.Max(Screen.DpScale, float.Epsilon);
        if (_outsideTap.Update(input, slopPx)) Unfocus();
    }

    /// <summary>
    /// このフレームに OS に取り消された指があるか（Android は指 0 がマウスの左ボタンを動かし、指の取り消しは左ボタンの離しと同じフレームに
    /// TouchPhase.Canceled として一覧に残る。PC のマウスの指は取り消されない）。
    /// </summary>
    private static bool AnyTouchCanceled()
    {
        int count = Input.TouchCount;
        for (int i = 0; i < count; i++)
        {
            if (Input.GetTouch(i).Phase == TouchPhase.Canceled) return true;
        }
        return false;
    }

    // ── 置き場 ──

    /// <summary>欄の大きさ（レイアウトの大きさ。無ければ背景の Sprite の大きさ）。</summary>
    private Vector2 FieldSize()
    {
        if (gameObject.GetComponent<CanvasTransform>() is { } t && t.HasLayout) return t.LayoutSize;
        return SpriteOf() is { } bg ? bg.Size : Vector2.Zero;
    }

    /// <summary>欄の画面の矩形（画面の画素。まだ描いていなければ Zero）。</summary>
    private Rect ScreenRect()
        => gameObject.GetComponent<CanvasTransform>() is { } t && t.HasLayout ? t.LayoutRect : Rect.Zero;

    /// <summary>1 単位の画素数（欄の画面の幅 ÷ レイアウトの幅）。</summary>
    private float PxPerUnit()
    {
        var size = FieldSize();
        return KeyboardInsetMath.PxPerUnit(ScreenRect().width, size.x);
    }

    /// <summary>欄のローカルの x（左端から。ノードの単位）→ いちばん近いカーソルの位置。</summary>
    private int CaretIndexAtLocal(float localX)
    {
        string shown = ShownText();
        return TextFieldLayout.CaretIndexAt(_stops, localX - _padding - _textStartX, shown);
    }

    /// <summary>見せている本文（フォーカスの間はエンジンの本文）。</summary>
    private string ShownText() => IsFocused ? _state.Text : Text;

    // ── 見た目 ──

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        var look = TextFieldLooks.Resolve(new TextFieldLookState(IsFocused, !IsEnabled, HasError, Filled), Theme);
        if (SpriteOf() is { } bg)
        {
            bg.Color = look.Background;
            bg.CornerRadius = look.CornerRadius;
            bg.BorderWidth = look.BorderWidth;
            bg.BorderColor = look.Border;
        }
        var size = FieldSize();
        // 左右の余白: 欄ごとの指定（Padding が 0 以上）か、テーマの size.field_padding（2026-10-02）
        _padding = TextFieldLayout.ResolvePadding(Padding, Theme.Number(TextFieldTokens.SizeFieldPadding));
        float inner = Math.Max(0f, size.x - 2f * _padding);
        if (TransformOf(ViewportChild) is { } viewport) viewport.Position = new Vector2(_padding, 0f);
        if (SpriteOf(ViewportChild) is { } viewportSprite) viewportSprite.Size = new Vector2(inner, size.y);

        string shown = ShownText();
        var content = TextOf(ContentChild);
        float fontSize = Theme.Number(TextSize.Length > 0 ? TextSize : TextFieldTokens.TextField);
        string fontPath = Theme.Text(UiTokens.FontFamily);
        if (content is { } c)
        {
            UiTextStyle.Apply(c, Theme, TextSize.Length > 0 ? TextSize : TextFieldTokens.TextField);
            c.Content = TextMarkupEscape.Escape(shown);
            c.Color = look.Text;
            fontSize = c.FontSize;
            fontPath = c.FontPath;
        }
        _stops = TextMeasure.CaretOffsets(shown, fontPath, fontSize);
        float textWidth = _stops[^1];
        float caretWidth = Theme.Number(TextFieldTokens.SizeCaret);
        int caret = Math.Clamp(_state.Caret, 0, shown.Length);
        _scroll = IsFocused ? TextFieldLayout.ScrollToReveal(_scroll, _stops[caret], textWidth, inner, caretWidth) : 0f;
        _textStartX = TextFieldLayout.TextStartX(textWidth, inner, Align, _scroll);
        float middle = size.y * Half;
        if (content is { } box)
        {
            // 枠つきの文字（枠の左端から左寄せ・枠の高さの中で縦の中央。折り返さない）。枠は文字より狭くしない
            // （横の位置は文字の左端 _textStartX に置いて、揃え・スクロールは部品が決める）
            box.Align = AlignLeft;
            box.VerticalAlign = AlignMiddle;
            box.Wrap = false;
            box.BoxWidth = Math.Max(textWidth, inner) + caretWidth;
            box.BoxHeight = size.y;
        }
        if (TransformOf(ContentChild) is { } contentTransform) contentTransform.Position = new Vector2(_textStartX, 0f);

        ApplyPlaceholder(look, shown.Length == 0, inner, size.y);

        // カーソル・選択・変換中の下線の縦の置き場（文字の行の高さの帯を欄の中央に）
        float lineHeight = fontSize * CaretHeightEm;
        float lineTop = middle - lineHeight * Half;
        PlaceBar(CaretChild, IsFocused && _caretOn && !_state.HasSelection,
            _textStartX + _stops[caret] - caretWidth * Half, lineTop, caretWidth, lineHeight, look.Caret);
        PlaceSpan(SelectionChild, IsFocused && _state.HasSelection, _state.SelectionMin, _state.SelectionMax,
            lineTop, lineHeight, look.Selection, shown.Length);
        float underline = Theme.Number(TextFieldTokens.SizeCompositionUnderline);
        PlaceSpan(CompositionChild, IsFocused && _state.IsComposing, _state.CompositionStart, _state.CompositionEnd,
            lineTop + lineHeight - underline, underline, look.Underline, shown.Length);

        if (IsFocused) SendCaretRect(_padding + _textStartX + _stops[caret], lineTop, caretWidth, lineHeight);
    }

    /// <summary>例の文を当てる（本文が空のときだけ見せる。内側の枠いっぱいの枠つきの文字で、揃えは欄の揃え）。</summary>
    private void ApplyPlaceholder(TextFieldLook look, bool empty, float inner, float height)
    {
        var node = gameObject.FindChild(PlaceholderChild);
        if (!node.IsValid) return;
        bool visible = empty && Placeholder.Length > 0;
        if (node.Visible != visible) node.Visible = visible;
        if (!visible || node.GetComponent<Text>() is not { } text) return;
        UiTextStyle.Apply(text, Theme, TextSize.Length > 0 ? TextSize : TextFieldTokens.TextField);
        text.Content = TextMarkupEscape.Escape(Placeholder);
        text.Color = look.Placeholder;
        text.Align = Align == TextFieldAlign.Center ? AlignCenter : AlignLeft;
        text.VerticalAlign = AlignMiddle;
        text.Wrap = false;
        text.BoxWidth = inner;
        text.BoxHeight = height;
        if (node.GetComponent<CanvasTransform>() is { } t) t.Position = Vector2.Zero;
    }

    /// <summary>棒（カーソル）を置く。</summary>
    private void PlaceBar(string path, bool visible, float x, float y, float width, float height, Color color)
    {
        var node = gameObject.FindChild(path);
        if (!node.IsValid) return;
        if (node.Visible != visible) node.Visible = visible;
        if (!visible) return;
        if (node.GetComponent<CanvasTransform>() is { } t) t.Position = new Vector2(x, y);
        if (node.GetComponent<Sprite>() is { } s)
        {
            s.Size = new Vector2(width, height);
            s.Color = color;
        }
    }

    /// <summary>本文の区間（選択・変換中）の帯を置く。</summary>
    private void PlaceSpan(string path, bool visible, int from, int to, float y, float height, Color color, int length)
    {
        if (!visible || from < 0 || to <= from)
        {
            PlaceBar(path, false, 0f, 0f, 0f, 0f, color);
            return;
        }
        int a = Math.Clamp(from, 0, length);
        int b = Math.Clamp(to, 0, length);
        PlaceBar(path, true, _textStartX + _stops[a], y, Math.Max(0f, _stops[b] - _stops[a]), height, color);
    }

    /// <summary>点滅だけを当てる（見た目の作り直しをしない）。</summary>
    private void ApplyCaretVisibility()
    {
        var node = gameObject.FindChild(CaretChild);
        bool visible = IsFocused && _caretOn && !_state.HasSelection;
        if (node.IsValid && node.Visible != visible) node.Visible = visible;
    }

    /// <summary>PC の IME の候補窓が避けるカーソルの矩形を送る（欄のローカルの単位 → 画面の画素。変わったときだけ）。</summary>
    private void SendCaretRect(float localX, float localY, float width, float height)
    {
        var rect = ScreenRect();
        if (!(rect.width > 0f)) return;
        float ppu = PxPerUnit();
        var screen = new Rect(rect.x + localX * ppu, rect.y + localY * ppu, Math.Max(1f, width * ppu), height * ppu);
        if (Math.Abs(screen.x - _sentCaretRect.x) < CaretRectEpsilon && Math.Abs(screen.y - _sentCaretRect.y) < CaretRectEpsilon
            && Math.Abs(screen.height - _sentCaretRect.height) < CaretRectEpsilon) return;
        _sentCaretRect = screen;
        TextInput.SetCaretRect(_session, screen);
    }
}
