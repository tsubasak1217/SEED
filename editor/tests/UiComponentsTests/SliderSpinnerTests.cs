using System;
using System.Linq;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する
using static UiComponentsTests.WidgetExtensionTests;

namespace UiComponentsTests;

/// <summary>
/// 2026-10-03 の部品の手直し（lane2 の残件）の純粋な計算のテスト（docs/ui_components.md §6）:
/// スライダの刻みの点（SliderTicks。Flutter の divisions・Wake or Pay の FullWidthSlider の TickCount）と、そのプレハブ
/// （slider.actor の Ticks・slider_tick.actor）、不定の進捗（ProgressSpinner）が祖先の切り抜きの外で回らないための判定（ClipVisibility）。
/// </summary>
public static class SliderSpinnerTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>プレハブ（slider.actor）の溝: 左端 10・長さ 220・中心の高さ 24。</summary>
    private static readonly SliderTrack Track = new(10f, 220f, 24f);

    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── 刻みの点 ─────────────────────────────────────────────
        h.Add("スライダの刻みの点: 数（両端を含めて刻み ＋ 1・0 と負は無し・上限・くっつくほど詰まれば無し・溝が無ければ無し）", () =>
        {
            float dot = theme.Number(UiTokens.SizeSliderTick);
            Check.Close(3, dot, Eps, "size.slider_tick 3（Wake or Pay の FullWidthSlider の app.size.slider_tick と同じ）");
            Check.Equal(0, SliderTicks.DotCount(0, Track.Length, dot), "既定 0 は点なし（従来どおり）");
            Check.Equal(0, SliderTicks.DotCount(-3, Track.Length, dot), "負は点なし");
            Check.Equal(5, SliderTicks.DotCount(4, Track.Length, dot), "4 刻み（1〜5 分）は 5 つ");
            Check.Equal(31, SliderTicks.DotCount(30, Track.Length, dot), "30 刻み（Wake or Pay の上限）は 31 個（間隔 7.3 ≧ 6）");
            Check.Equal(0, SliderTicks.DotCount(40, Track.Length, dot), "40 刻みは間隔 5.5 < 直径 × 2 = 6 なので描かない（点がくっつく）");
            Check.Equal(SliderTicks.MaxTickCount, SliderTicks.Normalize(int.MaxValue), "刻みの数は上限まで");
            Check.Equal(SliderTicks.MaxTickCount + 1, SliderTicks.DotCount(int.MaxValue, 10_000f, dot), "長い溝でも上限 ＋ 1 個まで（ノードを作りすぎない）");
            Check.Equal(0, SliderTicks.DotCount(4, 0f, dot), "溝の長さ 0 は描かない");
            Check.Equal(0, SliderTicks.DotCount(4, float.NaN, dot), "壊れた溝の長さは描かない");
            Check.Equal(5, SliderTicks.DotCount(4, Track.Length, 0f), "直径 0 は間隔を見ない（数だけ）");
        });

        h.Add("スライダの刻みの点: 置き場（両端は溝の端・等間隔・溝の中心の高さ）と塗りの上か（値の割合以下・ちょうど同じは上）と色", () =>
        {
            Check.Close(Track.X, SliderTicks.CenterX(0, 4, Track), Eps, "左端の点 = 溝の左端");
            Check.Close(Track.X + Track.Length, SliderTicks.CenterX(4, 4, Track), Eps, "右端の点 = 溝の右端");
            Check.Close(Track.X + Track.Length * 0.25f, SliderTicks.CenterX(1, 4, Track), Eps, "等間隔");
            var topLeft = SliderTicks.TopLeft(SliderTicks.CenterX(2, 4, Track), Track.CenterY, 3f);
            Check.Close(Track.X + Track.Length * 0.5f - 1.5f, topLeft.x, Eps, "左上 = 中心 − 半径（pivot 0 の点）");
            Check.Close(Track.CenterY - 1.5f, topLeft.y, Eps, "高さは溝の中心");
            // 値 3（1〜5 の真ん中 = 割合 0.5）: 点 0・1・2 が塗りの上、3・4 は外
            float half = ValueMath.Fraction(3f, 1f, 5f);
            Check.Equal("TTTFF", string.Concat(Enumerable.Range(0, 5).Select(i => SliderTicks.IsActive(i, 4, half) ? "T" : "F")), "値の点まで塗りの上");
            Check.Equal("TFFFF", string.Concat(Enumerable.Range(0, 5).Select(i => SliderTicks.IsActive(i, 4, 0f) ? "T" : "F")), "最小は左端だけ");
            Check.Equal("TTTTT", string.Concat(Enumerable.Range(0, 5).Select(i => SliderTicks.IsActive(i, 4, 1f) ? "T" : "F")), "最大は全部");
            Check.True(SliderTicks.IsActive(1, 3, 1f / 3f), "割合の丸めの誤差でもちょうど点の上の値はその点を上とみなす");
            Check.True(!SliderTicks.IsActive(1, 4, float.NaN), "壊れた割合は 0 とみなす");
            var on = SliderTicks.ColorOf(theme, active: true, enabled: true);
            var off = SliderTicks.ColorOf(theme, active: false, enabled: true);
            Check.Close(theme.Color(UiTokens.ColorOnPrimary).r, on.r, Eps, "塗りの上は color.on_primary");
            Check.Close(theme.Color(UiTokens.ColorOnSurfaceMuted).g, off.g, Eps, "塗りの外は color.on_surface_muted");
            var disabled = SliderTicks.ColorOf(theme, active: true, enabled: false);
            Check.Close(on.a * theme.Number(UiTokens.OpacityDisabled), disabled.a, Eps, "無効は opacity.disabled で薄める");
        });

        h.Add("スライダの刻みの点: プレハブ（slider.actor の Ticks は Fill と Thumb の間・部品なし・位置 0、slider_tick.actor は楕円の Sprite）", () =>
        {
            var slider = Load("prefabs", "slider.actor");
            Check.Equal("Track,Fill,Ticks,Thumb", string.Join(",", Children(slider).Select(Name)), "Ticks は Fill と Thumb の間（塗りの上・つまみの下に描く順）");
            var ticks = Child(slider, "Ticks");
            Check.Equal(0, ticks.GetProperty("components").GetArrayLength(), "Ticks は部品なし（当たりも描画も持たない）");
            var pos = ticks.GetProperty("canvas_transform").GetProperty("position");
            Check.True(pos[0].GetSingle() == 0f && pos[1].GetSingle() == 0f, "Ticks は位置 0（点の置き場は部品の左上から）");
            Check.Equal("0", Data(slider, "ScriptComponent").GetProperty("fields").TryGetProperty("TickCount", out var count) ? count.GetString() : "0",
                "プレハブの既定の刻みの数は 0（点を作らない）");
            var tick = Load("prefabs", "slider_tick.actor");
            var sprite = Data(tick, "SpriteComponent");
            Check.Equal("ellipse", sprite.GetProperty("shape").GetProperty("kind").GetString(), "点は楕円");
            Check.Close(theme.Number(UiTokens.SizeSliderTick), sprite.GetProperty("width").GetSingle(), Eps, "点の大きさ = size.slider_tick");
            var pivot = tick.GetProperty("canvas_transform").GetProperty("pivot");
            Check.True(pivot[0].GetSingle() == 0f && pivot[1].GetSingle() == 0f, "点は pivot 0（SliderTicks.TopLeft の前提）");
        });

        // ── 不定の進捗が切り抜きの外で回らない ───────────────────────
        h.Add("スピナーの見え方: 画面 ∩ 祖先の切り抜き（積）と重なるときだけ見える・境目で接するだけは見えない", () =>
        {
            var screen = ClipVisibility.ScreenRect(1080f, 2400f);
            var spinner = new Rect(500f, 1000f, 36f, 36f);
            Check.True(ClipVisibility.IsVisible(spinner, true, screen, ReadOnlySpan<Rect>.Empty), "切り抜きが無ければ画面と重なるかだけ（従来どおり）");
            Check.True(!ClipVisibility.IsVisible(new Rect(-40f, 0f, 36f, 36f), true, screen, ReadOnlySpan<Rect>.Empty), "画面の外は見えない（従来どおり）");
            // スクロールの窓（y 800〜1200）の中・外
            var viewport = new Rect(0f, 800f, 1080f, 400f);
            Check.True(ClipVisibility.IsVisible(spinner, true, screen, new[] { viewport }), "窓の中は見える");
            Check.True(!ClipVisibility.IsVisible(new Rect(500f, 1300f, 36f, 36f), true, screen, new[] { viewport }), "窓の下へ流れた（画面の中だが窓の外）は見えない");
            Check.True(ClipVisibility.IsVisible(new Rect(500f, 1190f, 36f, 36f), true, screen, new[] { viewport }), "窓の端に少しでも掛かれば見える");
            Check.True(!ClipVisibility.IsVisible(new Rect(500f, 1200f, 36f, 36f), true, screen, new[] { viewport }), "窓の下端で接するだけは見えない");
            // 入れ子の切り抜き（外の窓 ∩ 内の窓）: どちらか一方の外なら見えない（順は問わない）
            var inner = new Rect(0f, 1100f, 540f, 400f);
            var inBoth = new Rect(100f, 1120f, 36f, 36f);
            Check.True(ClipVisibility.IsVisible(inBoth, true, screen, new[] { viewport, inner }), "両方の中は見える");
            Check.True(ClipVisibility.IsVisible(inBoth, true, screen, new[] { inner, viewport }), "順は問わない");
            Check.True(!ClipVisibility.IsVisible(new Rect(100f, 1250f, 36f, 36f), true, screen, new[] { viewport, inner }), "内の窓の中でも外の窓の外なら見えない");
            Check.True(!ClipVisibility.IsVisible(new Rect(700f, 1120f, 36f, 36f), true, screen, new[] { viewport, inner }), "外の窓の中でも内の窓の外なら見えない");
            Check.True(!ClipVisibility.IsVisible(spinner, true, screen, new[] { viewport, new Rect(0f, 0f, 1080f, 300f) }), "重ならない 2 つの窓の積は空（何も見えない）");
        });

        h.Add("スピナーの見え方: 見えるほうへ倒す（矩形が測れていないノード・面積の無い／壊れた切り抜きは数えない）", () =>
        {
            var screen = ClipVisibility.ScreenRect(1080f, 2400f);
            var outside = new Rect(500f, 1300f, 36f, 36f);
            var viewport = new Rect(0f, 800f, 1080f, 400f);
            Check.True(ClipVisibility.IsVisible(outside, false, screen, new[] { viewport }), "ノードのレイアウトが無い（作った直後）は見える（最初のフレームを描かせる）");
            Check.True(ClipVisibility.IsVisible(outside, true, screen, new[] { new Rect(0f, 800f, 1080f, 0f) }),
                "高さ 0 の切り抜き（測れていない・Canvas も Sprite も無い）は数えない＝回り続ける（以前と同じ）");
            Check.True(ClipVisibility.IsVisible(outside, true, screen, new[] { new Rect(float.NaN, 0f, 10f, 10f) }), "NaN の切り抜きは数えない");
            Check.True(!ClipVisibility.IsUsableClip(Rect.Zero) && ClipVisibility.IsUsableClip(viewport), "数える切り抜きの判定");
            Check.True(!ClipVisibility.IsVisible(outside, true, ClipVisibility.ScreenRect(0f, 0f), ReadOnlySpan<Rect>.Empty), "画面の大きさが 0 なら見えない（従来どおり）");
            var narrowed = ClipVisibility.Narrow(screen, viewport);
            Check.True(narrowed == viewport, "画面の中の窓の積は窓そのもの");
            var apart = ClipVisibility.Narrow(viewport, new Rect(0f, 0f, 100f, 100f));
            Check.True(apart.width == 0f || apart.height == 0f, "重ならない積は大きさ 0");
            Check.True(!ClipVisibility.Overlaps(new Rect(10f, 10f, 0f, 0f), apart), "大きさ 0 の窓とは重ならない");
            Check.True(ClipVisibility.Overlaps(new Rect(900f, 900f, 0f, 0f), viewport), "大きさ 0 のノード（点）も窓の内側なら重なる");
        });
    }
}
