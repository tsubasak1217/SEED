using System;

namespace SEED.Binding;

// ============================================================
//  TwoWayBinding.cs — 双方向の結び付け（観測値 ⇔ 部品）
//
//  Bind.Toggle・Bind.Checkbox・Bind.Slider・Bind.TextField・Bind.Selection・Bind.TwoWay の本体。
//    - 作った時点で観測値の値を部品へ当てる（観測値が正）。当てる先の用意がまだなら待ち、用意ができたら知らせの口を付けて当てる。
//    - 値 → 部品: 観測値が変わったら部品へ書く。
//    - 部品 → 値: 利用者が部品を操作したら観測値へ入れる（観測値の他の購読・結び付けにも届く）。
//    - 往復を止める留め金:
//        _writingTarget … 部品へ書いている間に部品が知らせてきても（SelectionGroup.Select は必ず知らせる）観測値へ返さない
//        _writingSource … 部品の値を観測値へ入れている間の観測値の知らせを、部品へ書き戻さない
//      ただし入れている間に購読が値を直した（範囲に収める・拒否して戻す）ときは、留め金を外した後で観測値の今の値を部品へ書き戻す
//      （比べ方は観測値のもの。2026-10-03。2 回目のレビュー #25）。
//    - 部品が無くなったら（IsAlive が false）自分を外す。
// ============================================================

/// <summary>双方向の結び付け（観測値 ⇔ 部品）。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class TwoWayBinding<T> : BindingBase
{
    /// <summary>観測値。</summary>
    private readonly Observable<T> _source;

    /// <summary>部品（当てる先）。</summary>
    private readonly ITwoWayBindTarget<T> _target;

    /// <summary>部品の知らせの口を付けたか（用意ができてから 1 回だけ付ける）。</summary>
    private bool _listening;

    /// <summary>部品へ書いている途中か（部品の知らせを観測値へ返さない留め金）。</summary>
    private bool _writingTarget;

    /// <summary>部品の値を観測値へ入れている途中か（観測値の知らせを部品へ書き戻さない留め金）。</summary>
    private bool _writingSource;

    /// <summary>結び付けを作り、観測値の値を部品へ当てる（用意がまだなら待つ）。</summary>
    /// <param name="source">観測値。</param>
    /// <param name="target">部品。</param>
    internal TwoWayBinding(Observable<T> source, ITwoWayBindTarget<T> target)
    {
        _source = source;
        _target = target;
        Own(source.Subscribe(OnSourceChanged));
        Activate();
    }

    /// <summary>用意ができていれば知らせの口を付けて今の値を当てる。まだなら待つ。</summary>
    /// <returns>まだ待つなら true。</returns>
    /// <param name="inFrame">
    /// フレームの区切り（World が見える）からの呼び出しなら true。true のときだけ「当てる先が消えた」を確定して外す。
    /// それ以外（作ったとき・観測値の変化。OnDestroy の中のこともある）では見えなくても区切りで確かめ直す
    /// （docs/reviews/2026-10-03_code_review.md #7）。
    /// </param>
    private bool Activate(bool inFrame = false)
    {
        if (IsDisposed) return false;
        if (!_target.IsAlive)
        {
            if (!inFrame)
            {
                WaitForTarget();
                return true;
            }
            Dispose();
            return false;
        }
        if (!_target.IsReady)
        {
            WaitForTarget();
            return true;
        }

        // 部品の知らせの口は 1 回だけ付ける（結び付けを外すと一緒に外れる）
        if (!_listening)
        {
            _listening = true;
            Own(_target.Listen(OnTargetChanged));
        }
        WriteTarget(_source.Value);
        return false;
    }

    /// <summary>観測値が変わった: 部品へ書く（部品から来た値なら書き戻さない）。</summary>
    /// <param name="value">新しい値。</param>
    private void OnSourceChanged(T value)
    {
        // 留め金: 部品から来た値を部品へ返さない（往復を止める）
        if (IsDisposed || _writingSource) return;

        // 部品の用意ができる前の変化は、用意ができたときに最新の値で当てる
        if (!_listening)
        {
            Activate();
            return;
        }
        if (!_target.IsAlive)
        {
            // 見えないだけかもしれない（OnDestroy の中）: 区切りで確かめ直す
            WaitForTarget();
            return;
        }

        // 一度用意ができた後に当てられなくなった（選択の項目が 0 個になった等）: 区切りで確かめて最新の値を当てる
        if (!_target.IsReady)
        {
            WaitForTarget();
            return;
        }
        WriteTarget(value);
    }

    /// <summary>
    /// 部品の値が変わった（利用者の操作）: 観測値へ入れる（自分が書いた知らせなら無視）。入れている間（留め金）に購読が値を直した
    /// （範囲に収める・拒否して戻す）なら、留め金を外した後で直した値を部品へ書き戻す。
    /// </summary>
    /// <param name="value">部品の新しい値。</param>
    private void OnTargetChanged(T value)
    {
        // 留め金: 自分が部品へ書いたことによる知らせは観測値へ返さない
        if (IsDisposed || _writingTarget) return;
        _writingSource = true;
        try
        {
            _source.Value = value;
        }
        finally
        {
            _writingSource = false;
        }

        // 購読の直しを部品へ返す（2026-10-03。2 回目のレビュー #25）: 留め金の間に購読が値を直すと、その知らせ（再入の次の周）は
        // OnSourceChanged が留め金で捨てるので、部品は利用者が操作した値の見た目のまま残っていた（トグルは ON・観測値は false）。
        // 観測値の今の値が部品の値と違えば（比べ方は観測値のもの。等しいとみなす値なら利用者の打った文字を書き換えない）、
        // 観測値から来た変化と同じ道（消えた・用意がまだなら待つ）で部品へ書く。購読の中で結び付けを外したら何もしない。
        if (IsDisposed) return;
        var current = _source.Value;
        if (!_source.Comparer.Equals(current, value)) OnSourceChanged(current);
    }

    /// <summary>部品へ書く（書いている間は部品の知らせを無視する）。</summary>
    /// <param name="value">書く値。</param>
    private void WriteTarget(T value)
    {
        _writingTarget = true;
        try
        {
            _target.Write(value);
        }
        finally
        {
            _writingTarget = false;
        }
    }

    /// <inheritdoc />
    protected override bool OnFrame() => Activate(inFrame: true);
}
