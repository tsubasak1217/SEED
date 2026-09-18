// ============================================================
//  VersionControlPanelStateStore.cs — バージョン管理パネルの節の開閉を再起動をまたいで保つ
//
//  【何を持つか】
//  折りたたみ節（競合 / 変更 / ロック / 履歴）のそれぞれが開いているか。
//  それだけ。ツリーのどのフォルダーを畳んでいたかは持たない
//  （変更の中身は毎回まるごと入れ替わるので、畳み位置を復元しても意味が薄い）。
//
//  【なぜプロジェクトではなくエディタ側に持つのか】
//  「節を開いていたか」は作業者個人の見え方であって、プロジェクトの内容ではない。
//  プロジェクトフォルダへ書くとチーム作業で無意味な差分になるため、
//  editor/settings/ 配下のエディタ専用 JSON へ逃がす
//  （ProjectPanelStateStore と同じ置き場・同じ流儀）。
//
//  【なぜプロジェクト単位か】
//  バージョン管理が使えるかどうかがプロジェクトごとに違う。1 つのエディタ設定を
//  複数プロジェクトで共有するため、プロジェクトルートの正規化パスをキーにする。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/VersionControlTests）から直接リンクするため、
//  WPF 型・DispatcherTimer・EditorLog へは依存しない。
//  「いつ保存するか」と「どうビューへ戻すか」はパネル側（Sections.cs）の責務で、
//  このファイルは「JSON の読み書き」と「既定値への埋め戻し」だけを持つ。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.VersionControl;

/// <summary>
/// 保存ファイル中のプロジェクト 1 つぶん。JSON の <c>"projects"</c> 辞書の値に対応する。
/// </summary>
public sealed class VersionControlPanelProjectStateFile
{
    /// <summary>
    /// 節の保存キー（<see cref="VersionControlSections.ToKey"/>）→ 開いているか。
    /// 未知のキーは読み飛ばし、書かれていない節は既定値を使う。
    /// </summary>
    [JsonPropertyName("sections")]
    public Dictionary<string, bool> Sections { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// version_control_panel_state.json のルート。
/// </summary>
public sealed class VersionControlPanelStateDocument
{
    /// <summary>
    /// ファイル書式のバージョン。項目の意味を変える改訂を入れたときに旧ファイルを識別するために持つ。
    /// 0（＝キーが無い）は「バージョン導入前のファイル」として v1 相当に読む。
    /// </summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>プロジェクトキー（正規化したプロジェクトルート）→ そのプロジェクトの節の状態。</summary>
    [JsonPropertyName("projects")]
    public Dictionary<string, VersionControlPanelProjectStateFile> Projects { get; set; }
        = new(StringComparer.Ordinal);
}

/// <summary>
/// バージョン管理パネルの節の開閉を保存・復元するストア。
/// </summary>
public sealed class VersionControlPanelStateStore
{
    /// <summary>保存ファイル名（editor/settings/ の直下）。</summary>
    public const string FILE_NAME = "version_control_panel_state.json";

    /// <summary>このコードが書き出す書式バージョン。</summary>
    public const int SUPPORTED_FORMAT_VERSION = 1;

    /// <summary>JSON の読み書き設定（人が読める整形・日本語をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Encoder                     = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>読み込み済みの内容（ファイルが無ければ空）。</summary>
    private readonly VersionControlPanelStateDocument _document;

    /// <summary>保存先の絶対パス（未設定なら空文字）。</summary>
    public string FilePath { get; }

    /// <summary>読み込み時に起きた問題（ファイル破損・未知バージョンなど）。空なら正常。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>直近の <see cref="Save"/> が失敗した理由（成功したら null）。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>生成は <see cref="Load"/> / <see cref="LoadFile"/> から。</summary>
    /// <param name="filePath">保存先の絶対パス。</param>
    /// <param name="document">読み込み済みの内容。</param>
    /// <param name="warnings">読み込み時の問題。</param>
    private VersionControlPanelStateStore(
        string filePath,
        VersionControlPanelStateDocument document,
        IReadOnlyList<string> warnings)
    {
        FilePath  = filePath;
        _document = document;
        Warnings  = warnings;
    }

    // ── 読み込み ─────────────────────────────────────────────

    /// <summary>設定フォルダを指定して読み込む（通常の入口）。</summary>
    /// <param name="settingsDir">editor/settings の絶対パス。</param>
    public static VersionControlPanelStateStore Load(string settingsDir)
        => LoadFile(string.IsNullOrWhiteSpace(settingsDir)
            ? string.Empty
            : Path.Combine(settingsDir, FILE_NAME));

    /// <summary>
    /// ファイルパスを直接指定して読み込む（単体テスト用の入口でもある）。
    /// 読めない・壊れている場合は空の状態で返す（例外は投げない＝起動を止めない）。
    /// </summary>
    /// <param name="filePath">保存ファイルの絶対パス。</param>
    public static VersionControlPanelStateStore LoadFile(string filePath)
    {
        var warnings = new List<string>();
        var empty    = new VersionControlPanelStateDocument
        {
            FormatVersion = SUPPORTED_FORMAT_VERSION,
        };

        // ファイルが無いのは初回起動の正常な状態なので警告にしない。
        if (string.IsNullOrWhiteSpace(filePath))
            return new VersionControlPanelStateStore(string.Empty, empty, warnings);
        if (!File.Exists(filePath))
            return new VersionControlPanelStateStore(filePath, empty, warnings);

        VersionControlPanelStateDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<VersionControlPanelStateDocument>(
                File.ReadAllText(filePath), JsonOpts);
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"バージョン管理パネル状態の読み込みに失敗したため既定で開始: {filePath} — {ex.Message}");
            return new VersionControlPanelStateStore(filePath, empty, warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"バージョン管理パネル状態が空のため既定で開始: {filePath}");
            return new VersionControlPanelStateStore(filePath, empty, warnings);
        }

        // 未来のバージョンでも読める範囲は読む（捨てると新旧のエディタを往復しただけで
        // 利用者の設定が消える。害の大きさが釣り合わない）。
        if (parsed.FormatVersion > SUPPORTED_FORMAT_VERSION)
        {
            warnings.Add(
                $"バージョン管理パネル状態の format_version={parsed.FormatVersion} は未知" +
                $"（対応 {SUPPORTED_FORMAT_VERSION}）。読める範囲だけ解釈する");
        }

        // 手書きで "projects": null と書かれても落とさない。
        parsed.Projects ??= new Dictionary<string, VersionControlPanelProjectStateFile>(
            StringComparer.Ordinal);
        foreach (var state in parsed.Projects.Values)
        {
            state.Sections ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        return new VersionControlPanelStateStore(filePath, parsed, warnings);
    }

    // ── 出し入れ ─────────────────────────────────────────────

    /// <summary>
    /// 保存されている節の開閉を読む。保存が無い節は既定値
    /// （<see cref="VersionControlSections.DefaultIsExpanded"/>）で埋める。
    /// </summary>
    /// <param name="projectKey"><see cref="MakeProjectKey"/> で作ったキー。</param>
    /// <returns>すべての節について「開いているか」が入った辞書。</returns>
    public IReadOnlyDictionary<VersionControlSection, bool> GetSections(string? projectKey)
    {
        var result = new Dictionary<VersionControlSection, bool>();
        var saved  = projectKey is not null && _document.Projects.TryGetValue(projectKey, out var s)
            ? s.Sections
            : null;

        foreach (var section in VersionControlSections.InDisplayOrder)
        {
            var key = VersionControlSections.ToKey(section);

            result[section] = saved is not null && saved.TryGetValue(key, out var expanded)
                ? expanded
                : VersionControlSections.DefaultIsExpanded(section);
        }

        return result;
    }

    /// <summary>
    /// 節の開閉を書き込む（<see cref="Save"/> を呼ぶまでファイルへは出ない）。
    /// キーが null（プロジェクト未確定）なら何もしない。
    /// </summary>
    /// <param name="projectKey"><see cref="MakeProjectKey"/> で作ったキー。</param>
    /// <param name="sections">節 → 開いているか。</param>
    public void SetSections(
        string? projectKey, IReadOnlyDictionary<VersionControlSection, bool>? sections)
    {
        if (projectKey is null || sections is null) return;

        var entry = new VersionControlPanelProjectStateFile();
        foreach (var pair in sections)
        {
            entry.Sections[VersionControlSections.ToKey(pair.Key)] = pair.Value;
        }

        _document.Projects[projectKey] = entry;
    }

    /// <summary>
    /// 現在の内容をファイルへ書き出す。
    /// </summary>
    /// <returns>成功したら true。失敗理由は <see cref="LastSaveError"/> に入る。</returns>
    public bool Save()
    {
        LastSaveError = null;

        if (string.IsNullOrWhiteSpace(FilePath))
        {
            LastSaveError = "保存先が未設定です";
            return false;
        }

        try
        {
            // 書き出すたびに現行バージョンへ揃える。
            _document.FormatVersion = SUPPORTED_FORMAT_VERSION;

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_document, JsonOpts));
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            return false;
        }
    }

    // ── プロジェクトキー ──────────────────────────────────────

    /// <summary>
    /// プロジェクトルートから保存キーを作る。
    /// 大文字小文字・区切り文字・相対表記の揺れで別エントリにならないよう、
    /// 絶対パス化してスラッシュ区切りの小文字へ揃える
    /// （ProjectPanelStateStore.MakeProjectKey と同じ流儀）。
    /// </summary>
    /// <param name="projectRootDir">プロジェクトルートの絶対パス。空なら null を返す。</param>
    /// <returns>保存キー。作れなければ null（＝保存も復元もしない）。</returns>
    public static string? MakeProjectKey(string? projectRootDir)
    {
        if (string.IsNullOrWhiteSpace(projectRootDir)) return null;

        try
        {
            return Path.GetFullPath(projectRootDir)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .Replace('\\', '/')
                       .ToLowerInvariant();
        }
        catch
        {
            // 不正なパス文字などで正規化できない場合は素の文字列で妥協する（キーとしては十分）。
            return projectRootDir.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
