// ============================================================
//  ShellOpenCatalog.cs — 「OS の関連付けで開く」拡張子のカタログ（正典）
//
//  【役割】
//  プロジェクトパネルでダブルクリックされたファイルのうち、
//  エディタ自身が開かず **OS の既定のアプリへ丸投げする** 形式を 1 か所で決める。
//    ・.blend    … Blender（関連付けがあれば起動する）
//    ・画像      … 既定のフォトビューア
//    ・音声      … 既定のプレイヤー（タイルの試聴ボタンとは別動線）
//
//  【なぜ 1 か所へ集めるのか】
//  この判定はダブルクリック分岐（ProjectPanel）と、右クリックメニューや
//  マテリアル編集（OpenMaterialFile）など複数の動線から引かれる。
//  各所へ拡張子を書くと、片方だけ増えて「ダブルクリックでは開くのに
//  メニューからは開かない」といった食い違いが必ず起きる。
//
//  【画像・音声は自前で並べない（二重管理の禁止）】
//  画像と音声の拡張子は Assets/AssetPreviewKinds.cs が唯一の情報源。
//  ここは「その分類を関連付け対象に含めるか」という真偽値だけを持ち、
//  拡張子そのものは持たない。サムネイル対象へ新しい画像形式を足せば、
//  関連付けで開く対象にも自動で入る。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//  WPF 型・EditorLog などエディタ本体のシングルトンへ依存しない。
//  読み込み時の問題は Warnings に積み、呼び出し側がログへ出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Assets;

/// <summary>
/// shell_open_extensions.json の 1 エントリ（関連付けで開く拡張子）。
/// </summary>
public sealed class ShellOpenEntry
{
    /// <summary>拡張子（ドットの有無はどちらでもよい。読み込み時に正規化する）。</summary>
    [JsonPropertyName("extension")]
    public string? Extension { get; set; }

    /// <summary>何のための形式かの覚書（動作には影響しない）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// shell_open_extensions.json のファイル全体に対応するモデル（デシリアライズ専用）。
/// </summary>
public sealed class ShellOpenCatalogFile
{
    /// <summary>ファイル書式のバージョン。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>
    /// 画像（<c>AssetPreviewKinds</c> の画像分類）を関連付けで開くか。
    /// null は未指定＝組み込み既定（true）。
    /// </summary>
    [JsonPropertyName("include_image_extensions")]
    public bool? IncludeImageExtensions { get; set; }

    /// <summary>
    /// 音声（<c>AssetPreviewKinds</c> の音声分類）を関連付けで開くか。
    /// null は未指定＝組み込み既定（true）。
    /// </summary>
    [JsonPropertyName("include_audio_extensions")]
    public bool? IncludeAudioExtensions { get; set; }

    /// <summary>分類に属さない個別の拡張子一覧（.blend など）。</summary>
    [JsonPropertyName("extensions")]
    public List<ShellOpenEntry>? Extensions { get; set; }
}

/// <summary>
/// OS の関連付けで開く拡張子のカタログ。
///
/// <para>
/// 生成は <see cref="LoadFromDir"/>（通常）か <see cref="BuiltIn"/>（フォールバック・テスト）。
/// 判定は <see cref="ShouldOpenWithShell"/> / <see cref="ShouldOpenPathWithShell"/>。
/// 実際に起動する処理は <see cref="ShellOpenLauncher"/> が担う（判定と実行の分離）。
/// </para>
/// </summary>
public sealed class ShellOpenCatalog
{
    // ── ファイル名・既定値（マジックナンバーの一元化）──────────

    /// <summary>カタログファイルの名前（editor/config 直下）。</summary>
    public const string FileName = "shell_open_extensions.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>画像分類を含めるかの組み込み既定。</summary>
    public const bool BuiltInIncludeImages = true;

    /// <summary>音声分類を含めるかの組み込み既定。</summary>
    public const bool BuiltInIncludeAudio = true;

    // ── 組み込み既定 ─────────────────────────────────────────
    //
    //  editor/config/shell_open_extensions.json と同じ内容を持つ。
    //  「JSON を消してもダブルクリックは従来どおり動く」ことを保証する最後の砦。

    /// <summary>
    /// 分類（画像・音声）に属さない、個別に関連付けで開く拡張子。
    ///
    /// <para>
    /// <c>.blend1</c> 以降は Blender の世代バックアップで、プロジェクトパネルでは
    /// 既定で非表示（<c>ProjectPanelVisibilityRules</c>）。「隠しファイルを表示」で
    /// 出したときにダブルクリックの挙動が本体と変わらないよう、ここには載せておく。
    /// </para>
    /// </summary>
    private static IEnumerable<string> BuiltInExtensions()
    {
        yield return ".blend";
        yield return ".blend1";
        yield return ".blend2";
    }

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>分類に属さない個別の拡張子（ドット付き・大文字小文字を無視）。</summary>
    private readonly HashSet<string> _explicitExtensions;

    /// <summary>画像分類を関連付けで開くか。</summary>
    public bool IncludesImages { get; }

    /// <summary>音声分類を関連付けで開くか。</summary>
    public bool IncludesAudio { get; }

    /// <summary>読み込み時に起きた問題（空なら正常）。呼び出し側がログへ出す。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイルの絶対パス。組み込み既定なら null。</summary>
    public string? SourcePath { get; }

    /// <summary>個別指定の拡張子一覧（表示・診断用。分類ぶんは含まない）。</summary>
    public IReadOnlyCollection<string> ExplicitExtensions => _explicitExtensions;

    /// <summary>カタログを組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> から）。</summary>
    private ShellOpenCatalog(
        HashSet<string> explicitExtensions,
        bool includesImages,
        bool includesAudio,
        IReadOnlyList<string> warnings,
        string? sourcePath)
    {
        _explicitExtensions = explicitExtensions;
        IncludesImages      = includesImages;
        IncludesAudio       = includesAudio;
        Warnings            = warnings;
        SourcePath          = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>組み込み既定だけのカタログを作る（JSON を読まない）。</summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    public static ShellOpenCatalog BuiltIn(IReadOnlyList<string>? warnings = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in BuiltInExtensions()) set.Add(ext);
        return new ShellOpenCatalog(
            set, BuiltInIncludeImages, BuiltInIncludeAudio,
            warnings ?? Array.Empty<string>(), sourcePath: null);
    }

    /// <summary>
    /// 指定ファイルからカタログを読み込む。
    /// 読めない・壊れている場合は組み込み既定へフォールバックする
    /// （例外は投げない。ここで失敗してもダブルクリックは動かし続ける）。
    /// </summary>
    /// <param name="filePath">shell_open_extensions.json の絶対パス。</param>
    public static ShellOpenCatalog Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"関連付けカタログが見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む
        ShellOpenCatalogFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ShellOpenCatalogFile>(
                File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"関連付けカタログの読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"関連付けカタログが空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョン（新しすぎても読める範囲は読む）
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add(
                $"関連付けカタログの format_version={parsed.FormatVersion} は未知" +
                $"（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する");
        }

        // ④ 個別の拡張子を 1 件ずつ検証する。不正な要素は捨てて他を生かす。
        //    ここが 0 件でも「画像・音声だけ関連付けで開く」設定として成立するので、
        //    TextEditableCatalog と違って「0 件なら組み込み既定」にはしない
        //    （利用者が意図的に .blend を外した設定を勝手に戻さないため）。
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed.Extensions ?? new List<ShellOpenEntry>())
        {
            if (entry is null) continue;

            var ext = NormalizeExtension(entry.Extension);
            if (ext is null)
            {
                warnings.Add("拡張子が空の項目を無視（関連付けカタログ）");
                continue;
            }
            if (!set.Add(ext))
                warnings.Add($"拡張子が重複しているため後の方を無視: {ext}");
        }

        return new ShellOpenCatalog(
            set,
            parsed.IncludeImageExtensions ?? BuiltInIncludeImages,
            parsed.IncludeAudioExtensions ?? BuiltInIncludeAudio,
            warnings,
            Path.GetFullPath(filePath));
    }

    /// <summary>
    /// 構成フォルダ（editor/config）を指定してカタログを読み込む。
    /// </summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    public static ShellOpenCatalog LoadFromDir(string? configDir)
        => configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないため関連付け対象は組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    // ── 照合 ─────────────────────────────────────────────────

    /// <summary>
    /// この拡張子を OS の関連付けで開くか。
    /// </summary>
    /// <param name="extension">拡張子（ドットの有無はどちらでもよい）。</param>
    public bool ShouldOpenWithShell(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext is null) return false;

        if (_explicitExtensions.Contains(ext)) return true;
        if (IncludesImages && AssetPreviewKinds.IsImageExtension(ext)) return true;
        if (IncludesAudio  && AssetPreviewKinds.IsAudioExtension(ext)) return true;
        return false;
    }

    /// <summary>
    /// このファイルを OS の関連付けで開くか（拡張子を取り出して照合する）。
    /// </summary>
    /// <param name="path">ファイルパス。null・空可。</param>
    public bool ShouldOpenPathWithShell(string? path)
        => !string.IsNullOrEmpty(path) && ShouldOpenWithShell(Path.GetExtension(path));

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>JSON のパース設定（コメント・末尾カンマを許す／大文字小文字を無視）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// 拡張子を ".ext"（小文字化はしない。比較が大文字小文字無視のため不要）へ正規化する。
    /// </summary>
    /// <param name="extension">入力（"blend" / ".blend" / " .BLEND " など）。</param>
    /// <returns>正規化した拡張子。空なら null。</returns>
    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim();
        if (ext[0] != '.') ext = "." + ext;
        return ext.Length > 1 ? ext : null;
    }
}
