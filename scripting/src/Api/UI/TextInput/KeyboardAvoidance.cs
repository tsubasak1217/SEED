using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  KeyboardAvoidance.cs — フォーカスのある入力欄をソフトキーボードの上に見せる（W2-6b。docs/ui_text_input.md §10）
//
//  キーボードは描画面の上に重なる（Android の描画面は縮まない。I-9）ので、入力欄の側で避ける。欄の祖先を近い順に見て、
//    1. 縦にスクロールする窓（CanvasScroll。有効で縦か両方）があれば: 窓のうちキーボードに隠れる高さを中身の末尾の余白
//       （CanvasScroll.EndInset）にし（中身の最後までキーボードの上へスクロールできる＝鳴動画面の「キーボードが出てもスクロールできる」）、
//       欄の下端 ＋ size.keyboard_gap がキーボードの上端を越えていれば、その分だけ ScrollTo で上へ送る
//    2. キーボードを避ける入れ物（IKeyboardInsetTarget。ダイアログ）があれば: 入れ物を持ち上げる（Dialog が札を上へずらす）
//  のどちらか 1 つを行う（近い方）。フォーカスが外れた・キーボードが隠れたら余白と持ち上げを戻す。
//  見直すのは、キーボードの高さが変わったとき・フォーカスを得たときだけ（毎フレームは見直さない。スクロールの途中の矩形で
//  二重に送らない）。量の計算は KeyboardInsetMath（純粋な計算）。
// ============================================================

/// <summary>キーボードを避けて持ち上がる入れ物（ダイアログなど）。</summary>
public interface IKeyboardInsetTarget
{
    /// <summary>入れ物の根（入力欄の祖先をたどって見つける）。</summary>
    GameObject InsetOwner { get; }

    /// <summary>入れ物をキーボードの上へ持ち上げる（キーボードの上端と余白は画面の画素）。</summary>
    void ApplyKeyboardLift(float keyboardTopPx, float gapPx);

    /// <summary>持ち上げを戻す。</summary>
    void ClearKeyboardLift();
}

/// <summary>キーボードを避ける入れ物の登録簿（静的）。</summary>
public static class KeyboardInsets
{
    /// <summary>祖先をたどる深さの上限（壊れた木で回り続けない）。</summary>
    internal const int MaxAncestorDepth = 64;

    /// <summary>根のノード → 入れ物。</summary>
    private static readonly Dictionary<(uint, uint), IKeyboardInsetTarget> Targets = new();

    /// <summary>入れ物を登録する（開いたとき）。</summary>
    public static void Register(IKeyboardInsetTarget target) => Targets[NavNode.Key(target.InsetOwner)] = target;

    /// <summary>登録を外す（閉じたとき）。</summary>
    public static void Unregister(IKeyboardInsetTarget target) => Targets.Remove(NavNode.Key(target.InsetOwner));

    /// <summary>ノードの祖先（自分を含む）でいちばん近い入れ物（無ければ null）。</summary>
    internal static IKeyboardInsetTarget? FindFor(GameObject node)
    {
        if (Targets.Count == 0) return null;
        var current = node;
        for (int depth = 0; depth < MaxAncestorDepth && current.IsValid; depth++)
        {
            if (Targets.TryGetValue(NavNode.Key(current), out var target)) return target;
            current = current.Parent;
        }
        return null;
    }
}

/// <summary>入力欄 1 つぶんのキーボードの避け方（TextField が持つ）。</summary>
internal sealed class KeyboardAvoider
{
    /// <summary>まだ当てていない印（キーボードの高さ）。</summary>
    private const float NotApplied = -1f;
    /// <summary>欄をキーボードの上へ送るスクロールの時間（秒）。</summary>
    private const float ScrollSeconds = 0.25f;

    /// <summary>欄のノード。</summary>
    private readonly GameObject _field;
    /// <summary>余白を置いたスクロールの窓（無ければ無効）。</summary>
    private GameObject _scrollNode = new(Entity.None);
    /// <summary>持ち上げた入れ物。</summary>
    private IKeyboardInsetTarget? _target;
    /// <summary>最後に当てたキーボードの高さ（画素。当てていなければ負）。</summary>
    private float _appliedKeyboard = NotApplied;

    /// <summary>作る。</summary>
    internal KeyboardAvoider(GameObject field) => _field = field;

    /// <summary>
    /// 毎フレーム呼ぶ: フォーカスとキーボードの高さが変わったときだけ避け方を当て直す。
    /// </summary>
    /// <param name="active">フォーカスがあり、避ける設定か。</param>
    /// <param name="keyboardHeight">キーボードの高さ（画面の画素。見えていなければ 0）。</param>
    /// <param name="gapPx">欄とキーボードの間の余白（画面の画素）。</param>
    internal void Update(bool active, float keyboardHeight, float gapPx)
    {
        if (!active || !(keyboardHeight > 0f))
        {
            Clear();
            return;
        }
        if (keyboardHeight == _appliedKeyboard) return;
        Apply(keyboardHeight, gapPx);
    }

    /// <summary>次のフレームで当て直す（欄をタップし直したとき。スクロールで欄がキーボードの下へ隠れた場合に備える）。</summary>
    internal void Invalidate() => _appliedKeyboard = NotApplied;

    /// <summary>余白と持ち上げを戻す。</summary>
    internal void Clear()
    {
        if (_scrollNode.IsValid && _scrollNode.GetComponent<CanvasScroll>() is { } scroll) scroll.EndInset = Vector2.Zero;
        _scrollNode = new GameObject(Entity.None);
        _target?.ClearKeyboardLift();
        _target = null;
        _appliedKeyboard = NotApplied;
    }

    /// <summary>近い方の祖先（縦のスクロールの窓・キーボードを避ける入れ物）で避ける。</summary>
    private void Apply(float keyboardHeight, float gapPx)
    {
        _appliedKeyboard = keyboardHeight;
        float keyboardTop = KeyboardInsetMath.KeyboardTop(Screen.Height, keyboardHeight);
        var current = _field.Parent;
        for (int depth = 0; depth < KeyboardInsets.MaxAncestorDepth && current.IsValid; depth++)
        {
            if (current.GetComponent<CanvasScroll>() is { } scroll && scroll.Enabled && scroll.Direction != ScrollDirection.Horizontal)
            {
                AvoidInScroll(current, scroll, keyboardTop, gapPx);
                return;
            }
            if (KeyboardInsets.FindFor(current) is { } target && NavNode.Key(target.InsetOwner) == NavNode.Key(current))
            {
                _target = target;
                target.ApplyKeyboardLift(keyboardTop, gapPx);
                return;
            }
            current = current.Parent;
        }
    }

    /// <summary>スクロールの窓で避ける（隠れる高さを末尾の余白に、欄が隠れていれば上へ送る）。</summary>
    private void AvoidInScroll(GameObject node, CanvasScroll scroll, float keyboardTop, float gapPx)
    {
        if (node.GetComponent<CanvasTransform>() is not { } transform || !transform.HasLayout) return;
        var viewport = transform.LayoutRect;
        float pxPerUnit = KeyboardInsetMath.PxPerUnit(viewport.height, scroll.ViewportSize.y);
        float hidden = KeyboardInsetMath.HiddenPx(viewport, keyboardTop);
        _scrollNode = node;
        scroll.EndInset = new Vector2(0f, KeyboardInsetMath.PxToUnits(hidden, pxPerUnit));
        if (_field.GetComponent<CanvasTransform>() is not { } fieldTransform || !fieldTransform.HasLayout) return;
        float overlap = KeyboardInsetMath.OverlapPx(fieldTransform.LayoutRect, keyboardTop, gapPx);
        if (!(overlap > 0f)) return;
        var position = scroll.Position;
        scroll.ScrollTo(new Vector2(position.x, position.y + KeyboardInsetMath.PxToUnits(overlap, pxPerUnit)), ScrollSeconds);
    }
}
