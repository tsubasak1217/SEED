// ============================================================
//  EditorCommandExecutor.Visual.cs — 視覚確認・再生制御系コマンド
//
//  Claude Code などの外部エージェントが「実際の見た目」を確認しながら
//  作業できるようにするためのコマンド群。EditorCommandExecutor の partial 実装。
//
//  【シーン編集系（本体ファイル）との違い】
//   ・本体側はランタイムへ IPC を投げるだけの一方向コマンドが中心。
//   ・こちらはエディタ本体の状態（ウィンドウ・再生状態・選択）を触るため、
//     IEditorAiHost 経由で MainWindow へ委譲する。
//   ・戻り値は機械可読性を優先してすべて JSON（成功: {"ok":true,...} /
//     失敗: {"ok":false,"error":"..."}）に統一する。
//     ※本体側の既存コマンドは AI へ日本語の文章を返す設計なので、そちらは変更しない。
//
//  【対応コマンド】
//    screenshot        : ビューポート / ゲーム画面 / エディタ全体を PNG でキャプチャ
//    select_actor      : アクターを選択し ACTOR_COMPONENTS を返す
//    get_hierarchy     : 現在のヒエラルキーツリー
//    play_control      : play / pause / resume / stop
//    send_ipc          : 生 IPC 文字列の送信（低レベル）
//    anim_preview      : ANIM_PREVIEW（Edit モードのアニメプレビュー）
//    anim_preview_stop : ANIM_PREVIEW_STOP
//    anim_reload       : ANIM_RELOAD（.anim 書き換え後のキャッシュ破棄）
//    get_log           : エディタログ（ランタイム stderr 含む）の末尾 N 行
//    save_scene        : 現在のシーンを保存（Ctrl+S 相当）
//    get_editor_state  : エディタ状態のスナップショット
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SEEDEditor.AI.Capture;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>アクター選択時に ACTOR_COMPONENTS を待つタイムアウト（ミリ秒）。</summary>
    private const int VisualSelectTimeoutMs = 10_000;

    /// <summary>シーン保存完了を待つタイムアウト（ミリ秒）。</summary>
    private const int VisualSaveTimeoutMs = 10_000;

    /// <summary>play_control の wait_seconds に許す最大値（秒）。HTTP 側のタイムアウトより短くする。</summary>
    private const double VisualMaxWaitSeconds = 20.0;

    /// <summary>get_log の既定行数。</summary>
    private const int VisualDefaultLogLines = 200;

    /// <summary>get_log で一度に返せる最大行数。</summary>
    private const int VisualMaxLogLines = 5_000;

    /// <summary>スクリーンショットの既定出力先（OS のテンポラリ配下）のサブフォルダ名。</summary>
    private const string VisualScreenshotSubDir = "seed_mcp";

    /// <summary>スクリーンショットのファイル名に使う時刻フォーマット。</summary>
    private const string VisualScreenshotTimeFormat = "yyyyMMdd_HHmmss_fff";

    /// <summary>アクター未指定・未解決を表す DFS ID。</summary>
    private const int VisualNoActor = -1;

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// 視覚確認・再生制御系コマンドを実行する。
    /// 本ファイルが扱わないコマンド名の場合は null を返し、呼び出し元がエラーにする。
    /// </summary>
    private async Task<string?> ExecuteVisualToolAsync(string command, string argsJson)
    {
        using var doc = ParseArgs(argsJson);
        var args = doc.RootElement;

        return command switch
        {
            "screenshot"        => ExecuteScreenshot(args),
            "select_actor"      => await ExecuteSelectActorAsync(args),
            "get_hierarchy"     => ExecuteGetHierarchy(),
            "play_control"      => await ExecutePlayControlAsync(args),
            "send_ipc"          => ExecuteSendIpc(args),
            "anim_preview"      => ExecuteAnimPreview(args),
            "anim_preview_stop" => ExecuteAnimPreviewStop(args),
            "anim_reload"       => ExecuteAnimReload(args),
            "get_log"           => ExecuteGetLog(args),
            "save_scene"        => await ExecuteSaveSceneAsync(),
            "get_editor_state"  => ExecuteGetEditorState(),
            _                   => null,
        };
    }

    // ── コマンド実装 ─────────────────────────────────────────────

    /// <summary>
    /// 現在の描画内容を PNG でキャプチャする。
    /// target: "viewport"（シーンビュー）/ "game"（Play 中のゲーム画面）/ "editor"（ウィンドウ全体）。
    /// viewport と game はどちらもランタイムウィンドウを撮る（埋め込み Play では同一ウィンドウ）。
    /// </summary>
    private string ExecuteScreenshot(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var target = GetString(args, "target") ?? "viewport";
        nint hwnd;
        bool clientAreaOnly;
        switch (target)
        {
            case "viewport":
            case "game":
                hwnd           = host.RuntimeWindowHandle;
                clientAreaOnly = true;
                if (hwnd == nint.Zero)
                    return Error("ランタイムウィンドウがありません（ランタイム未起動）。");
                break;

            case "editor":
                hwnd           = host.EditorWindowHandle;
                clientAreaOnly = false;
                break;

            default:
                return Error($"不明な target '{target}'（viewport / game / editor のいずれか）。");
        }

        var path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(
                Path.GetTempPath(), VisualScreenshotSubDir,
                $"seed_{target}_{DateTime.Now.ToString(VisualScreenshotTimeFormat, CultureInfo.InvariantCulture)}.png");
        path = Path.GetFullPath(path);

        var result = WindowScreenCapture.Capture(hwnd, clientAreaOnly, path);
        if (!result.Ok) return Error(result.Error ?? "キャプチャに失敗しました。");

        _log($"[AI ツール] screenshot({target}) → {result.Path} ({result.Width}x{result.Height})");
        return Json(new
        {
            ok      = true,
            target,
            path    = result.Path,
            width   = result.Width,
            height  = result.Height,
            warning = result.Warning,
            state   = host.RuntimeState.ToString(),
        });
    }

    /// <summary>
    /// アクターを選択し、そのコンポーネント情報（ACTOR_COMPONENTS）を返す。
    /// actor_dfs_id か name のどちらかを指定する（name はヒエラルキーから解決する）。
    /// </summary>
    private async Task<string> ExecuteSelectActorAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var (dfsId, resolveError) = ResolveActorDfsId(args);
        if (resolveError is not null) return Error(resolveError);

        var json = await host.SelectActorAsync(dfsId, VisualSelectTimeoutMs);
        if (json is null)
            return Error($"ACTOR_COMPONENTS が {VisualSelectTimeoutMs} ms 以内に返りませんでした"
                       + "（ランタイム未接続、または DFS ID が範囲外の可能性）。");

        return Json(new
        {
            ok           = true,
            actor_dfs_id = dfsId,
            components   = RawJson(json),
        });
    }

    /// <summary>現在のヒエラルキーツリー（ランタイムが最後に push した内容）を返す。</summary>
    private string ExecuteGetHierarchy()
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var json  = host.HierarchyJson;
        var nodes = ParseHierarchyNodes(json);
        return Json(new
        {
            ok        = true,
            count     = nodes.Count,
            hierarchy = RawJson(json),
        });
    }

    /// <summary>
    /// 再生制御を実行する。wait_seconds を指定すると遷移後にその秒数だけ待ってから返す
    /// （直後の screenshot がゲーム進行後の状態を撮れるようにするため）。
    /// </summary>
    private async Task<string> ExecutePlayControlAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var action = GetString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return Error("'action' が必要です（play / pause / resume / stop）。");

        var error = await host.ControlPlayAsync(action);
        if (error is not null) return Error(error);

        // 待機時間は上限でクランプする（HTTP 側のタイムアウトを超えないようにするため）
        var waitSeconds = Math.Clamp(GetDouble(args, "wait_seconds") ?? 0.0, 0.0, VisualMaxWaitSeconds);
        if (waitSeconds > 0.0)
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

        return Json(new
        {
            ok           = true,
            action,
            state        = host.RuntimeState.ToString(),
            waited_secs  = waitSeconds,
        });
    }

    /// <summary>
    /// 生の IPC 文字列をランタイムへ送る（低レベルの逃げ道）。
    /// 応答は待たないため、結果の確認は get_log / screenshot などで別途行う。
    /// </summary>
    private string ExecuteSendIpc(JsonElement args)
    {
        var command = GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Error("'command' が必要です（例: \"ANIM_RELOAD:seed://animations/foo.anim\"）。");

        _sendToRuntime(command);
        return Json(new { ok = true, sent = command });
    }

    /// <summary>Edit モードのアニメーションプレビューを指定時刻へ適用する（ANIM_PREVIEW）。</summary>
    private string ExecuteAnimPreview(JsonElement args)
    {
        var (dfsId, resolveError) = ResolveActorDfsId(args);
        if (resolveError is not null) return Error(resolveError);

        var clipPath = GetString(args, "clip_path");
        if (string.IsNullOrWhiteSpace(clipPath))
            return Error("'clip_path' が必要です（.anim の絶対パスまたは seed:// 仮想パス）。");

        var time = GetDouble(args, "time") ?? 0.0;
        var virtualPath = ToVirtualClipPath(clipPath);

        // アニメーションタイムラインパネルが送るのと同一形式
        _sendToRuntime(FormattableString.Invariant(
            $"ANIM_PREVIEW:{dfsId},{virtualPath},{time}"));

        return Json(new { ok = true, actor_dfs_id = dfsId, clip_path = virtualPath, time });
    }

    /// <summary>アニメーションプレビューを終了して元値へ復元する（ANIM_PREVIEW_STOP）。</summary>
    private string ExecuteAnimPreviewStop(JsonElement args)
    {
        var (dfsId, resolveError) = ResolveActorDfsId(args);
        if (resolveError is not null) return Error(resolveError);

        _sendToRuntime($"ANIM_PREVIEW_STOP:{dfsId}");
        return Json(new { ok = true, actor_dfs_id = dfsId });
    }

    /// <summary>.anim のロード済みキャッシュを破棄させる（ANIM_RELOAD）。</summary>
    private string ExecuteAnimReload(JsonElement args)
    {
        var clipPath = GetString(args, "clip_path");
        if (string.IsNullOrWhiteSpace(clipPath))
            return Error("'clip_path' が必要です（.anim の絶対パスまたは seed:// 仮想パス）。");

        var virtualPath = ToVirtualClipPath(clipPath);
        _sendToRuntime($"ANIM_RELOAD:{virtualPath}");
        return Json(new { ok = true, clip_path = virtualPath });
    }

    /// <summary>
    /// エディタログの末尾 N 行を返す。ランタイムの stderr も "[STDERR] " 付きで
    /// このファイルへ流れ込むため、LOAD_ERROR / スクリプトの例外もここで拾える。
    /// </summary>
    private string ExecuteGetLog(JsonElement args)
    {
        var lines = (int)Math.Clamp(GetDouble(args, "lines") ?? VisualDefaultLogLines, 1, VisualMaxLogLines);
        var path  = EditorLog.FilePath;

        if (!File.Exists(path))
            return Error($"ログファイルが見つかりません: {path}");

        try
        {
            // エディタ自身が書き込み用に開きっぱなしのため、共有読み取りで開く
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            // 末尾 N 行だけをリングバッファ的に保持する（巨大ログでもメモリを食わない）
            var buffer = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (buffer.Count == lines) buffer.Dequeue();
                buffer.Enqueue(line);
            }

            return Json(new
            {
                ok       = true,
                path,
                lines    = buffer.Count,
                content  = string.Join("\n", buffer),
            });
        }
        catch (Exception ex)
        {
            return Error($"ログの読み取りに失敗しました: {ex.Message}");
        }
    }

    /// <summary>現在のシーンを保存する（Ctrl+S 相当）。</summary>
    private async Task<string> ExecuteSaveSceneAsync()
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var error = await host.SaveSceneAsync(VisualSaveTimeoutMs);
        if (error is not null) return Error(error);

        return Json(new { ok = true, scene_path = host.CurrentScenePath });
    }

    /// <summary>エディタの状態スナップショットを返す。</summary>
    private string ExecuteGetEditorState()
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        return Json(new
        {
            ok                   = true,
            state                = host.RuntimeState.ToString(),
            runtime_connected    = host.RuntimeConnected,
            runtime_window       = host.RuntimeWindowHandle.ToString(),
            scene_path           = host.CurrentScenePath,
            selected_actor_dfs_id = host.SelectedActorDfsId,
            actor_count          = ParseHierarchyNodes(host.HierarchyJson).Count,
            assets_path          = _assetsPath,
        });
    }

    // ── ヘルパー: 引数解析 ───────────────────────────────────────

    /// <summary>引数 JSON を解析する。空・不正な場合も落とさず空オブジェクトとして扱う。</summary>
    private static JsonDocument ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return JsonDocument.Parse("{}");
        try   { return JsonDocument.Parse(argsJson); }
        catch { return JsonDocument.Parse("{}"); }
    }

    /// <summary>文字列プロパティを取り出す（数値や真偽値で来た場合も文字列化して受け入れる）。</summary>
    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null   => null,
            _                    => el.GetRawText(),
        };
    }

    /// <summary>数値プロパティを取り出す（文字列で来た場合もパースを試みる）。</summary>
    private static double? GetDouble(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out var v) ? v : null,
            _ => null,
        };
    }

    /// <summary>
    /// actor_dfs_id / name / actor のいずれかから対象アクターの DFS ID を解決する。
    /// 名前指定はランタイムが push した最新ヒエラルキーから引く。
    /// </summary>
    /// <returns>(DFS ID, エラー理由)。成功時はエラーが null。</returns>
    private (int dfsId, string? error) ResolveActorDfsId(JsonElement args)
    {
        // 数値指定を優先する（actor は数値・名前どちらでも受け付ける互換キー）
        var id = GetDouble(args, "actor_dfs_id") ?? GetDouble(args, "actor");
        if (id is not null)
        {
            var value = (int)id.Value;
            return value < 0
                ? (VisualNoActor, $"actor_dfs_id が負の値です: {value}")
                : (value, null);
        }

        var name = GetString(args, "name") ?? GetString(args, "actor");
        if (string.IsNullOrWhiteSpace(name))
            return (VisualNoActor, "'actor_dfs_id'（数値）または 'name'（アクター名）が必要です。");

        var host = Host;
        if (host is null)
            return (VisualNoActor, "名前解決にはエディタ本体への接続が必要です（host 未設定）。");

        var nodes   = ParseHierarchyNodes(host.HierarchyJson);
        var matches = nodes.FindAll(n => string.Equals(n.Name, name, StringComparison.Ordinal));
        if (matches.Count == 0)
            matches = nodes.FindAll(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

        if (matches.Count == 0)
            return (VisualNoActor, $"アクター '{name}' がヒエラルキーに見つかりません"
                                 + "（get_hierarchy で現在の名前を確認してください）。");

        // 同名が複数ある場合は DFS 順で最初のものを使う（AI 側が ID 指定へ切り替えられるよう明記する）
        return (matches[0].Id, null);
    }

    // ── ヘルパー: ヒエラルキー ───────────────────────────────────

    /// <summary>ヒエラルキー JSON から取り出す最小限のノード情報。</summary>
    private readonly record struct HierarchyNodeInfo(int Id, string Name);

    /// <summary>
    /// HIERARCHY JSON（配列）を解析して (id, name) の一覧にする。
    /// 名前解決とアクター数のカウントにのみ使うので、他のフィールドは読み飛ばす。
    /// </summary>
    private static List<HierarchyNodeInfo> ParseHierarchyNodes(string json)
    {
        var result = new List<HierarchyNodeInfo>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var node in doc.RootElement.EnumerateArray())
            {
                if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var id   = idEl.GetInt32();
                var name = node.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? ""
                    : "";
                result.Add(new HierarchyNodeInfo(id, name));
            }
        }
        catch { /* 解析できない場合は空一覧として扱う */ }

        return result;
    }

    // ── ヘルパー: パス変換 ───────────────────────────────────────

    /// <summary>
    /// .anim のパスをランタイムが解釈できる形へ正規化する。
    /// アセットフォルダ配下の絶対パスは seed:// 仮想パスへ変換し、
    /// すでに仮想パス（またはアセット外）ならそのまま使う。
    /// </summary>
    private string ToVirtualClipPath(string clipPath)
        => VirtualPath.ToVirtual(clipPath, _assetsPath);

    // ── ヘルパー: JSON 応答 ──────────────────────────────────────

    /// <summary>成功／任意のオブジェクトを JSON 文字列にする。</summary>
    private static string Json(object payload)
        => JsonSerializer.Serialize(payload, VisualJsonOptions);

    /// <summary>エラー応答 {"ok":false,"error":"..."} を組み立てる。</summary>
    private static string Error(string message)
        => Json(new { ok = false, error = message });

    /// <summary>
    /// 文字列として受け取った JSON を、そのまま入れ子の JSON 値として埋め込むために
    /// JsonElement へ変換する。解析できない場合は文字列としてそのまま埋め込む。
    /// </summary>
    private static object RawJson(string json)
    {
        try   { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return json; }
    }

    /// <summary>応答 JSON のシリアライズ設定。日本語をエスケープせずそのまま出す。</summary>
    private static readonly JsonSerializerOptions VisualJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
