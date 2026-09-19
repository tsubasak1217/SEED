// ============================================================
//  ConflictSidePickerWindow.cs — 並べて表示できない競合の 2 択ダイアログ
//
//  【役割】
//  バイナリ（png / blend など）や、競合の印が入り得ないファイルは
//  マージエディタで開けない。そのときに「どちらの内容を残すか」だけを尋ねる小さなモーダル。
//
//  【なぜこれが要るのか】
//  競合の節の行からボタンを全部外した（2026-09-19 の指摘。ボタンが幅を食って
//  ファイル名が見えなかった）ため、解決の入口は**行のダブルクリック**だけになった。
//  開けないファイルでそのまま「開けません」と言って終わると、
//  バイナリの競合を解決する手段が一つも無くなる。その受け皿がここ。
//
//  【呼び名は呼び出し側が決める】
//  「現在 / 取り込み元」の呼び名は進行中のマージで変わる
//  （sync なら「自分の変更 / リモート」、ブランチのマージならブランチ名）。
//  推測をこの画面に持ち込まないよう、文言は組み立て済みのものを受け取る。
//
//  【ヘッドレスでは呼ばれない】
//  モーダルはヘッドレス起動で UI スレッドを永久に止める。
//  呼び出しは必ず <see cref="SEEDEditor.Headless.EditorDialogs.ShowConflictSidePicker"/>
//  を経由すること（ヘッドレス時はここまで来ず「キャンセル」が返る）。
//
//  【XAML を使わない理由】
//  文字 2 つとボタン 3 つしかなく、BranchPickerWindow / TextInputWindow と同じ理由で
//  コードだけで組む（StaticResource の綴り間違いがビルドを通ってしまうのを避ける）。
//
//  【配色・寸法】
//  自前では持たない。Theme/SeedDialogTheme（色と部品）と
//  Theme/SeedButtonStyles.xaml（ボタンの状態別の見た目）に従う。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Dialogs;

/// <summary>
/// 「どちらの内容を残すか」を選ばせるモーダルダイアログ。
/// </summary>
public sealed class ConflictSidePickerWindow : Window
{
    // ── レイアウト寸法（このダイアログ固有のもののみ）──────────────

    /// <summary>ウィンドウ幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 480;

    /// <summary>ウィンドウ高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 220;

    /// <summary>利用者が選んだ側。閉じられただけなら「キャンセル」のまま。</summary>
    public ConflictSidePick Pick { get; private set; } = ConflictSidePick.Cancel;

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="body">本文（「並べて表示できません（理由）。どちらを…」）。</param>
    /// <param name="path">対象のファイルのパス（どれを解決しているのか分かるように出す）。</param>
    /// <param name="keepCurrentText">「現在」を残すボタンの文言。</param>
    /// <param name="takeIncomingText">「取り込み元」を採るボタンの文言。</param>
    public ConflictSidePickerWindow(
        string body, string path, string keepCurrentText, string takeIncomingText)
    {
        SeedDialogTheme.ApplyWindowChrome(
            this, VersionControlMessages.PANEL_CONFLICT_PICK_SIDE_TITLE,
            WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        // ── 本文 / パス / ボタン列 の 3 段 ──
        var root = new Grid { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });

        var bodyText = SeedDialogTheme.NewLabel(body);
        Grid.SetRow(bodyText, 0);
        root.Children.Add(bodyText);

        // パスは補足色。長いので折り返す（NewLabel は既定で折り返す）。
        var pathText = SeedDialogTheme.NewLabel(
            path, SeedDialogTheme.DimText, SeedDialogTheme.NOTE_FONT_SIZE,
            SeedDialogTheme.ROW_SPACING_PX);
        Grid.SetRow(pathText, 1);
        root.Children.Add(pathText);

        // 並びは「消えない側 → 消える側 → 取り消し」。
        // 主操作は「現在を残す」側にする（取り返しがつくのはこちらだけ）。
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        buttons.Children.Add(SeedDialogTheme.NewButton(
            keepCurrentText, OnKeepCurrent, isPrimary: true));
        buttons.Children.Add(SeedDialogTheme.NewButton(
            takeIncomingText, OnTakeIncoming, leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        buttons.Children.Add(SeedDialogTheme.NewButton(
            VersionControlMessages.PANEL_CONFLICT_PICK_SIDE_CANCEL, OnCancel,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;

        // Escape で取り消せるようにする（ダイアログの定石）。
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Escape で取り消す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        OnCancel(sender, e);
        e.Handled = true;
    }

    /// <summary>「現在」側を残す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnKeepCurrent(object sender, RoutedEventArgs e)
    {
        Pick         = ConflictSidePick.KeepCurrent;
        DialogResult = true;
    }

    /// <summary>「取り込み元」側を採用する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnTakeIncoming(object sender, RoutedEventArgs e)
    {
        Pick         = ConflictSidePick.TakeIncoming;
        DialogResult = true;
    }

    /// <summary>取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Pick         = ConflictSidePick.Cancel;
        DialogResult = false;
    }
}
