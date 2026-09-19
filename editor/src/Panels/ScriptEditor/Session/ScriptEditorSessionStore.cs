// ============================================================
//  ScriptEditorSessionStore.cs — スクリプトエディタの「開いていたタブ」を再起動をまたいで保持するストア
//
//  【何を持つか】
//    ・開いていたタブの絶対パス（表示順そのまま）
//    ・アクティブだったタブ
//    ・各タブのキャレット（行・桁）と縦スクロール位置
//    ・そのタブが読み取り専用だったか（F12 で開いたエンジン API のソース等）
//
//  【なぜプロジェクトの cache へ置くのか】
//    「どのスクリプトを開いていたか」は作業者個人の見え方であって、プロジェクトの内容ではない。
//    一方でプロジェクトごとに中身が全く違うので、エディタ共通の設定フォルダに置くと
//    プロジェクトをキーにした辞書が必要になる。`<プロジェクトルート>/cache/editor/` は
//    シーンロック（cache/editor/scene_locks）・視点サイドカー（cache/editor/view）・
//    VCS の状態（cache/editor/vcs）と同じ「利用者別の状態」の置き場で、
//    初期コミットから `.loreignore` 済み＝バージョン管理に載らない。ここが一番素直。
//
//  【なぜパスを相対で保存するのか】
//    絶対パスだけで保存すると、プロジェクトフォルダを移動・リネームした瞬間に全タブが無効になる。
//    プロジェクトルート相対（スラッシュ区切り）で持てば移動先でもそのまま復元できる。
//    ただし F12 で開いたエンジン API のソースのように**ルートの外**にあるファイルは
//    相対化しても意味が無い（`..` を連ねた脆いパスになる）ので、絶対パスのまま保存する。
//    読み込み側は「相対ならルートと結合」「結合した結果がルートの外へ出たら捨てる」で受ける。
//
//  【なぜ SafeFileWriter を使わないのか】
//    `editor/src/Assets/SafeFileWriter.cs` の `WriteAllTextAtomic` は `.backup` 世代を残す。
//    アセット（失うと困る成果物）向けの仕組みで、**キャッシュには不要**どころか有害で、
//    cache/editor/ の下に誰も読まない .backup が溜まり続ける。
//    ここで欲しいのは「書き途中のファイルを残さない」ことだけなので、
//    `<path>.tmp` へ書いて `File.Move(temp, path, overwrite: true)` する最小の原子的置換を自前で持つ。
//
//  【WPF 非依存】
//    単体テスト（editor/tests/TextEditorLogicTests）から直接リンクするため、
//    WPF 型・AvalonEdit・DispatcherTimer・EditorLog へは依存しない。
//    「いつ保存するか」「どうエディタへ戻すか」は Panels/ScriptEditorPanel.Session.cs の責務で、
//    このファイルは「JSON の読み書き」「パスの相対化・絶対化」「値の正規化と間引き」だけを持つ。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Panels.ScriptEditor.Session;

// ── JSON モデル ────────────────────────────────────────────────
//
//  ファイルに書く形そのもの。パスは「プロジェクトルート相対」または「絶対」で、
//  どちらもスラッシュ区切りへ正規化してある（JSON 中でバックスラッシュを
//  エスケープせずに済み、人が読んで差分も追いやすい）。

/// <summary>
/// 保存ファイル中のタブ 1 枚ぶん。JSON の <c>"tabs"</c> 配列の要素に対応する。
/// </summary>
public sealed class ScriptEditorSessionTabFile
{
    /// <summary>
    /// ファイルの場所。プロジェクトルート配下なら**ルート相対**、
    /// ルート外（エンジン API のソース等）なら**絶対パス**。どちらもスラッシュ区切り。
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>キャレットの行（1 起点）。</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>キャレットの桁（1 起点）。</summary>
    [JsonPropertyName("column")]
    public int Column { get; set; }

    /// <summary>エディタの縦スクロール位置（px）。</summary>
    [JsonPropertyName("scroll")]
    public double Scroll { get; set; }

    /// <summary>
    /// 読み取り専用タブだったか。
    /// 復元側はこれが true のタブを <c>OpenFileReadOnly</c> で開き直す
    /// （エンジン API のソースを編集可能な状態で復活させないため）。
    /// </summary>
    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; set; }
}

/// <summary>
/// script_editor_session.json のルート。
/// </summary>
public sealed class ScriptEditorSessionDocument
{
    /// <summary>
    /// ファイル書式のバージョン。項目の意味を変える改訂を入れたときに旧ファイルを識別するために持つ。
    /// 0（＝キーが無い）は「バージョン導入前のファイル」として v1 相当に読む。
    /// </summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>アクティブだったタブの添字（<see cref="Tabs"/> に対する 0 始まり）。</summary>
    [JsonPropertyName("active_tab")]
    public int ActiveTab { get; set; }

    /// <summary>
    /// 開いていたタブ（左から順）。
    ///
    /// <para>
    /// スクリプトエディタに**タブの並べ替え機能は無い**ので、
    /// この並び ＝ 開いた順 ＝ パネル内部の <c>_docs</c> の並びである。
    /// 並べ替えが入ったら保存の契機（開く/閉じる/切り替える）に「並べ替え」を足すこと。
    /// </para>
    /// </summary>
    [JsonPropertyName("tabs")]
    public List<ScriptEditorSessionTabFile> Tabs { get; set; } = new();
}

// ── 受け渡し用の型（絶対パス）──────────────────────────────────
//
//  パネル側は常に絶対パスで状態を持つ。相対化・絶対化の境界をこの型で明示する。

/// <summary>
/// 保存時にパネルから受け取るタブ 1 枚ぶんの状態。
/// </summary>
/// <param name="FilePath">ファイルの絶対パス。</param>
/// <param name="CaretLine">キャレットの行（1 起点）。</param>
/// <param name="CaretColumn">キャレットの桁（1 起点）。</param>
/// <param name="ScrollOffset">エディタの縦スクロール位置（px）。</param>
/// <param name="IsReadOnly">読み取り専用タブか。</param>
public sealed record ScriptEditorSessionTabSnapshot(
    string FilePath,
    int    CaretLine,
    int    CaretColumn,
    double ScrollOffset,
    bool   IsReadOnly);

/// <summary>
/// 復元時にパネルへ返すタブ 1 枚ぶんの指示（パスは絶対・値は正規化済み）。
/// </summary>
/// <param name="FilePath">開くファイルの絶対パス。</param>
/// <param name="CaretLine">キャレットの行（1 以上）。</param>
/// <param name="CaretColumn">キャレットの桁（1 以上）。</param>
/// <param name="ScrollOffset">縦スクロール位置（0 以上の有限値）。</param>
/// <param name="IsReadOnly">読み取り専用タブとして開くか。</param>
public sealed record ScriptEditorSessionResolvedTab(
    string FilePath,
    int    CaretLine,
    int    CaretColumn,
    double ScrollOffset,
    bool   IsReadOnly);

/// <summary>
/// 復元時にパネルへ返すセッション全体。
/// </summary>
/// <param name="Tabs">復元するタブ（1 枚以上あることが保証される）。</param>
/// <param name="ActiveTabIndex">アクティブにするタブの添字（必ず <paramref name="Tabs"/> の範囲内）。</param>
public sealed record ScriptEditorSessionState(
    IReadOnlyList<ScriptEditorSessionResolvedTab> Tabs,
    int ActiveTabIndex);

// ── ストア本体 ─────────────────────────────────────────────────

/// <summary>
/// <see cref="ScriptEditorSessionDocument"/> の読み書きと、
/// パスの相対化・絶対化、値の正規化、復元枚数の頭打ちを担う。
///
/// <para>
/// 使い方: 起動時に <see cref="FilePathFor"/> → <see cref="LoadFile"/> → <see cref="Resolve"/> で復元、
/// タブが変わったら <see cref="Build"/> → <see cref="Save"/>。
/// 保存の間引き（デバウンス）と実際のエディタ操作は呼び出し側（パネル）の責務。
/// </para>
/// </summary>
public static class ScriptEditorSessionStore
{
    // ── 定数（マジックナンバー・マジックストリングの一元化）────

    /// <summary>保存ファイル名。</summary>
    public const string FileName = "script_editor_session.json";

    /// <summary>
    /// 利用者別の状態を置くフォルダ名（プロジェクトルート直下）。
    /// <c>SEEDEditor.Project.ProjectPaths.CACHE_DIR_NAME</c> と同じ値。
    /// このファイルは ProjectPaths に依存できない（単体テストへ単独でリンクするため）ので
    /// 値を写しているが、**変えるときは両方そろえること**（SceneLock.cs と同じ事情）。
    /// </summary>
    public const string CACHE_DIR_NAME = "cache";

    /// <summary>キャッシュのうちエディタ用の区画（シーンロック・視点サイドカーと同じ階層）。</summary>
    public const string EDITOR_DIR_NAME = "editor";

    /// <summary>このコードが書き出す書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// 復元するタブの上限枚数。
    /// 壊れた・肥大した JSON を読んでも起動が重くならないようにするための歯止めで、
    /// これを超えるぶんは黙って捨てる（保存側では切らない＝利用者のタブは失わない）。
    /// </summary>
    public const int MaxRestoredTabs = 32;

    /// <summary>キャレット行の下限（AvalonEdit の行番号は 1 起点）。</summary>
    public const int MinCaretLine = 1;

    /// <summary>キャレット桁の下限（AvalonEdit の桁番号は 1 起点）。</summary>
    public const int MinCaretColumn = 1;

    /// <summary>
    /// キャレット行・桁の上限。
    /// 手書きや過去の不具合で `2147483647` のような値が入っていても、
    /// そのまま計算に混ぜてオーバーフローさせないための歯止め。
    /// 実際の行数への丸めは、本文を読み込んだ後にパネル側（MoveCaretTo）が行う。
    /// </summary>
    public const int MaxCaretPosition = 100_000_000;

    /// <summary>スクロール位置の下限（負のオフセットは存在しない）。</summary>
    public const double MinScrollOffset = 0.0;

    /// <summary>
    /// スクロール位置の上限（px）。
    /// NaN・無限大・1e300 のような値を WPF のレイアウト計算へ渡すと
    /// ScrollViewer の内部状態が壊れるため、現実的な上限で頭打ちにする
    /// （1 行 20px 換算で 5000 万行ぶん。実在するファイルでは到達しない）。
    /// </summary>
    public const double MaxScrollOffset = 1_000_000_000.0;

    /// <summary>復元できるタブが 1 枚も無かったことを表す添字。</summary>
    private const int NoTabIndex = -1;

    /// <summary>保存するパスの区切り文字（JSON 中でエスケープが不要なスラッシュに統一する）。</summary>
    private const char PathSeparator = '/';

    /// <summary>原子的置換で使う一時ファイルの接尾辞。</summary>
    private const string TempFileSuffix = ".tmp";

    /// <summary>ルート外へ出る相対パスの目印（`..`）。</summary>
    private const string ParentDirectoryToken = "..";

    /// <summary>JSON の読み書き設定（人が読める整形・末尾カンマ許容・日本語をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Encoder                     = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 置き場 ───────────────────────────────────────────────

    /// <summary>
    /// プロジェクトルートから保存ファイルの絶対パスを組み立てる。
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。</param>
    /// <returns>
    /// <c>&lt;ルート&gt;/cache/editor/script_editor_session.json</c>。
    /// ルートが空（プロジェクト未確定）・パスとして扱えない場合は null
    /// ＝「保存も復元もしない」を表す。
    /// </returns>
    public static string? FilePathFor(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return null;
        try
        {
            return Path.Combine(
                Path.GetFullPath(projectRoot), CACHE_DIR_NAME, EDITOR_DIR_NAME, FileName);
        }
        catch
        {
            // 不正なパス文字などで扱えない場合は「置き場が無い」として扱う（例外で起動を止めない）。
            return null;
        }
    }

    // ── 読み込み ─────────────────────────────────────────────

    /// <summary>
    /// 保存ファイルを読む。
    ///
    /// <para>
    /// 読めない・壊れている・空ならすべて null（＝復元なし）を返す。**例外は投げない**。
    /// この機能の失敗でエディタが起動しないのは論外なので、呼び出し側は
    /// 戻り値が null なら「前回のタブは分からなかった」として何も開かなければよい。
    /// </para>
    /// </summary>
    /// <param name="filePath"><see cref="FilePathFor"/> が返したパス。</param>
    /// <param name="warning">
    /// 利用者・開発者へ伝える価値のある問題（JSON 破損・未知バージョン）。
    /// 問題が無い場合と「ファイルがまだ無い（初回起動）」場合は null。
    /// </param>
    /// <returns>読み込んだ内容。復元できるものが無ければ null。</returns>
    public static ScriptEditorSessionDocument? LoadFile(string? filePath, out string? warning)
    {
        warning = null;

        // ① 置き場が無い／まだ書かれていないのは初回起動の正常な状態なので警告にしない。
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (!File.Exists(filePath)) return null;

        // ② JSON を読む。破損・型不一致・空ファイルはここで例外になるのでまとめて拾う。
        ScriptEditorSessionDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ScriptEditorSessionDocument>(
                File.ReadAllText(filePath), JsonOpts);
        }
        catch (Exception ex)
        {
            warning = $"スクリプトエディタのセッションを読めませんでした（前回のタブは復元しません）: {filePath} — {ex.Message}";
            return null;
        }

        // 中身が `null` とだけ書かれている場合。復元するものが無いだけなので警告にしない。
        if (parsed is null) return null;

        // ③ 書式バージョンの確認。
        //    未来のバージョン＝項目が増えているだけの可能性が高いので、読める範囲は読む
        //    （ここで捨てると、新しいエディタで作業したあと古いエディタを 1 回起動しただけで
        //      開いていたタブが消える。害の大きさが釣り合わない）。
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warning =
                $"スクリプトエディタのセッションの format_version={parsed.FormatVersion} は未知" +
                $"（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する";
        }

        // ④ null 安全化（手書きで "tabs": null と書かれても落とさない）。
        parsed.Tabs ??= new List<ScriptEditorSessionTabFile>();
        foreach (var tab in parsed.Tabs) tab.Path ??= string.Empty;

        return parsed;
    }

    // ── 保存 ─────────────────────────────────────────────────

    /// <summary>
    /// パネルの現在状態から保存用の内容を組み立てる。
    ///
    /// <para>
    /// パスとして扱えないタブは落とし、それに伴ってアクティブ添字を詰め直す。
    /// 1 枚も残らなければ null を返す（＝書くものが無い）。
    /// 復元枚数の頭打ち（<see cref="MaxRestoredTabs"/>）は**ここでは掛けない**。
    /// 保存側で切ると利用者が実際に開いていたタブを失うため、間引きは復元側だけで行う。
    /// </para>
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス（空なら全タブを絶対パスで保存する）。</param>
    /// <param name="tabs">左から順のタブ状態。</param>
    /// <param name="activeTabIndex">アクティブタブの添字（無ければ負値）。</param>
    /// <returns>書き出す内容。書くものが無ければ null。</returns>
    public static ScriptEditorSessionDocument? Build(
        string? projectRoot,
        IReadOnlyList<ScriptEditorSessionTabSnapshot>? tabs,
        int activeTabIndex)
    {
        if (tabs is null || tabs.Count == 0) return null;

        var document  = new ScriptEditorSessionDocument { FormatVersion = SupportedFormatVersion };
        int newActive = NoTabIndex;

        for (int i = 0; i < tabs.Count; i++)
        {
            var snapshot = tabs[i];
            var stored   = ToStoredPath(projectRoot, snapshot.FilePath);
            if (stored is null) continue;   // パスとして扱えない → 復元できないので落とす

            // アクティブだったタブは、間引き後の位置へ付け替える。
            if (i == activeTabIndex) newActive = document.Tabs.Count;

            document.Tabs.Add(new ScriptEditorSessionTabFile
            {
                Path     = stored,
                Line     = SanitizeCaret(snapshot.CaretLine,   MinCaretLine),
                Column   = SanitizeCaret(snapshot.CaretColumn, MinCaretColumn),
                Scroll   = SanitizeScroll(snapshot.ScrollOffset),
                ReadOnly = snapshot.IsReadOnly,
            });
        }

        if (document.Tabs.Count == 0) return null;

        // アクティブタブ自体が落ちた／指定が無かった場合は先頭を指す（範囲外を書き残さない）。
        document.ActiveTab = newActive >= 0 ? newActive : 0;
        return document;
    }

    /// <summary>
    /// 内容をファイルへ書き出す（原子的置換）。
    ///
    /// <para>
    /// 一時ファイルへ書いてから <see cref="File.Move(string, string, bool)"/> で置き換える。
    /// 書き込み中に落ちても、前回の完全な内容が残る（中途半端な JSON を読むことがない）。
    /// </para>
    /// </summary>
    /// <param name="filePath">保存先の絶対パス。</param>
    /// <param name="document">書き出す内容。</param>
    /// <param name="error">失敗理由（成功したら null）。</param>
    /// <returns>成功したら true。</returns>
    public static bool Save(string? filePath, ScriptEditorSessionDocument? document, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filePath)) { error = "保存先が未設定です"; return false; }
        if (document is null)                    { error = "保存する内容がありません"; return false; }

        var temp = filePath + TempFileSuffix;
        try
        {
            // 書き出すたびに現行バージョンへ揃える（読み込み時に古い値を引き継いでいても直る）。
            document.FormatVersion = SupportedFormatVersion;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOpts));
            File.Move(temp, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // 書けたが置き換えに失敗した場合、一時ファイルを残さない（次回の書き込みを妨げない）。
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 後始末の失敗は無視 */ }
            return false;
        }
    }

    /// <summary>
    /// 保存ファイルを消す。
    ///
    /// <para>
    /// 「タブを 1 枚も開いていない」も立派な前回の状態なので、
    /// 中身が空の JSON を書く代わりにファイルごと消して表現する
    /// （<see cref="Build"/> が null を返す＝書くものが無い、と意味が一致する）。
    /// </para>
    /// </summary>
    /// <param name="filePath">保存先の絶対パス。</param>
    /// <param name="error">失敗理由（成功・元から無い場合は null）。</param>
    /// <returns>成功したら true（元からファイルが無い場合も true）。</returns>
    public static bool Delete(string? filePath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filePath)) { error = "保存先が未設定です"; return false; }

        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── 復元 ─────────────────────────────────────────────────

    /// <summary>
    /// 読み込んだ内容を、パネルがそのまま実行できる復元指示へ変換する。
    ///
    /// <para>間引きの規則:</para>
    /// <list type="bullet">
    ///   <item>パスとして扱えない／`..` でプロジェクトルートの外へ出る相対パスは捨てる</item>
    ///   <item>行・桁・スクロールは異常値（0 以下・NaN・巨大値）を正規化する</item>
    ///   <item>タブ数は <see cref="MaxRestoredTabs"/> 枚で頭打ちにする</item>
    ///   <item>アクティブ添字は間引き後の位置へ付け替え、範囲外なら先頭へ倒す</item>
    /// </list>
    ///
    /// <para>
    /// **実在しないファイルは捨てない**。パネル側が <c>OpenFile</c> で開けば
    /// 「ファイルが見つかりません」タブになり、ブランチを戻せばそのまま復活する。
    /// ここで消すと、ブランチを切り替えただけでタブ構成が失われる。
    /// </para>
    ///
    /// <para>1 枚も残らなければ null を返す（＝復元なし）。</para>
    /// </summary>
    /// <param name="document"><see cref="LoadFile"/> の戻り値（null 可）。</param>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。</param>
    /// <returns>復元指示。復元するものが無ければ null。</returns>
    public static ScriptEditorSessionState? Resolve(
        ScriptEditorSessionDocument? document, string? projectRoot)
    {
        if (document?.Tabs is null || document.Tabs.Count == 0) return null;

        var resolved  = new List<ScriptEditorSessionResolvedTab>();
        int newActive = NoTabIndex;

        for (int i = 0; i < document.Tabs.Count && resolved.Count < MaxRestoredTabs; i++)
        {
            var tab  = document.Tabs[i];
            var path = ToRestoredPath(projectRoot, tab.Path);
            if (path is null) continue;   // 扱えない／ルート外へ出る相対 → このタブは捨てる

            if (i == document.ActiveTab) newActive = resolved.Count;

            resolved.Add(new ScriptEditorSessionResolvedTab(
                path,
                SanitizeCaret(tab.Line,   MinCaretLine),
                SanitizeCaret(tab.Column, MinCaretColumn),
                SanitizeScroll(tab.Scroll),
                tab.ReadOnly));
        }

        if (resolved.Count == 0) return null;

        // アクティブだったタブが落ちた／添字が壊れている／上限で切られた場合は先頭に倒す。
        int active = newActive >= 0 && newActive < resolved.Count ? newActive : 0;
        return new ScriptEditorSessionState(resolved, active);
    }

    // ── パスの相対化・絶対化 ──────────────────────────────────

    /// <summary>
    /// 絶対パスを保存する形へ変換する。
    /// プロジェクトルート配下ならルート相対、ルート外なら絶対パスのまま。
    /// どちらもスラッシュ区切りへ正規化する。
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス（空なら常に絶対パスで保存）。</param>
    /// <param name="absolutePath">タブが開いているファイルの絶対パス。</param>
    /// <returns>保存する文字列。パスとして扱えなければ null。</returns>
    private static string? ToStoredPath(string? projectRoot, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        string full;
        try { full = Path.GetFullPath(absolutePath); }
        catch { return null; }

        var relative = TryGetRelativePathInside(projectRoot, full);
        return (relative ?? full).Replace('\\', PathSeparator);
    }

    /// <summary>
    /// 保存されていた文字列を、開くための絶対パスへ戻す。
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。</param>
    /// <param name="stored">保存されていた相対または絶対パス。</param>
    /// <returns>
    /// 絶対パス。扱えない・`..` でプロジェクトルートの外へ出る相対パスなら null。
    /// ルート外を指す相対パスを受け入れると、壊れた・細工された JSON で
    /// プロジェクトと無関係なファイルを勝手に開いてしまうため、必ず捨てる。
    /// </returns>
    private static string? ToRestoredPath(string? projectRoot, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        try
        {
            // 保存時にスラッシュへ正規化してあるので、この環境の区切りへ戻す。
            var native = stored.Replace(PathSeparator, Path.DirectorySeparatorChar);

            // 絶対パス（ルート外のエンジン API ソース等）はそのまま使う。
            if (Path.IsPathRooted(native)) return Path.GetFullPath(native);

            // 相対パスはプロジェクトルートが分かっているときだけ復元できる。
            if (string.IsNullOrWhiteSpace(projectRoot)) return null;

            var combined = Path.GetFullPath(Path.Combine(Path.GetFullPath(projectRoot), native));
            return IsInside(projectRoot, combined) ? combined : null;
        }
        catch
        {
            // 不正なパス文字などで扱えないときは、そのタブを諦める（例外で復元全体を壊さない）。
            return null;
        }
    }

    /// <summary>
    /// <paramref name="root"/> から見た <paramref name="target"/> の相対パスを返す。
    /// ルートの外（上位・別ドライブ）やルート自身なら null（＝相対化しない）。
    /// 判定の作りは <c>editor/src/Scene/SceneLock.cs</c> と同じ。
    /// </summary>
    /// <param name="root">基準フォルダ（空なら相対化しない）。</param>
    /// <param name="target">対象の絶対パス。</param>
    /// <returns>相対パス。ルート外なら null。</returns>
    private static string? TryGetRelativePathInside(string? root, string target)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));

            // ルート自身・空は「中のファイル」ではない。
            if (string.IsNullOrEmpty(relative) || relative == ".") return null;
            // 別ドライブだと絶対パスがそのまま返る。
            if (Path.IsPathRooted(relative)) return null;
            // ".." で始まるならルートの外。"..foo" のようなファイル名を誤判定しないよう
            // 直後が区切り（または終端）であることまで確かめる。
            if (relative.StartsWith(ParentDirectoryToken, StringComparison.Ordinal)
                && (relative.Length == ParentDirectoryToken.Length
                    || relative[ParentDirectoryToken.Length] == Path.DirectorySeparatorChar
                    || relative[ParentDirectoryToken.Length] == Path.AltDirectorySeparatorChar))
            {
                return null;
            }
            return relative;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="fullPath"/> が <paramref name="root"/> の配下かどうか。
    /// </summary>
    /// <param name="root">基準フォルダ。</param>
    /// <param name="fullPath">判定する絶対パス。</param>
    /// <returns>配下なら true。</returns>
    private static bool IsInside(string root, string fullPath)
    {
        // 末尾に区切りを付けてから前方一致を取る。
        // 付けないと "C:\proj2" が "C:\proj" の配下だと誤判定される。
        var normalized = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    // ── 値の正規化 ────────────────────────────────────────────

    /// <summary>
    /// キャレットの行・桁を安全な範囲へ丸める。
    /// 手書き JSON や過去の不具合で 0・負値・巨大値が入っていても、
    /// オフセット計算に混ざって落ちないようにする。
    /// </summary>
    /// <param name="value">元の値。</param>
    /// <param name="minimum">下限（行・桁とも 1 起点）。</param>
    /// <returns>丸めた値。</returns>
    private static int SanitizeCaret(int value, int minimum)
        => Math.Clamp(value, minimum, MaxCaretPosition);

    /// <summary>
    /// スクロール位置を安全な値へ丸める。
    /// NaN / 無限大 / 負値をそのまま ScrollViewer へ渡すとレイアウトが壊れるため、
    /// 有限でなければ先頭（0）に倒す。
    /// </summary>
    /// <param name="value">元の値。</param>
    /// <returns>丸めた値。</returns>
    private static double SanitizeScroll(double value)
        => double.IsFinite(value)
            ? Math.Clamp(value, MinScrollOffset, MaxScrollOffset)
            : MinScrollOffset;
}
