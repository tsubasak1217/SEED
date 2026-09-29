using System;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  Dialog.cs — ダイアログ（確認。題・本文・ボタン 1〜3・幕。W2-7。docs/ui_navigation.md §3.2）
//
//  【プレハブ】templates/ui/prefabs/dialog.actor（ModalHost が Dialogs の帯の下に作る）:
//      Dialog（Canvas・親に合わせる・CanvasStack〈縦・中央・中央〉・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる〈並べない〉・CanvasGesture〈タップ〉・GestureRelay）
//      └─ Card（Canvas・Sprite〈面・角丸〉・受けるジェスチャーの無い CanvasGesture〈遮る板〉・CanvasStack〈縦・余白・間隔 0〉・大きさの指定）
//         ├─ Title（Text）・Message（Text〈折り返し〉）
//         └─ Buttons（CanvasStack〈横・右寄せ〉）└─ Neutral・Negative・Positive（SEED.UI.Button）
//  Card は Scrim の兄弟（Scrim の子にすると Card の上のタップが幕のタップになる。W2-2 の遮り R3 は祖先を遮らない）。
//  【開き方】ModalHost.Current.ShowDialog(new DialogOptions { … }) → DialogHandle（ResultAsync / Completed）。
//  【大きさ】（Layout。W2 の手直し P2-1）札の幅は size.dialog_width。題・本文の行の数は見積もり（DialogLayout。Text.Measure は W2-6c）で、
//    題・本文の Text の行間を DialogLayout.LineHeightEm にして、描く行送りと見積もりの行の高さを一致させる。
//    札の高さ = 余白 ＋ 題 ＋ 間隔 ＋ 本文 ＋ 間隔 ＋ ボタンの行 ＋ 余白（DialogMetrics。出さない区画とその間隔は数えない）を、
//    札の CanvasLayoutItem と背景の Sprite の両方へ書く（札は親の CanvasStack が矩形を割り当てるので、札の CanvasStack の fit_height では
//    背景のスプライトが伸びない）。間隔は区画ごとに違う（size.dialog_title_gap・size.dialog_actions_gap）ので札の CanvasStack の間隔は 0 にし、
//    間隔は上の区画の枠（CanvasLayoutItem の高さ = 中身 ＋ 下の間隔）の下の空きにする（Text は枠の上端に置かれる）。
//  【動き】幕の濃さ 0 → opacity.dialog_scrim、札の大きさ ratio.dialog_scale_from → 1（motion.dialog・motion.dialog_curve）。出るときは逆。
//    札の大きさは実行中の見た目の倍率（CanvasLayoutItem.VisualScale。札の矩形の中心の周りに、背景・題・本文・ボタンが一体で縮む）へ
//    「開き具合の倍率 × 予測型の戻るのプレビューの倍率」を書く（W2 の手直し P2-1。以前の保存される CanvasTransform.Scale では、
//    入れ子のキャンバスの子が札の左上へ寄って縮んだ）。部分木の透明度が無いので札そのものはフェードしない（docs/backlog.md）。
// ============================================================

/// <summary>ダイアログ。</summary>
/// <remarks>
/// 【1 行の入力（W2-6b）】DialogOptions.Input があれば、札の Input の枠（dialog.actor の Card/Input。既定は隠す）へ入力欄
/// （templates/ui/prefabs/text_field.actor）を作って本文とボタンの行の間に並べ、開いたらフォーカスを当てる（キーボードが出る）。
/// Positive を選ぶと入力欄の文字（TrimResult なら前後の空白を落とす）を DialogHandle.InputText へ置いてから閉じる。
/// キーボードの完了（SubmitOnDone）でも Positive。キーボードが札に重なるときは札を持ち上げる（IKeyboardInsetTarget。
/// 札の上端は安全領域の上端 ＋ size.keyboard_gap より上へは行かない）。
/// </remarks>
public sealed class Dialog : ModalPlane, IKeyboardInsetTarget
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string CardChild = "Card";
    private const string TitleChild = "Card/Title";
    private const string MessageChild = "Card/Message";
    private const string InputChild = "Card/Input";
    private const string ButtonsChild = "Card/Buttons";
    /// <summary>入力欄のプレハブ（W2-6b）。</summary>
    private const string TextFieldPrefab = "assets://ui/prefabs/text_field.actor";
    private const string NeutralChild = "Card/Buttons/Neutral";
    private const string NegativeChild = "Card/Buttons/Negative";
    private const string PositiveChild = "Card/Buttons/Positive";
    /// <summary>ボタンの文字の子の名前。</summary>
    private const string ButtonLabelChild = "Label";
    /// <summary>ボタンの文字の左右の余白（ボタンの幅 = 文字の幅 + 余白）。</summary>
    private const float ButtonTextPaddingEm = 1.5f;
    /// <summary>ボタンの文字の左右（余白を掛ける数）。</summary>
    private const float ButtonTextPaddingSides = 2f;
    /// <summary>札の CanvasStack の等間隔の間隔（区画ごとの間隔は上の区画の枠が持つので使わない）。</summary>
    private const float CardStackSpacing = 0f;
    /// <summary>倍率なし（開いた・プレビューなし）。</summary>
    private const float NoScale = 1f;

    /// <summary>ボタンの結果と子の道（左から中立・いいえ・はい）。</summary>
    private static readonly (DialogResult Result, string Path)[] ButtonPaths =
    {
        (DialogResult.Neutral, NeutralChild), (DialogResult.Negative, NegativeChild), (DialogResult.Positive, PositiveChild),
    };

    /// <inheritdoc />
    public override ModalKind Kind => ModalKind.Dialog;

    /// <summary>シーンの ModalHost でダイアログを開く（ModalHost が無ければ null・警告）。</summary>
    public static DialogHandle? Show(DialogOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowDialog(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>決め方の中身。</summary>
    private DialogOptions _options = new();
    /// <summary>結果を 1 回だけ受ける留め金。</summary>
    private readonly DialogResultLatch _latch = new();
    /// <summary>幕・札のノード。</summary>
    private GameObject _scrim, _card;
    /// <summary>つないだ部品（幕のタップ・ボタン）の登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>つないだ幕のタップ。</summary>
    private GestureRelay? _scrimRelay;
    /// <summary>つないだボタン（結果 → ボタン）。</summary>
    private readonly System.Collections.Generic.Dictionary<DialogResult, Button> _buttons = new();
    /// <summary>出入りの動き（0 = 閉じた・1 = 開いた）。</summary>
    private UiTween _open = UiTween.At(0f);
    /// <summary>開き具合から決めた札の倍率（出入りの動き。1 = 開いた）。</summary>
    private float _openScale = NoScale;
    /// <summary>予測型の戻るのプレビューの倍率（W2 の手直し 3b。1 = プレビューなし）。確定した後も出終わるまで保つ。</summary>
    private float _previewScale = NoScale;
    /// <summary>入力欄のノード（W2-6b。入力が無ければ無効）。</summary>
    private GameObject _inputNode = new(Entity.None);
    /// <summary>つないだ入力欄（部品の OnStart の後に登録簿から引く）。</summary>
    private TextField? _input;
    /// <summary>札の持ち上げ（キャンバスの単位。キーボードを避ける。0 = 持ち上げない）。</summary>
    private float _liftUnits;

    /// <summary>キーボードを避ける入れ物の根（ダイアログの根）。</summary>
    public GameObject InsetOwner => Owner;

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as DialogOptions ?? new DialogOptions();
        _scrim = gameObject.FindChild(ScrimChild);
        _card = gameObject.FindChild(CardChild);
        CreateInput();
        KeyboardInsets.Register(this);
        Layout();
        ApplyOpen(0f);
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        _open.Retarget(1f, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>
    /// 題・本文・ボタンを当て、札の縦の割り付け（区画の枠の高さ・札の高さ。DialogMetrics）を決めて書く。
    /// </summary>
    private void Layout()
    {
        float width = Theme.Number(NavTokens.SizeDialogWidth);
        float padding = Theme.Number(NavTokens.SizeDialogPadding);
        float inner = DialogMetrics.InnerWidth(width, padding);

        // 題（折り返さない。改行があればその行の数）と本文（中の幅で折り返す）の高さの見積もり、ボタンの行
        float titleHeight = SetText(TitleChild, _options.Title, UiTokens.TextTitle, inner, wrap: false);
        float messageHeight = SetText(MessageChild, _options.Message, UiTokens.TextBody, inner, wrap: true);
        float buttonHeight = Theme.Number(NavTokens.SizeDialogButtonHeight);
        LayoutButtons(buttonHeight);
        // 1 行の入力欄（W2-6b）: 欄の高さは size.field_height、幅は札の中の幅（入力が無ければ 0＝出さない）
        float inputHeight = _inputNode.IsValid ? Theme.Number(TextFieldTokens.SizeFieldHeight) : 0f;
        if (_inputNode.IsValid && _inputNode.GetComponent<Sprite>() is { } inputBackground)
            inputBackground.Size = new Vector2(inner, inputHeight);

        // 縦の割り付け: 区画の枠 = 中身 ＋ 下の間隔（次に見える区画の上の間隔）、札の高さ = 余白 × 2 ＋ 枠の和
        var layout = DialogMetrics.Arrange(padding, DialogMetrics.Sections(
            titleHeight, messageHeight, buttonHeight,
            Theme.Number(NavTokens.SizeDialogTitleGap), Theme.Number(NavTokens.SizeDialogActionsGap), inputHeight));
        SetSlot(TitleChild, inner, layout.SlotHeights[DialogMetrics.TitleSection]);
        SetSlot(MessageChild, inner, layout.SlotHeights[DialogMetrics.MessageSection]);
        SetSlot(InputChild, inner, layout.SlotHeights[DialogMetrics.InputSection]);
        SetSlot(ButtonsChild, inner, layout.SlotHeights[DialogMetrics.ButtonsSection]);

        // 札: 並べ方（余白・等間隔の間隔 0）と大きさ。背景のスプライトも同じ大きさにする
        // （ボタンの背景と同じ。コンテナが伸ばさない軸はスプライトの大きさのまま描かれる）
        if (_card.GetComponent<CanvasStack>() is { } stack)
        {
            stack.Padding = CanvasPadding.All(padding);
            stack.Spacing = CardStackSpacing;
        }
        var cardSize = new Vector2(width, layout.CardHeight);
        if (_card.GetComponent<CanvasLayoutItem>() is { } cardItem) cardItem.PreferredSize = cardSize;
        if (_card.GetComponent<Sprite>() is { } cardBackground) cardBackground.Size = cardSize;
    }

    /// <summary>
    /// 文字の子へ中身・大きさ・行間・枠を当てる（空なら隠す）。戻り値は中身の高さ（行の数の見積もり × 行の高さ。隠したら 0）。
    /// </summary>
    /// <param name="path">子の道。</param>
    /// <param name="content">文字。</param>
    /// <param name="sizeToken">大きさのトークン。</param>
    /// <param name="boxWidth">枠の幅（札の中の幅）。</param>
    /// <param name="wrap">枠の幅で折り返すか（題は折り返さない）。</param>
    private float SetText(string path, string content, string sizeToken, float boxWidth, bool wrap)
    {
        var node = gameObject.FindChild(path);
        var text = node.GetComponent<Text>();
        bool visible = content.Length > 0 && text is not null;
        node.Visible = visible;
        if (!visible || text is not { } shown) return 0f;
        float size = Theme.Number(sizeToken, shown.FontSize);
        float height = DialogLayout.EstimateHeight(content, size, wrap ? boxWidth : DialogLayout.NoWrapWidth);
        shown.Content = content;
        shown.FontSize = size;
        // 描く行送り（大きさ × 行間）を見積もりの行の高さにそろえる（行送りの余白は行の上下へ半分ずつ）
        shown.LineSpacing = DialogLayout.LineHeightEm;
        shown.BoxWidth = boxWidth;
        shown.BoxHeight = height;
        shown.Wrap = wrap;
        return height;
    }

    /// <summary>区画の枠（CanvasLayoutItem の大きさ = 中の幅 × 〈中身 ＋ 下の間隔〉）を当てる（出さない区画〈0〉は書かない）。</summary>
    private void SetSlot(string path, float width, float height)
    {
        if (!(height > 0f)) return;
        if (gameObject.FindChild(path).GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(width, height);
    }

    /// <summary>出すボタンへ文字と大きさ（幅 = 文字の幅の見積もり ＋ 左右の余白。高さより狭くしない）を当て、出さないボタンを隠す。</summary>
    private void LayoutButtons(float height)
    {
        var shown = DialogModel.Buttons(_options);
        float labelSize = Theme.Number(UiTokens.TextLabel);
        foreach (var (result, path) in ButtonPaths)
        {
            var node = gameObject.FindChild(path);
            bool visible = shown.Contains(result);
            node.Visible = visible;
            if (!visible) continue;
            string text = DialogModel.ButtonText(_options, result);
            if (node.FindChild(ButtonLabelChild).GetComponent<Text>() is { } label) label.Content = text;
            float w = Math.Max(DialogLayout.EstimateWidth(text, labelSize) + ButtonTextPaddingEm * ButtonTextPaddingSides * labelSize, height);
            if (node.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(w, height);
            // ボタンの背景は指定の大きさで描かれる（コンテナが伸ばさない軸はスプライトの大きさのまま）ので合わせる
            if (node.GetComponent<Sprite>() is { } bg) bg.Size = new Vector2(w, height);
        }
    }

    /// <summary>入力があれば入力欄を札の Input の枠に作る（W2-6b。枠を見せる。中身は部品の OnStart の後に Bind で当てる）。</summary>
    private void CreateInput()
    {
        var slot = gameObject.FindChild(InputChild);
        bool wanted = _options.Input is not null && slot.IsValid;
        if (slot.IsValid) NavNode.SetVisible(slot, wanted);
        if (!wanted) return;
        _inputNode = GameObject.Instantiate(TextFieldPrefab, slot);
        if (!_inputNode.IsValid) Debug.LogWarning($"{LogPrefix} 入力欄のプレハブを作れませんでした（{TextFieldPrefab}）");
    }

    /// <summary>入力欄をつなぐ（中身を当て、完了で Positive、開いたらフォーカス）。</summary>
    private void BindInput()
    {
        if (_input is not null || !_inputNode.IsValid || _options.Input is not { } spec) return;
        if (Of<TextField>(_inputNode) is not { } field) return;
        _input = field;
        field.Placeholder = spec.Placeholder;
        field.Kind = spec.Kind;
        field.MaxLength = spec.MaxLength;
        field.AllowPaste = spec.AllowPaste;
        field.UnfocusOnDone = false;
        field.SetText(spec.Text);
        field.Submitted += (_, action) =>
        {
            if (spec.SubmitOnDone && action == TextInputAction.Done) Choose(DialogResult.Positive);
        };
        field.Focus();
    }

    /// <summary>幕のタップ・ボタンをつなぐ（部品の OnStart の順は決まっていないので、登録簿が変わるたびに引き直す）。</summary>
    private void Bind()
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindInput();
        if (_scrimRelay is null && Of<GestureRelay>(_scrim) is { } relay)
        {
            _scrimRelay = relay;
            relay.Tapped += (_, _) => OnScrimTapped();
        }
        foreach (var (result, path) in ButtonPaths)
        {
            if (_buttons.ContainsKey(result) || Of<Button>(gameObject.FindChild(path)) is not { } button) continue;
            _buttons[result] = button;
            button.Clicked += _ => Choose(result);
        }
    }

    /// <summary>ボタン・幕・戻るで結果を決めて閉じる（1 回だけ）。Positive なら入力欄の文字を手札へ置いてから閉じる（W2-6b）。</summary>
    private void Choose(DialogResult result)
    {
        if (Phase is ModalPhase.Exiting or ModalPhase.Closed) return;
        if (!_latch.TryComplete(result)) return;
        if (_input is { } field)
        {
            // フォーカスを外して変換中の文字を確定扱いにし、キーボードを隠してから文字を読む
            field.Unfocus();
            if (result == DialogResult.Positive && _options.Input is { } spec && Handle is DialogHandle handle)
                handle.SetInputText(spec.Finish(field.Text));
        }
        ClearKeyboardLift();
        KeyboardInsets.Unregister(this);
        RequestClose(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 札の下端 ＋ 余白がキーボードの上端を越えた分だけ札を上へずらす（CanvasLayoutItem.Translate。持ち上げる前の矩形で測る）。
    /// 札の上端は安全領域の上端 ＋ 余白より上へは行かない（KeyboardInsetMath.LiftPx）。
    /// </remarks>
    public void ApplyKeyboardLift(float keyboardTopPx, float gapPx)
    {
        if (!_card.IsValid || _card.GetComponent<CanvasTransform>() is not { } t || !t.HasLayout) return;
        var rect = t.LayoutRect;
        float pxPerUnit = KeyboardInsetMath.PxPerUnit(rect.height, t.LayoutSize.y);
        // 今の持ち上げを戻した矩形（画面の画素）で測る（持ち上げた矩形で測ると、持ち上げの分だけ少なく見える）
        var resting = new Rect(rect.x, rect.y + _liftUnits * pxPerUnit, rect.width, rect.height);
        float lift = KeyboardInsetMath.LiftPx(resting, keyboardTopPx, gapPx, Screen.SafeArea.y + gapPx);
        _liftUnits = KeyboardInsetMath.PxToUnits(lift, pxPerUnit);
        NavNode.SetTranslate(_card, new Vector2(0f, -_liftUnits));
        Redraw.Request();
    }

    /// <inheritdoc />
    public void ClearKeyboardLift()
    {
        if (_liftUnits == 0f) return;
        _liftUnits = 0f;
        if (_card.IsValid) NavNode.SetTranslate(_card, Vector2.Zero);
        Redraw.Request();
    }

    /// <summary>幕のタップ。</summary>
    private void OnScrimTapped()
    {
        if (DialogModel.OnScrimTap(_options) is { } result) Choose(result);
    }

    /// <inheritdoc />
    internal override bool HandleBack()
    {
        var (consumed, result) = DialogModel.OnBack(_options);
        if (result is { } r) Choose(r);
        return consumed;
    }

    /// <inheritdoc />
    /// <remarks>予測型の戻るのプレビュー（3b）で縮めるのは札（真ん中の周り。幕はそのまま）。</remarks>
    protected override GameObject BackPreviewNode => _card;

    /// <inheritdoc />
    /// <remarks>戻るで閉じない（CancelableByBack = false）ダイアログは縮めない（戻るは受けるが何も起きない）。</remarks>
    protected override bool ClosesOnBack => _options.CancelableByBack;

    /// <inheritdoc />
    /// <remarks>
    /// 札の倍率は出入りの動きと同じ欄（VisualScale）なので、プレビューの倍率を覚えて開き具合の倍率との積を書く。
    /// 確定した戻るの後はプレビューの倍率を保ったまま出る動きが進む（縮めた姿勢のまま出る。ClearBackPreview は出終わってから来る）。
    /// </remarks>
    protected override void ApplyBackPreviewPose(BackPreviewPose pose)
    {
        _previewScale = pose.Scale;
        ApplyCardScale();
    }

    /// <inheritdoc />
    protected override void OnPlaneUpdate(float dt)
    {
        Bind();
        if (!_open.IsRunning) return;
        ApplyOpen(_open.Advance(dt));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else if (Phase == ModalPhase.Exiting) FinishClose();
    }

    /// <inheritdoc />
    /// <remarks>入力欄のフォーカスとキーボードの持ち上げもここで手放す（ボタンを通らずに閉じたとき〈Dismiss・シーンの切り替え〉も）。</remarks>
    protected override void OnBeginExit()
    {
        _input?.Unfocus();
        ClearKeyboardLift();
        KeyboardInsets.Unregister(this);
        _open.Retarget(0f, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>開き具合（0〜1）→ 幕の濃さと札の大きさ（曲線を通す）。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionDialogCurve, UiCurve.Decelerate).Evaluate(linear);
        var scrim = Theme.Color(NavTokens.ColorScrim);
        NavNode.SetSpriteColor(_scrim, scrim.WithAlpha(Theme.Number(NavTokens.OpacityDialogScrim) * p));
        _openScale = DialogMetrics.OpenScale(p, Theme.Number(NavTokens.RatioDialogScaleFrom, NoScale));
        ApplyCardScale();
    }

    /// <summary>札へ見た目の倍率（開き具合 × プレビュー。札の矩形の中心の周りに、背景と中身が一体で縮む）を書く。</summary>
    private void ApplyCardScale()
    {
        float s = DialogMetrics.CardScale(_openScale, _previewScale);
        NavNode.SetVisualScale(_card, new Vector2(s, s));
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        // 札の面の色・角丸（テーマの差し替えに追従）
        if (_card.GetComponent<Sprite>() is { } card)
        {
            card.Color = Theme.Color(UiTokens.ColorSurface);
            card.CornerRadius = Theme.Number(NavTokens.RadiusDialog);
        }
        if (gameObject.FindChild(TitleChild).GetComponent<Text>() is { } title)
        {
            title.Color = Theme.Color(UiTokens.ColorOnSurface);
            // 書体と見出しの太さ（W2-9。大きさは Layout が文字の量と一緒に決める）
            UiTextStyle.ApplyFont(title, Theme, UiTokens.FontWeightTitle);
        }
        if (gameObject.FindChild(MessageChild).GetComponent<Text>() is { } message)
        {
            message.Color = Theme.Color(UiTokens.ColorOnSurfaceMuted);
            UiTextStyle.ApplyFont(message, Theme);
        }
    }
}
