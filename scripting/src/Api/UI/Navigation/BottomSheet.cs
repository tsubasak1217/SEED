using System;

namespace SEED.UI;

// ============================================================
//  BottomSheet.cs — 下からのシート（段: 半分・全体。つまみ・板のドラッグとフリックで閉じる。W2-7。docs/ui_navigation.md §3.3）
//
//  【プレハブ】templates/ui/prefabs/bottom_sheet.actor（ModalHost が Sheets の帯の下に作る）:
//      BottomSheet（Canvas・親に合わせる・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる。見た目だけ）
//      └─ Track（Canvas・親に合わせる・CanvasScroll〈縦・端で止まる・慣性・間隔のスナップ・入れ子で渡さない〉・CanvasStack〈縦〉）
//         ├─ Gap（Sprite〈透明〉・高さ = 領域・CanvasGesture〈タップ〉・GestureRelay）…… 幕の上のタップ = 閉じる
//         └─ Panel（Canvas・Sprite〈面・上の角丸〉・高さ = シート・CanvasStack〈縦〉）
//            ├─ HandleRow └─ Handle（つまみ）
//            └─ Content（ここへ中身のプレハブを作る）
//  スクロールの位置 = 板の出ている高さ（0 = 閉じた）。段と閉じる判定は SheetMath（純粋な計算）、指のドラッグ・フリックの速度から
//  止まる段を決めるのは CanvasScroll の Interval のスナップ（W2-2 の速度・W2-3 の物理。Rust）。中身の縦の一覧が先頭で下へ引かれた
//  残りは Track へ渡る（W2-3 の入れ子の受け渡し）。止まった位置が 0 なら閉じる。
//  【安全領域】板の下の余白 = 画面の下端から安全領域までの高さ（dp。Screen.DpScale で換算）。板の背景は画面の端まで。
//  スクロールの窓の中の位置は安全領域の計算に入れない（CanvasSafeArea を板に付けると動かしている間に縮み直すため、C# で余白を当てる）。
// ============================================================

/// <summary>下からのシート。</summary>
public sealed class BottomSheet : ModalPlane
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string TrackChild = "Track";
    private const string GapChild = "Track/Gap";
    private const string PanelChild = "Track/Panel";
    private const string ContentChild = "Track/Panel/Content";
    private const string HandleChild = "Track/Panel/HandleRow/Handle";
    /// <summary>大きさ・余白が変わったとみなす差（キャンバスの単位）。</summary>
    private const float MetricsEpsilon = 0.5f;
    /// <summary>準備（大きさの測り・中身の作成）を待つフレームの上限。</summary>
    private const int MaxPrepareFrames = 120;

    /// <inheritdoc />
    public override ModalKind Kind => ModalKind.Sheet;

    /// <summary>シーンの ModalHost で下からのシートを開く（ModalHost が無ければ null・警告）。</summary>
    public static ModalHandle? Show(SheetOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowSheet(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>指定。</summary>
    private SheetOptions _options = new();
    /// <summary>子のノード。</summary>
    private GameObject _scrim, _track, _gap, _panel, _content;
    /// <summary>中身の根（作ったら）。</summary>
    private GameObject _contentRoot;
    /// <summary>中身の画面のスクリプトへ値を渡したか。</summary>
    private bool _contentEntered;
    /// <summary>隙間のタップをつないだか。</summary>
    private GestureRelay? _gapRelay;
    /// <summary>測った領域の高さ・板の高さ・下の余白（0 = まだ）。</summary>
    private float _area, _panelHeight, _bottomInset;
    /// <summary>大きさを当てた後、中身の大きさが反映されるのを待っているか。</summary>
    private bool _sizesApplied;
    /// <summary>準備を待ったフレーム。</summary>
    private int _prepareFrames;
    /// <summary>閉じる向きの ScrollTo を出したか。</summary>
    private bool _exitScrolled;

    /// <summary>今の段（位置から）。</summary>
    public SheetDetent Detent => SheetMath.Classify(Position, _panelHeight, _options.HalfDetent);

    /// <summary>板の出ている高さ（スクロールの位置）。</summary>
    private float Position => _track.GetComponent<CanvasScroll>() is { } s ? s.Position.y : 0f;

    /// <summary>段へ動かす（開いている間。スクリプトから）。</summary>
    public void SnapTo(SheetDetent detent)
    {
        if (Phase is not (ModalPhase.Open or ModalPhase.Entering) || detent is SheetDetent.Between) return;
        if (detent == SheetDetent.Closed)
        {
            RequestClose(null);
            return;
        }
        ScrollTo(SheetMath.DetentPosition(detent, _panelHeight));
    }

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as SheetOptions ?? new SheetOptions();
        _scrim = gameObject.FindChild(ScrimChild);
        _track = gameObject.FindChild(TrackChild);
        _gap = gameObject.FindChild(GapChild);
        _panel = gameObject.FindChild(PanelChild);
        _content = gameObject.FindChild(ContentChild);
        NavNode.SetSpriteColor(_scrim, Theme.Color(NavTokens.ColorScrim).WithAlpha(0f));
        // 板は隙間（プレハブの既定は十分に高い）の下＝画面の外にあり、幕も透明なので、すぐ見せて大きさを測らせる
        NavNode.SetVisible(Owner, true);
        if (_options.ContentPrefab.Length > 0)
        {
            _contentRoot = GameObject.Instantiate(_options.ContentPrefab, _content.IsValid ? _content : _panel);
            if (!_contentRoot.IsValid) Debug.LogError($"{LogPrefix} シートの中身を作れません: {_options.ContentPrefab}");
        }
    }

    /// <inheritdoc />
    protected override void OnPlaneUpdate(float dt)
    {
        BindGap();
        EnterContent();
        Measure();
        switch (Phase)
        {
            case ModalPhase.Preparing:
                Prepare();
                break;
            case ModalPhase.Entering:
            case ModalPhase.Open:
                UpdateOpen();
                break;
            case ModalPhase.Exiting:
                UpdateExit();
                break;
        }
    }

    /// <summary>隙間のタップをつなぐ。</summary>
    private void BindGap()
    {
        if (_gapRelay is not null || Of<GestureRelay>(_gap) is not { } relay) return;
        _gapRelay = relay;
        relay.Tapped += (_, _) =>
        {
            if (_options.DismissOnScrimTap && Phase is ModalPhase.Open or ModalPhase.Entering) RequestClose(null);
        };
    }

    /// <summary>中身の画面のスクリプト（UiScreen）へ値を渡す（1 回）。</summary>
    private void EnterContent()
    {
        if (_contentEntered || !_contentRoot.IsValid || Of<UiScreen>(_contentRoot) is not { } screen) return;
        _contentEntered = true;
        screen.Enter(_options.Args);
    }

    /// <summary>
    /// 窓（Track）の大きさを測り、隙間・板の高さと下の余白・スナップの間隔を当てる（回転などで変わったら当て直す）。
    /// </summary>
    private void Measure()
    {
        if (_track.GetComponent<CanvasScroll>() is not { } scroll || !scroll.HasMetrics) return;
        float area = scroll.ViewportSize.y;
        float inset = SafeInsets.BottomUnits();
        if (area <= 0f || (MathF.Abs(area - _area) < MetricsEpsilon && MathF.Abs(inset - _bottomInset) < MetricsEpsilon)) return;
        _area = area;
        _bottomInset = inset;
        float fraction = _options.HeightFraction > 0f ? _options.HeightFraction : Theme.Number(NavTokens.RatioSheetMaxHeight, 1f);
        _panelHeight = SheetMath.PanelHeight(area, fraction);
        if (_gap.GetComponent<CanvasLayoutItem>() is { } gapItem) gapItem.PreferredSize = new Vector2(0f, area);
        if (_panel.GetComponent<CanvasLayoutItem>() is { } panelItem) panelItem.PreferredSize = new Vector2(0f, _panelHeight);
        // 高さはコンテナが伸ばさない軸なので、スプライト（隙間の当たりの板・板の背景）の高さも合わせる
        // （隙間のスプライトがプレハブの高さのままだと、板の上のタップが隙間＝閉じるになる）
        if (_gap.GetComponent<Sprite>() is { } gapSprite) gapSprite.Height = area;
        if (_panel.GetComponent<Sprite>() is { } panelSprite) panelSprite.Height = _panelHeight;
        if (_panel.GetComponent<CanvasStack>() is { } stack) stack.Padding = new CanvasPadding(0f, 0f, 0f, inset);
        scroll.SnapInterval = SheetMath.SnapInterval(_panelHeight, _options.HalfDetent);
        _sizesApplied = true;
        Redraw.Request();
    }

    /// <summary>準備: 大きさが反映されたら（中身の長さ = 隙間 + 板）開く段へ動かす。</summary>
    private void Prepare()
    {
        Redraw.Request();
        if (++_prepareFrames > MaxPrepareFrames)
        {
            Debug.LogWarning($"{LogPrefix} シートの大きさを測れません（{MaxPrepareFrames} フレーム）。閉じます");
            RequestClose(null);
            return;
        }
        if (!_sizesApplied || _track.GetComponent<CanvasScroll>() is not { } scroll) return;
        if (MathF.Abs(scroll.MaxPosition.y - _panelHeight) > MetricsEpsilon) return;
        BeginEnter();
        ScrollTo(SheetMath.DetentPosition(SheetMath.OpenDetent(_options), _panelHeight));
    }

    /// <summary>開いている間: 幕の濃さを位置に合わせ、止まったら段を確かめる（0 なら閉じる）。</summary>
    private void UpdateOpen()
    {
        ApplyScrim();
        if (_track.GetComponent<CanvasScroll>() is not { } scroll || scroll.IsScrolling || scroll.IsDragging) return;
        var detent = SheetMath.Classify(scroll.Position.y, _panelHeight, _options.HalfDetent);
        if (Phase == ModalPhase.Entering && detent != SheetDetent.Closed) EndEnter();
        else if (Phase == ModalPhase.Open && detent == SheetDetent.Closed)
        {
            // 指で閉じた段まで下ろした（つまみ・板のドラッグ・下へのフリック・中身の一覧から渡った残り）
            RequestClose(null);
        }
    }

    /// <summary>閉じる途中: 閉じた位置に止まったら消す。</summary>
    private void UpdateExit()
    {
        ApplyScrim();
        if (_track.GetComponent<CanvasScroll>() is not { } scroll) { FinishClose(); return; }
        if (!_exitScrolled) ScrollTo(0f);
        if (scroll.IsScrolling || SheetMath.Classify(scroll.Position.y, _panelHeight, _options.HalfDetent) != SheetDetent.Closed) return;
        FinishClose();
    }

    /// <inheritdoc />
    protected override void OnBeginExit()
    {
        _exitScrolled = false;
        if (_track.GetComponent<CanvasScroll>() is { } scroll && MathF.Abs(scroll.Position.y) > SheetMath.DetentTolerance) ScrollTo(0f);
        else _exitScrolled = true;
    }

    /// <inheritdoc />
    internal override bool HandleBack()
    {
        if (_options.CancelableByBack) RequestClose(null);
        return true;
    }

    /// <summary>位置へ motion.sheet 秒で動かす（曲線は CanvasScroll の ScrollTo の easeInOut）。</summary>
    private void ScrollTo(float position)
    {
        if (_track.GetComponent<CanvasScroll>() is not { } scroll) return;
        scroll.ScrollTo(new Vector2(0f, position), Theme.Number(NavTokens.MotionSheet));
        if (Phase == ModalPhase.Exiting) _exitScrolled = true;
        Redraw.Request();
    }

    /// <summary>幕の濃さ（最初の段まで開く間に 0 → opacity.scrim）。</summary>
    private void ApplyScrim()
    {
        float alpha = SheetMath.ScrimAlpha(Position, _panelHeight, _options.HalfDetent, Theme.Number(NavTokens.OpacityScrim));
        NavNode.SetSpriteColor(_scrim, Theme.Color(NavTokens.ColorScrim).WithAlpha(alpha));
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (_panel.GetComponent<Sprite>() is { } panel)
        {
            float r = Theme.Number(NavTokens.RadiusSheet);
            panel.Color = Theme.Color(UiTokens.ColorSurface);
            panel.CornerRadii = new CornerRadii(r, r, 0f, 0f);
        }
        if (gameObject.FindChild(HandleChild).GetComponent<Sprite>() is { } handle)
        {
            handle.Color = Theme.Color(UiTokens.ColorOnSurfaceMuted);
            handle.Size = new Vector2(Theme.Number(NavTokens.SizeHandleWidth), Theme.Number(NavTokens.SizeHandleHeight));
            handle.CornerRadius = Theme.Number(NavTokens.SizeHandleHeight) / 2f;
        }
    }
}
