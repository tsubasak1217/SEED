using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ListRowNav.cs — 一覧（SEED.UI.ListView）の行の方向キーのアダプタ（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  行は部品（UiWidget）ではなく、行のプレハブの根の CanvasGesture（タップを受ける）とスクリプトで押される。
//  移れる = 行の根の CanvasGesture が有効でタップを受け、スクリプトが付いていて、移れる位置にある（窓の外でもスクロールで見せる）。
//  行の根が SEED.UI の部品（例えば Button）なら、そちらのアダプタに任せて行としては数えない（二重に数えない）。
//  決定 = 行の Gesture のタップ相当: 行の根のスクリプトすべてへ OnGestureTap を、行の真ん中を押した値で届ける
//  （エンジンのジェスチャーと同じく、CanvasGesture を付けたノードのスクリプトにだけ届く）。スクリプトの例外はここで止めてログへ出す。
// ============================================================

/// <summary>一覧の行のアダプタ。</summary>
internal sealed class ListRowNav : IUiNavigable
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] nav:";
    /// <summary>半分。</summary>
    private const float Half = 0.5f;
    /// <summary>合成するタップの指の番号（最初の指）。</summary>
    private const int TapPointerId = 0;
    /// <summary>合成するタップの押していた時間（秒。キーの決定は一瞬）。</summary>
    private const float TapDuration = 0f;

    /// <summary>行の根のノード。</summary>
    private readonly GameObject _row;

    /// <summary>行に被せる。</summary>
    /// <param name="row">行の根のノード。</param>
    public ListRowNav(GameObject row) { _row = row; }

    /// <inheritdoc />
    public GameObject NavNode => _row;

    /// <inheritdoc />
    public Rect CanvasRect => NavVisibility.TryGetRect(_row, out var rect) ? rect : Rect.Zero;

    /// <inheritdoc />
    public bool IsNavigable
        => _row.IsValid
           && _row.GetComponent<CanvasGesture>() is { Enabled: true, Tap: true }
           && !HasWidgetAdapter()
           && _row.GetScripts<SEEDScript>().Length > 0
           && NavVisibility.IsReachable(_row);

    /// <inheritdoc />
    public NavAxis AdjustAxis => NavAxis.None;

    /// <inheritdoc />
    public void OnNavFocus(bool focused) { }

    /// <inheritdoc />
    public void OnNavSubmit()
    {
        if (!_row.IsValid || _row.GetComponent<CanvasGesture>() is not { Enabled: true, Tap: true }) return;
        var e = SyntheticTap();
        foreach (var script in _row.GetScripts<SEEDScript>())
        {
            try
            {
                script.OnGestureTap(e);
            }
            catch (Exception ex)
            {
                // 行のスクリプトの例外で UiNavigator（呼び出し元）まで止めない
                Debug.LogError($"{LogPrefix} 行 '{_row.Name}' のタップで例外: {ex}");
            }
        }
        Redraw.Request();
    }

    /// <inheritdoc />
    public bool OnNavAdjust(int direction) => false;

    /// <inheritdoc />
    public override string ToString() => $"ListRow({_row.Name})";

    /// <summary>行の根が SEED.UI の部品で、そのアダプタがあるか（あればそちらで数える）。</summary>
    private bool HasWidgetAdapter()
    {
        foreach (var widget in _row.GetScripts<UiWidget>())
            if (NavAdapters.For(widget) is not null) return true;
        return false;
    }

    /// <summary>行の真ん中を押したタップの値（エンジンのジェスチャーと同じ座標の取り方。移動・速度は 0）。</summary>
    private GestureEvent SyntheticTap()
    {
        var rect = CanvasRect;
        var screen = rect.Center;
        // キャンバスの画素（画面の中央が原点・Y 下向き）
        var canvas = new Vector2(screen.x - Screen.Width * Half, screen.y - Screen.Height * Half);
        // ノードの単位の真ん中（ノードの見た目の矩形の左上が原点）
        var local = _row.GetComponent<CanvasTransform>() is { HasLayout: true } t ? t.LayoutSize * Half : Vector2.Zero;
        return new GestureEvent(GestureKind.Tap, TapPointerId, canvas, screen, local, canvas, Vector2.Zero, Vector2.Zero,
            Screen.DpScale, TapDuration, canceled: false);
    }
}
