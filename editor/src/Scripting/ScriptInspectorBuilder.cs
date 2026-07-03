using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Scripting;

public static class ScriptInspectorBuilder
{
    private static readonly SolidColorBrush BrushLabel  = new(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush BrushText   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushBg     = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush BrushBorder = new(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x55, 0xAA, 0xFF));

    /// <summary>
    /// [SerializeField] フィールド一覧から WPF の StackPanel を生成する。
    /// onValueChanged: (fieldPath, newValueString)。ネストは "parent.child" のドットパス。
    /// </summary>
    public static StackPanel Build(
        IReadOnlyList<ScriptFieldInfo>      fields,
        IReadOnlyDictionary<string, string> currentValues,
        Action<string, string>              onValueChanged)
    {
        var stack = new StackPanel();
        BuildInto(stack, fields, currentValues, onValueChanged, prefix: "");
        return stack;
    }

    /// <summary>フィールド群を stack に構築する（prefix でドットパスを連結する再帰用）。</summary>
    private static void BuildInto(
        StackPanel                          stack,
        IReadOnlyList<ScriptFieldInfo>      fields,
        IReadOnlyDictionary<string, string> values,
        Action<string, string>              onChange,
        string                              prefix)
    {
        foreach (var field in fields)
        {
            // [Header] があれば見出しを前置する
            if (!string.IsNullOrEmpty(field.Header))
                stack.Children.Add(MakeHeader(field.Header!));

            var fullPath = prefix + field.Field.Name;

            // [Serializable] ネストクラス → 折りたたみで子フィールドを再帰表示
            if (field.Children is not null)
            {
                stack.Children.Add(BuildNestedFoldout(field, fullPath, values, onChange));
                continue;
            }

            var row = BuildRow(field, fullPath, values, onChange);
            if (row is not null) stack.Children.Add(row);
        }
    }

    private static UIElement? BuildRow(
        ScriptFieldInfo field, string path,
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange)
    {
        values.TryGetValue(path, out var raw);
        var t       = field.Field.FieldType;
        var onLeaf  = (Action<string>)(s => onChange(path, s));

        if (t == typeof(float) || t == typeof(double))
        {
            var v = raw is not null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)
                ? pv : Convert.ToSingle(field.DefaultValue ?? 0f);
            return field.RangeMin is float mn && field.RangeMax is float mx
                ? BuildRangeRow(field, v, mn, mx, isInt: false, onLeaf)
                : BuildFloatRow(field, v, onLeaf);
        }
        if (t == typeof(int) || t == typeof(long) || t == typeof(short))
        {
            var v = raw is not null && int.TryParse(raw, out var pv) ? pv : Convert.ToInt32(field.DefaultValue ?? 0);
            return field.RangeMin is float imn && field.RangeMax is float imx
                ? BuildRangeRow(field, v, imn, imx, isInt: true, onLeaf)
                : BuildIntRow(field, v, onLeaf);
        }
        if (t == typeof(bool))
        {
            var v = raw is not null ? raw == "true" : (bool)(field.DefaultValue ?? false);
            return BuildBoolRow(field, v, onLeaf);
        }
        if (t == typeof(string))
        {
            var v = raw ?? (string?)field.DefaultValue ?? "";
            return BuildStringRow(field, v, onLeaf);
        }
        return BuildReadOnlyRow(field);
    }

    /// <summary>[Serializable] ネストクラスを折りたたみ（Expander）で表示する。</summary>
    private static UIElement BuildNestedFoldout(
        ScriptFieldInfo field, string fullPath,
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange)
    {
        var inner = new StackPanel { Margin = new Thickness(10, 2, 0, 2) };
        // 子は "親パス." を prefix にして再帰構築する
        BuildInto(inner, field.Children!, values, onChange, prefix: fullPath + ".");

        var expander = new Expander
        {
            Header     = field.Label,
            IsExpanded = true,
            Foreground = BrushText,
            Margin     = new Thickness(0, 2, 0, 2),
            Content    = inner,
            ToolTip    = field.Tooltip,
        };
        return expander;
    }

    /// <summary>[Header] 見出しの TextBlock を生成する。</summary>
    private static UIElement MakeHeader(string text) => new TextBlock
    {
        Text       = text,
        Foreground = BrushText,
        FontWeight = FontWeights.Bold,
        FontSize   = 11,
        Margin     = new Thickness(0, 8, 0, 2),
    };

    // ── 型別ビルダー ─────────────────────────────────────────

    private static UIElement BuildFloatRow(ScriptFieldInfo field, float value, Action<string> onChange)
    {
        var tb   = MakeTextBox(Fmt(value));
        var drag = MakeDragLabel(tb, 0.1, onChange, isInt: false);
        tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitFloat(tb, onChange); e.Handled = true; } };
        tb.LostFocus += (_, _) => CommitFloat(tb, onChange);
        return MakeRow(field.Label, field.Tooltip, drag, tb);
    }

    private static UIElement BuildIntRow(ScriptFieldInfo field, int value, Action<string> onChange)
    {
        var tb   = MakeTextBox(value.ToString());
        var drag = MakeDragLabel(tb, 1.0, onChange, isInt: true);
        tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitInt(tb, onChange); e.Handled = true; } };
        tb.LostFocus += (_, _) => CommitInt(tb, onChange);
        return MakeRow(field.Label, field.Tooltip, drag, tb);
    }

    /// <summary>[Range] 付き数値フィールド: スライダー + 数値ボックスを表示する。</summary>
    private static UIElement BuildRangeRow(
        ScriptFieldInfo field, double value, float min, float max, bool isInt, Action<string> onChange)
    {
        // min > max の指定ミスに備えて入れ替える
        if (min > max) (min, max) = (max, min);
        value = Math.Clamp(value, min, max);

        var slider = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 4, 0),
            IsSnapToTickEnabled = isInt,
            TickFrequency     = isInt ? 1 : 0.0001,
        };
        var box = MakeTextBox(isInt ? ((int)Math.Round(value)).ToString() : Fmt((float)value));
        box.Width = 52;

        // 再入防止フラグ（スライダー↔ボックスの相互更新でループしないように）
        bool updating = false;

        slider.ValueChanged += (_, e) =>
        {
            if (updating) return;
            updating = true;
            var v = isInt ? Math.Round(e.NewValue) : e.NewValue;
            box.Text = isInt ? ((int)v).ToString() : Fmt((float)v);
            onChange(box.Text);
            updating = false;
        };

        void CommitBox()
        {
            if (updating) return;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            v = Math.Clamp(v, min, max);
            if (isInt) v = Math.Round(v);
            updating = true;
            box.Text      = isInt ? ((int)v).ToString() : Fmt((float)v);
            slider.Value  = v;
            onChange(box.Text);
            updating = false;
        }
        box.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitBox(); e.Handled = true; } };
        box.LostFocus += (_, _) => CommitBox();

        // スライダー（可変幅）と数値ボックス（固定幅）を横並びにする
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(slider);
        grid.Children.Add(box);

        return MakeRow(field.Label, field.Tooltip, null, grid);
    }

    private static UIElement BuildBoolRow(ScriptFieldInfo field, bool value, Action<string> onChange)
    {
        var cb = new CheckBox
        {
            IsChecked         = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 0, 0),
        };
        cb.Checked   += (_, _) => onChange("true");
        cb.Unchecked += (_, _) => onChange("false");
        return MakeRow(field.Label, field.Tooltip, null, cb);
    }

    private static UIElement BuildStringRow(ScriptFieldInfo field, string value, Action<string> onChange)
    {
        var tb = MakeTextBox(value);
        tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { onChange(tb.Text); e.Handled = true; } };
        tb.LostFocus += (_, _) => onChange(tb.Text);
        return MakeRow(field.Label, field.Tooltip, null, tb);
    }

    private static UIElement BuildReadOnlyRow(ScriptFieldInfo field)
    {
        var val = new TextBlock
        {
            Text              = $"({field.Field.FieldType.Name})",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize          = 11,
            FontStyle         = FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return MakeRow(field.Label, field.Tooltip, null, val);
    }

    // ── ウィジェット生成 ──────────────────────────────────────

    private static Grid MakeRow(string label, string? tooltip, UIElement? prefix, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        if (prefix is not null)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = BrushLabel,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            ToolTip           = tooltip,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

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

    private static TextBox MakeTextBox(string text) => new()
    {
        Text                     = text,
        Background               = BrushBg,
        Foreground               = BrushText,
        CaretBrush               = BrushText,
        BorderBrush              = BrushBorder,
        BorderThickness          = new Thickness(1),
        FontSize                 = 11,
        Padding                  = new Thickness(3, 1, 3, 1),
        Margin                   = new Thickness(2, 1, 0, 1),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static TextBlock MakeDragLabel(TextBox target, double speed, Action<string> onChange, bool isInt)
    {
        var label = new TextBlock
        {
            Text                = "≡",
            Foreground          = BrushAccent,
            FontSize            = 11,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor              = Cursors.SizeWE,
            Margin              = new Thickness(2, 0, 2, 0),
        };

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
            var spd    = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? speed * 0.1 : speed;
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

    private static void CommitFloat(TextBox tb, Action<string> onChange)
    {
        if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            tb.Text = Fmt(v);
            onChange(tb.Text);
        }
    }

    private static void CommitInt(TextBox tb, Action<string> onChange)
    {
        if (int.TryParse(tb.Text, out var v))
        {
            tb.Text = v.ToString();
            onChange(tb.Text);
        }
    }

    private static string Fmt(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
}
