using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  WidgetRef.cs — 結び付ける部品（UiWidget）の引き当て（部品そのもの、または部品の付くノードから）
//
//  部品のスクリプトは画面のスクリプトより後に始まることがある（プレハブの子の OnStart の順は決まっていない。Instantiate した部品は
//  次のフレームから）。ノードから結ぶときは、登録簿（UiRegistry）の版が変わるたびに UiWidget.Of で引き直し、見つかったら固定する。
//    - 生存: 部品が見つかった後は「まだ登録簿にいるか」（OnDestroy で外れる）。見つかる前はノード（ActorLife）。
// ============================================================

/// <summary>結び付ける部品の引き当て。</summary>
/// <typeparam name="TWidget">部品の型。</typeparam>
internal sealed class WidgetRef<TWidget> where TWidget : UiWidget
{
    /// <summary>まだ引いていないことを表す登録簿の版。</summary>
    private const int NeverLooked = -1;

    /// <summary>部品の付くノードの見張り（部品そのものから作ったときは null）。</summary>
    private readonly ActorLife? _node;

    /// <summary>見つかった部品（見つかるまで null）。</summary>
    private TWidget? _widget;

    /// <summary>最後に引いた登録簿の版（変わっていなければ引き直さない）。</summary>
    private int _registryVersion = NeverLooked;

    /// <summary>部品そのものから作る（UiWidget.Of で引いた部品。OnStart の後）。</summary>
    /// <param name="widget">部品。</param>
    internal WidgetRef(TWidget widget)
    {
        _widget = widget;
    }

    /// <summary>部品の付くノードから作る（部品がまだ始まっていなくてよい）。</summary>
    /// <param name="node">部品の付くノード。</param>
    internal WidgetRef(GameObject node)
    {
        _node = new ActorLife(node);
    }

    /// <summary>今の部品（まだ始まっていなければ null）。</summary>
    internal TWidget? Current
    {
        get
        {
            if (_widget is not null || _node is null) return _widget;

            // 登録簿が変わっていなければ引き直さない（毎フレームの確かめを安くする）
            if (_registryVersion == UiRegistry.Version) return null;
            _registryVersion = UiRegistry.Version;
            _widget = UiWidget.Of<TWidget>(_node.Node);
            return _widget;
        }
    }

    /// <summary>部品（または、まだ見つかっていない部品の付くノード）がまだあるか。</summary>
    internal bool IsAlive
    {
        get
        {
            if (_widget is not null) return UiRegistry.IsRegistered(_widget);
            return _node?.IsAlive ?? false;
        }
    }
}
