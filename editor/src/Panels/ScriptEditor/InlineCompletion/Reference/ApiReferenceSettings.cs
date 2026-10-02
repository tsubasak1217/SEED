// ============================================================
//  ApiReferenceSettings.cs — 「AI 補完へ渡す API リファレンスの選び方」の設定（正典）
//
//  【役割】
//  editor/config/inline_completion_reference.json を読み、検証して保持する。
//  無い・壊れているときは組み込み既定（同じ中身）で動く。読み込みの問題は Warnings に積み、
//  呼び出し側（ScriptApiReference）がログへ出す。
//
//  【持つもの】
//  - budget_chars        : 予算（注入する最大文字数）の既定。利用者の環境設定
//                          （editor_preferences.json の inline_completion_reference_chars）が優先
//  - max_part_chars      : 節をこれより長ければ切れ端に分ける
//  - cursor_window_lines : カーソルの前後この行数を「近く」として重く数える
//  - min_prefix_length   : 接頭辞の一致を見る語の最短の文字数
//  - min_score           : 文脈で選ぶ節の点の下限（下回る節は予算が余っていても入れない）
//  - relative_min_score  : 文脈で選ぶ節の点の下限を「最も点の高い節の点 × この割合」にもする
//                          （強く当たる節があるときに、UI のような広い語だけで当たる節を入れない）
//  - always_include      : 常に入れる節の見出し（見出しの文がこの文字列で始まる節）
//  - ignored_words       : 文脈の語から捨てる語（どこにでも出る SEED など）
//  - weights             : 重み（ApiReferenceWeights）
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>inline_completion_reference.json の重みの節（デシリアライズ専用。null は既定）。</summary>
public sealed class ApiReferenceWeightsFile
{
    /// <summary>ファイルのどこかに出る語の重み。</summary>
    [JsonPropertyName("file")] public double? File { get; set; }

    /// <summary>カーソルの近く（前後 cursor_window_lines 行）に出る語に足す重み。</summary>
    [JsonPropertyName("near_cursor")] public double? NearCursor { get; set; }

    /// <summary>カーソルの直前で書きかけの語に足す重み。</summary>
    [JsonPropertyName("typing")] public double? Typing { get; set; }

    /// <summary>using の名前空間の語（using SEED.Localization; の Localization）に足す重み。</summary>
    [JsonPropertyName("using")] public double? Using { get; set; }

    /// <summary>SEED. に続く語（SEED.UI.ScreenStack の UI・ScreenStack）に足す重み。</summary>
    [JsonPropertyName("engine_qualified")] public double? EngineQualified { get; set; }

    /// <summary>節の見出しに一致したときの倍率。</summary>
    [JsonPropertyName("heading_match")] public double? HeadingMatch { get; set; }

    /// <summary>節の本文に一致したときの倍率。</summary>
    [JsonPropertyName("body_match")] public double? BodyMatch { get; set; }

    /// <summary>接頭辞の一致（Bind → Binding）の倍率。</summary>
    [JsonPropertyName("prefix_match")] public double? PrefixMatch { get; set; }
}

/// <summary>inline_completion_reference.json のファイル全体（デシリアライズ専用。null は既定）。</summary>
public sealed class ApiReferenceSettingsFile
{
    /// <summary>ファイル書式のバージョン。</summary>
    [JsonPropertyName("format_version")] public int FormatVersion { get; set; }

    /// <summary>予算の既定（注入する最大文字数）。</summary>
    [JsonPropertyName("budget_chars")] public int? BudgetChars { get; set; }

    /// <summary>切れ端の単独の本文の上限。</summary>
    [JsonPropertyName("max_part_chars")] public int? MaxPartChars { get; set; }

    /// <summary>カーソルの前後で「近く」とみなす行数。</summary>
    [JsonPropertyName("cursor_window_lines")] public int? CursorWindowLines { get; set; }

    /// <summary>接頭辞の一致を見る語の最短の文字数。</summary>
    [JsonPropertyName("min_prefix_length")] public int? MinPrefixLength { get; set; }

    /// <summary>文脈で選ぶ節の点の下限。</summary>
    [JsonPropertyName("min_score")] public double? MinScore { get; set; }

    /// <summary>文脈で選ぶ節の点の下限（最も点の高い節に対する割合。0〜1）。</summary>
    [JsonPropertyName("relative_min_score")] public double? RelativeMinScore { get; set; }

    /// <summary>常に入れる節の見出し（見出しの文の先頭）。</summary>
    [JsonPropertyName("always_include")] public List<string>? AlwaysInclude { get; set; }

    /// <summary>文脈の語から捨てる語。</summary>
    [JsonPropertyName("ignored_words")] public List<string>? IgnoredWords { get; set; }

    /// <summary>重み。</summary>
    [JsonPropertyName("weights")] public ApiReferenceWeightsFile? Weights { get; set; }
}

/// <summary>
/// 重み（検証済み。すべて 0 以上）。
/// 文脈の語の重み = 当てはまる種類（file・near_cursor・typing・using・engine_qualified）の重みの和。
/// 切れ端の点 = Σ 語の重み × 語の珍しさ × 一致の倍率（heading_match・body_match。接頭辞の一致は prefix_match も掛ける）。
/// </summary>
/// <param name="File">ファイルのどこかに出る語。</param>
/// <param name="NearCursor">カーソルの近くに出る語に足す分。</param>
/// <param name="Typing">書きかけの語に足す分。</param>
/// <param name="Using">using の名前空間の語に足す分。</param>
/// <param name="EngineQualified">SEED. に続く語に足す分。</param>
/// <param name="HeadingMatch">見出しに一致したときの倍率。</param>
/// <param name="BodyMatch">本文に一致したときの倍率。</param>
/// <param name="PrefixMatch">接頭辞の一致の倍率。</param>
public sealed record ApiReferenceWeights(
    double File, double NearCursor, double Typing, double Using, double EngineQualified,
    double HeadingMatch, double BodyMatch, double PrefixMatch);

/// <summary>
/// AI 補完へ渡す API リファレンスの選び方（検証済み）。
/// 生成は <see cref="LoadFromDir"/>（通常）か <see cref="BuiltIn"/>（フォールバック・テスト）。
/// </summary>
public sealed class ApiReferenceSettings
{
    // ── ファイル名・範囲 ─────────────────────────────────────

    /// <summary>設定ファイルの名前（editor/config 直下）。</summary>
    public const string FileName = "inline_completion_reference.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>予算の下限（0 = リファレンスを注入しない）。</summary>
    public const int MinBudgetChars = 0;

    /// <summary>予算の上限（docs 全体の数倍。上限の広いローカル LLM でも足りる大きさ）。</summary>
    public const int MaxBudgetChars = 2_000_000;

    /// <summary>切れ端の上限の下限（これより細かく刻むと見出しの繰り返しばかりになる）。</summary>
    public const int MinPartChars = 500;

    /// <summary>接頭辞の一致を見る語の最短の文字数の下限。</summary>
    public const int MinPrefixLengthFloor = 1;

    // ── 組み込み既定（editor/config/inline_completion_reference.json と同じ中身）──

    /// <summary>予算の既定（以前の固定値 MaxChars と同じ 12000 文字）。</summary>
    public const int DefaultBudgetChars = 12000;

    /// <summary>切れ端の上限の既定。</summary>
    public const int DefaultMaxPartChars = 2500;

    /// <summary>カーソルの近くとみなす行数の既定（前後それぞれ）。</summary>
    public const int DefaultCursorWindowLines = 20;

    /// <summary>接頭辞の一致を見る語の最短の文字数の既定（"Bin" から）。</summary>
    public const int DefaultMinPrefixLength = 3;

    /// <summary>文脈で選ぶ節の点の下限の既定。</summary>
    public const double DefaultMinScore = 6.0;

    /// <summary>最も点の高い節に対する割合の下限の既定。</summary>
    public const double DefaultRelativeMinScore = 0.4;

    /// <summary>割合の下限の上限（1 = 最も点の高い節と同点の節だけ）。</summary>
    public const double MaxRelativeMinScore = 1.0;

    /// <summary>常に入れる節の既定（前書き・§1・§2・§7 の先頭・コンポーネント一覧の表）。</summary>
    public static readonly IReadOnlyList<string> DefaultAlwaysInclude = new[]
    {
        "SEED スクリプト API リファレンス",
        "1. スクリプトの基本形",
        "2. ライフサイクル関数",
        "7. GameObject とコンポーネント",
        "利用可能なコンポーネント一覧",
    };

    /// <summary>文脈の語から捨てる語の既定（どのファイルにも出て手がかりにならない）。</summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredWords = new[]
    {
        "SEED", "SEEDEditor", "Scripting", "SEEDScript", "System", "Collections", "Generic", "Linq",
    };

    /// <summary>重みの既定。</summary>
    public static readonly ApiReferenceWeights DefaultWeights = new(
        File: 1.0, NearCursor: 2.0, Typing: 3.0, Using: 2.0, EngineQualified: 1.0,
        HeadingMatch: 2.0, BodyMatch: 1.0, PrefixMatch: 0.5);

    // ── 値 ───────────────────────────────────────────────────

    /// <summary>予算の既定（注入する最大文字数）。利用者の環境設定が優先する。</summary>
    public int BudgetChars { get; }

    /// <summary>切れ端の単独の本文の上限。</summary>
    public int MaxPartChars { get; }

    /// <summary>カーソルの前後で「近く」とみなす行数。</summary>
    public int CursorWindowLines { get; }

    /// <summary>接頭辞の一致を見る語の最短の文字数。</summary>
    public int MinPrefixLength { get; }

    /// <summary>文脈で選ぶ節の点の下限。</summary>
    public double MinScore { get; }

    /// <summary>文脈で選ぶ節の点の下限（最も点の高い節の点に対する割合。0〜1）。</summary>
    public double RelativeMinScore { get; }

    /// <summary>常に入れる節の見出し（見出しの文がこの文字列で始まる節。書いた順）。</summary>
    public IReadOnlyList<string> AlwaysInclude { get; }

    /// <summary>文脈の語から捨てる語（大文字小文字を区別しない）。</summary>
    public IReadOnlySet<string> IgnoredWords { get; }

    /// <summary>重み。</summary>
    public ApiReferenceWeights Weights { get; }

    /// <summary>読み込み時に起きた問題（空なら正常）。呼び出し側がログへ出す。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイルの絶対パス。組み込み既定なら null。</summary>
    public string? SourcePath { get; }

    /// <summary>設定を組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> / <see cref="Create"/> から）。</summary>
    private ApiReferenceSettings(
        int budgetChars, int maxPartChars, int cursorWindowLines, int minPrefixLength, double minScore,
        double relativeMinScore, IReadOnlyList<string> alwaysInclude, IReadOnlySet<string> ignoredWords,
        ApiReferenceWeights weights, IReadOnlyList<string> warnings, string? sourcePath)
    {
        BudgetChars = budgetChars;
        MaxPartChars = maxPartChars;
        CursorWindowLines = cursorWindowLines;
        MinPrefixLength = minPrefixLength;
        MinScore = minScore;
        RelativeMinScore = relativeMinScore;
        AlwaysInclude = alwaysInclude;
        IgnoredWords = ignoredWords;
        Weights = weights;
        Warnings = warnings;
        SourcePath = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>組み込み既定（JSON を読まない）。</summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    /// <returns>設定。</returns>
    public static ApiReferenceSettings BuiltIn(IReadOnlyList<string>? warnings = null) =>
        new(DefaultBudgetChars, DefaultMaxPartChars, DefaultCursorWindowLines, DefaultMinPrefixLength, DefaultMinScore,
            DefaultRelativeMinScore, DefaultAlwaysInclude, new HashSet<string>(DefaultIgnoredWords, ApiIdentifierTokenizer.WordComparer),
            DefaultWeights, warnings ?? Array.Empty<string>(), sourcePath: null);

    /// <summary>
    /// 値を指定して作る（テスト・一時的な上書き用。null の項目は既定）。範囲外の値は丸める。
    /// </summary>
    /// <returns>設定。</returns>
    public static ApiReferenceSettings Create(
        int? budgetChars = null, int? maxPartChars = null, int? cursorWindowLines = null,
        int? minPrefixLength = null, double? minScore = null, double? relativeMinScore = null,
        IReadOnlyList<string>? alwaysInclude = null, IReadOnlyList<string>? ignoredWords = null,
        ApiReferenceWeights? weights = null)
    {
        var file = new ApiReferenceSettingsFile
        {
            FormatVersion = SupportedFormatVersion,
            BudgetChars = budgetChars,
            MaxPartChars = maxPartChars,
            CursorWindowLines = cursorWindowLines,
            MinPrefixLength = minPrefixLength,
            MinScore = minScore,
            RelativeMinScore = relativeMinScore,
            AlwaysInclude = alwaysInclude?.ToList(),
            IgnoredWords = ignoredWords?.ToList(),
            Weights = weights is null ? null : new ApiReferenceWeightsFile
            {
                File = weights.File, NearCursor = weights.NearCursor, Typing = weights.Typing,
                Using = weights.Using, EngineQualified = weights.EngineQualified,
                HeadingMatch = weights.HeadingMatch, BodyMatch = weights.BodyMatch, PrefixMatch = weights.PrefixMatch,
            },
        };
        return FromFile(file, new List<string>(), sourcePath: null);
    }

    /// <summary>
    /// 指定ファイルから読む。読めない・壊れているときは組み込み既定へ戻す（例外は投げない）。
    /// </summary>
    /// <param name="filePath">inline_completion_reference.json の絶対パス。</param>
    /// <returns>設定。</returns>
    public static ApiReferenceSettings Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"リファレンスの選び方の設定が見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む
        ApiReferenceSettingsFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ApiReferenceSettingsFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"リファレンスの選び方の設定の読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }
        if (parsed is null)
        {
            warnings.Add($"リファレンスの選び方の設定が空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョン（新しすぎても読める範囲は読む）
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add($"リファレンスの選び方の設定の format_version={parsed.FormatVersion} は未知" +
                         $"（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する");
        }

        // ④ 項目ごとに検証する（だめな項目は既定へ戻して他を生かす）
        return FromFile(parsed, warnings, Path.GetFullPath(filePath));
    }

    /// <summary>構成フォルダ（editor/config）を指定して読む。</summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    /// <returns>設定。</returns>
    public static ApiReferenceSettings LoadFromDir(string? configDir) =>
        configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないためリファレンスの選び方は組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    /// <summary>利用者の予算の指定を範囲へ丸める（null なら設定の既定）。</summary>
    /// <param name="requested">環境設定の値（null = 未指定）。</param>
    /// <returns>使う予算。</returns>
    public int ResolveBudget(int? requested) =>
        requested is { } value ? Math.Clamp(value, MinBudgetChars, MaxBudgetChars) : BudgetChars;

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>JSON のパース設定（コメント・末尾カンマを許す／大文字小文字を無視）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>ファイルの中身を検証して設定にする（範囲外は丸めて警告を積む）。</summary>
    private static ApiReferenceSettings FromFile(ApiReferenceSettingsFile file, List<string> warnings, string? sourcePath)
    {
        int budget = ClampInt(file.BudgetChars, DefaultBudgetChars, MinBudgetChars, MaxBudgetChars, "budget_chars", warnings);
        int maxPart = ClampInt(file.MaxPartChars, DefaultMaxPartChars, MinPartChars, MaxBudgetChars, "max_part_chars", warnings);
        int window = ClampInt(file.CursorWindowLines, DefaultCursorWindowLines, 0, int.MaxValue, "cursor_window_lines", warnings);
        int minPrefix = ClampInt(file.MinPrefixLength, DefaultMinPrefixLength, MinPrefixLengthFloor, int.MaxValue, "min_prefix_length", warnings);
        double minScore = NonNegative(file.MinScore, DefaultMinScore, "min_score", warnings);
        double relativeMinScore = NonNegative(file.RelativeMinScore, DefaultRelativeMinScore, "relative_min_score", warnings);
        if (relativeMinScore > MaxRelativeMinScore)
        {
            warnings.Add($"relative_min_score={relativeMinScore} は 1 を超えるため {MaxRelativeMinScore} に丸めた");
            relativeMinScore = MaxRelativeMinScore;
        }

        // 常に入れる見出し: 空白だけの項目は捨てる。項目の無い指定（[]）は「常に入れる節なし」として尊重する
        IReadOnlyList<string> always = file.AlwaysInclude is null
            ? DefaultAlwaysInclude
            : file.AlwaysInclude.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        var ignored = new HashSet<string>(
            (file.IgnoredWords ?? DefaultIgnoredWords.ToList()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            ApiIdentifierTokenizer.WordComparer);

        var w = file.Weights;
        var d = DefaultWeights;
        var weights = new ApiReferenceWeights(
            File: NonNegative(w?.File, d.File, "weights.file", warnings),
            NearCursor: NonNegative(w?.NearCursor, d.NearCursor, "weights.near_cursor", warnings),
            Typing: NonNegative(w?.Typing, d.Typing, "weights.typing", warnings),
            Using: NonNegative(w?.Using, d.Using, "weights.using", warnings),
            EngineQualified: NonNegative(w?.EngineQualified, d.EngineQualified, "weights.engine_qualified", warnings),
            HeadingMatch: NonNegative(w?.HeadingMatch, d.HeadingMatch, "weights.heading_match", warnings),
            BodyMatch: NonNegative(w?.BodyMatch, d.BodyMatch, "weights.body_match", warnings),
            PrefixMatch: NonNegative(w?.PrefixMatch, d.PrefixMatch, "weights.prefix_match", warnings));

        return new ApiReferenceSettings(budget, maxPart, window, minPrefix, minScore, relativeMinScore,
                                        always, ignored, weights, warnings, sourcePath);
    }

    /// <summary>整数の項目を範囲へ丸める（null は既定。丸めたら警告）。</summary>
    private static int ClampInt(int? value, int fallback, int min, int max, string name, List<string> warnings)
    {
        if (value is not { } v) return fallback;
        int clamped = Math.Clamp(v, min, max);
        if (clamped != v) warnings.Add($"{name}={v} は範囲外のため {clamped} に丸めた（{min}〜{max}）");
        return clamped;
    }

    /// <summary>実数の項目を 0 以上にそろえる（null は既定。負・非数は既定へ戻して警告）。</summary>
    private static double NonNegative(double? value, double fallback, string name, List<string> warnings)
    {
        if (value is not { } v) return fallback;
        if (double.IsFinite(v) && v >= 0.0) return v;
        warnings.Add($"{name}={v} は 0 以上の数でないため既定 {fallback} を使用");
        return fallback;
    }
}
