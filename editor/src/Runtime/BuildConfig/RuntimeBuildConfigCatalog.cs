// ============================================================
//  RuntimeBuildConfigCatalog.cs — ビルド構成カタログ（JSON の読み込み・検証）
//
//  【役割】
//  editor/config/runtime_build_configs.json を読み、
//  「選べる構成の一覧」と「既定の構成」を提供する。
//
//  【壊れていても起動を止めない】
//  この JSON はエディタが起動してランタイムを立ち上げるための入口にある。
//  ファイルが無い・JSON が壊れている・項目が欠けている、のいずれでも
//  エディタが起動できなくなってはいけないため、必ず組み込み既定
//  （debug / develop / release の 3 件）へフォールバックする。
//  何が起きたかは Warnings に残し、呼び出し側（MainWindow）がログへ出す。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/RuntimeBuildConfigTests）からリンクして使うため、
//  EditorLog を含むエディタ本体のシングルトンへは依存しない。
//  ログ出力は Warnings 経由で呼び出し側へ委ねる。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Runtime.BuildConfig;

/// <summary>
/// runtime_build_configs.json のファイル全体に対応するモデル。
/// デシリアライズ専用（アプリからはカタログ <see cref="RuntimeBuildConfigCatalog"/> を使う）。
/// </summary>
public sealed class RuntimeBuildConfigFile
{
    /// <summary>
    /// ファイル書式のバージョン。
    /// 将来、項目の意味を変える改訂を入れたときに旧ファイルを識別するために持つ。
    /// 未知（新しすぎる）バージョンでも、読める範囲は読んで警告だけ出す。
    /// </summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>既定で選ばれる構成の id。</summary>
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    /// <summary>選択できる構成の一覧（UI の並び順そのまま）。</summary>
    [JsonPropertyName("configs")]
    public List<RuntimeBuildConfig> Configs { get; set; } = new();
}

/// <summary>
/// ビルド構成カタログ。JSON から読んだ構成一覧と既定 id を保持し、
/// id から構成を引く／不正な id を既定へ丸める、を担う。
/// </summary>
public sealed class RuntimeBuildConfigCatalog
{
    // ── ファイル・フォルダ名（マジックストリングの一元化）────────────

    /// <summary>構成カタログを置くフォルダ名（エディタルート直下）。</summary>
    public const string ConfigDirName = "config";

    /// <summary>構成カタログのファイル名。</summary>
    public const string FileName = "runtime_build_configs.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    // ── 組み込み既定（JSON が読めないときのフォールバック）──────────
    // editor/config/runtime_build_configs.json と同じ内容を持つ。
    // 「JSON を消してもエディタは起動できる」ことを保証するための最後の砦であり、
    // 構成を増やすときにここを触る必要は無い（JSON だけで増やせる）。

    /// <summary>組み込み既定の既定構成 id。</summary>
    public const string BuiltInDefaultId = "develop";

    /// <summary>組み込み既定の構成一覧を新しく作る。</summary>
    private static List<RuntimeBuildConfig> CreateBuiltInConfigs() => new()
    {
        RuntimeBuildConfig.Create(
            "debug", "Debug", "dev", "debug",
            "最適化なし。フルデバッグ向け（最も遅い）"),
        RuntimeBuildConfig.Create(
            "develop", "Develop", "develop", "develop",
            "最適化 1 ＋ デバッグ情報。普段の Play 向け（既定）"),
        RuntimeBuildConfig.Create(
            "release", "Release", "release", "release",
            "配布相当の最適化（初回ビルドに時間がかかる）"),
    };

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>選択できる構成の一覧（必ず 1 件以上）。</summary>
    public IReadOnlyList<RuntimeBuildConfig> Configs { get; }

    /// <summary>既定の構成（環境設定に選択が無いときに使う）。必ず非 null。</summary>
    public RuntimeBuildConfig Default { get; }

    /// <summary>
    /// 読み込み時に起きた問題（ファイル欠落・JSON 破損・不正な項目・未知の既定 id）。
    /// 空なら完全に正常。呼び出し側がログへ出す。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// 実際に読んだファイルの絶対パス。組み込み既定にフォールバックした場合は null。
    /// </summary>
    public string? SourcePath { get; }

    /// <summary>カタログを組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> から）。</summary>
    private RuntimeBuildConfigCatalog(
        IReadOnlyList<RuntimeBuildConfig> configs,
        RuntimeBuildConfig defaultConfig,
        IReadOnlyList<string> warnings,
        string? sourcePath)
    {
        Configs    = configs;
        Default    = defaultConfig;
        Warnings   = warnings;
        SourcePath = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>
    /// 組み込み既定だけのカタログを作る（JSON を読まない）。
    /// </summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    public static RuntimeBuildConfigCatalog BuiltIn(IReadOnlyList<string>? warnings = null)
    {
        var configs = CreateBuiltInConfigs();
        var def     = FindById(configs, BuiltInDefaultId) ?? configs[0];
        return new RuntimeBuildConfigCatalog(configs, def, warnings ?? Array.Empty<string>(), sourcePath: null);
    }

    /// <summary>
    /// 指定ファイルからカタログを読み込む。
    /// 読めない・壊れている・有効な構成が 1 件も無い場合は組み込み既定へフォールバックする
    /// （例外は投げない）。
    /// </summary>
    /// <param name="filePath">runtime_build_configs.json の絶対パス。</param>
    public static RuntimeBuildConfigCatalog Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認。無ければ組み込み既定（初回起動・配布形態など）。
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"構成カタログが見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む。破損・型不一致はここで例外になるので、まとめて拾う。
        RuntimeBuildConfigFile? parsed;
        try
        {
            var json = File.ReadAllText(filePath);
            parsed = JsonSerializer.Deserialize<RuntimeBuildConfigFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"構成カタログの読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"構成カタログが空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョンの確認。新しすぎる場合でも読める範囲は読む（警告のみ）。
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add(
                $"構成カタログの format_version={parsed.FormatVersion} は未知（対応 {SupportedFormatVersion}）。" +
                "読める範囲だけ解釈する");
        }

        // ④ 構成を 1 件ずつ検証する。不正な要素は捨てて他を生かす
        //    （1 件の書き間違いで全部使えなくなるのを避ける）。
        var configs = new List<RuntimeBuildConfig>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in parsed.Configs)
        {
            if (cfg is null) continue;
            if (!cfg.IsValid)
            {
                warnings.Add($"構成の必須項目が欠けているため無視: id='{cfg.Id}' label='{cfg.Label}'");
                continue;
            }
            if (!seenIds.Add(cfg.Id))
            {
                warnings.Add($"構成の id が重複しているため後の方を無視: id='{cfg.Id}'");
                continue;
            }
            configs.Add(cfg);
        }

        // ⑤ 有効な構成が 1 件も残らなければ組み込み既定へ（選択肢ゼロの UI を作らない）。
        if (configs.Count == 0)
        {
            warnings.Add($"有効な構成が 1 件も無いため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ⑥ 既定 id の解決。未知・未指定なら先頭を既定にする。
        var def = FindById(configs, parsed.Default);
        if (def is null)
        {
            if (!string.IsNullOrWhiteSpace(parsed.Default))
                warnings.Add($"既定 id '{parsed.Default}' が configs に無いため先頭 '{configs[0].Id}' を既定にする");
            else
                warnings.Add($"既定 id が未指定のため先頭 '{configs[0].Id}' を既定にする");
            def = configs[0];
        }

        return new RuntimeBuildConfigCatalog(configs, def, warnings, Path.GetFullPath(filePath));
    }

    /// <summary>
    /// 構成フォルダ（editor/config）を指定してカタログを読み込む。
    /// </summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    public static RuntimeBuildConfigCatalog LoadFromDir(string? configDir)
        => configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないため組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    // ── 参照 ─────────────────────────────────────────────────

    /// <summary>
    /// id から構成を引く。見つからなければ null。
    /// </summary>
    /// <param name="id">構成 id（null / 空なら null を返す）。</param>
    public RuntimeBuildConfig? Find(string? id) => FindById(Configs, id);

    /// <summary>
    /// id から構成を引く。見つからなければ既定を返す（必ず非 null）。
    ///
    /// 環境設定に保存された id が、カタログの編集・ダウングレードで消えていることが
    /// あり得るため、選択の解決は必ずここを通す。
    /// </summary>
    /// <param name="id">構成 id（null なら既定）。</param>
    public RuntimeBuildConfig Resolve(string? id) => Find(id) ?? Default;

    /// <summary>指定 id が、このカタログに存在するか。</summary>
    /// <param name="id">構成 id。</param>
    public bool Contains(string? id) => Find(id) is not null;

    // ── 内部ヘルパー ─────────────────────────────────────────

    /// <summary>一覧から id で構成を探す（大文字小文字を区別しない）。</summary>
    private static RuntimeBuildConfig? FindById(IReadOnlyList<RuntimeBuildConfig> configs, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var c in configs)
            if (string.Equals(c.Id, id, RuntimeBuildConfig.IdComparison))
                return c;
        return null;
    }

    /// <summary>
    /// JSON 読み込みオプション。
    /// コメント許容・末尾カンマ許容は「人が手で書く設定ファイル」のため。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling  = JsonCommentHandling.Skip,
        AllowTrailingCommas  = true,
        PropertyNameCaseInsensitive = true,
    };
}
