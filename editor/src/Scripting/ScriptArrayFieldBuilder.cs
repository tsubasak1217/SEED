using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;
using static SEEDEditor.Scripting.ScriptFieldWidgets;

namespace SEEDEditor.Scripting;

/// <summary>
/// 配列フィールド（<c>T[]</c> / <c>List&lt;T&gt;</c>）1 件分のインスペクタ UI を組み立てる。
///
/// 【構成】
///   ▼ フィールド名 (件数)          ← 折りたたみ（Expander。状態は ExpandStateStore が保持）
///       [0] &lt;要素エディタ&gt; [×]
///       [1] &lt;要素エディタ&gt; [×]
///       [＋ 要素を追加]
///
/// 【値の書き戻し】
/// 要素の編集・追加・削除のいずれでも、その場で全要素から JSON 配列文字列を組み直し、
/// 非配列フィールドとまったく同じ経路（onChange → SET_SCRIPT_FIELD）で 1 本の文字列として送る。
/// そのためランタイム側の Undo・シリアライズ・ホットリロード引き継ぎに追加実装なしで乗る。
///
/// 【要素エディタ】
/// 要素 1 個の文字列表現は非配列フィールドと同一（<see cref="SEED.ScriptArray"/> の規約）なので、
/// 型別の入力部品（<see cref="ScriptFieldWidgets"/>／参照ピッカー）をそのまま再利用する。
/// </summary>
internal static class ScriptArrayFieldBuilder
{
    // ── レイアウト定数 ───────────────────────────────────────

    /// <summary>要素番号（[0] [1] …）を表示する列の幅（px）。</summary>
    private const double IndexColumnWidth = 34;

    /// <summary>要素行・追加ボタンの左インデント（px）。折りたたみ配下であることを示す。</summary>
    private const double ElementIndent = 8;

    /// <summary>削除（×）・追加（＋）ボタンのアイコン一辺サイズ（px）。</summary>
    private const double RowButtonIconSize = 10;

    /// <summary>削除・追加ボタンの内側余白（px）。</summary>
    private static readonly Thickness RowButtonPadding = new(5, 1, 5, 1);

    /// <summary>参照要素が未設定のときにピッカーへ出す文言。</summary>
    private const string ReferenceUnsetText = "(未設定)";

    /// <summary>文字列要素がドロップを受け付けるファイル拡張子（アクタープレハブ）。</summary>
    private static readonly string[] ActorPathExtensions = [".actor"];

    // ── 配色（削除・追加ボタン） ─────────────────────────────
    private static readonly SolidColorBrush BrushButtonBg     = new(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly SolidColorBrush BrushButtonBorder = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushRemove       = new(Color.FromRgb(0xCC, 0x77, 0x77));
    private static readonly SolidColorBrush BrushAdd          = new(Color.FromRgb(0x77, 0xCC, 0x88));
    private static readonly SolidColorBrush BrushIndex        = new(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly SolidColorBrush BrushEmptyNote    = new(Color.FromRgb(0x66, 0x66, 0x66));

    /// <summary>
    /// 配列フィールド 1 件分の UI を生成する。
    /// </summary>
    /// <param name="field">フィールド情報（<see cref="ScriptFieldInfo.Array"/> が非 null であること）。</param>
    /// <param name="path">フィールドのドットパス（SET_SCRIPT_FIELD のキー）。</param>
    /// <param name="rawValue">
    /// 現在のシリアライズ値（JSON 配列文字列）。null のときは宣言時初期値を使う。
    /// </param>
    /// <param name="onChange">値変更の通知（JSON 配列文字列をそのまま渡す）。</param>
    /// <param name="onRefDrop">参照要素のドロップ解決役。null なら参照要素は表示のみ。</param>
    /// <param name="assetPathToVirtual">
    /// ドロップされた絶対パスを保存用パス（assets:// 仮想パス）へ変換する関数。
    /// null のとき文字列要素へのファイルドロップは受け付けない。
    /// </param>
    /// <param name="expandStates">折りたたみ状態のストア（UI 再構築をまたいで復元する）。</param>
    /// <param name="expandKey">このフィールドの折りたたみ状態キー。</param>
    public static UIElement Build(
        ScriptFieldInfo         field,
        string                  path,
        string?                 rawValue,
        Action<string>          onChange,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual,
        ExpandStateStore?       expandStates,
        string                  expandKey)
    {
        var arrayInfo = field.Array!.Value;

        // 現在値: 保存値が無ければ宣言時初期値（実配列）を JSON 化して初期表示にする
        var json = rawValue ?? SEED.ScriptArray.EncodeValue(field.DefaultValue, arrayInfo.ElementType);
        var elements = new List<string>(SEED.ScriptArray.Decode(json));

        // 要素の並びを行 UI として作り直すためのコンテナ（追加・削除のたびに中身を作り替える）
        var rows = new StackPanel { Margin = new Thickness(ElementIndent, 2, 0, 2) };

        var expander = new Expander
        {
            IsExpanded = true,
            Foreground = BrushText,
            FontSize   = RowFontSize,
            Margin     = new Thickness(0, 2, 0, 2),
            Content    = rows,
            ToolTip    = BuildTooltip(field, arrayInfo),
        };

        // 値を確定して送る（要素 → JSON 配列文字列）。ヘッダの件数表示もここで更新する。
        void Commit()
        {
            expander.Header = $"{field.Label} ({elements.Count})";
            onChange(SEED.ScriptArray.Encode(elements, arrayInfo.ElementKind));
        }

        // 行 UI を現在の要素リストから作り直す（追加・削除の直後に呼ぶ）
        void Rebuild()
        {
            rows.Children.Clear();
            expander.Header = $"{field.Label} ({elements.Count})";

            if (elements.Count == 0)
                rows.Children.Add(MakeEmptyNote());

            for (int i = 0; i < elements.Count; i++)
            {
                // ラムダへ捕捉する添字はループ変数と別変数にする（キャプチャ事故防止）
                var index = i;
                rows.Children.Add(BuildElementRow(
                    arrayInfo, index, elements[index],
                    // 要素の値が変わった: リストを更新して送り直す（行の作り直しは不要）
                    text =>
                    {
                        if (index >= elements.Count) return;
                        if (elements[index] == text) return;   // 無変化なら IPC を送らない
                        elements[index] = text;
                        Commit();
                    },
                    // 要素の削除: リストから外して行を作り直してから送る
                    () =>
                    {
                        if (index >= elements.Count) return;
                        elements.RemoveAt(index);
                        Rebuild();
                        Commit();
                    },
                    onRefDrop, assetPathToVirtual));
            }

            rows.Children.Add(MakeAddButton(arrayInfo, () =>
            {
                elements.Add(SEED.ScriptArray.DefaultElementValue(arrayInfo.ElementType));
                Rebuild();
                Commit();
            }));
        }

        Rebuild();
        expandStates?.Track(expander, expandKey, defaultExpanded: true);
        return expander;
    }

    /// <summary>折りたたみヘッダのツールチップ（フィールド説明＋要素型の案内）を作る。</summary>
    private static string BuildTooltip(ScriptFieldInfo field, ScriptArrayFieldInfo arrayInfo)
    {
        var kindText = arrayInfo.ElementReference is not null
            ? $"{arrayInfo.ElementReference.Value.Kind} への参照"
            : arrayInfo.ElementType.Name;
        var head = field.Tooltip is null ? "" : field.Tooltip + "\n";
        return $"{head}要素型: {kindText}\n[＋] で末尾に追加、[×] でその要素を削除します";
    }

    /// <summary>要素が 0 個のときに出す案内行。</summary>
    private static UIElement MakeEmptyNote() => new TextBlock
    {
        Text       = "(要素なし)",
        Foreground = BrushEmptyNote,
        FontSize   = RowFontSize,
        FontStyle  = FontStyles.Italic,
        Margin     = new Thickness(2, 1, 0, 1),
    };

    // ── 要素行 ───────────────────────────────────────────────

    /// <summary>
    /// 要素 1 個分の行（[番号] エディタ [×]）を作る。
    /// </summary>
    private static UIElement BuildElementRow(
        ScriptArrayFieldInfo    arrayInfo,
        int                     index,
        string                  value,
        Action<string>          onElementChanged,
        Action                  onRemove,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual)
    {
        var (editor, prefix) = BuildElementEditor(arrayInfo, value, onElementChanged, onRefDrop, assetPathToVirtual);

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndexColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // ドラッグハンドル
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 削除ボタン

        var indexLabel = new TextBlock
        {
            Text              = $"[{index}]",
            Foreground        = BrushIndex,
            FontSize          = RowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(indexLabel, 0);
        grid.Children.Add(indexLabel);

        if (prefix is not null)
        {
            Grid.SetColumn(prefix, 1);
            grid.Children.Add(prefix);
        }

        Grid.SetColumn(editor, 2);
        grid.Children.Add(editor);

        var removeBtn = MakeIconButton("Icon.Close", BrushRemove, $"{index} 番目の要素を削除する", onRemove);
        Grid.SetColumn(removeBtn, 3);
        grid.Children.Add(removeBtn);

        return grid;
    }

    /// <summary>
    /// 要素型に応じた入力部品を作る。
    /// 戻り値の prefix は数値ドラッグハンドル（数値要素のみ。他は null）。
    /// </summary>
    private static (UIElement editor, UIElement? prefix) BuildElementEditor(
        ScriptArrayFieldInfo    arrayInfo,
        string                  value,
        Action<string>          onChanged,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual)
    {
        // 参照要素: 共通の参照ピッカー（D&D・✕・ジャンプ）へ委譲する
        if (arrayInfo.ElementReference is { } refKind)
        {
            SEED.ScriptReference.TryParse(value, out var actorName, out var slotName);
            var spec = new ReferenceFieldSpec
            {
                Kind         = refKind.Kind,
                WantSlotName = ReferenceKindCatalog.NeedsSlotSelection(refKind.Kind),
                UnsetText    = ReferenceUnsetText,
                ExtraTooltip = refKind.IsNullable
                    ? "未設定のときスクリプトからは null になります"
                    : "未設定のときスクリプトからは IsValid == false のハンドルになります",
            };
            var picker = ReferencePicker.Create(
                spec,
                string.IsNullOrEmpty(actorName) ? null : actorName,
                slotName,
                (actor, slot) => onChanged(actor is null
                    ? SEED.ScriptReference.UnsetValue
                    : SEED.ScriptReference.Format(actor, slot)),
                onRefDrop);
            return (picker.Element, null);
        }

        var elementType = arrayInfo.ElementType;

        // 真偽値要素: チェックボックス
        if (elementType == typeof(bool))
        {
            var cb = new CheckBox
            {
                IsChecked         = value == "true",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
            };
            cb.Checked   += (_, _) => onChanged("true");
            cb.Unchecked += (_, _) => onChanged("false");
            return (cb, null);
        }

        // 実数要素: テキストボックス＋横ドラッグ
        if (elementType == typeof(float) || elementType == typeof(double))
        {
            var v = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) ? pv : 0f;
            var tb = MakeTextBox(Fmt(v));
            var drag = MakeDragLabel(tb, DragSpeedFloat, onChanged, isInt: false);
            tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitFloat(tb, onChanged); e.Handled = true; } };
            tb.LostFocus += (_, _) => CommitFloat(tb, onChanged);
            return (tb, drag);
        }

        // 整数要素: テキストボックス＋横ドラッグ
        if (elementType == typeof(int) || elementType == typeof(long) || elementType == typeof(short))
        {
            var v = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pv) ? pv : 0;
            var tb = MakeTextBox(v.ToString(CultureInfo.InvariantCulture));
            var drag = MakeDragLabel(tb, DragSpeedInt, onChanged, isInt: true);
            tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitInt(tb, onChanged); e.Handled = true; } };
            tb.LostFocus += (_, _) => CommitInt(tb, onChanged);
            return (tb, drag);
        }

        // 文字列要素: テキストボックス（＋ .actor ファイルのドロップでパスを流し込む）
        var text = MakeTextBox(value);
        text.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { onChanged(text.Text); e.Handled = true; } };
        text.LostFocus += (_, _) => onChanged(text.Text);
        if (assetPathToVirtual is not null)
            AttachActorPathDrop(text, assetPathToVirtual, onChanged);
        return (text, null);
    }

    /// <summary>
    /// 文字列要素のテキストボックスへ「.actor ファイルのドロップでパスを入れる」挙動を付ける。
    ///
    /// <c>GameObject.Instantiate(string actorPath)</c> に渡すプレハブパスの配列という
    /// 定番の使い方を想定した補助で、受け付けるのは Project パネル／エクスプローラーから
    /// ドラッグした単一の .actor ファイルのみ。保存する文字列は assets:// 仮想パスへ変換する
    /// （絶対パスのまま保存すると別マシンで解決できないため）。
    /// </summary>
    private static void AttachActorPathDrop(
        TextBox target, Func<string, string> toVirtualPath, Action<string> onChanged)
    {
        target.AllowDrop = true;

        // ドラッグ中: .actor 以外は拒否（カーソルで可否が分かる）
        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = SEEDEditor.Panels.FileRefBuilder.ExtractDroppedPath(e, ActorPathExtensions) is null
                ? DragDropEffects.None
                : DragDropEffects.Copy;
            e.Handled = true;
        }
        target.PreviewDragOver  += OnDragOver;
        target.PreviewDragEnter += OnDragOver;

        target.PreviewDrop += (_, e) =>
        {
            var dropped = SEEDEditor.Panels.FileRefBuilder.ExtractDroppedPath(e, ActorPathExtensions);
            if (dropped is null) return;

            // 絶対パスなら assets:// 仮想パスへ、既に仮想パスならそのまま
            var stored = Path.IsPathRooted(dropped) ? toVirtualPath(dropped) : dropped;
            target.Text = stored;
            onChanged(stored);
            e.Handled = true;
        };
    }

    // ── ボタン ───────────────────────────────────────────────

    /// <summary>末尾に要素を 1 つ足すボタン行を作る。</summary>
    private static UIElement MakeAddButton(ScriptArrayFieldInfo arrayInfo, Action onAdd)
    {
        var typeName = arrayInfo.ElementReference?.Kind ?? arrayInfo.ElementType.Name;
        var btn = MakeIconButton("Icon.Add", BrushAdd, $"末尾に {typeName} の要素を 1 つ追加する", onAdd);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Margin              = new Thickness(0, 3, 0, 1);
        return btn;
    }

    /// <summary>インスペクタ共通の見た目を持つ小さなアイコンボタンを作る。</summary>
    private static Button MakeIconButton(string iconKey, Brush iconBrush, string tooltip, Action onClick)
    {
        var icon = AppIcon.Create(iconKey, RowButtonIconSize);
        icon.SetBrush(iconBrush);

        var btn = new Button
        {
            Content           = icon,
            Background        = BrushButtonBg,
            BorderBrush       = BrushButtonBorder,
            BorderThickness   = new Thickness(1),
            Padding           = RowButtonPadding,
            Margin            = new Thickness(3, 0, 0, 0),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template          = SEEDEditor.Panels.FileRefBuilder.BuildButtonTemplate(),
            ToolTip           = tooltip,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
