using System;
using System.IO;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// SEED.UI の基本の部品（W2-4）の「状態 → 見た目」と値の計算のテスト（docs/ui_components.md §3・§5）。
/// </summary>
public static class Program
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;

    /// <summary>既定のテーマ（出力へ写した default_theme.json）。</summary>
    private static UiThemeData LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "default_theme.json");
        var theme = UiThemeData.Parse(File.ReadAllText(path), null, out var error);
        Check.Equal("", error, "既定のテーマが読める");
        return theme;
    }

    /// <summary>2 色がほぼ等しいか。</summary>
    private static void SameColor(Color expected, Color actual, string what)
    {
        Check.Close(expected.r, actual.r, Eps, what + " r");
        Check.Close(expected.g, actual.g, Eps, what + " g");
        Check.Close(expected.b, actual.b, Eps, what + " b");
        Check.Close(expected.a, actual.a, Eps, what + " a");
    }

    public static int Main()
    {
        var h = new TestHarness();
        var theme = LoadDefault();

        // ── テーマ ─────────────────────────────────────────────
        h.Add("テーマ: 既定のテーマは部品の読むトークンをすべて持つ", () =>
        {
            foreach (var token in UiTokens.All)
                Check.True(theme.Has(token), $"既定のテーマに {token} がある");
            Check.Equal("default", theme.Name, "名前");
        });

        h.Add("テーマ: グループの平坦化・sRGB → 線形・説明の鍵を読まない・最上位の直書き", () =>
        {
            var t = UiThemeData.Parse(
                "{\"_about\":\"x\",\"color\":{\"a\":\"#FFFFFF\",\"b\":\"#80808080\",\"_note\":\"#000000\"},"
                + "\"radius\":{\"button\":12},\"kakugo.danger\":\"#FF0000\",\"deep\":{\"x\":{\"y\":3}}}", null, out var err);
            Check.Equal("", err, "読める");
            SameColor(new Color(1f, 1f, 1f, 1f), t.Color("color.a"), "白");
            // #80 = 128/255 = 0.50196（sRGB）→ 線形 0.21586
            Check.Close(0.21586, t.Color("color.b").r, 1e-4, "sRGB の中間の灰色は線形で約 0.216");
            Check.Close(128.0 / 255.0, t.Color("color.b").a, 1e-4, "アルファは線形へ直さない");
            Check.True(!t.Has("color._note"), "_ で始まる鍵は読まない");
            Check.Close(12, t.Number("radius.button"), Eps, "数");
            SameColor(new Color(1f, 0f, 0f, 1f), t.Color("kakugo.danger"), "最上位の直書き");
            Check.Close(3, t.Number("deep.x.y"), Eps, "入れ子");
        });

        h.Add("テーマ: 壊れた JSON は空・書いていないトークンは代わりのテーマ・最後は既定値", () =>
        {
            var broken = UiThemeData.Parse("{ not json", theme, out var err);
            Check.True(err.Length > 0, "理由が返る");
            SameColor(theme.Color(UiTokens.ColorPrimary), broken.Color(UiTokens.ColorPrimary), "壊れても代わりのテーマの色");
            var custom = UiThemeData.Parse("{\"color\":{\"primary\":\"#000000\"}}", theme, out _);
            SameColor(new Color(0f, 0f, 0f, 1f), custom.Color(UiTokens.ColorPrimary), "書いたトークンは差し替わる");
            Check.Close(theme.Number(UiTokens.RadiusButton), custom.Number(UiTokens.RadiusButton), Eps, "書いていないのは既定");
            Check.Close(7, UiThemeData.Empty().Number("nope", 7f), Eps, "どこにも無ければ呼び出し側の既定値");
            Check.True(!UiColorMath.TryParseHex("#12345", out _), "桁の足りない色は読まない");
            Check.True(!UiColorMath.TryParseHex("red", out _), "名前の色は読まない");
        });

        h.Add("色: 重ね合わせ（透明の上にも重ね色が出る・不透明の上は混ざる）", () =>
        {
            var over = UiColorMath.Over(UiColorMath.Transparent, new Color(1f, 1f, 1f, 1f), 0.2f);
            SameColor(new Color(1f, 1f, 1f, 0.2f), over, "透明の上");
            var mixed = UiColorMath.Over(new Color(0f, 0f, 0f, 1f), new Color(1f, 1f, 1f, 1f), 0.25f);
            SameColor(new Color(0.25f, 0.25f, 0.25f, 1f), mixed, "黒の上に 25% の白");
        });

        // ── ボタン ─────────────────────────────────────────────
        h.Add("ボタン: 塗り・押下・無効（状態 → 見た目は 1 か所）", () =>
        {
            var normal = ButtonLooks.Resolve(ButtonVariant.Filled, false, false, theme);
            SameColor(theme.Color(UiTokens.ColorPrimary), normal.Background, "通常の背景は主の色");
            SameColor(theme.Color(UiTokens.ColorOnPrimary), normal.Content, "文字は主の色の上の色");
            Check.Close(theme.Number(UiTokens.RadiusButton), normal.CornerRadius, Eps, "角丸はトークン");
            Check.Close(0, normal.BorderWidth, Eps, "塗りは枠なし");
            var pressed = ButtonLooks.Resolve(ButtonVariant.Filled, true, false, theme);
            Check.True(pressed.Background != normal.Background, "押下は背景が変わる");
            var disabled = ButtonLooks.Resolve(ButtonVariant.Filled, true, true, theme);
            SameColor(theme.Color(UiTokens.ColorDisabled), disabled.Background, "無効は押していても無効の色");
            SameColor(theme.Color(UiTokens.ColorOnDisabled), disabled.Content, "無効の文字");
        });

        h.Add("ボタン: 枠・文字だけ（背景は透明・押下で重ね色が出る）", () =>
        {
            var outlined = ButtonLooks.Resolve(ButtonVariant.Outlined, false, false, theme);
            Check.Close(0, outlined.Background.a, Eps, "枠のボタンの背景は透明");
            Check.Close(theme.Number(UiTokens.SizeBorder), outlined.BorderWidth, Eps, "枠の太さ");
            SameColor(theme.Color(UiTokens.ColorPrimary), outlined.Content, "文字は主の色");
            var textPressed = ButtonLooks.Resolve(ButtonVariant.Text, true, false, theme);
            Check.Close(theme.Number(UiTokens.OpacityPressed), textPressed.Background.a, Eps, "文字だけのボタンの押下は薄い重ね色");
        });

        // ── スイッチ・チェックボックス ─────────────────────────
        h.Add("スイッチ: つまみの位置・大きさ・色はオンの度合いで補間（途中は間）", () =>
        {
            const float w = 52f, hgt = 32f;
            var off = ToggleLooks.ResolveSwitch(0f, false, false, w, hgt, theme);
            var on = ToggleLooks.ResolveSwitch(1f, false, false, w, hgt, theme);
            var mid = ToggleLooks.ResolveSwitch(0.5f, false, false, w, hgt, theme);
            Check.Close(16, off.KnobCenterX, Eps, "オフのつまみの中心は高さの半分");
            Check.Close(36, on.KnobCenterX, Eps, "オンのつまみの中心は幅 − 高さの半分");
            Check.Close(26, mid.KnobCenterX, Eps, "途中は間");
            Check.Close(theme.Number(UiTokens.SizeToggleKnobOff), off.KnobSize, Eps, "オフは小さなつまみ");
            Check.Close(theme.Number(UiTokens.SizeToggleKnob), on.KnobSize, Eps, "オンは大きなつまみ");
            SameColor(theme.Color(UiTokens.ColorPrimary), on.Track, "オンの台は主の色");
            Check.Close(0, on.TrackBorder.a, Eps, "オンは枠なし");
            Check.Close(hgt / 2, on.TrackRadius, Eps, "台はピル形");
            var pressedOff = ToggleLooks.ResolveSwitch(0f, true, false, w, hgt, theme);
            Check.Close(theme.Number(UiTokens.SizeToggleKnob), pressedOff.KnobSize, Eps, "押している間はつまみが大きい");
            var disabled = ToggleLooks.ResolveSwitch(1f, false, true, w, hgt, theme);
            Check.Close(on.Knob.a * theme.Number(UiTokens.OpacityDisabled), disabled.Knob.a, Eps, "無効は薄い");
        });

        h.Add("チェックボックス: オンは塗りと印・オフは枠だけ・無効は薄い", () =>
        {
            var on = ToggleLooks.ResolveCheckbox(true, false, false, theme);
            var off = ToggleLooks.ResolveCheckbox(false, false, false, theme);
            Check.True(on.MarkVisible && !off.MarkVisible, "印はオンだけ");
            SameColor(theme.Color(UiTokens.ColorPrimary), on.Box, "オンの塗り");
            Check.Close(0, off.Box.a, Eps, "オフの塗りは透明");
            Check.Close(theme.Number(UiTokens.SizeCheckBorder), off.BorderWidth, Eps, "枠の太さ");
            var disabled = ToggleLooks.ResolveCheckbox(true, false, true, theme);
            Check.True(disabled.Box.a < on.Box.a, "無効は薄い");
        });

        // ── 選択の項目 ─────────────────────────────────────────
        h.Add("選択: セグメント・チップ・ラジオの選ぶ／選ばない・無効", () =>
        {
            var seg = SelectionLooks.Resolve(SelectionLookKind.Segment, true, false, false, theme);
            SameColor(theme.Color(UiTokens.ColorSelected), seg.Background, "選んだセグメントは選択の色");
            var segOff = SelectionLooks.Resolve(SelectionLookKind.Segment, false, false, false, theme);
            Check.Close(0, segOff.Background.a, Eps, "選んでいないセグメントは透明");
            var chipOff = SelectionLooks.Resolve(SelectionLookKind.Chip, false, false, false, theme);
            Check.Close(theme.Number(UiTokens.SizeBorder), chipOff.BorderWidth, Eps, "選んでいないチップは枠");
            var chipOn = SelectionLooks.Resolve(SelectionLookKind.Chip, true, false, false, theme);
            Check.Close(0, chipOn.BorderWidth, Eps, "選んだチップは枠なし");
            var radio = SelectionLooks.Resolve(SelectionLookKind.Radio, true, false, false, theme);
            Check.True(radio.IndicatorVisible, "選んだラジオは点");
            SameColor(theme.Color(UiTokens.ColorPrimary), radio.Border, "選んだラジオの輪は主の色");
            Check.True(!SelectionLooks.Resolve(SelectionLookKind.Radio, false, false, false, theme).IndicatorVisible, "選んでいないラジオは点なし");
            var disabled = SelectionLooks.Resolve(SelectionLookKind.Chip, false, true, true, theme);
            SameColor(theme.Color(UiTokens.ColorOnDisabled), disabled.Label, "選べない項目は灰色の文字（押していても）");
            Check.Close(0, disabled.Background.a, Eps, "選べない項目は押下の重ね色も出ない");
        });

        // ── 値（スライダ・数値欄・進捗）───────────────────────
        h.Add("スライダ: 段階・範囲・指の位置 → 値", () =>
        {
            Check.Close(0.3, ValueMath.Snap(0.27f, 0f, 1f, 0.1f), Eps, "0.1 刻みへ寄せる");
            Check.Close(1, ValueMath.Snap(3f, 0f, 1f, 0f), Eps, "範囲へ収める");
            Check.Close(15, ValueMath.Snap(13f, 5f, 30f, 5f), Eps, "段階は最小から数える");
            Check.Close(30, ValueMath.Snap(29f, 5f, 30f, 10f), Eps, "端を越える段階は端へ収める");
            // 溝 x = 10〜210（長さ 200）・0..100・段階 5
            Check.Close(50, ValueMath.ValueAt(110f, 10f, 200f, 0f, 100f, 5f), Eps, "真ん中");
            Check.Close(0, ValueMath.ValueAt(-40f, 10f, 200f, 0f, 100f, 5f), Eps, "溝の左の外は最小");
            Check.Close(100, ValueMath.ValueAt(999f, 10f, 200f, 0f, 100f, 5f), Eps, "溝の右の外は最大");
            Check.Close(25, ValueMath.ValueAt(62f, 10f, 200f, 0f, 100f, 5f), Eps, "26 → 25（段階）");
            Check.Close(0.25, ValueMath.Fraction(25f, 0f, 100f), Eps, "割合");
            Check.Close(0, ValueMath.Fraction(3f, 5f, 5f), Eps, "幅 0 の範囲は 0");
        });

        h.Add("数値欄: 増減（範囲へ収める）・文字の解釈（数でない入力は捨てる）・表示", () =>
        {
            Check.Close(6, ValueMath.Step(5f, 1, 0f, 10f, 1f), Eps, "+1");
            Check.Close(10, ValueMath.Step(10f, 1, 0f, 10f, 1f), Eps, "上限で止まる");
            Check.Close(0, ValueMath.Step(0.5f, -1, 0f, 10f, 1f), Eps, "下限で止まる");
            Check.True(ValueMath.TryParse("42", 0f, 100f, 1f, out var v) && v == 42f, "数");
            Check.True(ValueMath.TryParse(" 250 ", 0f, 100f, 1f, out v) && v == 100f, "範囲の外は収める");
            Check.True(ValueMath.TryParse("7.6", 0f, 100f, 1f, out v) && v == 8f, "段階へ寄せる");
            Check.True(!ValueMath.TryParse("abc", 0f, 100f, 1f, out _), "数でない入力は捨てる");
            Check.True(!ValueMath.TryParse("", 0f, 100f, 1f, out _), "空は捨てる");
            Check.True(!ValueMath.TryParse("NaN", 0f, 100f, 1f, out _), "NaN は捨てる");
            Check.Equal("15分", ValueMath.Format(15f, "0", "分"), "書式と単位");
            Check.Equal("1.5", ValueMath.Format(1.5f, "0.0", ""), "小数");
        });

        h.Add("数値欄: 長押しの連続は押し続けるほど速くなり、最短の間隔で止まる", () =>
        {
            float first = theme.Number(UiTokens.MotionRepeatInterval);
            float shortest = theme.Number(UiTokens.MotionRepeatMinInterval);
            float accel = theme.Number(UiTokens.MotionRepeatAccel);
            Check.Close(first, ValueMath.RepeatInterval(0f, first, shortest, accel), Eps, "始めは最初の間隔");
            Check.True(ValueMath.RepeatInterval(1f, first, shortest, accel) < first, "1 秒後は短い");
            Check.Close(shortest, ValueMath.RepeatInterval(60f, first, shortest, accel), Eps, "長く押すと最短");
            int oneSecond = ValueMath.RepeatCount(1f, 0f, first, shortest, accel);
            int threeSeconds = ValueMath.RepeatCount(3f, 0f, first, shortest, accel);
            Check.True(threeSeconds > oneSecond * 3, $"後ほど速い（1 秒 {oneSecond} 回・3 秒 {threeSeconds} 回）");
        });

        h.Add("進捗: 0..1 へ収める・棒の幅・輪の角度", () =>
        {
            Check.Close(1, ValueMath.Progress(1.7f), Eps, "上限");
            Check.Close(0, ValueMath.Progress(float.NaN), Eps, "NaN は 0");
            Check.Close(60, ValueMath.BarFillWidth(0.25f, 240f), Eps, "棒の幅");
            Check.Close(270, ValueMath.RingSweepDegrees(0.75f), Eps, "輪の角度");
        });

        // ── 選択の状態 ─────────────────────────────────────────
        h.Add("選択の状態: 1 つ選ぶ・外せる・複数・選べない", () =>
        {
            var single = new SelectionModel(SelectionMode.Single);
            single.Resize(3);
            Check.True(single.Tap(1), "選ぶ");
            Check.True(!single.Tap(1), "選んだ項目をもう一度押しても変わらない");
            Check.True(single.Tap(2) && single.SelectedIndex == 2 && single.SelectedIndices.Count == 1, "他を押すと移る");
            single.SetDisabled(0, true);
            Check.True(!single.Tap(0) && single.SelectedIndex == 2, "選べない項目は押しても変わらない");

            var optional = new SelectionModel(SelectionMode.SingleOptional);
            optional.Resize(2);
            optional.Tap(0);
            Check.True(optional.Tap(0) && optional.SelectedIndex == -1, "外せる");

            var multi = new SelectionModel(SelectionMode.Multiple);
            multi.Resize(7);
            multi.Tap(1); multi.Tap(3); multi.Tap(5);
            Check.Equal(3, multi.SelectedIndices.Count, "複数");
            multi.Tap(3);
            Check.Equal("1,5", string.Join(",", multi.SelectedIndices), "もう一度押すと外れる");
            Check.True(multi.Set(6, true) && !multi.Set(6, true), "プログラムから選ぶ（同じなら変わらない）");
        });

        // ── 動き ───────────────────────────────────────────────
        h.Add("動き: 行き先へ時間で動き、途中で行き先が変わっても今の値から動き直す", () =>
        {
            var tw = UiTween.At(0f);
            tw.Retarget(1f, 0.2f);
            Check.True(tw.IsRunning, "動いている");
            tw.Advance(0.1f);
            float mid = tw.Value;
            Check.True(mid > 0.5f && mid < 1f, $"途中（fastOutSlowIn で前半が速い: {mid}）");
            tw.Retarget(0f, 0.2f);
            Check.Close(mid, tw.Value, Eps, "戻り始めは今の値（跳ばない）");
            tw.Advance(0.3f);
            Check.Close(0, tw.Value, Eps, "行き先で止まる");
            Check.True(!tw.IsRunning, "止まった");
            tw.Jump(1f);
            Check.Close(1, tw.Value, Eps, "すぐ行き先");
        });

        // ── 時刻ホイール（W2-5）─────────────────────────────────
        WheelTests.Register(h);

        return h.Run();
    }
}
