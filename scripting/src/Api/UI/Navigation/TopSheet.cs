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
// ============================================================

/// <summary>上からの覆い。</summary>
public sealed class TopSheet : ModalPlane
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string PanelChild = "Panel";
    private const string ContentChild = "Panel/Content";
    private const string HandleChild = "Panel/HandleRow/Handle";
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
        EnterContent();
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

    /// <summary>準備: 中身ができあがったら見せて降りる（中身の高さが並びに入ってから動かす）。</summary>
    private void Prepare()
    {
        Redraw.Request();
        bool contentReady = !_contentRoot.IsValid || NavNode.IsBuilt(_contentRoot);
        if (!contentReady && ++_prepareFrames <= MaxPrepareFrames) return;
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        _open.Retarget(1f, Theme.Number(NavTokens.MotionOverlay));
    }

    /// <summary>降りる・上がる動き。</summary>
    private void Animate(float dt)
    {
        ApplyOpen(_open.Advance(dt));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else FinishClose();
    }

    /// <summary>開いている間: 離した後の戻る動き（DragUp）。</summary>
    private void Settle(float dt)
    {
        if (_dragging || !_settle.IsRunning) return;
        _dragOffset = _settle.Advance(dt);
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

    /// <summary>開き具合 → 板のずらし（自分の高さに対する割合）と幕の濃さ。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionOverlayCurve, UiCurve.EaseOut).Evaluate(linear);
        NavNode.SetFraction(_panel, new Vector2(0f, HiddenFraction * (1f - p)));
        NavNode.SetSpriteColor(_scrim, Theme.Color(NavTokens.ColorScrim).WithAlpha(Theme.Number(NavTokens.OpacityScrim) * p));
    }

    /// <summary>引いた距離 → 板のずらし（キャンバスの単位。上へ引くほど上へ）。</summary>
    private void ApplyDrag() => NavNode.SetTranslate(_panel, new Vector2(0f, -_dragOffset));

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
