using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiFocus.cs — フォーカス（キーボードで動かす相手）と、画面ごとのフォーカスの範囲（W2-7）
//
//  - 相手（IFocusable）: キーボードの入力を受ける部品（ホイール〈W2-5〉・入力欄〈W2-6〉）。Request で相手になりたいと申し出る
//  - 範囲（FocusScope）: 画面のスタックの段・ダイアログ・シート・覆いごとに 1 つ。相手は自分の祖先をたどって
//    最初に見つかった範囲に属する（どれにも属さなければ根の範囲）。いちばん前の範囲の相手だけが「今のフォーカス」
//  - 画面を積む・ダイアログを開くと、その範囲が前に出る（下の画面のホイールは矢印キーを受けない）。
//    閉じる・下ろすと範囲が外れ、前に出た範囲が覚えていた相手へ戻る
//  - 戻るの段（BackDispatcher）は最初に今のフォーカスへ尋ねる（IBackConsumer。W2-6 の入力欄が IME を閉じる・フォーカスを外す）
//  純粋な本体は Model/FocusModel.cs（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>フォーカスを受ける部品（キーボードの入力を受ける）。</summary>
public interface IFocusable
{
    /// <summary>部品のアクター（範囲を祖先から探す起点）。</summary>
    GameObject FocusOwner { get; }

    /// <summary>今のフォーカスになった・外れた（見た目の切り替えなど）。</summary>
    void OnFocusChanged(bool focused);
}

/// <summary>戻るを先に受けられるフォーカスの相手（W2-6 の入力欄: IME を閉じる・フォーカスを外す）。</summary>
public interface IBackConsumer
{
    /// <summary>戻るを受けたら true（後ろの層へ回さない）。</summary>
    bool HandleBack();
}

/// <summary>フォーカスの範囲（画面・ダイアログ・シート・覆いごと）。</summary>
public sealed class FocusScope
{
    /// <summary>範囲の持ち主のノード（画面の枠・ダイアログの根など。祖先をたどって見つける）。</summary>
    public GameObject Owner { get; }
    /// <summary>名前（ログ・診断）。</summary>
    public string Name { get; }

    internal FocusScope(GameObject owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>フォーカスの管理（シーンに 1 つ。静的）。</summary>
public static class UiFocus
{
    /// <summary>祖先をたどる深さの上限（壊れた木で回り続けない）。</summary>
    private const int MaxAncestorDepth = 64;

    /// <summary>範囲と相手の本体。</summary>
    private static readonly FocusModel<IFocusable> Model = new();
    /// <summary>持ち主のノード → 範囲。</summary>
    private static readonly Dictionary<(uint, uint), FocusScope> ScopesByOwner = new();
    /// <summary>最後に知らせた今のフォーカス（変わったときだけ OnFocusChanged を呼ぶ）。</summary>
    private static IFocusable? _notified;

    /// <summary>今のフォーカス（いちばん前の範囲の相手。無ければ null）。</summary>
    public static IFocusable? Current => Model.Current;

    /// <summary>いちばん前の範囲（根の範囲なら null）。</summary>
    public static FocusScope? TopScope => Model.TopScope as FocusScope;

    /// <summary>今のフォーカスが変わった（新しい相手。無ければ null）。</summary>
    public static event System.Action<IFocusable?>? Changed;

    /// <summary>
    /// 範囲を作って前へ出す（画面の枠・ダイアログの根など。同じ持ち主なら前の範囲を返して前へ出す）。
    /// overlay = 重ねる範囲（ダイアログ・シート・覆い。画面の範囲より常に前）。
    /// </summary>
    public static FocusScope CreateScope(GameObject owner, string name, bool overlay = false)
    {
        var key = NavNode.Key(owner);
        if (!ScopesByOwner.TryGetValue(key, out var scope))
        {
            scope = new FocusScope(owner, name);
            ScopesByOwner[key] = scope;
        }
        Model.PushScope(scope, overlay);
        Notify();
        return scope;
    }

    /// <summary>範囲を前へ出す（画面が上になった・タブを選んだ）。</summary>
    public static void BringToFront(FocusScope? scope)
    {
        if (scope is null) return;
        Model.BringToFront(scope);
        Notify();
    }

    /// <summary>範囲を後ろへ回す（覆われた・タブを離れた）。</summary>
    public static void SendToBack(FocusScope? scope)
    {
        if (scope is null) return;
        Model.SendToBack(scope);
        Notify();
    }

    /// <summary>
    /// 範囲を重ねる範囲にする・外す（2026-10-02。ModalHost.Park が、画面の下へ回した面の範囲を画面の範囲と同じ扱いにする）。
    /// </summary>
    internal static void SetOverlay(FocusScope? scope, bool overlay)
    {
        if (scope is null) return;
        Model.SetOverlay(scope, overlay);
        Notify();
    }

    /// <summary>
    /// 範囲が重ねる範囲（ダイアログ・シート・覆い）か（2026-10-03。方向キーの移動〈UiNavigation〉が、ダイアログの下の部品へ移らないための問い）。
    /// </summary>
    internal static bool IsOverlayScope(FocusScope? scope) => scope is not null && Model.IsOverlay(scope);

    /// <summary>
    /// 範囲がまだあるか（外れていない。2026-10-03。方向キーの移動〈UiNavigation〉が、閉じていく途中のダイアログの部品に枠を残さないための問い）。
    /// </summary>
    internal static bool IsLiveScope(FocusScope scope) => Model.HasScope(scope);

    /// <summary>範囲を外す（画面を下ろした・ダイアログを閉じた）。</summary>
    public static void RemoveScope(FocusScope? scope)
    {
        if (scope is null) return;
        ScopesByOwner.Remove(NavNode.Key(scope.Owner));
        Model.RemoveScope(scope);
        Notify();
    }

    /// <summary>
    /// 相手になりたい（指で触れた・スクリプトが Focus した）。相手の範囲がいちばん前なら今のフォーカスになる（true）。
    /// 後ろの範囲なら、その範囲が前に出たときの相手として覚える（false）。
    /// </summary>
    public static bool Request(IFocusable item)
    {
        bool current = Model.Request(item, ScopeOf(item.FocusOwner));
        Notify();
        return current;
    }

    /// <summary>相手を外す（部品が消える・フォーカスを手放す）。</summary>
    public static void Release(IFocusable item)
    {
        Model.Release(item);
        Notify();
    }

    /// <summary>ノードが属する範囲（祖先をたどって最初に見つかった範囲の持ち主。無ければ null＝根の範囲）。</summary>
    public static FocusScope? ScopeOf(GameObject node)
    {
        if (ScopesByOwner.Count == 0) return null;
        var current = node;
        for (int depth = 0; depth < MaxAncestorDepth && current.IsValid; depth++)
        {
            if (ScopesByOwner.TryGetValue(NavNode.Key(current), out var scope)) return scope;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>今のフォーカスへ戻るを尋ねる（BackDispatcher の Focus の層）。</summary>
    internal static bool HandleBack() => Model.Current is IBackConsumer consumer && consumer.HandleBack();

    /// <summary>
    /// 今のフォーカスが戻るを受けそうか（副作用なし。BackDispatcher の Focus の層の問い。W2 の手直し 3b）。
    /// IBackConsumer の中身は問えないので、相手が IBackConsumer なら受けるとみなす（安全側）。
    /// </summary>
    internal static bool WantsBack() => Model.Current is IBackConsumer;

    /// <summary>今のフォーカスが変わっていたら、外れた相手と新しい相手へ知らせる。</summary>
    private static void Notify()
    {
        var now = Model.Current;
        if (ReferenceEquals(now, _notified)) return;
        var previous = _notified;
        _notified = now;
        previous?.OnFocusChanged(false);
        now?.OnFocusChanged(true);
        Changed?.Invoke(now);
        Redraw.Request();
    }
}
