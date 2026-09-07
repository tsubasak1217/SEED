using System;

namespace SEED;

/// <summary>
/// 名前付きイベントバス（スクリプト間の疎結合な通知）。
///
/// 「文字列の名前」でイベントを発火（<see cref="Raise(string)"/>）し、
/// 同じ名前を購読（<see cref="Subscribe(string, Action)"/>）しているハンドラを同期で呼ぶ。
/// 発火側と購読側が互いの型・参照を知らなくて良いのが利点で、
/// 「魚がヒットした」「HP が変わった」といったゲーム内通知を配線なしで飛ばせる。
///
/// 【ScriptEvent との使い分け】
/// - <see cref="ScriptEvent"/>: インスペクタで「どのアクターのどのメソッドを呼ぶか」を結線する。
///   呼び先が決まっていて、デザイナーが差し替えたい 1 対 1（または少数）の通知向け。
/// - <c>SEED.Events</c>: 呼び先を知らないまま名前だけで飛ばす 1 対多の通知向け。
///   受け手が動的に増減する（生成・破棄されるアクターが受ける）場合はこちら。
///
/// 【推奨: 寿命に自動追従する購読】
/// スクリプトからは <c>Events.Subscribe</c> ではなく <c>this.On("name", handler)</c> を使うこと。
/// そのスクリプトインスタンスの破棄時（OnDestroy 経路）に自動で解除されるため、
/// 破棄済みスクリプトのハンドラが呼ばれ続ける事故が起きない。
///
/// 【仕様の要点】
/// - 発火は同期・即時。登録順にハンドラを呼ぶ。
/// - 引数は 0 個または 1 個（string / float / GameObject）。種別が一致する購読だけを呼ぶ。
/// - ハンドラ内の例外は握り潰してエラーログへ出し、残りのハンドラは続行する。
/// - 発火中の購読・解除は進行中の発火には反映されず、次回の発火から反映される。
/// - 同名イベントの再入は深さ上限で打ち切る（無限再帰の保険）。
/// - イベント名は大文字小文字を区別する。空文字は無視して警告 1 回。
/// </summary>
public static class Events
{
    // ── 発火（Raise）────────────────────────────────────────
    // 戻り値は「実際に呼び出したハンドラ件数」。0 なら誰も受けていない（デバッグに便利）。

    /// <summary>引数なしでイベントを発火する。<c>Action</c> で購読しているハンドラだけを呼ぶ。</summary>
    /// <param name="name">イベント名（大文字小文字を区別）。</param>
    /// <returns>呼び出したハンドラ件数。</returns>
    public static int Raise(string name)
        => EventBus.Raise(name, EventBus.EventPayload.None());

    /// <summary>string 引数付きでイベントを発火する。<c>Action&lt;string&gt;</c> の購読だけを呼ぶ。</summary>
    public static int Raise(string name, string arg)
        => EventBus.Raise(name, EventBus.EventPayload.Of(arg));

    /// <summary>float 引数付きでイベントを発火する。<c>Action&lt;float&gt;</c> の購読だけを呼ぶ。</summary>
    public static int Raise(string name, float arg)
        => EventBus.Raise(name, EventBus.EventPayload.Of(arg));

    /// <summary>GameObject 引数付きでイベントを発火する。<c>Action&lt;GameObject&gt;</c> の購読だけを呼ぶ。</summary>
    public static int Raise(string name, GameObject arg)
        => EventBus.Raise(name, EventBus.EventPayload.Of(arg));

    // ── 購読（Subscribe）────────────────────────────────────
    // 戻り値のハンドルを Dispose するか Unsubscribe へ渡すと解除できる。
    // 名前が空・ハンドラが null の場合は登録されず、解除済みハンドル（IsActive=false）が返る。

    /// <summary>引数なしイベントを購読する。</summary>
    /// <param name="name">イベント名（大文字小文字を区別）。</param>
    /// <param name="handler">呼び出すハンドラ。</param>
    /// <returns>解除に使う購読ハンドル（null は返らない）。</returns>
    public static EventSubscription Subscribe(string name, Action handler)
        => EventBus.Subscribe(name, EventArgKind.None, handler);

    /// <summary>string 引数付きイベントを購読する。</summary>
    public static EventSubscription Subscribe(string name, Action<string> handler)
        => EventBus.Subscribe(name, EventArgKind.String, handler);

    /// <summary>float 引数付きイベントを購読する。</summary>
    public static EventSubscription Subscribe(string name, Action<float> handler)
        => EventBus.Subscribe(name, EventArgKind.Float, handler);

    /// <summary>GameObject 引数付きイベントを購読する。</summary>
    public static EventSubscription Subscribe(string name, Action<GameObject> handler)
        => EventBus.Subscribe(name, EventArgKind.GameObject, handler);

    // ── 解除・クリア ────────────────────────────────────────

    /// <summary>
    /// 購読を解除する（<see cref="EventSubscription.Dispose"/> と等価）。
    /// null・解除済み・未登録ハンドルはいずれも無害に無視する。
    /// </summary>
    public static void Unsubscribe(EventSubscription? handle) => EventBus.Unsubscribe(handle);

    /// <summary>
    /// 指定した名前の購読をすべて解除する。
    /// 手動 <see cref="Subscribe(string, Action)"/> の後始末や、
    /// シーン遷移前に古い購読を掃除したいときに使う。
    /// </summary>
    public static void Clear(string name) => EventBus.Clear(name);

    /// <summary>
    /// 全イベントの購読を破棄する。
    ///
    /// 注意: 他スクリプトが張った購読（<c>this.On(...)</c> による自動追従分を含む）も
    /// まとめて消えるため、シーン遷移直前などゲーム全体の区切りでのみ使うこと。
    /// ホットリロード時はエンジンが自動的にこれを呼ぶ。
    /// </summary>
    public static void ClearAll() => EventBus.ClearAll();

    /// <summary>指定した名前の現在の購読件数（デバッグ用）。</summary>
    public static int SubscriberCount(string name) => EventBus.SubscriberCount(name);
}
