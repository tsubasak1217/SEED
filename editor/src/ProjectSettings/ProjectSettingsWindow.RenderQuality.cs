// ============================================================
//  ProjectSettingsWindow.RenderQuality.cs — プロジェクト設定「レンダリング品質」パネル（Android 段階D-2）
//
//  【役割】
//  project_settings.json の "render_quality" 節（RenderQualitySettings）を編集する。プラットフォーム
//  （デスクトップ／Android）ごとに次を選ぶ:
//    - プリセット    … 「既定」（プラットフォームの既定＝デスクトップ desktop／Android mobile）か、定義済みのプリセット
//    - 描画スケール  … 「プリセットのまま」か 0.5〜1.0 の上書き
//    - 影            … 「プリセットのまま」「描く」「描かない」
//  プリセットの一覧・中身は runtime/config/render_presets.json（RenderQualityPresetCatalog が埋め込みを読む）。
//  他のつまみ（gi・deferred・bloom 等）の上書きは画面に出さず、手で書いた値は保存で失わない（ExtraData）。
//
//  【反映】ランタイムは起動時に 1 回だけ読む。Android は project_settings.json が APK の pak に入るので、
//  変えたら APK を作り直すと反映される（実行ボタン・SeedAndroid の run は入力の変化を見て作り直す）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    // ── 見た目（「解像度設定」パネルの小節と揃える）──────────────────

    /// <summary>ラベル列の幅（同じ流儀の他のパネルと同じ）。</summary>
    private const double QualityLabelColumnWidth = 120;

    /// <summary>コンボボックスの幅。</summary>
    private const double QualityComboWidth = 260;

    /// <summary>小節の上の余白。</summary>
    private const double QualitySectionTopMargin = 16;

    /// <summary>小節の見出しの文字の大きさ。</summary>
    private const double QualitySectionTitleFontSize = 13;

    /// <summary>ラベル・入力欄の文字の大きさ。</summary>
    private const double QualityFieldFontSize = 12;

    /// <summary>説明の文字の大きさ。</summary>
    private const double QualityNoteFontSize = 11;

    /// <summary>行の下の余白。</summary>
    private const double QualityRowBottomMargin = 6;

    /// <summary>見出しの下の余白。</summary>
    private const double QualityTitleBottomMargin = 8;

    /// <summary>見出しの文字色（他のパネルの小節の見出しと同じ）。</summary>
    private static readonly SolidColorBrush QualityTitleBrush = new(Color.FromRgb(0xDD, 0xDD, 0xDD));

    /// <summary>ラベルの文字色。</summary>
    private static readonly SolidColorBrush QualityLabelBrush = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>説明の文字色。</summary>
    private static readonly SolidColorBrush QualityNoteBrush = new(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>区切り線の色（他のパネルと同じ）。</summary>
    private static readonly SolidColorBrush QualitySeparatorBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));

    /// <summary>描画スケールの選択肢（上書きするとき。ランタイムの値域 0.5〜1.0 の中）。</summary>
    private static readonly double[] RenderScaleChoices = { 1.0, 0.9, 0.8, 0.75, 0.7, 0.6, 0.5 };

    /// <summary>画面に出すプラットフォーム（節の名前, 見出し）。</summary>
    private static readonly (string Key, string Title)[] QualityPlatforms =
    {
        (RenderQualitySettings.DesktopKey, "デスクトップ（Windows）"),
        (RenderQualitySettings.AndroidKey, "Android"),
    };

    /// <summary>プラットフォームごとの入力欄（表示中だけ。CollectSettingsFromUi で値を集める）。</summary>
    private readonly Dictionary<string, QualityPlatformControls> _qualityControls = new();

    /// <summary>1 プラットフォームぶんの入力欄。Tag にはそれぞれの設定値（null ＝ 既定・プリセットのまま）を入れる。</summary>
    /// <param name="Preset">プリセットのコンボボックス（Tag: プリセット名 / null）。</param>
    /// <param name="RenderScale">描画スケールのコンボボックス（Tag: double / null）。</param>
    /// <param name="Shadows">影のコンボボックス（Tag: bool / null）。</param>
    private sealed record QualityPlatformControls(ComboBox Preset, ComboBox RenderScale, ComboBox Shadows);

    /// <summary>「レンダリング品質」パネルを構築して返す。</summary>
    /// <returns>パネル。</returns>
    private UIElement BuildRenderQualityPanel()
    {
        _qualityControls.Clear();
        var panel = new StackPanel();
        panel.Children.Add(BuildPanelHeader(
            "レンダリング品質",
            "端末の性能に合わせて描画を軽くする「描画品質プリセット」を、プラットフォームごとに選びます。\n" +
            "既定はデスクトップが「デスクトップ（従来どおり）」＝何も下げない、Android が「モバイル（軽量）」です。"));
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = QualitySeparatorBrush,
            Margin     = new Thickness(0, 0, 0, QualitySectionTopMargin),
        });

        var settings = _data.RenderQuality;
        foreach (var (key, title) in QualityPlatforms)
        {
            panel.Children.Add(BuildQualityPlatformSection(key, title, settings?.Get(key)));
        }

        panel.Children.Add(BuildQualityNote(
            "描画スケールはゲーム画面（3D）だけを縮小して描いて画面へ拡大し、UI（キャンバス）は画面の解像度のまま重ねます。" +
            "スクリプトの Screen.Width・Input.MousePosition・タッチ・安全領域の座標はスケールで変わりません。\n" +
            "描画スケール・影以外のつまみ（gi・deferred・bloom 等）は project_settings.json の render_quality に直接書けます" +
            "（キーの一覧は docs/rendering_roadmap.md の「描画品質プリセット」）。\n" +
            "ゲームの起動時にだけ読まれます。Android は APK に入るので、変えたら APK を作り直すと反映されます。",
            0));
        return panel;
    }

    /// <summary>1 プラットフォームぶんの小節（プリセット・描画スケール・影）を作る。</summary>
    /// <param name="platformKey">節の名前。</param>
    /// <param name="title">見出し。</param>
    /// <param name="current">今の節（null ＝ 何も指定なし）。</param>
    /// <returns>小節。</returns>
    private UIElement BuildQualityPlatformSection(string platformKey, string title, RenderQualityPlatformSettings? current)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, QualitySectionTopMargin) };
        section.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = QualityTitleBrush,
            FontSize   = QualitySectionTitleFontSize,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, QualityTitleBottomMargin),
        });

        // ── プリセット ──
        var defaultName = RenderQualitySettings.DefaultPresetFor(platformKey);
        var defaultLabel = RenderQualityPresetCatalog.Find(defaultName)?.Label ?? defaultName;
        var presetCombo = NewQualityCombo();
        presetCombo.Items.Add(new ComboBoxItem { Content = $"既定：{defaultLabel}", Tag = null });
        foreach (var preset in RenderQualityPresetCatalog.Presets)
        {
            presetCombo.Items.Add(new ComboBoxItem { Content = $"{preset.Label}（{preset.Name}）", Tag = preset.Name });
        }
        // 一覧に無い名前（手で書いた・新しいエンジンの名前）も選択肢として残す（保存で消さない）
        var currentPreset = RenderQualityPlatformSettings.NormalizePreset(current?.Preset);
        if (currentPreset is not null && RenderQualityPresetCatalog.Find(currentPreset) is null)
        {
            presetCombo.Items.Add(new ComboBoxItem { Content = $"{currentPreset}（このエディタの一覧に無い名前）", Tag = currentPreset });
        }
        presetCombo.SelectedIndex = IndexOfTag(presetCombo, currentPreset, (a, b) =>
            string.Equals((string)a, (string)b, System.StringComparison.OrdinalIgnoreCase));
        section.Children.Add(BuildQualityRow("プリセット", presetCombo));

        // 選んだプリセットの説明とつまみ（選択を変えると更新）
        var presetNote = BuildQualityNote(string.Empty, QualityLabelColumnWidth);
        void RefreshPresetNote()
        {
            var name = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? defaultName;
            var preset = RenderQualityPresetCatalog.Find(name);
            presetNote.Text = preset is null
                ? $"プリセット {name} の中身はこのエディタでは分かりません（ランタイムが知らなければ既定の {defaultName} になります）。"
                : $"{preset.Description}\n中身: {RenderQualityPresetCatalog.DescribeKnobs(preset.Knobs)}";
        }
        presetCombo.SelectionChanged += (_, _) => RefreshPresetNote();
        RefreshPresetNote();
        section.Children.Add(presetNote);

        // ── 描画スケール ──
        var scaleCombo = NewQualityCombo();
        scaleCombo.Items.Add(new ComboBoxItem { Content = "プリセットのまま", Tag = null });
        foreach (var choice in RenderScaleChoices)
        {
            scaleCombo.Items.Add(new ComboBoxItem { Content = FormatScale(choice), Tag = choice });
        }
        var currentScale = RenderQualityPlatformSettings.ClampRenderScale(current?.RenderScale);
        if (currentScale is double scale && !RenderScaleChoices.Contains(scale))
        {
            // 手で書いた中間の値（0.65 等）も選択肢として残す
            scaleCombo.Items.Add(new ComboBoxItem { Content = FormatScale(scale), Tag = scale });
        }
        scaleCombo.SelectedIndex = IndexOfTag(scaleCombo, currentScale, (a, b) => (double)a == (double)b);
        section.Children.Add(BuildQualityRow("描画スケール", scaleCombo));

        // ── 影 ──
        var shadowCombo = NewQualityCombo();
        shadowCombo.Items.Add(new ComboBoxItem { Content = "プリセットのまま", Tag = null });
        shadowCombo.Items.Add(new ComboBoxItem { Content = "描く", Tag = true });
        shadowCombo.Items.Add(new ComboBoxItem { Content = "描かない", Tag = false });
        shadowCombo.SelectedIndex = IndexOfTag(shadowCombo, current?.Shadows, (a, b) => (bool)a == (bool)b);
        section.Children.Add(BuildQualityRow("影", shadowCombo));

        _qualityControls[platformKey] = new QualityPlatformControls(presetCombo, scaleCombo, shadowCombo);
        return section;
    }

    /// <summary>
    /// 「レンダリング品質」パネルの入力値を _data.RenderQuality へ集める（パネルを表示していなければ何もしない）。
    /// 画面に出さないつまみ・知らない節は元の値のまま残す。何も無ければ節ごと消える。
    /// </summary>
    private void CollectRenderQualitySettings()
    {
        if (_qualityControls.Count == 0) return;
        var settings = _data.RenderQuality ?? new RenderQualitySettings();
        foreach (var (key, controls) in _qualityControls)
        {
            var platform = settings.Get(key) ?? new RenderQualityPlatformSettings();
            platform.Preset      = RenderQualityPlatformSettings.NormalizePreset((controls.Preset.SelectedItem as ComboBoxItem)?.Tag as string);
            platform.RenderScale = (controls.RenderScale.SelectedItem as ComboBoxItem)?.Tag is double scale
                ? RenderQualityPlatformSettings.ClampRenderScale(scale)
                : null;
            platform.Shadows     = (controls.Shadows.SelectedItem as ComboBoxItem)?.Tag is bool shadows ? shadows : null;
            settings.Set(key, platform);
        }
        _data.RenderQuality = settings.IsEmpty ? null : settings;
    }

    /// <summary>このパネルのコンボボックスを作る（配色はアプリ共通のダークテーマ暗黙スタイルに任せる）。</summary>
    /// <returns>コンボボックス。</returns>
    private static ComboBox NewQualityCombo() => new()
    {
        FontSize            = QualityFieldFontSize,
        Width               = QualityComboWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>ラベル＋入力欄の行を作る（他のパネルと同じ「ラベル列＋入力列」の Grid）。</summary>
    /// <param name="label">ラベル。</param>
    /// <param name="control">入力欄。</param>
    /// <returns>行。</returns>
    private static Grid BuildQualityRow(string label, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, QualityRowBottomMargin) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(QualityLabelColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock
        {
            Text              = label,
            Foreground        = QualityLabelBrush,
            FontSize          = QualityFieldFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    /// <summary>説明文を作る（他のパネルと同じ小さめの灰色・折り返し）。</summary>
    /// <param name="text">本文。</param>
    /// <param name="leftMargin">左の余白（ラベル列に揃えるときはその幅）。</param>
    /// <returns>説明文。</returns>
    private static TextBlock BuildQualityNote(string text, double leftMargin) => new()
    {
        Text         = text,
        Foreground   = QualityNoteBrush,
        FontSize     = QualityNoteFontSize,
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(leftMargin, 0, 0, QualityTitleBottomMargin),
    };

    /// <summary>描画スケールの表示（例「0.75 倍（画面の 75%）」「1 倍（等倍）」）。</summary>
    /// <param name="scale">スケール。</param>
    /// <returns>表示文字列。</returns>
    private static string FormatScale(double scale) =>
        scale >= RenderQualityPlatformSettings.MaxRenderScale
            ? "1 倍（等倍）"
            : $"{scale.ToString("0.##", CultureInfo.InvariantCulture)} 倍（画面の {(scale * 100).ToString("0", CultureInfo.InvariantCulture)}%）";

    /// <summary>Tag が値と一致する項目の位置を返す（null は Tag が null の項目。見つからなければ先頭）。</summary>
    /// <param name="combo">コンボボックス。</param>
    /// <param name="value">探す値。</param>
    /// <param name="equals">null でない値どうしの比べ方。</param>
    /// <returns>位置。</returns>
    private static int IndexOfTag(ComboBox combo, object? value, System.Func<object, object, bool> equals)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            var tag = (combo.Items[i] as ComboBoxItem)?.Tag;
            if (value is null ? tag is null : tag is not null && equals(tag, value)) return i;
        }
        return 0;
    }
}
