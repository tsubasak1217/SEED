using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプトの [SerializeField] インスペクタ行を組み立てるための共通ウィジェット群。
///
/// スカラーフィールドの行（<see cref="ScriptInspectorBuilder"/>）と
/// 配列フィールドの要素行（<see cref="ScriptArrayFieldBuilder"/>）の双方から使うため、
/// 配色・テキストボックス・数値ドラッグハンドル・数値の書式といった
/// 「見た目と入力の基本部品」だけをここへ切り出している（UI の構成方針は各ビルダー側の責務）。
/// </summary>
internal static class ScriptFieldWidgets
{
    // ── 配色 ─────────────────────────────────────────────────
    public static readonly SolidColorBrush BrushLabel  = new(Color.FromRgb(0x88, 0x88, 0x88));
    public static readonly SolidColorBrush BrushText   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    public static readonly SolidColorBrush BrushBg     = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    public static readonly SolidColorBrush BrushBorder = new(Color.FromRgb(0x3F, 0x3F, 0x46));
    public static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x55, 0xAA, 0xFF));

    // ── レイアウト定数 ───────────────────────────────────────

    /// <summary>
    /// フィールド名ラベル列の初期幅（px）。
    /// 実際の幅は <see cref="ScriptLabelColumnGroup"/> がセクション内の全ラベルを見て
    /// 決め直す（ラベル優先で幅を配る）。ここはグループへ参加する前の暫定値。
    /// </summary>
    public const double LabelColumnWidth = 90;

    /// <summary>行内の共通フォントサイズ（px）。</summary>
    public const double RowFontSize = 11;

    /// <summary>数値ドラッグハンドルのアイコン一辺サイズ（px）。</summary>
    public const double DragHandleIconSize = 11;

    /// <summary>数値ドラッグの既定感度（1px あたりの増分。float 用）。</summary>
    public const double DragSpeedFloat = 0.1;

    /// <summary>数値ドラッグの既定感度（1px あたりの増分。int 用）。</summary>
    public const double DragSpeedInt = 1.0;

    /// <summary>Shift 併用時のドラッグ感度倍率（微調整用）。</summary>
    public const double DragSpeedFineScale = 0.1;

    /// <summary>float 値の表示書式（小数第 3 位まで）。</summary>
    public const string FloatFormat = "F3";

    // ── 行内アイコンボタン（配列要素・ScriptEvent 行で共有）─────

    /// <summary>行内ボタンの背景色。</summary>
    public static readonly SolidColorBrush BrushButtonBg = new(Color.FromRgb(0x2A, 0x2A, 0x2A));

    /// <summary>行内ボタンの枠線色。</summary>
    public static readonly SolidColorBrush BrushButtonBorder = new(Color.FromRgb(0x44, 0x44, 0x44));

    /// <summary>削除（×）・並び替え（∧∨）ボタンのアイコン一辺サイズ（px）。</summary>
    public const double RowButtonIconSize = 10;

    /// <summary>行内ボタン（削除・並び替え）の内側余白（px）。</summary>
    public static readonly Thickness RowButtonPadding = new(5, 1, 5, 1);

    /// <summary>押せないボタンの不透明度（0〜1）。押せないことを見た目で示す。</summary>
    public const double DisabledButtonOpacity = 0.3;

    /// <summary>
    /// インスペクタ共通の見た目を持つアイコンボタンを作る。
    ///
    /// 配列フィールド（<see cref="ScriptArrayFieldBuilder"/>）と ScriptEvent フィールド
    /// （<see cref="ScriptEventFieldBuilder"/>）の行操作ボタンで見た目・当たり判定を揃えるため、
    /// 生成をここへ 1 本化している。
    /// </summary>
    /// <param name="iconKey">ベクターアイコンのリソースキー（Icons.xaml）。</param>
    /// <param name="iconBrush">アイコンの色。</param>
    /// <param name="tooltip">ツールチップ文言。</param>
    /// <param name="onClick">押されたときの処理。</param>
    /// <param name="iconSize">アイコン一辺サイズ（px）。既定は行内ボタン用の小さいサイズ。</param>
    /// <param name="padding">内側余白。null なら行内ボタン用の既定値。</param>
    /// <param name="isEnabled">
    /// 押せるかどうか。false のときはクリックを受け付けず、
    /// 共通テンプレートに無効時の見た目が無いため不透明度で押せないことを示す。
    /// </param>
    public static Button MakeIconButton(
        string     iconKey,
        Brush      iconBrush,
        string     tooltip,
        Action     onClick,
        double     iconSize  = RowButtonIconSize,
        Thickness? padding   = null,
        bool       isEnabled = true)
    {
        var icon = SEEDEditor.Controls.AppIcon.Create(iconKey, iconSize);
        icon.SetBrush(iconBrush);

        var btn = new Button
        {
            Content           = icon,
            Background        = BrushButtonBg,
            BorderBrush       = BrushButtonBorder,
            BorderThickness   = new Thickness(1),
            Padding           = padding ?? RowButtonPadding,
            Margin            = new Thickness(3, 0, 0, 0),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template          = SEEDEditor.Panels.FileRefBuilder.BuildButtonTemplate(),
            ToolTip           = tooltip,
            IsEnabled         = isEnabled,
            Opacity           = isEnabled ? 1.0 : DisabledButtonOpacity,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ── 行レイアウト ─────────────────────────────────────────

    /// <summary>
    /// フィールド情報から「ラベル ＋ （任意の前置要素） ＋ コントロール」の 1 行を組む。
    /// ラベルには完全名と説明（[Tooltip] / <c>/// &lt;summary&gt;</c>）のツールチップが付く。
    /// </summary>
    public static Grid MakeRow(ScriptFieldInfo field, UIElement? prefix, UIElement control)
        => MakeRow(field.Label, ScriptFieldTooltip.Build(field), prefix, control);

    /// <summary>
    /// 「ラベル ＋ （任意の前置要素） ＋ コントロール」の 1 行を組む。
    /// </summary>
    /// <param name="label">ラベル文字列（列幅が足りなければ「…」で切り詰めて表示する）。</param>
    /// <param name="tooltip">ラベルのツールチップ内容（文字列でも UI 要素でも可。null なら付けない）。</param>
    /// <param name="prefix">ラベルと入力欄の間に挟む要素（数値ドラッグハンドルなど）。</param>
    /// <param name="control">入力欄本体。</param>
    public static Grid MakeRow(string label, object? tooltip, UIElement? prefix, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };

        // ラベル列は「内容に合わせて広げる」列。実際の幅は行の生成後に
        // ScriptLabelColumnGroup がセクション全体で揃えて決める（ラベル優先で配分）。
        // グループが見つからない場合の暴走防止として px 上限だけは常に掛けておく。
        var labelColumn = new ColumnDefinition
        {
            Width    = new GridLength(LabelColumnWidth),
            MaxWidth = ScriptLabelColumnGroup.MaxLabelWidth,
        };
        grid.ColumnDefinitions.Add(labelColumn);
        if (prefix is not null)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = BrushLabel,
            FontSize          = RowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            ToolTip           = tooltip,
        };
        if (tooltip is not null)
        {
            // 説明はやや長くなるので、出るまでは短く・出てからは長く表示する
            ToolTipService.SetInitialShowDelay(lbl, ScriptFieldTooltip.InitialShowDelayMs);
            ToolTipService.SetShowDuration(lbl, ScriptFieldTooltip.ShowDurationMs);
        }
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        // ラベルを「折り返さずに表示するのに必要な幅」を、まだツリーに載っていない
        // この時点で測っておく（レイアウト中の再測定を避けるため）。
        lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ScriptLabelColumnGroup.AttachRow(grid, labelColumn, lbl.DesiredSize.Width);

        var controlCol = 1;
        if (prefix is not null)
        {
            Grid.SetColumn(prefix, 1);
            grid.Children.Add(prefix);
            controlCol = 2;
        }
        Grid.SetColumn(control, controlCol);
        grid.Children.Add(control);

        return grid;
    }

    // ── 入力部品 ─────────────────────────────────────────────

    /// <summary>インスペクタ共通の見た目を持つテキストボックスを作る。</summary>
    public static TextBox MakeTextBox(string text) => new()
    {
        Text                     = text,
        Background               = BrushBg,
        Foreground               = BrushText,
        CaretBrush               = BrushText,
        BorderBrush              = BrushBorder,
        BorderThickness          = new Thickness(1),
        FontSize                 = RowFontSize,
        Padding                  = new Thickness(3, 1, 3, 1),
        Margin                   = new Thickness(2, 1, 0, 1),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 数値テキストボックスの左に添える「横ドラッグで値を増減するハンドル」を作る。
    /// ドラッグ中は 1px ごとに値を送るため、呼び出し側の onChange は連続発火に耐える必要がある
    /// （SET_SCRIPT_FIELD 経路はランタイム側で Undo がまとめられる）。
    /// </summary>
    public static SEEDEditor.Controls.AppIcon MakeDragLabel(
        TextBox target, double speed, Action<string> onChange, bool isInt)
    {
        var label = SEEDEditor.Controls.AppIcon.Create("Icon.DragHandle", DragHandleIconSize);
        label.SetBrush(BrushAccent);
        label.VerticalAlignment   = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Cursor              = Cursors.SizeWE;
        label.Margin              = new Thickness(2, 0, 2, 0);

        double originX = 0;
        float  originV = 0;

        label.MouseLeftButtonDown += (_, e) =>
        {
            if (float.TryParse(target.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                originX = e.GetPosition(null).X;
                originV = v;
                label.CaptureMouse();
            }
            e.Handled = true;
        };
        label.MouseMove += (_, e) =>
        {
            if (!label.IsMouseCaptured) return;
            var spd    = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? speed * DragSpeedFineScale : speed;
            var newVal = originV + (float)((e.GetPosition(null).X - originX) * spd);
            target.Text = isInt ? ((int)MathF.Round(newVal)).ToString() : Fmt(newVal);
            onChange(target.Text);
        };
        label.MouseLeftButtonUp += (_, e) =>
        {
            if (label.IsMouseCaptured) label.ReleaseMouseCapture();
            e.Handled = true;
        };

        return label;
    }

    /// <summary>テキストボックスの内容を float として確定し、正規化した書式で送る。</summary>
    public static void CommitFloat(TextBox tb, Action<string> onChange)
    {
        if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            tb.Text = Fmt(v);
            onChange(tb.Text);
        }
    }

    /// <summary>テキストボックスの内容を int として確定し、正規化した書式で送る。</summary>
    public static void CommitInt(TextBox tb, Action<string> onChange)
    {
        if (int.TryParse(tb.Text, out var v))
        {
            tb.Text = v.ToString();
            onChange(tb.Text);
        }
    }

    /// <summary>float 値をインスペクタ共通の書式（不変カルチャ）で文字列化する。</summary>
    public static string Fmt(float v) => v.ToString(FloatFormat, CultureInfo.InvariantCulture);
}
