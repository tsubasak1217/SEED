using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// インスペクタ行の右端へ添える「デフォルトに戻す」ボタンの共通ファクトリ。
///
/// 同じ見た目・同じ操作感のボタンを複数の経路が必要とするため一箇所にまとめる。
///   ・水面シェーディングアセットの `@reset` 付きパラメータ（InspectorPanel）
///   ・スクリプトの [ResetButton] 付き [SerializeField]（ScriptInspectorBuilder）
///
/// ボタン自身は「何を既定値と見なすか」を知らない。押されたら渡されたコールバックを
/// 呼ぶだけで、値の解釈と送信（＝Undo への載せ方）は呼び出し側の責務とする。
/// </summary>
internal static class ResetButtonFactory
{
    /// <summary>ボタンに出すアイコン（巻き戻し）。</summary>
    private const string IconKey = "Icon.Reset";

    /// <summary>アイコンの一辺サイズ（px）。行幅を圧迫しない小型サイズ。</summary>
    private const double IconSize = 11;

    /// <summary>ボタン背景色（他の小型ボタンと同系統の暗いグレー）。</summary>
    private static readonly Color BackgroundColor = Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>ボタン文字色。</summary>
    private static readonly Color ForegroundColor = Color.FromRgb(0xBB, 0xBB, 0xBB);

    /// <summary>ボタン枠線色。</summary>
    private static readonly Color BorderColor = Color.FromRgb(0x44, 0x44, 0x44);

    /// <summary>ボタン内側の左右余白（px）。記号が中央に見える値。</summary>
    private const double ContentPaddingX = 5;

    /// <summary>ボタン内側の下余白（px）。</summary>
    private const double ContentPaddingBottom = 1;

    /// <summary>ボタン枠線の太さ（px）。</summary>
    private const double BorderThicknessPx = 1;

    /// <summary>行本体とボタンの間隔（px）。</summary>
    private const double OuterMarginLeft = 4;

    /// <summary>ボタン内側の余白（左右 5・下 1 で記号が中央に見える値）。</summary>
    private static readonly Thickness ContentPadding =
        new(ContentPaddingX, 0, ContentPaddingX, ContentPaddingBottom);

    /// <summary>行本体との間隔。</summary>
    private static readonly Thickness OuterMargin = new(OuterMarginLeft, 0, 0, 0);

    /// <summary>
    /// 行の右端でこのボタンが占める幅（px。左側の間隔を含む）。
    ///
    /// ⟲ ボタンを持たない行（複数行の TextBox など）へ同じ右余白を空けて、
    /// 入力欄の右端を他の行とそろえるために使う。
    /// 定数を各所へ書き写すとボタンのサイズ変更時にずれるため、ここから引くこと。
    /// </summary>
    internal const double ReservedRowWidth =
        IconSize + ContentPaddingX * 2 + BorderThicknessPx * 2 + OuterMarginLeft;

    /// <summary>
    /// 「デフォルトに戻す」ボタンを 1 個生成する。
    /// </summary>
    /// <param name="tooltip">ボタンのツールチップ（何がどの値に戻るかを書く）。</param>
    /// <param name="onReset">押下時に呼ぶ処理（既定値の適用）。</param>
    public static Button Create(string tooltip, Action onReset)
    {
        var button = new Button
        {
            Content           = AppIcon.Create(IconKey, IconSize),
            Background        = new SolidColorBrush(BackgroundColor),
            Foreground        = new SolidColorBrush(ForegroundColor),
            BorderBrush       = new SolidColorBrush(BorderColor),
            BorderThickness   = new Thickness(BorderThicknessPx),
            Padding           = ContentPadding,
            Margin            = OuterMargin,
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template          = SEEDEditor.Panels.FileRefBuilder.BuildButtonTemplate(),
            ToolTip           = tooltip,
        };
        button.Click += (_, _) => onReset();
        return button;
    }

    /// <summary>
    /// 既存の行要素の右端に「デフォルトに戻す」ボタンを添えた要素を返す。
    /// 行の形（Grid / StackPanel など）に依存しないよう DockPanel で包む。
    /// </summary>
    /// <param name="row">元の行要素（可変幅で残りを占める）。</param>
    /// <param name="tooltip">ボタンのツールチップ。</param>
    /// <param name="onReset">押下時に呼ぶ処理（既定値の適用）。</param>
    public static UIElement Wrap(UIElement row, string tooltip, Action onReset)
    {
        var dock = new DockPanel { LastChildFill = true };
        var button = Create(tooltip, onReset);
        // Dock された子を先に、可変幅の本体を最後に入れる（LastChildFill の作法）。
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(button);
        dock.Children.Add(row);
        return dock;
    }
}
