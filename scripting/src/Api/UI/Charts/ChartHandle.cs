using System;

namespace SEED.UI;

// ============================================================
//  ChartHandle.cs — 日付線のハンドルのノードの世話（W2 の手直し P2-4。docs/ui_charts.md §2・§6.1）
//
//  【プレハブ】グラフの子 Handle（任意。無いプレハブでは何もしない＝今までどおり）:
//      Handle（Sprite〈楕円〉・CanvasGesture〈drag = true・drag_axis = horizontal・fling / tap / long_press / pinch / press_feedback = false・
//             min_hit_size_dp 48〉・SEED.UI.GestureRelay）。pivot・anchor は 0（位置 = 左上。ほかの子と同じ）。はじめは非表示。
//  【役割】（ノードの読み書きだけ。いつ出すか・どこへ置くか・指をどう選びに換えるかはグラフ〈ChartView〉が決める）
//    - 見せる・隠す・位置・直径・色・縁・レイヤーを、前に書いた値から変わったときだけ FFI で書く（吹き出しと同じ流儀）
//    - ハンドルの GestureRelay（子のノードのドラッグを持ち主へ渡す）のドラッグのイベントを、グラフへそのまま渡す
//      （GestureRelay の OnStart は順が決まっていないので、登録簿が変わるたびに TryBind し直す）
//  【位置の覚え】Position は最後に書いた左上（グラフのノードの単位）。ジェスチャーのイベントはスクリプトのフェーズより前に配られ、
//  エンジンはその時点の World（＝前のフレームに書いた位置）でハンドルの LocalPosition を求めるので、ハンドラの中で読む Position は
//  イベントの座標の原点と一致する（このクラスはハンドラの中で位置を書かない）。
// ============================================================

/// <summary>日付線のハンドル（グラフの子 Handle）のノードの読み書きとドラッグの受け渡し。</summary>
internal sealed class ChartHandle
{
    /// <summary>子の名前（プレハブ）。</summary>
    public const string ChildName = "Handle";

    /// <summary>ハンドルのノード。</summary>
    private readonly GameObject _node;
    /// <summary>持ち主へ渡す面（登録されるまで null）。</summary>
    private GestureRelay? _relay;
    /// <summary>見せているか（null = まだ書いていない。プレハブの初めの値に依らず最初に必ず書く）。</summary>
    private bool? _shown;
    /// <summary>最後に書いた左上（グラフのノードの単位。まだなら非数）。</summary>
    private Vector2 _position = new(float.NaN, float.NaN);
    /// <summary>最後に書いた直径・縁の太さ（まだなら非数）。</summary>
    private float _diameter = float.NaN, _border = float.NaN;
    /// <summary>最後に書いた塗り・縁の色（null = まだ）。</summary>
    private Color? _fill, _borderColor;
    /// <summary>最後に書いたレイヤー（null = まだ）。</summary>
    private int? _layer;

    /// <summary>ドラッグが始まった（ハンドルの上で押して横へ slop を超えた）。</summary>
    public event Action<GestureEvent>? DragStarted;
    /// <summary>ドラッグの途中（1 フレームに 1 件まで）。</summary>
    public event Action<GestureEvent>? DragUpdated;
    /// <summary>ドラッグが終わった（離した・取り消し。取り消しは <see cref="GestureEvent.Canceled"/>）。</summary>
    public event Action<GestureEvent>? DragEnded;

    private ChartHandle(GameObject node) { _node = node; }

    /// <summary>グラフの子 Handle を引く（無ければ null）。</summary>
    public static ChartHandle? Find(GameObject chart)
    {
        var node = chart.FindChild(ChildName);
        return node.IsValid ? new ChartHandle(node) : null;
    }

    /// <summary>見せているか（最後に見せると書いた）。</summary>
    public bool IsShown => _shown == true;

    /// <summary>最後に書いた左上（グラフのノードの単位）。</summary>
    public Vector2 Position => _position;

    /// <summary>GestureRelay とつないだか。</summary>
    public bool IsBound => _relay is not null;

    /// <summary>ハンドルの GestureRelay をつなぐ（まだ登録されていなければ何もしない。つないだら true）。</summary>
    public bool TryBind()
    {
        if (_relay is not null) return true;
        if (UiWidget.Of<GestureRelay>(_node) is not { } relay) return false;
        _relay = relay;
        relay.DragStarted += (_, e) => DragStarted?.Invoke(e);
        relay.DragUpdated += (_, e) => DragUpdated?.Invoke(e);
        relay.DragEnded += (_, e) => DragEnded?.Invoke(e);
        return true;
    }

    /// <summary>隠す（見せていれば）。</summary>
    public void Hide()
    {
        if (_shown == false) return;
        _node.Visible = false;
        _shown = false;
    }

    /// <summary>
    /// 見せて、位置・直径・色・縁・レイヤーを当てる（前に書いた値と同じものは書かない）。
    /// </summary>
    /// <param name="topLeft">左上（グラフのノードの単位）。</param>
    /// <param name="diameter">直径。</param>
    /// <param name="fill">塗り（選んだ系列の色）。</param>
    /// <param name="borderColor">縁の色。</param>
    /// <param name="border">縁の太さ（0 = 縁なし）。</param>
    /// <param name="layer">レイヤー（吹き出しと同じ手前）。</param>
    public void Show(Vector2 topLeft, float diameter, Color fill, Color borderColor, float border, int layer)
    {
        if (_shown != true)
        {
            _node.Visible = true;
            _shown = true;
        }
        if (topLeft != _position && _node.GetComponent<CanvasTransform>() is { } ct)
        {
            ct.Position = topLeft;
            _position = topLeft;
        }
        // 見た目の値が 1 つも変わっていなければ Sprite を引かない（毎フレーム呼ばれるので FFI を増やさない）
        bool lookChanged = diameter != _diameter || _fill != fill || _borderColor != borderColor || border != _border || _layer != layer;
        if (!lookChanged || _node.GetComponent<Sprite>() is not { } sprite) return;
        if (diameter != _diameter)
        {
            sprite.Size = new Vector2(diameter, diameter);
            _diameter = diameter;
        }
        if (_fill != fill)
        {
            sprite.Color = fill;
            _fill = fill;
        }
        if (_borderColor != borderColor)
        {
            sprite.BorderColor = borderColor;
            _borderColor = borderColor;
        }
        if (border != _border)
        {
            sprite.BorderWidth = border;
            _border = border;
        }
        if (_layer != layer)
        {
            sprite.Layer = layer;
            _layer = layer;
        }
    }

    /// <summary>テーマが替わった: 見た目の値（直径・色・縁・レイヤー）を次の <see cref="Show"/> で書き直させる（位置と見せ方は保つ）。</summary>
    public void InvalidateLook()
    {
        _diameter = _border = float.NaN;
        _fill = _borderColor = null;
        _layer = null;
    }
}
