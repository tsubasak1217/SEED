using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  WheelPicker.cs — ホイールの列（上下に流れて中央の行が選ばれる。W2-5。docs/ui_components.md §11）
//
//  【プレハブ】templates/ui/prefabs/wheel_picker.actor
//      WheelPicker（Sprite = 列の大きさ〈透明〉・CanvasGesture〈タップ。押下の見た目なし〉・このスクリプト）
//      ├─ Band（Sprite = 中央の帯〈角丸〉。ShowBand が false なら隠す＝時刻ホイールは全列にまたがる帯を自分で持つ）
//      ├─ Viewport（Sprite = 窓〈透明〉・CanvasClip・CanvasScroll〈縦・Interval のスナップ・入れ子で渡さない〉）
//      │   └─（行: ListView が wheel_row.actor から作る）
//      └─ Blocker（Sprite〈透明〉・受けるジェスチャーの無い CanvasGesture＝遮る板。無効の間だけ見せて指を取らせない）
//  【分担】スクロール（ドラッグ・慣性・跳ね返り・行の高さごとのスナップ）はエンジンの CanvasScroll（W2-3）。この部品は
//    - 行の並び（先頭の余白 =（窓 − 行）/ 2 で、位置 = 行 × 行の高さ のとき行が中央）と、端をつなげる循環（WheelLoop）
//    - 行の曲面の見た目（WheelLook の純関数を WheelRows が行ごとに当てる）と中央の帯
//    - 中央の行が変わったら SelectionChanged（と軽い触感 Haptics.Tap。指とその慣性の間だけ・1 フレーム 1 回）、止まったら Settled
//    - 止まった行が選べない（範囲の外）なら最も近い選べる行へ戻す（Flutter の CupertinoDatePicker と同じ 200ms）
//    - タップした行へ動かす（止まっている間だけ。動いている間のタップは止めるだけ。Flutter の tap-to-scroll と同じ 300ms）
//    - キーボードの上下の矢印（最後に触れたホイール = WheelFocus）・スクリプトからの値の設定（動きあり・なし）
//  値は 2 通り: 数の範囲（Min〜Max・刻み Step・書式。インスペクタで作れる）か、Configure で項目の数と文字を渡す（時刻ホイールの列）。
//  状態 → 見た目: 帯・文字の色と大きさ（テーマのトークン）は ApplyLook、位置で変わる行の見た目は毎フレーム WheelRows.Apply。
// ============================================================

/// <summary>ホイールの列（中央の行が選ばれる）。</summary>
public sealed class WheelPicker : UiWidget
{
    /// <summary>子の窓の名前。</summary>
    private const string ViewportChild = "Viewport";
    /// <summary>子の中央の帯の名前。</summary>
    private const string BandChild = "Band";
    /// <summary>子の遮る板の名前。</summary>
    private const string BlockerChild = "Blocker";
    /// <summary>行のプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultRowPrefab = "assets://ui/prefabs/wheel_row.actor";
    /// <summary>半分。</summary>
    private const float Half = 0.5f;
    /// <summary>窓の高さ・中身の長さが変わったとみなす差（キャンバスの単位）。</summary>
    private const float MetricsEpsilon = 0.5f;
    /// <summary>
    /// 準備（窓の大きさ・中身の長さ・行の作成）を待つ間に描き続けを頼むフレームの上限（60 fps で 1 秒）。祖先が隠れていて
    /// 測れない・行のプレハブが無いときに、描画を止められなくなる（render_policy: on_demand の電池）ことを防ぐ。
    /// </summary>
    private const int MaxWaitRedrawFrames = 60;

    // ── 値（数の範囲。Configure を呼ばないときに使う）──────────────
    /// <summary>最小の値。</summary>
    [SerializeField(Label = "最小")]
    public int Min;
    /// <summary>最大の値（Min から Step ずつの値のうち、これを超えないものまで）。</summary>
    [SerializeField(Label = "最大")]
    public int Max = 59;
    /// <summary>値の刻み（1 以上）。</summary>
    [SerializeField(Label = "刻み")]
    public int Step = 1;
    /// <summary>値（最初の値。動かすと中央の行の値に合わせて書き換わる）。</summary>
    [SerializeField(Label = "値")]
    public int Value;
    /// <summary>表示の書式（.NET の数の書式。"00" なら 2 桁）。</summary>
    [SerializeField(Label = "書式")]
    public string Format = "00";
    /// <summary>表示の後ろの文字（例 "分"）。</summary>
    [SerializeField(Label = "単位")]
    public string Suffix = "";
    /// <summary>端をつなげる（59 の次が 00）。</summary>
    [SerializeField(Label = "端をつなげる")]
    public bool Looping = true;
    /// <summary>選べる値を SelectableMin〜SelectableMax に絞る（外の行は灰色で、止まると最も近い選べる行へ戻る）。</summary>
    [SerializeField(Label = "選べる範囲を絞る")]
    public bool LimitSelectable;
    /// <summary>選べる最小の値。</summary>
    [SerializeField(Label = "選べる最小")]
    public int SelectableMin;
    /// <summary>選べる最大の値。</summary>
    [SerializeField(Label = "選べる最大")]
    public int SelectableMax = 59;

    // ── 振る舞い ──────────────────────────────────────────────
    /// <summary>中央の行が変わるたびに軽い触感（Android の端末だけ。指とその慣性の間だけ）。</summary>
    [SerializeField(Label = "触感")]
    public bool Haptic = true;
    /// <summary>キーボードの上下の矢印で動かす（最後に触れたホイールだけ）。</summary>
    [SerializeField(Label = "キーボード")]
    public bool Keyboard = true;
    /// <summary>中央の帯を描く（時刻ホイールの列は false。帯は時刻ホイールが全列にまたがって描く）。</summary>
    [SerializeField(Label = "中央の帯")]
    public bool ShowBand = true;

    // ── 見た目（0 はテーマのトークン）────────────────────────────
    /// <summary>行の高さ（0 = テーマの size.wheel_item）。</summary>
    [SerializeField(Label = "行の高さ（0 = テーマ）")]
    public float ItemExtent;
    /// <summary>文字の大きさ（0 = テーマの text.wheel）。</summary>
    [SerializeField(Label = "文字の大きさ（0 = テーマ）")]
    public float TextSize;
    /// <summary>円柱の直径 ÷ 窓の高さ。</summary>
    [SerializeField(Label = "円柱の直径の比")]
    public float DiameterRatio = WheelLookParams.CupertinoDiameterRatio;
    /// <summary>遠近の強さ（0〜0.01）。</summary>
    [SerializeField(Label = "遠近")]
    public float Perspective = WheelLookParams.CupertinoPerspective;
    /// <summary>行の詰め具合（大きいほど多くの行が見える）。</summary>
    [SerializeField(Label = "詰め具合")]
    public float Squeeze = WheelLookParams.DatePickerSqueeze;
    /// <summary>中央の行の拡大（1 = 拡大しない）。</summary>
    [SerializeField(Label = "中央の拡大")]
    public float Magnification = WheelLookParams.DatePickerMagnification;
    /// <summary>円柱の面の傾きで暗くする強さ（0〜1）。</summary>
    [SerializeField(Label = "面の傾きの暗さ")]
    public float EdgeShade = WheelLookParams.DefaultEdgeShade;
    /// <summary>行のプレハブ（子に Label〈Text〉を持つ .actor）。</summary>
    [SerializeField(Label = "行のプレハブ"), AssetReference("actor")]
    public string RowPrefab = DefaultRowPrefab;

    /// <summary>中央の行（項目）が変わった（指・慣性・スクリプトの動きの途中も。新しい項目の番号）。</summary>
    public event Action<WheelPicker, int>? SelectionChanged;
    /// <summary>止まった（止まった項目の番号。選べない行に止まったときは戻り終えてから）。</summary>
    public event Action<WheelPicker, int>? Settled;

    /// <summary>項目の数。</summary>
    public int Count => _count;
    /// <summary>端をつなげているか。</summary>
    public bool IsLooping => _looping;
    /// <summary>中央の項目（最後に見た値。まだ置いていなければ置く予定の項目）。</summary>
    public int SelectedIndex => _selectedItem >= 0 ? _selectedItem : Math.Max(0, _pendingItem);
    /// <summary>中央の値（数の範囲のとき。Configure したときは項目の番号）。</summary>
    public int SelectedValue => _labelOf is null ? ValueOfItem(SelectedIndex) : SelectedIndex;
    /// <summary>動いている（ドラッグ・慣性・スナップ・スクリプトの動き・位置を書いた直後）。</summary>
    public bool IsMoving => _wasMoving || _requestIssued;
    /// <summary>指で触れている（ドラッグ中・動きを指で止めている）。</summary>
    public bool IsUserInteracting { get; private set; }
    /// <summary>行が置けて、値を設定するとすぐ動かせる状態か（窓の大きさが分かり、置く予定の行を置き終えた）。</summary>
    public bool IsReady => _rows is not null && _viewport > 0f && !_layoutDirty && !_awaitingContent && _pendingItem < 0;
    /// <summary>使っている行の高さ（テーマか ItemExtent）。</summary>
    public float ResolvedItemExtent => WheelLoop.SanitizeExtent(ItemExtent > 0f ? ItemExtent : Theme.Number(UiTokens.SizeWheelItem, WheelLoop.MinItemExtent));
    /// <summary>曲面の見た目の値（フィールドとテーマから）。</summary>
    public WheelLookParams LookParams => new WheelLookParams
    {
        DiameterRatio = DiameterRatio,
        Perspective = Perspective,
        Squeeze = Squeeze,
        Magnification = Magnification,
        DimOpacity = Theme.Number(UiTokens.OpacityWheelDim, WheelLookParams.CupertinoDimOpacity),
        EdgeShade = EdgeShade,
    }.Sanitized();

    // ── 状態 ──────────────────────────────────────────────────
    /// <summary>項目の数。</summary>
    private int _count;
    /// <summary>項目 → 文字（null なら数の範囲の書式）。</summary>
    private Func<int, string>? _labelOf;
    /// <summary>項目 → 選べるか（null ならすべて。数の範囲の絞り込みと両方を満たす項目だけ選べる）。</summary>
    private Func<int, bool>? _enabledOf;
    /// <summary>端をつなげるか。</summary>
    private bool _looping;
    /// <summary>周の数と行の数。</summary>
    private int _cycles = 1, _totalRows;
    /// <summary>行の高さ・窓の高さ・列の幅（キャンバスの単位）。</summary>
    private float _extent, _viewport, _width;
    /// <summary>窓（CanvasScroll を持つ子）。</summary>
    private GameObject _viewportObject;
    /// <summary>行。</summary>
    private WheelRows? _rows;
    /// <summary>並び（余白・中身の長さ・前もって作る範囲）を作り直す必要があるか。</summary>
    private bool _layoutDirty = true;
    /// <summary>書いた中身の長さがエンジンの測った値に届くのを待っているか（中身の大きさは 1 フレーム遅れて効く）。</summary>
    private bool _awaitingContent;
    /// <summary>待っている中身の長さ。</summary>
    private float _expectedContent;
    /// <summary>置けるようになったら黙って置く項目（−1 = 無し）。</summary>
    private int _pendingItem = -1;
    /// <summary>中央の項目（−1 = まだ）。</summary>
    private int _selectedItem = -1;
    /// <summary>前のフレームで動いていたか。</summary>
    private bool _wasMoving;
    /// <summary>このフレームで位置を書いた（ScrollTo・Jump）。次のフレームは動いているとみなし、止まったら Settled を出す。</summary>
    private bool _requestIssued;
    /// <summary>スクリプト・キーボード・タップの動きの行き先の行（連続のキーで行き先を積み上げる。指で触れたら消す）。</summary>
    private int? _animTargetRow;
    /// <summary>前のフレームで止まっていたか（動いている間のタップは止めるだけにする）。</summary>
    private bool _restingBefore = true;
    /// <summary>最後に行の見た目を当てた位置（変わらなければ当て直さない）。</summary>
    private float _appliedPosition = float.NaN;
    /// <summary>行の見た目を当て直す必要があるか（テーマ・幅・選べる行が変わった）。</summary>
    private bool _looksDirty = true;
    /// <summary>行の文字の共通の見た目。</summary>
    private WheelRowStyle _style;
    /// <summary>行の番号 → 選べるか（毎フレームの見た目の当てで使う。作り直さないよう覚えておく）。</summary>
    private Func<int, bool>? _rowEnabled;
    /// <summary>準備を待って描き続けを頼んだフレームの数（上限 <see cref="MaxWaitRedrawFrames"/>）。</summary>
    private int _waitFrames;
    /// <summary>行が作れないことを知らせたか（1 度だけ）。</summary>
    private bool _warnedRows;

    // ── スクリプトからの操作 ───────────────────────────────────

    /// <summary>
    /// 項目の数と文字を渡して作り直す（時刻ホイールの列など。数の範囲のフィールドは使わなくなる）。
    /// 置き直しは黙って行う（SelectionChanged・Settled を出さない）。
    /// </summary>
    /// <param name="count">項目の数。</param>
    /// <param name="labelOf">項目 → 文字。</param>
    /// <param name="looping">端をつなげるか。</param>
    /// <param name="selectedIndex">中央に置く項目。</param>
    public void Configure(int count, Func<int, string> labelOf, bool looping, int selectedIndex)
    {
        _count = Math.Max(0, count);
        _labelOf = labelOf;
        _looping = looping;
        _selectedItem = -1;
        _pendingItem = ClampItem(selectedIndex);
        _layoutDirty = true;
        _rows?.Relabel();
        Redraw.Request();
    }

    /// <summary>文字を作り直す（項目の数は同じ。表記の切り替えなど）。</summary>
    public void SetLabels(Func<int, string> labelOf)
    {
        _labelOf = labelOf;
        _rows?.Relabel();
        _looksDirty = true;
        Redraw.Request();
    }

    /// <summary>項目ごとの選べるかを渡す（null ですべて選べる）。灰色の表示と止まったときの戻りに効く。</summary>
    public void SetItemEnabled(Func<int, bool>? enabledOf)
    {
        _enabledOf = enabledOf;
        _looksDirty = true;
        Redraw.Request();
    }

    /// <summary>項目が選べるか。</summary>
    public bool IsItemEnabled(int item)
    {
        if (item < 0 || item >= _count) return false;
        if (_labelOf is null && LimitSelectable)
        {
            int v = ValueOfItem(item);
            if (v < Math.Min(SelectableMin, SelectableMax) || v > Math.Max(SelectableMin, SelectableMax)) return false;
        }
        return _enabledOf?.Invoke(item) ?? true;
    }

    /// <summary>
    /// 項目を中央へ動かす（端をつなげるなら近い向きへ回る）。置ける前（最初の描画の前）なら置けるようになったとき黙って置く。
    /// </summary>
    /// <param name="index">項目。</param>
    /// <param name="animate">true なら motion.wheel 秒で動かす（Flutter の animateToItem と同じ easeInOut）、false ならすぐ移す。</param>
    public void SelectIndex(int index, bool animate = true) => MoveToItem(ClampItem(index), animate, Theme.Number(UiTokens.MotionWheel));

    /// <summary>値を中央へ動かす（数の範囲のとき。最も近い項目へ）。</summary>
    public void SetValue(int value, bool animate = true) => SelectIndex(ItemOfValue(value), animate);

    /// <summary>選べる項目を delta 個進める（キーボードの上下と同じ。端をつながないなら端で止まる）。</summary>
    public void StepBy(int delta, bool animate = true)
    {
        int from = _animTargetRow is { } row ? WheelLoop.ItemOfRow(row, Math.Max(1, _count)) : SelectedIndex;
        int to = WheelLoop.StepItem(from, delta, _count, _looping, IsItemEnabled);
        if (to != from) SelectIndex(to, animate);
    }

    /// <summary>キーボードの相手にする。</summary>
    public void Focus() => WheelFocus.Set(this);

    /// <summary>列の幅を変える（時刻ホイールが列を並べ直すとき）。</summary>
    public void SetWidth(float width)
    {
        if (!float.IsFinite(width) || width <= 0f || MathF.Abs(width - _width) < MetricsEpsilon) return;
        _width = width;
        if (SpriteOf() is { } root) root.Width = width;
        if (SpriteOf(ViewportChild) is { } vp) vp.Width = width;
        if (SpriteOf(BlockerChild) is { } blocker) blocker.Width = width;
        _rows?.Restyle();
        _looksDirty = true;
        Refresh();
    }

    // ── 部品の土台（UiWidget）───────────────────────────────────

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        _width = SpriteOf() is { } root ? root.Width : 0f;
        // 数の範囲のフィールドから項目を作る（Configure が呼ばれたらそちらで置き換わる）
        if (_labelOf is null) ConfigureFromFields();
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy() => WheelFocus.Clear(this);

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 隠した列（24 時間表記の午前/午後）は動かさない
        if (!gameObject.Visible) return;
        if (!EnsureParts(out var scroll)) return;
        // 窓の大きさは最初の描画の後に分かる。分かるまで・中身の長さが効くまでは行を置かない（描き続けて待つ）
        if (!scroll.HasMetrics)
        {
            RequestWhileWaiting();
            return;
        }
        float viewport = scroll.ViewportSize.y;
        if (MathF.Abs(viewport - _viewport) > MetricsEpsilon)
        {
            // 窓の高さが分かった・変わった: 並びを作り直し、帯を窓の中央へ置き直す
            _viewport = viewport;
            _layoutDirty = true;
            Refresh();
        }
        if (_layoutDirty) ApplyLayout(scroll);
        if (_awaitingContent)
        {
            if (MathF.Abs(scroll.ContentSize.y - _expectedContent) > MetricsEpsilon)
            {
                RequestWhileWaiting();
                return;
            }
            _awaitingContent = false;
        }
        if (_pendingItem >= 0) PlaceSilently(scroll, _pendingItem);

        TrackMotion(scroll);
        UpdateRows(scroll);
        HandleKeyboard();
    }

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled || !IsReady || _count <= 0) return;
        if (_viewportObject.GetComponent<CanvasScroll>() is not { } scroll) return;
        // 動いている間（慣性・スナップ・スクリプトの動き・指で止めた直後）のタップは止めるだけ（行を選ばない）
        if (!_restingBefore || scroll.Phase != ScrollPhase.Idle) return;
        // 押した位置が映っている行を、曲面の見た目の逆で求める
        float visual = e.LocalPosition.y - _viewport * Half;
        float distance = WheelLook.DistanceAtOffset(visual, _viewport, LookParams);
        int row = WheelLoop.RowAtPosition(scroll.Position.y + distance, _extent, _totalRows);
        int item = ItemOfRow(row);
        WheelFocus.Set(this);
        if (!IsItemEnabled(item) || row == WheelLoop.RowAtPosition(scroll.Position.y, _extent, _totalRows)) return;
        StartMotionToRow(scroll, row, animate: true, Theme.Number(UiTokens.MotionWheel));
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        bool disabled = !IsEnabled;
        float fade = disabled ? Theme.Number(UiTokens.OpacityDisabled) : 1f;
        _style = new WheelRowStyle
        {
            FontSize = TextSize > 0f ? TextSize : Theme.Number(UiTokens.TextWheel),
            Enabled = Theme.Color(UiTokens.ColorOnSurface),
            Disabled = Theme.Color(UiTokens.ColorOnDisabled),
            Fade = fade,
        };
        float extent = ResolvedItemExtent;
        if (MathF.Abs(extent - _extent) > float.Epsilon)
        {
            // 行の高さが変わった（テーマの差し替え）: 並びを作り直して、中央の項目を置き直す
            _extent = extent;
            _layoutDirty = true;
            if (_selectedItem >= 0) _pendingItem = _selectedItem;
        }
        float height = _viewport > 0f ? _viewport : (SpriteOf() is { } root ? root.Height : 0f);
        ApplyBandLook(SpriteOf(BandChild), TransformOf(BandChild), ShowBand, _width, height, extent, LookParams, Theme, fade);
        // 無効の間は遮る板を見せて、窓のスクロールに指を渡さない
        var blocker = gameObject.FindChild(BlockerChild);
        if (blocker.IsValid && blocker.Visible != disabled) blocker.Visible = disabled;
        _rows?.Restyle();
        _looksDirty = true;
    }

    /// <summary>
    /// 中央の帯の見た目を当てる（時刻ホイールの全列にまたがる帯も同じ関数で）。帯の高さ = 行の高さ × 拡大、左右の余白 size.wheel_band_inset。
    /// </summary>
    internal static void ApplyBandLook(Sprite? band, CanvasTransform? ct, bool show, float width, float height, float extent,
        in WheelLookParams look, UiThemeData theme, float fade)
    {
        if (band is not { } sprite) return;
        if (!show)
        {
            sprite.Color = UiColorMath.Transparent;
            return;
        }
        float inset = theme.Number(UiTokens.SizeWheelBandInset);
        float bandHeight = WheelLook.BandHeight(extent, look);
        sprite.Color = UiColorMath.FadeAlpha(theme.Color(UiTokens.ColorSurfaceVariant), fade);
        sprite.CornerRadius = theme.Number(UiTokens.RadiusWheelBand);
        sprite.Size = new Vector2(Math.Max(0f, width - inset * 2f), bandHeight);
        if (ct is { } t) t.Position = new Vector2(inset, (height - bandHeight) * Half);
    }

    // ── 中身 ──────────────────────────────────────────────────

    /// <summary>数の範囲のフィールドから項目を作る。</summary>
    private void ConfigureFromFields()
    {
        int step = Math.Max(1, Step);
        int lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
        _count = (hi - lo) / step + 1;
        _looping = Looping;
        _pendingItem = ClampItem(ItemOfValue(Value));
        _layoutDirty = true;
    }

    /// <summary>窓・行を引き、行の一覧を作る（プレハブの子がそろうまで false）。</summary>
    private bool EnsureParts(out CanvasScroll scroll)
    {
        scroll = default;
        if (_rows is null)
        {
            var vp = gameObject.FindChild(ViewportChild);
            if (!vp.IsValid || vp.GetComponent<CanvasScroll>() is null) return false;
            _viewportObject = vp;
            _rows = new WheelRows(vp, string.IsNullOrEmpty(RowPrefab) ? DefaultRowPrefab : RowPrefab, LabelOfRow);
        }
        if (_viewportObject.GetComponent<CanvasScroll>() is not { } s) return false;
        scroll = s;
        return true;
    }

    /// <summary>並び（余白・行の数・中身の長さ・スナップ・前もって作る範囲）を作り直し、中身の長さが効くのを待つ。</summary>
    private void ApplyLayout(CanvasScroll scroll)
    {
        if (_rows is null) return;
        _extent = ResolvedItemExtent;
        float lead = WheelLoop.LeadPadding(_viewport, _extent);
        _cycles = WheelLoop.CycleCount(_count, _extent, _looping);
        _totalRows = WheelLoop.TotalRows(_count, _cycles);
        var list = _rows.List;
        list.LeadingPadding = lead;
        list.TrailingPadding = lead;
        list.SetRowExtent(_extent);
        list.SetCount(_totalRows);
        // 円柱の裏へ回る手前まで（と 1 行の余裕）を前もって作る（窓の外でも曲面で内側に映る行がある）
        list.CacheExtent = Math.Max(_extent, WheelLook.MaxVisibleDistance(_viewport, LookParams) - _viewport * Half + _extent);
        scroll.Direction = ScrollDirection.Vertical;
        scroll.Snap = ScrollSnap.Interval;
        scroll.SnapInterval = _extent;
        scroll.HandOffToParent = false;
        scroll.ContentSizeMode = ScrollContentSize.Fixed;
        _expectedContent = lead * 2f + _totalRows * _extent;
        scroll.FixedContentSize = new Vector2(0f, _expectedContent);
        _awaitingContent = true;
        if (_pendingItem < 0) _pendingItem = Math.Max(0, _selectedItem);
        _layoutDirty = false;
        _looksDirty = true;
        _rows.Restyle();
    }

    /// <summary>項目を黙って中央へ置く（最初の配置・並びの作り直し。イベントを出さない）。</summary>
    private void PlaceSilently(CanvasScroll scroll, int item)
    {
        int row = _looping ? WheelLoop.CenterRowOfItem(item, _count, _cycles) : Math.Clamp(item, 0, Math.Max(0, _totalRows - 1));
        scroll.JumpTo(new Vector2(0f, WheelLoop.PositionOfRow(row, _extent)));
        _pendingItem = -1;
        _selectedItem = ClampItem(item);
        _animTargetRow = null;
        _wasMoving = false;
        _requestIssued = false;
        SyncValueField();
    }

    /// <summary>項目へ動かす（置ける前なら置く予定にする）。</summary>
    private void MoveToItem(int item, bool animate, float duration)
    {
        if (_count <= 0) return;
        if (!IsReady || _viewportObject.GetComponent<CanvasScroll>() is not { } scroll)
        {
            _pendingItem = item;
            Redraw.Request();
            return;
        }
        int from = _animTargetRow ?? WheelLoop.RowAtPosition(scroll.Position.y, _extent, _totalRows);
        int row = _looping ? WheelLoop.NearestRowOfItem(item, from, _count, _totalRows) : item;
        StartMotionToRow(scroll, row, animate, duration);
    }

    /// <summary>行へ動かし始める（動きあり: ScrollTo・なし: すぐ移す）。</summary>
    private void StartMotionToRow(CanvasScroll scroll, int row, bool animate, float duration)
    {
        var target = new Vector2(scroll.Position.x, WheelLoop.PositionOfRow(row, _extent));
        if (animate && duration > 0f)
        {
            scroll.ScrollTo(target, duration);
            _animTargetRow = row;
        }
        else
        {
            scroll.JumpTo(target);
            _animTargetRow = null;
        }
        _requestIssued = true;
        Redraw.Request();
    }

    /// <summary>
    /// 1 フレームの動きを見る: 中央の項目が変わったら知らせる（と触感）、止まったら選べる行へ戻すか Settled を出す。
    /// </summary>
    private void TrackMotion(CanvasScroll scroll)
    {
        var phase = scroll.Phase;
        bool touching = phase is ScrollPhase.Dragging or ScrollPhase.Held;
        IsUserInteracting = touching;
        if (touching)
        {
            // 指が動きを引き継いだ: スクリプトの動きの行き先は捨てる。キーボードの相手にする
            _animTargetRow = null;
            WheelFocus.Set(this);
        }
        bool moving = phase != ScrollPhase.Idle || _requestIssued;
        _requestIssued = false;

        int row = WheelLoop.RowAtPosition(scroll.Position.y, _extent, _totalRows);
        int item = ItemOfRow(row);
        if (item != _selectedItem)
        {
            _selectedItem = item;
            SyncValueField();
            // 触感は指とその慣性の間だけ（スクリプト・タップの動きでは鳴らさない＝Flutter の tap-to-scroll と同じ）。1 フレーム 1 回
            if (Haptic && phase is ScrollPhase.Dragging or ScrollPhase.Ballistic) SEED.Platform.Haptics.Tap();
            SelectionChanged?.Invoke(this, item);
        }
        // 止まったことを先に覚えてから知らせる（Settled の受け手が IsMoving を見て「全列が止まった」を判定できるように）
        bool cameToRest = !moving && _wasMoving;
        _wasMoving = moving;
        if (cameToRest) OnCameToRest(scroll, row, item);
        // 止まった所で戻しの動き（選べない行から）を始めたら、次のフレームも動いているとみなす
        _wasMoving = _wasMoving || _requestIssued;
        _restingBefore = !_wasMoving;
    }

    /// <summary>止まった: 選べない行なら最も近い選べる行へ戻す。選べる行なら（遠ければ真ん中の周へ黙って戻して）Settled。</summary>
    private void OnCameToRest(CanvasScroll scroll, int row, int item)
    {
        _animTargetRow = null;
        if (!IsItemEnabled(item))
        {
            int target = WheelLoop.NearestEnabledItem(item, _count, _looping, IsItemEnabled);
            if (target >= 0 && target != item)
            {
                MoveToItem(target, animate: true, Theme.Number(UiTokens.MotionWheelCorrect));
                return;
            }
        }
        if (_looping && WheelLoop.ShouldRecenter(row, _count, _cycles))
        {
            // 同じ項目の真ん中の周の行へ（並びは周ごとに同じなので見た目は変わらない）
            scroll.JumpTo(new Vector2(scroll.Position.x, WheelLoop.PositionOfRow(WheelLoop.RecenteredRow(row, _count, _cycles), _extent)));
        }
        Settled?.Invoke(this, item);
    }

    /// <summary>行の一覧を更新し、位置・中身・見た目が変わったら行の見た目を当て直す。</summary>
    private void UpdateRows(CanvasScroll scroll)
    {
        if (_rows is null) return;
        bool bound = _rows.Update();
        float position = scroll.Position.y;
        bool moved = float.IsNaN(_appliedPosition) || MathF.Abs(position - _appliedPosition) > float.Epsilon;
        if (moved || bound || _looksDirty)
        {
            _rowEnabled ??= row => IsItemEnabled(ItemOfRow(row));
            _rows.Apply(position, _viewport, _extent, _width, LookParams, _style, _rowEnabled);
            _appliedPosition = position;
            _looksDirty = false;
        }
        // 作っている途中の行がある（作った行は次のフレームから使える）間は描き続ける
        if (_rows.AllVisibleRowsBound())
        {
            _waitFrames = 0;
        }
        else if (!RequestWhileWaiting() && !_warnedRows)
        {
            _warnedRows = true;
            SEED.Debug.LogWarning($"[SEED.UI] WheelPicker '{gameObject.Name}': 行を作れません（行のプレハブ: {RowPrefab}）");
        }
    }

    /// <summary>準備を待つ間の描き続けの依頼（上限のフレームまで）。</summary>
    /// <returns>頼んだなら true（上限を超えたら false＝次の自然なフレームを待つ）。</returns>
    private bool RequestWhileWaiting()
    {
        if (_waitFrames >= MaxWaitRedrawFrames) return false;
        _waitFrames++;
        Redraw.Request();
        return true;
    }

    /// <summary>キーボード: 相手のホイールだけが上下の矢印で 1 つずつ動く（動きあり）。</summary>
    private void HandleKeyboard()
    {
        if (!Keyboard || !IsEnabled || !ReferenceEquals(WheelFocus.Current, this)) return;
        int delta = 0;
        if (Input.GetKeyDown(KeyCode.UpArrow)) delta--;
        if (Input.GetKeyDown(KeyCode.DownArrow)) delta++;
        if (delta != 0) StepBy(delta);
    }

    // ── 値と項目 ──────────────────────────────────────────────

    /// <summary>行の文字（行 → 項目 → 文字）。</summary>
    private string LabelOfRow(int row)
    {
        int item = ItemOfRow(row);
        return _labelOf?.Invoke(item) ?? ValueMath.Format(ValueOfItem(item), Format, Suffix);
    }

    /// <summary>行の項目（端をつなげるなら mod）。</summary>
    private int ItemOfRow(int row) => _count <= 0 ? 0 : WheelLoop.ItemOfRow(row, _count);

    /// <summary>項目を範囲へ収める（端をつなげるなら mod）。</summary>
    private int ClampItem(int item)
    {
        if (_count <= 0) return 0;
        return _looping ? WheelLoop.ItemOfRow(item, _count) : Math.Clamp(item, 0, _count - 1);
    }

    /// <summary>項目の値（数の範囲）。</summary>
    private int ValueOfItem(int item) => Math.Min(Min, Max) + item * Math.Max(1, Step);

    /// <summary>値の項目（最も近い項目。範囲の外の値は端の項目＝端をつなげる列でも回さない）。</summary>
    private int ItemOfValue(int value)
    {
        int step = Math.Max(1, Step);
        int lo = Math.Min(Min, Max);
        int item = (int)Math.Round((value - (double)lo) / step, MidpointRounding.AwayFromZero);
        return Math.Clamp(item, 0, Math.Max(0, _count - 1));
    }

    /// <summary>数の範囲のとき、Value フィールドを中央の値に合わせる（インスペクタ・保存で今の値が見える）。</summary>
    private void SyncValueField()
    {
        if (_labelOf is null && _selectedItem >= 0) Value = ValueOfItem(_selectedItem);
    }
}
