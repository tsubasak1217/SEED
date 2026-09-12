// ============================================================
//  ProjectPanelStateStore.cs — プロジェクトパネルの「開いている場所」を再起動をまたいで保持するストア
//
//  【何を持つか】
//    プロジェクトパネル（アセットブラウザ）のタブ状態を丸ごと持つ。
//      ・開いているタブの一覧と、各タブが開いているフォルダ
//      ・アクティブなタブ
//      ・各タブのフォルダツリー展開集合 / 選択アイテム / ファイル一覧のスクロール位置
//
//  【なぜプロジェクトではなくエディタ側に持つのか】
//    「どのフォルダを開いていたか」は作業者個人の見え方であって、プロジェクトの内容ではない。
//    プロジェクトフォルダへ書くとチーム作業で無意味な差分になるため、
//    editor/settings/ 配下のエディタ専用 JSON（EditorViewState と同じ置き場）へ逃がす。
//
//  【なぜプロジェクト単位か】
//    フォルダ構成はプロジェクトごとに違う。1 つのエディタ設定フォルダを複数プロジェクトで
//    共有するため、プロジェクトルートの正規化パスをキーにした辞書で持つ。
//
//  【なぜパスを相対で保存するのか】
//    絶対パスで保存すると、プロジェクトフォルダを移動・リネームした瞬間に全タブが無効になる。
//    アセットルート相対（スラッシュ区切り）で保存しておけば、移動先でもそのまま復元できる
//    （プロジェクトキー側は移動で変わるが、そちらは「別プロジェクト扱いになる」だけで害が小さい）。
//
//  【WPF 非依存】
//    単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//    WPF 型・DispatcherTimer・EditorLog といったエディタ本体の仕組みへは依存しない。
//    保存のデバウンス（タイマ）と実際のビュー操作は Panels/ProjectPanel.StatePersistence.cs 側の責務。
//    このファイルは「JSON の読み書き」と「パスの相対化・絶対化・間引き」だけを持つ。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Assets;

// ── JSON モデル ────────────────────────────────────────────────
//
//  ファイルに書く形そのもの。パスはすべて「アセットルート相対・スラッシュ区切り」で、
//  アセットルート自身は空文字で表す。

/// <summary>
/// 保存ファイル中のタブ 1 枚ぶん。JSON の <c>"tabs"</c> 配列の要素に対応する。
/// </summary>
public sealed class ProjectPanelTabStateFile
{
    /// <summary>このタブが開いているフォルダ（アセットルート相対。ルート自身は空文字）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>フォルダツリーで展開されていたフォルダ（同じく相対パス）。</summary>
    [JsonPropertyName("expanded")]
    public List<string> Expanded { get; set; } = new();

    /// <summary>ファイル一覧で選択されていたアイテム（同じく相対パス）。未選択なら null。</summary>
    [JsonPropertyName("selected")]
    public string? Selected { get; set; }

    /// <summary>ファイル一覧の垂直スクロール位置（px）。</summary>
    [JsonPropertyName("scroll")]
    public double Scroll { get; set; }
}

/// <summary>
/// 保存ファイル中のプロジェクト 1 つぶん。JSON の <c>"projects"</c> 辞書の値に対応する。
/// </summary>
public sealed class ProjectPanelProjectStateFile
{
    /// <summary>アクティブだったタブの添字（<see cref="Tabs"/> に対する 0 始まり）。</summary>
    [JsonPropertyName("active_tab")]
    public int ActiveTab { get; set; }

    /// <summary>開いていたタブ（左から順）。</summary>
    [JsonPropertyName("tabs")]
    public List<ProjectPanelTabStateFile> Tabs { get; set; } = new();
}

/// <summary>
/// project_panel_state.json のルート。
/// </summary>
public sealed class ProjectPanelStateDocument
{
    /// <summary>
    /// ファイル書式のバージョン。項目の意味を変える改訂を入れたときに旧ファイルを識別するために持つ。
    /// 0（＝キーが無い）は「バージョン導入前のファイル」として v1 相当に読む。
    /// </summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>プロジェクトキー（正規化したプロジェクトルート）→ そのプロジェクトのタブ状態。</summary>
    [JsonPropertyName("projects")]
    public Dictionary<string, ProjectPanelProjectStateFile> Projects { get; set; }
        = new(StringComparer.Ordinal);
}

// ── 受け渡し用の型（絶対パス） ─────────────────────────────────
//
//  パネル側は常に絶対パスで状態を持つ。相対化・絶対化の境界をこの型で明示する。

/// <summary>
/// 保存時にパネルから受け取るタブ 1 枚ぶんの状態（パスはすべて絶対パス）。
/// </summary>
/// <param name="FolderPath">開いているフォルダの絶対パス。</param>
/// <param name="ExpandedPaths">ツリーで展開されているフォルダの絶対パス。</param>
/// <param name="SelectedPath">選択中アイテムの絶対パス（未選択なら null）。</param>
/// <param name="ScrollOffset">ファイル一覧の垂直スクロール位置。</param>
public sealed record ProjectPanelTabSnapshot(
    string FolderPath,
    IReadOnlyCollection<string> ExpandedPaths,
    string? SelectedPath,
    double ScrollOffset);

/// <summary>
/// 復元時にパネルへ返すタブ 1 枚ぶんの状態（パスはすべて実在が確認済みの絶対パス）。
/// </summary>
/// <param name="FolderPath">開くフォルダの絶対パス（実在するもののみ）。</param>
/// <param name="ExpandedPaths">展開するフォルダの絶対パス（実在するもののみ）。</param>
/// <param name="SelectedPath">選択するアイテムの絶対パス（実在しなければ null）。</param>
/// <param name="ScrollOffset">ファイル一覧の垂直スクロール位置（0 以上の有限値）。</param>
public sealed record ProjectPanelResolvedTab(
    string FolderPath,
    IReadOnlyList<string> ExpandedPaths,
    string? SelectedPath,
    double ScrollOffset);

/// <summary>
/// 復元時にパネルへ返すプロジェクト 1 つぶんの状態。
/// </summary>
/// <param name="Tabs">復元するタブ（1 枚以上あることが保証される）。</param>
/// <param name="ActiveTabIndex">アクティブにするタブの添字（必ず Tabs の範囲内）。</param>
public sealed record ProjectPanelResolvedState(
    IReadOnlyList<ProjectPanelResolvedTab> Tabs,
    int ActiveTabIndex);

// ── ストア本体 ─────────────────────────────────────────────────

/// <summary>
/// <see cref="ProjectPanelStateDocument"/> の読み書きと、パスの相対化・絶対化・間引きを担う。
///
/// <para>
/// 使い方: 起動時に <see cref="Load"/> → <see cref="TryGet"/> + <see cref="Resolve"/> で復元、
/// 状態が変わったら <see cref="BuildState"/> + <see cref="Set"/> → <see cref="Save"/>。
/// 保存の間引き（デバウンス）は呼び出し側（パネル）の責務。
/// </para>
/// </summary>
public sealed class ProjectPanelStateStore
{
    // ── 定数（マジックナンバー・マジックストリングの一元化）────

    /// <summary>設定フォルダ内のファイル名。</summary>
    public const string FileName = "project_panel_state.json";

    /// <summary>このコードが書き出す書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// 1 プロジェクトあたり復元するタブの上限枚数。
    /// 壊れた・肥大した JSON を読んでも起動が重くならないようにするための歯止めで、
    /// これを超えるぶんは黙って捨てる（保存側では切らない＝利用者のタブは失わない）。
    /// </summary>
    public const int MaxRestoredTabs = 32;

    /// <summary>スクロール位置の下限（負のオフセットは存在しない）。</summary>
    private const double MinScrollOffset = 0.0;

    /// <summary>復元できるタブが 1 枚も無かったことを表す添字。</summary>
    private const int NoTabIndex = -1;

    /// <summary>アセットルート自身を指す相対パス（＝空文字）。</summary>
    private const string RootRelativePath = "";

    /// <summary>JSON の読み書き設定（人が読める整形・末尾カンマ許容・日本語をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Encoder                     = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>読み込み済みの内容（ファイルが無ければ空）。</summary>
    private readonly ProjectPanelStateDocument _document;

    /// <summary>保存先の絶対パス。</summary>
    public string FilePath { get; }

    /// <summary>読み込み時に起きた問題（ファイル破損・未知バージョンなど）。空なら正常。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>直近の <see cref="Save"/> が失敗した理由（成功したら null）。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>生成は <see cref="Load"/> / <see cref="LoadFile"/> から。</summary>
    /// <param name="filePath">保存先の絶対パス。</param>
    /// <param name="document">読み込み済みの内容。</param>
    /// <param name="warnings">読み込み時の問題。</param>
    private ProjectPanelStateStore(
        string filePath, ProjectPanelStateDocument document, IReadOnlyList<string> warnings)
    {
        FilePath  = filePath;
        _document = document;
        Warnings  = warnings;
    }

    // ── 読み込み ─────────────────────────────────────────────

    /// <summary>設定フォルダを指定して読み込む（通常の入口）。</summary>
    /// <param name="settingsDir">editor/settings の絶対パス。</param>
    public static ProjectPanelStateStore Load(string settingsDir)
        => LoadFile(string.IsNullOrWhiteSpace(settingsDir)
            ? string.Empty
            : Path.Combine(settingsDir, FileName));

    /// <summary>
    /// ファイルパスを直接指定して読み込む（単体テスト用の入口でもある）。
    /// 読めない・壊れている場合は空の状態で返す（例外は投げない＝起動を止めない）。
    /// </summary>
    /// <param name="filePath">project_panel_state.json の絶対パス。</param>
    public static ProjectPanelStateStore LoadFile(string filePath)
    {
        var warnings = new List<string>();
        var empty    = new ProjectPanelStateDocument { FormatVersion = SupportedFormatVersion };

        // ① ファイルが無いのは初回起動の正常な状態なので警告にしない。
        if (string.IsNullOrWhiteSpace(filePath)) return new ProjectPanelStateStore("", empty, warnings);
        if (!File.Exists(filePath)) return new ProjectPanelStateStore(filePath, empty, warnings);

        // ② JSON を読む。破損・型不一致はここで例外になるのでまとめて拾う。
        ProjectPanelStateDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectPanelStateDocument>(
                File.ReadAllText(filePath), JsonOpts);
        }
        catch (Exception ex)
        {
            warnings.Add($"プロジェクトパネル状態の読み込みに失敗したため初期状態で起動: {filePath} — {ex.Message}");
            return new ProjectPanelStateStore(filePath, empty, warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"プロジェクトパネル状態が空のため初期状態で起動: {filePath}");
            return new ProjectPanelStateStore(filePath, empty, warnings);
        }

        // ③ 書式バージョンの確認。
        //    ・0（キー無し）＝ バージョン導入前のファイル。形は同じなのでそのまま読む。
        //    ・未来のバージョン＝ 項目が増えているだけの可能性が高いので、読める範囲は読む
        //      （ここで捨てると、新しいエディタで作業したあと古いエディタを 1 回起動しただけで
        //        タブ構成が消える。害の大きさが釣り合わない）。
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add(
                $"プロジェクトパネル状態の format_version={parsed.FormatVersion} は未知" +
                $"（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する");
        }

        // ④ null 安全化（手書きで "projects": null と書かれても落とさない）。
        parsed.Projects ??= new Dictionary<string, ProjectPanelProjectStateFile>(StringComparer.Ordinal);
        foreach (var state in parsed.Projects.Values)
        {
            state.Tabs ??= new List<ProjectPanelTabStateFile>();
            foreach (var tab in state.Tabs)
            {
                tab.Path     ??= RootRelativePath;
                tab.Expanded ??= new List<string>();
            }
        }

        return new ProjectPanelStateStore(filePath, parsed, warnings);
    }

    // ── プロジェクトエントリの出し入れ ────────────────────────

    /// <summary>保存済みのエントリを返す。無ければ null（＝復元するものが無い）。</summary>
    /// <param name="projectKey"><see cref="MakeProjectKey"/> で作ったキー。</param>
    public ProjectPanelProjectStateFile? TryGet(string? projectKey)
        => projectKey is not null && _document.Projects.TryGetValue(projectKey, out var state) ? state : null;

    /// <summary>
    /// エントリを差し替える。キーが null（プロジェクト未確定）なら何もしない。
    /// </summary>
    /// <param name="projectKey"><see cref="MakeProjectKey"/> で作ったキー。</param>
    /// <param name="state">書き込む状態。null なら何もしない。</param>
    public void Set(string? projectKey, ProjectPanelProjectStateFile? state)
    {
        if (projectKey is null || state is null) return;
        _document.Projects[projectKey] = state;
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
            // 書き出すたびに現行バージョンへ揃える（読み込み時に古い値を引き継いでいても直る）。
            _document.FormatVersion = SupportedFormatVersion;
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
    /// 絶対パス化してスラッシュ区切りの小文字へ揃える（EditorViewState.MakeSceneKey と同じ流儀）。
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
                       .Replace('\\', AssetUriPath.Separator)
                       .ToLowerInvariant();
        }
        catch
        {
            // 不正なパス文字などで正規化できない場合は素の文字列で妥協する（キーとしては十分）。
            return projectRootDir.Replace('\\', AssetUriPath.Separator).ToLowerInvariant();
        }
    }

    // ── 保存用: 絶対パス → 相対パス ───────────────────────────

    /// <summary>
    /// パネルの現在状態から保存用エントリを組み立てる。
    ///
    /// <para>
    /// アセットルート外のタブ（プロジェクト差し替え直後などに起こりうる）は落とし、
    /// それに伴ってアクティブ添字を詰め直す。1 枚も残らなければ null を返す
    /// （＝保存するものが無い。呼び出し側は前回の保存内容をそのまま残す）。
    /// </para>
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="tabs">左から順のタブ状態。</param>
    /// <param name="activeTabIndex">アクティブタブの添字。</param>
    public static ProjectPanelProjectStateFile? BuildState(
        string? assetsRoot, IReadOnlyList<ProjectPanelTabSnapshot>? tabs, int activeTabIndex)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || tabs is null || tabs.Count == 0) return null;

        var result    = new ProjectPanelProjectStateFile();
        int newActive = NoTabIndex;

        for (int i = 0; i < tabs.Count; i++)
        {
            var snapshot = tabs[i];
            var relative = AssetUriPath.ToRelative(assetsRoot, snapshot.FolderPath);
            if (relative is null) continue;   // アセットルート外 → 保存しても復元できないので落とす

            // アクティブだったタブは、間引き後の位置へ付け替える。
            if (i == activeTabIndex) newActive = result.Tabs.Count;

            result.Tabs.Add(new ProjectPanelTabStateFile
            {
                Path     = relative,
                Expanded = ToRelativeList(assetsRoot, snapshot.ExpandedPaths),
                Selected = AssetUriPath.ToRelative(assetsRoot, snapshot.SelectedPath),
                Scroll   = SanitizeScroll(snapshot.ScrollOffset),
            });
        }

        if (result.Tabs.Count == 0) return null;

        // アクティブタブ自体が落ちた場合は先頭を指す（範囲外を書き残さない）。
        result.ActiveTab = newActive >= 0 ? newActive : 0;
        return result;
    }

    /// <summary>絶対パスの集合を相対パスの一覧へ変換する（変換できないものは落とし、並びを安定させる）。</summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="absolutePaths">変換元の絶対パス集合。</param>
    private static List<string> ToRelativeList(string assetsRoot, IReadOnlyCollection<string>? absolutePaths)
    {
        var list = new List<string>();
        if (absolutePaths is null) return list;

        foreach (var abs in absolutePaths)
        {
            var relative = AssetUriPath.ToRelative(assetsRoot, abs);
            if (relative is null) continue;
            list.Add(relative);
        }

        // HashSet 由来で順序が不定だと、中身が同じでも差分が出て無駄な書き込みに見える。
        // 並べておくと JSON を人が読んだときにも追いやすい。
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // ── 復元用: 相対パス → 絶対パス（存在しないものは間引く）──

    /// <summary>
    /// 保存済みエントリを、いま実在するパスだけの復元指示へ変換する。
    ///
    /// <para>間引きの規則:</para>
    /// <list type="bullet">
    ///   <item>フォルダが消えているタブは<b>そのタブごと</b>捨てる</item>
    ///   <item>展開集合のうち消えているフォルダは、その要素だけ捨てる</item>
    ///   <item>選択アイテムが消えていれば選択なしにする</item>
    ///   <item>アクティブ添字は間引き後の位置へ付け替え、範囲外なら 0 にする</item>
    ///   <item>タブ数は <see cref="MaxRestoredTabs"/> 枚で頭打ちにする</item>
    /// </list>
    ///
    /// <para>1 枚も残らなければ null を返す（呼び出し側はルート 1 枚の既定状態で始める）。</para>
    /// </summary>
    /// <param name="saved">保存済みエントリ（null 可）。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    public static ProjectPanelResolvedState? Resolve(
        ProjectPanelProjectStateFile? saved, string? assetsRoot)
    {
        if (saved?.Tabs is null || saved.Tabs.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(assetsRoot) || !Directory.Exists(assetsRoot)) return null;

        var resolved  = new List<ProjectPanelResolvedTab>();
        int newActive = NoTabIndex;

        for (int i = 0; i < saved.Tabs.Count && resolved.Count < MaxRestoredTabs; i++)
        {
            var tab    = saved.Tabs[i];
            var folder = ToAbsoluteFolder(assetsRoot, tab.Path);
            if (folder is null) continue;   // 消えた／アセット外 → タブごとスキップ

            if (i == saved.ActiveTab) newActive = resolved.Count;

            resolved.Add(new ProjectPanelResolvedTab(
                folder,
                ResolveExpanded(assetsRoot, tab.Expanded),
                ResolveSelected(assetsRoot, tab.Selected),
                SanitizeScroll(tab.Scroll)));
        }

        if (resolved.Count == 0) return null;

        // アクティブだったタブが落ちた／添字が壊れている場合は先頭に倒す。
        int active = newActive >= 0 && newActive < resolved.Count ? newActive : 0;
        return new ProjectPanelResolvedState(resolved, active);
    }

    /// <summary>相対パスを実在するフォルダの絶対パスへ変換する（実在しなければ null）。</summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="relative">アセットルート相対パス（空文字ならルート自身）。</param>
    private static string? ToAbsoluteFolder(string assetsRoot, string? relative)
    {
        var abs = AssetUriPath.ToAbsolute(assetsRoot, relative);
        return abs is not null && Directory.Exists(abs) ? abs : null;
    }

    /// <summary>展開集合を絶対パスへ戻す（実在しないものと重複は落とす）。</summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="relatives">保存されていた相対パス一覧。</param>
    private static List<string> ResolveExpanded(string assetsRoot, List<string>? relatives)
    {
        var list = new List<string>();
        if (relatives is null) return list;

        foreach (var relative in relatives)
        {
            var abs = ToAbsoluteFolder(assetsRoot, relative);
            if (abs is null) continue;
            if (!list.Contains(abs, StringComparer.OrdinalIgnoreCase)) list.Add(abs);
        }
        return list;
    }

    /// <summary>
    /// 選択アイテムを絶対パスへ戻す。ファイル・フォルダのどちらでも実在すれば採用し、
    /// 消えていれば null（＝選択なし）にする。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="relative">保存されていた相対パス。</param>
    private static string? ResolveSelected(string assetsRoot, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var abs = AssetUriPath.ToAbsolute(assetsRoot, relative);
        if (abs is null) return null;
        return File.Exists(abs) || Directory.Exists(abs) ? abs : null;
    }

    /// <summary>
    /// スクロール位置を安全な値へ丸める。
    /// 手書き JSON や過去の不具合で NaN / 無限大 / 負値が入っていても、
    /// ScrollViewer へ渡した瞬間にレイアウトが壊れないようにする。
    /// </summary>
    /// <param name="value">元の値。</param>
    private static double SanitizeScroll(double value)
        => double.IsFinite(value) ? Math.Max(MinScrollOffset, value) : MinScrollOffset;
}
