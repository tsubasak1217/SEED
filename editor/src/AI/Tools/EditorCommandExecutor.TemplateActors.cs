// ============================================================
//  EditorCommandExecutor.TemplateActors.cs — テンプレートアクタの一覧と追加（template_actor_list / template_actor_add）
//
//  外部エージェント（MCP の seed_template_actor）が、テンプレートアクタ（templates/*/template_actors.json の部品）を
//  一覧し、シーンへ追加するためのコマンド。EditorCommandExecutor の partial 実装。正典は docs/template_library.md §9。
//
//  【コマンドを 2 つに分けている理由】
//   許可判定（AiOperationPolicy.CheckAllowed）はコマンド名だけを見る。一覧（観測系）と追加（変更系）を
//   別のコマンドにしておくと、読み取り専用の対話エディタでも一覧だけは取れる。
//   MCP 側は seed_template_actor(action) の 1 ツールにまとめ、action でコマンドを選ぶ（Program.cs）。
//
//  【追加の道筋】
//   カタログから path の 1 件を引き、IEditorAiHost.AddTemplateActorAsync（MainWindow.AiHost.Tools.cs）へ渡す。
//   そこから先はテンプレートアクタの窓と同じ（TemplateActorAddFlow: 編集できるか → 追加先の引き直し →
//   2D/3D の規則 → 準備〈まっさらにする・依存ファイルのコピー・一時ファイル〉→ 送る直前の引き直し → ADD_TEMPLATE_ACTOR）。
//
//  【安全性】
//   template_actor_list は観測系（ライブラリのファイルを読むだけ）。
//   template_actor_add は変更系（シーンを変え、依存ファイルをプロジェクトの assets へコピーする）。Edit 中のみ受け付ける。
// ============================================================

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SEEDEditor.AI.Tools.RuntimeIpc;
using SEEDEditor.Runtime;
using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 引数名 ───────────────────────────────────────────────────

    /// <summary>template_actor_add: テンプレートのライブラリ相対パスの引数名。</summary>
    private const string TemplateActorPathArg = "path";

    /// <summary>template_actor_add: 親のアクタの引数名（省略でルート）。</summary>
    private const string TemplateActorParentArg = "parent";

    /// <summary>ライブラリ相対パスの区切り（カタログの TemplateRelPath と同じ '/'）。</summary>
    private const char TemplateActorPathSeparator = '/';

    /// <summary>
    /// 追加の命令を送ってからランタイムの結果（SCENE_MODIFIED / LOAD_ERROR）を待つタイムアウト（ミリ秒）。
    /// ランタイムはテンプレートをその場で組み立てる（モデル・画像の読み込みを含む）ので長めに取る。
    /// </summary>
    private const int TemplateActorAddTimeoutMs = 30_000;

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// テンプレートアクタのコマンドを実行する。扱わないコマンド名なら null を返し、呼び出し元が他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecuteTemplateActorTool(string command, JsonElement args)
        => command switch
        {
            "template_actor_list" => ExecuteTemplateActorListAsync(),
            "template_actor_add"  => ExecuteTemplateActorAddAsync(args),
            _                     => null,
        };

    // ── list ─────────────────────────────────────────────────────

    /// <summary>
    /// カタログの一覧を返す（窓の一覧と同じ TemplateActorCatalog.Load。重いので UI スレッドの外で読む）。
    /// </summary>
    private async Task<string> ExecuteTemplateActorListAsync()
    {
        var (catalog, root, error) = await LoadTemplateActorCatalogAsync();
        if (catalog is null) return Error(error ?? "テンプレートアクタのカタログを読めませんでした。");

        return Json(new
        {
            ok           = true,
            library_root = root,
            count        = catalog.Entries.Count,
            entries      = catalog.Entries.Select(e => new
            {
                path        = e.TemplateRelPath,
                name        = e.Name,
                category    = e.CategoryKey,
                tags        = e.Tags,
                description = e.Description,
                is_2d       = e.Is2D,
            }),
            warnings     = catalog.Warnings,
        });
    }

    // ── add ──────────────────────────────────────────────────────

    /// <summary>
    /// カタログの 1 件を、窓と同じ道筋でシーンへ追加する。
    /// </summary>
    /// <param name="args">ツール引数（path・parent）。</param>
    private async Task<string> ExecuteTemplateActorAddAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // Play 中に入れても Play の停止で消える（＝作業として残らない）ので、Edit に限る
        if (host.RuntimeState != EditorState.Edit)
            return Error($"テンプレートアクタの追加は Edit 状態でのみ実行できます（現在: {host.RuntimeState}）。");

        var requested = GetString(args, TemplateActorPathArg);
        if (string.IsNullOrWhiteSpace(requested))
            return Error("'path'（template_actor_list の path。例 \"ui/prefabs/button.actor\"）が必要です。");

        var (present, parentDfs, parentError) = ResolveParentActorArg(args, TemplateActorParentArg);
        if (parentError is not null) return Error(parentError);

        // ── カタログから 1 件を引く（区切り・大小文字の違いは見逃す）──
        var (catalog, root, loadError) = await LoadTemplateActorCatalogAsync();
        if (catalog is null || root is null) return Error(loadError ?? "テンプレートアクタのカタログを読めませんでした。");

        var wanted = NormalizeTemplateRelPath(requested);
        var entry  = catalog.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeTemplateRelPath(e.TemplateRelPath), wanted, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return Error($"テンプレートアクタ '{requested}' がカタログにありません（template_actor_list の path を使ってください）。");

        // ── 窓と同じ道筋で追加し、ランタイムの結果を待つ ──
        var (outcome, reply) = await host.AddTemplateActorAsync(root, entry, present ? parentDfs : null, TemplateActorAddTimeoutMs);
        _log($"[AI ツール] template_actor_add: {entry.TemplateRelPath} → {outcome.Target.Describe()}"
           + $" → {outcome.Status}{(reply.Line is null ? "" : $" / {reply.Line}")}");
        return FormatTemplateActorAddResult(entry, outcome, reply);
    }

    /// <summary>
    /// 追加の結果を JSON にする（送れたか・ランタイムが入れたか・コピーしたファイル・警告）。
    /// </summary>
    /// <param name="entry">追加したテンプレートアクタ。</param>
    /// <param name="outcome">手順の結果。</param>
    /// <param name="reply">送った後のランタイムの結果（送っていなければ Refused）。</param>
    private static string FormatTemplateActorAddResult(TemplateActorEntry entry, TemplateActorAddOutcome outcome, AiIpcReply reply)
    {
        // ランタイムが入れたか: SCENE_MODIFIED なら入った、LOAD_ERROR なら理由つきで断られた
        string? error = null;
        if (!outcome.IsSent)
            error = outcome.Message;
        else if (!reply.HasLine)
            error = reply.DescribeFailure(TemplateActorAddTimeoutMs) + "（命令は送ったので、遅れて入ることがあります。seed_hierarchy で確かめてください）";
        else if (reply.Line!.StartsWith(TemplateActorIpc.RejectedPrefix, StringComparison.Ordinal))
            error = reply.Line[TemplateActorIpc.RejectedPrefix.Length..];

        var result = outcome.Result;
        return Json(new
        {
            ok               = error is null,
            status           = outcome.Status.ToString(),
            template         = new { path = entry.TemplateRelPath, name = entry.Name, is_2d = entry.Is2D },
            target           = outcome.Target.Describe(),
            parent_dfs       = outcome.Target.ParentDfs,
            sent             = outcome.SentCommand,
            runtime_reply    = reply.Line,
            error,
            copied           = result?.Copy.Copied,
            skipped_existing = result?.Copy.SkippedExisting,
            copy_failures    = result?.Copy.Failures.Select(f => new { path = f.RelPath, message = f.Message }),
            missing          = result?.Missing,
            warnings         = result?.Warnings,
        });
    }

    // ── 小道具 ───────────────────────────────────────────────────

    /// <summary>
    /// テンプレートライブラリを探してカタログを読む（窓と同じ TemplateLibraryLocator.Resolve → TemplateActorCatalog.Load）。
    /// </summary>
    /// <returns>(カタログ, ライブラリの絶対パス, 読めなかった理由)。読めたら理由は null。</returns>
    private static async Task<(TemplateActorCatalog? Catalog, string? Root, string? Error)> LoadTemplateActorCatalogAsync()
    {
        var root = TemplateLibraryLocator.Resolve();
        if (root is null)
            return (null, null, "テンプレートライブラリが見つかりません（<repo>/templates または環境変数 "
                              + $"{TemplateLibraryLocator.OverrideEnvVar}。想定の場所: {TemplateLibraryLocator.ResolveOrExpectedPath()}）。");
        try
        {
            var catalog = await Task.Run(() => TemplateActorCatalog.Load(root));
            return (catalog, root, null);
        }
        catch (Exception ex)
        {
            return (null, root, $"テンプレートアクタのカタログを読めませんでした（{root}）: {ex.Message}");
        }
    }

    /// <summary>ライブラリ相対パスを比べる形にする（前後の空白・'\' 区切り・先頭の区切りを揃える）。</summary>
    /// <param name="path">パス。</param>
    private static string NormalizeTemplateRelPath(string path) =>
        path.Trim().Replace('\\', TemplateActorPathSeparator).TrimStart(TemplateActorPathSeparator);
}
