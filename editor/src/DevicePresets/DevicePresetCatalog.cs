// ============================================================
//  DevicePresetCatalog.cs — 端末プリセットの一覧（editor/config/device_presets.json の読み込み・検証）
//
//  【役割】
//  editor/config/device_presets.json（正典）を読み、実行先セレクタに「PC（端末の模擬: …）」として並べる
//  端末の一覧を提供する。端末を増やすときは JSON に 1 件足すだけでよい（コードは変えない）。
//  データの形・行の並び・起動の仕組みは docs/editor_device_presets.md。
//
//  【壊れていても止めない】（ランタイムのビルド構成の表 RuntimeBuildConfigCatalog と同じ流儀）
//  ファイルが無い・JSON が壊れている・使える端末が 1 件も無い、のいずれでも、組み込みの 1 件（Pixel 6a 半分）へ
//  フォールバックして続ける。1 件だけ間違っている（値が範囲外・型が違う・id の重複）ならその 1 件だけを捨てる。
//  何が起きたかは Warnings に残し、呼び出し側（MainWindow）がログへ出す。
//
//  【WPF 非依存】単体テスト（editor/tests/AndroidRunUiTests）からリンクして使うため、EditorLog などへは依存しない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.DevicePresets;

/// <summary>端末プリセットの一覧（読み込み済み・検証済み）。</summary>
public sealed class DevicePresetCatalog
{
    // ── ファイル・キー（マジックストリングの一元化）────────────────

    /// <summary>端末プリセットのファイル名（エディタの構成フォルダ editor/config の中）。</summary>
    public const string FileName = "device_presets.json";

    /// <summary>このコードが理解する書式の版。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>書式の版のキー。</summary>
    private const string FormatVersionKey = "format_version";

    /// <summary>端末の配列のキー。</summary>
    private const string PresetsKey = "presets";

    /// <summary>1 件の識別子のキー（デシリアライズできなかった要素の警告に id を添えるため）。</summary>
    private const string IdKey = "id";

    /// <summary>id を比べる規則（選択の記録と JSON の id の大文字小文字の違いを許す）。</summary>
    public const StringComparison IdComparison = StringComparison.OrdinalIgnoreCase;

    // ── 警告の文言 ───────────────────────────────────────────

    /// <summary>ファイルが無い（{0}=パス）。</summary>
    private const string MissingFileFormat = "端末プリセットが見つからないため組み込みの 1 件を使います: {0}";

    /// <summary>構成フォルダが無い。</summary>
    private const string MissingDirWarning = "構成フォルダ（editor/config）が見つからないため、端末プリセットは組み込みの 1 件を使います";

    /// <summary>JSON として読めない（{0}=パス、{1}=理由）。</summary>
    private const string BrokenFileFormat = "端末プリセットを JSON として読めないため組み込みの 1 件を使います: {0} — {1}";

    /// <summary>根がオブジェクトでない・presets が配列でない（{0}=パス）。</summary>
    private const string NoPresetsArrayFormat = "端末プリセットに \"presets\" の配列が無いため組み込みの 1 件を使います: {0}";

    /// <summary>使える端末が無い（{0}=パス）。</summary>
    private const string NoValidPresetsFormat = "使える端末プリセットが 1 件も無いため組み込みの 1 件を使います: {0}";

    /// <summary>書式の版が新しすぎる（{0}=版、{1}=対応する版）。</summary>
    private const string NewerFormatFormat = "端末プリセットの format_version={0} は未知です（対応 {1}）。読める範囲だけ読みます";

    /// <summary>1 件を捨てた（{0}=何番目〈1 から〉、{1}=id、{2}=理由）。</summary>
    private const string SkippedPresetFormat = "端末プリセットの {0} 件目（id='{1}'）を無視します: {2}";

    /// <summary>要素がオブジェクトでない。</summary>
    private const string NotAnObjectProblem = "オブジェクト（{ … }）ではありません";

    /// <summary>id が重複（{0}=id）。</summary>
    private const string DuplicateIdProblemFormat = "id '{0}' が前の端末と重複しています";

    /// <summary>番号を 1 から数えるためのずれ。</summary>
    private const int DisplayIndexOffset = 1;

    // ── 組み込みの 1 件（JSON が読めないときの最後の砦）──────────────
    // editor/config/device_presets.json の "pixel6a-half" と同じ値（AndroidRunUiTests の DevicePresetTests が突き合わせる）。
    // 端末を増やすときにここを触る必要は無い（JSON だけで増やせる）。

    /// <summary>組み込みの端末（Pixel 6a 半分。dp の数は実寸と同じで、窓が多くの画面に収まる大きさ）。</summary>
    public static DevicePreset BuiltInPreset { get; } = new()
    {
        Id = "pixel6a-half",
        Name = "Pixel 6a 半分",
        WidthPx = 540,
        HeightPx = 1200,
        ScaleFactor = 1.3125,
        SafeArea = new DeviceSafeArea(0, 66, 0, 32),
        KeyboardHeightPx = 490,
        RenderQuality = "mobile",
        Description = "Pixel 6a（1080×2400・×2.625）の縦横を半分にした窓。dp の数（411×914）と安全領域・キーボードの dp は実寸と同じ。",
    };

    /// <summary>JSON の読み方（人が手で書くファイルなのでコメントと末尾のカンマを許す）。</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 1 件のデシリアライズの設定（キーの大文字小文字を問わない）。要素のデシリアライズは要素の元の文字列を読み直すので、
    /// 要素の中のコメント・末尾のカンマもここで許す（ファイル全体の読み方 <see cref="DocumentOptions"/> と揃える）。
    /// </summary>
    private static readonly JsonSerializerOptions ElementOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>端末の一覧（JSON の順。必ず 1 件以上）。</summary>
    public IReadOnlyList<DevicePreset> Presets { get; }

    /// <summary>読み込みで起きた問題（空なら完全に正常）。呼び出し側がログへ出す。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>読んだファイルの絶対パス（組み込みへフォールバックしたら null）。</summary>
    public string? SourcePath { get; }

    /// <summary>一覧を組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> から）。</summary>
    /// <param name="presets">端末の一覧（1 件以上）。</param>
    /// <param name="warnings">問題。</param>
    /// <param name="sourcePath">読んだファイル（組み込みなら null）。</param>
    private DevicePresetCatalog(IReadOnlyList<DevicePreset> presets, IReadOnlyList<string> warnings, string? sourcePath)
    {
        Presets = presets;
        Warnings = warnings;
        SourcePath = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>組み込みの 1 件だけの一覧を作る（JSON を読まない）。</summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    /// <returns>一覧。</returns>
    public static DevicePresetCatalog BuiltIn(IReadOnlyList<string>? warnings = null) =>
        new(new[] { BuiltInPreset }, warnings ?? Array.Empty<string>(), sourcePath: null);

    /// <summary>
    /// 構成フォルダ（editor/config）から読む。
    /// </summary>
    /// <param name="configDir">構成フォルダ（見つからなければ null。組み込みの 1 件へ）。</param>
    /// <returns>一覧。</returns>
    public static DevicePresetCatalog LoadFromDir(string? configDir) =>
        configDir is null ? BuiltIn(new[] { MissingDirWarning }) : Load(Path.Combine(configDir, FileName));

    /// <summary>
    /// ファイルから読む（例外は投げない。読めなければ組み込みの 1 件と警告）。
    /// </summary>
    /// <param name="filePath">device_presets.json の絶対パス。</param>
    /// <returns>一覧。</returns>
    public static DevicePresetCatalog Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルが無ければ組み込み（配布の形でファイルを外したときなど）
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add(string.Format(MissingFileFormat, filePath));
            return BuiltIn(warnings);
        }

        // ② JSON として読む（文法の誤り・読めないファイルはまとめてここで拾う）
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(filePath), DocumentOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add(string.Format(BrokenFileFormat, filePath, ex.Message));
            return BuiltIn(warnings);
        }

        using (document)
        {
            var root = document.RootElement;

            // ③ 形の確認（根がオブジェクトで presets が配列）
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(PresetsKey, out var presetsElement)
                || presetsElement.ValueKind != JsonValueKind.Array)
            {
                warnings.Add(string.Format(NoPresetsArrayFormat, filePath));
                return BuiltIn(warnings);
            }

            // ④ 書式の版（新しすぎても読める範囲は読む）
            if (root.TryGetProperty(FormatVersionKey, out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version > SupportedFormatVersion)
            {
                warnings.Add(string.Format(NewerFormatFormat, version, SupportedFormatVersion));
            }

            // ⑤ 1 件ずつ検証する（間違った 1 件だけを捨て、ほかは生かす）
            var presets = ReadPresets(presetsElement, warnings);
            if (presets.Count == 0)
            {
                warnings.Add(string.Format(NoValidPresetsFormat, filePath));
                return BuiltIn(warnings);
            }
            return new DevicePresetCatalog(presets, warnings, Path.GetFullPath(filePath));
        }
    }

    /// <summary>
    /// 配列の要素を 1 件ずつ検証して端末にする（捨てた理由は警告へ）。
    /// </summary>
    /// <param name="presetsElement">"presets" の配列。</param>
    /// <param name="warnings">警告の書き込み先。</param>
    /// <returns>使える端末（JSON の順。id の重複は前のものを残す）。</returns>
    private static List<DevicePreset> ReadPresets(JsonElement presetsElement, List<string> warnings)
    {
        var presets = new List<DevicePreset>();
        var seenIds = new HashSet<string>(StringComparer.FromComparison(IdComparison));
        var index = 0;
        foreach (var element in presetsElement.EnumerateArray())
        {
            var number = index + DisplayIndexOffset;
            index++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(string.Format(SkippedPresetFormat, number, string.Empty, NotAnObjectProblem));
                continue;
            }

            // 型の違い（数の所に文字列など）は 1 件のデシリアライズの例外になる。その 1 件だけを捨てる
            DevicePresetJson? json;
            try
            {
                json = element.Deserialize<DevicePresetJson>(ElementOptions);
            }
            catch (JsonException ex)
            {
                warnings.Add(string.Format(SkippedPresetFormat, number, PeekId(element), ex.Message));
                continue;
            }

            // オブジェクトであることは確かめ済みなので null にはならないが、型の上の約束として扱う
            if (json is null)
            {
                warnings.Add(string.Format(SkippedPresetFormat, number, string.Empty, NotAnObjectProblem));
                continue;
            }
            var preset = json.ToPreset(out var problem);
            if (preset is null)
            {
                warnings.Add(string.Format(SkippedPresetFormat, number, json.Id ?? string.Empty, problem));
                continue;
            }
            if (!seenIds.Add(preset.Id))
            {
                warnings.Add(string.Format(SkippedPresetFormat, number, preset.Id, string.Format(DuplicateIdProblemFormat, preset.Id)));
                continue;
            }
            presets.Add(preset);
        }
        return presets;
    }

    /// <summary>
    /// デシリアライズできなかった要素の id を、警告の文言のために覗く（文字列でなければ空）。
    /// </summary>
    /// <param name="element">要素。</param>
    /// <returns>id。</returns>
    private static string PeekId(JsonElement element) =>
        element.TryGetProperty(IdKey, out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : string.Empty;

    // ── 参照 ─────────────────────────────────────────────────

    /// <summary>
    /// id から端末を引く（大文字小文字を区別しない）。
    /// </summary>
    /// <param name="id">id（null・空なら null）。</param>
    /// <returns>端末（無ければ null）。</returns>
    public DevicePreset? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, IdComparison));
}
