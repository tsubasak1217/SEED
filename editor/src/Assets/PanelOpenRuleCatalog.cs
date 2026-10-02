// ============================================================
//  PanelOpenRuleCatalog.cs — 「ダブルクリックで専用のパネルへ回すファイル」の規則（正典）
//
//  【役割】
//  プロジェクトパネルでダブルクリックしたファイルのうち、拡張子だけでは決められない
//  （同じ .json でも置き場で開き方が違う）ものを、専用のパネルへ回す規則を 1 か所で決める。
//    ・locale/*.json … 文字列表のパネル（ContentId "localization"。docs/localization.md §15）
//  規則はデータ（editor/config/panel_open_rules.json）。無い・壊れているときは組み込み既定で動く。
//
//  【判定の順番】（docs/editor_project_panel.md §10）
//  .scene / .actor など拡張子で決まる専用エディタの後、テキストエディタ（text_editable_extensions.json）の前。
//  .json は内蔵エディタでも開けるので、この規則を先に見ないと文字列表のパネルへ届かない。
//  テキストで直したいときは右クリック「テキストエディタで開く」（.anim / .inputmap と同じ）。
//
//  【WPF 非依存】
//  このフォルダは editor/tests/AssetsRootProbeTests がワイルドカードで丸ごと取り込むので、
//  WPF 型・EditorLog などエディタ本体のシングルトンへ依存しない。読み込みの問題は Warnings に積む。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Assets;

/// <summary>panel_open_rules.json の規則 1 つ（このフォルダのこの拡張子は、このパネルで開く）。</summary>
public sealed class PanelOpenRuleEntry
{
    /// <summary>開くパネルの ContentId（layout.xml の鍵と同じ。例 "localization"）。</summary>
    [JsonPropertyName("panel")]
    public string? Panel { get; set; }

    /// <summary>ファイルの直上のフォルダの名前（大文字小文字は無視。例 "locale"）。</summary>
    [JsonPropertyName("parent_folder")]
    public string? ParentFolder { get; set; }

    /// <summary>拡張子（ドットの有無はどちらでもよい）。</summary>
    [JsonPropertyName("extensions")]
    public List<string>? Extensions { get; set; }

    /// <summary>何のための規則かの覚書（動作には影響しない）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>panel_open_rules.json のファイル全体（デシリアライズ専用）。</summary>
public sealed class PanelOpenRuleCatalogFile
{
    /// <summary>ファイル書式のバージョン。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>規則（上から順に照合し、最初に当たったものを使う）。</summary>
    [JsonPropertyName("rules")]
    public List<PanelOpenRuleEntry>? Rules { get; set; }
}

/// <summary>読み込み済みの規則 1 つ（正規化済み）。</summary>
/// <param name="Panel">開くパネルの ContentId。</param>
/// <param name="ParentFolder">直上のフォルダの名前。</param>
/// <param name="Extensions">拡張子（ドット付き。大文字小文字は無視して比べる）。</param>
public sealed record PanelOpenRule(string Panel, string ParentFolder, IReadOnlySet<string> Extensions);

/// <summary>
/// ダブルクリックで専用のパネルへ回すファイルの規則。
/// 生成は <see cref="LoadFromDir"/>（通常）か <see cref="BuiltIn"/>（フォールバック・テスト）、
/// 判定は <see cref="FindPanelFor"/>。
/// </summary>
public sealed class PanelOpenRuleCatalog
{
    /// <summary>規則のファイルの名前（editor/config 直下）。</summary>
    public const string FileName = "panel_open_rules.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>文字列表のパネルの ContentId（MainWindow の登録と同じ。変えない）。</summary>
    public const string LocalizationPanel = "localization";

    /// <summary>多言語の置き場のフォルダ名（SEED.Localization の既定の置き場 assets://locale）。</summary>
    public const string LocaleFolderName = "locale";

    /// <summary>多言語のデータファイルの拡張子。</summary>
    public const string JsonExtension = ".json";

    /// <summary>規則（上から順に照合する）。</summary>
    public IReadOnlyList<PanelOpenRule> Rules { get; }

    /// <summary>読み込み時に起きた問題（空なら正常）。呼び出し側がログへ出す。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイルの絶対パス。組み込み既定なら null。</summary>
    public string? SourcePath { get; }

    /// <summary>規則の一覧を組み立てる（Load / BuiltIn から）。</summary>
    private PanelOpenRuleCatalog(IReadOnlyList<PanelOpenRule> rules, IReadOnlyList<string> warnings, string? sourcePath)
    {
        Rules = rules;
        Warnings = warnings;
        SourcePath = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>
    /// 組み込み既定（editor/config/panel_open_rules.json と同じ中身）。JSON を消しても locale/*.json は文字列表で開く。
    /// </summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    /// <returns>規則の一覧。</returns>
    public static PanelOpenRuleCatalog BuiltIn(IReadOnlyList<string>? warnings = null) =>
        new(new[]
            {
                new PanelOpenRule(LocalizationPanel, LocaleFolderName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { JsonExtension }),
            },
            warnings ?? Array.Empty<string>(),
            sourcePath: null);

    /// <summary>
    /// 指定ファイルから読む。読めない・壊れているときは組み込み既定へ戻す（例外は投げない）。
    /// </summary>
    /// <param name="filePath">panel_open_rules.json の絶対パス。</param>
    /// <returns>規則の一覧。</returns>
    public static PanelOpenRuleCatalog Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"パネルで開く規則が見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む
        PanelOpenRuleCatalogFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PanelOpenRuleCatalogFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"パネルで開く規則の読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }
        if (parsed is null)
        {
            warnings.Add($"パネルで開く規則が空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョン（新しすぎても読める範囲は読む）
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add($"パネルで開く規則の format_version={parsed.FormatVersion} は未知（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する");
        }

        // ④ 規則を 1 件ずつ確かめる（だめな規則は捨てて他を生かす。0 件は「どれもパネルへ回さない」設定として尊重する）
        var rules = new List<PanelOpenRule>();
        foreach (var entry in parsed.Rules ?? new List<PanelOpenRuleEntry>())
        {
            if (entry is null) continue;
            string panel = (entry.Panel ?? string.Empty).Trim();
            string folder = (entry.ParentFolder ?? string.Empty).Trim();
            if (panel.Length == 0 || folder.Length == 0)
            {
                warnings.Add("panel か parent_folder が空の規則を無視（パネルで開く規則）");
                continue;
            }
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in entry.Extensions ?? new List<string>())
            {
                var normalized = NormalizeExtension(extension);
                if (normalized is not null) extensions.Add(normalized);
            }
            if (extensions.Count == 0)
            {
                warnings.Add($"拡張子の無い規則を無視（パネル {panel}・フォルダ {folder}）");
                continue;
            }
            rules.Add(new PanelOpenRule(panel, folder, extensions));
        }
        return new PanelOpenRuleCatalog(rules, warnings, Path.GetFullPath(filePath));
    }

    /// <summary>構成フォルダ（editor/config）を指定して読む。</summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    /// <returns>規則の一覧。</returns>
    public static PanelOpenRuleCatalog LoadFromDir(string? configDir) =>
        configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないためパネルで開く規則は組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    // ── 照合 ─────────────────────────────────────────────────

    /// <summary>
    /// このファイルを開く専用のパネルを返す（直上のフォルダ名と拡張子が当たる最初の規則）。
    /// </summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <returns>パネルの ContentId（どの規則にも当たらなければ null）。</returns>
    public string? FindPanelFor(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string extension = Path.GetExtension(filePath);
        string parent = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
        foreach (var rule in Rules)
        {
            if (!rule.Extensions.Contains(extension)) continue;
            if (!string.Equals(rule.ParentFolder, parent, StringComparison.OrdinalIgnoreCase)) continue;
            return rule.Panel;
        }
        return null;
    }

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>JSON のパース設定（コメント・末尾カンマを許す／大文字小文字を無視）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>拡張子を ".ext" へそろえる（空なら null）。</summary>
    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim();
        if (ext[0] != '.') ext = "." + ext;
        return ext.Length > 1 ? ext : null;
    }
}
