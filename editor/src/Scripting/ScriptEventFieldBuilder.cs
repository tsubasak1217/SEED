using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SEEDEditor.Controls;
using static SEEDEditor.Scripting.ScriptFieldWidgets;

namespace SEEDEditor.Scripting;

/// <summary>
/// <c>SEED.ScriptEvent</c>（UnityEvent 相当）フィールド 1 件分のインスペクタ UI を組み立てる。
///
/// 【構成】
///   ▼ フィールド名 (件数)                                        ← 折りたたみ（ExpandStateStore が状態保持）
///       [0] [対象アクタ] [スクリプト型 ▼] [メソッド ▼] [引数] [∧][∨][×]
///       [1] …
///       [                    追加                    ]
///
/// 【値の書き戻し】
/// 行の追加・削除・並び替え・各項目の編集のいずれでも、その場で全バインディングから
/// JSON 配列文字列を組み直し、非配列フィールドとまったく同じ経路
/// （onChange → SET_SCRIPT_FIELD）で 1 本の文字列として送る。
/// エンコードは <see cref="SEED.ScriptEvent.Encode"/> の 1 経路しか使わないため、
/// ランタイム側の Undo・シリアライズ・ホットリロード引き継ぎに追加実装なしで乗る。
///
/// 【候補の遅延取得】
/// スクリプト型・メソッドの候補は「アクタ構成の IPC 往復 ＋ Roslyn コンパイル」を伴うため、
/// 行を描くたびに取りに行くと選択のたびにエディタが固まる。よってコンボボックスを
/// 開いたときに初めて <see cref="IScriptEventCatalogProvider"/> へ問い合わせる。
/// 取得できるまで（および取得できなかったとき）は現在値だけを 1 件表示し、
/// 候補に無い値には <see cref="MissingMarker"/> を添えて「解決できていない」ことを示す。
/// 候補が取れないからといって保存値を書き換えることは絶対にしない。
///
/// 【引数種別】
/// 引数種別（<see cref="SEED.ScriptEventArgKind"/>）はメソッド選択時にシグネチャから
/// 自動決定し、ユーザーには選ばせない（候補算出は <see cref="ScriptEventCatalog"/>）。
/// </summary>
internal static class ScriptEventFieldBuilder
{
    // ── レイアウト定数 ───────────────────────────────────────

    /// <summary>行番号（[0] [1] …）を表示する列の幅（px）。</summary>
    private const double IndexColumnWidth = 26;

    /// <summary>行・追加ボタンの左インデント（px）。折りたたみ配下であることを示す。</summary>
    private const double RowIndent = 8;

    /// <summary>対象アクタ列の相対幅（Star）。参照名が長くなりがちなので広めに取る。</summary>
    private const double ActorColumnStar = 2.0;

    /// <summary>スクリプト型・メソッド・引数の各列の相対幅（Star）。</summary>
    private const double PickerColumnStar = 1.5;

    /// <summary>コンボボックス・引数欄が潰れないための最小幅（px）。</summary>
    private const double PickerMinWidth = 58;

    /// <summary>並び替えボタンの内側余白（px）。上下 2 個並ぶので横幅を詰めておく。</summary>
    private static readonly Thickness MoveButtonPadding = new(3, 1, 3, 1);

    /// <summary>追加（＋）ボタンのアイコン一辺サイズ（px）。行内ボタンより明確に大きくする。</summary>
    private const double AddButtonIconSize = 16;

    /// <summary>追加ボタンの高さ（px）。押しやすさ優先で行 UI より厚くする。</summary>
    private const double AddButtonHeight = 26;

    /// <summary>追加ボタンの内側余白（px）。</summary>
    private static readonly Thickness AddButtonPadding = new(6, 2, 6, 2);

    /// <summary>追加ボタンの外側余白（px）。行との間を少し空ける。</summary>
    private static readonly Thickness AddButtonMargin = new(0, 5, 0, 2);

    /// <summary>行内コントロールの共通外側余白（px）。</summary>
    private static readonly Thickness CellMargin = new(2, 1, 0, 1);

    // ── 文言定数 ─────────────────────────────────────────────

    /// <summary>対象アクタが未設定のときにピッカーへ出す文言。</summary>
    private const string ActorUnsetText = "(未設定)";

    /// <summary>スクリプト型が未選択のときにコンボへ出す文言。</summary>
    private const string ScriptUnsetText = "(スクリプト)";

    /// <summary>メソッドが未選択のときにコンボへ出す文言。</summary>
    private const string MethodUnsetText = "(メソッド)";

    /// <summary>引数種別が None のときに引数欄へ出す文言。</summary>
    private const string NoArgumentText = "(引数なし)";

    /// <summary>結線が 0 件のときに出す案内文。</summary>
    private const string EmptyNoteText = "(結線なし)";

    /// <summary>候補一覧に無い保存値へ添える印（値そのものは書き換えない）。</summary>
    private const string MissingMarker = "⚠ ";

    /// <summary>コンボの候補が空だったときに出す案内項目の文言。</summary>
    private const string NoCandidateText = "(候補なし)";

    /// <summary>メソッド候補の同一性キーで名前と引数種別を繋ぐ区切り（識別子に使えない制御文字）。</summary>
    private const string MethodKeySeparator = "\u0001";

    // ── 配色 ─────────────────────────────────────────────────
    private static readonly SolidColorBrush BrushRemove    = new(Color.FromRgb(0xCC, 0x77, 0x77));
    private static readonly SolidColorBrush BrushMove      = new(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly SolidColorBrush BrushAdd       = new(Color.FromRgb(0x77, 0xCC, 0x88));
    private static readonly SolidColorBrush BrushIndex     = new(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly SolidColorBrush BrushEmptyNote = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush BrushComboBg   = new(Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>
    /// ScriptEvent フィールド 1 件分の UI を生成する。
    /// </summary>
    /// <param name="field">フィールド情報（<see cref="ScriptFieldInfo.IsScriptEvent"/> が true であること）。</param>
    /// <param name="rawValue">
    /// 現在のシリアライズ値（JSON 配列文字列）。null・壊れ値は「結線 0 件」として扱う
    /// （<see cref="SEED.ScriptEvent.Decode"/> の寛容デコード）。
    /// </param>
    /// <param name="onChange">値変更の通知（JSON 配列文字列をそのまま渡す）。</param>
    /// <param name="onRefDrop">
    /// アクタ参照のドロップ解決役。null なら対象アクタ欄は表示のみになる。
    /// </param>
    /// <param name="catalog">
    /// 結線先候補（スクリプト型・メソッド）の問い合わせ先。null なら候補は取得できず、
    /// 現在値の表示だけになる（値は壊さない）。
    /// </param>
    /// <param name="expandStates">折りたたみ状態のストア（UI 再構築をまたいで復元する）。</param>
    /// <param name="expandKey">このフィールドの折りたたみ状態キー。</param>
    public static UIElement Build(
        ScriptFieldInfo              field,
        string?                      rawValue,
        Action<string>               onChange,
        IReferenceDropResolver?      onRefDrop,
        IScriptEventCatalogProvider? catalog,
        ExpandStateStore?            expandStates,
        string                       expandKey)
    {
        // 現在値 → バインディングの並び（壊れた値でも例外にならず空リストになる）
        var bindings = new List<SEED.ScriptEventBinding>(
            SEED.ScriptEvent.Decode(rawValue ?? SEED.ScriptEvent.EmptyJson));

        var rows = new StackPanel { Margin = new Thickness(RowIndent, 2, 0, 2) };

        var expander = new Expander
        {
            IsExpanded = true,
            Header     = $"{field.Label} ({bindings.Count})",
            Foreground = BrushText,
            FontSize   = RowFontSize,
            Margin     = new Thickness(0, 2, 0, 2),
            Content    = rows,
            ToolTip    = BuildHeaderTooltip(field),
        };
        // 通常のフィールド行と違い Expander には ScriptFieldTooltip が自動で付かないので、
        // ヘッダのツールチップは手動で組んで持たせる（表示時間も行ラベルと揃える）。
        ToolTipService.SetInitialShowDelay(expander, ScriptFieldTooltip.InitialShowDelayMs);
        ToolTipService.SetShowDuration(expander, ScriptFieldTooltip.ShowDurationMs);

        // 値を確定して送る（バインディング列 → JSON 配列文字列）。件数表示もここで更新する。
        void Commit()
        {
            expander.Header = $"{field.Label} ({bindings.Count})";
            onChange(SEED.ScriptEvent.Encode(bindings));
        }

        // 行 UI を現在のバインディング列から作り直す
        void Rebuild() => RebuildRows(rows, bindings, field, onRefDrop, catalog, Commit, RebuildLater);

        // コンボの選択変更ハンドラの内側から行を作り直すと、イベント発火中の
        // コントロールをツリーから外すことになる。1 ティック遅らせて安全に作り直す。
        void RebuildLater() =>
            rows.Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);

        Rebuild();
        expandStates?.Track(expander, expandKey, defaultExpanded: true);
        return expander;
    }

    /// <summary>折りたたみヘッダのツールチップ（完全なラベル＋説明＋操作案内）を作る。</summary>
    private static object BuildHeaderTooltip(ScriptFieldInfo field)
    {
        var panel = (StackPanel)ScriptFieldTooltip.Build(field);
        panel.Children.Add(new TextBlock
        {
            Text         = "追加ボタンで結線を 1 件足し、各行でアクタ・スクリプト・メソッドを選びます。\n"
                         + "スクリプト型／メソッドの候補はコンボを開いたときに取得します。",
            FontSize     = RowFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        });
        return panel;
    }

    /// <summary>
    /// バインディング列から行 UI を作り直す。
    /// </summary>
    /// <param name="rows">行を並べるコンテナ（中身は毎回作り替える）。</param>
    /// <param name="bindings">編集対象のバインディング列（この場で書き換える）。</param>
    /// <param name="field">フィールド情報（追加ボタンの文言に使う）。</param>
    /// <param name="onRefDrop">アクタ参照のドロップ解決役。</param>
    /// <param name="catalog">候補の問い合わせ先。</param>
    /// <param name="commit">値を確定して IPC へ流す処理。</param>
    /// <param name="rebuildLater">1 ティック遅らせて行を作り直す処理。</param>
    private static void RebuildRows(
        StackPanel                    rows,
        List<SEED.ScriptEventBinding> bindings,
        ScriptFieldInfo               field,
        IReferenceDropResolver?       onRefDrop,
        IScriptEventCatalogProvider?  catalog,
        Action                        commit,
        Action                        rebuildLater)
    {
        rows.Children.Clear();

        if (bindings.Count == 0) rows.Children.Add(MakeEmptyNote());

        for (int i = 0; i < bindings.Count; i++)
        {
            // ラムダへ捕捉する添字はループ変数と別変数にする（キャプチャ事故防止）
            var index = i;
            rows.Children.Add(BuildBindingRow(
                bindings[index], index,
                canMoveUp:   index > 0,
                canMoveDown: index < bindings.Count - 1,
                onRefDrop:   onRefDrop,
                catalog:     catalog,
                commit:      commit,
                onStructureChanged: rebuildLater,
                // 削除: 列から外して送り直し、行を作り直す
                onRemove: () =>
                {
                    if (index >= bindings.Count) return;
                    bindings.RemoveAt(index);
                    commit();
                    rebuildLater();
                },
                // 並び替え: 値そのものを入れ替えるので書き戻しは通常の編集と同じ経路
                onMoveUp:   () => Swap(bindings, index, index - 1, commit, rebuildLater),
                onMoveDown: () => Swap(bindings, index, index + 1, commit, rebuildLater)));
        }

        rows.Children.Add(MakeAddButton(field, () =>
        {
            bindings.Add(new SEED.ScriptEventBinding());
            commit();
            rebuildLater();
        }));
    }

    /// <summary>バインディング 2 件の位置を入れ替える（範囲外・同一なら何もしない）。</summary>
    private static void Swap(
        List<SEED.ScriptEventBinding> bindings, int a, int b, Action commit, Action rebuildLater)
    {
        if (a < 0 || b < 0 || a >= bindings.Count || b >= bindings.Count || a == b) return;
        (bindings[a], bindings[b]) = (bindings[b], bindings[a]);
        commit();
        rebuildLater();
    }

    /// <summary>結線が 0 件のときに出す案内行。</summary>
    private static UIElement MakeEmptyNote() => new TextBlock
    {
        Text       = EmptyNoteText,
        Foreground = BrushEmptyNote,
        FontSize   = RowFontSize,
        FontStyle  = FontStyles.Italic,
        Margin     = new Thickness(2, 1, 0, 1),
    };

    /// <summary>末尾に結線を 1 件足すボタン行を作る。</summary>
    private static UIElement MakeAddButton(ScriptFieldInfo field, Action onAdd)
    {
        var btn = MakeIconButton(
            "Icon.Add", BrushAdd, $"「{field.Label}」に呼び出し先を 1 件追加する", onAdd,
            AddButtonIconSize, AddButtonPadding);
        btn.HorizontalAlignment = HorizontalAlignment.Stretch;   // 行いっぱいに広げる
        btn.MinHeight           = AddButtonHeight;
        btn.Margin              = AddButtonMargin;
        return btn;
    }

    // ── 行 ───────────────────────────────────────────────────

    /// <summary>
    /// バインディング 1 件分の行を作る。
    ///
    /// 各項目の編集は「バインディングを書き換える → <paramref name="commit"/>」の 1 経路に統一する。
    /// 対象アクタ・スクリプト型が変わったときだけ、候補の前提そのものが変わるので
    /// <paramref name="onStructureChanged"/> で行を作り直す。
    /// </summary>
    private static UIElement BuildBindingRow(
        SEED.ScriptEventBinding      binding,
        int                          index,
        bool                         canMoveUp,
        bool                         canMoveDown,
        IReferenceDropResolver?      onRefDrop,
        IScriptEventCatalogProvider? catalog,
        Action                       commit,
        Action                       onStructureChanged,
        Action                       onRemove,
        Action                       onMoveUp,
        Action                       onMoveDown)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndexColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActorColumnStar,  GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PickerColumnStar, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PickerColumnStar, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PickerColumnStar, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── [i] 行番号 ────────────────────────────────────────
        var indexLabel = new TextBlock
        {
            Text              = $"[{index}]",
            Foreground        = BrushIndex,
            FontSize          = RowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(indexLabel, 0);
        grid.Children.Add(indexLabel);

        // ── 引数欄のホスト（メソッド選択に応じて中身だけ差し替える）──
        var argHost = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth          = PickerMinWidth,
        };

        // ── 対象アクタ（共通の参照ピッカーへ委譲：D&D・解除・ジャンプ）──
        var actorPicker = ReferencePicker.Create(
            new ReferenceFieldSpec
            {
                Kind = ReferenceKindCatalog.GameObjectKind,
                // ScriptEvent はアクタ名しか保存しない（コンポーネントはスクリプト型名で解決する）
                WantSlotName = false,
                UnsetText    = ActorUnsetText,
                ExtraTooltip = "呼び出し先のアクタ。実行時にこの名前でシーンから探します",
            },
            string.IsNullOrEmpty(binding.Actor) ? null : binding.Actor,
            null,
            (actor, _) =>
            {
                binding.Actor = actor ?? "";
                // アクタが変われば「そのアクタが持つスクリプト型」の候補も変わる。
                // 保存済みのスクリプト型・メソッドはあえて消さない（別アクタでも
                // 同名スクリプトが付いていることは普通にあるため）。解決できなければ印が付く。
                commit();
                onStructureChanged();
            },
            onRefDrop);
        actorPicker.Element.Margin = CellMargin;
        Grid.SetColumn(actorPicker.Element, 1);
        grid.Children.Add(actorPicker.Element);

        // ── スクリプト型 ─────────────────────────────────────
        var scriptCombo = MakeLazyCombo(
            currentValue:   binding.Script,
            currentDisplay: binding.Script,
            unsetText:      ScriptUnsetText,
            tooltip:        "呼び出し先のスクリプト型。開いたときに対象アクタの構成から候補を取得します",
            requestOptions: catalog is null || string.IsNullOrEmpty(binding.Actor)
                ? null
                : ready => catalog.RequestScriptTypes(
                      binding.Actor, names => ready(ToScriptOptions(names))),
            onSelected: option =>
            {
                var newScript = option?.Value ?? "";
                if (newScript == binding.Script) return;
                binding.Script = newScript;
                // 型が変われば呼べるメソッドは総入れ替えになるので、メソッドと引数は白紙へ戻す
                // （別型の同名メソッドへ黙って結線されるより、選び直させる方が安全）。
                binding.Method  = "";
                binding.ArgKind = SEED.ScriptEventArgKind.None;
                binding.Arg     = "";
                commit();
                onStructureChanged();
            });
        Grid.SetColumn(scriptCombo, 2);
        grid.Children.Add(scriptCombo);

        // ── メソッド ─────────────────────────────────────────
        var currentMethod = new ScriptEventMethod(binding.Method, binding.ArgKind);
        var methodCombo = MakeLazyCombo(
            currentValue:   MethodKey(binding.Method, binding.ArgKind),
            currentDisplay: string.IsNullOrEmpty(binding.Method) ? "" : currentMethod.DisplayText,
            unsetText:      MethodUnsetText,
            tooltip:        "呼び出すメソッド。引数の種別はシグネチャから自動で決まります",
            requestOptions: catalog is null
                         || string.IsNullOrEmpty(binding.Actor)
                         || string.IsNullOrEmpty(binding.Script)
                ? null
                : ready => catalog.RequestMethods(
                      binding.Actor, binding.Script, methods => ready(ToMethodOptions(methods))),
            onSelected: option =>
            {
                var method  = option?.MethodName ?? "";
                var argKind = option?.ArgKind    ?? SEED.ScriptEventArgKind.None;
                if (method == binding.Method && argKind == binding.ArgKind) return;

                // 引数種別が変わると引数文字列の意味も変わるので値を捨てる
                // （"true" が float 欄に残るような不整合を作らない）。
                if (argKind != binding.ArgKind) binding.Arg = "";
                binding.Method  = method;
                binding.ArgKind = argKind;
                commit();
                // 引数欄だけを差し替える（行ごと作り直すとコンボの開閉状態が飛ぶため）
                argHost.Content = BuildArgumentEditor(binding, onRefDrop, commit);
            });
        Grid.SetColumn(methodCombo, 3);
        grid.Children.Add(methodCombo);

        // ── 引数 ─────────────────────────────────────────────
        argHost.Content = BuildArgumentEditor(binding, onRefDrop, commit);
        Grid.SetColumn(argHost, 4);
        grid.Children.Add(argHost);

        // ── 操作ボタン ───────────────────────────────────────
        var actions = MakeRowActions(index, onRemove, onMoveUp, onMoveDown, canMoveUp, canMoveDown);
        Grid.SetColumn(actions, 5);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>行の右端に並べる操作ボタン群（上へ・下へ・削除）を作る。</summary>
    private static FrameworkElement MakeRowActions(
        int index, Action onRemove, Action onMoveUp, Action onMoveDown, bool canMoveUp, bool canMoveDown)
    {
        var panel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(MakeIconButton(
            "Icon.MoveUp", BrushMove, $"{index} 番目の結線を 1 つ上へ移動する", onMoveUp,
            RowButtonIconSize, MoveButtonPadding, canMoveUp));
        panel.Children.Add(MakeIconButton(
            "Icon.MoveDown", BrushMove, $"{index} 番目の結線を 1 つ下へ移動する", onMoveDown,
            RowButtonIconSize, MoveButtonPadding, canMoveDown));
        panel.Children.Add(MakeIconButton(
            "Icon.Close", BrushRemove, $"{index} 番目の結線を削除する", onRemove));
        return panel;
    }

    // ── 引数欄 ───────────────────────────────────────────────

    /// <summary>
    /// 引数種別に応じた入力部品を作る。
    ///
    /// 種別と部品の対応（種別の表そのものは <see cref="SEED.ScriptEventArgKind"/> が正典）:
    ///   None       … 「引数なし」の表示だけ（入力させない）
    ///   String     … 1 行テキスト（改行は保存値から必ず取り除く）
    ///   Float／Int … 数値テキスト（不変カルチャで解釈・整形）
    ///   Bool       … チェックボックス（"true" / "false"）
    ///   GameObject … アクタ参照ピッカー（保存値はアクタ名）
    /// </summary>
    private static UIElement BuildArgumentEditor(
        SEED.ScriptEventBinding binding, IReferenceDropResolver? onRefDrop, Action commit)
    {
        // 引数値を 1 か所で更新する（無変化なら IPC を送らない）
        void SetArg(string text)
        {
            if (binding.Arg == text) return;
            binding.Arg = text;
            commit();
        }

        switch (binding.ArgKind)
        {
            case SEED.ScriptEventArgKind.String:
            {
                var tb = MakeTextBox(binding.Arg);
                tb.AcceptsReturn = false;
                tb.ToolTip       = "メソッドへ渡す文字列（改行は保存できないため取り除かれます）";
                void CommitText()
                {
                    var text = StripLineBreaks(tb.Text);
                    if (tb.Text != text) tb.Text = text;   // 貼り付けで混入した改行を表示にも反映
                    SetArg(text);
                }
                tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitText(); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitText();
                return tb;
            }

            case SEED.ScriptEventArgKind.Float:
            {
                var v = float.TryParse(binding.Arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)
                    ? pv : 0f;
                var tb = MakeTextBox(Fmt(v));
                tb.ToolTip = "メソッドへ渡す実数値";
                tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitFloat(tb, SetArg); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitFloat(tb, SetArg);
                return tb;
            }

            case SEED.ScriptEventArgKind.Int:
            {
                var v = int.TryParse(binding.Arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pv)
                    ? pv : 0;
                var tb = MakeTextBox(v.ToString(CultureInfo.InvariantCulture));
                tb.ToolTip = "メソッドへ渡す整数値";
                tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitInt(tb, SetArg); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitInt(tb, SetArg);
                return tb;
            }

            case SEED.ScriptEventArgKind.Bool:
            {
                var cb = new CheckBox
                {
                    IsChecked         = binding.Arg == SEED.ScriptEvent.TrueText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = CellMargin,
                    ToolTip           = "メソッドへ渡す真偽値",
                };
                cb.Checked   += (_, _) => SetArg(SEED.ScriptEvent.TrueText);
                cb.Unchecked += (_, _) => SetArg(SEED.ScriptEvent.FalseText);
                return cb;
            }

            case SEED.ScriptEventArgKind.GameObject:
            {
                var picker = ReferencePicker.Create(
                    new ReferenceFieldSpec
                    {
                        Kind         = ReferenceKindCatalog.GameObjectKind,
                        WantSlotName = false,
                        UnsetText    = ActorUnsetText,
                        ExtraTooltip = "メソッドへ渡すアクタ。実行時に GameObject.Find で解決します",
                    },
                    string.IsNullOrEmpty(binding.Arg) ? null : binding.Arg,
                    null,
                    (actor, _) => SetArg(actor ?? ""),
                    onRefDrop);
                picker.Element.Margin = CellMargin;
                return picker.Element;
            }

            default:
                return new TextBlock
                {
                    Text              = NoArgumentText,
                    Foreground        = BrushEmptyNote,
                    FontSize          = RowFontSize,
                    FontStyle         = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = CellMargin,
                };
        }
    }

    /// <summary>
    /// 保存する文字列から改行を取り除く。
    ///
    /// SET_SCRIPT_FIELD は 1 行 1 コマンドの行区切り IPC である。
    /// JSON エンコーダ側でも改行はエスケープされるが、
    /// 「インスペクタで見えている値」と「保存される値」を一致させるため入口でも落とす。
    /// </summary>
    private static string StripLineBreaks(string text)
        => text.Replace("\r", "").Replace("\n", "");

    // ── 遅延コンボボックス ───────────────────────────────────

    /// <summary>
    /// 遅延コンボボックスの選択肢 1 件。
    ///
    /// 表示（<see cref="Display"/>）と同一性キー（<see cref="Value"/>）を分けているのは、
    /// 「候補に無い保存値」に印を添えて表示しても値そのものは変えないため。
    /// </summary>
    /// <param name="Value">同一性の判定に使うキー（スクリプト型名／メソッドキー）。未設定行は空文字列。</param>
    /// <param name="Display">コンボに表示する文字列。</param>
    /// <param name="MethodName">メソッド候補のときのメソッド名（それ以外は空文字列）。</param>
    /// <param name="ArgKind">メソッド候補のときの引数種別（それ以外は None）。</param>
    private sealed record ComboOption(
        string Value, string Display, string MethodName, SEED.ScriptEventArgKind ArgKind)
    {
        /// <summary>ComboBox は項目の <c>ToString()</c> を表示に使うため、表示文字列を返す。</summary>
        public override string ToString() => Display;
    }

    /// <summary>メソッド候補の同一性キー（名前だけではオーバーロードを区別できない）。</summary>
    private static string MethodKey(string name, SEED.ScriptEventArgKind argKind)
        => string.IsNullOrEmpty(name) ? "" : $"{name}{MethodKeySeparator}{(int)argKind}";

    /// <summary>スクリプト型名の一覧を選択肢へ変換する。</summary>
    private static IReadOnlyList<ComboOption> ToScriptOptions(IReadOnlyList<string> names)
    {
        var options = new List<ComboOption>(names.Count);
        foreach (var n in names)
            options.Add(new ComboOption(n, n, "", SEED.ScriptEventArgKind.None));
        return options;
    }

    /// <summary>メソッド候補の一覧を選択肢へ変換する。</summary>
    private static IReadOnlyList<ComboOption> ToMethodOptions(IReadOnlyList<ScriptEventMethod> methods)
    {
        var options = new List<ComboOption>(methods.Count);
        foreach (var m in methods)
            options.Add(new ComboOption(MethodKey(m.Name, m.ArgKind), m.DisplayText, m.Name, m.ArgKind));
        return options;
    }

    /// <summary>
    /// 「開いたときに初めて候補を取りに行く」コンボボックスを作る。
    ///
    /// 【表示の規則】
    /// - 候補取得前: 未設定行 ＋（保存値があれば）保存値 1 件だけを出し、保存値を選択状態にする。
    /// - 候補取得後: 未設定行 ＋ 取得した候補。保存値が候補に無ければ
    ///   <see cref="MissingMarker"/> 付きの行を足して選択を維持する（値は書き換えない）。
    /// - 候補が 0 件で保存値も無い場合は、選んでも何も起きない案内行を出す。
    ///
    /// 取得は 1 回だけ行う。対象アクタ・スクリプト型が変わった場合は行ごと作り直されるため、
    /// このコンボも新しい前提で作り直される。
    /// </summary>
    /// <param name="currentValue">現在の保存値（同一性キー。未設定なら空文字列）。</param>
    /// <param name="currentDisplay">現在の保存値の表示文字列（未設定なら空文字列）。</param>
    /// <param name="unsetText">未設定行の文言。</param>
    /// <param name="tooltip">コンボのツールチップ。</param>
    /// <param name="requestOptions">
    /// 候補の取得要求。null なら取得手段が無い（対象アクタ未設定・プロバイダ未接続）ことを表し、
    /// 開いても問い合わせない。渡すコールバックは UI スレッドで呼ばれる前提。
    /// </param>
    /// <param name="onSelected">選択が変わったときの通知（未設定行・案内行を選ぶと null が渡る）。</param>
    private static ComboBox MakeLazyCombo(
        string                                      currentValue,
        string                                      currentDisplay,
        string                                      unsetText,
        string                                      tooltip,
        Action<Action<IReadOnlyList<ComboOption>>>? requestOptions,
        Action<ComboOption?>                        onSelected)
    {
        var combo = new ComboBox
        {
            Background               = BrushComboBg,
            Foreground               = BrushText,
            BorderBrush              = BrushBorder,
            BorderThickness          = new Thickness(1),
            FontSize                 = RowFontSize,
            Padding                  = new Thickness(3, 1, 3, 1),
            Margin                   = CellMargin,
            MinWidth                 = PickerMinWidth,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip                  = tooltip,
        };

        // 未設定行（選ぶと結線先の解除に相当）。同一性キーは空文字列。
        var unsetOption = new ComboOption("", unsetText, "", SEED.ScriptEventArgKind.None);

        // 項目の作り直し中に SelectionChanged が発火して値を壊さないためのガード
        bool updating = false;

        // 候補一覧を流し込み、保存値の選択を復元する
        void Populate(IReadOnlyList<ComboOption> options)
        {
            updating = true;
            try
            {
                combo.Items.Clear();
                combo.Items.Add(unsetOption);

                ComboOption? selected = string.IsNullOrEmpty(currentValue) ? unsetOption : null;
                foreach (var o in options)
                {
                    combo.Items.Add(o);
                    if (o.Value == currentValue) selected = o;
                }

                // 候補に無い保存値は「解決できていない」ことを示して残す（値は変えない）
                if (selected is null)
                {
                    var missing = new ComboOption(
                        currentValue, MissingMarker + currentDisplay, "", SEED.ScriptEventArgKind.None);
                    combo.Items.Insert(1, missing);
                    selected = missing;
                }
                // 候補ゼロ（対象アクタにスクリプトが無い等）を「取得したが空」と分かる形で示す
                else if (options.Count == 0)
                {
                    combo.Items.Add(new ComboOption("", NoCandidateText, "", SEED.ScriptEventArgKind.None));
                }

                combo.SelectedItem = selected;
            }
            finally { updating = false; }
        }

        // 取得前の暫定表示: 未設定行 ＋ 現在値だけ（候補は開くまで取りに行かない）
        Populate(string.IsNullOrEmpty(currentValue)
            ? Array.Empty<ComboOption>()
            : new[] { new ComboOption(currentValue, currentDisplay, "", SEED.ScriptEventArgKind.None) });

        // 初回に開いたときだけ候補を取りに行く（IPC 往復＋コンパイルを伴うため）
        bool requested = false;
        combo.DropDownOpened += (_, _) =>
        {
            if (requested || requestOptions is null) return;
            requested = true;
            requestOptions(Populate);
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            var option = combo.SelectedItem as ComboOption;
            // 未設定行・案内行（キーが空）は「未設定を選んだ」として扱う
            onSelected(option is null || string.IsNullOrEmpty(option.Value) ? null : option);
        };

        return combo;
    }
}
