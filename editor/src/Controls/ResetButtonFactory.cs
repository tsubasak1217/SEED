using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// インスペクタ行の右端へ添える「デフォルトに戻す」ボタン（⟲）の共通ファクトリ。
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
    /// <summary>ボタンに出す記号。巻き戻しを表す矢印 1 文字（行幅を圧迫しないよう文字のみ）。</summary>
    private const string Glyph = "⟲";

    /// <summary>ボタンの文字サイズ。</summary>
    private const double GlyphFontSize = 11;

    /// <summary>ボタン背景色（他の小型ボタンと同系統の暗いグレー）。</summary>
    private static readonly Color BackgroundColor = Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>ボタン文字色。</summary>
    private static readonly Color ForegroundColor = Color.FromRgb(0xBB, 0xBB, 0xBB);

    /// <summary>ボタン枠線色。</summary>
    private static readonly Color BorderColor = Color.FromRgb(0x44, 0x44, 0x44);

    /// <summary>ボタン内側の余白（左右 5・下 1 で記号が中央に見える値）。</summary>
    private static readonly Thickness ContentPadding = new(5, 0, 5, 1);

    /// <summary>行本体との間隔。</summary>
    private static readonly Thickness OuterMargin = new(4, 0, 0, 0);

    /// <summary>
    /// 「デフォルトに戻す」ボタンを 1 個生成する。
    /// </summary>
    /// <param name="tooltip">ボタンのツールチップ（何がどの値に戻るかを書く）。</param>
    /// <param name="onReset">押下時に呼ぶ処理（既定値の適用）。</param>
    public static Button Create(string tooltip, Action onReset)
    {
        var button = new Button
        {
            Content           = Glyph,
            Background        = new SolidColorBrush(BackgroundColor),
            Foreground        = new SolidColorBrush(ForegroundColor),
            BorderBrush       = new SolidColorBrush(BorderColor),
            BorderThickness   = new Thickness(1),
            FontSize          = GlyphFontSize,
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
