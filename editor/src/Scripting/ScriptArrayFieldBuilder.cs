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
///       [0] &lt;要素エディタ&gt; [上へ][下へ][削除]
///       [1] &lt;要素エディタ&gt; [上へ][下へ][削除]
///       [          追加          ]   ← 行いっぱいの大きめボタン
/// ボタンはすべてベクターアイコン（Icon.MoveUp / Icon.MoveDown / Icon.Close / Icon.Add）。
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

    /// <summary>削除（×）・並び替え（∧∨）ボタンのアイコン一辺サイズ（px）。</summary>
    private const double RowButtonIconSize = 10;

    /// <summary>行内ボタン（削除・並び替え）の内側余白（px）。</summary>
    private static readonly Thickness RowButtonPadding = new(5, 1, 5, 1);

    /// <summary>並び替えボタンの内側余白（px）。上下 2 個並ぶので横幅を詰めておく。</summary>
    private static readonly Thickness MoveButtonPadding = new(3, 1, 3, 1);

    /// <summary>端の要素で押せない並び替えボタンの不透明度（0〜1）。押せないことを見た目で示す。</summary>
    private const double DisabledButtonOpacity = 0.3;

    /// <summary>追加（＋）ボタンのアイコン一辺サイズ（px）。行内ボタンより明確に大きくする。</summary>
    private const double AddButtonIconSize = 16;

    /// <summary>追加ボタンの高さ（px）。押しやすさ優先で行 UI より厚くする。</summary>
    private const double AddButtonHeight = 26;

    /// <summary>追加ボタンの内側余白（px）。</summary>
    private static readonly Thickness AddButtonPadding = new(6, 2, 6, 2);

    /// <summary>追加ボタンの外側余白（px）。要素行との間を少し空ける。</summary>
    private static readonly Thickness AddButtonMargin = new(0, 5, 0, 2);

    /// <summary>参照要素が未設定のときにピッカーへ出す文言。</summary>
    private const string ReferenceUnsetText = "(未設定)";

    /// <summary>文字列要素がドロップを受け付けるファイル拡張子（アクタープレハブ）。</summary>
    private static readonly string[] ActorPathExtensions = [".actor"];

    // ── 配色（削除・追加ボタン） ─────────────────────────────
    private static readonly SolidColorBrush BrushButtonBg     = new(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly SolidColorBrush BrushButtonBorder = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushRemove       = new(Color.FromRgb(0xCC, 0x77, 0x77));
    private static readonly SolidColorBrush BrushMove         = new(Color.FromRgb(0x99, 0x99, 0x99));
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

        // 要素 1 個の文字列表現は、スカラ・参照なら素の値、構造体なら JSON オブジェクト。
        // どちらも「文字列 1 本のリスト」として同じ手順（追加・削除・束ね直し）で扱える。
        var isStruct = arrayInfo.StructMembers is not null;
        var elements = new List<string>(isStruct
            ? SEED.ScriptStructArray.DecodeObjects(json)
            : SEED.ScriptArray.Decode(json));

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

        // 要素 2 個の位置を入れ替える（並び替えボタン用）。
        // 値そのものを入れ替えるので、書き戻しは通常の編集とまったく同じ経路に乗る。
        void Swap(int a, int b)
        {
            if (a < 0 || b < 0 || a >= elements.Count || b >= elements.Count || a == b) return;
            (elements[a], elements[b]) = (elements[b], elements[a]);

            // 構造体要素は「配列キー + 添字」で開閉状態を覚えているため、
            // 値と一緒に状態も入れ替えて開閉が要素に追従するようにする（入れ子メンバごと移動）。
            if (isStruct)
                expandStates?.SwapSubtree(
                    $"{expandKey}{StructElementKeySeparator}{a}",
                    $"{expandKey}{StructElementKeySeparator}{b}",
                    ScriptStructElementBuilder.MemberKeySeparator);

            Rebuild();
            Commit();
        }

        // 行 UI を現在の要素リストから作り直す（追加・削除・並び替えの直後に呼ぶ）
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
                    // 1 つ上／下の要素と入れ替える（端の要素では該当ボタンを無効化する）
                    () => Swap(index, index - 1),
                    () => Swap(index, index + 1),
                    canMoveUp:   index > 0,
                    canMoveDown: index < elements.Count - 1,
                    onRefDrop, assetPathToVirtual,
                    expandStates, expandKey));
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
        return $"{head}要素型: {kindText}\n"
             + "追加ボタンで末尾に追加、各行の削除ボタンでその要素を削除、上下ボタンで並び替えます";
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
    /// 要素 1 個分の行（[番号] エディタ [∧][∨][×]）を作る。
    /// </summary>
    /// <param name="onMoveUp">1 つ上の要素と入れ替える処理。</param>
    /// <param name="onMoveDown">1 つ下の要素と入れ替える処理。</param>
    /// <param name="canMoveUp">上へ動かせるか（先頭要素なら false）。</param>
    /// <param name="canMoveDown">下へ動かせるか（末尾要素なら false）。</param>
    private static UIElement BuildElementRow(
        ScriptArrayFieldInfo    arrayInfo,
        int                     index,
        string                  value,
        Action<string>          onElementChanged,
        Action                  onRemove,
        Action                  onMoveUp,
        Action                  onMoveDown,
        bool                    canMoveUp,
        bool                    canMoveDown,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual,
        ExpandStateStore?       expandStates,
        string                  expandKey)
    {
        // 構造体要素は 1 行に収まらないので、折りたたみグループ（[i] + メンバ行）にする
        if (arrayInfo.StructMembers is not null)
            return BuildStructElementGroup(
                arrayInfo, index, value, onElementChanged, onRemove,
                onMoveUp, onMoveDown, canMoveUp, canMoveDown,
                onRefDrop, assetPathToVirtual, expandStates, expandKey);

        var (editor, prefix) = BuildElementEditor(arrayInfo, value, onElementChanged, onRefDrop, assetPathToVirtual);

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndexColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // ドラッグハンドル
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 並び替え＋削除ボタン

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

        var actions = MakeRowActions(index, onRemove, onMoveUp, onMoveDown, canMoveUp, canMoveDown);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>
    /// 要素行の右端に並べる操作ボタン群（[∧][∨][×]）を作る。
    /// 1 次元配列でも構造体配列でも並びを揃えるため、生成をここへ 1 本化している。
    /// </summary>
    private static FrameworkElement MakeRowActions(
        int index, Action onRemove, Action onMoveUp, Action onMoveDown, bool canMoveUp, bool canMoveDown)
    {
        var panel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var up = MakeIconButton(
            "Icon.MoveUp", BrushMove, $"{index} 番目の要素を 1 つ上へ移動する", onMoveUp,
            RowButtonIconSize, MoveButtonPadding, canMoveUp);
        var down = MakeIconButton(
            "Icon.MoveDown", BrushMove, $"{index} 番目の要素を 1 つ下へ移動する", onMoveDown,
            RowButtonIconSize, MoveButtonPadding, canMoveDown);
        var remove = MakeIconButton(
            "Icon.Close", BrushRemove, $"{index} 番目の要素を削除する", onRemove);

        panel.Children.Add(up);
        panel.Children.Add(down);
        panel.Children.Add(remove);
        return panel;
    }

    /// <summary>
    /// 構造体要素 1 個分の折りたたみグループ（ヘッダ「[i]」＋ 右端の [×]、中身はメンバ行）を作る。
    ///
    /// メンバ行の構築は <see cref="ScriptStructElementBuilder"/> に任せる（単一責任）。
    /// 折りたたみ状態は「配列のキー + 要素番号」で記憶する。要素を削除すると
    /// 以降の番号がずれるが、開閉状態が 1 つずれるだけで値には影響しない。
    /// </summary>
    private static UIElement BuildStructElementGroup(
        ScriptArrayFieldInfo    arrayInfo,
        int                     index,
        string                  objectJson,
        Action<string>          onElementChanged,
        Action                  onRemove,
        Action                  onMoveUp,
        Action                  onMoveDown,
        bool                    canMoveUp,
        bool                    canMoveDown,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual,
        ExpandStateStore?       expandStates,
        string                  expandKey)
    {
        var elementKey = $"{expandKey}{StructElementKeySeparator}{index}";

        var members = ScriptStructElementBuilder.Build(
            arrayInfo, objectJson, onElementChanged,
            onRefDrop, assetPathToVirtual, expandStates, elementKey);

        var expander = new Expander
        {
            IsExpanded = true,
            Header     = $"[{index}]",
            Foreground = BrushText,
            FontSize   = RowFontSize,
            Margin     = new Thickness(0, 1, 0, 1),
            Content    = members,
            ToolTip    = $"{arrayInfo.ElementType.Name} の {index} 番目の要素",
        };
        expandStates?.Track(expander, elementKey, defaultExpanded: true);

        // 折りたたみ（可変幅）と削除ボタン（固定幅）を横並びにする
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(expander, 0);
        grid.Children.Add(expander);

        var actions = MakeRowActions(index, onRemove, onMoveUp, onMoveDown, canMoveUp, canMoveDown);
        // 中身が縦に伸びるので、操作ボタンはヘッダの高さに合わせて上端へ寄せる
        actions.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>要素の折りたたみ状態キーで「配列キー」と「要素番号」を繋ぐ区切り。</summary>
    private const string StructElementKeySeparator = "#";

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

    /// <summary>
    /// 末尾に要素を 1 つ足すボタン行を作る。
    /// 要素行の小さなボタンと違い「配列に対する主操作」なので、
    /// アイコンを大きく・高さを厚く・横幅を行いっぱいに広げて押しやすくする。
    /// </summary>
    private static UIElement MakeAddButton(ScriptArrayFieldInfo arrayInfo, Action onAdd)
    {
        var typeName = arrayInfo.ElementReference?.Kind ?? arrayInfo.ElementType.Name;
        var btn = MakeIconButton(
            "Icon.Add", BrushAdd, $"末尾に {typeName} の要素を 1 つ追加する", onAdd,
            AddButtonIconSize, AddButtonPadding);
        btn.HorizontalAlignment = HorizontalAlignment.Stretch;   // 行いっぱいに広げる
        btn.MinHeight           = AddButtonHeight;
        btn.Margin              = AddButtonMargin;
        return btn;
    }

    /// <summary>
    /// インスペクタ共通の見た目を持つアイコンボタンを作る。
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
    private static Button MakeIconButton(
        string     iconKey,
        Brush      iconBrush,
        string     tooltip,
        Action     onClick,
        double     iconSize  = RowButtonIconSize,
        Thickness? padding   = null,
        bool       isEnabled = true)
    {
        var icon = AppIcon.Create(iconKey, iconSize);
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
}
