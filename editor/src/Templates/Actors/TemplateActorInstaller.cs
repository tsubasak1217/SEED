// ============================================================
//  TemplateActorInstaller.cs — テンプレートアクタ 1 件を「追加できる状態」にする
//
//  【役割】（WPF にもランタイムにも触れない。単体テストで一通り回せる）
//   1. テンプレート（.actor）を読む
//   2. まっさらな 1 本の木にする（入れ子の展開・プレハブの印の除去。TemplateActorFlattener）
//   3. 動かすのに要るファイルをプロジェクトへコピーする（既にあれば触らない。TemplateActorDependencyPlanner）
//   4. まっさらな木を一時ファイルへ書き出す（TemplateActorStaging）
//  あとは呼び出し側（テンプレートアクタの窓）が ADD_TEMPLATE_ACTOR をランタイムへ送るだけ。
//
//  コピーを送信より先に行うのは、ランタイムがアクタを組み立てる瞬間にモデル・テクスチャが
//  プロジェクトに揃っている必要があるため（モデルが無いとアクタの組み立て自体が失敗する）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// <see cref="TemplateActorInstaller.Prepare"/> の結果。
/// </summary>
public sealed class TemplateActorInstallResult
{
    /// <summary>ランタイムへ送れる状態になったなら true。</summary>
    public bool Success => Error is null;

    /// <summary>失敗の理由（成功なら null）。</summary>
    public string? Error { get; init; }

    /// <summary>まっさらな木の一時ファイル（成功時のみ）。</summary>
    public string StagedPath { get; init; } = "";

    /// <summary>テンプレートのルートの名前（シーンに入るアクタの名前）。</summary>
    public string RootName { get; init; } = "";

    /// <summary>ルートが 2D アクタか。</summary>
    public bool Is2D { get; init; }

    /// <summary>入れ子のプレハブを展開したノード数。</summary>
    public int ExpandedCount { get; init; }

    /// <summary>コピーの結果（読み込みに失敗したときは空）。</summary>
    public TemplateActorCopyResult Copy { get; init; } = new();

    /// <summary>ライブラリにもプロジェクトにも無かった参照。</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>展開できなかった入れ子など、知らせたいこと。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// テンプレートアクタの追加の準備（読む → まっさらにする → コピー → 一時ファイル）。状態を持たない。
/// </summary>
public static class TemplateActorInstaller
{
    /// <summary>まっさらな木を書き出すときの JSON の書式（読みやすさのため字下げし、日本語はそのまま書く）。</summary>
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>テンプレートを読むときの設定（ランタイムの serde_json と同じく厳密に読む）。</summary>
    private static readonly JsonDocumentOptions TemplateJsonOptions = new();

    /// <summary>
    /// テンプレートアクタ 1 件を、ランタイムへ送れる状態にする。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="assetsRoot">プロジェクトのアセットルートの絶対パス（依存ファイルのコピー先）。</param>
    /// <param name="entry">追加するテンプレートアクタ。</param>
    /// <param name="stagingDirectory">一時ファイルの置き場（省略時は OS の一時フォルダ。テストで差し替える）。</param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <returns>準備の結果。</returns>
    public static TemplateActorInstallResult Prepare(
        string libraryRoot,
        string assetsRoot,
        TemplateActorEntry entry,
        string? stagingDirectory = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || !Directory.Exists(assetsRoot))
            return new TemplateActorInstallResult { Error = "プロジェクトのアセットフォルダが見つかりません" };

        var fullLibrary = Path.GetFullPath(libraryRoot);

        // ── 1. テンプレートを読む ────────────────────────────────
        var template = ReadActorJson(AssetPathUtil.ToAbsolute(fullLibrary, entry.TemplateRelPath), out var readError);
        if (template is null)
            return new TemplateActorInstallResult { Error = $"テンプレートを読めませんでした: {entry.TemplateRelPath}（{readError}）" };

        // ── 2. まっさらな 1 本の木にする ─────────────────────────
        var flat = TemplateActorFlattener.Flatten(template, source => LoadNestedFromLibrary(fullLibrary, source));
        var json = flat.Root.ToJsonString(OutputJsonOptions);

        // ── 3. 動かすのに要るファイルをコピーする（既にあるものは触らない）──
        var plan = TemplateActorDependencyPlanner.Plan(
            fullLibrary, assetsRoot, entry.TemplateRelPath, json, entry.RequiredRelPaths, log);
        var copy = TemplateActorDependencyPlanner.Execute(plan, log);

        // ── 4. 一時ファイルへ書き出す ───────────────────────────
        string staged;
        try
        {
            staged = TemplateActorStaging.Stage(json, entry.TemplateRelPath, stagingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TemplateActorInstallResult
            {
                Error = $"一時ファイルを書けませんでした（{ex.Message}）",
                Copy  = copy,
            };
        }

        return new TemplateActorInstallResult
        {
            StagedPath    = staged,
            RootName      = flat.RootName,
            Is2D          = flat.Is2D,
            ExpandedCount = flat.ExpandedCount,
            Copy          = copy,
            Missing       = plan.Missing,
            Warnings      = flat.Warnings,
        };
    }

    /// <summary>
    /// 入れ子のプレハブの参照（prefab_source の値）をライブラリの中で解決して読む。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="source">prefab_source の値（assets:// 仮想パス、またはルート相対パス）。</param>
    /// <returns>参照先の .actor のルート。ライブラリの外・見つからない・読めないなら null。</returns>
    public static JsonObject? LoadNestedFromLibrary(string libraryRoot, string source)
    {
        string rel;
        if (source.StartsWith(AssetPathUtil.AssetsScheme, StringComparison.OrdinalIgnoreCase))
            rel = AssetPathUtil.NormalizeRelative(source[AssetPathUtil.AssetsScheme.Length..]);
        else if (!Path.IsPathRooted(source))
            rel = AssetPathUtil.NormalizeRelative(source);
        else
            return null;   // 絶対パスはライブラリの外（利用者の環境ごとに違う）なので辿らない

        if (rel.Length == 0) return null;
        var abs = AssetPathUtil.ToAbsolute(libraryRoot, rel);
        return File.Exists(abs) ? ReadActorJson(abs, out _) : null;
    }

    /// <summary>
    /// .actor のテキストを読み、ルートのオブジェクトを返す。
    /// </summary>
    /// <param name="path">ファイルの絶対パス。</param>
    /// <param name="error">読めなかったときの理由。</param>
    /// <returns>ルートのオブジェクト。読めなければ null。</returns>
    private static JsonObject? ReadActorJson(string path, out string error)
    {
        error = "";
        try
        {
            // File.ReadAllText は先頭の BOM を取り除いて読む（ライブラリには BOM 付きのファイルもある）
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: TemplateJsonOptions);
            if (node is JsonObject obj) return obj;
            error = "先頭が JSON のオブジェクトではありません";
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }
}
