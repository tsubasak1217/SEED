using System.Windows;
using System.Windows.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「ジェスチャーを受けるノード」（CanvasGestureComponent。W2-2）の実装。
///
/// ■ 対象
///   受けるジェスチャーの旗（タップ・長押し・ドラッグ・フリック）、ドラッグの軸、押下の見た目のイベントの有無、
///   ヒット領域の最小の大きさ（dp）。規則の正典は docs/input_gestures.md。
///
/// ■ 値の受け渡し
///   レイアウトの部品（InspectorPanel.CanvasLayout.cs）と同じ「キャンバス UI の純データの部品」として扱う:
///   ランタイムは値を serde の書式のまま ACTOR_COMPONENTS の "layout" に入れて送り、変更は
///   <c>SET_CANVAS_LAYOUT_FIELD:{actor},{slot},{key},{value}</c>（key は serde の欄の名前）で返す。
///   Undo はランタイム側の共通機構、既定値へのリセットは RESET_COMPONENT_FIELD（行の ⟲）。
///
/// ■ 条件付きの表示（選択に応じて無関係な欄は出さない）
///   ドラッグの軸はドラッグかフリックを受けるときだけ、押下の見た目はタップか長押しを受けるときだけ出す。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>ACTOR_COMPONENTS の "type" 文字列（ComponentCatalog と一致）。</summary>
    private const string CanvasGestureComponentType = "CanvasGestureComponent";

    /// <summary>ヒット領域の最小の大きさの既定（dp。Rust 側 DEFAULT_MIN_HIT_SIZE_DP と同じ）。</summary>
    private const float CanvasGestureDefaultMinHitSizeDp = 48f;

    /// <summary>ドラッグの軸（serde の名前, 表示名）。Rust 側 GestureDragAxis の snake_case と一致させる。</summary>
    private static readonly (string Value, string Label)[] GestureDragAxisOptions =
    {
        ("any", "全方向"),
        ("horizontal", "横だけ（縦の動きは親へ譲る）"),
        ("vertical", "縦だけ（横の動きは親へ譲る）"),
    };

    /// <summary>CanvasGestureComponent（ジェスチャーを受けるノード）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasGestureSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasGestureComponentType);
        var section = BuildSection("ジェスチャー");
        var body = (StackPanel)section.Child;
        body.Children.Add(rows.Check("受ける", "enabled", true));
        body.Children.Add(rows.Check("タップ", "tap", true));
        body.Children.Add(rows.Check("長押し", "long_press", false));
        body.Children.Add(rows.Check("ドラッグ", "drag", false));
        body.Children.Add(rows.Check("フリック", "fling", false));

        // 無関係な欄は出さない（Inspector の条件付き表示の方針）
        var wantsPress = LayoutBool(rows.Values, "tap", true) || LayoutBool(rows.Values, "long_press", false);
        var wantsDrag = LayoutBool(rows.Values, "drag", false) || LayoutBool(rows.Values, "fling", false);
        if (wantsDrag)
            body.Children.Add(rows.Choice("ドラッグの軸", "drag_axis", "any", GestureDragAxisOptions));
        if (wantsPress)
            body.Children.Add(rows.Check("押下の見た目", "press_feedback", true));
        body.Children.Add(rows.Number("最小のヒット領域（dp）", "min_hit_size_dp", CanvasGestureDefaultMinHitSizeDp));

        body.Children.Add(CanvasLayoutHint(
            "指ごとに、押した位置の子 → 親のノードが受けたいジェスチャーを競い、最初に成り立った 1 つが勝ちます"
            + "（ドラッグは 8 dp 動いたら、長押しは 500ms で、タップは離したとき）。勝ったドラッグは指を捕まえ、外へ出ても届きます。"
            + "スクリプトの OnGestureTap / OnGestureLongPress / OnGestureDrag* / OnGestureFling / OnGesturePress* で受けます。"));
        body.Children.Add(CanvasLayoutHint(
            "当たり判定の形は CanvasComponent があればキャンバス領域、無ければ最初の Sprite の矩形。見た目が最小のヒット領域より"
            + "小さければ中心をそろえて広げ、広げた領域が重なる所は見た目に近いノードが受けます。旗をすべて外すと、後ろのノードへ"
            + "指を渡さない「遮る板」になります。ポインタ判定（raycast_target）も持つノードは OnPointerDown / Up / Click を受けません。"));
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }
}
