// ============================================================
//  EditorCommandExecutor.ScreenPreview.cs — Edit 上の画面プレビュー（preview）
//
//  外部エージェント（MCP の seed_preview）が、保存されないプレビューを差し込む・消すためのコマンド。
//  EditorCommandExecutor の partial 実装。正典は docs/editor_screen_preview.md。
//
//  【action】
//    add       … parent の下へ prefab_path を出す。host が無ければヒエラルキーの右クリックと同じ（枠なし・親の直下）、
//                host があればインスペクタの差し込み先の案内と同じ（PreviewHostSlotSelector で行を選んで欄の値を当てる）
//    clear     … dfs を含むプレビューを 1 つ消す
//    clear_all … 表示中のタブのプレビューを全部消す
//  ここは引数の検査と整形だけ。判定・親の引き直し・送信は IEditorAiHost（MainWindow.AiHost.Tools.cs）が
//  UI と同じ関数（MainWindow.ScreenPreview.cs）で行う。
//
//  【安全性】
//   変更系。プレビューは保存されずシーンも未保存にしないが、Undo 履歴へ 1 件ずつ積まれ（Ctrl+Z の相手が変わる）、
//   出した根が選択され、表示中の木の DFS 番号もずれる。利用者が操作している対話エディタでこれらを外から起こさないため、
//   AiOperationPolicy の ReadOnlyCommands には入れない（docs/editor_mcp.md §7.2 の判断）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SEEDEditor.AI.Tools.RuntimeIpc;
using SEEDEditor.Preview;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 引数名・action ───────────────────────────────────────────

    /// <summary>preview: 操作（add / clear / clear_all）の引数名。</summary>
    private const string PreviewActionArg = "action";

    /// <summary>preview(add): 中身のプレハブの引数名。</summary>
    private const string PreviewPrefabArg = "prefab_path";

    /// <summary>preview(add): 親のアクタの引数名。</summary>
    private const string PreviewParentArg = "parent";

    /// <summary>preview(add): 差し込み先の行の見出しの引数名。</summary>
    private const string PreviewHostArg = "host";

    /// <summary>preview(clear): 消すプレビューの DFS ID の引数名。</summary>
    private const string PreviewDfsArg = "dfs";

    /// <summary>action: 差し込む。</summary>
    private const string PreviewActionAdd = "add";

    /// <summary>action: 1 つ消す。</summary>
    private const string PreviewActionClear = "clear";

    /// <summary>action: 表示中のタブのプレビューを全部消す。</summary>
    private const string PreviewActionClearAll = "clear_all";

    /// <summary>プレビューにできるプレハブの拡張子（プレハブを選ぶ窓と同じ）。</summary>
    private static readonly string[] PreviewPrefabExtensions = [".actor", ".actor2d"];

    // ── タイムアウト ─────────────────────────────────────────────

    /// <summary>
    /// 差し込みの応答（PREVIEW_ADDED / PREVIEW_ERROR）を待つタイムアウト（ミリ秒）。
    /// ランタイムはプレハブ（入れ子・モデル・画像を含む）をその場で組み立てるので、単発の IPC より長く取る。
    /// </summary>
    private const int PreviewAddTimeoutMs = 20_000;

    /// <summary>消去の応答（PREVIEW_CLEARED / PREVIEW_ERROR）を待つタイムアウト（ミリ秒）。</summary>
    private const int PreviewClearTimeoutMs = 5_000;

    /// <summary>host 指定時に親の構成（ACTOR_COMPONENTS）を待つタイムアウト（ミリ秒）。</summary>
    private const int PreviewHostComponentsTimeoutMs = 5_000;

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// 画面プレビューのコマンドを実行する。扱わないコマンド名なら null を返し、呼び出し元が他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecuteScreenPreviewTool(string command, JsonElement args)
        => command switch
        {
            "preview" => ExecuteScreenPreviewAsync(args),
            _         => null,
        };

    /// <summary>action で差し込み・消去へ振り分ける。</summary>
    /// <param name="args">ツール引数。</param>
    private async Task<string> ExecuteScreenPreviewAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var action = GetString(args, PreviewActionArg);
        switch (action)
        {
            case PreviewActionAdd:
                return await ExecuteScreenPreviewAddAsync(host, args);

            case PreviewActionClear:
            {
                var dfs = GetDouble(args, PreviewDfsArg);
                if (dfs is null || dfs.Value < 0)
                    return Error("'dfs'（消すプレビューの根か中のノードの DFS ID。0 以上）が必要です。"
                               + "全部消すなら action:\"clear_all\" を使ってください。");
                return await ExecuteScreenPreviewClearAsync(host, (int)dfs.Value);
            }

            case PreviewActionClearAll:
                return await ExecuteScreenPreviewClearAsync(host, dfs: null);

            default:
                return Error($"不明な action '{action}'（add / clear / clear_all のいずれか）。");
        }
    }

    // ── add ──────────────────────────────────────────────────────

    /// <summary>
    /// プレビューを差し込み、PREVIEW_ADDED（根の DFS ID）を返す。
    /// </summary>
    /// <param name="host">エディタ本体への窓口。</param>
    /// <param name="args">ツール引数（parent・prefab_path・host）。</param>
    private async Task<string> ExecuteScreenPreviewAddAsync(IEditorAiHost host, JsonElement args)
    {
        // ── 親（ルートへは出せない）──
        var (present, parentDfs, parentError) = ResolveParentActorArg(args, PreviewParentArg);
        if (parentError is not null) return Error(parentError);
        if (!present || parentDfs is not int parent)
            return Error("'parent'（DFS ID か \"Root/Child\" 形式の名前パス）が必要です（プレビューはルートへは出せません）。");

        // ── 差し込み先の行（host。インスペクタの案内と同じく、親のスクリプトの欄の値を当てる）──
        ResolvedPreviewSlot? slot = null;
        string? hostScript = null;
        var hostSpec = GetString(args, PreviewHostArg);
        if (!string.IsNullOrWhiteSpace(hostSpec))
        {
            var components = await host.GetActorComponentsAsync(parent, PreviewHostComponentsTimeoutMs);
            if (string.IsNullOrEmpty(components))
                return Error($"親（DFS {parent}）の構成（ACTOR_COMPONENTS）が {PreviewHostComponentsTimeoutMs} ms 以内に取れませんでした。");

            var selection = PreviewHostSlotSelector.Select(host.ScreenPreviewHosts, components, hostSpec);
            if (selection.Slot is null)
                return Json(new { ok = false, parent_dfs = parent, error = selection.Error, available = selection.Available });
            slot       = selection.Slot;
            hostScript = selection.HostShortName;
        }

        // ── 中身（省略時は行の既定。「選ぶ...」の無い行は既定以外を選べない＝インスペクタと同じ）──
        var requested = GetString(args, PreviewPrefabArg);
        string? prefab;
        if (string.IsNullOrWhiteSpace(requested))
        {
            prefab = slot?.Prefab;
            if (prefab is null)
                return Error("'prefab_path' が必要です（host の行に既定のプレハブが無い、または host を指定していません）。");
        }
        else
        {
            var (normalized, prefabError) = NormalizePreviewPrefab(requested);
            if (prefabError is not null) return Error(prefabError);
            prefab = normalized!;
            if (slot is { CanPick: false } && !SamePreviewPrefab(prefab, slot.Prefab))
                return Error($"差し込み先の行「{slot.Label}」は中身を選べません（インスペクタと同じく既定の {slot.Prefab ?? "(なし)"} だけ）。"
                           + "prefab_path を省いてください。");
        }

        // ── 送って PREVIEW_ADDED / PREVIEW_ERROR を待つ（UI と同じ道筋は host 側）──
        var reply = await host.AddScreenPreviewAsync(parent, prefab, slot, PreviewAddTimeoutMs);
        _log($"[AI ツール] preview add: {prefab} → DFS {parent}{(slot is null ? "" : $"（{hostScript}/{slot.Label}）")} → {reply.Line ?? reply.Status.ToString()}");

        object? hostInfo = slot is null ? null : new
        {
            script     = hostScript,
            slot       = slot.Label,
            under      = slot.Under,
            frame      = slot.Frame,
            frame_body = slot.FrameBody,
            layer_bias = slot.LayerBias,
        };

        if (!reply.HasLine)
            return Json(new { ok = false, prefab, parent_dfs = parent, host = hostInfo, error = reply.DescribeFailure(PreviewAddTimeoutMs) });
        if (ScreenPreviewIpc.TryParseError(reply.Line!, out var reason))
            return Json(new { ok = false, prefab, parent_dfs = parent, host = hostInfo, error = reason });
        if (!ScreenPreviewIpc.TryParseAdded(reply.Line!, out var worldLine, out var rootDfs))
            return Json(new { ok = false, prefab, parent_dfs = parent, error = $"想定外の応答です: {reply.Line}" });

        return Json(new
        {
            ok         = true,
            root_dfs   = rootDfs,
            world_line = worldLine,
            prefab,
            parent_dfs = parent,
            host       = hostInfo,
            note       = "保存されません（シーンも未保存になりません）。Undo 履歴へ 1 件積まれ、根が選択されています。",
        });
    }

    // ── clear / clear_all ────────────────────────────────────────

    /// <summary>
    /// プレビューを消し、PREVIEW_CLEARED（消した数）を返す。
    /// </summary>
    /// <param name="host">エディタ本体への窓口。</param>
    /// <param name="dfs">消すプレビューの根か中のノードの DFS ID（null なら全部）。</param>
    private async Task<string> ExecuteScreenPreviewClearAsync(IEditorAiHost host, int? dfs)
    {
        var reply = await host.ClearScreenPreviewAsync(dfs, PreviewClearTimeoutMs);
        _log($"[AI ツール] preview clear: {(dfs is int d ? $"DFS {d}" : "すべて")} → {reply.Line ?? reply.Status.ToString()}");

        if (!reply.HasLine)
            return Json(new { ok = false, dfs, error = reply.DescribeFailure(PreviewClearTimeoutMs) });
        if (ScreenPreviewIpc.TryParseError(reply.Line!, out var reason))
            return Json(new { ok = false, dfs, error = reason });
        if (!ScreenPreviewIpc.TryParseCleared(reply.Line!, out var cleared))
            return Json(new { ok = false, dfs, error = $"想定外の応答です: {reply.Line}" });

        // 0 件はエラーにしない（消すものが無かった、という正常な結果。ランタイムも木を変えない）
        return Json(new { ok = true, dfs, cleared });
    }

    // ── 小道具 ───────────────────────────────────────────────────

    /// <summary>
    /// 中身のプレハブの指定を、ランタイムへ送る形（アセットの中なら assets:// 仮想パス）に揃え、在るか確かめる。
    /// assets:// の仮想パス・絶対パス・アセットルート相対パス（"ui/prefabs/x.actor"）を受け付ける。
    /// </summary>
    /// <param name="requested">指定された文字列。</param>
    /// <returns>(揃えたパス, 受け付けられない理由)。受け付けたら理由は null。</returns>
    private (string? Prefab, string? Error) NormalizePreviewPrefab(string requested)
    {
        var text = requested.Trim();
        if (!PreviewPrefabExtensions.Any(ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return (null, $"'prefab_path' は {string.Join(" / ", PreviewPrefabExtensions)} のプレハブを指定してください（指定: {text}）。");

        // 仮想パス・絶対パス・アセットルート相対パスを、いったん絶対パスにして在るか見る
        string absolute;
        if (VirtualPath.IsVirtual(text))
            absolute = VirtualPath.ToAbsolute(text, _assetsPath);
        else if (Path.IsPathRooted(text))
            absolute = Path.GetFullPath(text);
        else
            absolute = Path.GetFullPath(Path.Combine(_assetsPath, text.Replace('/', Path.DirectorySeparatorChar)));

        if (!File.Exists(absolute))
            return (null, $"プレハブが見つかりません: {text}（{absolute}）");

        // アセットの中なら仮想パス（プレハブを選ぶ窓と同じ形）、外なら絶対パスのまま
        return (VirtualPath.ToVirtual(absolute, _assetsPath), null);
    }

    /// <summary>2 つのプレハブの指定が同じファイルを指すか（仮想パス・区切り・大小文字の違いを見逃す）。</summary>
    /// <param name="a">一方（揃えたもの）。</param>
    /// <param name="b">もう一方（行の既定。null なら一致しない）。</param>
    private bool SamePreviewPrefab(string a, string? b)
    {
        if (b is null) return false;
        static string Canonical(string path, string assetsRoot) =>
            Path.GetFullPath(VirtualPath.ToAbsolute(path, assetsRoot));
        return string.Equals(Canonical(a, _assetsPath), Canonical(b, _assetsPath), StringComparison.OrdinalIgnoreCase);
    }
}
