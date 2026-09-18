// ============================================================
//  BranchPickerWindow.cs — 一覧からブランチを 1 つ選ばせる汎用ダイアログ
//
//  【役割】
//  「どのブランチを取り込むか」「どのブランチを削除（アーカイブ）するか」を
//  選ばせるだけの小さなモーダル。選択肢・説明文・補足はすべて呼び出し側が渡すので、
//  この型は用途（マージ／削除）を知らない。
//
//  【なぜ入力欄ではなく一覧なのか】
//  ブランチ名を手で打たせると、打ち間違いがそのまま
//  「存在しないブランチ」の失敗になり、削除では **別のブランチを消しかねない**。
//  選ばせる形なら、除外規則（現在のブランチ・既定ブランチ）を一覧の時点で効かせられる。
//
//  【ヘッドレスでは呼ばれない】
//  モーダルはヘッドレス起動で UI スレッドを永久に止める。
//  呼び出しは必ず <see cref="SEEDEditor.Headless.EditorDialogs.ShowBranchPicker"/>
//  を経由すること（ヘッドレス時はここまで来ず null が返る）。
//
//  【XAML を使わない理由】
//  一覧 1 つとボタン 2 つしかなく、TextInputWindow と同じ理由でコードだけで組む。
//
//  【配色・寸法】
//  自前では持たない。Theme/SeedDialogTheme（色と部品）と
//  Theme/SeedButtonStyles.xaml（ボタンの状態別の見た目）に従う。
// ============================================================

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl;

namespace SEEDEditor.Dialogs;

/// <summary>
/// 一覧からブランチ名を 1 つ選ばせるモーダルダイアログ。
/// </summary>
public sealed class BranchPickerWindow : Window
{
    // ── レイアウト寸法（このダイアログ固有のもののみ）──────────────

    /// <summary>ウィンドウ幅（px）。</summary>
    private const double WINDOW_WIDTH_PX = 420;

    /// <summary>ウィンドウ高さ（px）。</summary>
    private const double WINDOW_HEIGHT_PX = 340;

    /// <summary>一覧の高さ（px）。説明文と補足の分を差し引いた残り。</summary>
    private const double LIST_HEIGHT_PX = 160;

    /// <summary>選択されたブランチ名。取り消されたときは null。</summary>
    public string? SelectedBranch { get; private set; }

    /// <summary>ブランチの一覧。</summary>
    private readonly ListBox _list;

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="title">タイトルバーに出す文字列。</param>
    /// <param name="prompt">一覧の上に出す説明文。</param>
    /// <param name="branches">選択肢（空で呼ばないこと。呼び出し側が先に確かめる）。</param>
    /// <param name="note">一覧の下に出す補足（省略可）。</param>
    public BranchPickerWindow(
        string title, string prompt, IReadOnlyList<string> branches, string? note = null)
    {
        SeedDialogTheme.ApplyWindowChrome(this, title, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        // ── 説明文 / 一覧 / 補足 / ボタン列 の 4 段 ──
        var root = new Grid { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var promptText = SeedDialogTheme.NewLabel(prompt);
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        _list = NewBranchList(branches);
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        if (!string.IsNullOrEmpty(note))
        {
            var noteText = SeedDialogTheme.NewLabel(
                note, SeedDialogTheme.DimText, SeedDialogTheme.NOTE_FONT_SIZE,
                SeedDialogTheme.ROW_SPACING_PX);
            Grid.SetRow(noteText, 2);
            root.Children.Add(noteText);
        }

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        buttons.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.PANEL_BRANCH_PICKER_OK, OnOk, isPrimary: true));
        buttons.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.PANEL_BRANCH_PICKER_CANCEL, OnCancel,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;

        // 開いた直後から矢印キーで選べるようにする。
        Loaded += (_, _) => _list.Focus();
    }

    /// <summary>
    /// ブランチ一覧を作る。色・余白・選択/ホバーの見た目は共通書式に任せる
    /// （ここで色を決めない）。
    /// </summary>
    /// <param name="branches">選択肢。</param>
    private ListBox NewBranchList(IReadOnlyList<string> branches)
    {
        var list = SeedDialogTheme.NewListBox(
            branches, LIST_HEIGHT_PX, SeedDialogTheme.ROW_SPACING_PX);

        // 最初の 1 件を選んでおく。何も選ばれていない状態で OK を押されると
        // 「押したのに閉じない」に見えるため。
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        // ダブルクリックと Enter で確定できるようにする（一覧の定石）。
        // MouseDoubleClick は MouseButtonEventHandler なので、そのままでは
        // RoutedEventHandler の OnOk を渡せない（引数の型は互換でも delegate 型が別）。
        list.MouseDoubleClick += (s, args) => OnOk(s, args);
        list.KeyDown          += OnListKeyDown;
        return list;
    }

    /// <summary>Enter で確定、Escape で取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>決定。何も選ばれていなければ閉じない。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string name } || name.Length == 0)
        {
            _list.Focus();
            return;
        }

        SelectedBranch = name;
        DialogResult   = true;
    }

    /// <summary>取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        SelectedBranch = null;
        DialogResult   = false;
    }
}
