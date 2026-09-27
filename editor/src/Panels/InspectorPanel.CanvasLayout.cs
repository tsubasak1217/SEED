using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「レイアウトの部品」（W2-1b）の実装。
///
/// ■ 対象
///   CanvasStack（縦・横に並べる）・CanvasWrap（折り返し）・CanvasGrid（格子）・CanvasLayoutItem（子の側の指定）・
///   CanvasSafeArea（安全領域）の 5 種と、CanvasComponent の寸法の単位（px / dp。ルートキャンバスだけ）。
///   規則の正典は docs/canvas_camera_rework.md §6.3〜6.5。
///
/// ■ 値の受け渡し
///   ランタイムは値を serde の書式のまま ACTOR_COMPONENTS の "layout" に入れて送る（<see cref="SlotInfo.CanvasLayoutJson"/>）。
///   変更は <c>SET_CANVAS_LAYOUT_FIELD:{actor},{slot},{key},{value}</c>（key は serde の欄の名前。入れ子は "/" 区切り、
///   value は数値・true/false・列挙の名前）。ランタイムは値を JSON として型へ戻せたときだけ反映する（範囲外は捨てる）。
///   Undo はランタイム側の共通機構、既定値へのリセットは RESET_COMPONENT_FIELD（行の ⟲）。
///   単位は <c>SET_CANVAS_UNIT:{actor},{slot},{px|dp}</c>。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>ACTOR_COMPONENTS でレイアウトの部品の値が入る鍵（Rust 側 canvas_layout_slots::INSPECTOR_KEY と一致）。</summary>
    private const string CanvasLayoutJsonKey = "layout";

    /// <summary>値が無いときの JSON。</summary>
    private const string CanvasLayoutEmptyJson = "{}";

    /// <summary>寸法の単位: 画素（既定。Rust 側 CanvasUnit::Px の serde 名）。</summary>
    private const string CanvasUnitPx = "px";

    /// <summary>寸法の単位: 端末に依らない単位（Rust 側 CanvasUnit::Dp の serde 名）。</summary>
    private const string CanvasUnitDp = "dp";

    /// <summary>ACTOR_COMPONENTS の "type" 文字列（ComponentCatalog と一致）。</summary>
    private const string CanvasStackComponentType = "CanvasStackComponent";
    /// <summary>折り返しの "type" 文字列。</summary>
    private const string CanvasWrapComponentType = "CanvasWrapComponent";
    /// <summary>格子の "type" 文字列。</summary>
    private const string CanvasGridComponentType = "CanvasGridComponent";
    /// <summary>子の側の指定の "type" 文字列。</summary>
    private const string CanvasLayoutItemComponentType = "CanvasLayoutItemComponent";
    /// <summary>安全領域の "type" 文字列。</summary>
    private const string CanvasSafeAreaComponentType = "CanvasSafeAreaComponent";

    /// <summary>説明文の文字色（他のコンポーネントの補足文と同じ灰色）。</summary>
    private static readonly Color CanvasLayoutHintColor = Color.FromRgb(0x66, 0x66, 0x66);
    /// <summary>説明文の文字の大きさ（他のコンポーネントの補足文と同じ）。</summary>
    private const double CanvasLayoutHintFontSize = 10;
    /// <summary>選択行のラベルの文字色（BuildCheckRow と同じ）。</summary>
    private static readonly Color CanvasLayoutLabelColor = Color.FromRgb(0xAA, 0xAA, 0xAA);
    /// <summary>行のラベルの文字の大きさ。</summary>
    private const double CanvasLayoutLabelFontSize = 11;
    /// <summary>選択肢の箱の幅（他のコンポーネントの形状の選択と同じ）。</summary>
    private const double CanvasLayoutComboWidth = 170;
    /// <summary>数値の欄の表示の書式（位置・大きさ）。</summary>
    private const string CanvasLayoutNumberFormat = "F1";
    /// <summary>比・重みの欄の表示の書式。</summary>
    private const string CanvasLayoutRatioFormat = "F2";
    /// <summary>列数の欄の表示の書式（整数）。</summary>
    private const string CanvasLayoutCountFormat = "F0";

    // ── 選択肢（serde の名前, 表示名）。Rust 側 canvas_layout_params.rs の snake_case と一致させる ──

    /// <summary>並べる向き。</summary>
    private static readonly (string Value, string Label)[] LayoutDirectionOptions =
    {
        ("vertical", "縦（上から下）"),
        ("horizontal", "横（左から右）"),
    };

    /// <summary>主軸の揃え。</summary>
    private static readonly (string Value, string Label)[] MainAlignOptions =
    {
        ("start", "先頭"),
        ("center", "中央"),
        ("end", "末尾"),
        ("space_between", "両端をそろえて間を等分"),
        ("space_around", "前後を等分"),
        ("space_evenly", "端と間をすべて等分"),
    };

    /// <summary>交差軸の揃え。</summary>
    private static readonly (string Value, string Label)[] CrossAlignOptions =
    {
        ("start", "先頭"),
        ("center", "中央"),
        ("end", "末尾"),
        ("stretch", "いっぱいに伸ばす"),
    };

    /// <summary>子の側の揃えの上書き。</summary>
    private static readonly (string Value, string Label)[] ItemAlignOptions =
    {
        ("auto", "コンテナに従う"),
        ("start", "先頭"),
        ("center", "中央"),
        ("end", "末尾"),
        ("stretch", "いっぱいに伸ばす"),
    };

    /// <summary>非表示・無効の子の扱い。</summary>
    private static readonly (string Value, string Label)[] HiddenChildrenOptions =
    {
        ("collapse", "詰める"),
        ("keep_space", "場所を残す"),
    };

    /// <summary>寸法の単位。</summary>
    private static readonly (string Value, string Label)[] CanvasUnitOptions =
    {
        (CanvasUnitPx, "px（画素・従来）"),
        (CanvasUnitDp, "dp（端末に依らない単位）"),
    };

    // ── 共通の小道具 ─────────────────────────────────────────

    /// <summary>レイアウトの部品の値（生の JSON）を読む。壊れていれば空のオブジェクト。</summary>
    private static JsonElement ParseCanvasLayout(SlotInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(info.CanvasLayoutJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse(CanvasLayoutEmptyJson);
            return empty.RootElement.Clone();
        }
    }

    /// <summary>数値の欄（入れ子は "/" 区切り。無ければ既定値）。</summary>
    private static float LayoutNumber(JsonElement root, string path, float fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    /// <summary>真偽の欄（無ければ既定値）。</summary>
    private static bool LayoutBool(JsonElement root, string path, bool fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    /// <summary>文字列（列挙の名前）の欄（無ければ既定値）。</summary>
    private static string LayoutString(JsonElement root, string path, string fallback)
        => TryLayoutValue(root, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    /// <summary>"/" 区切りの道筋で欄を引く。</summary>
    private static bool TryLayoutValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var part in path.Split('/'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        }
        return true;
    }

    /// <summary>補足の説明文。</summary>
    private static TextBlock CanvasLayoutHint(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(CanvasLayoutHintColor),
        FontSize = CanvasLayoutHintFontSize,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 2),
    };

    /// <summary>選択肢の行（ラベル + ComboBox）。選び直したら onChange に serde の名前を渡す。</summary>
    private static UIElement BuildLayoutChoiceRow(
        string label, string current, (string Value, string Label)[] options, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = CanvasLayoutLabelFontSize, Width = InspectorRowLabelWidth,
            Foreground = new SolidColorBrush(CanvasLayoutLabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var combo = new ComboBox { Width = CanvasLayoutComboWidth, FontSize = CanvasLayoutLabelFontSize };
        foreach (var (value, text) in options)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        var index = Array.FindIndex(options, o => o.Value == current);
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value) onChange(value);
        };
        row.Children.Add(combo);
        return row;
    }

    /// <summary>
    /// レイアウトの部品の行を組み立てる道具（送信先・種別・値を 1 つにまとめ、各 Build*SlotContent を短くする）。
    /// </summary>
    private sealed class CanvasLayoutRows
    {
        private readonly InspectorPanel _panel;
        private readonly SlotInfo _info;
        private readonly string _componentType;
        /// <summary>今の値（ランタイムが送った JSON）。</summary>
        public JsonElement Values { get; }

        public CanvasLayoutRows(InspectorPanel panel, SlotInfo info, string componentType)
        {
            _panel = panel;
            _info = info;
            _componentType = componentType;
            Values = ParseCanvasLayout(info);
        }

        /// <summary>1 つの欄をランタイムへ送る（SET_CANVAS_LAYOUT_FIELD）。</summary>
        public void Send(string key, string value)
        {
            if (_panel._currentActorId < 0) return;
            _panel._runtime?.SendToRuntime($"SET_CANVAS_LAYOUT_FIELD:{_panel._currentActorId},{_info.SlotIdx},{key},{value}");
        }

        /// <summary>数値の行（⟲ で既定値へ戻せる）。</summary>
        public UIElement Number(string label, string key, float fallback, string format = CanvasLayoutNumberFormat)
            => _panel.BuildResettableFloatRow(_info.SlotIdx, _componentType, label,
                LayoutNumber(Values, key, fallback), key, format,
                v => Send(key, v.ToString(CultureInfo.InvariantCulture)));

        /// <summary>整数の行（列数。小数は丸め、負は 0）。</summary>
        public UIElement Count(string label, string key, int fallback)
            => _panel.BuildResettableFloatRow(_info.SlotIdx, _componentType, label,
                LayoutNumber(Values, key, fallback), key, CanvasLayoutCountFormat,
                v => Send(key, Math.Max(0, (int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)));

        /// <summary>真偽の行。</summary>
        public UIElement Check(string label, string key, bool fallback)
            => BuildCheckRow(label, LayoutBool(Values, key, fallback), v => Send(key, v ? "true" : "false"));

        /// <summary>選択肢の行。</summary>
        public UIElement Choice(string label, string key, string fallback, (string Value, string Label)[] options)
            => BuildLayoutChoiceRow(label, LayoutString(Values, key, fallback), options, v => Send(key, v));

        /// <summary>余白の 4 行（左・上・右・下）。</summary>
        public void Padding(StackPanel body)
        {
            body.Children.Add(Number("余白 左", "padding/left", 0f));
            body.Children.Add(Number("余白 上", "padding/top", 0f));
            body.Children.Add(Number("余白 右", "padding/right", 0f));
            body.Children.Add(Number("余白 下", "padding/bottom", 0f));
        }

        /// <summary>中身に合わせる・非表示の子の扱いの共通の行（3 つのコンテナで同じ）。</summary>
        public void FitAndHidden(StackPanel body)
        {
            body.Children.Add(Check("幅を中身に", "fit_width", false));
            body.Children.Add(Check("高さを中身に", "fit_height", false));
            body.Children.Add(Choice("非表示の子", "hidden_children", "collapse", HiddenChildrenOptions));
            body.Children.Add(CanvasLayoutHint(
                "「中身に合わせる」は CanvasComponent を持つコンテナの領域をその軸だけ中身の大きさにします"
                + "（CanvasComponent が無いコンテナは常に中身の大きさ）。非表示・無効の子は「詰める」と場所を取りません。"));
        }
    }

    // ── 各コンポーネントの UI ───────────────────────────────

    /// <summary>CanvasStackComponent（縦・横に並べる）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasStackSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasStackComponentType);
        var section = BuildSection("縦・横に並べる");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("並べる", "enabled", true));
        body.Children.Add(rows.Choice("向き", "direction", "vertical", LayoutDirectionOptions));
        body.Children.Add(rows.Number("間隔", "spacing", 0f));
        rows.Padding(body);
        body.Children.Add(rows.Choice("主軸の揃え", "main_align", "start", MainAlignOptions));
        body.Children.Add(rows.Choice("交差軸の揃え", "cross_align", "start", CrossAlignOptions));
        body.Children.Add(rows.Check("逆順", "reverse", false));
        rows.FitAndHidden(body);
        body.Children.Add(CanvasLayoutHint(
            "子（フォルダの中の子も）の位置はコンテナが決めます（子の Anchor・Position は使いません）。"
            + "子の大きさは自分の大きさ（Canvas → 最初の Sprite → Text の枠 → Canvas Layout Item の指定）か、"
            + "Canvas Layout Item の「伸ばす重み」と交差軸の「いっぱいに伸ばす」で決まります。伸ばした子の Sprite は矩形いっぱいに描かれます。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }

    /// <summary>CanvasWrapComponent（折り返して並べる）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasWrapSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasWrapComponentType);
        var section = BuildSection("折り返して並べる");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("並べる", "enabled", true));
        body.Children.Add(rows.Choice("向き", "direction", "horizontal", LayoutDirectionOptions));
        body.Children.Add(rows.Number("子の間隔", "spacing", 0f));
        body.Children.Add(rows.Number("行の間隔", "run_spacing", 0f));
        rows.Padding(body);
        body.Children.Add(rows.Choice("行の中の揃え", "main_align", "start", MainAlignOptions));
        body.Children.Add(rows.Choice("交差軸の揃え", "cross_align", "start", CrossAlignOptions));
        body.Children.Add(rows.Choice("行の塊の揃え", "run_align", "start", MainAlignOptions));
        rows.FitAndHidden(body);
        body.Children.Add(CanvasLayoutHint(
            "子を自分の大きさのまま並べ、領域の幅（縦なら高さ）に収まらなくなったら次の行へ折り返します。"
            + "領域の幅が決まらない（CanvasComponent が無い・幅を中身に）ときは折り返しません。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }

    /// <summary>CanvasGridComponent（格子に並べる）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasGridSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasGridComponentType);
        var section = BuildSection("格子に並べる");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("並べる", "enabled", true));
        const int DefaultColumns = 3;
        var columns = (int)LayoutNumber(rows.Values, "columns", DefaultColumns);
        body.Children.Add(rows.Count("列数（0=自動）", "columns", DefaultColumns));
        // セルの最小幅は自動の列数（列数 0）のときだけ使う（無関係な欄は出さない）
        if (columns == 0)
            body.Children.Add(rows.Number("セルの最小幅", "cell_min_width", 100f));
        body.Children.Add(rows.Number("縦横比（幅÷高さ）", "cell_aspect_ratio", 1f, CanvasLayoutRatioFormat));
        body.Children.Add(rows.Number("列の間隔", "spacing_x", 0f));
        body.Children.Add(rows.Number("行の間隔", "spacing_y", 0f));
        rows.Padding(body);
        body.Children.Add(rows.Choice("セルの中の置き方", "cell_align", "stretch", CrossAlignOptions));
        rows.FitAndHidden(body);
        body.Children.Add(CanvasLayoutHint(
            "子を左上から行優先でセルへ入れます。セルの幅は領域の幅を列数で等分、高さはセルの幅 ÷ 縦横比"
            + "（縦横比 0 なら行ごとに中身の最大の高さ）。「いっぱいに伸ばす」と子の Sprite はセルいっぱいに描かれます。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }

    /// <summary>CanvasLayoutItemComponent（子の側の指定）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasLayoutItemSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasLayoutItemComponentType);
        var section = BuildSection("レイアウトの子の指定");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("コンテナに無視させる", "ignore_layout", false));
        body.Children.Add(rows.Number("伸ばす重み", "flex", 0f, CanvasLayoutRatioFormat));
        body.Children.Add(rows.Number("幅の指定（0=中身）", "preferred_width", 0f));
        body.Children.Add(rows.Number("高さの指定（0=中身）", "preferred_height", 0f));
        body.Children.Add(rows.Number("幅の下限", "min_width", 0f));
        body.Children.Add(rows.Number("高さの下限", "min_height", 0f));
        body.Children.Add(rows.Number("幅の上限（0=なし）", "max_width", 0f));
        body.Children.Add(rows.Number("高さの上限（0=なし）", "max_height", 0f));
        body.Children.Add(rows.Choice("揃えの上書き", "align_self", "auto", ItemAlignOptions));
        body.Children.Add(rows.Check("親の幅に合わせる", "fill_width", false));
        body.Children.Add(rows.Check("親の高さに合わせる", "fill_height", false));
        body.Children.Add(CanvasLayoutHint(
            "コンテナ（Stack・Wrap・Grid）の子に付けます。伸ばす重みは Stack の主軸の余りを重みで分けます。"
            + "「親に合わせる」はコンテナの外の子で使い、親の CanvasComponent の領域いっぱいに広げます（Sprite も広がります）。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }

    /// <summary>CanvasSafeAreaComponent（安全領域）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasSafeAreaSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasSafeAreaComponentType);
        var section = BuildSection("安全領域");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("縮める", "enabled", true));
        body.Children.Add(rows.Check("左", "left", true));
        body.Children.Add(rows.Check("上", "top", true));
        body.Children.Add(rows.Check("右", "right", true));
        body.Children.Add(rows.Check("下", "bottom", true));
        body.Children.Add(CanvasLayoutHint(
            "このノードのキャンバス領域（CanvasComponent が要ります）を、画面の安全領域（切り欠き・ステータスバー・"
            + "ジェスチャーバーを避けた内側）へ辺ごとに縮めます。子のアンカー・コンテナは縮めた領域を基準にします。"
            + "画面の回転・システムバーの出し入れに追従します。PC の Play とエディタでは画面全体（縮めません）。"
            + "Sprite の大きさは変わらないので、画面の端まで塗る背景は親に置いてください。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }

    /// <summary>
    /// CanvasComponent の寸法の単位（px / dp）の選択（ビューポート所属のルートキャンバスだけに出す。W2-1b）。
    /// </summary>
    private UIElement BuildCanvasUnitSection(SlotInfo info)
    {
        var sp = new StackPanel();
        sp.Children.Add(BuildLayoutChoiceRow("寸法の単位", info.CanvasUnit, CanvasUnitOptions, unit =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_CANVAS_UNIT:{_currentActorId},{info.SlotIdx},{unit}");
        }));
        sp.Children.Add(CanvasLayoutHint(
            "dp にすると、このキャンバスの大きさは「画面 ÷ 1 dp の画素数」になり、子の位置・大きさ・余白は縦横同じ倍率で"
            + "画素へ換算されます（自動スケールは使いません）。1 dp = 端末の DPI ÷ 基準 DPI の画素（Android は densityDpi ÷ 160、"
            + "PC は OS の表示スケール）。エディタでは 1 dp = 1 px（設計解像度がそのまま dp の大きさ）。"));
        return sp;
    }
}
