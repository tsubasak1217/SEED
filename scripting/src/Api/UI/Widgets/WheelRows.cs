using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  WheelRows.cs — ホイールの行（一覧の使い回しと、行ごとの曲面の見た目の当て方。W2-5。docs/ui_components.md §11）
//
//  【分担】行を作って使い回すのは W2-3 の ListView（見えている行だけをプレハブから作る）。ここは
//    - 行に文字を入れる（ListView の Bind から。行の番号 → 項目 → 文字は持ち主が決める）
//    - 毎フレーム、見えている行ごとに WheelLook（純関数）で見た目を求め、行の子 Label（Text）の位置・倍率・色と表示を当てる
//  行の根の位置は ListView が平らな並びの位置に置く（スクロールの位置でエンジンがずらす）。曲面の見た目は Label だけを
//  動かして作る（行の根に触らない＝ListView の置き方と使い回しを壊さない）。
//  書き込みは前に書いた値と比べて変わったものだけ（FFI の往復を減らす。止まっている行は何も書かない）。
//
//  【行のプレハブ】templates/ui/prefabs/wheel_row.actor
//      WheelRow（Actor2D。pivot 0・anchor 0。部品なし）
//      └─ Label（Text〈枠あり・中央揃え〉。pivot 0.5 = 枠の中心がノードの位置）
// ============================================================

/// <summary>行の文字の共通の見た目（テーマから。持ち主の ApplyLook のたびに作り直す）。</summary>
internal readonly struct WheelRowStyle
{
    /// <summary>文字の大きさ。</summary>
    public float FontSize { get; init; }
    /// <summary>選べる行の文字の色。</summary>
    public Color Enabled { get; init; }
    /// <summary>選べない行の文字の色。</summary>
    public Color Disabled { get; init; }
    /// <summary>部品全体の濃さ（無効の部品は opacity.disabled）。</summary>
    public float Fade { get; init; }
    /// <summary>書体（font.family。W2-9）。</summary>
    public string FontFamily { get; init; }
    /// <summary>文字の太さ（font.weight。W2-9）。</summary>
    public float FontWeight { get; init; }
}

/// <summary>ホイールの行（使い回しと見た目の当て方）。</summary>
internal sealed class WheelRows
{
    /// <summary>行の子の文字の名前。</summary>
    private const string LabelChild = "Label";
    /// <summary>半分。</summary>
    private const float Half = 0.5f;
    /// <summary>位置・倍率が変わったとみなす差（これより小さい変化は書かない）。</summary>
    private const float WriteEpsilon = 1e-3f;

    /// <summary>行 1 つの子の参照と、最後に書いた値。</summary>
    private sealed class RowState
    {
        /// <summary>文字のノード。</summary>
        public GameObject Label;
        /// <summary>文字。</summary>
        public Text? Text;
        /// <summary>文字のノードの位置と倍率。</summary>
        public CanvasTransform? Transform;
        /// <summary>最後に書いた表示（null = まだ書いていない）。</summary>
        public bool? Visible;
        /// <summary>最後に書いた位置。</summary>
        public Vector2 Position = new(float.NaN, float.NaN);
        /// <summary>最後に書いた倍率。</summary>
        public Vector2 Scale = new(float.NaN, float.NaN);
        /// <summary>最後に書いた色。</summary>
        public Color Color = new(float.NaN, float.NaN, float.NaN, float.NaN);
        /// <summary>最後に当てた文字の大きさ・枠の版（−1 = まだ）。</summary>
        public int StyleVersion = -1;

        public RowState(GameObject label) { Label = label; }
    }

    /// <summary>行の一覧（W2-3）。</summary>
    private readonly ListView _list;
    /// <summary>行の根のアクターの鍵 → 子の参照と最後に書いた値。</summary>
    private readonly Dictionary<(uint, uint), RowState> _states = new();
    /// <summary>行の番号 → 文字。</summary>
    private readonly Func<int, string> _labelOfRow;
    /// <summary>このフレームの一覧の更新で行に中身を入れたか。</summary>
    private bool _anyBound;
    /// <summary>文字の大きさ・枠の版（変わったら全部の行へ当て直す）。</summary>
    private int _styleVersion;

    /// <summary>作る。</summary>
    /// <param name="viewport">スクロールの窓（CanvasScroll を持つノード。行はその子に作る）。</param>
    /// <param name="rowPrefab">行のプレハブ（assets:// の .actor）。</param>
    /// <param name="labelOfRow">行の番号 → 文字。</param>
    public WheelRows(GameObject viewport, string rowPrefab, Func<int, string> labelOfRow)
    {
        _labelOfRow = labelOfRow;
        _list = new ListView(viewport, rowPrefab, 0, WheelLoop.MinItemExtent, Bind);
    }

    /// <summary>行の一覧（行の数・長さ・余白の設定）。</summary>
    public ListView List => _list;

    /// <summary>文字の大きさ・枠が変わった（次の当てで全部の行へ書き直す）。</summary>
    public void Restyle() => _styleVersion++;

    /// <summary>文字が変わった（付いている行へ次の更新で入れ直す）。</summary>
    public void Relabel() => _list.Refresh();

    /// <summary>一覧を更新する（見える行の割り当て・行の作成）。</summary>
    /// <returns>このフレームで行に中身を入れたなら true（見た目を当て直す合図）。</returns>
    public bool Update()
    {
        _anyBound = false;
        _list.Update();
        return _anyBound;
    }

    /// <summary>見えている行がすべて作られて付いているか（作っている途中の行があれば false）。</summary>
    public bool AllVisibleRowsBound()
    {
        var range = _list.VisibleRange;
        for (int index = range.First; index <= range.Last; index++)
            if (_list.RowOf(index) is null) return false;
        return true;
    }

    /// <summary>行へ中身を入れる（ListView の Bind から）。</summary>
    private void Bind(GameObject row, int index)
    {
        var st = StateOf(row);
        if (st.Text is { } text) text.Content = _labelOfRow(index);
        // 使い回した行は前の番号の見た目のままなので、次の当てで必ず書き直す
        st.Visible = null;
        st.Position = new Vector2(float.NaN, float.NaN);
        st.Scale = new Vector2(float.NaN, float.NaN);
        st.Color = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        _anyBound = true;
    }

    /// <summary>
    /// 見えている行へ曲面の見た目を当てる。
    /// </summary>
    /// <param name="position">スクロールの位置（行 r が中央に来るのは r × 行の高さ）。</param>
    /// <param name="viewport">窓の高さ。</param>
    /// <param name="extent">行の高さ。</param>
    /// <param name="width">列の幅（文字の枠の幅・中心の x）。</param>
    /// <param name="look">曲面の見た目の値。</param>
    /// <param name="style">文字の共通の見た目。</param>
    /// <param name="enabledOfRow">行の番号 → 選べるか。</param>
    public void Apply(float position, float viewport, float extent, float width, in WheelLookParams look,
        in WheelRowStyle style, Func<int, bool> enabledOfRow)
    {
        var range = _list.VisibleRange;
        for (int index = range.First; index <= range.Last; index++)
        {
            if (_list.RowOf(index) is not { } row) continue;
            var st = StateOf(row);
            if (st.Text is not { } text || st.Transform is not { } ct) continue;
            if (st.StyleVersion != _styleVersion)
            {
                text.FontSize = style.FontSize;
                // 書体・太さ（W2-9。同じ値は書かない）
                string family = style.FontFamily ?? string.Empty;
                if (text.FontPath != family) text.FontPath = family;
                if (text.Weight != style.FontWeight) text.Weight = style.FontWeight;
                text.BoxWidth = width;
                text.BoxHeight = extent;
                st.StyleVersion = _styleVersion;
            }
            // 行の中心と窓の中心の平らな距離（先頭の余白 =（窓 − 行）/ 2 なので 行 × 行の高さ − 位置）
            float distance = index * extent - position;
            var r = WheelLook.Resolve(distance, viewport, extent, look);
            if (st.Visible != r.Visible)
            {
                st.Label.Visible = r.Visible;
                st.Visible = r.Visible;
            }
            if (!r.Visible) continue;
            // 文字の中心を「映る位置」へ（行の根は平らな位置にあるので、差だけ動かす）
            var pos = new Vector2(width * Half, extent * Half + r.Offset - distance);
            if (Differs(st.Position, pos))
            {
                ct.Position = pos;
                st.Position = pos;
            }
            var scale = new Vector2(r.ScaleX * r.Magnify, r.ScaleY * r.Magnify);
            if (Differs(st.Scale, scale))
            {
                ct.Scale = scale;
                st.Scale = scale;
            }
            var baseColor = enabledOfRow(index) ? style.Enabled : style.Disabled;
            var color = UiColorMath.FadeAlpha(baseColor, r.Opacity * style.Fade);
            if (!SameColor(st.Color, color))
            {
                text.Color = color;
                st.Color = color;
            }
        }
    }

    /// <summary>行の子の参照（初めての行なら引いて覚える）。</summary>
    private RowState StateOf(GameObject row)
    {
        var key = (row.Entity.Index, row.Entity.Generation);
        if (_states.TryGetValue(key, out var st)) return st;
        var label = row.FindChild(LabelChild);
        st = new RowState(label)
        {
            Text = label.GetComponent<Text>(),
            Transform = label.GetComponent<CanvasTransform>(),
        };
        _states[key] = st;
        return st;
    }

    /// <summary>2 つの値が書き直すほど違うか（前の値が NaN = まだ書いていないなら違う）。</summary>
    private static bool Differs(Vector2 previous, Vector2 next)
        => float.IsNaN(previous.x) || MathF.Abs(previous.x - next.x) > WriteEpsilon || MathF.Abs(previous.y - next.y) > WriteEpsilon;

    /// <summary>2 つの色が同じか（前の値が NaN なら違う）。</summary>
    private static bool SameColor(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
}
