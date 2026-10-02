using System;

namespace SEED.UI;

// ============================================================
//  Popup.cs — 中央のポップアップ（札が画面の真ん中に出て、周りの幕を押すと閉じる。2026-10-02。lane3。
//             docs/ui_navigation.md §3.6。backlog W3-6 (3)。Wake or Pay のプロフィールのポップアップ〈PopupPlane〉を汎用化）
//
//  【プレハブ】templates/ui/prefabs/popup.actor（ModalHost が PopupOptions.Kind の帯〈既定は Overlays〉の下に作る）:
//      Popup（Canvas・親に合わせる・CanvasStack〈縦・真ん中・真ん中。上下の余白 = 安全領域〉・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる〈並べない〉・CanvasGesture〈タップ〉・GestureRelay）…… タップで閉じる（DismissOnScrimTap）
//      └─ Card（Canvas・Sprite〈面・角丸 radius.popup〉・受けるジェスチャーの無い CanvasGesture〈遮る板〉・CanvasStack〈縦・内側の余白〉）
//         ├─ Content（ここへ中身のプレハブを作る。残りの高さいっぱい）
//         └─ CloseButton（右上の丸い ×。並べない・札の角に半分かかる位置はプレハブの値。ShowCloseButton = false で隠す）└─ Label
//  Card は Scrim の兄弟（Scrim の子にすると札の上のタップが幕のタップになる。W2-2 の遮り R3 は祖先を遮らない）。
//  【大きさ】PopupCardMath（純粋）: 幅は画面の幅 − size.popup_margin × 2（上限 size.popup_max_width）、高さは中身の高さ ＋ size.popup_padding × 2
//    （上限は安全領域の高さ × ratio.popup_max_height）。中身の高さは PopupOptions.ContentHeight → 中身の画面のスクリプトの
//    IPopupContentSize.PopupContentHeight → 中身の根のレイアウトの高さ（親に合わせる中身は上限）の順。札の CanvasLayoutItem と背景の Sprite の両方へ書く
//    （中身に合わせるコンテナの背景が伸びない件と同じ。ダイアログと同じ書き方）。
//  【準備】中身ができあがり、中身の画面のスクリプトへ Enter を届けて 1 フレーム描き（ContentSettleGate）、札の大きさが 1 フレーム変わらなくなるまで
//    見せない（最初のフレームに潰れた札を見せない。上限 MaxPrepareFrames）。
//  【動き】ダイアログと同じ: 幕の濃さ 0 → opacity.dialog_scrim、札の大きさ ratio.dialog_scale_from → 1（motion.dialog・motion.dialog_curve）。
//    札の大きさは見た目の倍率（VisualScale）に「開き具合 × 予測型の戻るのプレビュー」を書く（DialogMetrics.CardScale）。
//    PopupOptions.Animate = false なら開いた姿で出る。閉じる動きを見せないのは ModalHandle.Close(結果, false)・ModalHost.CloseAll(false)。
//  【閉じる】幕のタップ（DismissOnScrimTap）・右上の ×・戻る（CancelableByBack。閉じなくても戻るは受ける）・手札の Close。指で払っては閉じない。
// ============================================================

/// <summary>中央のポップアップ。</summary>
public sealed class Popup : ModalPlane
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string CardChild = "Card";
    private const string ContentChild = "Card/Content";
    private const string CloseChild = "Card/CloseButton";
    private const string CloseLabelChild = "Card/CloseButton/Label";
    /// <summary>準備（中身の組み立て・札の大きさ）を待つフレームの上限（越えたら今の大きさで見せる）。</summary>
    private const int MaxPrepareFrames = 120;
    /// <summary>札の大きさが落ち着いたとみなすフレーム数（大きさを書いたフレームの次のフレームのレイアウトから効く）。</summary>
    private const int StableFramesToShow = 1;
    /// <summary>大きさ・余白が変わったとみなす差（キャンバスの単位）。</summary>
    private const float SizeEpsilon = 0.5f;
    /// <summary>倍率なし（開いた・プレビューなし）。</summary>
    private const float NoScale = 1f;
    /// <summary>開き具合（開いた・閉じた）。</summary>
    private const float Opened = 1f, Closed = 0f;

    /// <inheritdoc />
    /// <remarks>開く約束の PopupOptions.Kind（既定は覆い。約束を受け取る前も覆い）。</remarks>
    public override ModalKind Kind => (Options as PopupOptions)?.Kind ?? ModalKind.Overlay;

    /// <summary>シーンの ModalHost で中央のポップアップを開く（ModalHost が無ければ null・警告）。</summary>
    /// <param name="options">指定。</param>
    public static ModalHandle? Show(PopupOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowPopup(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>指定。</summary>
    private PopupOptions _options = new();
    /// <summary>子のノードと中身の根。</summary>
    private GameObject _scrim, _card, _content, _contentRoot, _close;
    /// <summary>幕と × のタップ。</summary>
    private GestureRelay? _scrimRelay, _closeRelay;
    /// <summary>開き具合（0 = 閉じた・1 = 開いた）。</summary>
    private UiTween _open = UiTween.At(Closed);
    /// <summary>動きの 1 フレームの進め（上限。開く動きは準備のフレームで始めて次のフレームから進めるので、始めのフレームは元から数えない）。</summary>
    private MotionStep _step;
    /// <summary>開き具合から決めた札の倍率。</summary>
    private float _openScale = NoScale;
    /// <summary>予測型の戻るのプレビューの倍率。</summary>
    private float _previewScale = NoScale;
    /// <summary>準備を待ったフレーム。</summary>
    private int _prepareFrames;
    /// <summary>札の大きさが変わらずに続いたフレーム（中身の高さが分かってから）。</summary>
    private int _stableFrames;
    /// <summary>今の札の大きさ（まだなら 0）。</summary>
    private PopupCardSize _cardSize;
    /// <summary>中身の画面のスクリプトへ値を渡したか。</summary>
    private bool _contentEntered;
    /// <summary>中身の落ち着き待ち（Enter を届けて 1 フレーム描くまで見せない）。</summary>
    private ContentSettleGate _contentSettle;

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as PopupOptions ?? new PopupOptions();
        _scrim = gameObject.FindChild(ScrimChild);
        _card = gameObject.FindChild(CardChild);
        _content = gameObject.FindChild(ContentChild);
        _close = gameObject.FindChild(CloseChild);
        if (_options.ContentPrefab.Length > 0)
        {
            _contentRoot = GameObject.Instantiate(_options.ContentPrefab, _content.IsValid ? _content : _card);
            if (!_contentRoot.IsValid) Debug.LogError($"{LogPrefix} ポップアップの中身を作れません: {_options.ContentPrefab}");
        }
        NavNode.SetVisible(_close, _options.ShowCloseButton);
        ApplyOpen(Closed);
    }

    /// <inheritdoc />
    protected override void OnPlaneUpdate(float dt)
    {
        Bind();
        // 中身ができあがった後のフレームを、Enter を届けるより前に数える（ContentSettleGate の約束の順）
        if (_contentRoot.IsValid && NavNode.IsBuilt(_contentRoot)) _contentSettle.Frame();
        EnterContent();
        FitCard();
        switch (Phase)
        {
            case ModalPhase.Preparing:
                Prepare();
                break;
            case ModalPhase.Entering:
            case ModalPhase.Exiting:
                Animate(dt);
                break;
        }
    }

    /// <summary>幕のタップ・× のタップをつなぐ（部品の OnStart の順は決まっていないので、つながるまで毎フレーム引く）。</summary>
    private void Bind()
    {
        if (_scrimRelay is null && Of<GestureRelay>(_scrim) is { } scrim)
        {
            _scrimRelay = scrim;
            scrim.Tapped += (_, _) =>
            {
                if (_options.DismissOnScrimTap && Phase is ModalPhase.Open or ModalPhase.Entering) RequestClose(null);
            };
        }
        if (_closeRelay is null && Of<GestureRelay>(_close) is { } close)
        {
            _closeRelay = close;
            close.Tapped += (_, _) =>
            {
                if (Phase is ModalPhase.Open or ModalPhase.Entering) RequestClose(null);
            };
        }
    }

    /// <summary>中身の画面のスクリプト（UiScreen）へ値を渡す（1 回）。</summary>
    private void EnterContent()
    {
        if (_contentEntered || !_contentRoot.IsValid) return;
        if (!DeliverEnter(_contentRoot, _options.Args)) return;
        _contentEntered = true;
        // 開く動きは、このフレーム（Enter の中の変更）と中身のスクリプトの最初の Update を描いた後から
        _contentSettle.MarkEntered();
    }

    /// <summary>
    /// 準備: 中身が落ち着き（画面のスクリプトの無い中身は待つ上限の後）、札の大きさが 1 フレーム変わらなくなったら見せて開く
    /// （動きなしの頼みなら開いた姿で。待つ上限を過ぎたら今の大きさで）。
    /// </summary>
    private void Prepare()
    {
        Redraw.Request();
        bool contentReady = !_contentRoot.IsValid || (NavNode.IsBuilt(_contentRoot) && _contentSettle.IsSettled);
        bool sized = _stableFrames >= StableFramesToShow;
        if (!(contentReady && sized) && ++_prepareFrames <= MaxPrepareFrames) return;
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        if (!_options.Animate)
        {
            _open.Jump(Opened);
            ApplyOpen(Opened);
            EndEnter();
            return;
        }
        _open.Retarget(Opened, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>開く・閉じる動き（1 フレームで進める時間は上限まで）。</summary>
    private void Animate(float dt)
    {
        ApplyOpen(_open.Advance(_step.Next(dt)));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else if (Phase == ModalPhase.Exiting) FinishClose();
    }

    /// <inheritdoc />
    protected override void OnBeginExit() => _open.Retarget(Closed, Theme.Number(NavTokens.MotionDialog));

    /// <inheritdoc />
    internal override bool HandleBack()
    {
        if (_options.CancelableByBack) RequestClose(null);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>予測型の戻るのプレビューで縮めるのは札（真ん中の周り。幕はそのまま）。</remarks>
    protected override GameObject BackPreviewNode => _card;

    /// <inheritdoc />
    protected override bool ClosesOnBack => _options.CancelableByBack;

    /// <inheritdoc />
    /// <remarks>札の倍率は出入りの動きと同じ欄なので、プレビューの倍率を覚えて開き具合の倍率との積を書く（Dialog と同じ）。</remarks>
    protected override void ApplyBackPreviewPose(BackPreviewPose pose)
    {
        _previewScale = pose.Scale;
        ApplyCardScale();
    }

    /// <summary>
    /// 札の大きさを決めて書く（毎フレーム。変わったときだけ書く）: 覆う領域・安全領域・中身の高さから PopupCardMath.Card。
    /// 札を安全領域の真ん中に置くため、根の縦の並びの上下の余白を安全領域にする。
    /// </summary>
    private void FitCard()
    {
        if (Owner.GetComponent<CanvasTransform>() is not { HasLayout: true } root || root.LayoutSize.y <= 0f) return;
        float top = SafeInsets.TopUnits();
        float bottom = SafeInsets.BottomUnits();
        if (Owner.GetComponent<CanvasStack>() is { } stack &&
            (MathF.Abs(stack.Padding.Top - top) > SizeEpsilon || MathF.Abs(stack.Padding.Bottom - bottom) > SizeEpsilon))
        {
            stack.Padding = new CanvasPadding(0f, top, 0f, bottom);
        }

        var (content, known) = ContentHeight();
        float padding = Theme.Number(NavTokens.SizePopupPadding);
        var size = PopupCardMath.Card(root.LayoutSize.x, root.LayoutSize.y, top, bottom, Theme.Number(NavTokens.SizePopupMargin),
            Theme.Number(NavTokens.SizePopupMaxWidth), Theme.Number(NavTokens.RatioPopupMaxHeight), padding, _options.Width, content);
        bool changed = MathF.Abs(size.Width - _cardSize.Width) > SizeEpsilon || MathF.Abs(size.Height - _cardSize.Height) > SizeEpsilon;
        _stableFrames = known && !changed ? _stableFrames + 1 : 0;
        if (!changed) return;
        _cardSize = size;
        var vector = new Vector2(size.Width, size.Height);
        if (_card.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = vector;
        if (_card.GetComponent<Sprite>() is { } face) face.Size = vector;
        if (_card.GetComponent<CanvasStack>() is { } cardStack) cardStack.Padding = CanvasPadding.All(padding);
        Redraw.Request();
    }

    /// <summary>
    /// 札の中身の高さ（内側の余白を含まない）と、それが分かったか: 指定（PopupOptions.ContentHeight）→ 中身の画面のスクリプトの
    /// IPopupContentSize → 中身の根のレイアウトの高さ（親に合わせる中身は 0 = 上限の高さ）。中身が無ければ 0（上限の高さ）で分かったとする。
    /// </summary>
    private (float Height, bool Known) ContentHeight()
    {
        if (_options.ContentHeight > 0f) return (_options.ContentHeight, true);
        if (!_contentRoot.IsValid) return (0f, true);
        if (Of<UiScreen>(_contentRoot) is IPopupContentSize sized)
        {
            float reported = sized.PopupContentHeight;
            return (reported, reported > 0f);
        }
        if (_contentRoot.GetComponent<CanvasLayoutItem>() is { FillHeight: true }) return (0f, true);
        return _contentRoot.GetComponent<CanvasTransform>() is { HasLayout: true } ct && ct.LayoutSize.y > 0f
            ? (ct.LayoutSize.y, true)
            : (0f, false);
    }

    /// <summary>開き具合（0〜1）→ 幕の濃さと札の倍率（曲線を通す）。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionDialogCurve, UiCurve.Decelerate).Evaluate(linear);
        NavNode.SetSpriteColor(_scrim, Theme.Color(NavTokens.ColorScrim).WithAlpha(Theme.Number(NavTokens.OpacityDialogScrim) * p));
        _openScale = DialogMetrics.OpenScale(p, Theme.Number(NavTokens.RatioDialogScaleFrom, NoScale));
        ApplyCardScale();
    }

    /// <summary>札へ見た目の倍率（開き具合 × プレビュー。札の矩形の真ん中の周りに、面・中身・× が一体で縮む）を書く。</summary>
    private void ApplyCardScale()
    {
        float s = DialogMetrics.CardScale(_openScale, _previewScale);
        NavNode.SetVisualScale(_card, new Vector2(s, s));
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (_card.GetComponent<Sprite>() is { } card)
        {
            card.Color = Theme.Color(UiTokens.ColorSurface);
            card.CornerRadius = Theme.Number(NavTokens.RadiusPopup);
        }
        if (_close.GetComponent<Sprite>() is { } close)
        {
            close.Color = Theme.Color(UiTokens.ColorSurfaceVariant);
            close.BorderColor = Theme.Color(UiTokens.ColorOutline);
        }
        if (gameObject.FindChild(CloseLabelChild).GetComponent<Text>() is { } label)
        {
            label.Color = Theme.Color(UiTokens.ColorOnSurface);
            UiTextStyle.ApplyFont(label, Theme);
        }
        ApplyOpen(_open.Value);
    }
}
