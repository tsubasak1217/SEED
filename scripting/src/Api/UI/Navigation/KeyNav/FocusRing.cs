using System;

namespace SEED.UI;

// ============================================================
//  FocusRing.cs — 方向キー・パッドのフォーカスの枠（focus ring）のノード（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  【作り】枠のプレハブ（既定 templates/ui/prefabs/focus_ring.actor = 縁だけの角丸の Sprite と CanvasLayoutItem〈ignore_layout〉）を
//  UiNavigator のノードの子として 1 つだけ作る（部品の木には入れない）。フォーカスした部品の矩形（画面の画素）に重ねる。
//  【置き場】CanvasLayoutItem の実行中だけの欄（保存しない。docs/ui_navigation.md §6）で書く:
//    - Translate（キャンバスの単位）= 枠の左上 − 平行移動 0 のときの枠の左上
//    - LayerBias = layer.focus_ring − 祖先の底上げ（いちばん手前。トーストの帯より上）
//    大きさ・角丸・線は Sprite（作ったノードは保存されない）。
//  【写し方】画面の画素 → 親の単位は、親（UiNavigator のノード）のレイアウトの矩形から求める（FocusRingMath）。さらに枠自身の
//  前のフレームの矩形から「平行移動 0 のときの左上」と「1 単位の画素数」を測り直す（親の anchor・pivot・キャンバスの原点の違いに
//  左右されない。最初の 1 回だけ親から推した値を使う）。
//  【見た目】線 = color.focus_ring・size.focus_ring_width、部品との間 = size.focus_ring_gap、角丸 = 部品の角丸 + 間 + 太さ
//  （部品が角丸でなければ radius.focus_ring）。書き込みは前の値と変わったときだけ（変われば Redraw.Request）。
//  プレハブが無い・作れないときは枠を出さない（1 度だけ警告）。作ったノードは構築されるまで（フレームの末尾）待つ。
// ============================================================

/// <summary>フォーカスの枠のノード（1 つ）。</summary>
internal sealed class FocusRing
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] nav:";
    /// <summary>位置・大きさが変わったとみなす差（キャンバスの単位）。</summary>
    private const float Epsilon = 0.01f;

    /// <summary>枠のノード（まだ作っていなければ無効）。</summary>
    private GameObject _node;
    /// <summary>作ったプレハブ（替わったら作り直す）。</summary>
    private string _prefab = string.Empty;
    /// <summary>作れなかったプレハブ（同じプレハブでは作り直さない）。</summary>
    private string? _failedPrefab;
    /// <summary>作れないことを知らせたか。</summary>
    private bool _warned;

    /// <summary>最後に書いた平行移動（単位）。</summary>
    private Vector2 _translate = new(float.NaN, float.NaN);
    /// <summary>最後に書いた大きさ（単位）。</summary>
    private Vector2 _size = new(float.NaN, float.NaN);
    /// <summary>最後に書いた角丸・太さ。</summary>
    private float _radius = float.NaN, _width = float.NaN;
    /// <summary>最後に書いた線の色。</summary>
    private Color _color = new(float.NaN, float.NaN, float.NaN, float.NaN);
    /// <summary>最後に書いた底上げ。</summary>
    private int _bias = int.MinValue;
    /// <summary>最後に書いた表示。</summary>
    private bool? _visible;

    /// <summary>枠を見せているか。</summary>
    public bool IsShown => _visible == true;

    /// <summary>
    /// 毎フレーム: 相手の矩形に枠を重ねる（見せないなら隠す）。
    /// </summary>
    /// <param name="parent">枠の親（UiNavigator のノード）。</param>
    /// <param name="prefab">枠のプレハブ（空なら出さない）。</param>
    /// <param name="target">フォーカスの相手（null なら隠す）。</param>
    /// <param name="show">枠を見せるか（方向キーで操作している間）。</param>
    /// <param name="theme">今のテーマ。</param>
    public void Update(GameObject parent, string prefab, IUiNavigable? target, bool show, UiThemeData theme)
    {
        if (!show || target is null || !NavVisibility.TryGetRect(target.NavNode, out var rect))
        {
            SetVisible(false);
            return;
        }
        if (!Ensure(parent, prefab)) return;
        if (_node.GetComponent<CanvasTransform>() is not { } ringTransform)
        {
            // 作ったフレームはまだ構築されていない: 次のフレームで置く
            Redraw.Request();
            return;
        }
        float gap = theme.Number(NavTokens.SizeFocusRingGap);
        float width = theme.Number(NavTokens.SizeFocusRingWidth);
        // 1 単位の画素数と、平行移動 0 のときの枠の左上（画面の画素）
        var (pxPerUnit, origin) = MeasureFrame(parent, ringTransform);
        var ring = FocusRingMath.RingRect(rect, origin, pxPerUnit, gap, width);
        float targetRadius = target.NavNode.GetComponent<Sprite>() is { Shape: SpriteShapeKind.Rect } sprite ? sprite.CornerRadius : 0f;
        float radius = FocusRingMath.RingRadius(targetRadius, gap, width, theme.Number(NavTokens.RadiusFocusRing));
        bool changed = ApplySprite(ring.Size, radius, width, theme.Color(NavTokens.ColorFocusRing));
        changed |= ApplyLayout(ring.Position, UiLayers.FocusRingBand(theme) - NavNode.AncestorsBias(_node));
        changed |= SetVisible(true);
        if (changed) Redraw.Request();
    }

    /// <summary>枠のノードを消す（UiNavigator の破棄）。</summary>
    public void Destroy()
    {
        if (_node.IsValid) _node.Destroy();
        _node = default;
        _visible = null;
    }

    /// <summary>枠のノードを用意する（無ければ作る。プレハブが替わったら作り直す）。用意できれば true。</summary>
    private bool Ensure(GameObject parent, string prefab)
    {
        if (string.IsNullOrEmpty(prefab) || !parent.IsValid) return false;
        if (_node.IsValid && prefab == _prefab) return true;
        if (prefab == _failedPrefab) return false;
        if (_node.IsValid) _node.Destroy();
        ResetWritten();
        _node = GameObject.Instantiate(prefab, parent);
        _prefab = prefab;
        if (!_node.IsValid)
        {
            _failedPrefab = prefab;
            if (!_warned)
            {
                _warned = true;
                Debug.LogWarning($"{LogPrefix} フォーカスの枠を作れません（枠のプレハブ: {prefab}。templates/ui の focus_ring.actor を取り込むと出ます）");
            }
            return false;
        }
        // 構築されて置くまでは隠しておく（プレハブの位置に一瞬出ないように）
        _node.Visible = false;
        _visible = false;
        return true;
    }

    /// <summary>
    /// 1 単位の画素数と、平行移動 0 のときの枠の左上（画面の画素）を求める。枠自身が前のフレームに描かれていれば
    /// その矩形から測り（書いた大きさ・平行移動で割り戻す）、まだなら親のレイアウトの矩形から推す。
    /// </summary>
    private (float pxPerUnit, Vector2 origin) MeasureFrame(GameObject parent, CanvasTransform ringTransform)
    {
        // 親から推す（最初の 1 回・枠がまだ描かれていないとき）
        float pxPerUnit = Screen.DpScale;
        var origin = Vector2.Zero;
        if (parent.GetComponent<CanvasTransform>() is { HasLayout: true } p)
        {
            var parentRect = p.LayoutRect;
            pxPerUnit = FocusRingMath.PxPerUnit(parentRect.width, p.LayoutSize.x, Screen.DpScale);
            origin = parentRect.Position;
        }
        // 枠自身の前のフレームの矩形から測り直す（書いた値で描かれたものだけ）
        if (ringTransform.HasLayout && float.IsFinite(_size.x) && _size.x > Epsilon && float.IsFinite(_translate.x))
        {
            var drawn = ringTransform.LayoutRect;
            float measured = FocusRingMath.PxPerUnit(drawn.width, _size.x, pxPerUnit);
            if (drawn.width > 0f)
            {
                pxPerUnit = measured;
                origin = new Vector2(drawn.x - _translate.x * pxPerUnit, drawn.y - _translate.y * pxPerUnit);
            }
        }
        return (pxPerUnit, origin);
    }

    /// <summary>大きさ・角丸・線を当てる（変わったものだけ）。変えたら true。</summary>
    private bool ApplySprite(Vector2 size, float radius, float width, Color color)
    {
        if (_node.GetComponent<Sprite>() is not { } sprite) return false;
        bool changed = false;
        if (!Near(size, _size))
        {
            sprite.Size = size;
            _size = size;
            changed = true;
        }
        if (!Near(radius, _radius))
        {
            sprite.Shape = SpriteShapeKind.Rect;
            sprite.CornerRadius = radius;
            _radius = radius;
            changed = true;
        }
        if (!Near(width, _width))
        {
            sprite.BorderWidth = width;
            _width = width;
            changed = true;
        }
        if (!color.Equals(_color))
        {
            sprite.BorderColor = color;
            // 塗りは透明（縁だけ）
            sprite.Color = new Color(color.r, color.g, color.b, 0f);
            _color = color;
            changed = true;
        }
        return changed;
    }

    /// <summary>平行移動と底上げを当てる（CanvasLayoutItem の実行中だけの欄。無ければ位置で代える）。変えたら true。</summary>
    private bool ApplyLayout(Vector2 translate, int bias)
    {
        bool changed = false;
        if (!Near(translate, _translate))
        {
            if (_node.GetComponent<CanvasLayoutItem>() is { } item) item.Translate = translate;
            else if (_node.GetComponent<CanvasTransform>() is { } ct) ct.Position = translate;
            _translate = translate;
            changed = true;
        }
        if (bias != _bias)
        {
            NavNode.SetBias(_node, bias);
            _bias = bias;
            changed = true;
        }
        return changed;
    }

    /// <summary>見せる・隠す（変わったら true）。</summary>
    private bool SetVisible(bool visible)
    {
        if (_visible == visible || !_node.IsValid) return false;
        _node.Visible = visible;
        _visible = visible;
        Redraw.Request();
        return true;
    }

    /// <summary>最後に書いた値を忘れる（作り直したノードへすべて書き直す）。</summary>
    private void ResetWritten()
    {
        _translate = new Vector2(float.NaN, float.NaN);
        _size = new Vector2(float.NaN, float.NaN);
        _radius = float.NaN;
        _width = float.NaN;
        _color = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        _bias = int.MinValue;
        _visible = null;
    }

    /// <summary>2 つの値が近いか（前の値が NaN なら違う）。</summary>
    private static bool Near(float a, float b) => MathF.Abs(a - b) <= Epsilon;

    /// <summary>2 つのベクトルが近いか。</summary>
    private static bool Near(Vector2 a, Vector2 b) => Near(a.x, b.x) && Near(a.y, b.y);
}
