using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;

namespace SEEDEditor.Scripting;

/// <summary>
/// ScriptFieldInfo の一覧から、インスペクタに表示する WPF コントロール群を動的生成する。
/// float/int/bool/string の基本型は編集可能な行を、[Range] 付きはスライダーを、
/// [Serializable] ネストクラスは折りたたみ Expander を、その他は読み取り専用表示を作る。
/// </summary>
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
    /// onReferenceDropped: 参照フィールドへのアクタードロップを解決するハンドラ
    /// （null の場合、参照フィールドは表示のみでドロップを受け付けない）。
    /// expandStates / expandKeyPrefix: [Serializable] ネストの折りたたみ状態を
    /// 呼び出し側（インスペクタ）が保持し、UI 再構築後に復元するためのストアとキー接頭辞。
    /// 値を 1 つ編集するたびにパネルが作り直される呼び出し元では必ず渡すこと
    /// （渡さないと編集の直後にネストが閉じてしまう）。
    /// </summary>
    public static StackPanel Build(
        IReadOnlyList<ScriptFieldInfo>      fields,
        IReadOnlyDictionary<string, string> currentValues,
        Action<string, string>              onValueChanged,
        IReferenceDropResolver?             referenceResolver  = null,
        ExpandStateStore?                   expandStates       = null,
        string                              expandKeyPrefix    = "")
    {
        var stack = new StackPanel();
        BuildInto(stack, fields, currentValues, onValueChanged, referenceResolver, prefix: "",
                  expandStates: expandStates, expandKeyPrefix: expandKeyPrefix);
        return stack;
    }

    /// <summary>フィールド群を stack に構築する（prefix でドットパスを連結する再帰用）。</summary>
    private static void BuildInto(
        StackPanel                          stack,
        IReadOnlyList<ScriptFieldInfo>      fields,
        IReadOnlyDictionary<string, string> values,
        Action<string, string>              onChange,
        IReferenceDropResolver?             onRefDrop,
        string                              prefix,
        ExpandStateStore?                   expandStates,
        string                              expandKeyPrefix)
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
                stack.Children.Add(BuildNestedFoldout(
                    field, fullPath, values, onChange, onRefDrop, expandStates, expandKeyPrefix));
                continue;
            }

            var row = BuildRow(field, fullPath, values, onChange, onRefDrop);
            if (row is not null) stack.Children.Add(row);
        }
    }

    /// <summary>
    /// フィールド 1 件分の行を作り、[ResetButton] が付いていれば右端に
    /// 「デフォルトに戻す」ボタンを添えて返す。
    ///
    /// リセットは通常の値編集とまったく同じ経路（onChange → SET_SCRIPT_FIELD）を通す。
    /// これだけでランタイム側の共通 Undo 機構（field_edit.rs）に自動で載るため、
    /// Ctrl+Z でリセット前の値へ戻せる。
    /// </summary>
    private static UIElement? BuildRow(
        ScriptFieldInfo field, string path,
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange,
        IReferenceDropResolver? onRefDrop)
    {
        var row = BuildRowCore(field, path, values, onChange, onRefDrop);
        if (row is null || !field.ShowResetButton) return row;

        // 既定値を文字列化できない型（列挙型など未対応の読み取り専用行）はボタンを出さない。
        var resetValue = FormatResetValue(field);
        if (resetValue is null) return row;

        return ResetButtonFactory.Wrap(
            row,
            $"「{field.Label}」を宣言時の既定値に戻す（Ctrl+Z で取り消せます）",
            () => onChange(path, resetValue));
    }

    /// <summary>
    /// [ResetButton] の戻り先の値を、SET_SCRIPT_FIELD へ流す文字列表現に変換する。
    ///
    /// 元の値は ScriptCompiler が「型のインスタンスを 1 個作って読んだフィールド値」
    /// （＝宣言の初期化子。初期化子が無ければ言語既定値 0 / false / null）である。
    /// 書式は各行ビルダーが解釈する形式と揃えること（数値は F3・bool は true/false）。
    /// 未対応型は null を返し、呼び出し側でボタン自体を出さない。
    /// </summary>
    private static string? FormatResetValue(ScriptFieldInfo field)
    {
        // 参照フィールドは型に依らず「未設定」へ戻す（✕ ボタンと同じ値）。
        if (field.Reference is not null) return SEED.ScriptReference.UnsetValue;

        var t = field.Field.FieldType;
        var d = field.DefaultValue;
        try
        {
            if (t == typeof(float) || t == typeof(double)) return Fmt(Convert.ToSingle(d ?? 0f));
            if (t == typeof(int) || t == typeof(long) || t == typeof(short))
                return Convert.ToInt64(d ?? 0L).ToString(CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return (bool)(d ?? false) ? "true" : "false";
            if (t == typeof(string))
            {
                var s = (string?)d ?? "";
                // IPC は 1 行 1 コマンドの行区切りなので、改行を含む既定値は送れない
                // （途中で別コマンドとして解釈されてしまう）。値を勝手に書き換えるより、
                // ボタンを出さない方が安全なのでリセット非対応として扱う。
                return s.Contains('\n') || s.Contains('\r') ? null : s;
            }
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            // 想定外の値が入っていた場合はボタンを出さない（誤った値を書き込まない安全側）。
            return null;
        }
        return null;
    }

    /// <summary>型に応じた行本体を生成する（リセットボタンは付けない）。</summary>
    private static UIElement? BuildRowCore(
        ScriptFieldInfo field, string path,
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange,
        IReferenceDropResolver? onRefDrop)
    {
        values.TryGetValue(path, out var raw);
        var t       = field.Field.FieldType;
        var onLeaf  = (Action<string>)(s => onChange(path, s));

        // 参照フィールド（GameObject / Transform / Camera …）は専用の行を作る
        if (field.Reference is { } refKind)
            return BuildReferenceRow(field, refKind, raw, onLeaf, onRefDrop);

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
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange,
        IReferenceDropResolver? onRefDrop,
        ExpandStateStore? expandStates, string expandKeyPrefix)
    {
        var inner = new StackPanel { Margin = new Thickness(10, 2, 0, 2) };
        // 子は "親パス." を prefix にして再帰構築する
        BuildInto(inner, field.Children!, values, onChange, onRefDrop, prefix: fullPath + ".",
                  expandStates: expandStates, expandKeyPrefix: expandKeyPrefix);

        var expander = new Expander
        {
            // 既定は「開」。ストアを渡された場合は初期値も含めてストア側が決める。
            IsExpanded = true,
            Header     = field.Label,
            Foreground = BrushText,
            Margin     = new Thickness(0, 2, 0, 2),
            Content    = inner,
            ToolTip    = field.Tooltip,
        };
        // キーはフィールドのドットパス（構造上の識別子。ラベル変更や値編集で変わらない）。
        expandStates?.Track(expander, expandKeyPrefix + fullPath, defaultExpanded: true);
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

    // ── 参照フィールド行 ─────────────────────────────────────
    //
    // 「アクター（＋コンポーネントスロット）への参照」を 1 行で編集する UI。
    // 見た目・操作・ドロップ解決はエディタ共通の参照ピッカー
    // （SEEDEditor.Controls.ReferencePicker）へ完全に委譲しており、
    // ここではスクリプト固有の「シリアライズ書式（"アクタ名|スロット名"）」と
    // 参照種別（ReferenceKind）を仕様（ReferenceFieldSpec）へ翻訳するだけを担う。

    /// <summary>未設定時に参照ラベルへ表示する文言。</summary>
    private const string ReferenceUnsetText = "(未設定)";

    /// <summary>
    /// 参照フィールド 1 件分の行を生成する。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・ツールチップ用）。</param>
    /// <param name="refKind">参照種別（GameObject / コンポーネント種別と Nullable 可否）。</param>
    /// <param name="rawValue">現在のシリアライズ値（"アクタ名" / "アクタ名|スロット名" / 空）。</param>
    /// <param name="onChange">値変更の通知（シリアライズ値をそのまま渡す）。</param>
    /// <param name="onRefDrop">ドロップ解決役。null ならドロップを受け付けない。</param>
    private static UIElement BuildReferenceRow(
        ScriptFieldInfo field, SEED.ScriptReference.ReferenceKind refKind,
        string? rawValue, Action<string> onChange, IReferenceDropResolver? onRefDrop)
    {
        // 保存書式（"アクタ名|スロット名"）を (アクタ名, スロット名) へ分解する
        string? actorName = null, slotName = null;
        if (SEED.ScriptReference.TryParse(rawValue ?? SEED.ScriptReference.UnsetValue,
                                          out var parsedActor, out var parsedSlot))
        {
            actorName = parsedActor;
            slotName  = parsedSlot;
        }

        var spec = new ReferenceFieldSpec
        {
            Kind         = refKind.Kind,
            // コンポーネント種別の参照だけがスロット名を保存できる
            // （GameObject / Transform 系はアクタ名のみ）。
            WantSlotName = ReferenceKindCatalog.NeedsSlotSelection(refKind.Kind),
            UnsetText    = ReferenceUnsetText,
            ExtraTooltip = (field.Tooltip is null ? "" : field.Tooltip + "\n")
                         + (refKind.IsNullable
                             ? "未設定のときスクリプトからは null になります"
                             : "未設定のときスクリプトからは IsValid == false のハンドルになります"),
        };

        var picker = ReferencePicker.Create(spec, actorName, slotName,
            (actor, slot) => onChange(actor is null
                ? SEED.ScriptReference.UnsetValue
                : SEED.ScriptReference.Format(actor, slot)),
            onRefDrop);

        return MakeRow(field.Label, field.Tooltip, null, picker.Element);
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
