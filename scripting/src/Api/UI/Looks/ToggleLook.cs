namespace SEED.UI;

// ============================================================
//  ToggleLook.cs — スイッチ（トグル）とチェックボックスの状態 → 見た目（W2-4。純粋な計算）
//
//  スイッチ: 台（角丸 = 高さの半分のピル形）と丸いつまみ。オンの度合い t（0 = オフ・1 = オン。つまみの動きの途中は
//  その間）で台の色・つまみの位置・大きさ・色を補間する（Material 3 のスイッチ: オフは小さなつまみ＋枠、オンは大きなつまみ）。
//  つまみの中心の x = 高さの半分 〜 幅 − 高さの半分。押している間はつまみをオンの大きさにする。
//  チェックボックス: 箱（角丸・枠）と印（チェック）。オンは主の色で塗り、印を出す。
//  無効はどちらも全体の不透明度を opacity.disabled 倍にする。
// ============================================================

/// <summary>スイッチの見た目。</summary>
/// <param name="Track">台の色。</param>
/// <param name="TrackBorder">台の枠の色。</param>
/// <param name="TrackBorderWidth">台の枠の太さ。</param>
/// <param name="TrackRadius">台の角丸（高さの半分）。</param>
/// <param name="Knob">つまみの色。</param>
/// <param name="KnobSize">つまみの直径。</param>
/// <param name="KnobCenterX">つまみの中心の x（台の左端が 0）。</param>
/// <param name="KnobCenterY">つまみの中心の y（台の上端が 0）。</param>
public readonly record struct ToggleLook(
    Color Track, Color TrackBorder, float TrackBorderWidth, float TrackRadius,
    Color Knob, float KnobSize, float KnobCenterX, float KnobCenterY);

/// <summary>チェックボックスの見た目。</summary>
/// <param name="Box">箱の塗り。</param>
/// <param name="Border">箱の枠の色。</param>
/// <param name="BorderWidth">箱の枠の太さ。</param>
/// <param name="CornerRadius">箱の角丸。</param>
/// <param name="Mark">印の色。</param>
/// <param name="MarkVisible">印を出すか。</param>
public readonly record struct CheckboxLook(Color Box, Color Border, float BorderWidth, float CornerRadius, Color Mark, bool MarkVisible);

/// <summary>スイッチ・チェックボックスの状態 → 見た目。</summary>
public static class ToggleLooks
{
    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>
    /// スイッチの見た目を決める。
    /// </summary>
    /// <param name="onAmount">オンの度合い（0..1。つまみの動きの途中は間の値）。</param>
    /// <param name="pressed">押している。</param>
    /// <param name="disabled">無効。</param>
    /// <param name="trackWidth">台の幅。</param>
    /// <param name="trackHeight">台の高さ。</param>
    /// <param name="theme">テーマ。</param>
    public static ToggleLook ResolveSwitch(float onAmount, bool pressed, bool disabled, float trackWidth, float trackHeight, UiThemeData theme)
    {
        float t = System.Math.Clamp(onAmount, 0f, 1f);
        var trackOff = theme.Color(UiTokens.ColorSurfaceVariant);
        var trackOn = theme.Color(UiTokens.ColorPrimary);
        var track = UiColorMath.Lerp(trackOff, trackOn, t);
        // 枠はオフのときだけ（オンへ向かうほど消える）
        var border = UiColorMath.FadeAlpha(theme.Color(UiTokens.ColorOutline), 1f - t);
        var knob = UiColorMath.Lerp(theme.Color(UiTokens.ColorKnobOff), theme.Color(UiTokens.ColorKnob), t);
        float offSize = theme.Number(UiTokens.SizeToggleKnobOff);
        float onSize = theme.Number(UiTokens.SizeToggleKnob);
        float size = pressed ? onSize : offSize + (onSize - offSize) * t;
        float half = trackHeight * Half;
        float cx = half + (trackWidth - trackHeight) * t;
        if (pressed)
            track = UiColorMath.Over(track, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed));
        var look = new ToggleLook(track, border, theme.Number(UiTokens.SizeCheckBorder), half, knob, size, cx, half);
        return disabled ? Fade(look, theme.Number(UiTokens.OpacityDisabled)) : look;
    }

    /// <summary>
    /// チェックボックスの見た目を決める。
    /// </summary>
    /// <param name="isChecked">オン。</param>
    /// <param name="pressed">押している。</param>
    /// <param name="disabled">無効。</param>
    /// <param name="theme">テーマ。</param>
    public static CheckboxLook ResolveCheckbox(bool isChecked, bool pressed, bool disabled, UiThemeData theme)
    {
        var primary = theme.Color(UiTokens.ColorPrimary);
        var box = isChecked ? primary : UiColorMath.Transparent;
        var border = isChecked ? primary : theme.Color(UiTokens.ColorOnSurfaceMuted);
        if (pressed)
            box = UiColorMath.Over(box, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed));
        var look = new CheckboxLook(box, border, theme.Number(UiTokens.SizeCheckBorder), theme.Number(UiTokens.RadiusCheckbox),
            theme.Color(UiTokens.ColorOnPrimary), isChecked);
        if (!disabled) return look;
        float k = theme.Number(UiTokens.OpacityDisabled);
        return look with
        {
            Box = UiColorMath.FadeAlpha(look.Box, k),
            Border = UiColorMath.FadeAlpha(look.Border, k),
            Mark = UiColorMath.FadeAlpha(look.Mark, k),
        };
    }

    /// <summary>スイッチ全体を薄くする（無効）。</summary>
    private static ToggleLook Fade(ToggleLook look, float k) => look with
    {
        Track = UiColorMath.FadeAlpha(look.Track, k),
        TrackBorder = UiColorMath.FadeAlpha(look.TrackBorder, k),
        Knob = UiColorMath.FadeAlpha(look.Knob, k),
    };
}
