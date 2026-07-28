// ============================================================
//  SceneSettingsControls.cs — シーン設定ウィンドウの共通コントロール生成
//
//  シーン設定ウィンドウの右パネルに並べる「ラベル + 入力」行を組み立てる静的ファクトリ。
//  ウィンドウ本体（SceneSettingsWindow）はカテゴリ構成と IPC 通知に専念し、
//  見た目・入力ハンドリングの定型処理はすべてここへ集約する（単一責任の分離）。
//
//  各ビルダーは以下の共通契約を持つ:
//   - 値の読み書きは get/set デリガート経由で SceneSettingsData を直接触る
//   - ユーザー操作で値が変わったときのみ onChanged を呼ぶ
//     （プログラムからの表示更新中は host.Suppressed が true になり呼ばれない）
//   - 表示を現在値へ戻す処理を host.RegisterRefresh() へ登録する
// ============================================================

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.SceneSettings;

/// <summary>
/// コントロールファクトリが値の変更通知・表示更新のために必要とするウィンドウ側の機能。
/// SceneSettingsWindow が実装する。
/// </summary>
internal interface ISceneSettingsPanelHost
{
    /// <summary>プログラムから表示を更新中なら true（この間はユーザー操作扱いにしない）。</summary>
    bool Suppressed { get; }

    /// <summary>コントロールの表示を現在のデータ値へ戻す処理を登録する。</summary>
    void RegisterRefresh(Action refresh);

    /// <summary>数値入力に使うテキストボックスのスタイル（ウィンドウの Resources から供給）。</summary>
    Style? TextBoxStyle { get; }
}

/// <summary>
/// シーン設定ウィンドウ用の共通コントロールビルダー。
/// 配色・寸法はすべて名前付き定数で保持する（マジックナンバー禁止）。
/// </summary>
internal static class SceneSettingsControls
{
    // ── レイアウト定数 ────────────────────────────────────────

    /// <summary>行ラベル列の幅（px）。</summary>
    private const double LabelColumnWidth = 120;
    /// <summary>スライダー横の数値入力ボックスの幅（px）。</summary>
    private const double NumberBoxWidth = 60;
    /// <summary>各行の上下マージン（px）。</summary>
    private const double RowVerticalMargin = 3;
    /// <summary>コンボボックス・カラースウォッチの高さ（px）。</summary>
    private const double InputHeight = 24;
    /// <summary>カラースウォッチの幅（px）。</summary>
    private const double ColorSwatchWidth = 140;
    /// <summary>セクション見出しの上マージン（px）。</summary>
    private const double SectionTopMargin = 18;
    /// <summary>サブオプション（親チェックの子）のインデント幅（px）。</summary>
    public const double SubOptionIndent = 20;

    // ── 配色定数（プロジェクト設定ウィンドウと同系のダークテーマ）──

    private static readonly SolidColorBrush BrushLabel   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushHint    = new(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly SolidColorBrush BrushHeading = new(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly SolidColorBrush BrushRule    = new(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly SolidColorBrush BrushBorder  = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush BrushAxisX   = new(Color.FromRgb(0xE0, 0x6C, 0x75));
    private static readonly SolidColorBrush BrushAxisY   = new(Color.FromRgb(0x98, 0xC3, 0x79));
    private static readonly SolidColorBrush BrushAxisZ   = new(Color.FromRgb(0x61, 0xAF, 0xEF));
    private static readonly SolidColorBrush BrushAxisRot = new(Color.FromRgb(0xCC, 0xCC, 0xAA));

    // ── フォントサイズ定数 ────────────────────────────────────

    private const double FontSizeHeading = 15;
    private const double FontSizeBody    = 12;
    private const double FontSizeHint    = 11;
    private const double FontSizeAxis    = 9;

    /// <summary>整数表示のフォーマット（FOV / Far など）。</summary>
    private const string IntegerFormat = "0";
    /// <summary>小数表示のフォーマット（速度・各種強度）。</summary>
    private const string DecimalFormat = "0.##";

    // ── 見出し・区切り ────────────────────────────────────────

    /// <summary>パネル先頭に置くタイトル + 説明のヘッダーを生成する。</summary>
    public static UIElement PanelHeader(string title, string description)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = BrushHeading,
            FontSize   = FontSizeHeading + 1,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6),
        });
        header.Children.Add(new TextBlock
        {
            Text         = description,
            Foreground   = BrushHint,
            FontSize     = FontSizeHint,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new Border
        {
            Height     = 1,
            Background = BrushRule,
            Margin     = new Thickness(0, 10, 0, 0),
        });
        return header;
    }

    /// <summary>パネル内のセクション見出し（「ポストプロセス」など）を生成する。</summary>
    public static UIElement SectionHeader(string title)
        => new TextBlock
        {
            Text       = title,
            Foreground = BrushHeading,
            FontSize   = FontSizeHeading,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, SectionTopMargin, 0, 8),
        };

    /// <summary>補足説明テキスト（グレーの小さい注記）を生成する。</summary>
    public static UIElement HintText(string text, double leftIndent = 0)
        => new TextBlock
        {
            Text         = text,
            Foreground   = BrushHint,
            FontSize     = FontSizeHint,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(leftIndent, 2, 0, 6),
        };

    // ── チェックボックス ──────────────────────────────────────

    /// <summary>
    /// ON/OFF チェックボックス行を生成する。
    /// </summary>
    /// <param name="host">通知・表示更新の受け口。</param>
    /// <param name="label">チェックボックスのラベル。</param>
    /// <param name="tooltip">ツールチップ（不要なら null）。</param>
    /// <param name="get">現在値の取得。</param>
    /// <param name="set">値の反映先。</param>
    /// <param name="onChanged">ユーザー操作で値が変わったときの通知。</param>
    /// <param name="indent">左インデント（サブオプション用）。</param>
    public static CheckBox Check(
        ISceneSettingsPanelHost host, string label, string? tooltip,
        Func<bool> get, Action<bool> set, Action onChanged, double indent = 0)
    {
        var check = new CheckBox
        {
            Content   = label,
            IsChecked = get(),
            FontSize  = FontSizeBody,
            ToolTip   = tooltip,
            // WPF 既定の CheckBox 前景色は黒（SystemColors.ControlTextBrush）で、
            // ダーク背景（#1E1E1E）に同化して読めなくなる。行ラベルと同じ明色を明示する。
            Foreground = BrushLabel,
            Margin    = new Thickness(indent, RowVerticalMargin, 0, RowVerticalMargin),
        };

        void Handle(object _, RoutedEventArgs __)
        {
            if (host.Suppressed) return;
            set(check.IsChecked == true);
            onChanged();
        }
        check.Checked   += Handle;
        check.Unchecked += Handle;

        host.RegisterRefresh(() => check.IsChecked = get());
        return check;
    }

    // ── スライダー + 数値入力 ─────────────────────────────────

    /// <summary>
    /// 「ラベル + スライダー + 数値入力」の行を生成する。
    /// スライダーと入力ボックスは相互に同期し、確定時にのみ onChanged を呼ぶ。
    /// </summary>
    /// <param name="integer">true なら整数値として丸める（FOV / 描画距離）。</param>
    public static UIElement SliderRow(
        ISceneSettingsPanelHost host, string label,
        double min, double max, bool integer, string? tooltip,
        Func<double> get, Action<double> set, Action onChanged)
    {
        var grid = NewRowGrid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NumberBoxWidth) });
        if (tooltip != null) grid.ToolTip = tooltip;

        var labelBlock = NewLabel(label);
        var slider = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = Math.Clamp(get(), min, max),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
        };
        var box = new TextBox
        {
            Text                = Format(get(), integer),
            Style               = host.TextBoxStyle,
            TextAlignment       = TextAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        // スライダー操作 → 値を丸めてデータへ反映し、数値ボックス表示も更新する
        slider.ValueChanged += (_, e) =>
        {
            if (host.Suppressed) return;
            var value = integer ? Math.Round(e.NewValue) : e.NewValue;
            set(value);
            box.Text = Format(value, integer);
            onChanged();
        };

        // 数値ボックス確定（Enter / フォーカス喪失）→ 範囲内へクランプしてスライダーへ反映する。
        // スライダーの Value 変更が上のハンドラを通してデータ反映・通知まで行う。
        void CommitBox()
        {
            if (host.Suppressed) return;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                box.Text = Format(get(), integer);   // 不正入力は現在値へ戻す
                return;
            }
            var value = Math.Clamp(integer ? Math.Round(parsed) : parsed, min, max);
            if (Math.Abs(value - slider.Value) < double.Epsilon)
            {
                box.Text = Format(value, integer);   // 変化なし（表示の正規化のみ）
                return;
            }
            slider.Value = value;
        }
        box.LostFocus += (_, _) => CommitBox();
        box.KeyDown   += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitBox();
            e.Handled = true;
        };

        host.RegisterRefresh(() =>
        {
            slider.Value = Math.Clamp(get(), min, max);
            box.Text     = Format(get(), integer);
        });

        AddCell(grid, labelBlock, 0);
        AddCell(grid, slider,     1);
        AddCell(grid, box,        2);
        return grid;
    }

    // ── コンボボックス ────────────────────────────────────────

    /// <summary>
    /// 「ラベル + コンボボックス」の行を生成する。
    /// items の各要素は (表示ラベル, タグ文字列)。タグはそのまま設定値になる。
    /// </summary>
    /// <param name="combo">生成したコンボボックス（有効/無効の連動制御に使う）。</param>
    public static UIElement ComboRow(
        ISceneSettingsPanelHost host, string label,
        (string Label, string Tag)[] items, string? tooltip,
        Func<string> get, Action<string> set, Action onChanged,
        out ComboBox combo)
    {
        var grid = NewRowGrid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (tooltip != null) grid.ToolTip = tooltip;

        combo = new ComboBox
        {
            FontSize          = FontSizeHint,
            Height            = InputHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (itemLabel, tag) in items)
            combo.Items.Add(new ComboBoxItem { Content = itemLabel, Tag = tag });

        var localCombo = combo;
        SelectByTag(localCombo, get());

        localCombo.SelectionChanged += (_, _) =>
        {
            if (host.Suppressed) return;
            if ((localCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
            set(tag);
            onChanged();
        };

        host.RegisterRefresh(() => SelectByTag(localCombo, get()));

        AddCell(grid, NewLabel(label), 0);
        AddCell(grid, localCombo,      1);
        return grid;
    }

    /// <summary>ComboBox から Tag が一致する項目を選択する（見つからなければ無変更）。</summary>
    public static void SelectByTag(ComboBox combo, string? tag)
    {
        if (tag == null) return;
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && (item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    // ── カラースウォッチ ──────────────────────────────────────

    /// <summary>
    /// 「ラベル + カラースウォッチ」の行を生成する。クリックでカラーピッカーを開く。
    /// 色はリニア RGB で保持し、表示のみ sRGB へ変換する。
    /// </summary>
    public static UIElement ColorRow(
        ISceneSettingsPanelHost host, Window owner, string label, string? tooltip,
        Func<float[]> get, Action<float[]> set, Action onChanged)
    {
        var grid = NewRowGrid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (tooltip != null) grid.ToolTip = tooltip;

        var swatch = new Border
        {
            Height              = InputHeight,
            Width               = ColorSwatchWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush         = BrushBorder,
            BorderThickness     = new Thickness(1),
            Cursor              = Cursors.Hand,
        };

        void UpdateSwatch()
        {
            var color = get();
            swatch.Background = new SolidColorBrush(Color.FromRgb(
                LinearToSrgbByte(color[0]), LinearToSrgbByte(color[1]), LinearToSrgbByte(color[2])));
        }
        UpdateSwatch();

        swatch.MouseLeftButtonDown += (_, _) =>
        {
            var current = get();
            // 環境光はライト色と同様にアルファを持たない（a=1 固定）。入出力はリニア RGB。
            var result = ColorPickerWindow.ShowDialog(owner, current[0], current[1], current[2], 1f);
            if (result is null) return;
            var (r, g, b, _) = result.Value;
            set(new[] { r, g, b });
            UpdateSwatch();
            onChanged();
        };

        host.RegisterRefresh(UpdateSwatch);

        AddCell(grid, NewLabel(label), 0);
        AddCell(grid, swatch,          1);
        return grid;
    }

    /// <summary>リニア RGB 成分（0..1）を sRGB の 8bit 値へ変換する（スウォッチ表示用）。</summary>
    public static byte LinearToSrgbByte(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        // sRGB 伝達関数（IEC 61966-2-1）。ColorPickerWindow の LinearToSrgb と同一。
        const double linearCutoff = 0.0031308;
        const double linearScale  = 12.92;
        const double gammaScale   = 1.055;
        const double gammaOffset  = 0.055;
        const double gammaExp     = 1.0 / 2.4;
        const double byteMax      = 255.0;
        double s = c <= linearCutoff
            ? c * linearScale
            : gammaScale * Math.Pow(c, gammaExp) - gammaOffset;
        return (byte)Math.Clamp((int)Math.Round(s * byteMax), 0, (int)byteMax);
    }

    // ── XYZ 3 成分入力 ────────────────────────────────────────

    /// <summary>XYZ 3 成分入力行の軸ラベル色（位置用）。</summary>
    private static readonly SolidColorBrush[] PositionAxisBrushes = { BrushAxisX, BrushAxisY, BrushAxisZ };

    /// <summary>
    /// 「ラベル + X/Y/Z 数値入力」の行を生成する（カメラ位置・回転の手動入力）。
    /// Enter またはフォーカス喪失で 3 成分まとめて確定する。
    /// </summary>
    /// <param name="axisLabels">各成分の見出し（例 "X" / "X°"）。</param>
    /// <param name="rotationColor">true なら回転用の一律配色、false なら XYZ 個別配色。</param>
    public static UIElement Vector3Row(
        ISceneSettingsPanelHost host, string label, string[] axisLabels, bool rotationColor,
        Func<float[]> get, Action<float[]> set, Action onChanged)
    {
        var outer = new StackPanel { Margin = new Thickness(0, RowVerticalMargin, 0, RowVerticalMargin) };

        // 軸ラベル行（ラベル列の分だけ右へずらす）
        var labelRow = new Grid { Margin = new Thickness(LabelColumnWidth, 0, 0, 1) };
        for (int i = 0; i < axisLabels.Length; i++)
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < axisLabels.Length; i++)
        {
            var axis = new TextBlock
            {
                Text                = axisLabels[i],
                Foreground          = rotationColor ? BrushAxisRot : PositionAxisBrushes[i],
                FontSize            = FontSizeAxis,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            AddCell(labelRow, axis, i);
        }
        outer.Children.Add(labelRow);

        // 入力行
        var inputRow = NewRowGrid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        for (int i = 0; i < axisLabels.Length; i++)
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddCell(inputRow, NewLabel(label), 0);

        var boxes = new TextBox[axisLabels.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = new TextBox
            {
                Text          = FormatFloat(get()[i]),
                Style         = host.TextBoxStyle,
                TextAlignment = TextAlignment.Right,
                Margin        = new Thickness(1, 0, 1, 0),
            };
            AddCell(inputRow, boxes[i], i + 1);
        }
        outer.Children.Add(inputRow);

        // 3 成分すべてがパースできたときのみ確定する（半端な入力で送信しない）
        void Commit()
        {
            if (host.Suppressed) return;
            var values = new float[boxes.Length];
            for (int i = 0; i < boxes.Length; i++)
            {
                if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return;
            }
            set(values);
            onChanged();
        }

        foreach (var box in boxes)
        {
            box.LostFocus += (_, _) => Commit();
            box.KeyDown   += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Commit();
                e.Handled = true;
            };
        }

        host.RegisterRefresh(() =>
        {
            var values = get();
            for (int i = 0; i < boxes.Length; i++)
                boxes[i].Text = FormatFloat(values[i]);
        });

        return outer;
    }

    // ── デフォルトに戻すボタン ────────────────────────────────

    /// <summary>
    /// カテゴリパネル末尾の「デフォルトに戻す」ボタンを生成する。
    /// 押下時はそのカテゴリの設定だけを既定値へ戻す（他カテゴリには触れない）。
    /// </summary>
    public static UIElement ResetButton(Action onReset)
    {
        var panel = new StackPanel { Margin = new Thickness(0, SectionTopMargin, 0, 0) };
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = BrushRule,
            Margin     = new Thickness(0, 0, 0, 10),
        });

        var button = new Button
        {
            Content             = "デフォルトに戻す",
            FontSize            = FontSizeHint,
            Padding             = new Thickness(10, 5, 10, 5),
            Cursor              = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background          = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground          = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            BorderBrush         = BrushBorder,
            BorderThickness     = new Thickness(1),
            ToolTip             = "このカテゴリの設定だけを既定値へ戻します。",
        };
        button.Click += (_, _) => onReset();
        panel.Children.Add(button);
        return panel;
    }

    // ── 内部ヘルパー ──────────────────────────────────────────

    /// <summary>行用の Grid（共通マージン付き）を生成する。</summary>
    private static Grid NewRowGrid()
        => new() { Margin = new Thickness(0, RowVerticalMargin, 0, RowVerticalMargin) };

    /// <summary>行ラベルの TextBlock を生成する。</summary>
    private static TextBlock NewLabel(string text)
        => new()
        {
            Text              = text,
            Foreground        = BrushLabel,
            FontSize          = FontSizeBody,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>指定列へ要素を配置する。</summary>
    private static void AddCell(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>スライダー値の表示文字列を生成する。</summary>
    private static string Format(double value, bool integer)
        => value.ToString(integer ? IntegerFormat : DecimalFormat, CultureInfo.InvariantCulture);

    /// <summary>XYZ 入力の表示文字列を生成する（小数第 2 位まで）。</summary>
    private static string FormatFloat(float value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
