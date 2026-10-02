using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ModalOpeningBook.cs — 作りかけの面の帳面（面のプレハブを作ったが、面のスクリプトがまだ開く約束を受け取っていない面）
//                        （2026-10-03。lane3。2 回目のレビュー #20・#31。純粋な計算。docs/ui_navigation.md §3.1）
//
//  ModalHost が面のプレハブを作ったら Add、面のスクリプト（ModalPlane）の OnStart で TryClaim（開く約束を受け取る）。
//  - 種類ごとの作りかけの数（CountOf）は帳面の中身から数える。別の数を持たないので、減らし忘れ（受け取られない面）・
//    二重に減らす（CloseAll の再入）が起きない。
//  - 受け取りの上限（MaxClaimWaitFrames）: 作ってからこのフレーム数のうちに受け取られなかった面（面のプレハブが無い・誤ったパスで
//    読み込みに失敗した・根に ModalPlane の派生が無い）は Tick が帳面から外して返す（ModalHost がエラーを出し、根を消して手札を閉じる）。
//    以前は作りかけの数が減らず、その種類の戻るを永久に飲み込み、手札の WhenClosed も終わらなかった（レビュー #20）。
//  - 取りやめた面（CloseAll・上限）の鍵は覚えておき、面のスクリプトが後で始まっても黙って消えるようにする（TakeCancelled は 1 回だけ答える）。
//    始まらないまま上限のフレーム数が過ぎた鍵は忘れる（根を消した面のスクリプトは始まらないので、覚えた鍵が溜まらない）。
//  - CancelAll は対象を帳面から先に全部外してから返す（呼び手が手札を閉じる知らせの中で CloseAll を呼び直しても、同じ面を二度取りやめない。
//    知らせの中で新しく開いた面は、新しい作りかけとして残る。レビュー #31）。
// ============================================================

/// <summary>作りかけの面 1 つ（鍵・開いた種類・開く約束）。</summary>
/// <typeparam name="TKey">面の根の鍵（ModalHost ではエンティティの番号と世代）。</typeparam>
/// <typeparam name="TClaim">開く約束（手札・指定・面の根など）。</typeparam>
/// <param name="Key">面の根の鍵。</param>
/// <param name="Kind">開いた種類。</param>
/// <param name="Claim">開く約束。</param>
public readonly record struct ModalOpening<TKey, TClaim>(TKey Key, ModalKind Kind, TClaim Claim);

/// <summary>作りかけの面の帳面。</summary>
/// <typeparam name="TKey">面の根の鍵。</typeparam>
/// <typeparam name="TClaim">開く約束。</typeparam>
public sealed class ModalOpeningBook<TKey, TClaim> where TKey : notnull
{
    /// <summary>
    /// 作ってから面のスクリプトが開く約束を受け取るまで待つフレームの上限（このフレーム数の Tick で受け取られなければ取りやめる）。
    /// ふだんは作ったフレームの末尾にできあがり、次のフレームの OnStart で受け取る（1〜2 フレーム）。取りやめた鍵を覚えておくフレーム数にも使う。
    /// </summary>
    public const int MaxClaimWaitFrames = 60;

    /// <summary>作りかけ 1 つ（作ってから数えたフレームつき）。</summary>
    private sealed class Pending
    {
        /// <summary>面の根の鍵。</summary>
        public required TKey Key { get; init; }
        /// <summary>開いた種類。</summary>
        public required ModalKind Kind { get; init; }
        /// <summary>開く約束。</summary>
        public required TClaim Claim { get; init; }
        /// <summary>作ってから数えたフレーム（Tick ごとに 1）。</summary>
        public int Frames;
    }

    /// <summary>取りやめた鍵 1 つ（覚えてから数えたフレームつき）。</summary>
    private sealed class Cancelled
    {
        /// <summary>面の根の鍵。</summary>
        public required TKey Key { get; init; }
        /// <summary>覚えてから数えたフレーム（Tick ごとに 1）。</summary>
        public int Frames;
    }

    /// <summary>鍵の比べ方。</summary>
    private static readonly IEqualityComparer<TKey> KeyComparer = EqualityComparer<TKey>.Default;

    /// <summary>何も無いときに Tick・CancelAll が返す空の並び（毎フレームの割り当てをしない）。</summary>
    private static readonly IReadOnlyList<ModalOpening<TKey, TClaim>> None = Array.Empty<ModalOpening<TKey, TClaim>>();

    /// <summary>作りかけ（開いた順）。数はふつう 0〜2 なので並びのまま探す。</summary>
    private readonly List<Pending> _pending = new();

    /// <summary>取りやめた鍵（面のスクリプトが後で始まったら黙って消す）。</summary>
    private readonly List<Cancelled> _cancelled = new();

    /// <summary>作りかけの数（全部の種類）。</summary>
    public int Count => _pending.Count;

    /// <summary>覚えている取りやめた鍵の数（診断・テスト用）。</summary>
    public int CancelledCount => _cancelled.Count;

    /// <summary>Tick で数えるもの（作りかけ・取りやめた鍵）があるか（無ければ毎フレームの Tick を省いてよい）。</summary>
    public bool HasWork => _pending.Count > 0 || _cancelled.Count > 0;

    /// <summary>種類の作りかけの数。</summary>
    /// <param name="kind">開いた種類。</param>
    /// <returns>作りかけの数。</returns>
    public int CountOf(ModalKind kind)
    {
        int count = 0;
        foreach (var pending in _pending)
            if (pending.Kind == kind) count++;
        return count;
    }

    /// <summary>面を作った（受け取りを待つ）。同じ鍵が残っていれば置き換える（エンティティの鍵は世代つきなので、ふつうは重ならない）。</summary>
    /// <param name="key">面の根の鍵。</param>
    /// <param name="kind">開いた種類。</param>
    /// <param name="claim">開く約束。</param>
    public void Add(TKey key, ModalKind kind, TClaim claim)
    {
        RemovePending(key);
        RemoveCancelled(key);
        _pending.Add(new Pending { Key = key, Kind = kind, Claim = claim });
    }

    /// <summary>面のスクリプトが開く約束を受け取る（帳面から外す）。</summary>
    /// <param name="key">面の根の鍵。</param>
    /// <param name="claim">開く約束（無ければ既定値）。</param>
    /// <returns>約束があれば true。</returns>
    public bool TryClaim(TKey key, out TClaim claim)
    {
        int index = IndexOfPending(key);
        if (index < 0)
        {
            claim = default!;
            return false;
        }
        claim = _pending[index].Claim;
        _pending.RemoveAt(index);
        return true;
    }

    /// <summary>取りやめた面か（1 回だけ答える。面のスクリプトが始まったときに黙って消すかを決める）。</summary>
    /// <param name="key">面の根の鍵。</param>
    /// <returns>取りやめた面なら true。</returns>
    public bool TakeCancelled(TKey key) => RemoveCancelled(key);

    /// <summary>
    /// 作りかけを全部（種類を絞るなら only だけ）取りやめる。閉じる順（ダイアログ → シート → 覆い、同じ種類の中は後から開いた面から。
    /// <see cref="ModalCloseOrder"/>）に並べ、帳面から先に全部外してから返す（返した後の知らせの中の再入で同じ面を二度扱わない）。
    /// </summary>
    /// <param name="only">この種類だけ（null = 全部の種類）。</param>
    /// <returns>取りやめた作りかけ（閉じる順）。</returns>
    public IReadOnlyList<ModalOpening<TKey, TClaim>> CancelAll(ModalKind? only)
    {
        if (_pending.Count == 0) return None;
        var order = ModalCloseOrder.Sequence<Pending>(kind => _pending.FindAll(p => p.Kind == kind), only);
        if (order.Count == 0) return None;
        var cancelled = new List<ModalOpening<TKey, TClaim>>(order.Count);
        foreach (var pending in order)
        {
            _pending.Remove(pending);
            MarkCancelled(pending.Key);
            cancelled.Add(new ModalOpening<TKey, TClaim>(pending.Key, pending.Kind, pending.Claim));
        }
        return cancelled;
    }

    /// <summary>
    /// 1 フレーム進める（ModalHost の毎フレーム。<see cref="HasWork"/> の間だけでよい）: 取りやめた鍵は上限のフレーム数で忘れ、
    /// 作りかけは上限のフレーム数に達したら帳面から外して返す（取りやめた鍵として覚える。開いた順）。
    /// </summary>
    /// <returns>上限に達して取りやめた作りかけ（無ければ空）。</returns>
    public IReadOnlyList<ModalOpening<TKey, TClaim>> Tick()
    {
        // 取りやめた鍵: 上限を過ぎても面のスクリプトが始まらなければ、もう始まらない（根は消してある）ので忘れる
        for (int i = _cancelled.Count - 1; i >= 0; i--)
            if (++_cancelled[i].Frames >= MaxClaimWaitFrames) _cancelled.RemoveAt(i);

        // 作りかけ: 上限に達したら取りやめる（作りかけの数から外れ、その種類の戻るを飲み込まなくなる）
        List<ModalOpening<TKey, TClaim>>? expired = null;
        for (int i = 0; i < _pending.Count;)
        {
            var pending = _pending[i];
            if (++pending.Frames < MaxClaimWaitFrames)
            {
                i++;
                continue;
            }
            _pending.RemoveAt(i);
            MarkCancelled(pending.Key);
            (expired ??= new List<ModalOpening<TKey, TClaim>>()).Add(new ModalOpening<TKey, TClaim>(pending.Key, pending.Kind, pending.Claim));
        }
        return expired ?? None;
    }

    /// <summary>取りやめた鍵として覚える（数え直す）。</summary>
    private void MarkCancelled(TKey key)
    {
        RemoveCancelled(key);
        _cancelled.Add(new Cancelled { Key = key });
    }

    /// <summary>作りかけの添字（無ければ −1）。</summary>
    private int IndexOfPending(TKey key)
    {
        for (int i = 0; i < _pending.Count; i++)
            if (KeyComparer.Equals(_pending[i].Key, key)) return i;
        return -1;
    }

    /// <summary>作りかけを外す（無ければ何もしない）。</summary>
    private void RemovePending(TKey key)
    {
        int index = IndexOfPending(key);
        if (index >= 0) _pending.RemoveAt(index);
    }

    /// <summary>取りやめた鍵を外す。</summary>
    /// <returns>覚えていたら true。</returns>
    private bool RemoveCancelled(TKey key)
    {
        for (int i = 0; i < _cancelled.Count; i++)
        {
            if (!KeyComparer.Equals(_cancelled[i].Key, key)) continue;
            _cancelled.RemoveAt(i);
            return true;
        }
        return false;
    }
}
