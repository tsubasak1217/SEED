// ============================================================
//  AndroidPlatformSettingsEditor.cs — プロジェクト設定「Android のプラットフォーム機能」の編集の状態（WPF 非依存。W1-2）
//
//  【役割】
//  プロジェクト設定ウィンドウ（ProjectSettingsWindow.AndroidPlatform.cs）の画面の判断と値の出し入れをここに置き、
//  WPF 側は結果を画面へ当てるだけにする（エージェントはエディタを起動できないので、判断は単体テスト AndroidPipelineTests で確かめる）。
//    機能のチェックボックス … 機能の表（AndroidPlatformFeatureCatalog）の順。表に無い名前（新しいエディタが足した機能）は
//                             画面に出さずに保ち、保存でも消さない
//    システムバー・アプリの分類 … コンボボックスで選ぶ。選び直すまでは書かれていた値をそのまま保つ（知らない値も消さない）。
//                             既定値（hidden・game）を選んだらキーごと省く（何も設定しないプロジェクトの android 節を増やさない）
//    ディープリンク … 行の追加・削除・編集（作業コピー）。何も書いていない行は保存しない
//    注意・誤り … ビルドと同じ関数（AndroidPlatformFeatureResolver）で出す。誤りがあれば保存を止める
//  機能の表が読めない（エンジンの不具合）ときは、チェックボックスを出さず理由を出し、書かれていた機能はそのまま保つ。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Android.Platform;

namespace SEEDEditor.ProjectSettings;

/// <summary>「Android のプラットフォーム機能」の編集の状態。</summary>
public sealed class AndroidPlatformSettingsEditor
{
    /// <summary>機能の表（読めなければ null）。</summary>
    private readonly AndroidPlatformFeatureCatalog? _catalog;

    /// <summary>有効にした機能（表にある名前。そろえた名前）。</summary>
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);

    /// <summary>表に無い機能の名前（書かれた表記のまま。保存でも保つ）。</summary>
    private readonly List<string> _unknownFeatures = new();

    /// <summary>書かれていたシステムバーの値（選び直すまで保つ）。</summary>
    private readonly string? _originalSystemBars;

    /// <summary>書かれていたアプリの分類の値（選び直すまで保つ）。</summary>
    private readonly string? _originalAppCategory;

    /// <summary>選び直したシステムバー（選んでいなければ null）。</summary>
    private string? _chosenSystemBars;

    /// <summary>選び直したアプリの分類（選んでいなければ null）。</summary>
    private string? _chosenAppCategory;

    /// <summary>
    /// 設定から編集の状態を作る。
    /// </summary>
    /// <param name="settings">今の android 節（無ければ null）。</param>
    /// <param name="catalog">機能の表（読めなければ null）。</param>
    /// <param name="catalogError">機能の表が読めなかった理由（読めたら null）。</param>
    public AndroidPlatformSettingsEditor(AndroidAppSettings? settings, AndroidPlatformFeatureCatalog? catalog, string? catalogError = null)
    {
        _catalog = catalog;
        CatalogError = catalogError;
        foreach (var raw in settings?.Features ?? new List<string>())
        {
            var name = AndroidPlatformFeatureCatalog.NormalizeName(raw);
            if (name is null) continue;
            if (catalog?.Find(name) is not null) _enabled.Add(name);
            else if (!_unknownFeatures.Contains(raw.Trim(), StringComparer.OrdinalIgnoreCase)) _unknownFeatures.Add(raw.Trim());
        }
        DeepLinks = settings?.DeepLinks?.Select(link => link.Clone()).ToList() ?? new List<AndroidDeepLinkSetting>();
        _originalSystemBars = AndroidAppSettings.NormalizeText(settings?.SystemBars);
        _originalAppCategory = AndroidAppSettings.NormalizeText(settings?.AppCategory);
    }

    /// <summary>
    /// 埋め込みの機能の表で編集の状態を作る（表が読めなければ理由を持った状態）。
    /// </summary>
    /// <param name="settings">今の android 節（無ければ null）。</param>
    /// <returns>編集の状態。</returns>
    public static AndroidPlatformSettingsEditor Load(AndroidAppSettings? settings)
    {
        try
        {
            return new AndroidPlatformSettingsEditor(settings, AndroidPlatformFeatureCatalog.BuiltIn);
        }
        catch (InvalidDataException ex)
        {
            return new AndroidPlatformSettingsEditor(settings, null, ex.Message);
        }
    }

    /// <summary>機能の表が読めなかった理由（読めたら null）。</summary>
    public string? CatalogError { get; }

    /// <summary>チェックボックスに出す機能（表の順。表が読めなければ空）。</summary>
    public IReadOnlyList<AndroidPlatformFeature> AvailableFeatures =>
        _catalog?.Features ?? (IReadOnlyList<AndroidPlatformFeature>)Array.Empty<AndroidPlatformFeature>();

    /// <summary>表に無い機能の名前（画面には出さず保つ）。</summary>
    public IReadOnlyList<string> UnknownFeatures => _unknownFeatures;

    /// <summary>ディープリンクの作業コピー（行の欄を直接書き換える）。</summary>
    public List<AndroidDeepLinkSetting> DeepLinks { get; }

    /// <summary>ディープリンクの一覧を使う機能が有効か（一覧の欄を強調する・注意の判断用）。</summary>
    public bool DeepLinkFeatureEnabled => AvailableFeatures.Any(f => f.DeepLinkFilters && _enabled.Contains(f.Name));

    /// <summary>機能が有効か。</summary>
    /// <param name="name">機能の名前。</param>
    /// <returns>有効なら true。</returns>
    public bool IsFeatureEnabled(string name) =>
        AndroidPlatformFeatureCatalog.NormalizeName(name) is { } normalized && _enabled.Contains(normalized);

    /// <summary>機能を有効・無効にする（表に無い名前は何もしない）。</summary>
    /// <param name="name">機能の名前。</param>
    /// <param name="enabled">有効にするか。</param>
    public void SetFeatureEnabled(string name, bool enabled)
    {
        var normalized = AndroidPlatformFeatureCatalog.NormalizeName(name);
        if (normalized is null || _catalog?.Find(normalized) is null) return;
        if (enabled) _enabled.Add(normalized);
        else _enabled.Remove(normalized);
    }

    /// <summary>今のシステムバーの値（書かれていた値か選び直した値。未設定なら既定値）。</summary>
    public string SystemBars => _chosenSystemBars ?? _originalSystemBars ?? AndroidSystemBarsSetting.Default;

    /// <summary>今のアプリの分類（書かれていた値か選び直した値。未設定なら既定値）。</summary>
    public string AppCategory => _chosenAppCategory ?? _originalAppCategory ?? AndroidAppCategorySetting.Default;

    /// <summary>システムバーのコンボボックスの初期選択（知らない値は既定値の位置）。</summary>
    public int SystemBarsIndex => AndroidSystemBarsSetting.IndexOf(SystemBars);

    /// <summary>アプリの分類のコンボボックスの初期選択（知らない値は既定値の位置）。</summary>
    public int AppCategoryIndex => AndroidAppCategorySetting.IndexOf(AppCategory);

    /// <summary>システムバーを選び直す。</summary>
    /// <param name="value">AndroidSystemBarsSetting の値。</param>
    public void SelectSystemBars(string value) => _chosenSystemBars = AndroidSystemBarsSetting.Normalize(value);

    /// <summary>アプリの分類を選び直す。</summary>
    /// <param name="value">AndroidAppCategorySetting の値。</param>
    public void SelectAppCategory(string value) => _chosenAppCategory = AndroidAppCategorySetting.Normalize(value);

    /// <summary>何も書いていないディープリンクの行を末尾に足す。</summary>
    /// <returns>足した行。</returns>
    public AndroidDeepLinkSetting AddDeepLink()
    {
        var link = new AndroidDeepLinkSetting();
        DeepLinks.Add(link);
        return link;
    }

    /// <summary>ディープリンクの行を消す（範囲外は何もしない）。</summary>
    /// <param name="index">行の位置。</param>
    public void RemoveDeepLinkAt(int index)
    {
        if (index >= 0 && index < DeepLinks.Count) DeepLinks.RemoveAt(index);
    }

    /// <summary>
    /// 編集の結果を android 節へ書く（機能は表の順＋表に無い名前、ディープリンクは何か書いてある行の複製、
    /// システムバー・アプリの分類は選び直したときだけ変える〈既定値ならキーを省く〉）。
    /// </summary>
    /// <param name="target">書き込む先。</param>
    public void ApplyTo(AndroidAppSettings target)
    {
        var features = AvailableFeatures.Where(f => _enabled.Contains(f.Name)).Select(f => f.Name).Concat(_unknownFeatures).ToList();
        target.Features = features.Count == 0 ? null : features;
        var links = DeepLinks.Where(link => !link.IsBlank).Select(link => link.Clone()).ToList();
        target.DeepLinks = links.Count == 0 ? null : links;
        target.SystemBars = _chosenSystemBars is null
            ? _originalSystemBars
            : _chosenSystemBars == AndroidSystemBarsSetting.Default ? null : _chosenSystemBars;
        target.AppCategory = _chosenAppCategory is null
            ? _originalAppCategory
            : _chosenAppCategory == AndroidAppCategorySetting.Default ? null : _chosenAppCategory;
    }

    /// <summary>
    /// 今の編集の結果をビルドと同じ関数で評価する（注意・誤りの表示と保存前の検査に使う。表が読めなければ null）。
    /// </summary>
    /// <returns>このビルドのプラットフォーム機能（表が読めなければ null）。</returns>
    public AndroidPlatformFeatureSet? Preview()
    {
        if (_catalog is null) return null;
        var settings = new AndroidAppSettings();
        ApplyTo(settings);
        return AndroidPlatformFeatureResolver.Resolve(settings, _catalog);
    }

    /// <summary>保存前の検査（ビルドと同じ規則。誤りの説明の一覧。表が読めないことは保存を止めない）。</summary>
    /// <returns>誤り（無ければ空）。</returns>
    public IReadOnlyList<string> Validate() => Preview()?.Errors ?? Array.Empty<string>();

    /// <summary>
    /// 画面に出す注意と誤り（誤りは「誤り: 」、注意は「注意: 」を頭に付けた行。表が読めなければその理由）。
    /// </summary>
    /// <returns>行の一覧（無ければ空）。</returns>
    public IReadOnlyList<string> DescribeProblems()
    {
        if (CatalogError is not null) return new[] { $"{ErrorPrefix}機能の表を読めません（エンジンの不具合）: {CatalogError}" };
        var preview = Preview();
        if (preview is null) return Array.Empty<string>();
        return preview.Errors.Select(e => ErrorPrefix + e).Concat(preview.Warnings.Select(w => WarningPrefix + w)).ToList();
    }

    /// <summary>誤りの行の頭（保存を止める）。</summary>
    public const string ErrorPrefix = "誤り: ";

    /// <summary>注意の行の頭（保存は止めない）。</summary>
    public const string WarningPrefix = "注意: ";
}
