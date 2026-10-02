using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SeedMcpServer;
using SpriteRigTests;

namespace AiSafetyTests;

/// <summary>
/// MCP サーバー（SeedMcpServer）を実際に起動し、JSON-RPC を stdin/stdout で話して確かめる。
///
/// <para>
/// 見るもの:
///   1. tools/list の各ツールの名前・説明・引数スキーマが JSON Schema として正しい形か
///      （required が properties にある・enum が文字列の配列・array に items がある など）
///   2. 2026-10-02 の新ツールが載り、seed_batch の cmd の enum に新コマンドがある・seed_launch に gpu_mem_log がある
///   3. 未束縛（seed_launch / seed_attach の前）では新ツールが一切エディタへ届かず拒否される
/// </para>
/// <para>
/// 安全のため、3 で送る引数は「仮に関門が壊れていて既定ポートの利用者のエディタへ届いても何も変えない」値
/// （存在しない動詞・action）だけにしてある。
/// </para>
/// </summary>
public static class McpServerProcessTests
{
    /// <summary>MCP サーバーの dll の場所を渡すアセンブリのメタデータのキー（AiSafetyTests.csproj）。</summary>
    private const string DllMetadataKey = "SeedMcpServerDll";

    /// <summary>MCP サーバーの終了を待つ上限（ミリ秒）。stdin を閉じるとメインループを抜けて終わる。</summary>
    private const int ServerExitTimeoutMs = 30_000;

    /// <summary>2026-10-02 に足したツール。</summary>
    private static readonly string[] NewTools =
        ["seed_platform_sim", "seed_gpu_mem_report", "seed_preview", "seed_template_actor"];

    /// <summary>2026-10-02 に seed_batch の cmd の enum へ足したコマンド。</summary>
    private static readonly string[] NewBatchCommands =
        ["platform_sim", "gpu_mem_report", "preview", "template_actor_list", "template_actor_add"];

    /// <summary>JSON Schema の type に書いてよい名前。</summary>
    private static readonly HashSet<string> SchemaTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean", "array", "object", "null" };

    /// <summary>tools/call で送る、何も変えない引数（ツール名 → arguments の JSON）。</summary>
    private static readonly (string Tool, string ArgsJson)[] HarmlessCalls =
    [
        ("seed_platform_sim",   "{\"verb\":\"noop_test\"}"),
        ("seed_gpu_mem_report", "{}"),
        ("seed_preview",        "{\"action\":\"noop_test\"}"),
        ("seed_template_actor", "{\"action\":\"noop_test\"}"),
    ];

    /// <summary>tools/list の要求の id。</summary>
    private const int ToolsListId = 2;

    /// <summary>tools/call の要求の id の始まり（HarmlessCalls の順に 1 ずつ足す）。</summary>
    private const int FirstCallId = 10;

    /// <summary>1 回だけ起動して結果を使い回す（id → 応答の JSON）。</summary>
    private static readonly Lazy<Dictionary<int, JsonElement>> Replies = new(RunServerOnce);

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("MCP サーバー: tools/list の全ツールのスキーマが正しい形", ToolSchemasAreWellFormed);
        h.Add("MCP サーバー: 新ツール・seed_batch の新コマンド・seed_launch の gpu_mem_log が載っている", NewToolsAreListed);
        h.Add("MCP サーバー: 未束縛では新ツールが拒否される（実プロセス）", NewToolsDeniedWhenUnbound);
    }

    // ============================================================
    //  テスト
    // ============================================================

    private static void ToolSchemasAreWellFormed()
    {
        var tools = Tools();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString() ?? "";
            Check.True(name.Length > 0, "ツール名が空");
            Check.True(names.Add(name), $"ツール名の重複: {name}");
            Check.True(!string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), $"{name}: 説明が空");
            ValidateObjectSchema(tool.GetProperty("inputSchema"), name);
        }
    }

    private static void NewToolsAreListed()
    {
        var byName = Tools().EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        foreach (var tool in NewTools)
            Check.True(byName.ContainsKey(tool), $"{tool} が tools/list に無い");

        // seed_batch の cmd の enum
        var cmdEnum = byName["seed_batch"].GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("operations").GetProperty("items").GetProperty("properties")
            .GetProperty("cmd").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var cmd in NewBatchCommands)
            Check.True(cmdEnum.Contains(cmd), $"seed_batch の cmd の enum に {cmd} が無い");

        // seed_launch の gpu_mem_log
        var gpuMemLog = byName["seed_launch"].GetProperty("inputSchema").GetProperty("properties").GetProperty("gpu_mem_log");
        Check.Equal("boolean", gpuMemLog.GetProperty("type").GetString(), "seed_launch の gpu_mem_log の型");

        // 必須の引数
        Check.True(Required(byName["seed_platform_sim"]).SequenceEqual(new[] { "verb" }), "seed_platform_sim の必須は verb");
        Check.True(Required(byName["seed_preview"]).SequenceEqual(new[] { "action" }), "seed_preview の必須は action");
        Check.True(Required(byName["seed_template_actor"]).SequenceEqual(new[] { "action" }), "seed_template_actor の必須は action");
    }

    private static void NewToolsDeniedWhenUnbound()
    {
        var replies = Replies.Value;
        for (int i = 0; i < HarmlessCalls.Length; i++)
        {
            var tool = HarmlessCalls[i].Tool;
            Check.True(replies.TryGetValue(FirstCallId + i, out var reply), $"{tool} の応答が無い");
            var result = reply.GetProperty("result");
            Check.True(result.GetProperty("isError").GetBoolean(), $"{tool} は未束縛ではエラーのはず");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            Check.True(text.Contains(SeedInstance.DENY_NOT_BOUND, StringComparison.Ordinal),
                       $"{tool} の拒否理由に束縛の定型文が無い: {text}");
        }
    }

    // ============================================================
    //  スキーマの形の検査
    // ============================================================

    /// <summary>type:"object" のスキーマ（properties・required）を検査する。</summary>
    /// <param name="schema">スキーマ。</param>
    /// <param name="where">エラーの文に出す場所。</param>
    private static void ValidateObjectSchema(JsonElement schema, string where)
    {
        Check.Equal(JsonValueKind.Object, schema.ValueKind, $"{where}: スキーマがオブジェクトでない");
        Check.Equal("object", schema.GetProperty("type").GetString(), $"{where}: type が object でない");
        Check.True(schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object,
                   $"{where}: properties がオブジェクトでない");

        if (schema.TryGetProperty("required", out var required))
        {
            Check.Equal(JsonValueKind.Array, required.ValueKind, $"{where}: required が配列でない");
            foreach (var r in required.EnumerateArray())
                Check.True(r.ValueKind == JsonValueKind.String && props.TryGetProperty(r.GetString()!, out _),
                           $"{where}: required の '{r}' が properties に無い");
        }

        foreach (var prop in props.EnumerateObject())
            ValidatePropertySchema(prop.Value, $"{where}.{prop.Name}");
    }

    /// <summary>1 つの引数のスキーマ（type・enum・items・入れ子の object）を検査する。</summary>
    /// <param name="schema">スキーマ。</param>
    /// <param name="where">エラーの文に出す場所。</param>
    private static void ValidatePropertySchema(JsonElement schema, string where)
    {
        Check.Equal(JsonValueKind.Object, schema.ValueKind, $"{where}: スキーマがオブジェクトでない");
        Check.True(schema.TryGetProperty("description", out var desc) && !string.IsNullOrWhiteSpace(desc.GetString()),
                   $"{where}: 説明が無い");

        // type は省略可（値の型を問わない引数: seed_save_set の value・parent など）
        string? type = null;
        if (schema.TryGetProperty("type", out var typeEl))
        {
            type = typeEl.GetString();
            Check.True(type is not null && SchemaTypes.Contains(type), $"{where}: 知らない type '{typeEl}'");
        }

        if (schema.TryGetProperty("enum", out var en))
        {
            Check.True(en.ValueKind == JsonValueKind.Array && en.GetArrayLength() > 0, $"{where}: enum が空");
            foreach (var v in en.EnumerateArray())
                Check.Equal(JsonValueKind.String, v.ValueKind, $"{where}: enum の値が文字列でない");
        }

        if (type == "array")
        {
            Check.True(schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object,
                       $"{where}: array に items が無い");
            // 要素が object のスキーマ（seed_batch の operations）は中まで見る
            if (items.TryGetProperty("properties", out _)) ValidateObjectSchema(items, $"{where}[]");
        }
    }

    // ============================================================
    //  MCP サーバーの起動と JSON-RPC
    // ============================================================

    /// <summary>tools/list の tools の配列を返す。</summary>
    private static JsonElement Tools()
    {
        Check.True(Replies.Value.TryGetValue(ToolsListId, out var list), "tools/list の応答が無い");
        return list.GetProperty("result").GetProperty("tools");
    }

    /// <summary>ツールの required の並び（無ければ空）。</summary>
    private static IEnumerable<string> Required(JsonElement tool) =>
        tool.GetProperty("inputSchema").TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()!)
            : Enumerable.Empty<string>();

    /// <summary>
    /// MCP サーバーを 1 回起動し、initialize → tools/list → tools/call（何も変えない引数）を送って、
    /// stdin を閉じて終わらせ、応答を id ごとにまとめる。
    /// </summary>
    private static Dictionary<int, JsonElement> RunServerOnce()
    {
        var dll = typeof(McpServerProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == DllMetadataKey)?.Value;
        if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
            throw new InvalidOperationException($"MCP サーバーの出力が見つかりません: {dll}（SeedMcpServer をビルドしてください）");

        // apphost（SeedMcpServer.exe）があればそれを、無ければ dotnet ホストで dll を起動する
        var exe = Path.ChangeExtension(dll, ".exe");
        var psi = File.Exists(exe)
            ? new ProcessStartInfo(exe)
            : new ProcessStartInfo("dotnet", $"\"{dll}\"");
        psi.UseShellExecute        = false;
        psi.RedirectStandardInput  = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.CreateNoWindow         = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardInputEncoding  = new UTF8Encoding(false);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("MCP サーバーを起動できませんでした");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // ── 要求（1 行 1 メッセージ）──
        var input = process.StandardInput;
        input.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        input.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{ToolsListId},\"method\":\"tools/list\",\"params\":{{}}}}");
        for (int i = 0; i < HarmlessCalls.Length; i++)
        {
            var (tool, argsJson) = HarmlessCalls[i];
            input.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{FirstCallId + i},\"method\":\"tools/call\","
                          + $"\"params\":{{\"name\":\"{tool}\",\"arguments\":{argsJson}}}}}");
        }
        input.Close();   // メインループを抜けさせる

        if (!process.WaitForExit(ServerExitTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"MCP サーバーが {ServerExitTimeoutMs} ms 以内に終わりませんでした: {stderrTask.Result}");
        }

        // ── 応答を id ごとにまとめる（通知は来ない。1 行 = 1 応答）──
        var replies = new Dictionary<int, JsonElement>();
        foreach (var line in stdoutTask.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                replies[id.GetInt32()] = doc.RootElement.Clone();
        }
        return replies;
    }
}
