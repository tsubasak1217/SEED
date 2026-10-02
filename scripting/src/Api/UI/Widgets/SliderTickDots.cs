using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  SliderTickDots.cs — スライダの刻みの点のノード（作る・置く・色を当てる。2026-10-03。docs/ui_components.md §6）
//
//  【置き場】Slider の子 Ticks（templates/ui/prefabs/slider.actor の Fill と Thumb の間。部品なしの Actor2D・位置 0）の下に、
//  点のプレハブ（既定 templates/ui/prefabs/slider_tick.actor = 楕円の Sprite 1 つ）から要る数だけ作る。
//  Ticks を Fill と Thumb の間に置くのは描く順のため（同じレイヤーのスプライトは木の深さ優先の順に描く）: 点は塗りの上・つまみの下。
//  【古いプレハブ】Ticks の無い slider.actor（2026-10-03 より前の写し。Wake or Pay の assets/ui など）では点を描かない
//  （刻みの数を指定したときに 1 度だけ警告）。点のプレハブが無いときも描かない（1 度だけ警告）。既定の刻みの数 0 では何も作らない。
//  【作り方】作った点は構築されるまで（フレームの末尾）CanvasTransform が無いので、次のフレームで置く（持ち主が HasPending を見て当て直す）。
//  点のノードは消さずに残し、数が減ったら隠す（数を増やしたときに使い回す）。書き込みは前の値と比べて変わったものだけ。
//  数・置き場・色の決め方は SliderTicks（純粋な計算）。
// ============================================================

/// <summary>スライダの刻みの点のノード（作る・置く・色）。</summary>
internal sealed class SliderTickDots
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.UI] Slider";

    /// <summary>
    /// 点の構築を待つ間に当て直しを頼むフレームの上限（60 fps で 1 秒）。プレハブが壊れていて構築されないときに、
    /// 描き続け（render_policy: on_demand の電池）を止められなくなることを防ぐ。
    /// </summary>
    private const int MaxWaitFrames = 60;

    /// <summary>点 1 つのノードと、最後に書いた値（同じ値を書かない）。</summary>
    private sealed class Dot
    {
        /// <summary>点のノード。</summary>
        public readonly GameObject Node;
        /// <summary>最後に書いた表示（null = まだ）。</summary>
        public bool? Visible;
        /// <summary>最後に書いた左上の置き場。</summary>
        public Vector2 Position = new(float.NaN, float.NaN);
        /// <summary>最後に書いた直径。</summary>
        public float Size = float.NaN;
        /// <summary>最後に書いた色。</summary>
        public Color Color = new(float.NaN, float.NaN, float.NaN, float.NaN);

        public Dot(GameObject node) { Node = node; }
    }

    /// <summary>作った点（構築を待っているものを含む）。</summary>
    private readonly List<Dot> _dots = new();
    /// <summary>構築を待って当て直しを頼んだフレームの数。</summary>
    private int _waitFrames;
    /// <summary>Ticks が無いことを知らせたか（1 度だけ）。</summary>
    private bool _warnedContainer;
    /// <summary>点を作れないことを知らせたか（1 度だけ）。</summary>
    private bool _warnedPrefab;
    /// <summary>作れなかった点のプレハブ（同じプレハブでは作り直さない＝失敗の警告を毎回出させない。null = 失敗なし）。</summary>
    private string? _failedPrefab;

    /// <summary>構築を待っている点があり、次のフレームで当て直す必要があるか（待つ上限を超えたら false）。</summary>
    public bool HasPending { get; private set; }

    /// <summary>作った点のノードの数（0 なら刻みの数 0 の部品は何もしなくてよい）。</summary>
    public int CreatedCount => _dots.Count;

    /// <summary>
    /// 点を要る数だけ作り、置いて色を当てる（要らない点は隠す）。
    /// </summary>
    /// <param name="container">点を置く親（Slider の子 Ticks。無効なら描かない）。</param>
    /// <param name="prefab">点のプレハブ（assets:// の .actor）。</param>
    /// <param name="count">描く点の数（SliderTicks.DotCount）。</param>
    /// <param name="size">点の直径。</param>
    /// <param name="positionOf">点の番号 → 左上の置き場（部品の左上から。Ticks は位置 0）。</param>
    /// <param name="colorOf">点の番号 → 色。</param>
    /// <param name="ownerName">持ち主の名前（警告に出す）。</param>
    public void Apply(GameObject container, string prefab, int count, float size, Func<int, Vector2> positionOf,
        Func<int, Color> colorOf, string ownerName)
    {
        HasPending = false;
        if (count > 0 && !container.IsValid)
        {
            // 古いプレハブ（Ticks が無い）: 点は描かない（取り込み直しを促す）
            if (!_warnedContainer)
            {
                _warnedContainer = true;
                Debug.LogWarning($"{LogPrefix} '{ownerName}': 刻みの点を置く子 Ticks がありません（templates/ui の slider.actor を取り込み直すと描けます）");
            }
            return;
        }
        EnsureDots(container, prefab, count, ownerName);
        bool pending = false;
        for (int i = 0; i < _dots.Count; i++)
        {
            var dot = _dots[i];
            if (!dot.Node.IsValid) continue;
            // 構築を待っている点（作ったフレームはまだ CanvasTransform が無い）: 次のフレームで置く
            if (dot.Node.GetComponent<CanvasTransform>() is not { } ct)
            {
                pending |= i < count;
                continue;
            }
            bool shown = i < count;
            if (dot.Visible != shown)
            {
                dot.Node.Visible = shown;
                dot.Visible = shown;
            }
            if (!shown) continue;
            ApplyDot(dot, ct, size, positionOf(i), colorOf(i));
        }
        // 構築を待つ点があれば次のフレームで当て直す（上限まで。超えたら知らせて止める）
        if (pending && _waitFrames < MaxWaitFrames)
        {
            _waitFrames++;
            HasPending = true;
        }
        else if (!pending)
        {
            _waitFrames = 0;
        }
        else if (!_warnedPrefab)
        {
            _warnedPrefab = true;
            Debug.LogWarning($"{LogPrefix} '{ownerName}': 刻みの点ができあがりません（点のプレハブ: {prefab}）");
        }
    }

    /// <summary>足りない点を作る（作った点は構築されるまで隠す。作れなければ 1 度だけ知らせてやめる）。</summary>
    private void EnsureDots(GameObject container, string prefab, int count, string ownerName)
    {
        // 前に作れなかったプレハブは作り直さない（エンジンの生成の警告を値の変化のたびに出させない）
        if (_dots.Count >= count || prefab == _failedPrefab) return;
        while (_dots.Count < count)
        {
            var node = GameObject.Instantiate(prefab, container);
            if (!node.IsValid)
            {
                _failedPrefab = prefab;
                if (!_warnedPrefab)
                {
                    _warnedPrefab = true;
                    Debug.LogWarning($"{LogPrefix} '{ownerName}': 刻みの点を作れません（点のプレハブ: {prefab}）");
                }
                return;
            }
            // 構築されて置くまでは隠しておく（プレハブの位置に一瞬出ないように）
            node.Visible = false;
            _dots.Add(new Dot(node) { Visible = false });
        }
    }

    /// <summary>点 1 つへ形・大きさ・色・置き場を書く（前に書いた値と同じなら書かない）。</summary>
    private static void ApplyDot(Dot dot, CanvasTransform ct, float size, Vector2 position, Color color)
    {
        if (dot.Node.GetComponent<Sprite>() is { } sprite)
        {
            if (dot.Size != size)
            {
                sprite.Shape = SpriteShapeKind.Ellipse;
                sprite.Size = new Vector2(size, size);
                dot.Size = size;
            }
            if (!SameColor(dot.Color, color))
            {
                sprite.Color = color;
                dot.Color = color;
            }
        }
        if (dot.Position.x != position.x || dot.Position.y != position.y)
        {
            ct.Position = position;
            dot.Position = position;
        }
    }

    /// <summary>2 つの色が同じか（前の値が NaN なら違う）。</summary>
    private static bool SameColor(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
}
