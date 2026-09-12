using System;
using System.IO;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタで開けるファイルの種別（言語）。
///
/// 種別ごとに「補完・診断・デバッグ（ブレークポイント）・保存後処理」の有無が変わるため、
/// タブ生成時に確定させ、以降の機能分岐はすべてこの値を見て行う。
/// </summary>
public enum EditorLanguage
{
    /// <summary>C# ユーザースクリプト（.cs）。IntelliSense・診断・デバッグの全機能が有効。</summary>
    CSharp,

    /// <summary>WGSL シェーディングアセット（.wgsl）。編集と構文着色のみ（デバッグ機能なし）。</summary>
    Wgsl,

    /// <summary>
    /// JSON（.json と、中身が JSON の独自拡張子 .icons / .inputmap / .anim）。
    /// 構文着色と保存のみ。コード解析系の機能（補完・診断・デバッグ）はすべて無効。
    /// </summary>
    Json,

    /// <summary>CSV（.csv）。着色なしのテキスト編集のみ。</summary>
    Csv,

    /// <summary>Markdown（.md）。AvalonEdit 同梱の定義があれば着色する。</summary>
    Markdown,

    /// <summary>プレーンテキスト（.txt ほか）。着色なしのテキスト編集のみ。</summary>
    PlainText,
}

/// <summary>
/// <see cref="EditorLanguage"/> に関するユーティリティ（拡張子判定・機能の有無）。
///
/// <para>
/// 「どの拡張子を開けるか」「何語として扱うか」の**対応表そのものは持たない**。
/// 実体は <see cref="TextEditableCatalog"/>（= editor/config/text_editable_extensions.json）で、
/// このクラスはアプリ全体で 1 つのカタログを共有するための入口にすぎない。
/// 対応形式を増やすときは JSON を触る（コードの変更は要らない）。
/// </para>
/// <para>
/// 起動時に <see cref="UseCatalog"/> でカタログを差し替える。呼ばれるまでは
/// 組み込み既定が入っているため、差し替えに失敗しても従来どおり .cs / .wgsl は開ける。
/// </para>
/// </summary>
public static class EditorLanguages
{
    /// <summary>C# ユーザースクリプトの拡張子。</summary>
    public const string CSharpExtension = ".cs";

    /// <summary>WGSL シェーディングアセットの拡張子。</summary>
    public const string WgslExtension = ".wgsl";

    /// <summary>
    /// 現在有効なカタログ。<see cref="UseCatalog"/> が呼ばれるまでは組み込み既定。
    /// </summary>
    public static TextEditableCatalog Catalog { get; private set; } = TextEditableCatalog.BuiltIn();

    /// <summary>
    /// カタログを差し替える（起動時に 1 度だけ呼ぶ）。
    /// </summary>
    /// <param name="catalog">読み込み済みのカタログ。null は無視する。</param>
    public static void UseCatalog(TextEditableCatalog? catalog)
    {
        if (catalog is not null) Catalog = catalog;
    }

    /// <summary>
    /// ファイルパスの拡張子から言語種別を判定する。
    /// カタログに無い拡張子は C# として扱う（従来どおりの挙動を維持するための既定値）。
    /// </summary>
    public static EditorLanguage FromPath(string filePath) => Catalog.LanguageFromPath(filePath);

    /// <summary>
    /// スクリプトエディタで開ける拡張子か（ProjectPanel のダブルクリック判定用）。
    /// </summary>
    public static bool IsEditableExtension(string extension) => Catalog.IsEditableExtension(extension);

    // ── 言語ごとの機能の有無 ──────────────────────────────────
    //
    //  ここに集約しておくと、新しい言語を足したときに「無効化し忘れて
    //  テキストファイルに C# の補完が出る」といった事故を防げる。

    /// <summary>
    /// Roslyn の意味解析を使う言語か（IntelliSense・診断・整形・デバッグ・
    /// AI インライン補完・保存後のコンパイル検証がこれで決まる）。
    /// </summary>
    /// <param name="language">判定する言語種別。</param>
    public static bool UsesRoslyn(EditorLanguage language) => language == EditorLanguage.CSharp;

    /// <summary>
    /// 補完ウィンドウ（IntelliSense 相当）を持つ言語か。
    /// C# は Roslyn、WGSL は静的辞書で候補を出す。それ以外の言語は候補源が無い。
    /// </summary>
    /// <param name="language">判定する言語種別。</param>
    public static bool HasCompletion(EditorLanguage language)
        => language is EditorLanguage.CSharp or EditorLanguage.Wgsl;

    /// <summary>
    /// ログ表示用の言語名（保存メッセージなどに使う）。
    /// </summary>
    /// <param name="language">表示する言語種別。</param>
    public static string DisplayName(EditorLanguage language) => language switch
    {
        EditorLanguage.CSharp   => "スクリプト",
        EditorLanguage.Wgsl     => "シェーダー",
        EditorLanguage.Json     => "JSON",
        EditorLanguage.Csv      => "CSV",
        EditorLanguage.Markdown => "Markdown",
        _                       => "テキスト",
    };
}
