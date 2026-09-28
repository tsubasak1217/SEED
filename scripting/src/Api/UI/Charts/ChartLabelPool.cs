using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartLabelPool.cs — 目盛りの文字のノードの使い回し（W2-8。docs/ui_charts.md §2）
//
//  【役割】目盛りの文字（Text）は SEED.Draw では描けないので、文字 1 つ = プレハブ（templates/ui/prefabs/chart_label.actor）の
//  ノード 1 つ。グラフが見た目を作り直すたびに Begin → Place × n → End と呼ぶと、要る数だけノードを使い、余りは隠す
//  （消さずに次に使い回す）。書き込みは前の値から変わったものだけ（パンの間に文字の中身は変わらず位置だけ動く）。
//  【作る時の遅れ】GameObject.Instantiate の 2D アクターはフレームの末尾に作られ、翌フレームから CanvasTransform が引ける
//  （ListView の行と同じ）。足りない分は作って隠しておき、できたら使う（その間は Pending が true。呼び出し側は描き直しを頼む）。
// ============================================================

/// <summary>目盛りの文字のノードの使い回し。</summary>
public sealed class ChartLabelPool
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] chart:";

    /// <summary>1 つの文字のノードと、前に書いた値。</summary>
    private sealed class Slot
    {
        /// <summary>ノード。</summary>
        public readonly GameObject Node;
        /// <summary>前に書いた値（同じなら書かない）。</summary>
        public string Text = "\0";
        public Vector2 Position = new(float.NaN, float.NaN);
        public Vector2 Box = new(float.NaN, float.NaN);
        public string Align = "";
        public Color Color = new(-1f, -1f, -1f, -1f);
        public float FontSize = float.NaN;
        /// <summary>前に書いた書体（W2-9。null = まだ書いていない）。</summary>
        public string? FontFamily;
        /// <summary>前に書いた太さ（W2-9）。</summary>
        public float FontWeight = float.NaN;
        public int Layer = int.MinValue;
        public bool Visible = true;

        public Slot(GameObject node) { Node = node; }
    }

    /// <summary>文字を置く親のノード。</summary>
    private readonly GameObject _parent;

    /// <summary>文字の書体（テーマの font.family。グラフが ApplyLook で入れる。W2-9）。</summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>文字の太さ（テーマの font.weight。W2-9）。</summary>
    public float FontWeight { get; set; }
    /// <summary>文字のプレハブ（assets:// のパス）。</summary>
    private readonly string _prefab;
    /// <summary>使えるノード。</summary>
    private readonly List<Slot> _slots = new();
    /// <summary>作っている途中のノード（翌フレームから使える）。</summary>
    private readonly List<GameObject> _pending = new();
    /// <summary>今回の Begin から使った数。</summary>
    private int _used;
    /// <summary>作れないことを 1 度だけ知らせたか。</summary>
    private bool _warned;

    /// <summary>親とプレハブを決めて作る。</summary>
    public ChartLabelPool(GameObject parent, string prefab)
    {
        _parent = parent;
        _prefab = prefab;
    }

    /// <summary>作っている途中のノードがあるか（あれば呼び出し側は次のフレームも作り直す）。</summary>
    public bool Pending => _pending.Count > 0;

    /// <summary>今の数（作った数）。</summary>
    public int Count => _slots.Count;

    /// <summary>置き直しを始める（作り終わったノードを使える側へ移す）。</summary>
    public void Begin()
    {
        _used = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var node = _pending[i];
            if (!node.IsValid)
            {
                _pending.RemoveAt(i);
                continue;
            }
            if (!NavNode.IsBuilt(node)) continue;
            _pending.RemoveAt(i);
            _slots.Add(new Slot(node) { Visible = false });
        }
    }

    /// <summary>
    /// 文字を 1 つ置く（ノードが足りなければ作り、今回は置けない＝false）。位置は枠の左上（親の単位）。
    /// </summary>
    public bool Place(string text, Vector2 position, Vector2 box, string align, Color color, float fontSize, int layer)
    {
        if (_used >= _slots.Count)
        {
            Grow();
            _used++;
            return false;
        }
        var s = _slots[_used++];
        var node = s.Node;
        if (!node.IsValid) return false;
        if (!s.Visible)
        {
            node.Visible = true;
            s.Visible = true;
        }
        if (s.Position.x != position.x || s.Position.y != position.y)
        {
            if (node.GetComponent<CanvasTransform>() is { } ct) ct.Position = position;
            s.Position = position;
        }
        if (node.GetComponent<Text>() is not { } t) return true;
        if (s.Text != text) { t.Content = text; s.Text = text; }
        if (s.Box.x != box.x || s.Box.y != box.y) { t.BoxWidth = box.x; t.BoxHeight = box.y; s.Box = box; }
        if (s.Align != align) { t.Align = align; s.Align = align; }
        if (!s.Color.Equals(color)) { t.Color = color; s.Color = color; }
        if (s.FontSize != fontSize) { t.FontSize = fontSize; s.FontSize = fontSize; }
        if (s.FontFamily != FontFamily) { t.FontPath = FontFamily; s.FontFamily = FontFamily; }
        if (s.FontWeight != FontWeight) { t.Weight = FontWeight; s.FontWeight = FontWeight; }
        if (s.Layer != layer) { t.Layer = layer; s.Layer = layer; }
        return true;
    }

    /// <summary>置き直しを終える（使わなかったノードを隠す）。</summary>
    public void End()
    {
        for (int i = _used; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (s.Visible && s.Node.IsValid)
            {
                s.Node.Visible = false;
                s.Visible = false;
            }
        }
    }

    /// <summary>ノードを 1 つ作る（作っている途中の数を数え、要る分だけ作る）。</summary>
    private void Grow()
    {
        // 今回足りない数は（使った数 + 1）− 使える数。作っている途中の分は差し引く
        int missing = _used + 1 - _slots.Count;
        if (missing <= _pending.Count) return;
        var node = GameObject.Instantiate(_prefab, _parent);
        if (!node.IsValid)
        {
            if (!_warned) Debug.LogError($"{LogPrefix} 目盛りの文字のプレハブを作れません: {_prefab}");
            _warned = true;
            return;
        }
        // 作られるまで（と置かれるまで）はプレハブの位置に出ないよう隠す
        node.Visible = false;
        _pending.Add(node);
    }
}
