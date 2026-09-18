// ============================================================
//  SeedButtonMetrics.cs — ボタンの寸法を 1 か所へ
//
//  【役割】
//  角の丸み・内側余白・フォーカス枠の太さなど、ボタンの寸法をまとめる。
//  色（SeedColorTable）と同じく「各画面が自前で決めない」ための単一の出所。
//
//  【なぜ C# 側に置くのか】
//  XAML のリソースとして Thickness / CornerRadius を宣言するには
//  型ごとに書き方が変わって読みにくく、数値が XAML に散る。
//  C# の定数にしておけば {x:Static} 1 つで参照でき、
//  コンパイラが綴り間違いを検出してくれる。
// ============================================================

using System.Windows;

namespace SEEDEditor.Theme;

/// <summary>
/// ボタン共通の寸法。値の変更はここだけで行う。
/// </summary>
public static class SeedButtonMetrics
{
    /// <summary>角の丸み [px]。エディタ内の他の角丸（バージョン管理パネル等）と揃えてある。</summary>
    public const double CORNER_RADIUS_PX = 2;

    /// <summary>通常ボタンの内側余白（左右）[px]。</summary>
    public const double PADDING_X_PX = 10;

    /// <summary>通常ボタンの内側余白（上下）[px]。</summary>
    public const double PADDING_Y_PX = 4;

    /// <summary>アイコン専用ボタンの内側余白（左右）[px]。アイコンは正方形に近く詰める。</summary>
    public const double ICON_PADDING_X_PX = 4;

    /// <summary>アイコン専用ボタンの内側余白（上下）[px]。</summary>
    public const double ICON_PADDING_Y_PX = 3;

    /// <summary>リンク風ボタンの内側余白（左右）[px]。文章の一部に見えるよう最小限にする。</summary>
    public const double LINK_PADDING_X_PX = 2;

    /// <summary>リンク風ボタンの内側余白（上下）[px]。</summary>
    public const double LINK_PADDING_Y_PX = 1;

    /// <summary>ダイアログの決定・取消ボタンの内側余白（左右）[px]。</summary>
    public const double DIALOG_PADDING_X_PX = 14;

    /// <summary>ダイアログの決定・取消ボタンの内側余白（上下）[px]。</summary>
    public const double DIALOG_PADDING_Y_PX = 5;

    /// <summary>
    /// ダイアログの決定・取消ボタンの最小幅 [px]。
    /// 「OK」のような短い文言でも押しやすい大きさを保ち、ボタン列の幅を揃える。
    /// </summary>
    public const double DIALOG_MIN_WIDTH_PX = 88;

    /// <summary>ダイアログのボタン同士の間隔 [px]。</summary>
    public const double DIALOG_BUTTON_GAP_PX = 8;

    /// <summary>枠線を出すボタン（スタート画面の大きなボタンなど）の枠の太さ [px]。</summary>
    public const double OUTLINED_BORDER_PX = 1;

    /// <summary>キーボードフォーカス枠の太さ [px]。</summary>
    public const double FOCUS_RING_PX = 1;

    /// <summary>フォーカス枠を本体の内側へ寄せる量 [px]（レイアウトに影響させないための重ね描き）。</summary>
    public const double FOCUS_RING_INSET_PX = 1;

    /// <summary>角の丸み。</summary>
    public static readonly CornerRadius Corner = new(CORNER_RADIUS_PX);

    /// <summary>通常ボタンの内側余白。</summary>
    public static readonly Thickness Padding =
        new(PADDING_X_PX, PADDING_Y_PX, PADDING_X_PX, PADDING_Y_PX);

    /// <summary>アイコン専用ボタンの内側余白。</summary>
    public static readonly Thickness IconPadding =
        new(ICON_PADDING_X_PX, ICON_PADDING_Y_PX, ICON_PADDING_X_PX, ICON_PADDING_Y_PX);

    /// <summary>リンク風ボタンの内側余白。</summary>
    public static readonly Thickness LinkPadding =
        new(LINK_PADDING_X_PX, LINK_PADDING_Y_PX, LINK_PADDING_X_PX, LINK_PADDING_Y_PX);

    /// <summary>ダイアログの決定・取消ボタンの内側余白。</summary>
    public static readonly Thickness DialogPadding =
        new(DIALOG_PADDING_X_PX, DIALOG_PADDING_Y_PX, DIALOG_PADDING_X_PX, DIALOG_PADDING_Y_PX);

    /// <summary>ボタン列で 2 個目以降のボタンに付ける左余白。</summary>
    public static readonly Thickness DialogButtonGap = new(DIALOG_BUTTON_GAP_PX, 0, 0, 0);

    /// <summary>枠線なし（既定）。</summary>
    public static readonly Thickness NoBorder = new(0);

    /// <summary>枠線あり。</summary>
    public static readonly Thickness OutlinedBorder = new(OUTLINED_BORDER_PX);

    /// <summary>フォーカス枠の太さ。</summary>
    public static readonly Thickness FocusRing = new(FOCUS_RING_PX);

    /// <summary>フォーカス枠の内寄せ量。</summary>
    public static readonly Thickness FocusRingInset = new(FOCUS_RING_INSET_PX);
}
