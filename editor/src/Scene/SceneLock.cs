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
//  【置き場（2026-09-19 に変更）】
//   `<プロジェクトルート>/cache/editor/scene_locks/<ルートからの相対パス>.lock`
//   例: assets/mainGame/MainGame.scene
//       → cache/editor/scene_locks/assets/mainGame/MainGame.scene.lock
//
//   かつては `<scene>.lock`（シーンの隣＝アセットの中）へ置いていた。これが
//   バージョン管理の追跡対象に入り、次の事故を起こした:
//     ・シーンを開いている間ロックファイルが「未追跡の追加 1 件」として出る
//     ・その状態ではマージ前の確認が止まるため、利用者はロックファイルを
//       そのまま送信してしまい、リポジトリへロックが混入した
//     ・混入したロックは別マシンのものとして扱われる（生死が判定できない）ので、
//       クローンした共同作業者のエディタでそのシーンが永久に読み取り専用になる
//   `cache/` は初期コミットから `.loreignore` 済みで、視点サイドカー
//   （`cache/editor/view/`）や VCS の状態（`cache/editor/vcs/`）と同じ
//   「利用者別の状態」の置き場。ロックも本質的に利用者別の状態なのでここへ置く。
//
//  【仕様】
//   ・ロックファイルに {pid, headless, started_at, machine} の JSON を書く
//   ・シーンを閉じる／切り替える／エディタ終了時に削除する
//   ・書いた側のプロセスがもう生きていない（＝異常終了で残った）ロックは
//     無効として上書きする。ロックが原因で誰も編集できなくなる方が有害なため。
//   ・旧位置（`<scene>.lock`）のファイルも取得時に見る（移行と汚染の後始末）。
//     判定は <see cref="ClassifyLegacyLock"/> に切り出してある。
//
//  【依存】
//   WPF にも ProjectContext にも EditorLog にも依存しない
//   （`editor/tests/AiSafetyTests` がこのファイル単体をリンクして使う）。
//   プロジェクトルートは引数で受け取り、ログは任意のコールバックへ流す。
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
/// 旧位置（<c>&lt;scene&gt;.lock</c>）のロックファイルに対して取るべき処置。
///
/// <para>
/// 旧位置のファイルは「旧版エディタが実際に開いている印」か
/// 「バージョン管理経由で紛れ込んだ汚染」のどちらかで、意味がまるで違う。
/// 前者を無視すると多重編集で保存が消え、後者を尊重すると共同作業者が
/// 永久に読み取り専用になる。そのため 3 通りに分けて扱う。
/// </para>
/// </summary>
public enum LegacySceneLockAction
{
    /// <summary>旧位置にファイルが無い。何もしない。</summary>
    None,

    /// <summary>同じマシンの生きているエディタが開いている。従来どおり尊重して読み取り専用にする。</summary>
    Respect,

    /// <summary>同じマシンの無効なロック（プロセス死亡／自分自身／壊れて読めない）。消して掃除する。</summary>
    Delete,

    /// <summary>別マシンのロック。尊重も削除もせず無視する（汚染の可能性が高い）。</summary>
    IgnoreForeign,
}

/// <summary>
/// シーンロックの読み書き。すべて静的メソッドで、状態は持たない。
/// </summary>
public static class SceneLock
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>ロックファイルの拡張子（シーンのファイル名へ付け足す）。</summary>
    public const string LOCK_EXTENSION = ".lock";

    /// <summary>
    /// 利用者別の状態を置くフォルダ名（プロジェクトルート直下）。
    /// <c>SEEDEditor.Project.ProjectPaths.CACHE_DIR_NAME</c> と同じ値。
    /// このファイルは ProjectPaths に依存できない（単体テストへ単独でリンクするため）ので
    /// 値を写しているが、**変えるときは両方そろえること**。
    /// </summary>
    public const string CACHE_DIR_NAME = "cache";

    /// <summary>キャッシュのうちエディタ用の区画（視点サイドカー・VCS の状態と同じ階層）。</summary>
    public const string EDITOR_DIR_NAME = "editor";

    /// <summary>シーンロックの置き場（<c>cache/editor/</c> の下）。</summary>
    public const string SCENE_LOCKS_DIR_NAME = "scene_locks";

    /// <summary>ログ行の接頭辞（grep しやすくするため）。</summary>
    public const string LOG_PREFIX = "[シーンロック]";

    /// <summary>ロック保持者が居るときに保存を拒否するメッセージの雛形。</summary>
    public const string DENY_LOCKED_FORMAT =
        "このシーンは別のエディタが開いています（{0}）。読み取り専用のため保存できません。"
      + "そちらを閉じてから開き直してください。";

    /// <summary>旧位置のロックを尊重した（旧版エディタが開いている）ときのログ雛形（{0}=パス {1}=保持者）。</summary>
    public const string LOG_LEGACY_RESPECTED_FORMAT =
        LOG_PREFIX + " 旧位置のロックが有効でした（{0}） — {1}";

    /// <summary>旧位置の無効なロックを掃除したときのログ雛形（{0}=パス）。</summary>
    public const string LOG_LEGACY_REMOVED_FORMAT =
        LOG_PREFIX + " 旧位置の無効なロックを削除しました（{0}）。"
      + "バージョン管理に追跡されていた場合は「削除」の変更として現れるので、送信すればリポジトリから消えます。";

    /// <summary>旧位置の別マシンのロックを無視したときのログ雛形（{0}=パス {1}=保持者）。</summary>
    public const string LOG_LEGACY_IGNORED_FORMAT =
        LOG_PREFIX + " 旧位置に別マシンのロックがありました（{0}） — {1}。"
      + "バージョン管理経由で紛れ込んだ可能性が高いため無視します（削除もしません）。";

    // ── JSON ─────────────────────────────────────────────────────

    /// <summary>ロックファイルの書き出し設定（毎回作らないよう共有する）。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // ── パス ─────────────────────────────────────────────────────

    /// <summary>
    /// シーンパスに対応するロックファイルのパスを返す。
    ///
    /// <para>
    /// 通常は <c>&lt;projectRoot&gt;/cache/editor/scene_locks/&lt;相対パス&gt;.lock</c>。
    /// プロジェクトルートが分からない、またはシーンがルートの外にある（別ドライブ・
    /// ルートの上位など）ときは、置き場を決められないので旧位置
    /// （<see cref="LegacyLockPathFor"/>）へ落とす。
    /// </para>
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルート（未確定なら null／空でよい）。</param>
    public static string LockPathFor(string scenePath, string? projectRoot)
    {
        var legacy = LegacyLockPathFor(scenePath);
        if (string.IsNullOrWhiteSpace(scenePath))   return legacy;
        if (string.IsNullOrWhiteSpace(projectRoot)) return legacy;

        var relative = TryGetRelativePathInside(projectRoot!, scenePath);
        if (relative is null) return legacy;

        return Path.Combine(
            projectRoot!, CACHE_DIR_NAME, EDITOR_DIR_NAME, SCENE_LOCKS_DIR_NAME,
            relative + LOCK_EXTENSION);
    }

    /// <summary>
    /// 旧位置（シーンの隣）のロックファイルのパスを返す。
    /// 2026-09-19 より前のエディタが使っていた場所で、読み取りと掃除のためだけに残す。
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    public static string LegacyLockPathFor(string scenePath) => scenePath + LOCK_EXTENSION;

    /// <summary>
    /// <paramref name="root"/> から見た <paramref name="target"/> の相対パスを返す。
    /// ルートの外（上位・別ドライブ）やルート自身なら null。
    /// </summary>
    /// <param name="root">基準フォルダ。</param>
    /// <param name="target">対象パス。</param>
    private static string? TryGetRelativePathInside(string root, string target)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));

            // ルート自身・空は「中のファイル」ではない。
            if (string.IsNullOrEmpty(relative) || relative == ".") return null;
            // 別ドライブだと絶対パスがそのまま返る。
            if (Path.IsPathRooted(relative)) return null;
            // ".." で始まるならルートの外。"..foo" のようなファイル名を誤判定しないよう
            // 直後が区切り（または終端）であることまで確かめる。
            if (relative.StartsWith("..", StringComparison.Ordinal)
                && (relative.Length == 2
                    || relative[2] == Path.DirectorySeparatorChar
                    || relative[2] == Path.AltDirectorySeparatorChar))
            {
                return null;
            }
            return relative;
        }
        catch
        {
            // 不正な文字などでパスを扱えないときは旧位置へ落とす（例外で開けなくしない）。
            return null;
        }
    }

    // ── 判定（純粋関数・テスト対象）───────────────────────────────

    /// <summary>
    /// ロック情報が「無効（stale）」かどうかを判定する。**新位置のロック用**。
    ///
    /// 無効とみなす条件:
    ///   ・情報が読めない（null / PID が 0 以下）
    ///   ・自分自身の PID（同一プロセスが張り直しているだけ）
    ///   ・別マシンではない かつ そのプロセスがもう生きていない
    ///
    /// 別マシンが張ったロックは生死を確認できないため、有効なものとして扱う
    /// （読み取り専用に落ちるだけで、データを失うより安全側）。新位置は
    /// バージョン管理に入らないので、ここに別マシンのロックがあるのは
    /// 「プロジェクトフォルダをネットワーク共有している」ときだけであり、
    /// その場合は尊重するのが正しい。
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
        if (!IsSameMachine(info.Machine, currentMachine)) return false;
        return !isProcessAlive(info.Pid);
    }

    /// <summary>
    /// 旧位置（<c>&lt;scene&gt;.lock</c>）のロックに対する処置を決める。**新位置とは規則が違う**。
    ///
    /// <list type="bullet">
    ///   <item>同じマシン・生きている別プロセス → <see cref="LegacySceneLockAction.Respect"/>。
    ///         旧版エディタが実際に開いているので従来どおり読み取り専用にする。</item>
    ///   <item>同じマシン・無効（プロセス死亡／自分自身／壊れて読めない）
    ///         → <see cref="LegacySceneLockAction.Delete"/>。残骸なので消す。
    ///         追跡されているブランチでは「削除」の変更として見えるようになり、
    ///         送信すればリポジトリから消せる。</item>
    ///   <item>別マシン → <see cref="LegacySceneLockAction.IgnoreForeign"/>。
    ///         ★ここが新位置と決定的に違う。旧位置はバージョン管理に混入し得るため、
    ///         別マシンのロックは「クローンに付いてきた汚染」である可能性が高い。
    ///         尊重すると共同作業者のエディタが永久に読み取り専用になる。
    ///         一方で本物の共有フォルダかもしれないので削除もしない。</item>
    /// </list>
    ///
    /// <para>
    /// マシン名が空のロックも「別マシン」として扱う（誰のものか決められないので
    /// 尊重も削除もしない）。
    /// </para>
    /// </summary>
    /// <param name="exists">旧位置にファイルが存在したか。</param>
    /// <param name="info">読み取れたロック情報（壊れていれば null）。</param>
    /// <param name="currentPid">このプロセスの PID。</param>
    /// <param name="currentMachine">このマシン名。</param>
    /// <param name="isProcessAlive">PID が生きているかを返す判定関数（テストで差し替える）。</param>
    public static LegacySceneLockAction ClassifyLegacyLock(
        bool exists, SceneLockInfo? info, int currentPid, string currentMachine,
        Func<int, bool> isProcessAlive)
    {
        if (!exists) return LegacySceneLockAction.None;

        // 壊れて読めないロックは、そこに居るはずのプロセスを特定できない。
        // 旧位置はもう使わない場所なので、掃除して先へ進む。
        if (info is null || info.Pid <= 0) return LegacySceneLockAction.Delete;

        if (!IsSameMachine(info.Machine, currentMachine)) return LegacySceneLockAction.IgnoreForeign;

        // 自分自身が残した旧位置のファイルは残骸（新位置へ移行済みのため）。
        if (info.Pid == currentPid) return LegacySceneLockAction.Delete;

        return isProcessAlive(info.Pid)
            ? LegacySceneLockAction.Respect
            : LegacySceneLockAction.Delete;
    }

    /// <summary>ロックのマシン名が自分のマシンかどうか（空は「不明」＝別マシン扱い）。</summary>
    /// <param name="lockMachine">ロックに書かれていたマシン名。</param>
    /// <param name="currentMachine">このマシン名。</param>
    private static bool IsSameMachine(string? lockMachine, string currentMachine)
        => !string.IsNullOrEmpty(lockMachine)
           && string.Equals(lockMachine, currentMachine, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>ロックファイル（新位置）を読む。無い／壊れている場合は null。</summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルート（未確定なら null／空でよい）。</param>
    public static SceneLockInfo? Read(string scenePath, string? projectRoot)
        => ReadAt(LockPathFor(scenePath, projectRoot));

    /// <summary>指定パスのロックファイルを読む。無い／壊れている場合は null。</summary>
    /// <param name="lockPath">ロックファイルの絶対パス。</param>
    private static SceneLockInfo? ReadAt(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath)) return null;
            return JsonSerializer.Deserialize<SceneLockInfo>(File.ReadAllText(lockPath));
        }
        catch
        {
            // 壊れたロックは「無い」とみなす（読めないロックで作業不能になる方が有害）。
            return null;
        }
    }

    /// <summary>
    /// ロックの取得を試みる。
    ///
    /// <para>
    /// 新位置のロックを見てから、旧位置のロック（移行前のエディタ・汚染）を
    /// <see cref="ClassifyLegacyLock"/> の規則で処理し、最後に新位置へ書く。
    /// </para>
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルート（未確定なら null／空でよい）。</param>
    /// <param name="headless">このインスタンスがヘッドレスか。</param>
    /// <param name="holder">取得できなかった場合、現在の保持者。</param>
    /// <param name="log">ログ 1 行を流す先（省略可。WPF 非依存に保つためコールバックで受ける）。</param>
    /// <returns>取得できた（＝編集可能）なら true。</returns>
    public static bool TryAcquire(
        string scenePath, string? projectRoot, bool headless, out SceneLockInfo? holder,
        Action<string>? log = null)
    {
        holder = null;
        var pid      = Environment.ProcessId;
        var machine  = Environment.MachineName;
        var lockPath = LockPathFor(scenePath, projectRoot);

        // ── 1. 新位置のロック ──
        var existing = ReadAt(lockPath);
        if (!IsStale(existing, pid, machine, IsProcessAlive))
        {
            holder = existing;
            return false;
        }

        // ── 2. 旧位置のロック（移行と汚染の後始末）──
        //   フォールバックで新位置＝旧位置になっている場合は 1 で見終わっているので飛ばす。
        var legacyPath = LegacyLockPathFor(scenePath);
        if (!IsSamePath(legacyPath, lockPath))
        {
            var legacyExists = FileExistsSafe(legacyPath);
            var legacyInfo   = legacyExists ? ReadAt(legacyPath) : null;
            switch (ClassifyLegacyLock(legacyExists, legacyInfo, pid, machine, IsProcessAlive))
            {
                case LegacySceneLockAction.Respect:
                    holder = legacyInfo;
                    log?.Invoke(string.Format(
                        LOG_LEGACY_RESPECTED_FORMAT, legacyPath, legacyInfo!.Describe()));
                    return false;

                case LegacySceneLockAction.Delete:
                    if (TryDelete(legacyPath))
                        log?.Invoke(string.Format(LOG_LEGACY_REMOVED_FORMAT, legacyPath));
                    break;

                case LegacySceneLockAction.IgnoreForeign:
                    log?.Invoke(string.Format(
                        LOG_LEGACY_IGNORED_FORMAT, legacyPath, legacyInfo!.Describe()));
                    break;
            }
        }

        // ── 3. 新位置へ書く ──
        var info = new SceneLockInfo
        {
            Pid       = pid,
            Headless  = headless,
            StartedAt = DateTime.UtcNow.ToString("o"),
            Machine   = machine,
        };
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(lockPath, JsonSerializer.Serialize(info, WriteOptions));
        }
        catch
        {
            // 書けない（読み取り専用メディア等）場合はロックを諦めて編集を許す。
            // ロックはあくまで事故防止であり、書けないこと自体を失敗にはしない。
        }
        return true;
    }

    /// <summary>
    /// 旧位置（シーンの隣）の無効なロックだけを掃除する。**ロックの取得はしない**。
    ///
    /// <para>
    /// <see cref="TryAcquire"/> の掃除は「シーンを開いた瞬間」にしか走らない。
    /// ところが旧位置のロックがバージョン管理に追跡されているブランチへ
    /// **シーンを開いたまま切り替える**と、切り替えがそのファイルを作業コピーへ書き戻す。
    /// シーンのパスは変わらないのでロックは取り直されず、残骸が残り続ける。
    /// シーンの読み直し（自動再読込を含む）のたびにこれを呼んで、その場で片付ける。
    /// 片付くと「削除」の変更として見えるようになり、送信すればリポジトリから消える。
    /// </para>
    /// <para>
    /// 消すのは <see cref="LegacySceneLockAction.Delete"/> と判定されたものだけ
    /// （同じマシンの生きているエディタのロック・別マシンのロックには触らない）。
    /// </para>
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルート（未確定なら null／空でよい）。</param>
    /// <param name="log">ログ 1 行を流す先（省略可）。</param>
    /// <returns>旧位置のファイルを消したら true。</returns>
    public static bool SweepLegacy(string scenePath, string? projectRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(scenePath)) return false;

        // フォールバック中は旧位置が現役のロック置き場なので、掃除の対象にしない。
        var legacyPath = LegacyLockPathFor(scenePath);
        if (IsSamePath(legacyPath, LockPathFor(scenePath, projectRoot))) return false;

        var exists = FileExistsSafe(legacyPath);
        var info   = exists ? ReadAt(legacyPath) : null;
        var action = ClassifyLegacyLock(
            exists, info, Environment.ProcessId, Environment.MachineName, IsProcessAlive);
        if (action != LegacySceneLockAction.Delete) return false;

        if (!TryDelete(legacyPath)) return false;
        log?.Invoke(string.Format(LOG_LEGACY_REMOVED_FORMAT, legacyPath));
        return true;
    }

    /// <summary>
    /// 自分が持っているロックを解放する。他インスタンスのロックは消さない。
    /// </summary>
    /// <param name="scenePath">対象シーンの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルート（取得時と同じ値を渡すこと）。</param>
    public static void Release(string scenePath, string? projectRoot)
    {
        var lockPath = LockPathFor(scenePath, projectRoot);
        var info     = ReadAt(lockPath);
        if (info is not null && info.Pid != Environment.ProcessId) return;
        TryDelete(lockPath);
    }

    // ── ファイル操作の下請け ─────────────────────────────────────

    /// <summary>ファイルの存在を例外なしで調べる。</summary>
    /// <param name="path">対象パス。</param>
    private static bool FileExistsSafe(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>ファイルを消す（消せなくても致命的ではない）。消せたら true。</summary>
    /// <param name="path">対象パス。</param>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>2 つのパスが同じ場所を指すか（大文字小文字は無視する）。</summary>
    /// <param name="a">パス A。</param>
    /// <param name="b">パス B。</param>
    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
