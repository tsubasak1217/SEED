// ============================================================
//  TextEditableCatalog.cs — スクリプトエディタで開ける拡張子のカタログ（正典）
//
//  【役割】
//  「どの拡張子を内蔵エディタで開けるか」「開いたとき何語として扱うか」
//  「何バイトを超えたら読み取り専用にするか」をデータとして持つ。
//  規則は editor/config/text_editable_extensions.json に置き、
//  ここはその読み込み・検証・照合だけを担う。
//
//  【なぜデータにするのか】
//  対応形式はゲーム側の都合で増える（.icons / .inputmap / 独自 JSON …）。
//  コードへ書くと増えるたびにビルドが要るうえ、開く動線（ProjectPanel）と
//  タブ生成（ScriptEditorPanel）の 2 か所へ追従漏れが起きる。
//  1 つの JSON を唯一の情報源にして、両方がここを引く。
//
//  【対象外にしているもの（意図的）】
//  .scene / .actor / .actor2d は**テキストで開かせない**。
//  これらはエディタが常時メモリ上に読み込んで編集している実体そのもので、
//  テキストで書き換えるとランタイム側の状態と二重管理になり、
//  「保存した瞬間に相手の変更が消える」「壊れた JSON でシーンが開けなくなる」
//  という取り返しのつかない壊れ方をする。編集はヒエラルキー／インスペクタから行う。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/TextEditorLogicTests）から直接リンクするため、
//  WPF 型・EditorLog などエディタ本体のシングルトンへは依存しない。
//  読み込み時の問題は Warnings に積み、呼び出し側がログへ出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// text_editable_extensions.json の 1 エントリ（拡張子と言語の対応）。
/// </summary>
public sealed class TextEditableEntry
{
    /// <summary>拡張子（ドットの有無はどちらでもよい。読み込み時に正規化する）。</summary>
    [JsonPropertyName("extension")]
    public string? Extension { get; set; }

    /// <summary>
    /// 言語 id（"csharp" / "wgsl" / "json" / "csv" / "markdown" / "text"）。
    /// 未知の id は "text" として扱い、警告を残す。
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>この拡張子を何に使うかの覚書（動作には影響しない）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// text_editable_extensions.json のファイル全体に対応するモデル（デシリアライズ専用）。
/// </summary>
public sealed class TextEditableCatalogFile
{
    /// <summary>ファイル書式のバージョン。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>
    /// 読み書き可能として開ける最大バイト数。これを超えるファイルは
    /// 読み取り専用タブで開く（巨大ファイルの誤編集・保存事故を防ぐ）。
    /// null は未指定＝組み込み既定。
    /// </summary>
    [JsonPropertyName("max_editable_bytes")]
    public long? MaxEditableBytes { get; set; }

    /// <summary>拡張子と言語の対応一覧。</summary>
    [JsonPropertyName("extensions")]
    public List<TextEditableEntry>? Extensions { get; set; }
}

/// <summary>
/// スクリプトエディタで開ける拡張子のカタログ。
///
/// <para>
/// 生成は <see cref="LoadFromDir"/>（通常）か <see cref="BuiltIn"/>（フォールバック・テスト）。
/// 実際に参照するときは <see cref="EditorLanguages"/> 経由で引く
/// （アプリ全体で 1 つのカタログを共有するため）。
/// </para>
/// </summary>
public sealed class TextEditableCatalog
{
    // ── ファイル名・既定値（マジックナンバーの一元化）──────────

    /// <summary>カタログファイルの名前（editor/config 直下）。</summary>
    public const string FileName = "text_editable_extensions.json";

    /// <summary>このコードが理解する書式バージョン。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// 読み書き可能として開ける最大バイト数の組み込み既定（1 MiB）。
    ///
    /// AvalonEdit は数 MB でも開けるが、行単位のハイライト・検索・
    /// 一致ハイライトが重くなるうえ、巨大な生成物（ログ・ダンプ）を
    /// 誤って編集・保存する事故のほうが実害が大きい。
    /// 超えた場合は「開けない」ではなく「読み取り専用で開く」にして、
    /// 中身の確認だけはできるようにする。
    /// </summary>
    public const long BuiltInMaxEditableBytes = 1024 * 1024;

    // ── 言語 id（JSON に書く文字列）───────────────────────────

    /// <summary>C# スクリプトの言語 id。</summary>
    public const string LanguageIdCSharp = "csharp";
    /// <summary>WGSL シェーディングアセットの言語 id。</summary>
    public const string LanguageIdWgsl = "wgsl";
    /// <summary>JSON（および JSON 系の独自拡張子）の言語 id。</summary>
    public const string LanguageIdJson = "json";
    /// <summary>CSV の言語 id。</summary>
    public const string LanguageIdCsv = "csv";
    /// <summary>Markdown の言語 id。</summary>
    public const string LanguageIdMarkdown = "markdown";
    /// <summary>プレーンテキストの言語 id。</summary>
    public const string LanguageIdPlainText = "text";

    /// <summary>言語 id → 言語種別の対応表（JSON の "language" を解釈するときに使う）。</summary>
    private static readonly IReadOnlyDictionary<string, EditorLanguage> LanguageIds =
        new Dictionary<string, EditorLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            [LanguageIdCSharp]   = EditorLanguage.CSharp,
            ["cs"]               = EditorLanguage.CSharp,
            [LanguageIdWgsl]     = EditorLanguage.Wgsl,
            [LanguageIdJson]     = EditorLanguage.Json,
            [LanguageIdCsv]      = EditorLanguage.Csv,
            [LanguageIdMarkdown] = EditorLanguage.Markdown,
            ["md"]               = EditorLanguage.Markdown,
            [LanguageIdPlainText] = EditorLanguage.PlainText,
            ["plaintext"]        = EditorLanguage.PlainText,
            ["txt"]              = EditorLanguage.PlainText,
        };

    // ── 組み込み既定 ─────────────────────────────────────────
    //
    //  editor/config/text_editable_extensions.json と同じ内容を持つ。
    //  「JSON を消してもスクリプト編集は従来どおり動く」ことを保証する最後の砦。

    /// <summary>組み込み既定の対応表（拡張子 → 言語）。</summary>
    private static IEnumerable<KeyValuePair<string, EditorLanguage>> BuiltInEntries()
    {
        // コード系（従来からの対応。これが欠けるとスクリプト編集自体が壊れる）
        yield return new(".cs",       EditorLanguage.CSharp);
        yield return new(".wgsl",     EditorLanguage.Wgsl);
        // 汎用テキスト
        yield return new(".json",     EditorLanguage.Json);
        yield return new(".txt",      EditorLanguage.PlainText);
        yield return new(".csv",      EditorLanguage.Csv);
        yield return new(".md",       EditorLanguage.Markdown);
        // JSON 系の独自拡張子（中身は JSON なので JSON として着色する）
        yield return new(".inputmap", EditorLanguage.Json);
        yield return new(".icons",    EditorLanguage.Json);
        yield return new(".anim",     EditorLanguage.Json);
    }

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>拡張子 → 言語種別（ドット付き・大文字小文字を無視）。</summary>
    private readonly Dictionary<string, EditorLanguage> _byExtension;

    /// <summary>読み書き可能として開ける最大バイト数。</summary>
    public long MaxEditableBytes { get; }

    /// <summary>読み込み時に起きた問題（空なら正常）。呼び出し側がログへ出す。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイルの絶対パス。組み込み既定なら null。</summary>
    public string? SourcePath { get; }

    /// <summary>対応している拡張子の一覧（表示・診断用。並び順は登録順）。</summary>
    public IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    /// <summary>カタログを組み立てる（生成は <see cref="Load"/> / <see cref="BuiltIn"/> から）。</summary>
    private TextEditableCatalog(
        Dictionary<string, EditorLanguage> byExtension,
        long maxEditableBytes,
        IReadOnlyList<string> warnings,
        string? sourcePath)
    {
        _byExtension     = byExtension;
        MaxEditableBytes = maxEditableBytes;
        Warnings         = warnings;
        SourcePath       = sourcePath;
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>組み込み既定だけのカタログを作る（JSON を読まない）。</summary>
    /// <param name="warnings">フォールバックした理由（無ければ空）。</param>
    public static TextEditableCatalog BuiltIn(IReadOnlyList<string>? warnings = null)
    {
        var map = new Dictionary<string, EditorLanguage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, lang) in BuiltInEntries()) map[ext] = lang;
        return new TextEditableCatalog(
            map, BuiltInMaxEditableBytes, warnings ?? Array.Empty<string>(), sourcePath: null);
    }

    /// <summary>
    /// 指定ファイルからカタログを読み込む。
    /// 読めない・壊れている・有効な項目が 1 件も無い場合は組み込み既定へフォールバックする
    /// （例外は投げない。ここで失敗してもスクリプト編集は動かし続ける）。
    /// </summary>
    /// <param name="filePath">text_editable_extensions.json の絶対パス。</param>
    public static TextEditableCatalog Load(string filePath)
    {
        var warnings = new List<string>();

        // ① ファイルの存在確認
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            warnings.Add($"編集可能拡張子カタログが見つからないため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ② JSON を読む
        TextEditableCatalogFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TextEditableCatalogFile>(
                File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"編集可能拡張子カタログの読み込みに失敗したため組み込み既定を使用: {filePath} — {ex.Message}");
            return BuiltIn(warnings);
        }

        if (parsed is null)
        {
            warnings.Add($"編集可能拡張子カタログが空のため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ③ 書式バージョン（新しすぎても読める範囲は読む）
        if (parsed.FormatVersion > SupportedFormatVersion)
        {
            warnings.Add(
                $"編集可能拡張子カタログの format_version={parsed.FormatVersion} は未知" +
                $"（対応 {SupportedFormatVersion}）。読める範囲だけ解釈する");
        }

        // ④ 項目を 1 件ずつ検証する。不正な要素は捨てて他を生かす。
        var map = new Dictionary<string, EditorLanguage>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed.Extensions ?? new List<TextEditableEntry>())
        {
            if (entry is null) continue;

            var ext = NormalizeExtension(entry.Extension);
            if (ext is null)
            {
                warnings.Add($"拡張子が空の項目を無視: language='{entry.Language}'");
                continue;
            }
            if (ForbiddenExtensions.Contains(ext))
            {
                // シーン・アクタは整合が壊れるためテキスト編集させない（クラス冒頭の理由）。
                // JSON へ書かれていても必ず拒否する（データで安全策を外させない）。
                warnings.Add($"テキスト編集を許可できない拡張子のため無視: {ext}");
                continue;
            }

            var language = ParseLanguage(entry.Language, ext, warnings);
            if (!map.TryAdd(ext, language))
                warnings.Add($"拡張子が重複しているため後の方を無視: {ext}");
        }

        // ⑤ 有効な項目がゼロなら組み込み既定へ（何も開けないエディタにしない）
        if (map.Count == 0)
        {
            warnings.Add($"有効な拡張子が 1 件も無いため組み込み既定を使用: {filePath}");
            return BuiltIn(warnings);
        }

        // ⑥ サイズ上限。未指定・0 以下は既定へ丸める（0 にすると何も編集できなくなるため）
        long maxBytes = BuiltInMaxEditableBytes;
        if (parsed.MaxEditableBytes is { } limit)
        {
            if (limit > 0) maxBytes = limit;
            else warnings.Add($"max_editable_bytes={limit} は不正なため既定 {BuiltInMaxEditableBytes} を使用");
        }

        return new TextEditableCatalog(map, maxBytes, warnings, Path.GetFullPath(filePath));
    }

    /// <summary>
    /// 構成フォルダ（editor/config）を指定してカタログを読み込む。
    /// </summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込み既定）。</param>
    public static TextEditableCatalog LoadFromDir(string? configDir)
        => configDir is null
            ? BuiltIn(new[] { "構成フォルダが見つからないため編集可能拡張子は組み込み既定を使用" })
            : Load(Path.Combine(configDir, FileName));

    // ── 照合 ─────────────────────────────────────────────────

    /// <summary>
    /// 内蔵エディタで開ける拡張子か。
    /// </summary>
    /// <param name="extension">拡張子（ドットの有無はどちらでもよい）。</param>
    public bool IsEditableExtension(string? extension)
    {
        var ext = NormalizeExtension(extension);
        return ext is not null && _byExtension.ContainsKey(ext);
    }

    /// <summary>
    /// 拡張子から言語種別を引く。
    /// </summary>
    /// <param name="extension">拡張子（ドットの有無はどちらでもよい）。</param>
    /// <param name="language">見つかった言語種別。</param>
    /// <returns>カタログに載っていれば true。</returns>
    public bool TryGetLanguage(string? extension, out EditorLanguage language)
    {
        language = EditorLanguage.PlainText;
        var ext = NormalizeExtension(extension);
        return ext is not null && _byExtension.TryGetValue(ext, out language);
    }

    /// <summary>
    /// ファイルパスから言語種別を判定する。
    ///
    /// カタログに無い拡張子は <see cref="EditorLanguage.CSharp"/> として扱う。
    /// これは従来の挙動（未知＝C#）をそのまま残すためで、定義ジャンプ（F12）が
    /// エンジン同梱ソースを拡張子違いで開く場合などに効く。
    /// ダブルクリックの動線は <see cref="IsEditableExtension"/> で先に絞られるため、
    /// この既定が効くのは「明示的に開いた」場合だけになる。
    /// </summary>
    /// <param name="filePath">ファイルパス。</param>
    public EditorLanguage LanguageFromPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return EditorLanguage.CSharp;
        return TryGetLanguage(Path.GetExtension(filePath), out var lang) ? lang : EditorLanguage.CSharp;
    }

    /// <summary>
    /// 読み書き可能な大きさを超えているか（超えたら読み取り専用で開く）。
    /// </summary>
    /// <param name="byteLength">ファイルのバイト数。</param>
    public bool ExceedsEditableSize(long byteLength) => byteLength > MaxEditableBytes;

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>
    /// テキスト編集を絶対に許可しない拡張子。
    /// エディタが常時読み込んで編集している実体（シーン・アクタ）で、
    /// テキスト編集と二重管理になると保存の取り合いで内容が消えるため。
    /// JSON 側に書かれていてもここで弾く。
    /// </summary>
    private static readonly HashSet<string> ForbiddenExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".scene", ".actor", ".actor2d" };

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
    /// <param name="extension">入力（"json" / ".json" / " .JSON " など）。</param>
    /// <returns>正規化した拡張子。空なら null。</returns>
    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.Trim();
        if (ext[0] != '.') ext = "." + ext;
        return ext.Length > 1 ? ext : null;
    }

    /// <summary>
    /// 言語 id を解釈する。未知の id はプレーンテキスト扱いにして警告を残す
    /// （1 件の書き間違いで拡張子ごと使えなくなるより、着色が落ちるほうが軽い）。
    /// </summary>
    private static EditorLanguage ParseLanguage(string? id, string ext, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add($"言語が未指定のためプレーンテキストとして扱う: {ext}");
            return EditorLanguage.PlainText;
        }
        if (LanguageIds.TryGetValue(id.Trim(), out var lang)) return lang;

        warnings.Add($"未知の言語 '{id}' のためプレーンテキストとして扱う: {ext}");
        return EditorLanguage.PlainText;
    }
}
