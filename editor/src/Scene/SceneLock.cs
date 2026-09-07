using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Scene;

// ============================================================
//  SceneLock.cs — シーンファイルの多重編集ロック
//
//  【なぜ必要か】
//   同じ .scene を 2 つのエディタ（例: 利用者の対話エディタと AI が起動した
//   ヘッドレスエディタ）が同時に開くと、後から保存した方が相手の変更を
//   丸ごと消す。ファイルロックではなく「誰が開いているか」を示す
//   目印ファイルを置き、2 つ目のインスタンスは読み取り専用へ落とす。
//
//  【仕様】
//   ・`<scene>.lock` に {pid, headless, started_at, machine} の JSON を書く
//   ・シーンを閉じる／切り替える／エディタ終了時に削除する
//   ・書いた側のプロセスがもう生きていない（＝異常終了で残った）ロックは
//     無効として上書きする。ロックが原因で誰も編集できなくなる方が有害なため。
//
//  WPF に依存しない（単体テストからリンクして使う）。
// ============================================================

/// <summary>ロックファイルの内容。</summary>
public sealed class SceneLockInfo
{
    /// <summary>ロックを取得したエディタのプロセス ID。</summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>ヘッドレス起動のインスタンスか。</summary>
    [JsonPropertyName("headless")]
    public bool Headless { get; set; }

    /// <summary>ロックを取得した時刻（UTC・ISO 8601）。</summary>
    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = "";

    /// <summary>ロックを取得したマシン名。ネットワーク共有上のアセットを考慮して残す。</summary>
    [JsonPropertyName("machine")]
    public string Machine { get; set; } = "";

    /// <summary>ログ・エラーメッセージ用の 1 行表現。</summary>
    public string Describe()
        => $"PID {Pid}{(Headless ? "（ヘッドレス）" : "")} / {Machine} / {StartedAt}";
}

/// <summary>
/// シーンロックの読み書き。すべて静的メソッドで、状態は持たない。
/// </summary>
public static class SceneLock
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>ロックファイルの拡張子（シーンパスへ付け足す）。</summary>
    public const string LOCK_EXTENSION = ".lock";

    /// <summary>ロック保持者が居るときに保存を拒否するメッセージの雛形。</summary>
    public const string DENY_LOCKED_FORMAT =
        "このシーンは別のエディタが開いています（{0}）。読み取り専用のため保存できません。"
      + "そちらを閉じてから開き直してください。";

    // ── パス ─────────────────────────────────────────────────────

    /// <summary>シーンパスに対応するロックファイルのパスを返す。</summary>
    public static string LockPathFor(string scenePath) => scenePath + LOCK_EXTENSION;

    // ── 判定（純粋関数・テスト対象）───────────────────────────────

    /// <summary>
    /// ロック情報が「無効（stale）」かどうかを判定する。
    ///
    /// 無効とみなす条件:
    ///   ・情報が読めない（null / PID が 0 以下）
    ///   ・自分自身の PID（同一プロセスが張り直しているだけ）
    ///   ・別マシンではない かつ そのプロセスがもう生きていない
    ///
    /// 別マシンが張ったロックは生死を確認できないため、有効なものとして扱う
    /// （読み取り専用に落ちるだけで、データを失うより安全側）。
    /// </summary>
    /// <param name="info">読み取ったロック情報。</param>
    /// <param name="currentPid">このプロセスの PID。</param>
    /// <param name="currentMachine">このマシン名。</param>
    /// <param name="isProcessAlive">PID が生きているかを返す判定関数（テストで差し替える）。</param>
    public static bool IsStale(
        SceneLockInfo? info, int currentPid, string currentMachine, Func<int, bool> isProcessAlive)
    {
        if (info is null || info.Pid <= 0) return true;
        if (info.Pid == currentPid) return true;
        // 別マシンのロックは生死判定ができないので有効扱い（安全側）。
        if (!string.Equals(info.Machine, currentMachine, StringComparison.OrdinalIgnoreCase))
            return false;
        return !isProcessAlive(info.Pid);
    }

    /// <summary>実際に PID のプロセスが生きているかを調べる（既定の判定関数）。</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            // プロセスが存在しない場合は例外になる＝死んでいる。
            return false;
        }
    }

    // ── 入出力 ───────────────────────────────────────────────────

    /// <summary>ロックファイルを読む。無い／壊れている場合は null。</summary>
    public static SceneLockInfo? Read(string scenePath)
    {
        var path = LockPathFor(scenePath);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SceneLockInfo>(File.ReadAllText(path));
        }
        catch
        {
            // 壊れたロックは「無い」とみなす（読めないロックで作業不能になる方が有害）。
            return null;
        }
    }

    /// <summary>
    /// ロックの取得を試みる。
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="headless">このインスタンスがヘッドレスか。</param>
    /// <param name="holder">取得できなかった場合、現在の保持者。</param>
    /// <returns>取得できた（＝編集可能）なら true。</returns>
    public static bool TryAcquire(string scenePath, bool headless, out SceneLockInfo? holder)
    {
        holder = null;
        var existing = Read(scenePath);
        var pid      = Environment.ProcessId;
        var machine  = Environment.MachineName;

        if (!IsStale(existing, pid, machine, IsProcessAlive))
        {
            holder = existing;
            return false;
        }

        var info = new SceneLockInfo
        {
            Pid       = pid,
            Headless  = headless,
            StartedAt = DateTime.UtcNow.ToString("o"),
            Machine   = machine,
        };
        try
        {
            File.WriteAllText(LockPathFor(scenePath),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            // 書けない（読み取り専用メディア等）場合はロックを諦めて編集を許す。
            // ロックはあくまで事故防止であり、書けないこと自体を失敗にはしない。
            return true;
        }
    }

    /// <summary>
    /// 自分が持っているロックを解放する。他インスタンスのロックは消さない。
    /// </summary>
    public static void Release(string scenePath)
    {
        var info = Read(scenePath);
        if (info is not null && info.Pid != Environment.ProcessId) return;
        try { File.Delete(LockPathFor(scenePath)); } catch { /* 消せなくても致命的ではない */ }
    }
}
