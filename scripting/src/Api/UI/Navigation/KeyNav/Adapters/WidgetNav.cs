namespace SEED.UI;

// ============================================================
//  WidgetNav.cs — SEED.UI の部品（UiWidget）を方向キーで扱うアダプタの共通の土台（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  部品の公開 API は変えず、部品の外から IUiNavigable を被せる（部品ごとの決定・値の増減は派生が決める）。
//    - ノード = 部品のアクター（UiWidget.Owner）
//    - 矩形 = そのアクターの前のフレームのレイアウトの矩形（画面の画素）
//    - 移れる = 登録簿に生きていて（OnStart の後・OnDestroy の前）、操作を受け（IsEnabled）、移れる位置にある（NavVisibility）
//  アダプタは部品 1 つにつき 1 つ（NavAdapters がキャッシュする。今のフォーカス・範囲ごとの覚えが同じものを指すため）。
// ============================================================

/// <summary>部品のアダプタの共通の土台。</summary>
/// <typeparam name="T">部品の型。</typeparam>
internal abstract class WidgetNav<T> : IUiNavigable where T : UiWidget
{
    /// <summary>部品。</summary>
    protected readonly T Widget;

    /// <summary>部品に被せる。</summary>
    /// <param name="widget">部品。</param>
    protected WidgetNav(T widget) { Widget = widget; }

    /// <inheritdoc />
    public GameObject NavNode => Widget.Owner;

    /// <inheritdoc />
    public Rect CanvasRect => NavVisibility.TryGetRect(Widget.Owner, out var rect) ? rect : Rect.Zero;

    /// <inheritdoc />
    public virtual bool IsNavigable
        => UiRegistry.IsRegistered(Widget) && Widget.IsEnabled && NavVisibility.IsReachable(Widget.Owner);

    /// <inheritdoc />
    public virtual NavAxis AdjustAxis => NavAxis.None;

    /// <inheritdoc />
    public virtual void OnNavFocus(bool focused) { }

    /// <inheritdoc />
    public abstract void OnNavSubmit();

    /// <inheritdoc />
    public virtual bool OnNavAdjust(int direction) => false;

    /// <inheritdoc />
    public override string ToString() => $"{Widget.GetType().Name}({Widget.Owner.Name})";
}
