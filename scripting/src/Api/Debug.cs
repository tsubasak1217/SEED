using System;
using System.Collections.Generic;

namespace SEED;

/// <summary>
/// スクリプトからのログ出力と、外部（エディタ／MCP）からのデバッグ指示の受け口。
///
/// <b>ログ</b>は標準出力／標準エラーへ書き出し、エンジンのログに流れる。
/// <c>System.Console</c> を直接使わずに済むよう、Unity 風の <c>Debug.Log</c> を提供する。
///
/// <b>デバッグコマンド</b>（<see cref="OnCommand"/>）は、
/// エディタ／MCP から送られた <c>SCRIPT_DEBUG:{name},{arg}</c> を受け取る仕組み。
/// 「ゲームの途中の状態を人の操作なしで作る」ための<b>開発用の入口</b>で、
/// AI が自動でゲームを検証するときの標準手段になっている（docs/editor_mcp.md）。
/// </summary>
public static class Debug
{
    // ─── ログ ────────────────────────────────────────────────

    /// <summary>情報ログ。</summary>
    public static void Log(object? message)
        => Console.WriteLine($"[Script] {message}");

    /// <summary>警告ログ。</summary>
    public static void LogWarning(object? message)
        => Console.WriteLine($"[Script:警告] {message}");

    /// <summary>エラーログ（標準エラーへ）。</summary>
    public static void LogError(object? message)
        => Console.Error.WriteLine($"[Script:エラー] {message}");

    // ─── デバッグコマンド ────────────────────────────────────

    /// <summary>
    /// コマンド名 → ハンドラ群の対応表【配り先を決める唯一の場所】。
    ///
    /// 同じ名前へ複数のスクリプトが登録できる（全部に配る）。
    /// 名前は大文字小文字を区別しない（送り手が綴りを気にしなくて済むように）。
    /// </summary>
    private static readonly Dictionary<string, List<Action<string>>> Handlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// デバッグコマンドのハンドラを登録する。
    ///
    /// 同じ <paramref name="name"/> と <paramref name="handler"/> の組を二重に登録しても
    /// 1 つしか残らない（<c>OnStart</c> が二度走っても配信が二重にならないように）。
    ///
    /// ハンドラは<b>ゲームスレッドの <c>BeginFrame</c></b> で呼ばれるので、
    /// 中で ECS を触って構わない。
    ///
    /// <code>
    /// public override void OnStart()
    /// {
    ///     SEED.Debug.OnCommand("catch_test", arg => FakeCatch(arg));
    /// }
    /// public override void OnDestroy()
    /// {
    ///     SEED.Debug.OffCommand("catch_test");   // 破棄時に必ず外す
    /// }
    /// </code>
    /// </summary>
    /// <param name="name">コマンド名（空・空白のみは無視）。</param>
    /// <param name="handler">呼ばれる処理。引数はコマンドの引数文字列（無指定なら空文字）。</param>
    public static void OnCommand(string name, Action<string> handler)
    {
        if (string.IsNullOrWhiteSpace(name) || handler is null) { return; }

        string key = name.Trim();
        if (!Handlers.TryGetValue(key, out List<Action<string>>? list))
        {
            list = new List<Action<string>>();
            Handlers[key] = list;
        }
        if (list.Contains(handler)) { return; }
        list.Add(handler);
    }

    /// <summary>
    /// デバッグコマンドのハンドラを外す【登録解除の唯一の入口】。
    ///
    /// <paramref name="handler"/> を省略（null）すると、その名前の登録を<b>全部</b>外す。
    /// スクリプトの <c>OnDestroy</c> から必ず呼ぶこと
    /// （外さないと、破棄済みのスクリプトのハンドラが呼ばれ続ける）。
    /// </summary>
    /// <param name="name">コマンド名。</param>
    /// <param name="handler">外すハンドラ。null ならその名前を丸ごと外す。</param>
    public static void OffCommand(string name, Action<string>? handler = null)
    {
        if (string.IsNullOrWhiteSpace(name)) { return; }

        string key = name.Trim();
        if (!Handlers.TryGetValue(key, out List<Action<string>>? list)) { return; }

        if (handler is null) { Handlers.Remove(key); return; }
        list.Remove(handler);
        if (list.Count == 0) { Handlers.Remove(key); }
    }

    /// <summary>登録をすべて捨てる（シーン遷移・Play 停止で持ち越さないため）。</summary>
    public static void ResetCommandHandlers() => Handlers.Clear();

    /// <summary>
    /// 溜まっているデバッグコマンドを取り出してハンドラへ配る
    /// 【外部指示がゲームへ入る唯一の場所】。
    ///
    /// <see cref="ScriptBridge"/> がフレーム先頭（<c>BeginFrame</c>）で 1 回呼ぶ。
    /// 待ち行列が空になるまで回すので、同じフレームに複数届いていても取りこぼさない。
    ///
    /// ハンドラが例外を投げても<b>次のハンドラ・次のコマンドへ進む</b>
    /// （デバッグ用の入口が落ちてゲーム本体が止まるのを避けるため）。
    /// 未登録の名前が来たら、綴り間違いに気付けるよう警告を出す。
    /// </summary>
    internal static void DispatchPendingCommands()
    {
        // 1 フレームで捌く上限。ハンドラが自分で送り返すような書き方をしても
        // 無限ループにならないようにするための蓋。
        for (int i = 0; i < MaxCommandsPerFrame; i++)
        {
            if (!ScriptHost.TryTakeDebugCommand(out string name, out string arg)) { return; }

            if (!Handlers.TryGetValue(name, out List<Action<string>>? list) || list.Count == 0)
            {
                LogWarning($"[Debug] 登録されていないデバッグコマンド: {name}");
                continue;
            }

            // 配信中に OnCommand / OffCommand が呼ばれても壊れないよう写しを回す
            var snapshot = list.ToArray();
            foreach (var handler in snapshot)
            {
                try { handler(arg); }
                catch (Exception e) { LogError($"[Debug] コマンド {name} のハンドラで例外: {e}"); }
            }
        }
    }

    /// <summary>1 フレームで処理するデバッグコマンドの上限。</summary>
    private const int MaxCommandsPerFrame = 16;
}
