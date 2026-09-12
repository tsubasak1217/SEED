// ============================================================
//  ProjectPanelVisibilityRules.cs — プロジェクトパネルの「見せない」判定（正典）
//
//  【役割】
//  プロジェクトパネル（アセットブラウザ）に出す必要の無いファイル・フォルダを
//  名前だけで判定する。判定規則はデータ（editor/config/project_panel_rules.json）
//  として外に出し、ここはその読み込みと照合だけを持つ。
//
//  【対象にするもの】
//    - エディタ／OS／外部ツールが勝手に作る作業ファイル
//      （.backup 世代バックアップ・*.lock・*.tmp・Thumbs.db・.DS_Store など）
//    - エンジンが生成する中間データ（地形のボクセル・散布・カバー）
//      ＝ エディタの地形ツールが管理するため、ユーザーが直接開く場面が無い
//
//  【PackagingRules とは別物】
//  editor/src/Packaging/Collect/PackagingRules.cs は「配布 PAK に何を収録するか」の
//  規則であり、こちらは「パネルに何を表示するか」の規則。目的も寿命も違うため
//  意図的に分けてある。例えば .tvox は
//    - パッケージには**必要**（ランタイムが読む）→ PackagingRules は収録する
//    - パネルには**不要**（人は触らない）      → ここでは隠す
//  という正反対の扱いになる。片方の都合でもう片方を書き換えないこと。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//  WPF 型・EditorLog などエディタ本体のシングルトンへは依存しない。
//  読み込み時の問題は Warnings に積み、呼び出し側がログへ出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SEEDEditor.Assets;

/// <summary>
/// project_panel_rules.json のファイル全体に対応するモデル（デシリアライズ専用）。
/// アプリからは <see cref="ProjectPanelVisibilityRules"/> を使う。
/// </summary>
public sealed class ProjectPanelVisibilityRulesFile
{
    /// <summary>
    /// ファイル書式のバージョン。項目の意味を変える改訂を入れたときに旧ファイルを
    /// 識別するために持つ。未知（新しすぎる）バージョンでも読める範囲は読む。
    /// </summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>
    /// 先頭がドットの名前（.git / .vscode / .DS_Store …）をまとめて隠すか。
    /// null は「未指定」＝組み込み既定（true）を使う。
    /// </summary>
    [JsonPropertyName("hide_dot_prefixed")]
    public bool? HideDotPrefixed { get; set; }

    /// <summary>隠すフォルダ名（完全一致・大文字小文字は無視）。</summary>
    [JsonPropertyName("hidden_folder_names")]
    public List<string>? HiddenFolderNames { get; set; }

    /// <summary>隠すファイル名（完全一致・大文字小文字は無視）。</summary>
    [JsonPropertyName("hidden_file_names")]
    public List<string>? HiddenFileNames { get; set; }

    /// <summary>隠すファイルの拡張子（ドット付き・大文字小文字は無視）。</summary>
    [JsonPropertyName("hidden_file_extensions")]
    public List<string>? HiddenFileExtensions { get; set; }

    /// <summary>隠すファイル名のワイルドカード（<c>*</c> と <c>?</c> のみ）。</summary>
    [JsonPropertyName("hidden_file_patterns")]
    public List<string>? HiddenFilePatterns { get; set; }
}

/// <summary>
/// プロジェクトパネルの表示／非表示ルール。
///
/// <para>
/// 生成は <see cref="LoadFromDir"/>（通常）か <see cref="BuiltIn"/>（フォールバック・テスト）。
/// 判定は名前だけで行い、ディスクへは触らない（列挙中に何千回呼ばれても軽いこと、
/// および単体テストで実ファイルを要らなくすることの両方が狙い）。
/// </para>
/// </summary>
public sealed class ProjectPanelVisibilityRules
{
    // ── ファイル名・バージョン（マジックストリングの一元化）──────

    /// <summary>ルールファイルの名前（editor/config 直下）。</summary>
    public const string FileName = "project_panel_rules.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    // ── 組み込み既定 ─────────────────────────────────────────
    //
    //  editor/config/project_panel_rules.json と同じ内容を持つ。
    //  「JSON を消してもパネルは正しく動く」ことを保証する最後の砦であり、
    //  ルールを足すときは JSON 側だけを触れば済む（ここは同期の写しにすぎない）。

    /// <summary>組み込み既定: 先頭がドットの名前を隠す。</summary>
    private const bool BuiltInHideDotPrefixed = true;

    /// <summary>組み込み既定で隠すフォルダ名。</summary>
    private static readonly string[] BuiltInHiddenFolderNames =
    [
        ".backup",    // シーン保存の世代バックアップ（エディタが自動生成）
        "__MACOSX",   // macOS で作った zip を展開したときに混入するメタデータ
    ];

    /// <summary>組み込み既定で隠すファイル名。</summary>
    private static readonly string[] BuiltInHiddenFileNames =
    [
        "Thumbs.db",    // Windows エクスプローラのサムネイルキャッシュ
        "desktop.ini",  // Windows のフォルダ表示設定
        ".DS_Store",    // macOS の Finder メタデータ
    ];

    /// <summary>組み込み既定で隠すファイル拡張子。</summary>
    private static readonly string[] BuiltInHiddenFileExtensions =
    [
        ".lock",      // エディタのロックファイル
        ".tmp",       // 各種一時ファイル
        ".bak",       // 手動・ツールのバックアップ
        ".blend1",    // Blender の世代バックアップ（.blend2 以降は下のパターンで拾う）
        ".tvox",      // 地形ボクセル（エンジン生成・バイナリ）
        ".tscatter",  // 地形の散布データ（エンジン生成・バイナリ）
        ".tcover",    // 地表カバーデータ（エンジン生成・バイナリ）
    ];

    /// <summary>組み込み既定で隠すファイル名パターン（ワイルドカード）。</summary>
    private static readonly string[] BuiltInHiddenFilePatterns =
    [
        "._*",          // macOS のリソースフォーク（AppleDouble）
        "*.blend?",     // Blender の世代バックアップ .blend1 / .blend2 / …
                        // （パターンは * と ? だけ。? は任意の 1 文字なので 1〜9 世代を 1 行で拾える）
    ];

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>先頭がドットの名前を隠すか。</summary>
    public bool HideDotPrefixed { get; }

    /// <summary>隠すフォルダ名の集合（大文字小文字を無視）。</summary>
    private readonly HashSet<string> _hiddenFolderNames;

    /// <summary>隠すファイル名の集合（大文字小文字を無視）。</summary>
    private readonly HashSet<string> _hiddenFileNames;

    /// <summary>隠すファイル拡張子の集合（ドット付き・大文字小文字を無視）。</summary>
    private readonly HashSet<string> _hiddenFileExtensions;

    /// <summary>隠すファイル名パターン（ワイルドカードを正規表現へ変換済み）。</summary>
    private readonly List<Regex> _hiddenFilePatterns;

    /// <summary>
    /// 読み込み時に起きた問題（ファイル欠落・JSON 破損・不正な項目）。
    /// 空なら完全に正常。呼び出し側がログへ出す。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイルの絶対パス。組み込み既定なら null。</summary>
    public string? SourcePath { get; }

    /// <summary>ルールを組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> から）。</summary>
    private ProjectPanelVisibilityRules(
        bool hideDotPrefixed,
        IEnumerable<string> folderNames,
        IEnumerable<string> fileNames,
        IEnumerable<string> fileExtensions,
        IEnumerable<string> filePatterns,
        IReadOnlyList<string> warnings,
        string? sourcePath)
    {
        HideDotPrefixed       = hideDotPrefixed;
        _hiddenFolderNames    = BuildNameSet(folderNames);
        _hiddenFileNames      = BuildNameSet(fileNames);
        _hiddenFileExtensions = BuildExtensionSet(fileExtensions);
        _hiddenFilePatterns   = BuildPatternList(filePatterns);
        Warnings              = warnings;
        SourcePath            = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>組み込み既定だけのルールを作る（JSON を読まない）。</summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    public static ProjectPanelVisibilityRules BuiltIn(IReadOnlyList<string>? warnings = null)
        => new(
            BuiltInHideDotPrefixed,
            BuiltInHiddenFolderNames,
            BuiltInHiddenFileNames,
            BuiltInHiddenFileExtensions,
            BuiltInHiddenFilePatterns,
            warnings ?? Array.Empty<string>(),
            sourcePath: null);

    /// <summary>
    /// 指定ファイルからルールを読み込む。
    /// 読めない・壊れている場合は組み込み既定へフォールバックする（例外は投げない）。
    /// </summary>
    /// <param name="filePath">project_panel_rules.json の絶対パス。</param>
    public static ProjectPanelVisibilityRules Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認。無ければ組み込み既定（配布形態・削除など）。
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"表示ルールが見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む。破損・型不一致はここで例外になるのでまとめて拾う。
        ProjectPanelVisibilityRulesFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectPanelVisibilityRulesFile>(
                File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"表示ルールの読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"表示ルールが空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョンの確認。新しすぎても読める範囲は読む（警告のみ）。
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add(
                $"表示ルールの format_version={parsed.FormatVersion} は未知（対応 {SupportedFormatVersion}）。" +
                "読める範囲だけ解釈する");
        }

        // ④ 各リストは「未指定なら組み込み既定」。空配列を書けば明示的に無効化できる
        //    （＝ユーザーが「何も隠さない」を選べる）ため、null と空配列は区別する。
        return new ProjectPanelVisibilityRules(
            parsed.HideDotPrefixed      ?? BuiltInHideDotPrefixed,
            parsed.HiddenFolderNames    ?? (IEnumerable<string>)BuiltInHiddenFolderNames,
            parsed.HiddenFileNames      ?? (IEnumerable<string>)BuiltInHiddenFileNames,
            parsed.HiddenFileExtensions ?? (IEnumerable<string>)BuiltInHiddenFileExtensions,
            parsed.HiddenFilePatterns   ?? (IEnumerable<string>)BuiltInHiddenFilePatterns,
            warnings,
            Path.GetFullPath(filePath));
    }

    /// <summary>
    /// 構成フォルダ（editor/config）を指定してルールを読み込む。
    /// </summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    public static ProjectPanelVisibilityRules LoadFromDir(string? configDir)
        => configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないため表示ルールは組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    // ── 判定 ─────────────────────────────────────────────────

    /// <summary>
    /// フォルダ名が非表示対象か。
    /// </summary>
    /// <param name="name">フォルダ名（パスではなく名前だけ）。</param>
    public bool IsHiddenFolderName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (HideDotPrefixed && name[0] == '.') return true;
        return _hiddenFolderNames.Contains(name);
    }

    /// <summary>
    /// ファイル名が非表示対象か。
    /// </summary>
    /// <param name="name">ファイル名（パスではなく名前だけ）。</param>
    public bool IsHiddenFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (HideDotPrefixed && name[0] == '.') return true;
        if (_hiddenFileNames.Contains(name)) return true;

        // 拡張子照合（"a.tar.gz" の拡張子は ".gz"。多段拡張子はパターン側で扱う）
        var ext = Path.GetExtension(name);
        if (ext.Length > 0 && _hiddenFileExtensions.Contains(ext)) return true;

        foreach (var re in _hiddenFilePatterns)
            if (re.IsMatch(name)) return true;

        return false;
    }

    /// <summary>
    /// エントリ名（ファイル／フォルダ）が非表示対象か。
    /// </summary>
    /// <param name="name">名前だけ（パス不可）。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    public bool IsHiddenName(string? name, bool isDirectory)
        => isDirectory ? IsHiddenFolderName(name) : IsHiddenFileName(name);

    /// <summary>
    /// 絶対パス／相対パスの末尾要素で非表示判定する。
    /// 末尾の区切り文字（"C:\a\b\"）は取り除いてから見るため、
    /// フォルダパスをそのまま渡してよい。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    public bool IsHiddenPath(string? path, bool isDirectory)
        => IsHiddenName(LeafName(path), isDirectory);

    /// <summary>
    /// パネルに表示してよいか（表示トグルを加味した最終判定）。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    /// <param name="showHidden">「隠しファイルを表示」トグルが ON なら true。</param>
    public bool ShouldShowPath(string? path, bool isDirectory, bool showHidden)
        => showHidden || !IsHiddenPath(path, isDirectory);

    /// <summary>
    /// パスの末尾要素（ファイル名・フォルダ名）を取り出す。
    /// 末尾区切り文字・空文字・ルートのみ（"C:\"）でも例外にしない。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>末尾要素。取り出せない場合は空文字。</returns>
    private static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return string.Empty;
        try { return Path.GetFileName(trimmed); }
        catch { return string.Empty; }   // 不正な文字を含むパスでも落とさない
    }

    // ── 内部: 入力の正規化 ────────────────────────────────────

    /// <summary>JSON のパース設定（コメント・末尾カンマを許す／大文字小文字を無視）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>名前リストを大文字小文字無視の集合にする（空要素は捨てる）。</summary>
    private static HashSet<string> BuildNameSet(IEnumerable<string> names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n)) set.Add(n.Trim());
        return set;
    }

    /// <summary>
    /// 拡張子リストを集合にする。ドットの有無を吸収して必ず ".ext" 形に揃える
    /// （JSON へ "tmp" と書いても ".tmp" と書いても同じ意味になる）。
    /// </summary>
    private static HashSet<string> BuildExtensionSet(IEnumerable<string> extensions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var ext = raw.Trim();
            if (ext[0] != '.') ext = "." + ext;
            set.Add(ext);
        }
        return set;
    }

    /// <summary>
    /// ワイルドカードパターン（<c>*</c> / <c>?</c>）を正規表現へ変換する。
    /// 変換できないパターンは捨てる（1 件の書き間違いで全部無効にしない）。
    /// </summary>
    private static List<Regex> BuildPatternList(IEnumerable<string> patterns)
    {
        var list = new List<Regex>();
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var pattern = raw.Trim();
            // メタ文字をすべてエスケープしてから、* と ? だけを復活させる。
            // こうすると "._*" の '.' が「任意の 1 文字」に化けない。
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try { list.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
            catch (ArgumentException) { /* 異常なパターンは無視する */ }
        }
        return list;
    }
}
