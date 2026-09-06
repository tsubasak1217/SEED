using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SEEDEditor.Controls;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「ScriptEvent の結線先候補を答える」実装。
///
/// ScriptEvent フィールドの UI（<see cref="ScriptEventFieldBuilder"/>）は
/// 「アクタ名 → そのアクタが持つスクリプト型 → 型 → 呼べるメソッド」を必要とするが、
/// その解決には次の 2 つが要り、どちらもインスペクタしか持っていない。
///   1. アクタ名 → DFS ID（Hierarchy へのフック <see cref="ActorRefJump.ActorDfsIdByName"/>）
///   2. DFS ID → コンポーネント構成（GET_ACTOR_COMPONENTS の IPC 往復）
/// そこでインスペクタが <see cref="IScriptEventCatalogProvider"/> を実装し、
/// UI 側は「非同期で候補が返ってくる」ことだけを知っていればよい形にする
/// （参照ピッカーの <see cref="IReferenceDropResolver"/> と同じ役割分担）。
///
/// 【重い処理を UI スレッドで回さない】
/// スクリプト型の解決は Roslyn のフルコンパイル（数百 ms〜1 秒）になり得るため、
/// 必ず <see cref="Task.Run(Action)"/> で逃がし、結果だけを UI スレッドへ戻す。
///
/// 【参照ドロップの保留と混ぜない】
/// GET_ACTOR_COMPONENTS の応答は 1 本の経路で返ってくる。参照ドロップの保留
/// （<c>_pendingReference</c>）と同じ入れ物を使うと、候補の問い合わせが
/// ドロップの解決を横取り（またはその逆）してしまう。よって ScriptEvent 用の
/// 保留は完全に別の辞書で持ち、応答受信時にそれぞれ独立して掃き出す。
/// </summary>
public partial class InspectorPanel : IScriptEventCatalogProvider
{
    /// <summary>
    /// ScriptEvent の候補取得のために GET_ACTOR_COMPONENTS を待っている問い合わせ。
    /// キーは問い合わせ中のアクタ DFS ID、値はその応答を待っている継続処理の並び。
    ///
    /// 同じアクタへ複数のコンボが同時に問い合わせても IPC は 1 回で済ませる。
    /// 参照ドロップの保留（_pendingReference）とは別物であり、互いに影響しない。
    /// </summary>
    private readonly Dictionary<int, List<Action<ActorComponentSnapshot?>>> _pendingScriptEventQueries = new();

    // ── IScriptEventCatalogProvider 実装 ──────────────────────

    /// <summary>
    /// アクタ名から、そのアクタに付いている ScriptComponent のスクリプト型名一覧を返す。
    /// 解決できない場合（アクタが見つからない・応答が壊れている）は空リストで呼び戻す。
    /// </summary>
    public void RequestScriptTypes(string actorName, Action<IReadOnlyList<string>> onReady)
    {
        WithActorSnapshot(actorName, snapshot =>
        {
            if (snapshot is null)
            {
                onReady(Array.Empty<string>());
                return;
            }
            // コンパイルはバックグラウンドで（UI スレッドで回すと選択操作が固まる）
            CompileOnBackground(
                () => ScriptEventCatalog.ScriptTypesOnActor(snapshot, CompileScriptForCatalog),
                onReady);
        });
    }

    /// <summary>
    /// アクタ名とスクリプト型名から、結線先にできるメソッドの一覧を返す。
    /// 型がそのアクタに見つからない場合は空リストで呼び戻す。
    /// </summary>
    public void RequestMethods(
        string actorName, string scriptTypeName, Action<IReadOnlyList<ScriptEventMethod>> onReady)
    {
        WithActorSnapshot(actorName, snapshot =>
        {
            if (snapshot is null || string.IsNullOrEmpty(scriptTypeName))
            {
                onReady(Array.Empty<ScriptEventMethod>());
                return;
            }
            CompileOnBackground(
                () => ScriptEventCatalog.MethodsOf(
                          FindScriptType(snapshot, scriptTypeName)),
                onReady);
        });
    }

    // ── 内部ヘルパー ──────────────────────────────────────────

    /// <summary>
    /// アクタ名からコンポーネント構成のスナップショットを得て継続処理へ渡す。
    ///
    /// 直近の受信をためている <see cref="ActorComponentCache"/> にあれば即座に、
    /// 無ければ GET_ACTOR_COMPONENTS を送って応答受信時
    /// （<see cref="CompletePendingScriptEventQueries"/>）に呼び戻す。
    /// 名前を DFS ID へ引けない（アクタが存在しない）場合は null を渡す。
    /// </summary>
    private void WithActorSnapshot(string actorName, Action<ActorComponentSnapshot?> complete)
    {
        var dfsId = ActorRefJump.ActorDfsIdByName?.Invoke(actorName);
        if (dfsId is null)
        {
            complete(null);
            return;
        }

        if (ActorComponentCache.TryGet(dfsId.Value) is { } cached)
        {
            complete(cached);
            return;
        }

        if (_runtime is null)
        {
            complete(null);
            return;
        }

        // 同じアクタへの問い合わせが既に飛んでいれば、IPC は送らず継続だけを積む
        if (_pendingScriptEventQueries.TryGetValue(dfsId.Value, out var waiters))
        {
            waiters.Add(complete);
            return;
        }
        _pendingScriptEventQueries[dfsId.Value] = new List<Action<ActorComponentSnapshot?>> { complete };
        _runtime.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId.Value}");
    }

    /// <summary>
    /// ACTOR_COMPONENTS の応答で、ScriptEvent の候補取得を待っている問い合わせを掃き出す。
    ///
    /// 参照ドロップの解決（<c>ResolvePendingReference</c>）とは独立に動く。
    /// 呼び出し元（OnActorComponentsReceived）は応答の DFS ID と解析済みスナップショットを渡すこと。
    /// </summary>
    /// <param name="dfsId">応答が示すアクタの DFS ID（取得できなければ負値）。</param>
    /// <param name="snapshot">解析済みのスナップショット（解析できなければ null）。</param>
    private void CompletePendingScriptEventQueries(int dfsId, ActorComponentSnapshot? snapshot)
    {
        if (dfsId < 0) return;
        if (!_pendingScriptEventQueries.TryGetValue(dfsId, out var waiters)) return;

        // 継続の中から再び問い合わせが積まれても壊れないよう、先に取り外す
        _pendingScriptEventQueries.Remove(dfsId);
        foreach (var waiter in waiters) waiter(snapshot);
    }

    /// <summary>
    /// 重い算出をバックグラウンドで行い、結果を UI スレッドで受け渡す。
    /// 例外はログへ落として空の結果を返す（候補が出ないだけで、保存値は壊さない）。
    /// </summary>
    /// <typeparam name="T">結果の要素型。</typeparam>
    /// <param name="compute">バックグラウンドで走らせる算出処理。</param>
    /// <param name="onReady">UI スレッドで結果を受け取るコールバック。</param>
    private static void CompileOnBackground<T>(
        Func<IReadOnlyList<T>> compute, Action<IReadOnlyList<T>> onReady)
    {
        Task.Run(() =>
            {
                try { return compute(); }
                catch (Exception ex)
                {
                    EditorLog.Write($"InspectorPanel: ScriptEvent の候補取得に失敗: {ex.Message}");
                    return (IReadOnlyList<T>)Array.Empty<T>();
                }
            })
            .ContinueWith(t => onReady(t.Result),
                          TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// スナップショット内の ScriptComponent スロットから、指定の型名を持つスクリプト型を探す。
    /// バックグラウンドスレッドから呼ばれる（UI 要素には触れない）。
    /// </summary>
    private Type? FindScriptType(ActorComponentSnapshot snapshot, string scriptTypeName)
    {
        foreach (var comp in snapshot.Components)
        {
            if (comp.TypeId != ReferenceKindCatalog.ScriptComponentTypeId) continue;
            if (string.IsNullOrEmpty(comp.ScriptPath)) continue;

            var type = CompileScriptForCatalog(comp.ScriptPath);
            if (type is not null && type.Name == scriptTypeName) return type;
        }
        return null;
    }

    /// <summary>
    /// 候補算出用のスクリプト型解決（バックグラウンドスレッドから呼ばれる）。
    ///
    /// 実体は <see cref="GetOrCompileScript"/>（仮想パスの絶対化と
    /// <c>_scriptTypeCache</c> への登録まで面倒を見る）。キャッシュは
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> なので
    /// UI スレッドと並行に触っても壊れない。
    /// コンパイルエラー中のスクリプトは null が返り、候補から落ちる。
    /// </summary>
    private Type? CompileScriptForCatalog(string scriptPath)
    {
        try { return GetOrCompileScript(scriptPath); }
        catch (Exception ex)
        {
            EditorLog.Write($"InspectorPanel: ScriptEvent 候補のコンパイルに失敗 [{scriptPath}]: {ex.Message}");
            return null;
        }
    }
}
