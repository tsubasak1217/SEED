// ============================================================
//  PreviewRecentStore.cs — 最近プレビューしたプレハブの一覧の保存（プロジェクトごと）
//
//  【置き場と形】
//  editor/settings/screen_preview_recent.json（エディタの設定フォルダ。EditorPaths.SettingsDir）:
//    { "format_version": 1,
//      "projects": { "<アセットルートの正規化した絶対パス>": ["assets://..", ...] } }
//  キーはアセットルートを絶対パスにして区切りを '/'・小文字へ揃えたもの
//  （ProjectPanelStateStore.MakeProjectKey と同じ流儀。大小文字・区切りの揺れで別の行にならない）。
//
//  【振る舞い】
//  - 読むたび・書くたびにファイルを読み直す（書く直前に読み直して足すので、別のエディタが先に保存した追加は消さない）。
//    錠は掛けないので、2 つのエディタが「読み直し → 書く」をほぼ同時に行うと後から書いた方の内容になり、
//    もう一方の追加が 1 件消えることはある（失うのは最近の一覧の 1 件だけ。次に使えばまた載る）。
//  - 壊れている・読めないときは空の一覧で続ける（起動も右クリックも止めない）。
//  - 書き込みは書き込みごとに固有の名前の一時ファイル（<保存ファイル>.<固有>.tmp。同じフォルダ）へ書き切ってから置き換える。
//    エディタどうしが互いの書きかけを本体へ移さないので、ファイルが壊れた JSON になることはない（2026-10-02 のレビュー #18。
//    以前は一時ファイルの名前が固定だった）。置き換えに失敗したら一時ファイルは消し、理由を LastError に残す。
//  一覧の規則（先頭へ移す・重複・上限）は PreviewRecentList。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクし、ファイルのパスを一時フォルダへ差し替えて試す。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Preview;

/// <summary>
/// screen_preview_recent.json の全体（読み書き用の形）。
/// </summary>
public sealed class PreviewRecentDocument
{
    /// <summary>書式の版。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>アセットルートのキー → 最近使ったプレハブ（最近の順）。</summary>
    [JsonPropertyName("projects")]
    public Dictionary<string, List<string>>? Projects { get; set; }
}

/// <summary>
/// 最近プレビューしたプレハブの一覧を、プロジェクト（アセットルート）ごとに保存する。
/// </summary>
public sealed class PreviewRecentStore
{
    /// <summary>保存ファイルの名前（エディタの設定フォルダ直下）。</summary>
    public const string FileName = "screen_preview_recent.json";

    /// <summary>このコードが書く書式の版。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>書き切ってから置き換えるための一時ファイルの名前の書式（{0} = 保存ファイルのパス・{1} = 書き込みごとの固有の文字列）。</summary>
    private const string TempPathFormat = "{0}.{1}.tmp";

    /// <summary>一時ファイルの固有の文字列の書式（Guid の 32 桁の 16 進。区切りなし）。</summary>
    private const string TempTokenFormat = "N";

    /// <summary>キーの区切り（OS に依らず '/'）。</summary>
    private const char KeySeparator = '/';

    /// <summary>保存ファイルの絶対パス。</summary>
    public string FilePath { get; }

    /// <summary>直近の読み書きで起きた問題（無ければ null。呼び出し側がログへ出す）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 保存ファイルを指定して作る（テストは一時フォルダのパスを渡す）。
    /// </summary>
    /// <param name="filePath">保存ファイルの絶対パス。</param>
    public PreviewRecentStore(string filePath) => FilePath = filePath;

    /// <summary>
    /// エディタの設定フォルダの下に置く（通常の入口）。
    /// </summary>
    /// <param name="settingsDir">editor/settings の絶対パス（EditorPaths.SettingsDir）。</param>
    /// <returns>保存の窓口。</returns>
    public static PreviewRecentStore ForSettingsDir(string settingsDir) => new(Path.Combine(settingsDir, FileName));

    /// <summary>
    /// プロジェクトの一覧を読む。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>最近使ったプレハブ（最近の順。無ければ空）。</returns>
    public IReadOnlyList<string> Get(string? assetsRoot)
    {
        var key = MakeRootKey(assetsRoot);
        if (key is null) return Array.Empty<string>();
        var document = Read();
        return document.Projects!.TryGetValue(key, out var list)
            ? PreviewRecentList.Normalize(list)
            : Array.Empty<string>();
    }

    /// <summary>
    /// 使ったプレハブを先頭へ足して保存する。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="virtualPath">使ったプレハブ（assets:// 仮想パス）。</param>
    /// <returns>新しい一覧（保存に失敗しても新しい一覧を返す。理由は <see cref="LastError"/>）。</returns>
    public IReadOnlyList<string> Push(string? assetsRoot, string virtualPath)
    {
        var key = MakeRootKey(assetsRoot);
        if (key is null) return Array.Empty<string>();

        // 書く直前に読み直す（ほかのエディタが先に保存した追加を消さない。ほぼ同時に書いたときは後の方の内容になる）
        var document = Read();
        document.Projects!.TryGetValue(key, out var current);
        var updated = PreviewRecentList.Push(current, virtualPath);
        document.Projects[key] = new List<string>(updated);
        Write(document);
        return updated;
    }

    /// <summary>
    /// 書き込み 1 回ぶんの一時ファイルのパス（&lt;保存ファイル&gt;.&lt;固有の 32 桁&gt;.tmp。保存ファイルと同じフォルダ）。
    /// 名前が書き込みごとに違うので、エディタを複数開いていても互いの書きかけを本体へ移さない
    /// （2026-10-02 のレビュー #18。以前は screen_preview_recent.json.tmp の固定で、一方の置き換えがもう一方の書きかけを本体へ移すことがあった）。
    /// 同じフォルダに置くのは、置き換えを名前の付け替え（同じボリューム）で済ませるため。
    /// </summary>
    /// <param name="filePath">保存ファイルの絶対パス。</param>
    /// <returns>一時ファイルの絶対パス。</returns>
    public static string MakeTempPath(string filePath) =>
        string.Format(CultureInfo.InvariantCulture, TempPathFormat, filePath, Guid.NewGuid().ToString(TempTokenFormat));

    /// <summary>
    /// アセットルートからキーを作る（絶対パス化・末尾の区切りを落とす・'/' 区切り・小文字）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <returns>キー（空なら null）。</returns>
    public static string? MakeRootKey(string? assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot)) return null;
        string full;
        try { full = Path.GetFullPath(assetsRoot); }
        catch { full = assetsRoot; }   // 正規化できない文字を含んでも、キーとしては素の文字列で足りる
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Replace('\\', KeySeparator)
                   .ToLowerInvariant();
    }

    // ============================================================
    //  ファイル
    // ============================================================

    /// <summary>
    /// ファイルを読む（無い・壊れている・読めないときは空の内容。例外は投げない）。
    /// </summary>
    /// <returns>内容（Projects は null にしない。キーは大小文字を無視して引ける）。</returns>
    private PreviewRecentDocument Read()
    {
        LastError = null;
        var empty = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilePath))
            return new PreviewRecentDocument { FormatVersion = SupportedFormatVersion, Projects = empty };

        try
        {
            var parsed = JsonSerializer.Deserialize<PreviewRecentDocument>(File.ReadAllText(FilePath), JsonOptions);
            if (parsed?.Projects is { } projects)
            {
                foreach (var (key, list) in projects)
                {
                    // 手で書き換えられた null の行は捨てる
                    if (string.IsNullOrWhiteSpace(key) || list is null) continue;
                    empty[key] = list;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 壊れていたら空で続ける（次の書き込みで正しい形に戻る）
            LastError = $"最近のプレビューの一覧を読めないので空で続けます: {FilePath} — {ex.Message}";
        }
        return new PreviewRecentDocument { FormatVersion = SupportedFormatVersion, Projects = empty };
    }

    /// <summary>
    /// ファイルへ書く（書き込みごとに固有の一時ファイルへ書き切ってから置き換える。失敗は <see cref="LastError"/> へ）。
    /// 置き換えに失敗した（別のプロセスが本体を開いている等）ときは一時ファイルを消す（名前が毎回違うので、残すと溜まる）。
    /// </summary>
    /// <param name="document">書く内容。</param>
    private void Write(PreviewRecentDocument document)
    {
        string? temp = null;
        try
        {
            document.FormatVersion = SupportedFormatVersion;
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            temp = MakeTempPath(FilePath);
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            temp = null;   // 置き換えた（一時ファイルはもう無い）
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = $"最近のプレビューの一覧を保存できません: {FilePath} — {ex.Message}";
        }
        finally
        {
            if (temp is not null) TryDeleteTemp(temp);
        }
    }

    /// <summary>置き換えられなかった一時ファイルを消す（消せなくても保存の結果は変えない）。</summary>
    /// <param name="temp">一時ファイルの絶対パス。</param>
    private static void TryDeleteTemp(string temp)
    {
        try { File.Delete(temp); }
        catch (IOException) { /* 消せなければ残す（中身は書き切った JSON なので害は無い） */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>JSON の読み書きの設定（読みはコメント・末尾カンマを許す。書きは字下げ）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        // 日本語のフォルダ名を \uXXXX にせず読める形で書く（設定ファイルは人も見る）
        Encoder                     = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
