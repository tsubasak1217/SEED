// ============================================================
//  ProjectSettingsWindow.Android.cs — プロジェクト設定「Android アプリ情報（モバイル）」小節
//
//  【役割】
//  project_settings.json の "android" 節（application_id / app_name / version_code / version_name。
//  AndroidAppSettings）を編集する入力欄。「解像度設定」パネルの「画面の向き（モバイル）」の下に並べる
//  （どちらも Android の APK を作るときに焼き込まれる値）。
//
//  【空欄の意味】
//  空欄は「既定値を使う」（節から消える）。既定値はビルドのときと同じ関数（AndroidAppIdentityResolver）で作り、
//  各欄の下に表示する（ID は .seedproj の名前から com.seedengine.<英数字化した名前>、名前はプロジェクトの表示名、
//  版は 1 / "1.0"）。値の検査もビルドと同じ関数で行い、誤りがあれば保存を止める。
//
//  【アイコン（段階D）】icon（元の PNG。アセットルートからの相対パスか絶対パス）と icon_background（#RRGGBB）。
//  ビルドのときに各密度の mipmap とアダプティブアイコンを生成する（editor/src/Android/Icons/。検査は LauncherIconSettings）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Project;
using SEEDEditor.Project;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    // ── 見た目（同じパネルの「画面の向き（モバイル）」小節と揃える）──────────

    /// <summary>小節の上の余白。</summary>
    private const double AndroidSectionTopMargin = 16;

    /// <summary>小節の見出しの文字の大きさ。</summary>
    private const double AndroidSectionTitleFontSize = 13;

    /// <summary>ラベル・入力欄の文字の大きさ。</summary>
    private const double AndroidFieldFontSize = 12;

    /// <summary>既定値・説明の文字の大きさ。</summary>
    private const double AndroidNoteFontSize = 11;

    /// <summary>ラベル列の幅（同じパネルの他の行と同じ）。</summary>
    private const double AndroidLabelColumnWidth = 120;

    /// <summary>行の下の余白。</summary>
    private const double AndroidRowBottomMargin = 2;

    /// <summary>既定値の表示の下の余白。</summary>
    private const double AndroidHintBottomMargin = 8;

    /// <summary>見出しの文字色（同じパネルの小節の見出しと同じ）。</summary>
    private static readonly SolidColorBrush AndroidTitleBrush = new(Color.FromRgb(0xDD, 0xDD, 0xDD));

    /// <summary>ラベルの文字色。</summary>
    private static readonly SolidColorBrush AndroidLabelBrush = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>既定値・説明の文字色。</summary>
    private static readonly SolidColorBrush AndroidNoteBrush = new(Color.FromRgb(0x66, 0x66, 0x66));

    // ── 入力欄（表示中だけ非 null。CollectSettingsFromUi で値を集める）──────────

    /// <summary>アプリ ID の入力欄。</summary>
    private TextBox? _tbAndroidApplicationId;

    /// <summary>アプリ名の入力欄。</summary>
    private TextBox? _tbAndroidAppName;

    /// <summary>バージョン番号（整数）の入力欄。</summary>
    private TextBox? _tbAndroidVersionCode;

    /// <summary>バージョン名の入力欄。</summary>
    private TextBox? _tbAndroidVersionName;

    /// <summary>アイコンの元の PNG の入力欄（段階D）。</summary>
    private TextBox? _tbAndroidIcon;

    /// <summary>アイコンの背景色の入力欄（段階D）。</summary>
    private TextBox? _tbAndroidIconBackground;

    /// <summary>
    /// バージョン番号の欄に入力された文字列（整数として読めなかったものも保存前の検査のために持つ）。
    /// null はまだ欄を表示していない。
    /// </summary>
    private string? _androidVersionCodeText;

    /// <summary>
    /// 「Android アプリ情報（モバイル）」小節を構築して返す。
    /// </summary>
    /// <returns>小節。</returns>
    private UIElement BuildAndroidAppPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, AndroidSectionTopMargin, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text       = "Android アプリ情報（モバイル）",
            Foreground = AndroidTitleBrush,
            FontSize   = AndroidSectionTitleFontSize,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, AndroidHintBottomMargin),
        });

        // 空欄のときに使われる既定値（ビルドと同じ関数で作る）
        var defaults = AndroidAppIdentityResolver.Resolve(
            null, ProjectContext.Paths?.Name, ProjectContext.Paths?.DisplayName, hasProjectContext: true);
        var android = _data.Android;

        _tbAndroidApplicationId = AddAndroidTextRow(panel, "アプリ ID", android?.ApplicationId,
            $"空欄なら {defaults.ApplicationId}（プロジェクト名から作る。名前を変えると ID も変わります）");
        _tbAndroidAppName = AddAndroidTextRow(panel, "アプリ名", android?.AppName,
            $"空欄なら {defaults.AppName.Value ?? "SEED Runtime"}（ランチャーに出る名前。先頭に @ ? は使えません）");
        _tbAndroidVersionCode = AddAndroidTextRow(panel, "バージョン番号", android?.VersionCode?.ToString(CultureInfo.InvariantCulture),
            $"空欄なら {AndroidAppIdentityResolver.DefaultVersionCode}（{AndroidAppIdentityResolver.MinVersionCode}〜{AndroidAppIdentityResolver.MaxVersionCode} の整数。ストアへ出すたびに増やす）");
        _tbAndroidVersionName = AddAndroidTextRow(panel, "バージョン名", android?.VersionName,
            $"空欄なら {AndroidAppIdentityResolver.DefaultVersionName}（人が読む版。例 1.0.3）");
        _androidVersionCodeText = _tbAndroidVersionCode.Text;
        _tbAndroidIcon = AddAndroidTextRow(panel, "アイコン（PNG）", android?.Icon,
            "空欄ならシステムの既定のアイコン。アセットルートからの相対パス（例 icons/app_icon.png）か絶対パス。" +
            $"{LauncherIconGenerator.RecommendedSourceSize}x{LauncherIconGenerator.RecommendedSourceSize} 以上の正方形を推奨（ビルドで各密度とアダプティブアイコンを生成）");
        _tbAndroidIconBackground = AddAndroidTextRow(panel, "アイコンの背景色", android?.IconBackground,
            $"空欄なら {RgbaColor.White.ToAndroidHex()}（アダプティブアイコンの背景。#RRGGBB か #AARRGGBB）");

        panel.Children.Add(new TextBlock
        {
            Text         = "APK を作るときにアプリへ書き込まれるので、変えたら APK を作り直してください。\n" +
                           "端末はアプリ ID で別のアプリかを見分けます。ID を変えると別のアプリとして入り、セーブデータも引き継ぎません（配布するゲームは ID を決めておくことを勧めます）。\n" +
                           "デスクトップ（Windows）の実行には影響しません。",
            Foreground   = AndroidNoteBrush,
            FontSize     = AndroidNoteFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(AndroidLabelColumnWidth, AndroidRowBottomMargin, 0, 0),
        });
        return panel;
    }

    /// <summary>
    /// 入力欄の値を _data.Android へ集める（空欄は null＝既定値。何も無ければ節ごと消える）。
    /// バージョン番号が整数として読めないときは値を変えず、保存前の検査（<see cref="ValidateAndroidAppInputs"/>）で止める。
    /// </summary>
    private void CollectAndroidAppSettings()
    {
        if (_tbAndroidApplicationId is null || _tbAndroidAppName is null || _tbAndroidVersionCode is null || _tbAndroidVersionName is null)
        {
            return;
        }
        var android = _data.Android ?? new AndroidAppSettings();
        android.ApplicationId = AndroidAppSettings.NormalizeText(_tbAndroidApplicationId.Text);
        android.AppName       = AndroidAppSettings.NormalizeText(_tbAndroidAppName.Text);
        android.VersionName   = AndroidAppSettings.NormalizeText(_tbAndroidVersionName.Text);
        // アイコン（段階D。欄が無い古い画面の状態でも値を消さない）
        if (_tbAndroidIcon is not null) android.Icon = AndroidAppSettings.NormalizeText(_tbAndroidIcon.Text);
        if (_tbAndroidIconBackground is not null) android.IconBackground = AndroidAppSettings.NormalizeText(_tbAndroidIconBackground.Text);

        _androidVersionCodeText = _tbAndroidVersionCode.Text;
        var codeText = AndroidAppSettings.NormalizeText(_androidVersionCodeText);
        if (codeText is null)
        {
            android.VersionCode = null;
        }
        else if (int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            android.VersionCode = code;
        }
        _data.Android = android.IsEmpty ? null : android;
    }

    /// <summary>
    /// 保存前の検査（ビルドと同じ規則）。誤りがあれば説明の一覧を返す。
    /// </summary>
    /// <returns>誤りの説明（無ければ空）。</returns>
    private IReadOnlyList<string> ValidateAndroidAppInputs()
    {
        var errors = new List<string>();
        var codeText = AndroidAppSettings.NormalizeText(_androidVersionCodeText);
        if (codeText is not null && !int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            errors.Add($"バージョン番号「{codeText}」は整数ではありません。");
        }
        errors.AddRange(AndroidAppIdentityResolver.Validate(_data.Android));
        // アイコン（ビルドと同じ検査。相対パスはアセットルートから）
        errors.AddRange(LauncherIconSettings.Validate(_data.Android, _assetsPath));
        return errors;
    }

    /// <summary>ラベル＋入力欄の行と、その下の既定値の表示を足す。</summary>
    /// <param name="panel">足す先。</param>
    /// <param name="label">ラベル。</param>
    /// <param name="value">今の値（null は空欄）。</param>
    /// <param name="hint">既定値の説明。</param>
    /// <returns>入力欄。</returns>
    private TextBox AddAndroidTextRow(StackPanel panel, string label, string? value, string hint)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, AndroidRowBottomMargin) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AndroidLabelColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text              = label,
            Foreground        = AndroidLabelBrush,
            FontSize          = AndroidFieldFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var textBox = new TextBox
        {
            Text     = value ?? string.Empty,
            FontSize = AndroidFieldFontSize,
            Style    = (Style)Resources["SettingTextBox"],
        };
        Grid.SetColumn(textBox, 1);
        row.Children.Add(textBox);
        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text         = hint,
            Foreground   = AndroidNoteBrush,
            FontSize     = AndroidNoteFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(AndroidLabelColumnWidth, 0, 0, AndroidHintBottomMargin),
        });
        return textBox;
    }
}
