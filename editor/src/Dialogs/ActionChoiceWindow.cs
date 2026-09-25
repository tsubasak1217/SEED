// ============================================================
//  ActionChoiceWindow.cs — 「A する / B する / キャンセル」の 3 択ダイアログ（ボタンの文言を呼び出し側が決める）
//
//  【役割】
//  標準の MessageBox は「はい / いいえ / キャンセル」しか出せず、「保存して実行 / 保存せず実行」のように
//  押したら何が起きるかをボタンそのものに書けない。その受け皿（最初の利用者は Android で実行する前の未保存の確認。
//  AndroidRun/AndroidUnsavedChangesPrompt.cs）。
//
//  【ヘッドレスでは呼ばれない】
//  モーダルはヘッドレス起動で UI スレッドを永久に止める。呼び出しは必ず
//  <see cref="SEEDEditor.Headless.EditorDialogs.ShowActionChoice"/> を経由すること（ヘッドレス時は「キャンセル」が返る）。
//
//  【XAML を使わない理由・配色】
//  ConflictSidePickerWindow と同じ（文字 1 つとボタン 3 つだけ。色と部品は Theme/SeedDialogTheme、ボタンの状態別の
//  見た目は Theme/SeedButtonStyles.xaml に従い、自前では持たない）。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Theme;

namespace SEEDEditor.Dialogs;

/// <summary>3 択ダイアログの答え。</summary>
public enum ActionChoice
{
    /// <summary>取り消した（何もしない）。閉じる・Escape・ヘッドレスもこれ。</summary>
    Cancel,

    /// <summary>主操作（左のボタン。アクセント色）。</summary>
    Primary,

    /// <summary>もう 1 つの操作（中央のボタン）。</summary>
    Secondary,
}

/// <summary>「A する / B する / キャンセル」の 3 択ダイアログ。</summary>
public sealed class ActionChoiceWindow : Window
{
    // ── レイアウト寸法（このダイアログ固有のもののみ）──────────────

    /// <summary>ウィンドウ幅 [px]（高さは本文に合わせる）。</summary>
    private const double WINDOW_WIDTH_PX = 500;

    /// <summary>高さを本文に合わせる前の仮の高さ [px]（SizeToContent で置き換わる）。</summary>
    private const double INITIAL_HEIGHT_PX = 200;

    /// <summary>本文とボタン列の間の余白 [px]。</summary>
    private const double BUTTON_ROW_TOP_MARGIN_PX = 16;

    /// <summary>利用者の答え。閉じられただけなら「キャンセル」のまま。</summary>
    public ActionChoice Choice { get; private set; } = ActionChoice.Cancel;

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="title">タイトル。</param>
    /// <param name="body">本文。</param>
    /// <param name="primaryText">主操作のボタンの文言。</param>
    /// <param name="secondaryText">もう 1 つの操作のボタンの文言。</param>
    /// <param name="cancelText">取り消しのボタンの文言。</param>
    public ActionChoiceWindow(string title, string body, string primaryText, string secondaryText, string cancelText)
    {
        SeedDialogTheme.ApplyWindowChrome(this, title, WINDOW_WIDTH_PX, INITIAL_HEIGHT_PX);
        SizeToContent = SizeToContent.Height;

        // ── 本文 / ボタン列 の 2 段 ──
        var root = new StackPanel { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };
        root.Children.Add(SeedDialogTheme.NewLabel(body));

        // 並びは「主操作 → もう 1 つ → 取り消し」（ConflictSidePickerWindow と同じ右寄せ）
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, BUTTON_ROW_TOP_MARGIN_PX, 0, 0),
        };
        var primary = SeedDialogTheme.NewButton(primaryText, OnPrimary, isPrimary: true);
        buttons.Children.Add(primary);
        buttons.Children.Add(SeedDialogTheme.NewButton(secondaryText, OnSecondary, leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        buttons.Children.Add(SeedDialogTheme.NewButton(cancelText, OnCancel, leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        root.Children.Add(buttons);

        Content = root;

        // Enter は主操作（既定のボタン）、Escape は取り消し（ダイアログの定石）
        primary.IsDefault = true;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => primary.Focus();
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

    /// <summary>主操作。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Choice       = ActionChoice.Primary;
        DialogResult = true;
    }

    /// <summary>もう 1 つの操作。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Choice       = ActionChoice.Secondary;
        DialogResult = true;
    }

    /// <summary>取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice       = ActionChoice.Cancel;
        DialogResult = false;
    }
}
