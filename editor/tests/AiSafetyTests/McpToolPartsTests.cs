using System.Diagnostics;
using SEEDEditor.AI.Tools.RuntimeIpc;
using SeedMcpServer;
using SpriteRigTests;

namespace AiSafetyTests;

/// <summary>
/// 2026-10-02 の MCP ツール（seed_platform_sim / seed_gpu_mem_report / seed_launch の gpu_mem_log）の部品の単体テスト。
///
/// <para>
/// エディタとランタイムを動かさずに確かめられる部分だけを見る:
/// IPC の 1 行の組み立てと応答の解釈（ランタイムの ipc.rs・platform_sim_ops.rs・gpu_mem_ops.rs と同じ書式）、
/// GPU メモリの内訳の要約表、起動時の環境変数（エディタ → ランタイムへ受け継がれること）。
/// </para>
/// </summary>
public static class McpToolPartsTests
{
    /// <summary>計測の旗の環境変数（Launcher と同じ名前を使う）。</summary>
    private const string GpuMemEnv = Launcher.ENV_GPU_MEM_LOG;

    /// <summary>子プロセスで環境変数を読むシェル（Windows のみ）。</summary>
    private const string ShellExe = "cmd.exe";

    /// <summary>子プロセスの待ち時間の上限（ミリ秒）。</summary>
    private const int ChildTimeoutMs = 10_000;

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("PLATFORM_SIM: 1 行の命令を組み立てる（前後の空白を落とす・引数なしも可）", PlatformSimBuildsCommand);
        h.Add("PLATFORM_SIM: 行を壊す入力（空・空白入りの動詞・カンマ／改行入りの引数）は送る前に断る", PlatformSimRejectsBrokenInput);
        h.Add("PLATFORM_SIM: OK / ERROR の応答を読み、理由を言い換える", PlatformSimParsesReplies);
        h.Add("GPU_MEM_REPORT: 命令の組み立て・書き出し先・応答の解釈", GpuMemReportIpcRoundTrip);
        h.Add("待ち合わせの結果: 応答が無いときだけ理由を返す", AiIpcReplyDescribesFailures);
        h.Add("GPU メモリの要約表: 合計・分類・上位（日本語の分類名）と完全な JSON", GpuMemFormatterSummarizes);
        h.Add("GPU メモリの要約表: 上位の件数を絞れる・失敗の応答はそのまま返す", GpuMemFormatterTopAndPassThrough);
        h.Add("seed_launch の環境変数: gpu_mem_log は true=1・false=消す・省略=触れない", LaunchEnvironmentTriState);
        h.Add("環境変数はエディタ → ランタイム（子プロセス）へ受け継がれ、false なら消える", EnvironmentReachesChildProcess);
    }

    // ============================================================
    //  PLATFORM_SIM
    // ============================================================

    private static void PlatformSimBuildsCommand()
    {
        var cmd = PlatformSimIpc.TryBuildCommand(" permission ", new[] { " post_notifications", "denied " }, out var error);
        Check.True(error is null, $"組み立てられなかった: {error}");
        Check.Equal("PLATFORM_SIM:permission,post_notifications,denied", cmd, "引数 2 つ");

        Check.Equal("PLATFORM_SIM:lifecycle,paused",
                    PlatformSimIpc.TryBuildCommand("lifecycle", new[] { "paused" }, out _), "引数 1 つ");
        // 引数の数の検査はランタイムの表が正典（bad_arguments で答える）。ここでは送る
        Check.Equal("PLATFORM_SIM:lifecycle",
                    PlatformSimIpc.TryBuildCommand("lifecycle", null, out _), "引数なし");
    }

    private static void PlatformSimRejectsBrokenInput()
    {
        foreach (var verb in new[] { null, "", "   ", "per mission", "permission,x" })
        {
            Check.True(PlatformSimIpc.TryBuildCommand(verb, Array.Empty<string>(), out var error) is null && error is not null,
                       $"動詞 '{verb ?? "(null)"}' は断るはず");
        }
        foreach (var arg in new[] { "a,b", "a\nb", "a\rb" })
        {
            Check.True(PlatformSimIpc.TryBuildCommand("permission", new[] { "exact_alarm", arg }, out var error) is null
                       && error!.Contains("args[1]"),
                       $"引数 '{arg.Replace("\n", "\\n").Replace("\r", "\\r")}' は断り、位置を示すはず");
        }
    }

    private static void PlatformSimParsesReplies()
    {
        Check.True(PlatformSimIpc.TryParseReply("PLATFORM_SIM_OK:{\"ok\":true,\"status\":\"denied\"}", out var ok), "OK を読める");
        Check.True(ok.Ok && ok.Payload == "{\"ok\":true,\"status\":\"denied\"}", "OK の中身は返答の JSON");

        Check.True(PlatformSimIpc.TryParseReply("PLATFORM_SIM_ERROR:not_playing", out var ng), "ERROR を読める");
        Check.True(!ng.Ok && ng.Payload == PlatformSimIpc.ReasonNotPlaying, "ERROR の中身は理由の名前");
        Check.True(!PlatformSimIpc.TryParseReply("INPUT_OK", out _), "ほかの応答は読まない");

        Check.True(PlatformSimIpc.DescribeReason(PlatformSimIpc.ReasonNotPlaying).Contains("seed_play"), "not_playing は Play を案内する");
        Check.True(PlatformSimIpc.DescribeReason("mystery").Contains("mystery"), "知らない理由は名前を残す");
        Check.True(PlatformSimIpc.ReplyPrefixes.Contains(PlatformSimIpc.OkPrefix)
                   && PlatformSimIpc.ReplyPrefixes.Contains(PlatformSimIpc.ErrorPrefix), "待つ頭は OK と ERROR");
    }

    // ============================================================
    //  GPU_MEM_REPORT
    // ============================================================

    private static void GpuMemReportIpcRoundTrip()
    {
        Check.Equal("GPU_MEM_REPORT", GpuMemReportIpc.BuildCommand(null), "パスなし");
        Check.Equal(@"GPU_MEM_REPORT:C:\t\a.json", GpuMemReportIpc.BuildCommand(@" C:\t\a.json "), "パスつき（前後の空白は落とす）");

        bool threw = false;
        try { GpuMemReportIpc.BuildCommand("a\nb.json"); }
        catch (ArgumentException) { threw = true; }
        Check.True(threw, "改行を含むパスは断るはず");

        var path = GpuMemReportIpc.MakeOutputPath(Path.GetTempPath(), new DateTime(2026, 10, 2, 3, 4, 5, 678));
        Check.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "seed_gpu_mem_20261002_030405_678.json"), path, "書き出し先");

        Check.True(GpuMemReportIpc.TryParseReply(@"GPU_MEM_REPORT_DONE:C:\t\a.json", out var ok, out var donePath)
                   && ok && donePath == @"C:\t\a.json", "DONE を読める");
        Check.True(GpuMemReportIpc.TryParseReply("GPU_MEM_REPORT_ERROR:計測が無効です", out var ok2, out var reason)
                   && !ok2 && reason == "計測が無効です", "ERROR を読める");
        Check.True(!GpuMemReportIpc.TryParseReply("GPU_MEM_REPORT", out _, out _), "命令そのものは応答ではない");
    }

    private static void AiIpcReplyDescribesFailures()
    {
        Check.True(AiIpcReply.FromLine("X").DescribeFailure(1) is null && AiIpcReply.FromLine("X").HasLine, "応答ありは理由なし");
        Check.True(AiIpcReply.Timeout().DescribeFailure(1234)!.Contains("1234"), "時間切れは待った時間を示す");
        Check.True(AiIpcReply.Disconnected().DescribeFailure(1)!.Contains("接続"), "未接続");
        Check.Equal("Edit でない", AiIpcReply.RefusedBecause("Edit でない").DescribeFailure(1), "断った理由はそのまま");
        Check.True(!AiIpcReply.Timeout().HasLine, "時間切れは行なし");
    }

    // ============================================================
    //  GPU メモリの要約表（GpuMemReportFormatter）
    // ============================================================

    /// <summary>ランタイムの GpuMemReport（serde）と同じ形の見本を、エディタの応答で包んだもの。</summary>
    private const string SampleReply =
        "{\"ok\":true,\"path\":\"C:\\\\tmp\\\\seed_gpu_mem_1.json\",\"state\":\"Edit\",\"report\":{"
      + "\"reason\":\"IPC GPU_MEM_REPORT\",\"frame\":321,\"context\":\"（描画の構成 ui: scene_3d=false）\","
      + "\"tracked_bytes\":3145728,\"texture_bytes\":2097152,\"buffer_bytes\":1048576,\"resource_count\":3,"
      + "\"categories\":["
      + "{\"category\":\"GBuffer\",\"key\":\"gbuffer\",\"name\":\"G-Buffer・デファード\",\"bytes\":2097152,\"count\":2},"
      + "{\"category\":\"UiText\",\"key\":\"ui_text\",\"name\":\"UI の文字\",\"bytes\":1048576,\"count\":1}],"
      + "\"top\":["
      + "{\"kind\":\"Texture\",\"category\":\"GBuffer\",\"label\":\"gbuffer_albedo\",\"detail\":\"1920x1080 Rgba8Unorm\","
      +   "\"bytes\":2097152,\"site_file\":\"src/renderer/gbuffer.rs\",\"site_line\":42,\"frame\":0,\"generation\":1},"
      + "{\"kind\":\"Buffer\",\"category\":\"UiText\",\"label\":\"glyph_atlas\",\"detail\":\"1 MiB\","
      +   "\"bytes\":1048576,\"site_file\":\"src/font/atlas.rs\",\"site_line\":7,\"frame\":0,\"generation\":1}],"
      + "\"stats\":{\"created_count\":5,\"created_bytes\":5000000,\"replaced_count\":2,\"replaced_bytes\":10},"
      + "\"hal\":{\"buffer_bytes\":1048576,\"texture_bytes\":4194304,\"acceleration_structure_bytes\":0,"
      +   "\"memory_allocations\":9,\"buffers\":3,\"textures\":4},"
      + "\"heaps\":null,\"heap_usage_bytes\":null,"
      + "\"swapchain\":{\"width\":1920,\"height\":1080,\"format\":\"Bgra8UnormSrgb\",\"bytes_per_pixel\":4,"
      +   "\"image_count\":3,\"bytes\":24883200}}}";

    private static void GpuMemFormatterSummarizes()
    {
        var text = GpuMemReportFormatter.Format(SampleReply, GpuMemReportFormatter.DEFAULT_TOP);

        Check.True(text.Contains("描画の構成 ui"), "文脈（描画の構成）が出る");
        Check.True(text.Contains("3.00 MiB"), "追跡した資源の合計");
        Check.True(text.Contains("5.00 MiB"), "wgpu-hal の確保ブロック（バッファ＋テクスチャ）");
        Check.True(text.Contains("確保 9 回"), "確保の回数");
        Check.True(text.Contains("実際の確保（ヒープの合計）: 取れませんでした"), "ヒープが取れないとき");
        Check.True(text.Contains("1920x1080 Bgra8UnormSrgb × 3 枚"), "スワップチェイン");
        Check.True(text.Contains("G-Buffer・デファード (gbuffer)"), "分類の表");
        Check.True(text.Contains("UI の文字 / glyph_atlas"), "上位の行の分類は日本語の名前へ引き直す");
        Check.True(text.Contains("src/renderer/gbuffer.rs:42"), "作った場所");
        Check.True(text.Contains(@"ファイル: C:\tmp\seed_gpu_mem_1.json"), "書いたファイル");

        var fullAt = text.IndexOf(GpuMemReportFormatter.FULL_JSON_HEADER, StringComparison.Ordinal);
        Check.True(fullAt > 0 && text.EndsWith(SampleReply, StringComparison.Ordinal), "完全な JSON が最後に付く");
    }

    private static void GpuMemFormatterTopAndPassThrough()
    {
        var one = GpuMemReportFormatter.Format(SampleReply, top: 1);
        var summary = one[..one.IndexOf(GpuMemReportFormatter.FULL_JSON_HEADER, StringComparison.Ordinal)];
        Check.True(summary.Contains("上位 1 件") && summary.Contains("gbuffer_albedo") && !summary.Contains("glyph_atlas"),
                   "上位は 1 件だけ");
        Check.True(GpuMemReportFormatter.Format(SampleReply, top: 0).Contains("上位 1 件"), "0 以下は 1 件に丸める");

        const string error = "ERROR: SEED エディタへ接続できません";
        Check.Equal(error, GpuMemReportFormatter.Format(error, GpuMemReportFormatter.DEFAULT_TOP), "JSON でない失敗はそのまま");
        const string failed = "{\"ok\":false,\"error\":\"計測が無効です\"}";
        Check.Equal(failed, GpuMemReportFormatter.Format(failed, GpuMemReportFormatter.DEFAULT_TOP), "report の無い応答はそのまま");
    }

    // ============================================================
    //  起動時の環境変数（seed_launch の gpu_mem_log）
    // ============================================================

    private static void LaunchEnvironmentTriState()
    {
        static string? Value(IReadOnlyList<KeyValuePair<string, string?>> changes, string name, out bool present)
        {
            var hit = changes.Where(c => c.Key == name).ToList();
            present = hit.Count > 0;
            return present ? hit[0].Value : null;
        }

        var on = Launcher.BuildLaunchEnvironment(headless: true, port: 7301, token: "tok", gpuMemLog: true);
        Check.Equal(Launcher.ENV_FLAG_ON, Value(on, GpuMemEnv, out var p1), "true は 1");
        Check.True(p1, "true は項目がある");
        Check.Equal("7301", Value(on, "SEED_AI_PORT", out _), "ポートも渡す");
        Check.Equal("tok", Value(on, "SEED_AI_TOKEN", out _), "トークンも渡す");
        Check.Equal(Launcher.ENV_FLAG_ON, Value(on, "SEED_HEADLESS", out _), "ヘッドレスの旗");

        var off = Launcher.BuildLaunchEnvironment(headless: false, port: 7302, token: "t", gpuMemLog: false);
        Check.True(Value(off, GpuMemEnv, out var p2) is null && p2, "false は「消す」（値 null の項目）");
        Check.True(!off.Any(c => c.Key == "SEED_HEADLESS"), "ヘッドレスでなければ旗を付けない");

        var keep = Launcher.BuildLaunchEnvironment(headless: true, port: 7303, token: "t", gpuMemLog: null);
        Value(keep, GpuMemEnv, out var p3);
        Check.True(!p3, "省略は触れない（MCP サーバーの環境のまま）");
    }

    /// <summary>
    /// エディタがランタイムを起動するのと同じ形（UseShellExecute=false・Environment に別の項目を足す）で子プロセスを起動し、
    /// 親の環境変数が届くこと（＝seed_launch でエディタへ渡せばランタイムまで届くこと）と、
    /// ApplyEnvironment の「消す」が子に効くことを、実際のプロセスで確かめる。
    /// </summary>
    private static void EnvironmentReachesChildProcess()
    {
        if (!OperatingSystem.IsWindows()) return;   // cmd.exe で読む確かめなので Windows だけ

        var saved = Environment.GetEnvironmentVariable(GpuMemEnv);
        try
        {
            // 「エディタのプロセスに SEED_GPU_MEM_LOG=1 が付いている」状態を作る
            Environment.SetEnvironmentVariable(GpuMemEnv, Launcher.ENV_FLAG_ON);

            // RuntimeManager.LaunchAsync と同じ: Environment に別の項目（ヘッドレスの旗）を足してから起動する
            var inherit = ChildPsi();
            inherit.Environment["SEED_RUNTIME_HEADLESS_TEST"] = "1";
            Check.Equal(Launcher.ENV_FLAG_ON, RunAndReadLine(inherit), "子プロセスに受け継がれる");

            // seed_launch(gpu_mem_log:false): 受け継いだ値を消す
            var removed = ChildPsi();
            Launcher.ApplyEnvironment(removed, Launcher.BuildLaunchEnvironment(false, 7305, "t", gpuMemLog: false));
            Check.Equal($"%{GpuMemEnv}%", RunAndReadLine(removed), "false なら子プロセスには無い（cmd は未定義の変数を展開しない）");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GpuMemEnv, saved);
        }
    }

    /// <summary>子プロセスで SEED_GPU_MEM_LOG を表示する起動情報を作る。</summary>
    private static ProcessStartInfo ChildPsi() => new(ShellExe, $"/c echo %{GpuMemEnv}%")
    {
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        CreateNoWindow         = true,
    };

    /// <summary>子プロセスを起動して標準出力の 1 行目を返す。</summary>
    private static string RunAndReadLine(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("子プロセスを起動できませんでした");
        var line = process.StandardOutput.ReadLine() ?? "";
        if (!process.WaitForExit(ChildTimeoutMs)) process.Kill();
        return line.Trim();
    }
}
