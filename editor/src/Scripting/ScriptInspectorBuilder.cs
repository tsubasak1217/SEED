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
    /// アクター行のドロップを受けて、参照フィールドの値を確定させる処理。
    ///
    /// インスペクタ側（IPC を持つ InspectorPanel）が実装する。ドロップされたアクターの
    /// コンポーネント一覧を取得し、種別に合うスロットを特定（複数あれば選択させる）してから
    /// <paramref name="apply"/> にシリアライズ値（"アクタ名" / "アクタ名|スロット名"）を渡す。
    /// </summary>
    /// <param name="field">対象の参照フィールド情報（種別の判定に使う）。</param>
    /// <param name="droppedActorDfsId">ドロップされたアクターの DFS ID。</param>
    /// <param name="apply">確定したシリアライズ値を適用するコールバック。</param>
    public delegate void ReferenceDropHandler(
        ScriptFieldInfo field, int droppedActorDfsId, Action<string> apply);

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
        ReferenceDropHandler?               onReferenceDropped = null,
        ExpandStateStore?                   expandStates       = null,
        string                              expandKeyPrefix    = "")
    {
        var stack = new StackPanel();
        BuildInto(stack, fields, currentValues, onValueChanged, onReferenceDropped, prefix: "",
                  expandStates: expandStates, expandKeyPrefix: expandKeyPrefix);
        return stack;
    }

    /// <summary>フィールド群を stack に構築する（prefix でドットパスを連結する再帰用）。</summary>
    private static void BuildInto(
        StackPanel                          stack,
        IReadOnlyList<ScriptFieldInfo>      fields,
        IReadOnlyDictionary<string, string> values,
        Action<string, string>              onChange,
        ReferenceDropHandler?               onRefDrop,
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

    private static UIElement? BuildRow(
        ScriptFieldInfo field, string path,
        IReadOnlyDictionary<string, string> values, Action<string, string> onChange,
        ReferenceDropHandler? onRefDrop)
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
        ReferenceDropHandler? onRefDrop,
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
    //   ・現在の参照先を表示（未設定は「(未設定)」）
    //   ・Hierarchy パネルからアクタ行を D&D して設定
    //   ・✕ ボタンで参照解除
    //   ・ダブルクリックで Hierarchy の該当アクタへジャンプ
    // 同種スロットを複数持つアクターをドロップした場合は、ドロップ解決側
    // （InspectorPanel）がスロット選択ダイアログを出してから値を確定させる。

    /// <summary>未設定時に参照ラベルへ表示する文言。</summary>
    private const string ReferenceUnsetText = "(未設定)";

    /// <summary>参照ドロップゾーンの枠線色（他の参照 UI と同系統の青）。</summary>
    private static readonly SolidColorBrush BrushRefBorder = new(Color.FromRgb(0x55, 0x77, 0x99));

    /// <summary>未設定表示に使う淡色。</summary>
    private static readonly SolidColorBrush BrushRefUnset = new(Color.FromRgb(0x77, 0x77, 0x77));

    /// <summary>参照解除ボタンの文字色（他の破棄系ボタンと同じ赤系）。</summary>
    private static readonly SolidColorBrush BrushRefClear = new(Color.FromRgb(0xAA, 0x55, 0x55));

    /// <summary>
    /// 参照フィールド 1 件分の行を生成する。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・ツールチップ用）。</param>
    /// <param name="refKind">参照種別（GameObject / コンポーネント種別と Nullable 可否）。</param>
    /// <param name="rawValue">現在のシリアライズ値（"アクタ名" / "アクタ名|スロット名" / 空）。</param>
    /// <param name="onChange">値変更の通知（シリアライズ値をそのまま渡す）。</param>
    /// <param name="onRefDrop">ドロップ解決ハンドラ。null ならドロップを受け付けない。</param>
    private static UIElement BuildReferenceRow(
        ScriptFieldInfo field, SEED.ScriptReference.ReferenceKind refKind,
        string? rawValue, Action<string> onChange, ReferenceDropHandler? onRefDrop)
    {
        // 現在の参照先表示（"アクタ名" または "アクタ名 / スロット名"）
        var display = SEED.ScriptReference.FormatDisplay(rawValue);
        var label = new TextBlock
        {
            Text              = display ?? ReferenceUnsetText,
            Foreground        = display is null ? BrushRefUnset : BrushText,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        // 現在のシリアライズ値（ドロップ・クリアで書き換わるのでローカルに保持する）
        var current = rawValue ?? SEED.ScriptReference.UnsetValue;

        // 表示とローカル値をまとめて更新するローカル関数
        void SetValue(string value)
        {
            current    = value;
            var disp   = SEED.ScriptReference.FormatDisplay(value);
            label.Text       = disp ?? ReferenceUnsetText;
            label.Foreground = disp is null ? BrushRefUnset : BrushText;
            onChange(value);
        }

        var kindLabel = ScriptReferenceCatalog.DisplayName(refKind.Kind);
        var drop = new Border
        {
            BorderBrush     = BrushRefBorder,
            BorderThickness = new Thickness(1),
            Background      = BrushBg,
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(5, 2, 5, 2),
            Margin          = new Thickness(2, 1, 0, 1),
            AllowDrop       = onRefDrop is not null,
            Child           = label,
            Cursor          = Cursors.Hand,
            ToolTip         = (field.Tooltip is null ? "" : field.Tooltip + "\n")
                            + $"参照型: {kindLabel}\n"
                            + "Hierarchy からアクタ行をドロップして参照を設定\n"
                            + "ダブルクリックで Hierarchy の参照先へジャンプ"
                            + (refKind.IsNullable
                                ? "\n未設定のときスクリプトからは null になります"
                                : "\n未設定のときスクリプトからは IsValid == false のハンドルになります"),
        };

        // ダブルクリックで Hierarchy の参照先アクタへジャンプする
        // （参照は張り替わるので、現在値から都度アクタ名を取り出す）
        SEEDEditor.Panels.ActorRefJump.AttachDoubleClickReveal(drop, () =>
            SEED.ScriptReference.TryParse(current, out var actorName, out _) ? actorName : null);

        if (onRefDrop is not null)
        {
            // HierarchyPanel は DoDragDrop を Move|Copy で呼ぶため、Move を返さないと
            // Effects の AND が None になりドロップが成立しない（VP ref 実装と同じ理由）。
            drop.DragOver += (_, e) =>
            {
                e.Effects = HasActorDragData(e) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            drop.Drop += (_, e) =>
            {
                var dfsId = TryGetDraggedActorDfsId(e);
                if (dfsId is null) return;
                onRefDrop(field, dfsId.Value, SetValue);
                e.Handled = true;
            };
        }

        // 参照解除ボタン
        var clear = new Button
        {
            Content         = "✕",
            FontSize        = 10,
            Width           = 20,
            Foreground      = BrushRefClear,
            Background      = BrushBg,
            BorderBrush     = BrushBorder,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(3, 1, 0, 1),
            ToolTip         = "参照を解除する",
        };
        clear.Click += (_, _) => SetValue(SEED.ScriptReference.UnsetValue);

        // ドロップゾーン（伸縮）と解除ボタン（固定幅）を横並びにする
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(drop, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(drop);
        grid.Children.Add(clear);

        return MakeRow(field.Label, field.Tooltip, null, grid);
    }

    /// <summary>ドラッグデータにアクター参照（Hierarchy / シーンビュー）が含まれるか。</summary>
    private static bool HasActorDragData(DragEventArgs e)
        => e.Data.GetDataPresent(HierarchyActorDfsIdFormat)
        || e.Data.GetDataPresent(SceneViewActorDfsIdFormat);

    /// <summary>ドラッグデータからアクターの DFS ID を取り出す（無ければ null）。</summary>
    private static int? TryGetDraggedActorDfsId(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(HierarchyActorDfsIdFormat))
            return e.Data.GetData(HierarchyActorDfsIdFormat) as int?;
        if (e.Data.GetDataPresent(SceneViewActorDfsIdFormat))
            return e.Data.GetData(SceneViewActorDfsIdFormat) as int?;
        return null;
    }

    /// <summary>Hierarchy パネルが単一アクタードラッグ時に付加する DFS ID のデータ形式名。</summary>
    private const string HierarchyActorDfsIdFormat = "HierarchyActorDfsId";

    /// <summary>シーンビューからのアクタードラッグに付加される DFS ID のデータ形式名。</summary>
    private const string SceneViewActorDfsIdFormat = "SceneViewActorDfsId";

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
