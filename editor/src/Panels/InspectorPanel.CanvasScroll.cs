using System.Windows;
using System.Windows.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「スクロールの領域」（CanvasScrollComponent。W2-3）の実装。
///
/// ■ 対象
///   中身（子）をずらして見せるスクロールの窓の設定一式: 向き（縦・横・両方）、端の振る舞い（跳ね返る・止める）、
///   慣性の有無、スナップ（なし・ページ送り・間隔）、入れ子での親への譲渡、中身の大きさの決め方（自動・固定）、
///   見える範囲の外を飛ばす設定、Clamp の摩擦・Bounce の減衰。規則の正典は docs/ui_scroll_list.md。
///
/// ■ 値の受け渡し
///   レイアウトの部品（InspectorPanel.CanvasLayout.cs）・ジェスチャー（InspectorPanel.CanvasGesture.cs）と同じ
///   「キャンバス UI の純データの部品」として扱う: ランタイムは値を serde の書式のまま ACTOR_COMPONENTS の
///   "layout" に入れて送り、変更は <c>SET_CANVAS_LAYOUT_FIELD:{actor},{slot},{key},{value}</c>（key は serde の
///   欄の名前。列挙は snake_case・bool は true/false・数値はそのまま）で返す。
///   Undo はランタイム側の共通機構、既定値へのリセットは RESET_COMPONENT_FIELD（行の ⟲）。
///
/// ■ 条件付きの表示（選択に応じて無関係な欄は出さない。CanvasGrid・CanvasGesture と同じ方針）
///   スナップの長さは snap が none のときは出さない。中身の幅・高さは content_size が fixed のときだけ出す。
///   飛ばす判定の余白は cull_outside が有効なときだけ出す。Clamp の摩擦は edge が clamp、Bounce の減衰は
///   edge が bounce のときだけ出す（両方同時に成り立つことは無い＝端の振る舞いは排他の選択肢のため）。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>ACTOR_COMPONENTS の "type" 文字列（ComponentCatalog と一致）。</summary>
    private const string CanvasScrollComponentType = "CanvasScrollComponent";

    /// <summary>見える範囲の外として飛ばす判定の余白の既定（Rust 側 DEFAULT_CACHE_EXTENT と同じ）。</summary>
    private const float CanvasScrollDefaultCacheExtent = 250f;

    /// <summary>Clamp（端で止める）の慣性の摩擦の既定（Rust 側 DEFAULT_FLING_FRICTION と同じ）。</summary>
    private const float CanvasScrollDefaultFlingFriction = 0.015f;

    /// <summary>Bounce（跳ね返り）の慣性の減衰の既定（Rust 側 DEFAULT_BOUNCE_DRAG と同じ）。</summary>
    private const float CanvasScrollDefaultBounceDrag = 0.135f;

    /// <summary>
    /// 摩擦・減衰など小さい物性値の表示書式（他コンポーネントの物性値行と同じ桁数。
    /// 既定の CanvasLayoutNumberFormat="F1" では 0.015 が "0.0" に潰れて見えるため個別に持つ）。
    /// </summary>
    private const string CanvasScrollPhysicsNumberFormat = "F3";

    /// <summary>スクロールの向き（serde の名前, 表示名）。Rust 側 ScrollDirection の snake_case と一致させる。</summary>
    private static readonly (string Value, string Label)[] ScrollDirectionOptions =
    {
        ("vertical", "縦（一覧）"),
        ("horizontal", "横（帯・ページ送り）"),
        ("both", "縦と横の両方"),
    };

    /// <summary>端での振る舞い（serde の名前, 表示名）。Rust 側 ScrollEdge の snake_case と一致させる。</summary>
    private static readonly (string Value, string Label)[] ScrollEdgeOptions =
    {
        ("bounce", "跳ね返る（端を越えて引っぱれる）"),
        ("clamp", "止める（端で止まる）"),
    };

    /// <summary>指を離したときのスナップ（serde の名前, 表示名）。Rust 側 ScrollSnap の snake_case と一致させる。</summary>
    private static readonly (string Value, string Label)[] ScrollSnapOptions =
    {
        ("none", "なし（慣性のまま止まる）"),
        ("page", "ページ送り（1 回で最大 1 ページ）"),
        ("interval", "間隔（一定間隔に吸着）"),
    };

    /// <summary>中身の大きさの決め方（serde の名前, 表示名）。Rust 側 ScrollContentSize の snake_case と一致させる。</summary>
    private static readonly (string Value, string Label)[] ScrollContentSizeOptions =
    {
        ("auto", "子から測る"),
        ("fixed", "固定（数値で指定）"),
    };

    /// <summary>CanvasScrollComponent（スクロールの領域）のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCanvasScrollSlotContent(SlotInfo info)
    {
        var rows = new CanvasLayoutRows(this, info, CanvasScrollComponentType);
        var section = BuildSection("スクロールの領域");
        var body = (StackPanel)section.Child;

        body.Children.Add(rows.Check("スクロールする", "enabled", true));
        body.Children.Add(rows.Choice("向き", "direction", "vertical", ScrollDirectionOptions));
        body.Children.Add(rows.Choice("端", "edge", "bounce", ScrollEdgeOptions));
        body.Children.Add(rows.Check("慣性", "inertia", true));
        body.Children.Add(rows.Choice("スナップ", "snap", "none", ScrollSnapOptions));

        // 無関係な欄は出さない（Inspector の条件付き表示の方針。CanvasGrid・CanvasGesture と同じ）
        var snap = LayoutString(rows.Values, "snap", "none");
        var contentSize = LayoutString(rows.Values, "content_size", "auto");
        var edge = LayoutString(rows.Values, "edge", "bounce");
        var cullOutside = LayoutBool(rows.Values, "cull_outside", true);

        // スナップの長さ: スナップしない（none）ときは無関係
        if (snap != "none")
            body.Children.Add(rows.Number("スナップの長さ（Page で 0 は窓の長さ・Interval で 0 はスナップしない）", "snap_interval", 0f));

        body.Children.Add(rows.Check("入れ子: 端で外側へ渡す", "hand_off_to_parent", true));
        body.Children.Add(rows.Choice("中身の大きさ", "content_size", "auto", ScrollContentSizeOptions));

        // 中身の幅・高さ: 固定（fixed）のときだけ数値を持つ
        if (contentSize == "fixed")
        {
            body.Children.Add(rows.Number("中身の幅", "content_width", 0f));
            body.Children.Add(rows.Number("中身の高さ", "content_height", 0f));
        }

        body.Children.Add(rows.Check("見える範囲の外を飛ばす", "cull_outside", true));

        // 飛ばす判定の余白: cull_outside が有効なときだけ効く
        if (cullOutside)
            body.Children.Add(rows.Number("飛ばす判定の余白", "cache_extent", CanvasScrollDefaultCacheExtent));

        // 端の振る舞いに応じた物性値: Clamp と Bounce は排他の選択肢なので同時には出ない
        if (edge == "clamp")
            body.Children.Add(rows.Number("慣性の摩擦（Clamp）", "fling_friction",
                CanvasScrollDefaultFlingFriction, CanvasScrollPhysicsNumberFormat));
        if (edge == "bounce")
            body.Children.Add(rows.Number("慣性の減衰（Bounce）", "bounce_drag",
                CanvasScrollDefaultBounceDrag, CanvasScrollPhysicsNumberFormat));

        body.Children.Add(CanvasLayoutHint(
            "2D キャンバスのノードに付けると、そのノードが「中身をずらして見せる窓」になります。指のドラッグ（軸はスクロールの"
            + "向き）で位置を動かし、離すとフリックの速度で慣性、端では跳ね返る（ばね）か止まります。ページ・間隔のスナップ、"
            + "入れ子（同じ向きの外側のスクロールへ端で残りのドラッグ・フリックを渡す）もできます。窓の大きさは CanvasComponent が"
            + "あればキャンバスの箱、無ければ最初の Sprite の矩形。スクリプトの OnScrollStart / OnScroll / OnScrollEnd で受けます。"));
        body.Children.Add(CanvasLayoutHint(
            "CanvasClip と一緒に付けると、窓の外の子を描画と当たり判定から外します（「見える範囲の外を飛ばす」はその判定に使う"
            + "余白）。中身の大きさは既定で子から測りますが、一覧の仮想化など見えている行だけを置きたいときは「固定」で数値を"
            + "指定します。スクリプトからは SEED.CanvasScroll の Position / JumpTo / ScrollTo で位置を操作できます。"));

        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(section);
        return sp;
    }
}
