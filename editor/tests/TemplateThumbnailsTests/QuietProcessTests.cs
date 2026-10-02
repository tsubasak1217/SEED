// ============================================================
//  QuietProcessTests.cs — 見張りのスレッドを止めてから Process を捨てる（後片付けの順）
//  （docs/reviews/2026-10-02_code_review.md #17）
//
//  ランタイムの代わりに、窓を作らない短命のプロセス（ping で 127.0.0.1 へ数回）を QuietProcess で起動し、
//  道具の後片付けと同じ「止める（Kill）→ すぐ Dispose」をして、Dispose から戻った時点で見張りのスレッドが止まっていること・
//  Dispose を 2 回呼んでも落ちないこと・Dispose の後に HasExited を聞いても落ちないことを確かめる。
// ============================================================

using SEEDEditor.Tools.SeedTemplateThumbnails.Runtime;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>QuietProcess の後片付けの単体テスト。</summary>
public static class QuietProcessTests
{
    /// <summary>窓を作らずにしばらく動くプロセス（Windows に必ずある）。</summary>
    private const string PingExeName = "PING.EXE";

    /// <summary>ping の回数（約 1 秒ずつ。テストの間に終わらない長さ）。</summary>
    private const string PingCount = "5";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("見張り: 止めた直後に Dispose しても見張りのスレッドは止まってから Process を捨てる・2 回目の Dispose も落ちない", DisposeJoinsWatcher);
    }

    /// <summary>Dispose は見張りのスレッドを止めてから Process を捨てる。</summary>
    private static void DisposeJoinsWatcher()
    {
        using var temp = new TempFolder();
        var exe = Path.Combine(Environment.SystemDirectory, PingExeName);
        var logs = new List<string>();
        var process = QuietProcess.Start(exe, ["-n", PingCount, "127.0.0.1"], temp.Path, temp.Combine("ping.log"),
            line => { lock (logs) logs.Add(line); });
        int pid = process.Process.Id;
        try
        {
            Check.True(process.IsWatching, "起動した直後は見張りのスレッドが動いている");

            // 道具の後片付けと同じ順: 止める（Kill はすぐには終わらない）→ すぐ Dispose
            process.Process.Kill();
            process.Dispose();

            Check.True(!process.IsWatching, "Dispose から戻った時点で見張りのスレッドが止まっていない");
            process.Dispose();                                   // 2 回目は何もしない（落ちない）
            Check.True(process.HasExited(), "Dispose の後の HasExited は「終わった」とみなす（例外を投げない）");
        }
        finally
        {
            // 途中で失敗したときも ping を残さない（Dispose の後なら Process は使えないので、控えた ID で探して止める）
            try
            {
                using var leftover = System.Diagnostics.Process.GetProcessById(pid);
                leftover.Kill();
            }
            catch (ArgumentException) { /* もう終わっている */ }
            catch (InvalidOperationException) { /* もう終わっている */ }
            catch (System.ComponentModel.Win32Exception) { /* 終わりかけで止められない */ }
            process.Dispose();
        }
    }
}
