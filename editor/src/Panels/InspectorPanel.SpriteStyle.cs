using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「形と塗り」（W2-4）の実装: SpriteComponent の形・塗り・9 スライス・影と、
/// CanvasClipComponent の切り抜きの形。
///
/// ■ 値の受け渡し
///   ランタイムは Sprite の値を serde の書式のまま ACTOR_COMPONENTS の "sprite_style"
///   （{"shape":{…},"fill":{…},"nine_slice":{…},"shadow":{…}}）、切り抜きの形を "clip_style"
///   （{"shape":"…","corner_radii":[…]}）に入れて送る（<see cref="SlotInfo.SpriteStyleJson"/>・
///   <see cref="SlotInfo.CanvasClipStyleJson"/>）。変更は
///   <c>SET_SPRITE_FIELD:{actor},{slot},{key},{value}</c>・<c>SET_CANVAS_CLIP_FIELD:…</c>（key はスクリプトと同じ欄名。
///   列挙は serde の名前、bool は true/false、数はカンマ区切り。runtime の sprite_style_ipc.rs）。Undo はランタイム側の共通機構。
///
/// ■ 関係のない欄は隠す（feedback_inspector_conditional_ui の方針）
///   角丸は矩形のときだけ、弧の欄は弧のときだけ、縁の色は縁の太さが 0 より大きいときだけ、グラデーションの欄は
///   単色でないときだけ（角度は線形・中心と半径は放射）、9 スライス・影の詳細はそれぞれ有効なときだけ出す。
///   値を変えるとランタイムが ACTOR_COMPONENTS を送り直し、インスペクタが組み直されるので、出す欄も追従する。
///   規則の正典は docs/ui_components.md。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>ACTOR_COMPONENTS で Sprite の形と塗りが入る鍵（Rust 側 component_ops.rs と一致）。</summary>
    private const string SpriteStyleJsonKey = "sprite_style";

    /// <summary>ACTOR_COMPONENTS で切り抜きの形が入る鍵（Rust 側 component_ops.rs と一致）。</summary>
    private const string CanvasClipStyleJsonKey = "clip_style";

    /// <summary>値が無いときの JSON。</summary>
    private const string SpriteStyleEmptyJson = "{}";

    /// <summary>形と塗りの数値の表示書式。</summary>
    private const string SpriteStyleNumberFormat = "F1";

    /// <summary>色の位置の表示書式（0..1 の割合）。</summary>
    private const string SpriteStyleStopFormat = "0.###";

    /// <summary>グラデーションの色の数の下限・上限（Rust 側 GRADIENT_MIN_COLORS・GRADIENT_MAX_COLORS と一致）。</summary>
    private const int SpriteGradientMinColors = 2;
    private const int SpriteGradientMaxColors = 4;

    /// <summary>色の要素数（RGBA）。</summary>
    private const int SpriteStyleColorLength = 4;

    /// <summary>9 スライスの倍率の既定（Rust 側 SpriteNineSlice の既定と一致）。</summary>
    private const float SpriteNineSliceScaleDefault = 1f;

    /// <summary>四隅の名前（左上・右上・右下・左下の並び。FFI・serde の並びと同じ）。</summary>
    private static readonly string[] SpriteCornerLabels = ["角丸 左上", "角丸 右上", "角丸 右下", "角丸 左下"];

    /// <summary>9 スライスの枠の名前（左・上・右・下）。</summary>
    private static readonly string[] SpriteNineSliceEdgeLabels = ["枠 左", "枠 上", "枠 右", "枠 下"];

    /// <summary>形の選択肢（serde の名前, 表示名）。</summary>
    private static readonly (string Value, string Label)[] SpriteShapeOptions =
        [("rect", "矩形（角丸）"), ("ellipse", "楕円・円"), ("arc", "弧（リング）")];

    /// <summary>塗りの選択肢。</summary>
    private static readonly (string Value, string Label)[] SpriteFillOptions =
        [("solid", "単色（カラー）"), ("linear", "線形グラデーション"), ("radial", "放射グラデーション")];

    /// <summary>9 スライスの埋め方の選択肢。</summary>
    private static readonly (string Value, string Label)[] SpriteNineSliceModeOptions =
        [("stretch", "伸ばす"), ("repeat", "繰り返す")];

    /// <summary>色の数の選択肢。</summary>
    private static readonly (string Value, string Label)[] SpriteGradientCountOptions =
        [("2", "2 色"), ("3", "3 色"), ("4", "4 色")];

    /// <summary>切り抜きの形の選択肢。</summary>
    private static readonly (string Value, string Label)[] CanvasClipShapeOptions =
        [("rect", "矩形"), ("rounded_rect", "角丸"), ("ellipse", "楕円・円"), ("sprite_shape", "スプライトの形に合わせる")];

    // ============================================================
    //  Sprite の形と塗り
    // ============================================================

    /// <summary>Sprite のインスペクタの末尾へ「形」「塗り」「9 スライス」「影」の節を足す。</summary>
    private void AddSpriteStyleSections(StackPanel sp, SlotInfo info)
    {
        var style = ParseStyleJson(info.SpriteStyleJson);

        // 1 つの欄をランタイムへ送る
        void Send(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_SPRITE_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        sp.Children.Add(BuildSpriteShapeSection(info, style, Send));
        sp.Children.Add(BuildSpriteFillSection(info, style, Send));
        sp.Children.Add(BuildSpriteNineSliceSection(info, style, Send));
        sp.Children.Add(BuildSpriteShadowSection(info, style, Send));
    }

    /// <summary>「形」の節（形・角丸・弧・縁）。</summary>
    private UIElement BuildSpriteShapeSection(SlotInfo info, JsonElement style, Action<string, string> send)
    {
        var section = BuildSection("形");
        var body = (StackPanel)section.Child;
        var kind = StyleString(style, "shape/kind", "rect");
        body.Children.Add(BuildLayoutChoiceRow("形", kind, SpriteShapeOptions, v => send("shape", v)));

        if (kind == "rect")
        {
            // 四隅の角丸（1 つずつ。送るときは 4 つまとめて）
            var radii = StyleFloats(style, "shape/corner_radii", 4);
            for (int i = 0; i < radii.Length; i++)
            {
                int corner = i;
                body.Children.Add(PlainNumberRow(info, SpriteCornerLabels[i], radii[i], v =>
                {
                    var next = (float[])radii.Clone();
                    next[corner] = Math.Max(0f, v);
                    send("corner_radii", JoinFloats(next));
                }));
            }
            body.Children.Add(PlainNumberRow(info, "角丸 すべて", radii.Max(),
                v => send("corner_radii", JoinFloats(Enumerable.Repeat(Math.Max(0f, v), 4).ToArray()))));
        }
        else if (kind == "arc")
        {
            body.Children.Add(PlainNumberRow(info, "開始角（度）", StyleFloat(style, "shape/arc_start", 0f),
                v => send("arc_start", Invariant(v))));
            body.Children.Add(PlainNumberRow(info, "角度（度）", StyleFloat(style, "shape/arc_sweep", 0f),
                v => send("arc_sweep", Invariant(v))));
            body.Children.Add(PlainNumberRow(info, "太さ", StyleFloat(style, "shape/arc_thickness", 0f),
                v => send("arc_thickness", Invariant(Math.Max(0f, v)))));
            body.Children.Add(BuildCheckRow("端を丸く", StyleBool(style, "shape/arc_round_caps", false),
                v => send("arc_round_caps", v ? "true" : "false")));
        }

        // 縁の線（太さが 0 より大きいときだけ色を出す）
        var borderWidth = StyleFloat(style, "shape/border_width", 0f);
        body.Children.Add(PlainNumberRow(info, "縁の太さ", borderWidth, v => send("border_width", Invariant(Math.Max(0f, v)))));
        if (borderWidth > 0f)
            body.Children.Add(StyleColorRow(info, "縁の色", StyleFloats(style, "shape/border_color", SpriteStyleColorLength),
                c => send("border_color", JoinFloats(c))));

        body.Children.Add(CanvasLayoutHint(
            "長さはキャンバスの単位（幅・高さと同じ。dp のキャンバスなら dp）。角丸・楕円・弧の外は描かれず、押せません。"
            + "縁は形の内側に引きます。"));
        return section;
    }

    /// <summary>「塗り」の節（種類・色・色の位置・角度／中心と半径）。</summary>
    private UIElement BuildSpriteFillSection(SlotInfo info, JsonElement style, Action<string, string> send)
    {
        var section = BuildSection("塗り");
        var body = (StackPanel)section.Child;
        var kind = StyleString(style, "fill/kind", "solid");
        body.Children.Add(BuildLayoutChoiceRow("塗り", kind, SpriteFillOptions, v => send("fill", v)));
        if (kind == "solid")
        {
            body.Children.Add(CanvasLayoutHint("単色はスプライトのカラーで塗ります。"));
            return section;
        }

        // 色（2〜4 色）。色の数を変えると最後の色で補うか切り詰める
        var colors = StyleColorList(style, "fill/colors");
        var count = Math.Clamp(colors.Count, SpriteGradientMinColors, SpriteGradientMaxColors);
        while (colors.Count < count) colors.Add(colors.Count > 0 ? colors[^1] : [1f, 1f, 1f, 1f]);
        body.Children.Add(BuildLayoutChoiceRow("色の数", count.ToString(CultureInfo.InvariantCulture), SpriteGradientCountOptions, v =>
        {
            int n = int.Parse(v, CultureInfo.InvariantCulture);
            var next = colors.Take(n).ToList();
            while (next.Count < n) next.Add(next[^1]);
            send("fill_colors", JoinFloats(next.SelectMany(c => c).ToArray()));
        }));
        for (int i = 0; i < count; i++)
        {
            int index = i;
            body.Children.Add(StyleColorRow(info, $"色 {i + 1}", colors[i], c =>
            {
                var next = colors.Take(count).Select(x => (float[])x.Clone()).ToList();
                next[index] = c;
                send("fill_colors", JoinFloats(next.SelectMany(x => x).ToArray()));
            }));
        }

        // 色の位置（カンマ区切り。空 = 等間隔）
        var stops = StyleFloatList(style, "fill/stops");
        body.Children.Add(StopsRow(stops, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) { send("fill_stops_clear", "true"); return; }
            send("fill_stops", text.Replace(" ", ""));
        }));

        if (kind == "linear")
        {
            body.Children.Add(PlainNumberRow(info, "角度（度）", StyleFloat(style, "fill/angle", 0f),
                v => send("fill_angle", Invariant(v))));
            body.Children.Add(CanvasLayoutHint("0 = 左 → 右、90 = 上 → 下（時計回り）。四隅がちょうど最初と最後の色になります。"));
        }
        else
        {
            var center = StyleFloats(style, "fill/center", 2);
            var radius = StyleFloats(style, "fill/radius", 2);
            body.Children.Add(PlainNumberRow(info, "中心 X（割合）", center[0], v => send("fill_center", JoinFloats([v, center[1]]))));
            body.Children.Add(PlainNumberRow(info, "中心 Y（割合）", center[1], v => send("fill_center", JoinFloats([center[0], v]))));
            body.Children.Add(PlainNumberRow(info, "半径 X（割合）", radius[0], v => send("fill_radius", JoinFloats([Math.Max(0f, v), radius[1]]))));
            body.Children.Add(PlainNumberRow(info, "半径 Y（割合）", radius[1], v => send("fill_radius", JoinFloats([radius[0], Math.Max(0f, v)]))));
            body.Children.Add(CanvasLayoutHint("中心・半径は矩形に対する割合（0.5, 0.5 = 内接する楕円の縁で最後の色）。"));
        }
        body.Children.Add(CanvasLayoutHint("グラデーションの色にもスプライトのカラーが掛かります（白なら色のまま）。"));
        return section;
    }

    /// <summary>「9 スライス」の節。</summary>
    private UIElement BuildSpriteNineSliceSection(SlotInfo info, JsonElement style, Action<string, string> send)
    {
        var section = BuildSection("9 スライス");
        var body = (StackPanel)section.Child;
        var enabled = StyleBool(style, "nine_slice/enabled", false);
        body.Children.Add(BuildCheckRow("9 スライスで描く", enabled, v => send("nine_slice", v ? "true" : "false")));
        if (!enabled) return section;

        if (string.IsNullOrEmpty(info.TexturePath))
            body.Children.Add(CanvasLayoutHint("テクスチャが無いと 9 スライスは効きません。"));
        var border = StyleFloats(style, "nine_slice/border", 4);
        for (int i = 0; i < border.Length; i++)
        {
            int edge = i;
            body.Children.Add(PlainNumberRow(info, SpriteNineSliceEdgeLabels[i], border[i], v =>
            {
                var next = (float[])border.Clone();
                next[edge] = Math.Max(0f, v);
                send("nine_slice_border", JoinFloats(next));
            }));
        }
        body.Children.Add(PlainNumberRow(info, "枠の倍率", StyleFloat(style, "nine_slice/scale", SpriteNineSliceScaleDefault),
            v => send("nine_slice_scale", Invariant(Math.Max(0f, v)))));
        body.Children.Add(BuildLayoutChoiceRow("辺", StyleString(style, "nine_slice/edge_mode", "stretch"),
            SpriteNineSliceModeOptions, v => send("nine_slice_edge", v)));
        body.Children.Add(BuildLayoutChoiceRow("中央", StyleString(style, "nine_slice/center_mode", "stretch"),
            SpriteNineSliceModeOptions, v => send("nine_slice_center", v)));
        body.Children.Add(BuildCheckRow("中央を描く", StyleBool(style, "nine_slice/fill_center", true),
            v => send("nine_slice_fill_center", v ? "true" : "false")));
        body.Children.Add(CanvasLayoutHint(
            "枠はテクスチャの画素。描く幅 = 枠 × 倍率（キャンバスの単位）。四隅は大きさを保ち、辺と中央は伸ばすか繰り返します"
            + "（繰り返しは回数を丸めて端で切れません）。"));
        return section;
    }

    /// <summary>「影」の節。</summary>
    private UIElement BuildSpriteShadowSection(SlotInfo info, JsonElement style, Action<string, string> send)
    {
        var section = BuildSection("影");
        var body = (StackPanel)section.Child;
        var enabled = StyleBool(style, "shadow/enabled", false);
        body.Children.Add(BuildCheckRow("影を描く", enabled, v => send("shadow", v ? "true" : "false")));
        if (!enabled) return section;

        body.Children.Add(StyleColorRow(info, "影の色", StyleFloats(style, "shadow/color", SpriteStyleColorLength),
            c => send("shadow_color", JoinFloats(c))));
        var offset = StyleFloats(style, "shadow/offset", 2);
        body.Children.Add(PlainNumberRow(info, "ずれ X", offset[0], v => send("shadow_offset", JoinFloats([v, offset[1]]))));
        body.Children.Add(PlainNumberRow(info, "ずれ Y", offset[1], v => send("shadow_offset", JoinFloats([offset[0], v]))));
        body.Children.Add(PlainNumberRow(info, "ぼかし", StyleFloat(style, "shadow/blur", 0f),
            v => send("shadow_blur", Invariant(Math.Max(0f, v)))));
        body.Children.Add(CanvasLayoutHint("形と同じ形をずらしてぼかし、後ろに描きます。"));
        return section;
    }

    // ============================================================
    //  切り抜きの形（CanvasClip）
    // ============================================================

    /// <summary>切り抜きのインスペクタへ「形」と（角丸のときだけ）四隅の半径の行を足す。</summary>
    private void AddCanvasClipShapeRows(StackPanel body, SlotInfo info, Action<string, string> send)
    {
        var style = ParseStyleJson(info.CanvasClipStyleJson);
        var shape = StyleString(style, "shape", "rect");
        body.Children.Add(BuildLayoutChoiceRow("形", shape, CanvasClipShapeOptions, v => send("shape", v)));
        if (shape == "rounded_rect")
        {
            var radii = StyleFloats(style, "corner_radii", 4);
            for (int i = 0; i < radii.Length; i++)
            {
                int corner = i;
                body.Children.Add(PlainNumberRow(info, SpriteCornerLabels[i], radii[i], v =>
                {
                    var next = (float[])radii.Clone();
                    next[corner] = Math.Max(0f, v);
                    send("corner_radii", JoinFloats(next));
                }));
            }
        }
        if (shape != "rect")
            body.Children.Add(CanvasLayoutHint(
                "角丸・楕円はいちばん内側の 1 つだけを画素単位で切り、外側の角丸の枠は外接矩形で切ります。"
                + "画素単位で切るのはスプライト（画像・形）だけで、文字・図形は外接矩形で切ります。"));
    }

    // ============================================================
    //  行と値の読み書きの小道具
    // ============================================================

    /// <summary>JSON を読む（壊れていれば空のオブジェクト）。</summary>
    private static JsonElement ParseStyleJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? SpriteStyleEmptyJson : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse(SpriteStyleEmptyJson);
            return empty.RootElement.Clone();
        }
    }

    /// <summary>"/" 区切りの道筋で欄の文字列を読む。</summary>
    private static string StyleString(JsonElement root, string path, string fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    /// <summary>欄の数を読む。</summary>
    private static float StyleFloat(JsonElement root, string path, float fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    /// <summary>欄の真偽を読む。</summary>
    private static bool StyleBool(JsonElement root, string path, bool fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    /// <summary>欄の数の配列を読む（長さ n。足りなければ 0 で補う）。</summary>
    private static float[] StyleFloats(JsonElement root, string path, int n)
    {
        var list = StyleFloatList(root, path);
        while (list.Count < n) list.Add(0f);
        return list.Take(n).ToArray();
    }

    /// <summary>欄の数の配列を読む（長さはそのまま）。</summary>
    private static System.Collections.Generic.List<float> StyleFloatList(JsonElement root, string path)
    {
        var list = new System.Collections.Generic.List<float>();
        if (TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number) list.Add(e.GetSingle());
        return list;
    }

    /// <summary>色の配列の配列を読む（[[r,g,b,a], …]）。</summary>
    private static System.Collections.Generic.List<float[]> StyleColorList(JsonElement root, string path)
    {
        var list = new System.Collections.Generic.List<float[]>();
        if (TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Array)
                {
                    var c = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetSingle()).ToList();
                    while (c.Count < SpriteStyleColorLength) c.Add(1f);
                    list.Add(c.Take(SpriteStyleColorLength).ToArray());
                }
        return list;
    }

    /// <summary>数をインバリアントな文字列へ。</summary>
    private static string Invariant(float v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>数の列をカンマ区切りの文字列へ。</summary>
    private static string JoinFloats(float[] values) => string.Join(",", values.Select(Invariant));

    /// <summary>素の数値の行（⟲ なし。Enter・フォーカスが外れた・ドラッグで確定）。</summary>
    private UIElement PlainNumberRow(SlotInfo info, string label, float value, Action<float> onCommit)
        => BuildResettableFloatRow(info.SlotIdx, SpriteComponentType, label, value, null, SpriteStyleNumberFormat, onCommit);

    /// <summary>色の行（市松背景つきスウォッチ・⟲ なし）。</summary>
    private UIElement StyleColorRow(SlotInfo info, string label, float[] c, Action<float[]> onPicked)
        => BuildColorPickerRow(label, c[0], c[1], c[2], c[3], info.SlotIdx, SpriteComponentType, null,
            (r, g, b, a) => onPicked([r, g, b, a]));

    /// <summary>色の位置の行（カンマ区切りの文字。空 = 等間隔。Enter かフォーカスが外れたら確定）。</summary>
    private static UIElement StopsRow(System.Collections.Generic.List<float> stops, Action<string> onCommit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = "色の位置", FontSize = CanvasLayoutLabelFontSize, Width = InspectorRowLabelWidth,
            Foreground = new SolidColorBrush(CanvasLayoutLabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var initial = string.Join(", ", stops.Select(s => s.ToString(SpriteStyleStopFormat, CultureInfo.InvariantCulture)));
        var box = new TextBox
        {
            Text = initial,
            Width = CanvasLayoutComboWidth,
            ToolTip = "0〜1 をカンマ区切りで（例 0, 0.3, 1）。空なら等間隔。",
        };
        // 変えたときだけ送る（フォーカスが外れただけでシーンを「変更あり」にしない）
        void Commit()
        {
            if (box.Text.Trim() != initial) onCommit(box.Text);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Return or System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();
        row.Children.Add(box);
        return row;
    }
}
