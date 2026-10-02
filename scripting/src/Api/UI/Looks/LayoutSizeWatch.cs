using System;

namespace SEED.UI;

// ============================================================
//  LayoutSizeWatch.cs — レイアウトの大きさの変化を見張る（2026-10-02。純粋な計算）
//
//  部品が「自分のレイアウトの大きさ（CanvasTransform.LayoutSize。前のフレームの描画の値）が変わったら置き直す」ために使う
//  （Slider の溝・Button の文字の枠・TextField の中身・Dialog の札の高さの上限）。浮動小数の丸めの差（Epsilon 以下）では
//  変わったとみなさない（止まっている画面で毎フレーム書き直さない）。最初の 1 回は必ず「変わった」。
// ============================================================

/// <summary>レイアウトの大きさの変化の見張り。</summary>
public struct LayoutSizeWatch
{
    /// <summary>変わったとみなす差の既定（キャンバスの単位）。</summary>
    public const float DefaultEpsilon = 0.01f;

    /// <summary>最後に受け入れた大きさ。</summary>
    private Vector2 _last;
    /// <summary>受け入れた大きさがあるか。</summary>
    private bool _has;

    /// <summary>最後に受け入れた大きさ（まだなら null）。</summary>
    public readonly Vector2? Last => _has ? _last : null;

    /// <summary>
    /// 今の大きさを見る。最初の 1 回か、前に受け入れた大きさから <paramref name="epsilon"/> を超えて変わったら受け入れて true。
    /// 有限でない成分を含む大きさは受け入れない（false）。
    /// </summary>
    /// <param name="size">今の大きさ。</param>
    /// <param name="epsilon">許す差。</param>
    public bool Update(Vector2 size, float epsilon = DefaultEpsilon)
    {
        if (!float.IsFinite(size.x) || !float.IsFinite(size.y)) return false;
        if (_has && !Changed(_last, size, epsilon)) return false;
        _last = size;
        _has = true;
        return true;
    }

    /// <summary>忘れる（次の Update は必ず変わったとみなす）。</summary>
    public void Reset() => _has = false;

    /// <summary>2 つの大きさがどちらかの軸で <paramref name="epsilon"/> を超えて違うか。</summary>
    /// <param name="a">前の大きさ。</param>
    /// <param name="b">今の大きさ。</param>
    /// <param name="epsilon">許す差。</param>
    public static bool Changed(Vector2 a, Vector2 b, float epsilon)
        => MathF.Abs(a.x - b.x) > epsilon || MathF.Abs(a.y - b.y) > epsilon;
}
