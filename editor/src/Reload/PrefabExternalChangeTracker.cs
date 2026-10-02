using System;
using System.Collections.Generic;

namespace SEEDEditor.Reload;

/// <summary>
/// デバウンスが満了したプレハブの変更 1 件の判定結果（<see cref="PrefabExternalChangeTracker.TakeDue"/>）。
/// </summary>
public enum PrefabChangeVerdict
{
    /// <summary>外部（テキストエディタ・AI・別ツール）の変更。当て直す。</summary>
    Changed,

    /// <summary>内容が前に知っていたものと同じ（タイムスタンプだけの更新・同じ書き込みの重複イベント）。何もしない。</summary>
    Unchanged,

    /// <summary>エディタ自身の書き込み（SAVE_ACTOR・PREFAB_WRITE_BACK・EXPORT_ACTOR。ランタイムがファイルを書く）。何もしない。</summary>
    SelfWrite,

    /// <summary>一括の書き換え（形式の一括アップグレード）の最中・直後。何もしない。</summary>
    Suppressed,

    /// <summary>読み取りの再試行の上限に達した（消えた・ロックされたまま）。何もしない。</summary>
    Unreadable,
}

/// <summary>
/// デバウンスが満了したプレハブの変更 1 件（パスと判定）。
/// </summary>
/// <param name="Path">プレハブの絶対パス（イベントで受け取ったままの綴り）。</param>
/// <param name="Verdict">判定。</param>
public readonly record struct PrefabChangeOutcome(string Path, PrefabChangeVerdict Verdict);

/// <summary>
/// プレハブ（.actor / .actor2d）の外部変更の「いつ・どれを当て直すか」を決める純粋な追跡器。
///
/// 【なぜ切り出すか】
/// ファイル監視（FileSystemWatcher）は 1 回の保存で複数のイベントを出し、書き込みの途中でも届く。
/// また .actor はエディタ自身もランタイム経由で書く（アクタータブの保存 SAVE_ACTOR・Play 中の変更の
/// 書き戻し PREFAB_WRITE_BACK・アクタファイル化 EXPORT_ACTOR）。それらを外部変更と取り違えると、
/// 保存のたびに二重に再展開・当て直しが走る。この判断を WPF・タイマー・ファイル監視から切り離し、
/// 時刻を引数で受けて単体テストで固定する（テスト: editor/tests/AutoReloadPolicyTests）。
///
/// 【規則】（SceneAutoReloader の自己保存の除外と同じ考え方・同じ時間の値）
///   1. デバウンス: パスごとに最後のイベントから <see cref="Debounce"/> 静まったら判定する。
///   2. 内容のハッシュを読む。読めなければ（書き込み中のロック等）デバウンスをやり直す（上限 <see cref="HashRetryLimit"/> 回）。
///   3. ハッシュが前に知っていたものと同じ → Unchanged（touch・重複イベント）。
///   4. 一括の書き換えの最中・直後 → Suppressed（ハッシュは覚える）。
///   5. そのパスの自己書き込みの窓（開始〜終了＋余韻 <see cref="SelfWriteTail"/>。終了の知らせが来なければ
///      開始から <see cref="SelfWriteMaxWait"/> で打ち切り）の中 → SelfWrite（ハッシュは覚える）。
///   6. それ以外 → Changed（ハッシュを覚える。同じ内容の後続のイベントは 3 で落ちる）。
///   自己書き込みの終了時にもその時点のハッシュを覚えるので、余韻が切れた後に遅れて届いた
///   自分の書き込みのイベントも 3 で落ちる。
///
/// パスの同一視は区切り（'/' と '\\'）と大文字小文字の違いを無視する（<see cref="PrefabPlayReapplyQueue"/> と同じ）。
/// 呼び出し側は絶対パスへ揃えてから渡す（仮想パスの解決はここでしない）。
/// スレッド安全ではない（呼び出し側が UI スレッドへ集める）。
/// </summary>
public sealed class PrefabExternalChangeTracker
{
    // ── 定数（マジックナンバー禁止。SceneAutoReloader / ScriptAutoReloader と同じ値）──────────

    /// <summary>最後のイベントからこの時間静まったら判定する（ミリ秒。スクリプト・シーンの自動再読込と同じ値）。</summary>
    private const int DebounceMs = 600;

    /// <summary>自己書き込みの開始から終了の知らせを待つ上限（ミリ秒。応答が失われても監視が死なないための保険）。</summary>
    private const int SelfWriteMaxWaitMs = 15000;

    /// <summary>自己書き込みの終了の知らせの後も自分の書き込みとみなす余韻（ミリ秒。イベントは書き込みより遅れて届くことがある）。</summary>
    private const int SelfWriteTailMs = 1500;

    /// <summary>ハッシュを読めなかったときの再試行の上限（回）。超えたら Unreadable として捨てる。</summary>
    private const int HashRetryLimitCount = 5;

    /// <summary>デバウンスの長さ。</summary>
    public static TimeSpan Debounce { get; } = TimeSpan.FromMilliseconds(DebounceMs);

    /// <summary>自己書き込みの終了の知らせを待つ上限。</summary>
    public static TimeSpan SelfWriteMaxWait { get; } = TimeSpan.FromMilliseconds(SelfWriteMaxWaitMs);

    /// <summary>自己書き込みの終了後の余韻。</summary>
    public static TimeSpan SelfWriteTail { get; } = TimeSpan.FromMilliseconds(SelfWriteTailMs);

    /// <summary>ハッシュを読めなかったときの再試行の上限。</summary>
    public static int HashRetryLimit => HashRetryLimitCount;

    // ── 内部の型 ────────────────────────────────────────────────

    /// <summary>判定待ちの 1 件。</summary>
    private sealed class PendingEntry
    {
        /// <summary>最初に受け取った綴りのパス（結果として返す）。</summary>
        public required string Path { get; init; }

        /// <summary>判定してよい時刻（最後のイベント＋デバウンス）。</summary>
        public DateTime DueUtc { get; set; }

        /// <summary>ハッシュの読み取りに失敗した回数。</summary>
        public int Retries { get; set; }
    }

    /// <summary>1 つのパスの自己書き込みの窓。</summary>
    private sealed class SelfWriteWindow
    {
        /// <summary>開始の知らせを受け、終了の知らせをまだ受けていないか。</summary>
        public bool InFlight { get; set; }

        /// <summary>終了の知らせを待つ上限の時刻（InFlight の間だけ意味がある）。</summary>
        public DateTime DeadlineUtc { get; set; }

        /// <summary>この時刻までは（終了の後も）自分の書き込みとみなす。</summary>
        public DateTime TailUntilUtc { get; set; }
    }

    // ── 状態 ────────────────────────────────────────────────────

    /// <summary>ファイル内容のハッシュを読む（読めなければ null）。テストは差し替える。</summary>
    private readonly Func<string, string?> _readContentHash;

    /// <summary>判定待ち（最初に来た順）。</summary>
    private readonly List<PendingEntry> _pending = new();

    /// <summary>判定待ちの索引（鍵 → 項目）。</summary>
    private readonly Dictionary<string, PendingEntry> _pendingByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>パスごとの「知っている内容」のハッシュ。</summary>
    private readonly Dictionary<string, string> _knownHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>パスごとの自己書き込みの窓。</summary>
    private readonly Dictionary<string, SelfWriteWindow> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一括の書き換えの入れ子の深さ（0 より大きい間は全部 Suppressed）。</summary>
    private int _suppressionDepth;

    /// <summary>一括の書き換えが終わった後も Suppressed にする時刻（遅れて届くイベントのための余韻）。</summary>
    private DateTime _suppressionTailUntilUtc = DateTime.MinValue;

    /// <param name="readContentHash">ファイル内容のハッシュを読む関数（読めなければ null）。</param>
    public PrefabExternalChangeTracker(Func<string, string?> readContentHash)
    {
        _readContentHash = readContentHash;
    }

    // ── 公開 API ────────────────────────────────────────────────

    /// <summary>判定待ちの件数。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 次に <see cref="TakeDue"/> を呼ぶべき時刻（判定待ちが無ければ null）。タイマーの張り直しに使う。
    /// </summary>
    public DateTime? NextDueUtc
    {
        get
        {
            DateTime? next = null;
            foreach (var entry in _pending)
                if (next is null || entry.DueUtc < next) next = entry.DueUtc;
            return next;
        }
    }

    /// <summary>
    /// ファイル監視のイベントを受ける（判定待ちに積み、デバウンスを数え直す）。
    /// </summary>
    /// <param name="absolutePath">プレハブの絶対パス。</param>
    /// <param name="nowUtc">今の時刻。</param>
    public void OnFileEvent(string absolutePath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        var key = Key(absolutePath);
        if (_pendingByKey.TryGetValue(key, out var entry))
        {
            // 書き込みが続いている。静まるまで待ち直す（読み取りの失敗回数も新しいイベントで数え直す）
            entry.DueUtc  = nowUtc + Debounce;
            entry.Retries = 0;
            return;
        }
        entry = new PendingEntry { Path = absolutePath, DueUtc = nowUtc + Debounce };
        _pending.Add(entry);
        _pendingByKey[key] = entry;
    }

    /// <summary>
    /// デバウンスが満了した判定待ちを判定して取り出す（読めなかったものは再試行のために残す）。
    /// </summary>
    /// <param name="nowUtc">今の時刻。</param>
    /// <returns>判定した件（最初にイベントが来た順）。Changed のものだけを当て直す。</returns>
    public IReadOnlyList<PrefabChangeOutcome> TakeDue(DateTime nowUtc)
    {
        var outcomes = new List<PrefabChangeOutcome>();
        // 判定中に一覧を書き換えるので写しを回す
        foreach (var entry in _pending.ToArray())
        {
            if (entry.DueUtc > nowUtc) continue;

            var key  = Key(entry.Path);
            var hash = _readContentHash(entry.Path);
            if (hash is null)
            {
                // 書き込み中のロック・一時的に消えている（rename の途中）など。少し待って読み直す
                entry.Retries++;
                if (entry.Retries <= HashRetryLimit)
                {
                    entry.DueUtc = nowUtc + Debounce;
                    continue;
                }
                RemovePending(entry, key);
                outcomes.Add(new PrefabChangeOutcome(entry.Path, PrefabChangeVerdict.Unreadable));
                continue;
            }

            RemovePending(entry, key);
            outcomes.Add(new PrefabChangeOutcome(entry.Path, Classify(key, hash, nowUtc)));
        }
        PruneExpiredWindows(nowUtc);
        return outcomes;
    }

    /// <summary>
    /// エディタ自身がそのプレハブの書き込みを始めた（ランタイムへ SAVE_ACTOR / PREFAB_WRITE_BACK を送った）。
    /// 終了の知らせ（<see cref="NotifySelfWriteFinished"/>）＋余韻までの変更は自分の書き込みとみなす。
    /// </summary>
    /// <param name="absolutePath">書き込み先の絶対パス。</param>
    /// <param name="nowUtc">今の時刻。</param>
    public void NotifySelfWriteStarted(string absolutePath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        var window = WindowFor(Key(absolutePath));
        window.InFlight     = true;
        window.DeadlineUtc  = nowUtc + SelfWriteMaxWait;
        // 終了の知らせが来るまでは上限の時刻まで窓を開けておく
        if (window.TailUntilUtc < window.DeadlineUtc) window.TailUntilUtc = window.DeadlineUtc;
    }

    /// <summary>
    /// エディタ自身のそのプレハブの書き込みが終わった（SAVE_OK / SAVE_ERROR・PREFAB_WRITE_BACK_DONE / _ERROR・
    /// EXPORT_ACTOR_OK）。余韻を残し、その時点の内容を「知っている内容」として覚える
    /// （余韻が切れた後に遅れて届く同じ内容のイベントも Unchanged で落ちる）。
    /// 開始の知らせが無かった書き込み（EXPORT_ACTOR）でもそのまま使える。
    /// </summary>
    /// <param name="absolutePath">書き込み先の絶対パス。</param>
    /// <param name="nowUtc">今の時刻。</param>
    public void NotifySelfWriteFinished(string absolutePath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        var key    = Key(absolutePath);
        var window = WindowFor(key);
        window.InFlight     = false;
        window.TailUntilUtc = nowUtc + SelfWriteTail;

        var hash = _readContentHash(absolutePath);
        if (hash is not null) _knownHashes[key] = hash;
    }

    /// <summary>
    /// 一括の書き換え（形式の一括アップグレード）を始める。<see cref="EndSuppression"/> ＋余韻までの変更は
    /// 全部 Suppressed にする（入れ子にできる）。
    /// </summary>
    public void BeginSuppression() => _suppressionDepth++;

    /// <summary>
    /// 一括の書き換えを終える。いちばん外側を閉じたときから余韻（<see cref="SelfWriteTail"/>）の間も Suppressed にする。
    /// </summary>
    /// <param name="nowUtc">今の時刻。</param>
    public void EndSuppression(DateTime nowUtc)
    {
        if (_suppressionDepth == 0) return;
        _suppressionDepth--;
        if (_suppressionDepth == 0) _suppressionTailUntilUtc = nowUtc + SelfWriteTail;
    }

    // ── 内部処理 ────────────────────────────────────────────────

    /// <summary>
    /// 読めた内容のハッシュから判定する（規則の 3〜6。Unchanged 以外はハッシュを覚える）。
    /// </summary>
    private PrefabChangeVerdict Classify(string key, string hash, DateTime nowUtc)
    {
        // 3. 内容が変わっていない（touch・同じ書き込みの重複イベント・覚え済みの自分の書き込み）
        if (_knownHashes.TryGetValue(key, out var known) && string.Equals(known, hash, StringComparison.Ordinal))
            return PrefabChangeVerdict.Unchanged;

        _knownHashes[key] = hash;

        // 4. 一括の書き換えの最中・直後
        if (_suppressionDepth > 0 || nowUtc < _suppressionTailUntilUtc)
            return PrefabChangeVerdict.Suppressed;

        // 5. そのパスの自己書き込みの窓の中
        if (IsInSelfWriteWindow(key, nowUtc)) return PrefabChangeVerdict.SelfWrite;

        // 6. 外部の変更
        return PrefabChangeVerdict.Changed;
    }

    /// <summary>そのパスが自己書き込みの窓の中か（開始〜上限の時刻、または終了＋余韻）。</summary>
    private bool IsInSelfWriteWindow(string key, DateTime nowUtc)
    {
        if (!_selfWrites.TryGetValue(key, out var window)) return false;
        if (window.InFlight && nowUtc < window.DeadlineUtc) return true;
        return nowUtc < window.TailUntilUtc;
    }

    /// <summary>そのパスの窓を取り出す（無ければ作る）。</summary>
    private SelfWriteWindow WindowFor(string key)
    {
        if (!_selfWrites.TryGetValue(key, out var window))
        {
            window = new SelfWriteWindow();
            _selfWrites[key] = window;
        }
        return window;
    }

    /// <summary>期限の切れた窓を捨てる（表が増え続けないように）。</summary>
    private void PruneExpiredWindows(DateTime nowUtc)
    {
        List<string>? expired = null;
        foreach (var (key, window) in _selfWrites)
        {
            bool inFlightAlive = window.InFlight && nowUtc < window.DeadlineUtc;
            if (!inFlightAlive && nowUtc >= window.TailUntilUtc) (expired ??= new()).Add(key);
        }
        if (expired is null) return;
        foreach (var key in expired) _selfWrites.Remove(key);
    }

    /// <summary>判定待ちから取り除く。</summary>
    private void RemovePending(PendingEntry entry, string key)
    {
        _pending.Remove(entry);
        _pendingByKey.Remove(key);
    }

    /// <summary>同一視の鍵（区切りを '/' に揃える。大文字小文字は辞書側で無視する）。</summary>
    private static string Key(string path) => path.Trim().Replace('\\', '/');
}
