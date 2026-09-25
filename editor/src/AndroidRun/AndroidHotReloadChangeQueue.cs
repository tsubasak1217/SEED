// ============================================================
//  AndroidHotReloadChangeQueue.cs — 実行中の差し替えの「変わったファイル」をまとめる（デバウンス。純粋な処理。docs/android.md §23）
//
//  【規則】
//    - 変わったファイルは相対パスごとに 1 回だけ覚える（同じファイルを何度保存しても 1 件。大文字小文字を問わない）
//    - 最後の変更から「静かな時間」（QuietPeriod）が過ぎたら、覚えたものを 1 組として取り出せる（連続保存は最後の 1 回にまとめる）
//    - 取り出した組の差し替えが終わるまで（in flight）は次の組を出さない。その間に来た変更は覚えておき、
//      終わった後に静かな時間を待って次の組にする（取りこぼさず、同時に 2 つ走らせない）
//  時刻は引数で受ける（ファイル監視のスレッド・タイマーのスレッドから呼ばれるので、呼び出し側がロックして使う）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests が時刻を進めて確かめる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.AndroidRun;

/// <summary>変わったファイルのまとめ役（デバウンス）。スレッド安全ではない（呼び出し側がロックする）。</summary>
public sealed class AndroidHotReloadChangeQueue
{
    /// <summary>覚えている変わったファイル（最初に来た順。大文字小文字を問わない重複なし）。</summary>
    private readonly List<string> _pending = new();

    /// <summary>重複の判定用。</summary>
    private readonly HashSet<string> _pendingSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最後に変更を覚えた時刻（覚えていなければ null）。</summary>
    private DateTime? _lastChangeUtc;

    /// <summary>静かな時間を指定して作る。</summary>
    /// <param name="quietPeriod">最後の変更からこれだけ静かなら組にする。</param>
    public AndroidHotReloadChangeQueue(TimeSpan quietPeriod)
    {
        QuietPeriod = quietPeriod;
    }

    /// <summary>静かな時間。</summary>
    public TimeSpan QuietPeriod { get; }

    /// <summary>取り出した組の差し替えの途中か。</summary>
    public bool InFlight { get; private set; }

    /// <summary>覚えている変更があるか。</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// 変わったファイルを覚える（静かな時間を数え直す）。
    /// </summary>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <param name="nowUtc">今の時刻。</param>
    public void Add(string relative, DateTime nowUtc)
    {
        if (_pendingSet.Add(relative)) _pending.Add(relative);
        _lastChangeUtc = nowUtc;
    }

    /// <summary>
    /// 組を取り出してよいか（覚えている変更があり、最後の変更から静かな時間が過ぎ、差し替えの途中でない）。
    /// </summary>
    /// <param name="nowUtc">今の時刻。</param>
    /// <returns>取り出してよければ true。</returns>
    public bool IsDue(DateTime nowUtc) =>
        !InFlight && _pending.Count > 0 && _lastChangeUtc is { } last && nowUtc - last >= QuietPeriod;

    /// <summary>
    /// 覚えている変更を 1 組として取り出し、差し替えの途中にする（取り出せないときは空）。
    /// </summary>
    /// <param name="nowUtc">今の時刻。</param>
    /// <returns>変わったファイルの組（最初に来た順）。</returns>
    public IReadOnlyList<string> TakeBatch(DateTime nowUtc)
    {
        if (!IsDue(nowUtc)) return Array.Empty<string>();
        var batch = _pending.ToArray();
        _pending.Clear();
        _pendingSet.Clear();
        InFlight = true;
        return batch;
    }

    /// <summary>
    /// 取り出した組の差し替えが終わった（途中に来た変更は残り、静かな時間を待って次の組になる）。
    /// </summary>
    /// <param name="nowUtc">今の時刻（途中に来た変更があれば、ここから静かな時間を数える）。</param>
    public void Complete(DateTime nowUtc)
    {
        InFlight = false;
        if (_pending.Count > 0) _lastChangeUtc = nowUtc;
    }

    /// <summary>覚えている変更を捨てる（Android の実行が終わった）。途中の組は Complete で終える。</summary>
    public void Clear()
    {
        _pending.Clear();
        _pendingSet.Clear();
        _lastChangeUtc = null;
    }
}
