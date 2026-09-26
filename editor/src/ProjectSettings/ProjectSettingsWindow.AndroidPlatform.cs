// ============================================================
//  ProjectSettingsWindow.AndroidPlatform.cs — プロジェクト設定「Android のプラットフォーム機能（アプリ向け）」小節（W1-2）
//
//  【役割】
//  project_settings.json の "android" 節のうち、アプリ向けの 4 つを編集する（「Android アプリ情報（モバイル）」の下に並べる）。
//    features     … 機能のチェックボックス（機能の表 runtime/android/platform_features.json の順。表示名・説明も表から）
//    system_bars  … システムバーの既定の出し方（コンボ。隠す＝既定・ゲーム／出したまま＝アプリ）
//    app_category … android:appCategory（コンボ。game＝既定／productivity 等）
//    deep_links   … ディープリンクの一覧（行の追加・削除・編集。機能 deep_links が有効なときだけ出す）
//  判断と値の出し入れは WPF 非依存の AndroidPlatformSettingsEditor（単体テスト AndroidPipelineTests）。ここは画面へ当てるだけ。
//  注意・誤りはビルドと同じ関数で出し（編集のたびに更新）、誤りがあれば保存を止める（ValidateAndroidAppInputs から呼ぶ）。
//  見た目は同じパネルの「Android アプリ情報（モバイル）」小節（ProjectSettingsWindow.Android.cs）の定数とスタイルに揃える。
//  docs/android.md §25.10・docs/project_system.md。
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.Theme;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    // ── 見た目（「Android アプリ情報（モバイル）」小節の定数に揃え、足りない分だけここに置く）──────────

    /// <summary>ディープリンクの行の scheme 欄の幅の比。</summary>
    private const double AndroidDeepLinkSchemeStars = 2;

    /// <summary>ディープリンクの行の host 欄の幅の比。</summary>
    private const double AndroidDeepLinkHostStars = 3;

    /// <summary>ディープリンクの行の path_prefix 欄の幅の比。</summary>
    private const double AndroidDeepLinkPathStars = 2;

    /// <summary>ディープリンクの行の欄どうしの間。</summary>
    private const double AndroidDeepLinkColumnGap = 4;

    /// <summary>ディープリンクの行を消す「×」のアイコンの一辺（当たり判定は CloseIconButton が決める）。</summary>
    private const double AndroidDeepLinkDeleteIconSize = 10;

    /// <summary>機能の説明の字下げ（チェックボックスの箱の幅の分）。</summary>
    private const double AndroidFeatureDescriptionIndent = 20;

    /// <summary>「追加」ボタンの上の余白。</summary>
    private const double AndroidDeepLinkAddTopMargin = 4;

    /// <summary>「追加」ボタンの内側の余白（横）。</summary>
    private const double AndroidDeepLinkAddPaddingX = 10;

    /// <summary>「追加」ボタンの内側の余白（縦）。</summary>
    private const double AndroidDeepLinkAddPaddingY = 2;

    /// <summary>誤りの文字色（ダイアログの誤りの文字と同じ。Theme/SeedColorTable）。</summary>
    private static readonly SolidColorBrush AndroidProblemErrorBrush = new(SeedThemeColors.DialogErrorText);

    /// <summary>注意の文字色（通知帯のアイコンと同じ黄色。Theme/SeedColorTable）。</summary>
    private static readonly SolidColorBrush AndroidProblemWarningBrush = new(SeedThemeColors.NoticeBarIcon);

    // ── 状態（表示中だけ非 null）──────────────────────────────

    /// <summary>編集の状態（小節を作ったときに今の設定から作る）。</summary>
    private AndroidPlatformSettingsEditor? _androidPlatformEditor;

    /// <summary>ディープリンクの行を並べる入れ物。</summary>
    private StackPanel? _androidDeepLinkRows;

    /// <summary>ディープリンクの欄全体（機能 deep_links が無効なら隠す）。</summary>
    private FrameworkElement? _androidDeepLinkSection;

    /// <summary>注意・誤りの表示。</summary>
    private TextBlock? _androidPlatformProblems;

    /// <summary>
    /// 「Android のプラットフォーム機能（アプリ向け）」小節を構築して返す。
    /// </summary>
    /// <returns>小節。</returns>
    private UIElement BuildAndroidPlatformPanel()
    {
        var editor = AndroidPlatformSettingsEditor.Load(_data.Android);
        _androidPlatformEditor = editor;

        var panel = new StackPanel { Margin = new Thickness(0, AndroidSectionTopMargin, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text       = "Android のプラットフォーム機能（アプリ向け）",
            Foreground = AndroidTitleBrush,
            FontSize   = AndroidSectionTitleFontSize,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, AndroidHintBottomMargin),
        });
        panel.Children.Add(AndroidNote(
            "目覚まし・通知などの OS の機能を使うアプリだけが設定します（ゲームは空のままで構いません）。チェックした機能の権限だけが APK に入ります" +
            "（使わない機能の権限は Google Play の審査の対象になるため入れません）。変えたら APK を作り直してください。",
            new Thickness(0, 0, 0, AndroidHintBottomMargin)));

        // ── 機能のチェックボックス（機能の表の順）──
        var featureList = new StackPanel();
        foreach (var feature in editor.AvailableFeatures)
        {
            var checkBox = new CheckBox
            {
                Content   = $"{feature.Label}（{feature.Name}）",
                IsChecked = editor.IsFeatureEnabled(feature.Name),
                FontSize  = AndroidFieldFontSize,
                ToolTip   = feature.Description,
            };
            var name = feature.Name;
            checkBox.Checked   += (_, _) => OnAndroidFeatureToggled(name, true);
            checkBox.Unchecked += (_, _) => OnAndroidFeatureToggled(name, false);
            featureList.Children.Add(checkBox);
            featureList.Children.Add(AndroidNote(feature.Description,
                new Thickness(AndroidFeatureDescriptionIndent, 0, 0, AndroidHintBottomMargin)));
        }
        if (editor.UnknownFeatures.Count > 0)
        {
            featureList.Children.Add(AndroidNote(
                $"このエディタの知らない機能（保存しても消えませんが、ビルドでは無視します）: {string.Join(", ", editor.UnknownFeatures)}",
                new Thickness(0, 0, 0, AndroidHintBottomMargin)));
        }
        panel.Children.Add(AndroidLabeledRow("機能", featureList));

        // ── システムバー・アプリの分類（コンボボックス）──
        var systemBars = AndroidChoiceCombo(AndroidSystemBarsSetting.Choices, editor.SystemBarsIndex,
            value => { editor.SelectSystemBars(value); RefreshAndroidPlatformProblems(); });
        panel.Children.Add(AndroidLabeledRow("システムバー", systemBars));
        panel.Children.Add(AndroidNote(
            "起動したときのステータスバー・ナビゲーションバー。出したままにすると描画はバーの裏まで広がるので、UI は Screen.SafeArea で避けてください。",
            new Thickness(AndroidLabelColumnWidth, 0, 0, AndroidHintBottomMargin)));

        var appCategory = AndroidChoiceCombo(AndroidAppCategorySetting.Choices, editor.AppCategoryIndex,
            value => { editor.SelectAppCategory(value); RefreshAndroidPlatformProblems(); });
        panel.Children.Add(AndroidLabeledRow("アプリの分類", appCategory));
        panel.Children.Add(AndroidNote(
            "マニフェストの android:appCategory（端末の電池・データの内訳などの分類）。game 以外では、大画面の端末で向きの固定が無視されることがあります（Android 16）。",
            new Thickness(AndroidLabelColumnWidth, 0, 0, AndroidHintBottomMargin)));

        // ── ディープリンク（機能 deep_links が有効なときだけ出す）──
        _androidDeepLinkSection = BuildAndroidDeepLinkSection();
        panel.Children.Add(_androidDeepLinkSection);

        // ── 注意・誤り（ビルドと同じ判定。編集のたびに更新）──
        _androidPlatformProblems = new TextBlock
        {
            FontSize     = AndroidNoteFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(AndroidLabelColumnWidth, AndroidRowBottomMargin, 0, 0),
        };
        panel.Children.Add(_androidPlatformProblems);

        RefreshAndroidDeepLinkVisibility();
        RefreshAndroidPlatformProblems();
        return panel;
    }

    /// <summary>ディープリンクの欄（見出し・行・追加ボタン・説明）を作る。</summary>
    private FrameworkElement BuildAndroidDeepLinkSection()
    {
        var content = new StackPanel();
        content.Children.Add(BuildAndroidDeepLinkGrid(
            new TextBlock { Text = "scheme", Foreground = AndroidNoteBrush, FontSize = AndroidNoteFontSize },
            new TextBlock { Text = "host", Foreground = AndroidNoteBrush, FontSize = AndroidNoteFontSize },
            new TextBlock { Text = "path_prefix", Foreground = AndroidNoteBrush, FontSize = AndroidNoteFontSize },
            new TextBlock { Text = "autoVerify", Foreground = AndroidNoteBrush, FontSize = AndroidNoteFontSize },
            null));

        _androidDeepLinkRows = new StackPanel();
        content.Children.Add(_androidDeepLinkRows);
        RebuildAndroidDeepLinkRows();

        // 通常ボタン（色・ホバーは共通書式。docs/editor_ui_style.md）
        var add = new Button
        {
            Content             = "ディープリンクを追加",
            FontSize            = AndroidNoteFontSize,
            Padding             = new Thickness(AndroidDeepLinkAddPaddingX, AndroidDeepLinkAddPaddingY, AndroidDeepLinkAddPaddingX, AndroidDeepLinkAddPaddingY),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, AndroidDeepLinkAddTopMargin, 0, 0),
        };
        add.Click += (_, _) =>
        {
            _androidPlatformEditor?.AddDeepLink();
            RebuildAndroidDeepLinkRows();
            RefreshAndroidPlatformProblems();
        };
        content.Children.Add(add);
        content.Children.Add(AndroidNote(
            "1 行が 1 つの intent-filter（VIEW・DEFAULT・BROWSABLE）になります。scheme と host は小文字、path_prefix は / で始め、使うなら host も書きます。" +
            "autoVerify は https と host があるときだけ検証されます（サイトに assetlinks.json が要ります）。",
            new Thickness(0, AndroidRowBottomMargin, 0, AndroidHintBottomMargin)));
        return AndroidLabeledRow("ディープリンク", content);
    }

    /// <summary>ディープリンクの行を今の編集の状態から作り直す。</summary>
    private void RebuildAndroidDeepLinkRows()
    {
        if (_androidDeepLinkRows is null || _androidPlatformEditor is null) return;
        _androidDeepLinkRows.Children.Clear();
        var links = _androidPlatformEditor.DeepLinks;
        for (var index = 0; index < links.Count; index++)
        {
            _androidDeepLinkRows.Children.Add(BuildAndroidDeepLinkRow(links[index], index));
        }
    }

    /// <summary>ディープリンク 1 行（scheme・host・path_prefix・autoVerify・削除）を作る。</summary>
    private UIElement BuildAndroidDeepLinkRow(AndroidDeepLinkSetting link, int index)
    {
        var scheme = AndroidDeepLinkTextBox(link.Scheme, text => link.Scheme = text);
        var host = AndroidDeepLinkTextBox(link.Host, text => link.Host = text);
        var path = AndroidDeepLinkTextBox(link.PathPrefix, text => link.PathPrefix = text);
        var verify = new CheckBox
        {
            IsChecked         = link.AutoVerify,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Android App Links の検証（android:autoVerify）",
        };
        verify.Checked   += (_, _) => { link.AutoVerify = true; RefreshAndroidPlatformProblems(); };
        verify.Unchecked += (_, _) => { link.AutoVerify = false; RefreshAndroidPlatformProblems(); };
        var remove = CloseIconButton.Create(
            AndroidDeepLinkDeleteIconSize,
            tooltip: "このディープリンクを消す",
            onClick: () =>
            {
                _androidPlatformEditor?.RemoveDeepLinkAt(index);
                RebuildAndroidDeepLinkRows();
                RefreshAndroidPlatformProblems();
            });
        return BuildAndroidDeepLinkGrid(scheme, host, path, verify, remove);
    }

    /// <summary>ディープリンクの見出し・行の共通の格子（列の幅をそろえる）。</summary>
    private static Grid BuildAndroidDeepLinkGrid(
        FrameworkElement scheme, FrameworkElement host, FrameworkElement path, FrameworkElement verify, FrameworkElement? remove)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, AndroidRowBottomMargin) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AndroidDeepLinkSchemeStars, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AndroidDeepLinkHostStars, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AndroidDeepLinkPathStars, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cells = new[] { scheme, host, path, verify, remove };
        for (var column = 0; column < cells.Length; column++)
        {
            if (cells[column] is not { } cell) continue;
            cell.Margin = new Thickness(0, 0, AndroidDeepLinkColumnGap, 0);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>ディープリンクの欄 1 つ（入力のたびに行の値と注意を更新する）。</summary>
    private TextBox AndroidDeepLinkTextBox(string? value, System.Action<string?> assign)
    {
        var textBox = new TextBox
        {
            Text     = value ?? string.Empty,
            FontSize = AndroidFieldFontSize,
            Style    = (Style)Resources["SettingTextBox"],
        };
        textBox.TextChanged += (_, _) =>
        {
            assign(AndroidAppSettings.NormalizeText(textBox.Text));
            RefreshAndroidPlatformProblems();
        };
        return textBox;
    }

    /// <summary>機能のチェックボックスが切り替わった。</summary>
    private void OnAndroidFeatureToggled(string name, bool enabled)
    {
        _androidPlatformEditor?.SetFeatureEnabled(name, enabled);
        RefreshAndroidDeepLinkVisibility();
        RefreshAndroidPlatformProblems();
    }

    /// <summary>ディープリンクの欄を、機能 deep_links が有効なときだけ出す（無関係な欄は隠す方針）。</summary>
    private void RefreshAndroidDeepLinkVisibility()
    {
        if (_androidDeepLinkSection is null || _androidPlatformEditor is null) return;
        _androidDeepLinkSection.Visibility = _androidPlatformEditor.DeepLinkFeatureEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>注意・誤りの表示を今の編集の状態で更新する（誤りがあれば誤りの色）。</summary>
    private void RefreshAndroidPlatformProblems()
    {
        if (_androidPlatformProblems is null || _androidPlatformEditor is null) return;
        var lines = _androidPlatformEditor.DescribeProblems();
        _androidPlatformProblems.Text = string.Join("\n", lines);
        _androidPlatformProblems.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _androidPlatformProblems.Foreground = lines.Any(line => line.StartsWith(AndroidPlatformSettingsEditor.ErrorPrefix, System.StringComparison.Ordinal))
            ? AndroidProblemErrorBrush
            : AndroidProblemWarningBrush;
    }

    /// <summary>
    /// 編集の結果を android 節へ書く（小節を表示していなければ何もしない。CollectAndroidAppSettings から呼ぶ）。
    /// </summary>
    /// <param name="android">書き込む先。</param>
    private void CollectAndroidPlatformSettings(AndroidAppSettings android) => _androidPlatformEditor?.ApplyTo(android);

    /// <summary>保存前の検査（ビルドと同じ規則。小節を表示していなければ空）。</summary>
    /// <returns>誤りの説明。</returns>
    private IReadOnlyList<string> ValidateAndroidPlatformInputs() =>
        _androidPlatformEditor?.Validate() ?? System.Array.Empty<string>();

    /// <summary>選択肢のコンボボックス（Tag に値。選び直したら onSelected を呼ぶ）。</summary>
    private static ComboBox AndroidChoiceCombo((string Value, string Label)[] choices, int selectedIndex, System.Action<string> onSelected)
    {
        // 配色はアプリ共通のダークテーマ暗黙スタイル（App.xaml）に任せる（「画面の向き」と同じ）
        var combo = new ComboBox { FontSize = AndroidFieldFontSize };
        foreach (var (value, label) in choices)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        combo.SelectedIndex = selectedIndex;
        // 初期選択の後に購読する（開いただけで「選び直した」ことにしない。書かれていた知らない値を消さないため）
        combo.SelectionChanged += (_, _) =>
        {
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is string value) onSelected(value);
        };
        return combo;
    }

    /// <summary>ラベル列＋中身の行（同じパネルの他の行と同じ列幅）。</summary>
    private static Grid AndroidLabeledRow(string label, UIElement content)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, AndroidRowBottomMargin) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AndroidLabelColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock
        {
            Text              = label,
            Foreground        = AndroidLabelBrush,
            FontSize          = AndroidFieldFontSize,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }

    /// <summary>説明の文（小さく暗い文字）。</summary>
    private static TextBlock AndroidNote(string text, Thickness margin) => new()
    {
        Text         = text,
        Foreground   = AndroidNoteBrush,
        FontSize     = AndroidNoteFontSize,
        TextWrapping = TextWrapping.Wrap,
        Margin       = margin,
    };
}
