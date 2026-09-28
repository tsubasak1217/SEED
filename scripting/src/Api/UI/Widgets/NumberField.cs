using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  NumberField.cs — 数値欄（増減ボタン・範囲・段階・長押しで連続。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/number_field.actor
//      NumberField（Sprite = 欄の背景〈角丸・枠〉・このスクリプト）
//      ├─ Minus（Button のプレハブと同じ作り。SEED.UI.Button〈文字だけ〉）
//      ├─ Value（Text。値の表示）
//      └─ Plus（Button）
//  【操作】−・＋のタップで段階ぶん増減（範囲へ収める）。長押しで連続（motion.repeat_* のトークン。押し続けるほど速く、
//  最短の間隔で止まる）。範囲の端では片側のボタンを無効にする。文字での入力（キーボード）は W2-6 の入力欄で、
//  今は `TrySetText`（数でない入力は捨てる・範囲の外は収める）だけを用意する。
//  子のボタンは別のスクリプトなので、登録簿（UiRegistry）から引いてつなぐ（相手の OnStart の後になるまで毎フレーム引き直す）。
// ============================================================

/// <summary>数値欄。</summary>
public sealed class NumberField : UiWidget
{
    /// <summary>子の減らすボタンの名前。</summary>
    private const string MinusChild = "Minus";
    /// <summary>子の増やすボタンの名前。</summary>
    private const string PlusChild = "Plus";
    /// <summary>子の値の文字の名前。</summary>
    private const string ValueChild = "Value";

    /// <summary>最小値。</summary>
    [SerializeField(Label = "最小")]
    public float Min;
    /// <summary>最大値。</summary>
    [SerializeField(Label = "最大")]
    public float Max = 100f;
    /// <summary>段階（0 以下なら 1 ずつ）。</summary>
    [SerializeField(Label = "段階")]
    public float Step = 1f;
    /// <summary>値。</summary>
    [SerializeField(Label = "値")]
    public float Value;
    /// <summary>表示の書式（.NET の数の書式。例 "0"・"0.0"）。</summary>
    [SerializeField(Label = "書式")]
    public string Format = "0";
    /// <summary>表示の後ろの文字（例 "分"・"円"）。</summary>
    [SerializeField(Label = "単位")]
    public string Suffix = "";

    /// <summary>値が変わった（新しい値）。</summary>
    public event Action<NumberField, float>? ValueChanged;

    /// <summary>つないだ子のボタン。</summary>
    private Button? _minus, _plus;
    /// <summary>引き直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>長押しの連続の向き（0 = 止まっている）。</summary>
    private int _repeatDirection;
    /// <summary>連続が始まってからの時間と、次に増減する時刻。</summary>
    private float _repeatHeld, _repeatNext;

    /// <summary>値を変える（範囲・段階へ寄せる。同じ値なら知らせない）。</summary>
    public void SetValue(float value, bool notify = true)
    {
        float v = ValueMath.Snap(value, Min, Max, Step > 0f ? Step : 0f);
        if (ValueMath.NearlyEqual(v, Value)) return;
        Value = v;
        Refresh();
        if (notify) ValueChanged?.Invoke(this, v);
    }

    /// <summary>文字を数として読んで値にする（数でない入力は捨てて false）。</summary>
    public bool TrySetText(string text)
    {
        if (!ValueMath.TryParse(text, Min, Max, Step > 0f ? Step : 0f, out var v)) return false;
        SetValue(v);
        return true;
    }

    /// <summary>段階ぶん増減する。</summary>
    public void StepBy(int direction) => SetValue(ValueMath.Step(Value, direction, Min, Max, Step));

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        Value = ValueMath.Snap(Value, Min, Max, Step > 0f ? Step : 0f);
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy() => Bind(null, null);

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 子のボタンがまだつながっていなければ、登録簿が変わったときに引き直す
        if ((_minus is null || _plus is null) && _registryVersion != UiRegistry.Version)
        {
            _registryVersion = UiRegistry.Version;
            Bind(Of<Button>(gameObject.FindChild(MinusChild)), Of<Button>(gameObject.FindChild(PlusChild)));
        }
        if (_repeatDirection == 0) return;
        // 長押しの連続: 間隔ごとに増減する（押している間は指がアリーナにいるので描画は止まらない）
        _repeatHeld += dt;
        while (_repeatHeld >= _repeatNext)
        {
            StepBy(_repeatDirection);
            _repeatNext += ValueMath.RepeatInterval(_repeatNext, Theme.Number(UiTokens.MotionRepeatInterval),
                Theme.Number(UiTokens.MotionRepeatMinInterval), Theme.Number(UiTokens.MotionRepeatAccel));
        }
        Redraw.Request();
    }

    /// <summary>子のボタンをつなぎ替える。</summary>
    private void Bind(Button? minus, Button? plus)
    {
        if (_minus is not null) { _minus.Clicked -= OnMinus; _minus.LongPressed -= OnMinusHold; _minus.Released -= OnRelease; }
        if (_plus is not null) { _plus.Clicked -= OnPlus; _plus.LongPressed -= OnPlusHold; _plus.Released -= OnRelease; }
        _minus = minus;
        _plus = plus;
        if (_minus is not null) { _minus.Clicked += OnMinus; _minus.LongPressed += OnMinusHold; _minus.Released += OnRelease; }
        if (_plus is not null) { _plus.Clicked += OnPlus; _plus.LongPressed += OnPlusHold; _plus.Released += OnRelease; }
        if (_minus is not null || _plus is not null) Refresh();
    }

    private void OnMinus(Button _) { if (IsEnabled) StepBy(-1); }
    private void OnPlus(Button _) { if (IsEnabled) StepBy(1); }
    private void OnMinusHold(Button _) => StartRepeat(-1);
    private void OnPlusHold(Button _) => StartRepeat(1);
    private void OnRelease(Button _) => _repeatDirection = 0;

    /// <summary>長押しの連続を始める（長押しの時点で 1 回、以後は間隔ごと）。</summary>
    private void StartRepeat(int direction)
    {
        if (!IsEnabled) return;
        _repeatDirection = direction;
        _repeatHeld = 0f;
        _repeatNext = 0f;
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        bool disabled = !IsEnabled;
        if (SpriteOf() is { } bg)
        {
            bg.Color = disabled ? UiColorMath.Transparent : Theme.Color(UiTokens.ColorSurface);
            bg.CornerRadius = Theme.Number(UiTokens.RadiusField);
            bg.BorderWidth = Theme.Number(UiTokens.SizeBorder);
            bg.BorderColor = disabled ? Theme.Color(UiTokens.ColorDisabled) : Theme.Color(UiTokens.ColorOutline);
        }
        if (TextOf(ValueChild) is { } text)
        {
            text.Content = ValueMath.Format(Value, Format, Suffix);
            text.Color = disabled ? Theme.Color(UiTokens.ColorOnDisabled) : Theme.Color(UiTokens.ColorOnSurface);
            UiTextStyle.Apply(text, Theme, UiTokens.TextBody);
        }
        // 範囲の端では片側のボタンを無効にする（全体が無効なら両方）
        _minus?.SetInteractable(!disabled && Value > Math.Min(Min, Max));
        _plus?.SetInteractable(!disabled && Value < Math.Max(Min, Max));
    }
}
