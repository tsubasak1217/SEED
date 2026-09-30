using System;
using System.Collections.Generic;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  BackChain.cs — 戻るの段（層を順に並べ、最初に「受けた」層で止める。W2-7。純粋な計算）
//
//  戻る（Android の戻る＝Escape、PC の Esc）を上の層から順に配り、最初に true（受けた）を返した層で止める。
//  どの層も受けなければ呼び出し側がアプリを閉じずに背面へ回す（App.MoveTaskToBack。roadmap X-3）。
//  既定の並び（BackOrder。値が小さいほど先）:
//    Focus（入力欄の IME を閉じる・フォーカスを外す。W2-6）→ Dialog → Sheet → Overlay（覆い）→ Navigation（画面のスタック・タブ）
//  トーストは戻るを受けない。スクリプトは任意の順の値で自分の層を足せる（BackDispatcher.AddLayer）。
//  同じ順の層は足した順（先に足した方が先）。
//
//  Navigation の層の中では、画面のスタック・タブ（入れ子になり得る）を **内側から** 尋ねる（NavigatorOrder）:
//  見えていて、上の段（覆われていない画面）の中にある物のうち、祖先の数が多い物が先。
//
//  【受ける層があるか（W2 の手直し 3b）】層の handler は呼ぶと閉じる・下ろすまでする（副作用なしに問えない）ので、層は任意で
//  副作用の無い問い（wants）を持つ。WouldHandle は上の層から wants を問い、最初に true の層を返す（配らずに「戻るを受けるか」を知る）。
//  wants の無い層は「受ける」とみなす（安全側: 受けると答えてもアプリには Escape が届き、受けなければ呼び出し側が背面へ回すので、
//  予測型の戻るの見た目が出ないだけで困らない）。予測型の戻る（Android 13 以上）で、根ではシステムに戻るを任せるかを決めるのに使う。
//  層は任意でプレビューの相手（preview。IBackPreviewTarget。最初に受ける層の相手を縮めて見せる）も持てる。
// ============================================================

/// <summary>戻るの段の既定の並び（値が小さいほど先に尋ねる）。</summary>
public static class BackOrder
{
    /// <summary>フォーカスしている入力欄（IME を閉じる・フォーカスを外す）。</summary>
    public const int Focus = 100;
    /// <summary>ダイアログ。</summary>
    public const int Dialog = 200;
    /// <summary>下からのシート。</summary>
    public const int Sheet = 300;
    /// <summary>上からの覆い。</summary>
    public const int Overlay = 400;
    /// <summary>画面のスタックとタブ（内側から）。</summary>
    public const int Navigation = 500;
}

/// <summary>戻るを配った結果。</summary>
/// <param name="Handled">どれかの層が受けたか（false なら呼び出し側がアプリを背面へ回す）。</param>
/// <param name="Layer">受けた層の名前（受けなければ空）。</param>
/// <param name="Order">受けた層の順の値（受けなければ int.MaxValue）。</param>
public readonly record struct BackDispatchResult(bool Handled, string Layer, int Order)
{
    /// <summary>どの層も受けなかった。</summary>
    public static BackDispatchResult Unhandled => new(false, string.Empty, int.MaxValue);
}

/// <summary>戻るの段（層の並び）。</summary>
public sealed class BackChain
{
    /// <summary>層 1 つ。</summary>
    private sealed class Layer
    {
        public required int Order { get; init; }
        public required string Name { get; init; }
        public required Func<bool> Handler { get; init; }
        public required long Sequence { get; init; }
        /// <summary>副作用の無い「受けるか」の問い（無ければ受けるとみなす）。</summary>
        public Func<bool>? Wants { get; init; }
        /// <summary>予測型の戻るのプレビューの相手（無ければ・null を返せば何も縮めない）。</summary>
        public Func<IBackPreviewTarget?>? Preview { get; init; }

        /// <summary>この層が戻るを受けるか（問いが無ければ受けるとみなす）。</summary>
        public bool WouldHandle => Wants?.Invoke() ?? true;

        /// <summary>配った結果の形（名前と順）。</summary>
        public BackDispatchResult AsResult => new(true, Name, Order);
    }

    /// <summary>層（順の値 → 足した順で並べる）。</summary>
    private readonly List<Layer> _layers = new();
    /// <summary>足した順の通し番号。</summary>
    private long _sequence;

    /// <summary>層の数。</summary>
    public int Count => _layers.Count;

    /// <summary>
    /// 層を足す。戻り値を Dispose すると外れる。
    /// </summary>
    /// <param name="order">順の値（BackOrder。小さいほど先）。</param>
    /// <param name="name">名前（ログ・診断）。</param>
    /// <param name="handler">戻るを受けたら true。</param>
    public IDisposable Add(int order, string name, Func<bool> handler) => Add(order, name, handler, null, null);

    /// <summary>
    /// 層を足す（受けるかの問いとプレビューの相手つき。W2 の手直し 3b）。戻り値を Dispose すると外れる。
    /// </summary>
    /// <param name="order">順の値（BackOrder。小さいほど先）。</param>
    /// <param name="name">名前（ログ・診断）。</param>
    /// <param name="handler">戻るを受けたら true（閉じる・下ろすまでする）。</param>
    /// <param name="wants">副作用の無い「今押されたら受けるか」の問い（null = 受けるとみなす）。</param>
    /// <param name="preview">予測型の戻るのプレビューの相手（null・null を返す = 何も縮めない）。</param>
    public IDisposable Add(int order, string name, Func<bool> handler, Func<bool>? wants, Func<IBackPreviewTarget?>? preview = null)
    {
        var layer = new Layer { Order = order, Name = name, Handler = handler, Sequence = _sequence++, Wants = wants, Preview = preview };
        _layers.Add(layer);
        _layers.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Sequence.CompareTo(b.Sequence));
        return new Removal(this, layer);
    }

    /// <summary>
    /// 戻るを上の層から配る（最初に true を返した層で止める）。層の中で層を足し外ししても壊れないよう写しを回す。
    /// </summary>
    public BackDispatchResult Dispatch()
    {
        foreach (var layer in _layers.ToArray())
        {
            if (!_layers.Contains(layer)) continue;
            if (layer.Handler()) return layer.AsResult;
        }
        return BackDispatchResult.Unhandled;
    }

    /// <summary>
    /// 今戻るが押されたら受ける層（副作用なし。上の層から問いを尋ね、最初に true の層の名前と順。無ければ Unhandled）。
    /// 問いの無い層は受けるとみなす。
    /// </summary>
    public BackDispatchResult WouldHandle() => FirstWanting()?.AsResult ?? BackDispatchResult.Unhandled;

    /// <summary>
    /// 順が <paramref name="order"/> より後ろ（値が大きい）の層のうち、今戻るが押されたら受ける最初の層（副作用なし。無ければ Unhandled）。
    /// 入力欄（W2-6b の TextField）が、キーボードを閉じた後の戻るを後ろの層へ回すか、根なので自分で受けるかを決めるのに使う。
    /// </summary>
    /// <param name="order">この順より後ろの層だけを問う（例 BackOrder.Focus）。</param>
    public BackDispatchResult WouldHandleAfter(int order)
    {
        foreach (var layer in _layers.ToArray())
        {
            if (!_layers.Contains(layer) || layer.Order <= order) continue;
            if (layer.WouldHandle) return layer.AsResult;
        }
        return BackDispatchResult.Unhandled;
    }

    /// <summary>
    /// 最初に受ける層のプレビューの相手（予測型の戻るで縮めて見せる。受ける層が無い・相手が無ければ null）。
    /// </summary>
    public IBackPreviewTarget? PreviewTarget() => FirstWanting()?.Preview?.Invoke();

    /// <summary>上の層から問い、最初に受ける層（問いの中で層を足し外ししても壊れないよう写しを回す）。</summary>
    private Layer? FirstWanting()
    {
        foreach (var layer in _layers.ToArray())
        {
            if (!_layers.Contains(layer)) continue;
            if (layer.WouldHandle) return layer;
        }
        return null;
    }

    /// <summary>並び（診断・テスト用。順の値と名前）。</summary>
    public IReadOnlyList<(int Order, string Name)> Describe() => _layers.Select(l => (l.Order, l.Name)).ToList();

    /// <summary>層を外す口（Dispose で 1 回だけ外す）。</summary>
    private sealed class Removal : IDisposable
    {
        private BackChain? _chain;
        private readonly Layer _layer;

        public Removal(BackChain chain, Layer layer)
        {
            _chain = chain;
            _layer = layer;
        }

        public void Dispose()
        {
            _chain?._layers.Remove(_layer);
            _chain = null;
        }
    }
}

/// <summary>画面のスタック・タブ（入れ子）を尋ねる順。</summary>
public static class NavigatorOrder
{
    /// <summary>
    /// 見えている（active）物だけを、内側から（祖先の数 depth が多い順。同じなら元の並び）並べる。
    /// </summary>
    /// <param name="candidates">候補（物・祖先の数・見えていて上の段の中にあるか）。</param>
    public static List<T> InnermostFirst<T>(IEnumerable<(T Item, int Depth, bool Active)> candidates)
        => candidates
            .Select((c, i) => (c.Item, c.Depth, c.Active, Index: i))
            .Where(c => c.Active)
            .OrderByDescending(c => c.Depth)
            .ThenBy(c => c.Index)
            .Select(c => c.Item)
            .ToList();
}
