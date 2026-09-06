using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Audio;

namespace SEEDEditor.Dialogs;

/// <summary>
/// 音声ファイルの無音カット設定ダイアログ。
///
/// しきい値・余白・末尾カット・保存方法を入力させ、OK で <see cref="Options"/> に確定する。
/// 実処理は行わず（<see cref="AudioSilenceTrimmer"/> が担当）、入力収集だけに責務を限定する。
/// </summary>
public sealed class AudioSilenceTrimWindow : Window
{
    // ── 配色（エディタの他ダイアログと揃える）────────────────────
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush FieldBrush      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush TextBrush       = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly Brush BorderBrush2    = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush DimTextBrush    = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    // ── レイアウト寸法 ────────────────────────────────────────────
    /// <summary>ウィンドウ幅（px）。</summary>
    private const double WindowWidthPx = 420;

    /// <summary>ウィンドウ高さ（px）。</summary>
    private const double WindowHeightPx = 300;

    /// <summary>ラベル列の幅（px）。</summary>
    private const double LabelWidthPx = 110;

    /// <summary>数値入力欄の幅（px）。</summary>
    private const double NumericBoxWidthPx = 80;

    /// <summary>行間の余白（px）。</summary>
    private const double RowSpacingPx = 6;

    /// <summary>外周の余白（px）。</summary>
    private const double ContentPaddingPx = 14;

    /// <summary>ボタンの幅（px）。</summary>
    private const double ButtonWidthPx = 84;

    // ── 入力コントロール ──────────────────────────────────────────
    private readonly TextBox    _thresholdBox;
    private readonly TextBox    _paddingBox;
    private readonly CheckBox   _trimTrailingCheck;
    private readonly RadioButton _overwriteRadio;
    private readonly RadioButton _saveAsRadio;

    /// <summary>OK が押されたときに確定した設定。キャンセル時は null。</summary>
    public AudioTrimOptions? Options { get; private set; }

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="targetFileName">対象ファイル名（タイトル下に表示する）。</param>
    public AudioSilenceTrimWindow(string targetFileName)
    {
        Title                 = "先頭の無音をカット";
        Width                 = WindowWidthPx;
        Height                = WindowHeightPx;
        Background            = BackgroundBrush;
        ResizeMode            = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;

        var root = new StackPanel { Margin = new Thickness(ContentPaddingPx) };

        // 対象ファイル名（何に対する操作かを明示する）
        root.Children.Add(new TextBlock
        {
            Text       = targetFileName,
            Foreground = DimTextBrush,
            Margin     = new Thickness(0, 0, 0, ContentPaddingPx),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // しきい値（dB）
        _thresholdBox = MakeNumericBox(AudioTrimOptions.DefaultThresholdDb);
        root.Children.Add(MakeRow("しきい値 (dB)", _thresholdBox,
            "この音量を超えた所を音の始まりとみなす"));

        // 余白（ms）
        _paddingBox = MakeNumericBox(AudioTrimOptions.DefaultPaddingMs);
        root.Children.Add(MakeRow("余白 (ms)", _paddingBox,
            "カット位置を音の手前へ戻す量"));

        // 末尾もカット
        _trimTrailingCheck = new CheckBox
        {
            Content    = "末尾の無音もカットする",
            Foreground = TextBrush,
            IsChecked  = AudioTrimOptions.DefaultTrimTrailing,
            Margin     = new Thickness(0, RowSpacingPx, 0, ContentPaddingPx),
        };
        root.Children.Add(_trimTrailingCheck);

        // 保存方法
        root.Children.Add(new TextBlock
        {
            Text       = "保存方法",
            Foreground = TextBrush,
            Margin     = new Thickness(0, 0, 0, RowSpacingPx / 2),
        });

        const string saveModeGroup = "SaveMode";
        _overwriteRadio = new RadioButton
        {
            Content    = "上書き（元は .bak に退避）",
            GroupName  = saveModeGroup,
            Foreground = TextBrush,
            IsChecked  = AudioTrimOptions.DefaultSaveMode == AudioTrimSaveMode.Overwrite,
            Margin     = new Thickness(0, 0, 0, RowSpacingPx / 2),
        };
        _saveAsRadio = new RadioButton
        {
            Content    = "別名で保存（<name>_trim.<ext>）",
            GroupName  = saveModeGroup,
            Foreground = TextBrush,
            IsChecked  = AudioTrimOptions.DefaultSaveMode == AudioTrimSaveMode.SaveAs,
        };
        root.Children.Add(_overwriteRadio);
        root.Children.Add(_saveAsRadio);

        // OK / キャンセル
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, ContentPaddingPx, 0, 0),
        };
        var okButton = new Button
        {
            Content   = "OK",
            Width     = ButtonWidthPx,
            IsDefault = true,
            Margin    = new Thickness(0, 0, RowSpacingPx, 0),
        };
        okButton.Click += OnOkClicked;
        var cancelButton = new Button
        {
            Content  = "キャンセル",
            Width    = ButtonWidthPx,
            IsCancel = true,
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>
    /// ラベル＋入力欄＋補足説明の 1 行を作る。
    /// </summary>
    /// <param name="label">左側のラベル文字列。</param>
    /// <param name="input">入力コントロール。</param>
    /// <param name="hint">右側に薄字で出す補足説明。</param>
    private static UIElement MakeRow(string label, UIElement input, string hint)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, RowSpacingPx),
        };
        row.Children.Add(new TextBlock
        {
            Text              = label,
            Width             = LabelWidthPx,
            Foreground        = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(input);
        row.Children.Add(new TextBlock
        {
            Text              = hint,
            Foreground        = DimTextBrush,
            Margin            = new Thickness(RowSpacingPx, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>
    /// 数値入力欄を作る。
    /// </summary>
    /// <param name="initialValue">初期値。</param>
    private static TextBox MakeNumericBox(double initialValue) => new()
    {
        Text            = initialValue.ToString(CultureInfo.InvariantCulture),
        Width           = NumericBoxWidthPx,
        Background      = FieldBrush,
        Foreground      = TextBrush,
        BorderBrush     = BorderBrush2,
        Padding         = new Thickness(4, 2, 4, 2),
        TextAlignment   = TextAlignment.Right,
    };

    /// <summary>
    /// OK 押下時。入力値を検証して <see cref="Options"/> を確定する。
    /// 数値として解釈できない欄があれば閉じずに知らせる。
    /// </summary>
    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(_thresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double thresholdDb))
        {
            MessageBox.Show(this, "しきい値には数値（例: -45）を入力してください。", Title,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(_paddingBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double paddingMs) || paddingMs < 0)
        {
            MessageBox.Show(this, "余白には 0 以上の数値（ミリ秒）を入力してください。", Title,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Options = new AudioTrimOptions
        {
            ThresholdDb  = thresholdDb,
            PaddingMs    = paddingMs,
            TrimTrailing = _trimTrailingCheck.IsChecked == true,
            SaveMode     = _saveAsRadio.IsChecked == true
                ? AudioTrimSaveMode.SaveAs
                : AudioTrimSaveMode.Overwrite,
        };

        DialogResult = true;
    }
}
