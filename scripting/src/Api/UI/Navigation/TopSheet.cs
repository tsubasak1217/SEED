using System;

namespace SEED.UI;

// ============================================================
//  TopSheet.cs — 上からの覆い（プロフィール・オプション。W2-7。docs/ui_navigation.md §3.4）
//
//  【プレハブ】templates/ui/prefabs/top_sheet.actor（ModalHost が Overlays の帯の下に作る）:
//      TopSheet（Canvas・親に合わせる・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる・CanvasGesture〈タップ〉・GestureRelay）…… タップで閉じる
//      └─ Panel（Canvas・Sprite〈面・下の角丸〉・幅を親に合わせる・CanvasStack〈縦・高さは中身に合わせる〉・
//               CanvasGesture〈縦のドラッグ・フリック〉・GestureRelay）…… 板のスクロールしない所の払い・引き（後ろへ指を渡さない）
//         ├─ HandleRow └─ Handle（つまみ。見た目だけ）
//         └─ Content（ここへ中身のプレハブを作る。中の一覧がスクロールする所は一覧が指を取る）
//  降りる動き: 板の自分の高さに対するずらし −1 → 0（CanvasLayoutItem.TranslateFraction。板の高さを知らなくてよい）、
//  幕の濃さ 0 → opacity.scrim（motion.overlay 0.22 秒・motion.overlay_curve = Flutter 版の top_sheet の easeOut）。
//  指で閉じる（OverlayOptions.DismissGesture）:
//    FlickDown（既定。Flutter 版の top_sheet.dart と同じ）… 板を下へ speed.fling_dismiss（300 dp/秒）超で払うと閉じる（板は動かない）
//    DragUp … 板を上へ引く（指に付いてくる）。size.drag_dismiss 以上か上へ速く払うと閉じ、足りなければ戻る（下へは抵抗つきで少しだけ）
//  板の上の余白 = ステータスバーの高さ（SafeInsets.TopUnits。背景は画面の上端まで）。
//  【動きなし】（2026-10-02）OverlayOptions.Animate = false なら、中身が落ち着いたら降りた姿で出す（閉じる動きなしは ModalHandle.Close(結果, false)）。
//  【高さいっぱい】（2026-10-02）OverlayOptions.FillHeight なら、中身の根の CanvasLayoutItem の高さを毎フレーム SheetMath.OverlayFillHeight に合わせ
//  （板の下端 = 画面の下 − 下の安全領域 − FillBottomMargin）、合わせた高さが 1 フレーム落ち着くまで見せない。
// ============================================================

/// <summary>上からの覆い。</summary>
public sealed class TopSheet : ModalPlane
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string PanelChild = "Panel";
    private const string ContentChild = "Panel/Content";
    private const string HandleChild = "Panel/HandleRow/Handle";
    private const string HandleRowChild = "Panel/HandleRow";
    /// <summary>高さいっぱいの高さが変わったとみなす差（キャンバスの単位）。</summary>
    private const float FillEpsilon = 0.5f;
    /// <summary>高さいっぱいの高さが落ち着いたとみなすフレーム数（書いたフレームの次のフレームのレイアウトから効く）。</summary>
    private const int FillStableFrames = 1;
    /// <summary>降りた・上に隠れたの開き具合。</summary>
    private const float Opened = 1f;
    /// <summary>閉じる向きと逆（下）へ引いたときの抵抗（引いた分の何割だけ動くか。DragUp）。</summary>
    private const float BackwardResistance = 0.3f;
    /// <summary>閉じる向きと逆へ動ける最大（キャンバスの単位。DragUp）。</summary>
    private const float MaxBackwardOffset = 24f;
    /// <summary>準備（中身の作成）を待つフレームの上限。</summary>
    private const int MaxPrepareFrames = 120;
    /// <summary>上に隠れた位置（自分の高さに対する割合）。</summary>
    private const float HiddenFraction = -1f;
    /// <summary>フリックだけで閉じる（距離のしきい値を使わない）ときのしきい値。</summary>
    private const float NoDistanceThreshold = 0f;

    /// <inheritdoc />
    public override ModalKind Kind => ModalKind.Overlay;

    /// <summary>シーンの ModalHost で上からの覆いを開く（ModalHost が無ければ null・警告）。</summary>
    public static ModalHandle? Show(OverlayOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowOverlay(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>指定。</summary>
    private OverlayOptions _options = new();
    /// <summary>子のノード。</summary>
    private GameObject _scrim, _panel, _content, _contentRoot;
    /// <summary>中身の画面のスクリプトへ値を渡したか。</summary>
    private bool _contentEntered;
    /// <summary>幕のタップ・板の払い。</summary>
    private GestureRelay? _scrimRelay, _panelRelay;
    /// <summary>開き具合（0 = 上に隠れた・1 = 降りた）。</summary>
    private UiTween _open = UiTween.At(0f);
    /// <summary>上へ引いた距離（閉じる向きが正。dp。DragUp）。</summary>
    private float _dragOffset;
    /// <summary>指で引いているか。</summary>
    private bool _dragging;
    /// <summary>離した後に戻る動き（引いた距離 → 0。DragUp）。</summary>
    private UiTween _settle = UiTween.At(0f);
    /// <summary>準備を待ったフレーム。</summary>
    private int _prepareFrames;
    /// <summary>中身の落ち着き待ち（中身の画面のスクリプトへ Enter を届けて 1 フレーム描くまで降りない。遷移の時計の直し）。</summary>
    private ContentSettleGate _contentSettle;
    /// <summary>動きの 1 フレームの進め（上限。降りる動きは準備のフレームで始め、次のフレームから進めるので始めのフレームは元から数えない）。</summary>
    private MotionStep _step;
    /// <summary>予測型の戻るのプレビューで上の辺を留めるずらし（キャンバスの単位・下が正。3b。引いたずらしと足して当てる）。</summary>
    private float _previewOffset;
    /// <summary>高さいっぱいの高さが変わらずに続いたフレーム（2026-10-02）。</summary>
    private int _fillFrames;
    /// <summary>高さいっぱいにできない（中身の根に CanvasLayoutItem が無い。警告を出した）。</summary>
    private bool _fillUnavailable;

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as OverlayOptions ?? new OverlayOptions();
        _scrim = gameObject.FindChild(ScrimChild);
        _panel = gameObject.FindChild(PanelChild);
        _content = gameObject.FindChild(ContentChild);
        if (_options.ContentPrefab.Length > 0)
        {
            _contentRoot = GameObject.Instantiate(_options.ContentPrefab, _content.IsValid ? _content : _panel);
            if (!_contentRoot.IsValid) Debug.LogError($"{LogPrefix} 覆いの中身を作れません: {_options.ContentPrefab}");
        }
        if (_panel.GetComponent<CanvasStack>() is { } stack) stack.Padding = new CanvasPadding(0f, SafeInsets.TopUnits(), 0f, 0f);
        ApplyOpen(0f);
    }

    /// <inheritdoc />
    protected override void OnPlaneUpdate(float dt)
    {
        Bind();
        // 中身ができあがった後のフレームを、Enter を届けるより前に数える（ContentSettleGate の約束の順）
        if (_contentRoot.IsValid && NavNode.IsBuilt(_contentRoot)) _contentSettle.Frame();
        EnterContent();
        FitHeight();
        switch (Phase)
        {
            case ModalPhase.Preparing:
                Prepare();
                break;
            case ModalPhase.Entering:
            case ModalPhase.Exiting:
                Animate(dt);
                break;
            case ModalPhase.Open:
                Settle(dt);
                break;
        }
    }

    /// <summary>
    /// 準備: 中身ができあがり、中身の画面のスクリプトへ Enter を届けて 1 フレーム描いたら（画面のスクリプトの無い中身は待つ上限の後）
    /// 見せて降りる（中身の高さが並びに入り、中身の組み立ての重いフレームが済んでから動かす。ContentSettleGate）。
    /// </summary>
    private void Prepare()
    {
        Redraw.Request();
        bool contentReady = !_contentRoot.IsValid || (NavNode.IsBuilt(_contentRoot) && _contentSettle.IsSettled);
        // 高さいっぱいなら、合わせた高さが 1 フレーム落ち着いてから（伸びる前の板を見せない）
        bool filled = !_options.FillHeight || !_contentRoot.IsValid || _fillUnavailable || _fillFrames >= FillStableFrames;
        if (!(contentReady && filled) && ++_prepareFrames <= MaxPrepareFrames) return;
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        if (!_options.Animate)
        {
            // 動きなし（2026-10-02）: 降りた姿で出す
            _open.Jump(Opened);
            ApplyOpen(Opened);
            EndEnter();
            return;
        }
        _open.Retarget(Opened, Theme.Number(NavTokens.MotionOverlay));
    }

    /// <summary>
    /// 高さいっぱい（OverlayOptions.FillHeight。2026-10-02）: 中身の根の CanvasLayoutItem の高さを、板の下端が「画面の下 − 下の安全領域 − 余白」
    /// に来る高さ（SheetMath.OverlayFillHeight）に合わせる（変わったときだけ書く。回転・窓の大きさにも追従）。
    /// </summary>
    private void FitHeight()
    {
        if (!_options.FillHeight || _fillUnavailable || !NavNode.IsBuilt(_contentRoot)) return;
        if (Owner.GetComponent<CanvasTransform>() is not { HasLayout: true } root || root.LayoutSize.y <= 0f) return;
        if (_contentRoot.GetComponent<CanvasLayoutItem>() is not { } item)
        {
            _fillUnavailable = true;
            Debug.LogWarning($"{LogPrefix} 高さいっぱいにできません（中身の根に CanvasLayoutItem がありません）: {_options.ContentPrefab}");
            return;
        }
        float handle = gameObject.FindChild(HandleRowChild).GetComponent<CanvasTransform>() is { HasLayout: true } row ? row.LayoutSize.y : 0f;
        float margin = _options.FillBottomMargin >= 0f ? _options.FillBottomMargin : Theme.Number(UiTokens.SpaceM);
        float height = SheetMath.OverlayFillHeight(root.LayoutSize.y, SafeInsets.TopUnits(), handle, margin, SafeInsets.BottomUnits());
        if (MathF.Abs(item.PreferredSize.y - height) <= FillEpsilon)
        {
            // 落ち着いた数は上限で止める（長く開いている覆いで数が溢れない）
            if (_fillFrames < FillStableFrames) _fillFrames++;
            return;
        }
        _fillFrames = 0;
        item.PreferredSize = new Vector2(item.PreferredSize.x, height);
        Redraw.Request();
    }

    /// <summary>降りる・上がる動き。</summary>
    private void Animate(float dt)
    {
        // 1 フレームで進める時間は上限まで（重いフレームで降りる・上がる動きが飛ばない）
        ApplyOpen(_open.Advance(_step.Next(dt)));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else FinishClose();
    }

    /// <summary>開いている間: 離した後の戻る動き（DragUp）。</summary>
    private void Settle(float dt)
    {
        if (_dragging || !_settle.IsRunning) return;
        _dragOffset = _settle.Advance(_step.Next(dt));
        ApplyDrag();
        Redraw.KeepAlive(_settle.Remaining);
    }

    /// <summary>幕のタップ・板の払いをつなぐ。</summary>
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
        if (_panelRelay is null && Of<GestureRelay>(_panel) is { } panel)
        {
            _panelRelay = panel;
            panel.DragStarted += (_, e) => OnDrag(e);
            panel.DragUpdated += (_, e) => OnDrag(e);
            panel.DragEnded += (_, e) => OnDragEnd(e);
        }
    }

    /// <summary>板を引いた（DragUp だけ板が付いてくる。上が閉じる向き＝DeltaDp の y は下が正なので符号を反す）。</summary>
    private void OnDrag(GestureEvent e)
    {
        if (_options.DismissGesture != OverlayDismissGesture.DragUp || Phase != ModalPhase.Open) return;
        _dragging = true;
        _settle.Jump(_dragOffset);
        _dragOffset = DragDismissMath.ApplyDrag(_dragOffset, -e.DeltaDp.y, BackwardResistance, MaxBackwardOffset);
        ApplyDrag();
        Redraw.Request();
    }

    /// <summary>離した: 閉じるか戻すかを決める。</summary>
    private void OnDragEnd(GestureEvent e)
    {
        if (Phase != ModalPhase.Open || e.Canceled)
        {
            EndDrag();
            return;
        }
        float fling = Theme.Number(NavTokens.SpeedFlingDismiss);
        switch (_options.DismissGesture)
        {
            case OverlayDismissGesture.FlickDown:
                // 下へ速く払った（Flutter 版: primaryVelocity > 300 で maybePop）
                if (DragDismissMath.ShouldDismiss(0f, e.VelocityDp.y, NoDistanceThreshold, fling)) RequestClose(null);
                return;
            case OverlayDismissGesture.DragUp:
                if (_dragging && DragDismissMath.ShouldDismiss(_dragOffset, -e.VelocityDp.y, Theme.Number(NavTokens.SizeDragDismiss), fling))
                {
                    _dragging = false;
                    RequestClose(null);
                    return;
                }
                EndDrag();
                return;
        }
    }

    /// <summary>引いた板を戻し始める。</summary>
    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _settle.Jump(_dragOffset);
        _settle.Retarget(0f, Theme.Number(UiTokens.MotionShort));
        Redraw.Request();
    }

    /// <summary>中身の画面のスクリプト（UiScreen）へ値を渡す（1 回）。</summary>
    private void EnterContent()
    {
        if (_contentEntered || !_contentRoot.IsValid || Of<UiScreen>(_contentRoot) is not { } screen) return;
        _contentEntered = true;
        // 降りる動きは、このフレーム（Enter の中の変更）と中身のスクリプトの最初の Update を描いた後から
        _contentSettle.MarkEntered();
        screen.Enter(_options.Args);
    }

    /// <inheritdoc />
    protected override void OnBeginExit() => _open.Retarget(0f, Theme.Number(NavTokens.MotionOverlay));

    /// <inheritdoc />
    internal override bool HandleBack()
    {
        if (_options.CancelableByBack) RequestClose(null);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>予測型の戻るのプレビュー（3b）で縮めるのは板。</remarks>
    protected override GameObject BackPreviewNode => _panel;

    /// <inheritdoc />
    protected override bool ClosesOnBack => _options.CancelableByBack;

    /// <inheritdoc />
    /// <remarks>
    /// 板の上の辺を留めて縮める（真ん中の周りに縮めた後、減った高さの半分だけ上へ戻す。板の高さはレイアウトの結果の LayoutSize〈1 フレーム遅れ。
    /// 開いている板は動かない〉。読めなければ留めない）。ずらしは DragUp の引いたずらしと足して当てる（ApplyDrag）。
    /// </remarks>
    protected override void ApplyBackPreviewPose(BackPreviewPose pose)
    {
        NavNode.SetVisualScale(_panel, new Vector2(pose.Scale, pose.Scale));
        float height = _panel.GetComponent<CanvasTransform>() is { } ct ? ct.LayoutSize.y : 0f;
        _previewOffset = BackPreviewMath.AnchorOffset(height, pose.Scale, BackPreviewAnchor.Top);
        ApplyDrag();
    }

    /// <summary>開き具合 → 板のずらし（自分の高さに対する割合）と幕の濃さ。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionOverlayCurve, UiCurve.EaseOut).Evaluate(linear);
        NavNode.SetFraction(_panel, new Vector2(0f, HiddenFraction * (1f - p)));
        NavNode.SetSpriteColor(_scrim, Theme.Color(NavTokens.ColorScrim).WithAlpha(Theme.Number(NavTokens.OpacityScrim) * p));
    }

    /// <summary>引いた距離 → 板のずらし（キャンバスの単位。上へ引くほど上へ）。予測型の戻るのプレビューの上の辺を留めるずらしも足す（3b）。</summary>
    private void ApplyDrag() => NavNode.SetTranslate(_panel, new Vector2(0f, -_dragOffset + _previewOffset));

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (_panel.GetComponent<Sprite>() is { } panel)
        {
            float r = Theme.Number(NavTokens.RadiusSheet);
            panel.Color = Theme.Color(UiTokens.ColorSurface);
            panel.CornerRadii = new CornerRadii(0f, 0f, r, r);
        }
        if (gameObject.FindChild(HandleChild).GetComponent<Sprite>() is { } handle)
        {
            handle.Color = Theme.Color(UiTokens.ColorOnSurfaceMuted);
            handle.Size = new Vector2(Theme.Number(NavTokens.SizeHandleWidth), Theme.Number(NavTokens.SizeHandleHeight));
            handle.CornerRadius = Theme.Number(NavTokens.SizeHandleHeight) / 2f;
        }
    }
}
