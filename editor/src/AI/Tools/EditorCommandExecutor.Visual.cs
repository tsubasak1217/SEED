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
//                        （max_width / scale / keep_full で縮小して返せる）
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
//    game_input_*      : ゲーム入力の注入（別ファイル: EditorCommandExecutor.GameInput.cs）
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>GPU 撮影（screenshot_gpu）の既定 target。</summary>
    private const string VisualGpuDefaultTarget = "game";

    /// <summary>GPU 撮影の応答 JSON に入れる method 名。</summary>
    private const string VisualGpuMethodName = "gpu";

    /// <summary>縮小オプション: 縮小後の最大幅（px）を指定する引数名。</summary>
    private const string VisualScreenshotMaxWidthKey = "max_width";

    /// <summary>縮小オプション: 縮小率 0..1 を指定する引数名。</summary>
    private const string VisualScreenshotScaleKey = "scale";

    /// <summary>縮小オプション: フル解像度版も残すかを指定する引数名。</summary>
    private const string VisualScreenshotKeepFullKey = "keep_full";

    /// <summary>
    /// GPU 撮影の応答待ちタイムアウト（ミリ秒）。
    /// 非表示時はイベントループ側からフレームを回すため通常は数十 ms で返るが、
    /// 大きなシーンのロード直後などを見込んで余裕を持たせる。
    /// </summary>
    private const int VisualGpuScreenshotTimeoutMs = 15_000;

    /// <summary>アクター未指定・未解決を表す DFS ID。</summary>
    private const int VisualNoActor = -1;

    // ── プロファイラ一発計測（profile）────────────────────────────

    /// <summary>profile の計測秒数の既定値。</summary>
    private const double ProfileDefaultSeconds = 3.0;

    /// <summary>profile の計測秒数の下限（これ未満は 1 窓にフレームが乗らない）。</summary>
    private const double ProfileMinSeconds = 0.2;

    /// <summary>profile の計測秒数の上限（MCP/HTTP のタイムアウトより十分短く）。</summary>
    private const double ProfileMaxSeconds = 30.0;

    /// <summary>
    /// profile の応答待ちに、計測秒数へ上乗せする余裕（ミリ秒）。
    /// ランタイムはフレーム末尾で窓の満了を判定するため、低フレームレート時に
    /// 1〜2 フレームぶん遅れて返ることを見込む。
    /// </summary>
    private const int ProfileTimeoutMarginMs = 5_000;

    /// <summary>profile の引数名: 計測秒数。</summary>
    private const string ProfileSecondsKey = "seconds";

    // ── 図鑑サムネイル生成（generate_fish_thumbnails）の定数 ──────────

    /// <summary>generate_fish_thumbnails の引数名: 出力画像の一辺のピクセル数。</summary>
    private const string FishThumbnailSizeKey = "size";

    /// <summary>図鑑サムネイルの既定サイズ（px）。</summary>
    private const int FishThumbnailDefaultSize = 512;

    /// <summary>図鑑サムネイルの最小サイズ（px）。これ未満は魚の形が判別できない。</summary>
    private const int FishThumbnailMinSize = 64;

    /// <summary>
    /// 図鑑サムネイルの最大サイズ（px）。
    /// 全魚ぶんの PNG がリポジトリに入るため、際限なく大きくできないよう上限を設ける。
    /// </summary>
    private const int FishThumbnailMaxSize = 2048;

    /// <summary>1 匹ぶんの描画応答を待つタイムアウト（ミリ秒）。モデル読み込みを含むため長め。</summary>
    private const int FishThumbnailTimeoutMs = 60_000;

    /// <summary>図鑑サムネイルの視点。魚は横向きの絵が図鑑として分かりやすいので side 固定。</summary>
    private const string FishThumbnailView = "side";

    /// <summary>レベルディレクトリ名の書式（"Lv3" → 3）。</summary>
    private static readonly Regex FishLevelDirPattern = new(@"^Lv(\d+)$", RegexOptions.Compiled);

    /// <summary>魚 prefab の拡張子。</summary>
    private const string FishActorExt = ".actor";

    /// <summary>ランタイムが解決できるアセット URI のスキーム接頭辞。</summary>
    private const string VisualAssetUriPrefix = "assets://";

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// 視覚確認・再生制御系コマンドを実行する。
    /// 本ファイルが扱わないコマンド名の場合は null を返し、呼び出し元がエラーにする。
    /// </summary>
    private async Task<string?> ExecuteVisualToolAsync(string command, string argsJson)
    {
        using var doc = ParseArgs(argsJson);
        var args = doc.RootElement;

        // ゲーム入力の注入（game_input_*）は別ファイルへ分けている。
        // 該当しなければ null が返るので、そのまま下の switch へ落ちる。
        if (ExecuteGameInputTool(command, args) is { } gameInputTask)
            return await gameInputTask;

        // セーブデータ操作・アクタ検索（save_data / find_actor）も別ファイル。
        if (ExecuteSaveDataTool(command, args) is { } saveDataTask)
            return await saveDataTask;

        // デバッグコマンド（script_debug）も別ファイル。
        if (ExecuteScriptDebugTool(command, args) is { } scriptDebugTask)
            return await scriptDebugTask;

        return command switch
        {
            "screenshot"        => ExecuteScreenshot(args),
            "screenshot_gpu"    => await ExecuteScreenshotGpuAsync(args),
            "shutdown"          => ExecuteShutdown(),
            "select_actor"      => await ExecuteSelectActorAsync(args),
            "get_hierarchy"     => ExecuteGetHierarchy(),
            "play_control"      => await ExecutePlayControlAsync(args),
            "send_ipc"          => ExecuteSendIpc(args),
            "prefab_reapply"    => ExecutePrefabReapply(args),
            "anim_preview"      => ExecuteAnimPreview(args),
            "anim_preview_stop" => ExecuteAnimPreviewStop(args),
            "anim_reload"       => ExecuteAnimReload(args),
            "get_log"           => ExecuteGetLog(args),
            "save_scene"        => await ExecuteSaveSceneAsync(args),
            "get_editor_state"  => ExecuteGetEditorState(),
            "profile"           => await ExecuteProfileAsync(args),
            "generate_fish_thumbnails" => await ExecuteGenerateFishThumbnailsAsync(args),
            _                   => null,
        };
    }

    // ── 保存の明示同意 ───────────────────────────────────────────

    /// <summary>ヘッドレスでの保存に必要な明示同意フラグの引数名。</summary>
    private const string SaveConfirmKey = "confirm";

    /// <summary>明示同意が無いままヘッドレス保存を要求されたときのメッセージ。</summary>
    private const string SaveConfirmRequiredMessage =
        "ヘッドレスインスタンスでのシーン保存には confirm:true が必要です"
      + "（利用者が見ていない場所で .scene を書き換えないための安全弁）。";

    /// <summary>引数から真偽値を取り出す。未指定・型違いは false。</summary>
    private static bool GetBool(JsonElement args, string key)
        => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(key, out var el)
        && el.ValueKind == JsonValueKind.True;

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

        // 撮影後の共通後処理として縮小を掛ける（max_width / scale 未指定なら素通り）。
        var shrink = ApplyScreenshotDownscale(args, result.Path!);

        _log($"[AI ツール] screenshot({target}) → {shrink.Path} ({shrink.Width}x{shrink.Height})");
        return Json(new
        {
            ok           = true,
            target,
            path         = shrink.Path,
            width        = shrink.Width,
            height       = shrink.Height,
            scaled       = shrink.Scaled,
            full_width   = shrink.FullWidth,
            full_height  = shrink.FullHeight,
            full_path    = shrink.FullPath,
            warning      = MergeWarnings(result.Warning, shrink.Warning),
            state        = host.RuntimeState.ToString(),
        });
    }

    /// <summary>
    /// ランタイムに GPU 読み戻しでスクリーンショットを撮らせる（IPC SCREENSHOT:）。
    ///
    /// 画面 DC からの BitBlt（<see cref="ExecuteScreenshot"/>）と違い、
    /// ウィンドウが他ウィンドウの裏・画面外・最小化でも撮れる。
    /// そのかわりランタイムの提示画像しか撮れない（エディタ UI 全体は撮れない）。
    /// target は "game" / "viewport" のみ（どちらも提示中のカラーターゲット＝同じ絵）。
    /// </summary>
    private async Task<string> ExecuteScreenshotGpuAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var target = GetString(args, "target") ?? VisualGpuDefaultTarget;
        if (target is not ("game" or "viewport"))
            return Error($"method='gpu' では target は game / viewport のみです（指定: '{target}'）。");

        var path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(
                Path.GetTempPath(), VisualScreenshotSubDir,
                $"seed_{target}_{DateTime.Now.ToString(VisualScreenshotTimeFormat, CultureInfo.InvariantCulture)}.png");
        path = Path.GetFullPath(path);

        var (ok, message, width, height) =
            await host.CaptureRuntimeScreenshotAsync(target, path, VisualGpuScreenshotTimeoutMs);
        if (!ok) return Error(message);

        // GPU 読み戻しで書かれた PNG も、画面キャプチャと同じ後処理で縮小する。
        var shrink = ApplyScreenshotDownscale(args, message);

        _log($"[AI ツール] screenshot_gpu({target}) → {shrink.Path} ({shrink.Width}x{shrink.Height})");
        return Json(new
        {
            ok          = true,
            target,
            method      = VisualGpuMethodName,
            path        = shrink.Path,
            width       = shrink.Width,
            height      = shrink.Height,
            scaled      = shrink.Scaled,
            full_width  = shrink.FullWidth,
            full_height = shrink.FullHeight,
            full_path   = shrink.FullPath,
            warning     = shrink.Warning,
            state       = host.RuntimeState.ToString(),
        });
    }

    /// <summary>
    /// エディタを正常終了させる（ヘッドレス運用の後始末）。
    /// 実際の終了は応答を返した後に行われるため、ここでは受理した旨だけを返す。
    /// </summary>
    private string ExecuteShutdown()
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        host.RequestShutdown();
        _log("[AI ツール] shutdown");
        return Json(new { ok = true, shutting_down = true });
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

    /// <summary>
    /// プロファイラの一発計測を実行する（IPC: PROFILE_DUMP:&lt;秒&gt;）。
    ///
    /// ランタイムは指定秒数ぶんのフレームを 1 窓へ畳み込み、
    /// 「セクション別 CPU 時間ツリー」と「統合バッチ更新ゲートの判定理由集計」を
    /// 1 個の JSON にまとめて返す。プロファイラパネルを開いている必要はない
    /// （計測期間だけランタイムが自動で計測を有効化する）。
    /// </summary>
    private async Task<string> ExecuteProfileAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // 計測秒数（範囲外はクランプ。極端な値で HTTP タイムアウトへ落ちないようにする）。
        var seconds = ProfileDefaultSeconds;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(ProfileSecondsKey, out var secElem)
            && secElem.ValueKind == JsonValueKind.Number
            && secElem.TryGetDouble(out var requested))
        {
            seconds = Math.Clamp(requested, ProfileMinSeconds, ProfileMaxSeconds);
        }

        var timeoutMs = (int)(seconds * 1000.0) + ProfileTimeoutMarginMs;
        var json      = await host.ProfileDumpAsync(seconds, timeoutMs);
        if (json is null)
            return Error($"PROFILE_DUMP が {timeoutMs} ms 以内に返りませんでした"
                       + "（ランタイム未接続、または描画ループが回っていない可能性）。");

        return Json(new
        {
            ok      = true,
            seconds,
            dump    = RawJson(json),
        });
    }

    // ── 図鑑サムネイル生成 ───────────────────────────────────────

    /// <summary>図鑑サムネイル生成の対象 1 匹ぶん（走査結果）。</summary>
    /// <param name="Level">魚レベル（prefab の置き場所 Lv&lt;N&gt; 由来）。</param>
    /// <param name="LevelDirName">レベルディレクトリ名（"Lv3" など）。</param>
    /// <param name="FileName">.actor のファイル名（拡張子つき）。</param>
    /// <param name="Stem">.actor のファイル名（拡張子なし）。出力 PNG の名前になる。</param>
    private readonly record struct FishThumbnailTarget(
        int Level, string LevelDirName, string FileName, string Stem);

    /// <summary>
    /// 全魚 prefab の図鑑サムネイル（透過 PNG）を生成し、最後に FishCatalog.cs を再生成する。
    ///
    /// <para>
    /// ランタイムの <c>RENDER_ACTOR_THUMBNAIL:</c> は 1 往復 1 応答なので、
    /// **必ず 1 匹ずつ逐次**で回す（並行させると応答の対応付けができない）。
    /// 途中で失敗した魚があっても中断せず、最後まで回して失敗一覧を返す
    /// （1 匹の不備で他 30 匹の再生成をやり直す羽目にならないようにするため）。
    /// </para>
    /// </summary>
    /// <param name="args">ツール引数。<c>size</c>（省略可）だけを見る。</param>
    private async Task<string> ExecuteGenerateFishThumbnailsAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // ── 出力サイズ（範囲外はクランプ）──────────────────────────
        var sizeArg = GetDouble(args, FishThumbnailSizeKey);
        var sizePx  = sizeArg is null
            ? FishThumbnailDefaultSize
            : (int)Math.Clamp(Math.Round(sizeArg.Value), FishThumbnailMinSize, FishThumbnailMaxSize);

        // ── 対象の走査（レベル昇順 → ファイル名の序数順で決定的に）────
        var fishDir = FishCatalogGenerator.GetFishActorDir(_assetsPath);
        if (!Directory.Exists(fishDir))
            return Error($"魚 prefab のディレクトリが見つかりません: {fishDir}");

        var targets = CollectFishThumbnailTargets(fishDir);
        if (targets.Count == 0)
            return Error($"魚 prefab（Lv<N>/*.actor）が 1 件も見つかりません: {fishDir}");

        // ── 1 匹ずつ描かせる ────────────────────────────────────────
        var failures  = new List<object>();
        var succeeded = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            var target   = targets[i];
            var outPath  = FishCatalogGenerator.GetImagePath(
                _assetsPath, target.LevelDirName, target.Stem);
            var actorUri = $"{VisualAssetUriPrefix}{FishCatalogGenerator.FISH_ACTOR_REL_DIR}"
                         + $"/{target.LevelDirName}/{target.FileName}";

            // 出力先ディレクトリはランタイム任せにせず、こちらで先に用意しておく。
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            _log($"[図鑑] ({i + 1}/{targets.Count}) {target.LevelDirName}/{target.Stem} -> {outPath}");

            var (ok, message) = await host.RenderActorThumbnailAsync(
                actorUri, outPath, sizePx, FishThumbnailView, FishThumbnailTimeoutMs);

            if (ok)
            {
                succeeded++;
            }
            else
            {
                _log($"[図鑑] 失敗: {target.LevelDirName}/{target.Stem}: {message}");
                failures.Add(new { actor = actorUri, error = message });
            }
        }

        // ── カタログ（FishCatalog.cs）の再生成 ──────────────────────
        // 画像が一部欠けていてもカタログ自体は prefab から作れるので、必ず更新する。
        string catalogPath;
        try
        {
            var (path, count) = FishCatalogGenerator.Generate(_assetsPath);
            catalogPath = path;
            _log($"[図鑑] FishCatalog.cs を再生成しました（{count} 種）: {path}");
        }
        catch (Exception ex)
        {
            return Error($"FishCatalog.cs の生成に失敗しました: {ex.Message}");
        }

        return Json(new
        {
            ok            = failures.Count == 0,
            total         = targets.Count,
            succeeded,
            failed        = failures.Count,
            catalog_path  = catalogPath,
            failures      = failures.ToArray(),
        });
    }

    /// <summary>
    /// 魚 prefab ディレクトリを走査して、サムネイル生成対象を決定的な順序で列挙する。
    /// 並び順は「レベル昇順 → ファイル名（拡張子なし）の序数順」。
    /// </summary>
    /// <param name="fishDir">魚 prefab のルートディレクトリ（絶対パス）。</param>
    private static List<FishThumbnailTarget> CollectFishThumbnailTargets(string fishDir)
    {
        var targets = new List<FishThumbnailTarget>();

        foreach (var levelDir in Directory.GetDirectories(fishDir))
        {
            // "Lv<N>" 以外（FishBase.actor の置き場など）は図鑑の対象外。
            var levelDirName = Path.GetFileName(levelDir);
            var matched      = FishLevelDirPattern.Match(levelDirName);
            if (!matched.Success) continue;
            if (!int.TryParse(matched.Groups[1].Value, out var level)) continue;

            foreach (var actorFile in Directory.GetFiles(levelDir, "*" + FishActorExt))
            {
                var fileName = Path.GetFileName(actorFile);
                if (!fileName.EndsWith(FishActorExt, StringComparison.Ordinal)) continue;
                targets.Add(new FishThumbnailTarget(
                    level, levelDirName, fileName, fileName[..^FishActorExt.Length]));
            }
        }

        return targets
            .OrderBy(t => t.Level)
            .ThenBy(t => t.Stem, StringComparer.Ordinal)
            .ToList();
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

    /// <summary>
    /// プレハブインスタンスを .actor の最新内容で再展開する（PREFAB_REAPPLY 系）。
    ///
    /// 指定方法は 3 通りで、いずれか 1 つだけを使う:
    /// <list type="bullet">
    ///   <item><c>actor_dfs_id</c> / <c>name</c>: そのアクタ配下のインスタンスだけを更新</item>
    ///   <item><c>prefab_path</c>: その .actor を参照するインスタンスだけを更新</item>
    ///   <item><c>all: true</c>: シーン内の全プレハブインスタンスを更新</item>
    /// </list>
    ///
    /// いずれも**破壊的**（インスタンス側で加えた変更がファイル内容で上書きされる）だが、
    /// ランタイム側で Undo 1 操作として記録されるため Ctrl+Z で戻せる。
    /// </summary>
    private string ExecutePrefabReapply(JsonElement args)
    {
        // all: true — シーン内の全プレハブ（引数なしの IPC）。
        if (GetBool(args, "all"))
        {
            _sendToRuntime(PrefabReapplyAllCommand);
            return Json(new { ok = true, target = "all", sent = PrefabReapplyAllCommand });
        }

        // prefab_path — 指定した 1 本の .actor を参照する全インスタンス。
        var prefabPath = GetString(args, "prefab_path");
        if (!string.IsNullOrWhiteSpace(prefabPath))
        {
            var cmd = $"{PrefabReapplyPathCommandPrefix}{prefabPath}";
            _sendToRuntime(cmd);
            return Json(new { ok = true, target = "path", prefab_path = prefabPath, sent = cmd });
        }

        // actor_dfs_id / name — そのアクタ配下のインスタンス。
        var (dfsId, resolveError) = ResolveActorDfsId(args);
        if (resolveError is not null)
        {
            return Error(resolveError
                + "（シーン全体を更新するなら all:true、"
                + "特定の .actor を指すなら prefab_path を使ってください）");
        }

        var single = FormattableString.Invariant($"{PrefabReapplyCommandPrefix}{dfsId}");
        _sendToRuntime(single);
        return Json(new { ok = true, target = "actor", actor_dfs_id = dfsId, sent = single });
    }

    /// <summary>指定アクタ配下のプレハブインスタンスを更新する IPC の接頭辞。</summary>
    private const string PrefabReapplyCommandPrefix = "PREFAB_REAPPLY:";

    /// <summary>指定した .actor を参照する全インスタンスを更新する IPC の接頭辞。</summary>
    private const string PrefabReapplyPathCommandPrefix = "PREFAB_REAPPLY_PATH:";

    /// <summary>シーン内の全プレハブインスタンスを更新する IPC（引数なし）。</summary>
    private const string PrefabReapplyAllCommand = "PREFAB_REAPPLY_ALL";

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
    private async Task<string> ExecuteSaveSceneAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // ヘッドレスインスタンスの保存は明示同意（confirm:true）を要求する。
        // AI が「とりあえず保存」してしまうと、利用者が見ていない場所で
        // .scene が書き換わる。保存は必ず意図した操作として行わせる。
        if (SEEDEditor.Headless.EditorStartupOptions.IsHeadless && !GetBool(args, SaveConfirmKey))
            return Error(SaveConfirmRequiredMessage);

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
    /// スクリーンショット引数（max_width / scale / keep_full）に従って PNG を縮小する。
    ///
    /// 撮影方式（画面 DC / GPU 読み戻し）に依らない共通後処理。
    /// 縮小指定が無ければ何もせず、元画像の寸法をそのまま返す。
    /// WPF のイメージング API を使うため UI スレッドから呼ぶこと
    /// （AI ブリッジは Dispatcher へマーシャルしているのでこの前提は満たされる）。
    /// </summary>
    /// <param name="args">ツール呼び出し引数。</param>
    /// <param name="path">撮影済み PNG の絶対パス。</param>
    private static DownscaleResult ApplyScreenshotDownscale(JsonElement args, string path)
    {
        var maxWidthArg = GetDouble(args, VisualScreenshotMaxWidthKey);
        var scaleArg    = GetDouble(args, VisualScreenshotScaleKey);
        var keepFull    = GetBool(args, VisualScreenshotKeepFullKey);

        // max_width は px 数なので整数へ丸める（0 以下は「指定なし」扱い）。
        int? maxWidth = maxWidthArg is not null && maxWidthArg.Value >= 1.0
            ? (int)Math.Round(maxWidthArg.Value)
            : null;

        return ScreenshotDownscaler.Apply(path, maxWidth, scaleArg, keepFull);
    }

    /// <summary>撮影側と縮小側の警告を 1 本にまとめる（両方無ければ null）。</summary>
    private static string? MergeWarnings(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
        if (string.IsNullOrEmpty(b)) return a;
        return a + " / " + b;
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
