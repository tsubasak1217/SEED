// ============================================================
//  MergeEditorWindow.cs — 競合のマージエディタ（3 面の専用ウィンドウ）
//
//  【手本】
//  Visual Studio の 3-way マージエディタ。上段左に「取り込み元」、上段右に「現在」、
//  下段に「結果」。上段はブロック単位で行を揃えて並べ、
//  各ブロックの左端のチェックで採用/不採用を切り替えると結果が作り直される。
//
//  【上段 2 面のスクロールは連動させない（2026-09-19 の指摘で撤去）】
//  行を揃えてあるので縦オフセットを写すだけで同期はできるが、**操作しづらい**。
//  片方を読みながらもう片方を別の位置で見たい（例: 取り込み元の後ろの方を見ながら
//  現在の該当箇所を探す）ときに、動かした側につられてもう片方が飛んでしまう。
//  行揃え（<see cref="MergeAlignedView"/> の詰め物）はそのまま残すので、
//  「前の競合 / 次の競合」は両面を同じ行番号へ寄せられる。
//
//  【なぜ専用ウィンドウなのか】
//  `.scene` / `.actor` / `.actor2d` はスクリプトエディタで開くことを
//  意図的に禁止している（editor/config/text_editable_extensions.json の `_note_forbidden`）。
//  競合の解決には中身を見せる必要があるので、
//  **解決のためだけに読めて、解決以外の編集経路を持たない** 画面を別に用意する。
//
//  【Lore を知らない】
//  確定は <see cref="MergeEditorCommitHandler"/> で外から差す。
//  この型はバージョン管理の仕組みを一切知らないので、
//  サンプルの印つきファイルを開いて動きを確かめることができる。
//
//  【配色】
//  Theme/SeedDialogTheme（面と文字）と Theme/SeedColorTable（差分の色）に従う。
//  ボタンは共通書式（SeedButtonStyles.xaml）。ここで色を決めない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using SEEDEditor.Headless;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Merge;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.Panels.VersionControl.MergeEditor;

/// <summary>
/// 競合したファイルを 3 面で解決する非モーダルウィンドウ。
/// </summary>
public sealed class MergeEditorWindow : Window
{
    // ── 寸法（このウィンドウ固有のもの）──────────────────────

    /// <summary>ウィンドウの初期幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 1180;

    /// <summary>ウィンドウの初期高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 820;

    /// <summary>ウィンドウの最小幅 [px]。</summary>
    private const double WINDOW_MIN_WIDTH_PX = 720;

    /// <summary>ウィンドウの最小高さ [px]。</summary>
    private const double WINDOW_MIN_HEIGHT_PX = 480;

    /// <summary>上段と下段の高さの比（上段 : 下段）。</summary>
    private const double UPPER_PANE_WEIGHT = 1.35;

    /// <summary>下段の高さの比。</summary>
    private const double LOWER_PANE_WEIGHT = 1.0;

    /// <summary>上下を分ける仕切りの太さ [px]。</summary>
    private const double SPLITTER_THICKNESS_PX = 4;

    /// <summary>面の見出しの文字サイズ。</summary>
    private const double PANE_TITLE_FONT_SIZE = 11;

    /// <summary>エディタの文字サイズ。</summary>
    private const double EDITOR_FONT_SIZE = 12;

    /// <summary>左右の面のあいだの余白 [px]。</summary>
    private const double PANE_GAP_PX = 4;

    /// <summary>ツールバーのボタン同士の間隔 [px]。</summary>
    private const double TOOLBAR_GAP_PX = 6;

    /// <summary>等幅フォント（差分は桁が揃っていないと読めない）。</summary>
    private const string EDITOR_FONT_FAMILY = "Consolas";

    // ── 素性 ────────────────────────────────────────────────

    /// <summary>対象のファイル（絶対パス）。</summary>
    public string AbsolutePath { get; }

    /// <summary>進行中のマージの出どころ（両方採用の並び順を決める）。</summary>
    private readonly MergeOrigin _origin;

    /// <summary>確定したときに呼ぶ処理。</summary>
    private readonly MergeEditorCommitHandler _onCommit;

    // ── 解析結果と選択 ──────────────────────────────────────

    /// <summary>解析済みの印つきテキスト。</summary>
    private readonly ConflictMarkerDocument _document;

    /// <summary>行を揃えた上段 2 面の表示モデル。</summary>
    private readonly MergeAlignedView _view;

    /// <summary>競合ブロックごとの選択（<see cref="MergeSegment.ConflictIndex"/> で引く）。</summary>
    private readonly MergeBlockChoice[] _choices;

    /// <summary>いま「前の競合 / 次の競合」で見ているブロックの位置。</summary>
    private int _focusedBlock = -1;

    // ── 画面の部品 ──────────────────────────────────────────

    /// <summary>上段左（取り込み元）。</summary>
    private readonly TextEditor _incomingEditor;

    /// <summary>上段右（現在）。</summary>
    private readonly TextEditor _currentEditor;

    /// <summary>下段（結果。編集できる）。</summary>
    private readonly TextEditor _resultEditor;

    /// <summary>上段左のチェック余白。</summary>
    private readonly MergeBlockMargin _incomingMargin;

    /// <summary>上段右のチェック余白。</summary>
    private readonly MergeBlockMargin _currentMargin;

    /// <summary>残りのブロック数などを出す状態行。</summary>
    private readonly TextBlock _statusText;

    /// <summary>「すべて両方」ボタン（できないファイルでは無効にする）。</summary>
    private readonly Button _takeBothButton;

    /// <summary>「マージを確定」ボタン（確定中は押せなくする）。</summary>
    private readonly Button _applyButton;

    /// <summary>確定を実行中か（二重に走らせない）。</summary>
    private bool _committing;

    /// <summary>
    /// マージエディタを開く。
    /// </summary>
    /// <param name="absolutePath">競合したファイルの絶対パス。</param>
    /// <param name="origin">
    /// 進行中のマージの出どころ。「両方を取り込む」の並び順がこれで決まる。
    /// </param>
    /// <param name="incomingName">「取り込み元」の表示名（例: "リモート" / "ブランチ feature"）。</param>
    /// <param name="currentName">「現在」の表示名（例: "自分の変更" / "ブランチ main"）。</param>
    /// <param name="onCommit">確定したときに呼ぶ処理（結果テキストを受け取る）。</param>
    /// <exception cref="MergeParseException">
    /// ファイルが読めない・バイナリ・印が壊れている・印が 1 つも無いとき。
    /// 呼び出し側（<see cref="MergeEditorWindows"/>）が理由を利用者へ見せる。
    /// </exception>
    public MergeEditorWindow(
        string absolutePath,
        MergeOrigin origin,
        string incomingName,
        string currentName,
        MergeEditorCommitHandler onCommit)
    {
        AbsolutePath = absolutePath ?? throw new ArgumentNullException(nameof(absolutePath));
        _origin      = origin;
        _onCommit    = onCommit ?? throw new ArgumentNullException(nameof(onCommit));

        // ── 中身を読んで解析する（ここで落ちたら窓は開かない）──
        var read = MergeFileText.Read(absolutePath);
        if (!read.Succeeded) throw new MergeParseException(read.Reason);

        if (!ConflictMarkerDocument.HasConflictMarkers(read.Text))
            throw new MergeParseException(VersionControlMessages.MERGE_EDITOR_NO_CONFLICT);

        _document = ConflictMarkerDocument.Parse(read.Text);
        _view     = MergeAlignedView.Build(_document);
        _choices  = new MergeBlockChoice[_document.ConflictCount];

        // 追加どうしの競合（両側とも元へ挿し込んだだけ）は両方を残すのが
        // 既定として自然なので、最初からチェックを入れておく。
        // 元の行を消している／書き換えているブロックは必ず利用者に選ばせる。
        // ★判定はツールバーの「すべて両方」と同じ関数を使う
        //   （別々の条件にすると「既定で入っているのにボタンは押せない」が起きる）。
        for (var i = 0; i < _choices.Length; i++)
        {
            _choices[i] = MergeTakeBothRule.IsUnionable(_document.Conflicts[i])
                ? MergeBlockChoice.Both
                : MergeBlockChoice.Neither;
        }

        // ── 画面を組む ──
        ApplyWindowChrome(Path.GetFileName(absolutePath));

        _incomingMargin = new MergeBlockMargin(
            index => _choices[index].TakeIncoming,
            ToggleIncoming,
            string.Format(
                VersionControlMessages.MERGE_EDITOR_TAKE_SIDE_TOOLTIP_FORMAT,
                VersionControlMessages.MERGE_EDITOR_PANE_INCOMING));

        _currentMargin = new MergeBlockMargin(
            index => _choices[index].TakeCurrent,
            ToggleCurrent,
            string.Format(
                VersionControlMessages.MERGE_EDITOR_TAKE_SIDE_TOOLTIP_FORMAT,
                VersionControlMessages.MERGE_EDITOR_PANE_CURRENT));

        _incomingEditor = NewSideEditor(_view.IncomingRows, _incomingMargin);
        _currentEditor  = NewSideEditor(_view.CurrentRows,  _currentMargin);
        _resultEditor   = NewResultEditor();
        _statusText     = SeedDialogTheme.NewLabel(string.Empty, SeedDialogTheme.DimText,
                                                   PANE_TITLE_FONT_SIZE);
        _takeBothButton = SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_ALL_BOTH, OnAllBoth,
            leftMargin: TOOLBAR_GAP_PX);
        _applyButton = SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_APPLY, OnApply, isPrimary: true,
            leftMargin: TOOLBAR_GAP_PX);

        Content = BuildLayout(incomingName, currentName);

        // 「すべて両方」は成り立つときだけ押せるようにする。
        // 押せないときは理由をツールチップに出す（WPF は既定で無効な要素にツールチップを出さない）。
        var takeBoth = MergeTakeBothRule.Evaluate(_document);
        _takeBothButton.IsEnabled = takeBoth.Allowed;
        if (!takeBoth.Allowed)
        {
            _takeBothButton.ToolTip = takeBoth.Reason;
            ToolTipService.SetShowOnDisabled(_takeBothButton, true);
        }

        RefreshResult();
    }

    // ============================================================
    //  画面の組み立て
    // ============================================================

    /// <summary>
    /// ウィンドウ自体の見た目を決める。
    ///
    /// <para>
    /// <see cref="SeedDialogTheme.ApplyWindowChrome"/> を使わないのは、
    /// あれが「大きさを変えられない小さなモーダル」向けだから。
    /// マージエディタは中身を読む画面なので、必ず大きさを変えられる必要がある。
    /// </para>
    /// </summary>
    /// <param name="fileName">タイトルに出すファイル名。</param>
    private void ApplyWindowChrome(string fileName)
    {
        Title                 = string.Format(
            VersionControlMessages.MERGE_EDITOR_TITLE_FORMAT, fileName);
        Width                 = WINDOW_WIDTH_PX;
        Height                = WINDOW_HEIGHT_PX;
        MinWidth              = WINDOW_MIN_WIDTH_PX;
        MinHeight             = WINDOW_MIN_HEIGHT_PX;
        Background            = SeedDialogTheme.Background;
        ResizeMode            = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;
    }

    /// <summary>
    /// 見出し → ツールバー → 上段 2 面 → 仕切り → 結果 → 状態行 の 6 段に組む。
    /// </summary>
    /// <param name="incomingName">「取り込み元」の表示名。</param>
    /// <param name="currentName">「現在」の表示名。</param>
    private UIElement BuildLayout(string incomingName, string currentName)
    {
        var root = new Grid { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 見出し
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // ツールバー
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(UPPER_PANE_WEIGHT, GridUnitType.Star),
        });                                                                        // 上段
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 仕切り
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(LOWER_PANE_WEIGHT, GridUnitType.Star),
        });                                                                        // 結果
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状態行

        var origin = SeedDialogTheme.NewLabel(
            string.Format(
                VersionControlMessages.MERGE_EDITOR_ORIGIN_FORMAT, incomingName, currentName),
            SeedDialogTheme.Text, PANE_TITLE_FONT_SIZE);
        Grid.SetRow(origin, 0);
        root.Children.Add(origin);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var upper = BuildUpperPanes(incomingName, currentName);
        Grid.SetRow(upper, 2);
        root.Children.Add(upper);

        var splitter = new GridSplitter
        {
            Height              = SPLITTER_THICKNESS_PX,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Center,
            Background          = SeedDialogTheme.FieldBorder,
            ResizeBehavior      = GridResizeBehavior.PreviousAndNext,
        };
        Grid.SetRow(splitter, 3);
        root.Children.Add(splitter);

        var result = BuildPane(
            VersionControlMessages.MERGE_EDITOR_PANE_RESULT, _resultEditor);
        Grid.SetRow(result, 4);
        root.Children.Add(result);

        _statusText.Margin = new Thickness(0, SeedDialogTheme.ROW_SPACING_PX / 2, 0, 0);
        Grid.SetRow(_statusText, 5);
        root.Children.Add(_statusText);

        return root;
    }

    /// <summary>ツールバー（一括選択・移動・確定）を作る。</summary>
    private UIElement BuildToolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, SeedDialogTheme.ROW_SPACING_PX, 0, PANE_GAP_PX),
        };

        bar.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_ALL_INCOMING, OnAllIncoming));
        bar.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_ALL_CURRENT, OnAllCurrent,
            leftMargin: TOOLBAR_GAP_PX));
        bar.Children.Add(_takeBothButton);

        bar.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_PREV_CONFLICT, OnPreviousConflict,
            leftMargin: TOOLBAR_GAP_PX * 3));
        bar.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_NEXT_CONFLICT, OnNextConflict,
            leftMargin: TOOLBAR_GAP_PX));

        bar.Children.Add(_applyButton);
        bar.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.MERGE_EDITOR_CANCEL, OnCancel,
            leftMargin: TOOLBAR_GAP_PX));

        return bar;
    }

    /// <summary>上段 2 面（取り込み元 / 現在）を左右に並べる。</summary>
    /// <param name="incomingName">「取り込み元」の表示名。</param>
    /// <param name="currentName">「現在」の表示名。</param>
    private UIElement BuildUpperPanes(string incomingName, string currentName)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var left = BuildPane(
            $"{VersionControlMessages.MERGE_EDITOR_PANE_INCOMING}（{incomingName}）",
            _incomingEditor);
        left.Margin = new Thickness(0, 0, PANE_GAP_PX, 0);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = BuildPane(
            $"{VersionControlMessages.MERGE_EDITOR_PANE_CURRENT}（{currentName}）",
            _currentEditor);
        right.Margin = new Thickness(PANE_GAP_PX, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>見出しつきの 1 面を作る。</summary>
    /// <param name="title">見出しの文言。</param>
    /// <param name="editor">中身のエディタ。</param>
    private static Grid BuildPane(string title, TextEditor editor)
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });

        var header = SeedDialogTheme.NewLabel(
            title, SeedDialogTheme.DimText, PANE_TITLE_FONT_SIZE);
        Grid.SetRow(header, 0);
        pane.Children.Add(header);

        Grid.SetRow(editor, 1);
        pane.Children.Add(editor);

        return pane;
    }

    /// <summary>上段の読み取り専用エディタを作る。</summary>
    /// <param name="rows">流し込む表示行。</param>
    /// <param name="margin">左端に付けるチェック余白。</param>
    private TextEditor NewSideEditor(
        IReadOnlyList<MergeDisplayRow> rows, MergeBlockMargin margin)
    {
        var editor = NewEditor(isReadOnly: true);
        editor.Text = MergeAlignedView.ToDisplayText(rows);

        var renderer = new MergeDiffRenderer();
        renderer.SetRows(rows);
        editor.TextArea.TextView.BackgroundRenderers.Add(renderer);

        // チェック余白は行番号より左に置く（選ぶ場所が一番外側にある方が押しやすい）。
        editor.TextArea.LeftMargins.Insert(0, margin);
        margin.SetBlocks(_view.Blocks);

        return editor;
    }

    /// <summary>下段の（編集できる）結果エディタを作る。</summary>
    private TextEditor NewResultEditor() => NewEditor(isReadOnly: false);

    /// <summary>共通のエディタ設定。</summary>
    /// <param name="isReadOnly">読み取り専用にするか。</param>
    private static TextEditor NewEditor(bool isReadOnly)
        => new()
        {
            IsReadOnly      = isReadOnly,
            ShowLineNumbers = true,
            WordWrap        = false,
            FontFamily      = new FontFamily(EDITOR_FONT_FAMILY),
            FontSize        = EDITOR_FONT_SIZE,
            Background      = SeedDialogTheme.Field,
            Foreground      = SeedDialogTheme.Text,
            BorderBrush     = SeedDialogTheme.FieldBorder,
            BorderThickness = new Thickness(SeedDialogTheme.BORDER_THICKNESS_PX),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

    // ============================================================
    //  選択と結果の作り直し
    // ============================================================

    /// <summary>「取り込み元」側の採用を切り替える。</summary>
    /// <param name="conflictIndex">競合ブロックの通し番号。</param>
    private void ToggleIncoming(int conflictIndex)
    {
        var choice = _choices[conflictIndex];
        _choices[conflictIndex] = choice with { TakeIncoming = !choice.TakeIncoming };
        RefreshResult();
    }

    /// <summary>「現在」側の採用を切り替える。</summary>
    /// <param name="conflictIndex">競合ブロックの通し番号。</param>
    private void ToggleCurrent(int conflictIndex)
    {
        var choice = _choices[conflictIndex];
        _choices[conflictIndex] = choice with { TakeCurrent = !choice.TakeCurrent };
        RefreshResult();
    }

    /// <summary>
    /// いまの選択から結果を作り直し、状態行を更新する。
    ///
    /// <para>
    /// ★ここで結果エディタの中身を丸ごと差し替える。手で直した内容は消えるので、
    /// そのことを状態行に常に出しておく（消えてから気づくのでは遅い）。
    /// </para>
    /// </summary>
    private void RefreshResult()
    {
        _resultEditor.Text = MergeComposer.Compose(_document, _choices, _origin);

        _incomingMargin.InvalidateVisual();
        _currentMargin.InvalidateVisual();

        var unselected = MergeComposer.CountUnselected(_choices);
        var remaining  = unselected == 0
            ? VersionControlMessages.MERGE_EDITOR_ALL_RESOLVED
            : string.Format(
                VersionControlMessages.MERGE_EDITOR_REMAINING_FORMAT,
                unselected, _choices.Length);

        ShowStatus(
            $"{remaining}　{VersionControlMessages.MERGE_EDITOR_RESULT_REGENERATED_NOTE}",
            isError: false);
    }

    /// <summary>状態行に 1 行出す。</summary>
    /// <param name="message">出す文言。</param>
    /// <param name="isError">失敗として（赤で）出すか。</param>
    private void ShowStatus(string message, bool isError)
    {
        _statusText.Text       = message;
        _statusText.Foreground = isError ? SeedDialogTheme.ErrorText : SeedDialogTheme.DimText;
    }

    // ============================================================
    //  ツールバーの操作
    // ============================================================

    /// <summary>すべてのブロックで「取り込み元」を採る。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnAllIncoming(object sender, RoutedEventArgs e)
        => ApplyToAll(MergeBlockChoice.IncomingOnly);

    /// <summary>すべてのブロックで「現在」を採る。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnAllCurrent(object sender, RoutedEventArgs e)
        => ApplyToAll(MergeBlockChoice.CurrentOnly);

    /// <summary>すべてのブロックで両方を採る。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnAllBoth(object sender, RoutedEventArgs e)
        => ApplyToAll(MergeBlockChoice.Both);

    /// <summary>全ブロックへ同じ選択を当てる。</summary>
    /// <param name="choice">当てる選択。</param>
    private void ApplyToAll(MergeBlockChoice choice)
    {
        for (var i = 0; i < _choices.Length; i++) _choices[i] = choice;
        RefreshResult();
    }

    /// <summary>1 つ前の競合ブロックへ移動する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnPreviousConflict(object sender, RoutedEventArgs e) => MoveToConflict(-1);

    /// <summary>1 つ後の競合ブロックへ移動する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnNextConflict(object sender, RoutedEventArgs e) => MoveToConflict(+1);

    /// <summary>
    /// 競合ブロックの表示位置へスクロールする。
    /// 端で止める（巻き戻すと「どこまで見たか」が分からなくなるため）。
    ///
    /// <para>
    /// ★上段 2 面は普段は連動しない（自由に別々の場所を見られる）ので、
    /// 「両面を同じブロックへ並べ直す」のはこの操作だけの役目になる。
    /// 行を揃えてある（<see cref="MergeAlignedView"/>）ため、
    /// 同じ行番号へ寄せれば両面とも同じブロックの先頭が出る。
    /// </para>
    /// </summary>
    /// <param name="step">移動量（-1 で前、+1 で次）。</param>
    private void MoveToConflict(int step)
    {
        if (_view.Blocks.Count == 0) return;

        var next = Math.Clamp(_focusedBlock + step, 0, _view.Blocks.Count - 1);
        _focusedBlock = next;

        // 行番号は 1 始まり。上段は行が揃っているので同じ行へ寄せればよい。
        var line = _view.Blocks[next].FirstRow + 1;
        _incomingEditor.ScrollToLine(line);
        _currentEditor.ScrollToLine(line);
    }

    // ============================================================
    //  確定
    // ============================================================

    /// <summary>
    /// 「マージを確定」。検査に通らなければ **書き込まずに** 理由を出す。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_committing) return;

        // ★結果エディタの中身をそのまま採る（手で直した分も確定の対象にする）。
        //   検査も同じ文字列に対して行うので、「見えているものが書かれる」。
        var text = _resultEditor.Text;

        var validation = MergeValidation.Validate(AbsolutePath, text);
        if (!validation.IsValid)
        {
            ShowStatus(
                string.Format(
                    VersionControlMessages.MERGE_EDITOR_APPLY_FAILED_FORMAT, validation.Message),
                isError: true);
            return;
        }

        // 未選択のブロックは「元へ戻す」になる。黙って捨てない。
        var unselected = MergeComposer.CountUnselected(_choices);
        if (unselected > 0 && !ConfirmUnselected(unselected)) return;

        _committing            = true;
        _applyButton.IsEnabled = false;
        try
        {
            var result = await _onCommit(text).ConfigureAwait(true);
            if (result.Succeeded)
            {
                Close();
                return;
            }

            ShowStatus(
                string.Format(
                    VersionControlMessages.MERGE_EDITOR_APPLY_FAILED_FORMAT, result.Message),
                isError: true);
        }
        catch (Exception ex)
        {
            // 確定の失敗でウィンドウごと落とさない（選択をやり直せるようにする）。
            EditorLog.Write($"[VCS] マージの確定で例外: {ex}");
            ShowStatus(
                string.Format(
                    VersionControlMessages.MERGE_EDITOR_APPLY_FAILED_FORMAT, ex.Message),
                isError: true);
        }
        finally
        {
            _committing            = false;
            _applyButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 未選択のブロックを残したまま確定してよいかを尋ねる。
    /// ヘッドレスでは必ず「いいえ」になる（EditorDialogs の約束）。
    /// </summary>
    /// <param name="unselected">未選択のブロック数。</param>
    private static bool ConfirmUnselected(int unselected)
        => EditorDialogs.Show(
               string.Format(
                   VersionControlMessages.MERGE_EDITOR_APPLY_UNSELECTED_CONFIRM_FORMAT,
                   unselected),
               VersionControlMessages.MERGE_EDITOR_APPLY_UNSELECTED_CONFIRM_TITLE,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>「キャンセル」。何も書き込まずに閉じる。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
