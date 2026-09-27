using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  TimeWheel.cs — 時刻ホイール（時・分〈と午前/午後〉の列。W2-5。docs/ui_components.md §11）
//
//  【プレハブ】templates/ui/prefabs/time_wheel.actor
//      TimeWheel（Sprite = 全体の大きさ〈透明〉・このスクリプト）
//      ├─ Band（Sprite = 全列にまたがる中央の帯〈角丸〉）
//      ├─ Meridiem（WheelPicker の列。端をつながない 2 行。24 時間表記では隠す）
//      ├─ Hour（WheelPicker の列。24 行・端をつなげる）
//      └─ Minute（WheelPicker の列。60 ÷ 刻み 行・端をつなげる）
//  列は WheelPicker（別のスクリプト）なので、登録簿（UiRegistry）から引いてつなぐ（相手の OnStart の後になるまで引き直す）。
//  列の横の位置と幅はこの部品が決める（全体の幅を列の数で等分。午前/午後の列は MeridiemOnLeft で左か右）。
//  【値】TimeOnly（秒は 0）。時の列の項目と「午前/午後の連動の状態」（TimeWheelMath.MeridiemState）から時を、分の列の項目 × 刻みから分を作る。
//  【連動】12 時間表記で時の列が 11 ↔ 12・23 ↔ 0 を越えたら午前/午後の列を動かす（Flutter の CupertinoDatePicker と同じ。
//  TimeWheelMath の説明）。午前/午後の列を指で変えたら時が 12 ずれる（時の列は動かない＝表示は 12 時間で同じ）。
//  【イベント】ValueChanged（値が変わるたび。指で回している途中も）・ValueSettled（全列が止まったとき）。
//  スクリプトからの SetValue は ValueChanged を 1 回だけ出し、列が動いている途中の値は知らせない（止まったら列から値を確かめ直す）。
// ============================================================

/// <summary>時刻ホイール。</summary>
public sealed class TimeWheel : UiWidget
{
    /// <summary>子の中央の帯の名前。</summary>
    private const string BandChild = "Band";
    /// <summary>子の時の列の名前。</summary>
    private const string HourChild = "Hour";
    /// <summary>子の分の列の名前。</summary>
    private const string MinuteChild = "Minute";
    /// <summary>子の午前/午後の列の名前。</summary>
    private const string MeridiemChild = "Meridiem";
    /// <summary>12 時間表記の列の数。</summary>
    private const int ColumnsWithMeridiem = 3;
    /// <summary>24 時間表記の列の数。</summary>
    private const int ColumnsWithoutMeridiem = 2;

    /// <summary>24 時間表記（false なら 12 時間表記と午前/午後の列）。</summary>
    [SerializeField(Label = "24 時間表記")]
    public bool Use24Hour = true;
    /// <summary>分の刻み（1 時間を割り切る数。1・5 など）。</summary>
    [SerializeField(Label = "分の刻み")]
    public int MinuteStep = 1;
    /// <summary>時（0〜23。最初の値。動かすと今の値に書き換わる）。</summary>
    [SerializeField(Label = "時")]
    public int Hour = 7;
    /// <summary>分（0〜59。最初の値）。</summary>
    [SerializeField(Label = "分")]
    public int Minute;
    /// <summary>時の書式（.NET の数の書式。"0" = 0〜23、"00" = 00〜23）。</summary>
    [SerializeField(Label = "時の書式")]
    public string HourFormat = "0";
    /// <summary>分の書式。</summary>
    [SerializeField(Label = "分の書式")]
    public string MinuteFormat = "00";
    /// <summary>午前の文字。</summary>
    [SerializeField(Label = "午前の文字")]
    public string AmLabel = "午前";
    /// <summary>午後の文字。</summary>
    [SerializeField(Label = "午後の文字")]
    public string PmLabel = "午後";
    /// <summary>午前/午後の列を左に置く（日本語の並び「午前 7:30」。false なら右）。</summary>
    [SerializeField(Label = "午前/午後を左に")]
    public bool MeridiemOnLeft = true;

    /// <summary>値が変わった（指で回している途中も。スクリプトの SetValue では 1 回）。</summary>
    public event Action<TimeWheel, TimeOnly>? ValueChanged;
    /// <summary>全列が止まった（止まったときの値）。</summary>
    public event Action<TimeWheel, TimeOnly>? ValueSettled;

    /// <summary>今の値。</summary>
    public TimeOnly Value => _value;
    /// <summary>時の列（つながる前は null）。</summary>
    public WheelPicker? HourColumn => _hour;
    /// <summary>分の列。</summary>
    public WheelPicker? MinuteColumn => _minute;
    /// <summary>午前/午後の列。</summary>
    public WheelPicker? MeridiemColumn => _meridiem;
    /// <summary>どれかの列が動いている。</summary>
    public bool IsMoving => (_hour?.IsMoving ?? false) || (_minute?.IsMoving ?? false) || (!Use24Hour && (_meridiem?.IsMoving ?? false));

    /// <summary>列。</summary>
    private WheelPicker? _hour, _minute, _meridiem;
    /// <summary>引き直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>午前/午後の連動の状態。</summary>
    private MeridiemState _state;
    /// <summary>今の値。</summary>
    private TimeOnly _value;
    /// <summary>スクリプトの動きの途中（列の途中の値は知らせず、連動もしない。止まるか指で触れたら列から確かめ直す）。</summary>
    private bool _programmatic;
    /// <summary>全体の大きさ（プレハブの Sprite）。</summary>
    private float _width, _height;

    // ── スクリプトからの操作 ───────────────────────────────────

    /// <summary>値を変える（分の刻みへ丸める。animate = false ならすぐ移す）。</summary>
    /// <param name="time">時刻（秒は捨てる）。</param>
    /// <param name="animate">true なら列を motion.wheel 秒で動かす。</param>
    public void SetValue(TimeOnly time, bool animate = true)
    {
        int step = TimeWheelMath.NormalizeMinuteStep(MinuteStep);
        var t = TimeWheelMath.RoundToStep(time, step);
        _state = TimeWheelMath.StateFor(t.Hour);
        bool changed = t != _value;
        SetValueCore(t);
        if (ColumnsBound && _hour is { } hour && _minute is { } minute)
        {
            // 列の準備ができていれば動かす（できる前なら列が置けるようになったとき黙って置く）
            _programmatic = hour.IsReady && minute.IsReady;
            hour.SelectIndex(t.Hour, animate);
            minute.SelectIndex(TimeWheelMath.ItemOfMinute(t.Minute, step), animate);
            _meridiem?.SelectIndex(_state.AmPm, animate && !Use24Hour);
        }
        if (changed) ValueChanged?.Invoke(this, t);
    }

    /// <summary>24 時間表記・12 時間表記を切り替える（値は変えない）。</summary>
    public void SetUse24Hour(bool use24Hour)
    {
        if (Use24Hour == use24Hour) return;
        Use24Hour = use24Hour;
        _state = TimeWheelMath.StateFor(_value.Hour);
        if (ColumnsBound)
        {
            // 時の列は表記の文字だけ変え、入れ替わりを解いた項目（= 時）へ移す。午前/午後の列は今の値へ
            _programmatic = _hour!.IsReady;
            _hour.SetLabels(HourLabel);
            _hour.SelectIndex(_value.Hour, animate: false);
            _meridiem?.SelectIndex(_state.AmPm, animate: false);
            LayoutColumns();
        }
        Refresh();
    }

    /// <summary>分の刻みを変える（1 時間を割り切る数。今の値は新しい刻みへ丸める）。</summary>
    public void SetMinuteStep(int step)
    {
        int s = TimeWheelMath.NormalizeMinuteStep(step);
        if (s == TimeWheelMath.NormalizeMinuteStep(MinuteStep) && s == MinuteStep) return;
        MinuteStep = s;
        var t = TimeWheelMath.RoundToStep(_value, s);
        _state = TimeWheelMath.StateFor(t.Hour);
        bool changed = t != _value;
        SetValueCore(t);
        if (ColumnsBound)
        {
            _minute!.Configure(TimeWheelMath.MinuteRows(s), MinuteLabel, looping: true, TimeWheelMath.ItemOfMinute(t.Minute, s));
            _programmatic = _hour!.IsReady;
            _hour.SelectIndex(t.Hour, animate: false);
            _meridiem?.SelectIndex(_state.AmPm, animate: false);
        }
        if (changed) ValueChanged?.Invoke(this, t);
    }

    // ── 部品の土台（UiWidget）───────────────────────────────────

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        if (SpriteOf() is { } root)
        {
            _width = root.Width;
            _height = root.Height;
        }
        MinuteStep = TimeWheelMath.NormalizeMinuteStep(MinuteStep);
        _value = TimeWheelMath.RoundToStep(TimeWheelMath.Compose(Hour, Minute), MinuteStep);
        _state = TimeWheelMath.StateFor(_value.Hour);
        Hour = _value.Hour;
        Minute = _value.Minute;
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy() => Bind(null, null, null);

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 列がまだつながっていなければ、登録簿が変わったときに引き直す
        if (!ColumnsBound && _registryVersion != UiRegistry.Version)
        {
            _registryVersion = UiRegistry.Version;
            Bind(Of<WheelPicker>(gameObject.FindChild(HourChild)), Of<WheelPicker>(gameObject.FindChild(MinuteChild)),
                Of<WheelPicker>(gameObject.FindChild(MeridiemChild)));
        }
        if (!ColumnsBound) return;
        // スクリプトの動きの途中に指で触れた: 列の見た目から値を確かめ直し、以後は指の動きとして扱う
        if (_programmatic && AnyColumnTouched())
        {
            _programmatic = false;
            Reconcile();
        }
        HandleFocusKeys();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        bool disabled = !IsEnabled;
        float fade = disabled ? Theme.Number(UiTokens.OpacityDisabled) : 1f;
        var hour = _hour;
        float extent = hour?.ResolvedItemExtent ?? WheelLoop.SanitizeExtent(Theme.Number(UiTokens.SizeWheelItem, WheelLoop.MinItemExtent));
        var look = hour?.LookParams ?? WheelLookParams.DatePicker;
        WheelPicker.ApplyBandLook(SpriteOf(BandChild), TransformOf(BandChild), show: true, _width, _height, extent, look, Theme, fade);
        // 列の押せる・押せないを合わせる（列が遮る板で指を取らなくなる）
        _hour?.SetInteractable(Interactable);
        _minute?.SetInteractable(Interactable);
        _meridiem?.SetInteractable(Interactable);
    }

    // ── 中身 ──────────────────────────────────────────────────

    /// <summary>時と分の列がつながっているか（午前/午後の列は 24 時間表記なら無くてもよい）。</summary>
    private bool ColumnsBound => _hour is not null && _minute is not null && (Use24Hour || _meridiem is not null);

    /// <summary>列をつなぎ替える（イベントの購読と、項目・文字・最初の値の設定）。</summary>
    private void Bind(WheelPicker? hour, WheelPicker? minute, WheelPicker? meridiem)
    {
        if (_hour is not null) { _hour.SelectionChanged -= OnHourChanged; _hour.Settled -= OnColumnSettled; }
        if (_minute is not null) { _minute.SelectionChanged -= OnMinuteChanged; _minute.Settled -= OnColumnSettled; }
        if (_meridiem is not null) { _meridiem.SelectionChanged -= OnMeridiemChanged; _meridiem.Settled -= OnColumnSettled; }
        _hour = hour;
        _minute = minute;
        _meridiem = meridiem;
        if (_hour is not null) { _hour.SelectionChanged += OnHourChanged; _hour.Settled += OnColumnSettled; }
        if (_minute is not null) { _minute.SelectionChanged += OnMinuteChanged; _minute.Settled += OnColumnSettled; }
        if (_meridiem is not null) { _meridiem.SelectionChanged += OnMeridiemChanged; _meridiem.Settled += OnColumnSettled; }
        if (!ColumnsBound) return;
        int step = TimeWheelMath.NormalizeMinuteStep(MinuteStep);
        _hour!.Configure(TimeWheelMath.HourRows, HourLabel, looping: true, _value.Hour);
        _minute!.Configure(TimeWheelMath.MinuteRows(step), MinuteLabel, looping: true, TimeWheelMath.ItemOfMinute(_value.Minute, step));
        _meridiem?.Configure(TimeWheelMath.MeridiemCount, MeridiemLabel, looping: false, _state.AmPm);
        LayoutColumns();
        Refresh();
    }

    /// <summary>列を横に並べる（全体の幅を列の数で等分。24 時間表記では午前/午後の列を隠す）。</summary>
    private void LayoutColumns()
    {
        if (_hour is null || _minute is null) return;
        bool withMeridiem = !Use24Hour && _meridiem is not null;
        var order = withMeridiem
            ? (MeridiemOnLeft ? new[] { _meridiem!, _hour, _minute } : new[] { _hour, _minute, _meridiem! })
            : new[] { _hour, _minute };
        float columnWidth = _width / (withMeridiem ? ColumnsWithMeridiem : ColumnsWithoutMeridiem);
        for (int i = 0; i < order.Length; i++)
        {
            var column = order[i];
            if (column.Owner.GetComponent<CanvasTransform>() is { } ct)
                ct.Position = new Vector2(columnWidth * i, ct.Position.y);
            column.SetWidth(columnWidth);
        }
        if (_meridiem is not null)
        {
            var owner = _meridiem.Owner;
            if (owner.Visible != withMeridiem) owner.Visible = withMeridiem;
        }
    }

    /// <summary>時の列の中央の項目が変わった: 半日を越えたら午前/午後を入れ替え（12 時間表記なら午前/午後の列を動かす）、値を作り直す。</summary>
    private void OnHourChanged(WheelPicker column, int item)
    {
        if (_programmatic) return;
        bool flipped = TimeWheelMath.OnHourItemChanged(ref _state, item);
        // 指が午前/午後の列にあるときは動かさない（指を離した所の値が優先＝その列の SelectionChanged で決まる）
        if (flipped && !Use24Hour && _meridiem is { IsUserInteracting: false } meridiem)
            meridiem.SelectIndex(_state.AmPm, animate: true);
        UpdateValue();
    }

    /// <summary>分の列の中央の項目が変わった。</summary>
    private void OnMinuteChanged(WheelPicker column, int item)
    {
        if (_programmatic) return;
        UpdateValue();
    }

    /// <summary>午前/午後の列の中央の項目が変わった（指で変えたときは時が 12 ずれる。連動で動かしたときは同じ値なので変わらない）。</summary>
    private void OnMeridiemChanged(WheelPicker column, int item)
    {
        if (_programmatic || Use24Hour) return;
        TimeWheelMath.OnMeridiemSelected(ref _state, item);
        UpdateValue();
    }

    /// <summary>列が止まった: 全列が止まっていれば（スクリプトの動きなら列から値を確かめ直して）ValueSettled。</summary>
    private void OnColumnSettled(WheelPicker column, int item)
    {
        if (IsMoving) return;
        if (_programmatic)
        {
            _programmatic = false;
            Reconcile();
        }
        ValueSettled?.Invoke(this, _value);
    }

    /// <summary>列の見た目（中央の項目）から連動の状態と値を作り直す。</summary>
    private void Reconcile()
    {
        if (_hour is null) return;
        _state = TimeWheelMath.Reconcile(_hour.SelectedIndex, _meridiem?.SelectedIndex ?? TimeWheelMath.Am, Use24Hour);
        UpdateValue();
    }

    /// <summary>列の中央の項目から値を作り、変わったら知らせる。</summary>
    private void UpdateValue()
    {
        if (_hour is null || _minute is null) return;
        int hour = TimeWheelMath.HourOfItem(_hour.SelectedIndex, _state);
        int minute = TimeWheelMath.MinuteOfItem(_minute.SelectedIndex, MinuteStep);
        var t = TimeWheelMath.Compose(hour, minute);
        if (t == _value) return;
        SetValueCore(t);
        ValueChanged?.Invoke(this, t);
    }

    /// <summary>値を覚える（フィールドも合わせる＝インスペクタ・保存で今の値が見える）。</summary>
    private void SetValueCore(TimeOnly t)
    {
        _value = t;
        Hour = t.Hour;
        Minute = t.Minute;
    }

    /// <summary>どれかの列に指が触れているか。</summary>
    private bool AnyColumnTouched()
        => (_hour?.IsUserInteracting ?? false) || (_minute?.IsUserInteracting ?? false) || (_meridiem?.IsUserInteracting ?? false);

    /// <summary>キーボードの左右の矢印: 相手のホイールがこの部品の列なら、隣の列へ移す。</summary>
    private void HandleFocusKeys()
    {
        var focused = WheelFocus.Current;
        if (focused is null || !IsEnabled) return;
        int delta = 0;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) delta--;
        if (Input.GetKeyDown(KeyCode.RightArrow)) delta++;
        if (delta == 0) return;
        bool withMeridiem = !Use24Hour && _meridiem is not null;
        var order = withMeridiem
            ? (MeridiemOnLeft ? new[] { _meridiem!, _hour!, _minute! } : new[] { _hour!, _minute!, _meridiem! })
            : new[] { _hour!, _minute! };
        int index = Array.FindIndex(order, c => ReferenceEquals(c, focused));
        if (index < 0) return;
        order[Math.Clamp(index + delta, 0, order.Length - 1)].Focus();
    }

    /// <summary>時の列の文字（表記に合わせる）。</summary>
    private string HourLabel(int item) => ValueMath.Format(TimeWheelMath.DisplayHour(item, Use24Hour), HourFormat, "");

    /// <summary>分の列の文字。</summary>
    private string MinuteLabel(int item) => ValueMath.Format(TimeWheelMath.MinuteOfItem(item, MinuteStep), MinuteFormat, "");

    /// <summary>午前/午後の列の文字。</summary>
    private string MeridiemLabel(int item) => item == TimeWheelMath.Am ? AmLabel : PmLabel;
}
