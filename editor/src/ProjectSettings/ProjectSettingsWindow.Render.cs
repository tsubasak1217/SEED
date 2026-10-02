// ============================================================
//  ProjectSettingsWindow.Render.cs — プロジェクト設定「描画の構成」パネル（render.profile と旗の上書き。2026-10-02）
//
//  【役割】
//  project_settings.json の "render" 節（RenderProfileSettings）を編集する:
//    構成            … render_profiles.json の構成（full＝3D あり〈既定〉/ ui＝2D/UI だけ）から選ぶ。
//                      既定の構成を選ぶと "profile" を書かない（既定の変更に追従する）
//    旗の上書き      … 旗ごとに「構成のまま」（キーを書かない）/「有効」(true) /「無効」(false) の 3 状態
//    GPU メモリの確保 … 「構成のまま」/ performance / memory_usage
//  構成の一覧・説明は埋め込みの runtime/config/render_profiles.json（RenderProfileCatalog。読めなければ組み込みの
//  full / ui と警告）、旗の表は RenderProfileFlagCatalog。選んだ構成と上書きから、起動したときの実効の旗を要約して出す。
//
//  【書き込み】
//  利用者が選び直した瞬間に _data.Render へ書く（選び直さなかった欄は読んだ値のまま残す）。「保存して閉じる」で
//  ファイルへ書き、何も無ければ節ごと省く（ProjectSettingsData.SaveTo）。手で書いた知らないキー・読めない値は保つ。
//
//  【反映】ランタイムは起動時に 1 回だけ読む（資源を起動時に確保するため。実行中には変わらない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    // ── 見た目 ────────────────────────────────────────────

    /// <summary>
    /// このパネルのラベル列の幅（「G-Buffer（デファード）」「GPU メモリの確保」などが 120px に収まらないため広げる）。
    /// </summary>
    private const double RenderProfileLabelColumnWidth = 170;

    /// <summary>注意の文字色（通知帯のアイコンと同じ黄色。Theme/SeedColorTable。「文字の描画」パネルも使う）。</summary>
    private static readonly SolidColorBrush SettingsWarningBrush = new(SeedThemeColors.NoticeBarIcon);

    // ── 文言 ──────────────────────────────────────────────

    /// <summary>「構成のまま」の選択肢の書式（{0} は構成の値）。</summary>
    private const string InheritChoiceFormat = "構成のまま（{0}）";

    /// <summary>反映の時期と 3D のシーンの注意（いつも出す）。</summary>
    private const string RenderProfileApplyNote =
        "ランタイムの起動時に 1 回だけ読みます（資源を起動時に確保するため、実行中には変わりません）。" +
        "保存した後の次の起動から効きます: Play・パッケージ・Android の APK（APK は作り直す）。" +
        "エディタのシーンビューと 2 回目以降の Play はランタイムを使い回すので、エディタを開き直すと確実です。\n" +
        "3D のシーンがあるのに ui を選ぶと、3D（モデル・地形・水・天球・SEED.Draw3D）は描かれません（起動ログに [SEED RENDER PROFILE][WARN]）。";

    /// <summary>3D のシーンを描かない構成を選んでいるときの注意。</summary>
    private const string RenderProfileNo3DWarning =
        "この設定では 3D のシーンを描きません。3D のモデル・地形・水・天球・SEED.Draw3D があるプロジェクトでは、それらが消えて見えます" +
        "（エディタのシーンビューも同じ構成で描きます）。2D/UI だけのアプリ向けの設定です。";

    // ── 状態（表示中だけ非 null）──────────────────────────────

    /// <summary>構成のコンボボックス。</summary>
    private ComboBox? _cmbRenderProfile;

    /// <summary>旗のコンボボックス（キー → コンボ。先頭の項目が「構成のまま」）。</summary>
    private readonly Dictionary<string, ComboBox> _renderFlagCombos = new(StringComparer.Ordinal);

    /// <summary>memory_hint のコンボボックス（先頭の項目が「構成のまま」）。</summary>
    private ComboBox? _cmbRenderMemoryHint;

    /// <summary>選んだ構成の説明。</summary>
    private TextBlock? _renderProfileDescription;

    /// <summary>実効の旗の要約。</summary>
    private TextBlock? _renderProfileSummary;

    /// <summary>3D のシーンを描かないときの注意。</summary>
    private TextBlock? _renderProfileNo3DWarning;

    /// <summary>設定の中身の注意（オブジェクトでない節・ランタイムが読まないキー）。</summary>
    private TextBlock? _renderProfileDataWarning;

    /// <summary>画面の表示を合わせ直している間は true（選択の変化を利用者の操作として扱わない）。</summary>
    private bool _renderPanelUpdating;

    /// <summary>「描画の構成」パネルを構築して返す。</summary>
    /// <returns>パネル。</returns>
    private UIElement BuildRenderProfilePanel()
    {
        _renderFlagCombos.Clear();
        var catalog = RenderProfileCatalog.Current;
        var settings = _data.Render;

        var panel = new StackPanel();
        panel.Children.Add(BuildPanelHeader(
            "描画の構成",
            "3D の描画資源（影・GI・bindless・レイトレーシング・G-Buffer・後処理・Play のピッキング）を用意するかを、構成で選びます。\n" +
            "2D/UI だけのアプリは「2D/UI だけ（ui）」にすると GPU メモリが大きく減ります。既定は「3D あり（full）」＝従来どおりです。"));
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = QualitySeparatorBrush,
            Margin     = new Thickness(0, 0, 0, QualitySectionTopMargin),
        });

        // ── 定義の読み込みの警告（読めずに組み込みの一覧を出しているときなど）──
        foreach (var warning in catalog.Warnings)
        {
            panel.Children.Add(BuildRenderWarning(warning, 0));
        }
        // ── 設定の中身の注意（オブジェクトでない節・ランタイムが読まないキー。選び直すと消えるので毎回作り直す）──
        _renderProfileDataWarning = BuildRenderWarning(string.Empty, 0);
        panel.Children.Add(_renderProfileDataWarning);

        // ── 構成 ──
        _cmbRenderProfile = NewQualityCombo();
        FillRenderProfileChoices(_cmbRenderProfile, catalog, settings?.Profile);
        _cmbRenderProfile.SelectionChanged += (_, _) => OnRenderProfileChanged(catalog);
        panel.Children.Add(BuildQualityRow("構成", _cmbRenderProfile, RenderProfileLabelColumnWidth));
        _renderProfileDescription = BuildQualityNote(string.Empty, RenderProfileLabelColumnWidth);
        panel.Children.Add(_renderProfileDescription);
        _renderProfileNo3DWarning = BuildRenderWarning(RenderProfileNo3DWarning, RenderProfileLabelColumnWidth);
        panel.Children.Add(_renderProfileNo3DWarning);
        panel.Children.Add(BuildQualityNote(RenderProfileApplyNote, RenderProfileLabelColumnWidth));

        // ── 旗の上書き ──
        panel.Children.Add(new TextBlock
        {
            Text       = "旗の上書き（構成の値を旗ごとに変える。「構成のまま」は書かない）",
            Foreground = QualityTitleBrush,
            FontSize   = QualitySectionTitleFontSize,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, QualitySectionTopMargin, 0, QualityTitleBottomMargin),
        });
        foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
        {
            var combo = NewQualityCombo();
            combo.Items.Add(new ComboBoxItem { Tag = null });
            combo.Items.Add(new ComboBoxItem { Content = RenderProfileFlagValues.EnabledText, Tag = true });
            combo.Items.Add(new ComboBoxItem { Content = RenderProfileFlagValues.DisabledText, Tag = false });
            combo.SelectedIndex = IndexOfTag(combo, settings?.GetFlagOverride(flag.Key), (a, b) => (bool)a == (bool)b);
            var key = flag.Key;
            combo.SelectionChanged += (_, _) => OnRenderFlagChanged(catalog, key, combo);
            _renderFlagCombos[key] = combo;
            panel.Children.Add(BuildQualityRow(flag.Label, combo, RenderProfileLabelColumnWidth));
            panel.Children.Add(BuildQualityNote($"{flag.Key} … 無効にすると: {flag.DisabledDescription}", RenderProfileLabelColumnWidth));
        }

        // ── GPU メモリの確保（memory_hint）──
        _cmbRenderMemoryHint = NewQualityCombo();
        _cmbRenderMemoryHint.Items.Add(new ComboBoxItem { Tag = null });
        foreach (var choice in RenderProfileFlagCatalog.MemoryHintChoices)
        {
            _cmbRenderMemoryHint.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        }
        _cmbRenderMemoryHint.SelectedIndex = IndexOfTag(_cmbRenderMemoryHint, settings?.MemoryHint,
            (a, b) => string.Equals((string)a, (string)b, StringComparison.Ordinal));
        _cmbRenderMemoryHint.SelectionChanged += (_, _) => OnRenderMemoryHintChanged(catalog);
        panel.Children.Add(BuildQualityRow(RenderProfileFlagCatalog.MemoryHintLabel, _cmbRenderMemoryHint, RenderProfileLabelColumnWidth));
        panel.Children.Add(BuildQualityNote(
            $"{RenderProfileFlagCatalog.MemoryHintKey} … " +
            string.Join(" ／ ", RenderProfileFlagCatalog.MemoryHintChoices.Select(c => $"{c.Value}: {c.Description}")),
            RenderProfileLabelColumnWidth));

        // ── 実効の要約 ──
        _renderProfileSummary = BuildQualityNote(string.Empty, 0);
        _renderProfileSummary.Foreground = QualityLabelBrush;
        _renderProfileSummary.Margin = new Thickness(0, QualitySectionTopMargin, 0, QualityTitleBottomMargin);
        panel.Children.Add(_renderProfileSummary);

        panel.Children.Add(BuildQualityNote(
            $"書き込むキー: {RenderProfileSettings.SectionKey}.{RenderProfileSettings.ProfileKey}（既定の構成なら書かない）・" +
            $"{RenderProfileSettings.SectionKey}.<旗>（「構成のまま」なら書かない）。旗の意味は docs/rendering_profiles.md の 3 章。",
            0));

        RefreshRenderProfileView(catalog);
        return panel;
    }

    /// <summary>
    /// 構成のコンボボックスに選択肢を入れ、今の設定の項目を選ぶ。
    /// 既定の構成には「・既定」を付ける。一覧に無い名前（手で書いた・新しいエンジンの名前）も項目として残す。
    /// </summary>
    /// <param name="combo">コンボボックス。</param>
    /// <param name="catalog">構成の一覧。</param>
    /// <param name="current">設定の構成の名前（null ＝ 既定）。</param>
    private static void FillRenderProfileChoices(ComboBox combo, RenderProfileCatalogData catalog, string? current)
    {
        // 既定の構成が一覧に無い（定義の誤り）ときだけ、名前の無い「既定」を出す（ランタイムは full で動く）
        if (catalog.DefaultDefinition is null)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"既定（{catalog.DefaultProfile}。一覧に無いので {RenderProfileCatalog.FallbackProfileName} として動く）",
                Tag     = null,
            });
        }
        foreach (var profile in catalog.Profiles)
        {
            var mark = catalog.IsDefault(profile.Name) ? "・既定" : string.Empty;
            combo.Items.Add(new ComboBoxItem { Content = $"{profile.Label}（{profile.Name}{mark}）", Tag = profile.Name });
        }

        var known = catalog.Find(current);
        if (current is not null && known is null)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{current.Trim()}（このエディタの一覧に無い名前。ランタイムは警告して既定で動く）",
                Tag     = current,
            });
        }

        // 選ぶ項目: 指定なし → 既定の構成 / 一覧にある名前 → その構成 / 無い名前 → その項目
        var selectedTag = current is null ? catalog.DefaultDefinition?.Name : known?.Name ?? current;
        combo.SelectedIndex = IndexOfTag(combo, selectedTag, (a, b) => string.Equals((string)a, (string)b, StringComparison.Ordinal));
    }

    /// <summary>構成を選び直したとき: 既定の構成ならキーを書かない、それ以外は名前を書く。</summary>
    /// <param name="catalog">構成の一覧。</param>
    private void OnRenderProfileChanged(RenderProfileCatalogData catalog)
    {
        if (_renderPanelUpdating || _cmbRenderProfile is null) return;
        var name = (_cmbRenderProfile.SelectedItem as ComboBoxItem)?.Tag as string;
        var settings = _data.Render ??= new RenderProfileSettings();
        settings.SetProfile(name is null || catalog.IsDefault(name) ? null : name);
        RefreshRenderProfileView(catalog);
    }

    /// <summary>旗を選び直したとき: 「構成のまま」ならキーを消し、有効・無効なら上書きを書く。</summary>
    /// <param name="catalog">構成の一覧。</param>
    /// <param name="key">旗のキー。</param>
    /// <param name="combo">旗のコンボボックス。</param>
    private void OnRenderFlagChanged(RenderProfileCatalogData catalog, string key, ComboBox combo)
    {
        if (_renderPanelUpdating) return;
        var value = (combo.SelectedItem as ComboBoxItem)?.Tag as bool?;
        (_data.Render ??= new RenderProfileSettings()).SetFlagOverride(key, value);
        RefreshRenderProfileView(catalog);
    }

    /// <summary>GPU メモリの確保を選び直したとき: 「構成のまま」ならキーを消し、それ以外は値を書く。</summary>
    /// <param name="catalog">構成の一覧。</param>
    private void OnRenderMemoryHintChanged(RenderProfileCatalogData catalog)
    {
        if (_renderPanelUpdating || _cmbRenderMemoryHint is null) return;
        var value = (_cmbRenderMemoryHint.SelectedItem as ComboBoxItem)?.Tag as string;
        (_data.Render ??= new RenderProfileSettings()).SetMemoryHint(value);
        RefreshRenderProfileView(catalog);
    }

    /// <summary>
    /// 選んだ構成に合わせて表示を合わせ直す（構成の説明・各旗の「構成のまま（…）」・実効の要約・3D の注意）。
    /// </summary>
    /// <param name="catalog">構成の一覧。</param>
    private void RefreshRenderProfileView(RenderProfileCatalogData catalog)
    {
        var resolved = RenderProfileCatalog.Resolve(catalog, _data.Render);

        if (_renderProfileDescription is not null)
        {
            _renderProfileDescription.Text = resolved.Definition is { } definition
                ? definition.Description
                : $"構成 {resolved.Name} の中身はこのエディタでは分かりません（すべて用意する full として見積もります）。";
        }

        // 「構成のまま（…）」に、選んだ構成そのものの値（上書き前）を出す
        foreach (var (key, combo) in _renderFlagCombos)
        {
            var text = resolved.ProfileFlags.Get(key) ? RenderProfileFlagValues.EnabledText : RenderProfileFlagValues.DisabledText;
            SetInheritChoiceText(combo, string.Format(InheritChoiceFormat, text));
        }
        if (_cmbRenderMemoryHint is not null)
        {
            SetInheritChoiceText(_cmbRenderMemoryHint, string.Format(InheritChoiceFormat, resolved.ProfileFlags.MemoryHint));
        }

        if (_renderProfileSummary is not null)
        {
            _renderProfileSummary.Text = $"この設定で起動したときの実効（構成 {resolved.Name}）: {resolved.Flags.Describe()}";
        }
        if (_renderProfileNo3DWarning is not null)
        {
            _renderProfileNo3DWarning.Visibility = resolved.Flags.Get(RenderProfileFlagCatalog.Scene3DKey)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (_renderProfileDataWarning is not null)
        {
            var text = DescribeRenderDataWarnings(_data.Render);
            _renderProfileDataWarning.Text = text;
            _renderProfileDataWarning.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// 設定の中身の注意を作る（オブジェクトでない節・ランタイムが読まないキー。どちらも保存で消さない）。無ければ空。
    /// </summary>
    /// <param name="settings">"render" 節（無ければ null）。</param>
    /// <returns>注意の文。</returns>
    private static string DescribeRenderDataWarnings(RenderProfileSettings? settings)
    {
        if (settings is null) return string.Empty;
        var lines = new List<string>();
        if (settings.UnreadableSection is { } raw)
        {
            lines.Add($"project_settings.json の {RenderProfileSettings.SectionKey} がオブジェクトではありません（{raw.GetRawText()}。" +
                      "ランタイムは警告して無視します）。ここで選ぶと、選んだ値のオブジェクトに置き換えます。");
        }
        if (settings.ExtraData.Count > 0)
        {
            lines.Add("ランタイムが読まないキー・値があります（保存しても消しません。起動ログに [SEED RENDER PROFILE][WARN]）: " +
                      string.Join("、", settings.ExtraData.Select(kv => $"{kv.Key}={kv.Value.GetRawText()}")));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// コンボボックスの先頭（「構成のまま」）の項目の文言を差し替える。選択中なら、閉じた箱の表示も新しい文言にするため
    /// 選び直す（選択の変化は利用者の操作として扱わない）。
    /// </summary>
    /// <param name="combo">コンボボックス。</param>
    /// <param name="text">新しい文言。</param>
    private void SetInheritChoiceText(ComboBox combo, string text)
    {
        if (combo.Items.Count == 0 || combo.Items[0] is not ComboBoxItem inherit) return;
        if (Equals(inherit.Content, text)) return;
        inherit.Content = text;
        if (combo.SelectedIndex != 0) return;
        _renderPanelUpdating = true;
        try
        {
            combo.SelectedIndex = -1;
            combo.SelectedIndex = 0;
        }
        finally
        {
            _renderPanelUpdating = false;
        }
    }

    /// <summary>注意の文を作る（通知帯のアイコンと同じ黄色・折り返し）。</summary>
    /// <param name="text">本文。</param>
    /// <param name="leftMargin">左の余白（ラベル列に揃えるときはその幅）。</param>
    /// <returns>注意の文。</returns>
    private static TextBlock BuildRenderWarning(string text, double leftMargin)
    {
        var warning = BuildQualityNote(text, leftMargin);
        warning.Foreground = SettingsWarningBrush;
        return warning;
    }
}
