// ============================================================
//  ProjectSettingsWindow.Font.cs — プロジェクト設定「文字の描画」パネル（font.distance_field / msdf_coloring。2026-10-02）
//
//  【役割】
//  project_settings.json の "font" 節（FontFieldSettings）を編集する:
//    距離場     … MTSDF（既定）/ SDF（従来の 1 チャネル）
//    辺の色分け … ink trap（既定）/ simple（MTSDF のときだけ効くので、SDF を選んでいる間は隠す）
//  値の表・説明は FontFieldCatalog（ランタイムの field_settings.rs / glyph_field.rs / edge_color.rs の写し）。
//
//  【書き込み】
//  利用者が選び直した瞬間に _data.Font へ書く。既定値（mtsdf・ink_trap）を選んだらキーを書かない
//  （ランタイムの既定が変わったときに追従する）。選び直さなかった欄・手で書いた知らないキー・読めない値はそのまま残す。
//
//  【反映】ランタイムは起動時に 1 回だけ読む（キャンバスの文字の描画器を作るとき。実行中には変わらない）。
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    // ── 文言 ──────────────────────────────────────────────

    /// <summary>反映の時期・起動オプション・アトラスの大きさの説明（いつも出す）。</summary>
    private const string FontFieldApplyNote =
        "ランタイムの起動時に 1 回だけ読みます（キャンバスの文字の描画器を作るとき。実行中には変わりません）。" +
        "保存した後の次の起動から効きます: Play・パッケージ・Android の APK（APK は作り直す）。" +
        "エディタのシーンビューと 2 回目以降の Play はランタイムを使い回すので、エディタを開き直すと確実です。\n" +
        "文字のアトラスの GPU メモリはどちらも同じです（MTSDF は RGBA8 の 2048²、SDF は R8 の 4096²。どちらも 16 MiB）。" +
        "ギズモ・操作ガイドの小さな文字は、この設定によらず従来どおり SDF で描きます。\n" +
        "PC の起動オプション --font-distance-field=sdf|mtsdf は、この設定より優先します（見比べ用）。";

    // ── 状態（表示中だけ非 null）──────────────────────────────

    /// <summary>距離場の種類のコンボボックス。</summary>
    private ComboBox? _cmbFontDistanceField;

    /// <summary>辺の色分けのコンボボックス。</summary>
    private ComboBox? _cmbFontColoring;

    /// <summary>選んだ距離場の種類の説明。</summary>
    private TextBlock? _fontDistanceFieldDescription;

    /// <summary>辺の色分けの欄（行と説明。SDF を選んでいる間は隠す）。</summary>
    private StackPanel? _fontColoringSection;

    /// <summary>選んだ辺の色分けの説明。</summary>
    private TextBlock? _fontColoringDescription;

    /// <summary>設定の中身の注意（読めない値・知らないキー・オブジェクトでない節）。</summary>
    private TextBlock? _fontDataWarning;

    /// <summary>「文字の描画」パネルを構築して返す。</summary>
    /// <returns>パネル。</returns>
    private UIElement BuildFontFieldPanel()
    {
        var settings = _data.Font;

        var panel = new StackPanel();
        panel.Children.Add(BuildPanelHeader(
            "文字の描画",
            "キャンバス（2D）の文字（TextComponent）を描くときの、グリフの距離場の種類を選びます。\n" +
            "既定の MTSDF は大きな文字の角と曲線が綺麗に出ます。SDF は 2026-10-01 までの方式で、見比べと退避のために残しています。"));
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = QualitySeparatorBrush,
            Margin     = new Thickness(0, 0, 0, QualitySectionTopMargin),
        });

        // ── 設定の中身の注意（読めない値など。選び直すと消えるので毎回作り直す）──
        _fontDataWarning = BuildRenderWarning(string.Empty, 0);
        panel.Children.Add(_fontDataWarning);

        // ── 距離場の種類 ──
        _cmbFontDistanceField = NewFontChoiceCombo(FontFieldCatalog.DistanceFieldChoices,
            settings?.EffectiveDistanceField ?? FontFieldCatalog.DefaultDistanceField);
        _cmbFontDistanceField.SelectionChanged += (_, _) => OnFontDistanceFieldChanged();
        panel.Children.Add(BuildQualityRow("距離場", _cmbFontDistanceField));
        _fontDistanceFieldDescription = BuildQualityNote(string.Empty, QualityLabelColumnWidth);
        panel.Children.Add(_fontDistanceFieldDescription);

        // ── 辺の色分け（MTSDF のときだけ）──
        _fontColoringSection = new StackPanel();
        _cmbFontColoring = NewFontChoiceCombo(FontFieldCatalog.ColoringChoices,
            settings?.EffectiveColoring ?? FontFieldCatalog.DefaultColoring);
        _cmbFontColoring.SelectionChanged += (_, _) => OnFontColoringChanged();
        _fontColoringSection.Children.Add(BuildQualityRow("辺の色分け", _cmbFontColoring));
        _fontColoringDescription = BuildQualityNote(string.Empty, QualityLabelColumnWidth);
        _fontColoringSection.Children.Add(_fontColoringDescription);
        panel.Children.Add(_fontColoringSection);

        // ── 反映・書き込むキー ──
        panel.Children.Add(BuildQualityNote(FontFieldApplyNote, 0));
        panel.Children.Add(BuildQualityNote(
            $"書き込むキー: {FontFieldSettings.SectionKey}.{FontFieldSettings.DistanceFieldKey}・" +
            $"{FontFieldSettings.SectionKey}.{FontFieldSettings.ColoringKey}（既定値を選んだら書かない）。" +
            "詳しくは docs/ui_components.md の 12 章。",
            0));

        RefreshFontFieldView();
        return panel;
    }

    /// <summary>選択肢のコンボボックスを作り、値の項目を選ぶ（Tag に JSON の値を入れる）。</summary>
    /// <param name="choices">選択肢。</param>
    /// <param name="current">選ぶ値（正規化した綴り）。</param>
    /// <returns>コンボボックス。</returns>
    private static ComboBox NewFontChoiceCombo(IReadOnlyList<FontFieldChoice> choices, string current)
    {
        var combo = NewQualityCombo();
        foreach (var choice in choices)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"{choice.Label}（{choice.Value}）", Tag = choice.Value });
        }
        combo.SelectedIndex = IndexOfTag(combo, current, (a, b) => string.Equals((string)a, (string)b, System.StringComparison.Ordinal));
        return combo;
    }

    /// <summary>距離場の種類を選び直したとき（既定値ならキーを書かない）。</summary>
    private void OnFontDistanceFieldChanged()
    {
        if (_cmbFontDistanceField?.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        (_data.Font ??= new FontFieldSettings()).SetDistanceField(value);
        RefreshFontFieldView();
    }

    /// <summary>辺の色分けを選び直したとき（既定値ならキーを書かない）。</summary>
    private void OnFontColoringChanged()
    {
        if (_cmbFontColoring?.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        (_data.Font ??= new FontFieldSettings()).SetColoring(value);
        RefreshFontFieldView();
    }

    /// <summary>選んだ値に合わせて説明・辺の色分けの欄の表示・注意を合わせ直す。</summary>
    private void RefreshFontFieldView()
    {
        var distanceField = (_cmbFontDistanceField?.SelectedItem as ComboBoxItem)?.Tag as string
                            ?? FontFieldCatalog.DefaultDistanceField;
        var coloring = (_cmbFontColoring?.SelectedItem as ComboBoxItem)?.Tag as string
                       ?? FontFieldCatalog.DefaultColoring;

        if (_fontDistanceFieldDescription is not null)
        {
            _fontDistanceFieldDescription.Text = FontFieldCatalog.FindDistanceField(distanceField)?.Description ?? string.Empty;
        }
        if (_fontColoringDescription is not null)
        {
            _fontColoringDescription.Text = FontFieldCatalog.FindColoring(coloring)?.Description ?? string.Empty;
        }
        // 辺の色分けは MSDF の作り方なので、SDF を選んでいる間は関係が無い（隠す。値は消さない）
        if (_fontColoringSection is not null)
        {
            _fontColoringSection.Visibility = distanceField == FontFieldCatalog.DistanceFieldMtsdf
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (_fontDataWarning is not null)
        {
            var text = DescribeFontDataWarnings(_data.Font);
            _fontDataWarning.Text = text;
            _fontDataWarning.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// 設定の中身の注意を作る（オブジェクトでない節・読めない値・知らないキー。どれも保存で消さない）。無ければ空。
    /// </summary>
    /// <param name="settings">"font" 節（無ければ null）。</param>
    /// <returns>注意の文。</returns>
    private static string DescribeFontDataWarnings(FontFieldSettings? settings)
    {
        if (settings is null) return string.Empty;
        var lines = new List<string>();
        if (settings.UnreadableSection is { } raw)
        {
            lines.Add($"project_settings.json の {FontFieldSettings.SectionKey} がオブジェクトではありません（{raw.GetRawText()}。" +
                      "ランタイムは無視して既定で動きます）。ここで選ぶと、選んだ値のオブジェクトに置き換えます。");
        }
        if (settings.ExtraData.Count > 0)
        {
            lines.Add("ランタイムが読まないキー・値があります（既定値として扱われます。保存しても消しません。読めない値は起動ログに [SEED FONT][WARN]）: " +
                      string.Join("、", settings.ExtraData.Select(kv => $"{kv.Key}={kv.Value.GetRawText()}")));
        }
        return string.Join("\n", lines);
    }
}
