using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ScreenStack.Prewarm.cs — 画面の作り置き（2026-10-02。lane3。docs/ui_navigation.md §2.8。backlog W3-6 (1)）
//
//  Prewarm(prefab) は、空いた時間（出入りの動きの無い間）に、隠した枠（FramePrefab。Screens の下）の中で画面のプレハブを組み立てておく。
//  次にそのプレハブを積む（Push・Replace・SetRoot・覆われて手放した画面の作り直し）とき、CreateInstance が枠ごと借りる
//  （中身ができあがっていれば次のフレームで Ready → OnScreenEnter → 落ち着いて出入りが始まる。プレハブの組み立ての重いフレームが無い）。
//  段階の移り変わりは純粋な PrewarmSlot（Model/PrewarmSlot.cs）が決め、ここはエンジンの操作（作る・見せる・隠す・付け替える）に置き換えるだけ。
//  - 温まった: できあがって PrewarmSlot.SettleFrames 経ち、画面のスクリプトの IsPrewarmReady が true（上限 MaxWaitFrames）
//  - 温め描き（PrewarmOptions.WarmDrawFrames > 0）: 枠をスタックの段 0 より奥のレイヤー（−1 段）で見せて、文字の字形を焼いておく
//  - 使い終わった後（PrewarmMode）: Once は画面と一緒に消える・Refill は空いた時間に作り直す・Reuse は枠ごと隠して戻す（作り直さない）
//  - 隠した枠は戻るの登録簿に「どの段でもない枠」として載せる（中の入れ子のスタックが戻るを受けない）
// ============================================================

public sealed partial class ScreenStack
{
    /// <summary>作り置きの隠した枠を登録簿に載せるときの段の番号（どの段とも重ならない。段の番号は 1 から）。</summary>
    private const int PrewarmEntryId = -1;

    /// <summary>温め描きで枠を置く深さ（スタックの 1 段の何段ぶん奥か。段 0〈根の画面〉の不透明な背景の下）。</summary>
    private const int WarmDrawDepthSteps = 1;

    /// <summary>作り置き 1 つの実体（段階は Slot。枠と中身のノード）。</summary>
    private sealed class PrewarmEntry
    {
        /// <summary>段階（純粋な状態機械）。</summary>
        public required PrewarmSlot Slot { get; init; }
        /// <summary>隠した枠（作り始めるまで・作り直しを待つ間は無効）。</summary>
        public GameObject Frame;
        /// <summary>中身（枠ができるまで無効）。</summary>
        public GameObject Content;
        /// <summary>中身を作ったか。</summary>
        public bool ContentRequested;
        /// <summary>枠ができあがったのを見たか（まだできていないのと、消えたのを見分ける）。</summary>
        public bool FrameSeen;
        /// <summary>中身を枠の Body（安全領域の中）に置いたか。</summary>
        public bool ContentInBody;
    }

    /// <summary>貸すときに CreateInstance へ渡すもの（枠・中身・始める段階）。</summary>
    private sealed record PrewarmLease(PrewarmEntry Entry, GameObject Frame, GameObject Content, BuildPhase Phase);

    /// <summary>プレハブ → 作り置き。</summary>
    private readonly Dictionary<string, PrewarmEntry> _prewarm = new(StringComparer.Ordinal);

    // ── 公開の口 ────────────────────────────────────────────

    /// <summary>
    /// 画面を事前に作っておく（作り置き）。空いた時間（出入りの動きの無い間）に隠した枠の中で組み立て、次にそのプレハブを積むときに使う。
    /// 同じプレハブの作り置きが既にあれば何もしない（false）。
    /// </summary>
    /// <param name="prefab">画面のプレハブ（assets:// の .actor）。</param>
    /// <param name="options">指定（使い終わった後の扱い・温め描き・安全領域。null = 既定 = 1 回だけ・描かない・安全領域の中）。</param>
    /// <returns>作り置きを始めたら true。</returns>
    public bool Prewarm(string prefab, PrewarmOptions? options = null)
    {
        if (string.IsNullOrEmpty(prefab))
        {
            Debug.LogWarning($"{LogPrefix} {gameObject.Name} prewarm: プレハブが空です");
            return false;
        }
        if (_prewarm.TryGetValue(prefab, out var existing) && existing.Slot.Stage != PrewarmStage.Discarded) return false;
        var slot = new PrewarmSlot(prefab, options);
        _prewarm[prefab] = new PrewarmEntry { Slot = slot };
        Debug.Log($"{LogPrefix} {gameObject.Name} prewarm {prefab} mode={slot.Options.Mode} warmDraw={slot.Options.WarmDrawFrames}");
        Redraw.Request();
        return true;
    }

    /// <summary>作り置きが貸せる状態（温まって隠してある）か。</summary>
    /// <param name="prefab">画面のプレハブ。</param>
    public bool IsPrewarmed(string prefab) => _prewarm.TryGetValue(prefab, out var entry) && entry.Slot.Stage == PrewarmStage.Ready;

    /// <summary>作り置きの段階（無ければ null。診断・確かめ用）。</summary>
    /// <param name="prefab">画面のプレハブ。</param>
    public PrewarmStage? GetPrewarmStage(string prefab) => _prewarm.TryGetValue(prefab, out var entry) ? entry.Slot.Stage : null;

    /// <summary>
    /// 作り置きを捨てる（隠した枠を消す。貸している最中なら、その画面が外れたときに一緒に消える）。
    /// </summary>
    /// <param name="prefab">画面のプレハブ。</param>
    /// <returns>捨てたら true（無ければ false）。</returns>
    public bool DiscardPrewarm(string prefab)
    {
        if (!_prewarm.Remove(prefab, out var entry)) return false;
        bool lent = entry.Slot.Stage == PrewarmStage.Lent;
        entry.Slot.Discard();
        if (!lent) DestroyPrewarmNodes(entry);
        Debug.Log($"{LogPrefix} {gameObject.Name} prewarm discard {prefab}{(lent ? "（貸している画面が外れたら消える）" : string.Empty)}");
        return true;
    }

    // ── 毎フレーム ──────────────────────────────────────────

    /// <summary>作り置きを 1 フレーム進める（OnWidgetUpdate から。AdvanceBuilds の後）。</summary>
    private void AdvancePrewarm()
    {
        if (_prewarm.Count == 0) return;
        foreach (var entry in new List<PrewarmEntry>(_prewarm.Values))
        {
            if (entry.Slot.Stage is PrewarmStage.Lent or PrewarmStage.Discarded) continue;
            if (DetectLost(entry)) continue;
            if (NavNode.IsBuilt(entry.Frame)) entry.FrameSeen = true;
            if (!RequestPrewarmContent(entry)) continue;
            bool built = entry.ContentRequested && NavNode.IsBuilt(entry.Content);
            // 画面のスクリプトが無い（まだ始まっていない）中身は、できあがってから SettleFrames 待てば温まったとみなす
            bool screenReady = !built || Of<UiScreen>(entry.Content) is not { } screen || screen.IsPrewarmReady;
            ApplyPrewarmAction(entry, entry.Slot.Tick(IsTransitioning, built, screenReady));
            // 作っている・描いている間は次のフレームを描かせる（on_demand でも進める）
            if (entry.Slot.Stage is PrewarmStage.Building or PrewarmStage.WarmDrawing) Redraw.Request();
        }
    }

    /// <summary>
    /// できあがったのを見た後で枠が無くなった（シーンの切り替え・誰かが消した）か。無くなったら段階を戻す（Once は捨てる）。
    /// </summary>
    /// <returns>無くなっていたら true（このフレームはそれ以上進めない）。</returns>
    private bool DetectLost(PrewarmEntry entry)
    {
        if (!entry.FrameSeen || NavNode.IsBuilt(entry.Frame)) return false;
        Debug.LogWarning($"{LogPrefix} {gameObject.Name} 作り置きの枠が消えました（{entry.Slot.Prefab}・{entry.Slot.Stage}）");
        ClearPrewarmNodes(entry);
        if (entry.Slot.Lost() == PrewarmStage.Discarded) _prewarm.Remove(entry.Slot.Prefab);
        Redraw.Request();
        return true;
    }

    /// <summary>
    /// 枠ができたら中身を作る（1 回。安全領域の指定に従って Body か枠の直下）。作れなければ作り置きを捨てる。
    /// </summary>
    /// <returns>続けて進めてよければ true（作れずに捨てたら false）。</returns>
    private bool RequestPrewarmContent(PrewarmEntry entry)
    {
        if (entry.Slot.Stage != PrewarmStage.Building || entry.ContentRequested || !NavNode.IsBuilt(entry.Frame)) return true;
        var body = entry.Slot.Options.SafeArea ? entry.Frame.FindChild(BodyChild) : new GameObject(Entity.None);
        var parent = body.IsValid ? body : entry.Frame;
        // 枠の背景は不透明の画面と同じ色（温め描きで見える所は奥なので、色はどちらでも利用者には見えない）
        NavNode.SetSpriteColor(entry.Frame, Theme.Color(UiTokens.ColorBackground));
        entry.Content = GameObject.Instantiate(entry.Slot.Prefab, parent);
        entry.ContentRequested = true;
        entry.ContentInBody = body.IsValid;
        if (entry.Content.IsValid) return true;
        Debug.LogError($"{LogPrefix} {gameObject.Name} 作り置きの中身を作れません: {entry.Slot.Prefab}（作り置きを捨てます）");
        DiscardPrewarm(entry.Slot.Prefab);
        return false;
    }

    /// <summary>作り置きの段階の移り変わりが決めたことをする（作り始める・温め描きを始める・終える）。</summary>
    private void ApplyPrewarmAction(PrewarmEntry entry, PrewarmAction action)
    {
        switch (action)
        {
            case PrewarmAction.StartBuild:
                StartPrewarmBuild(entry);
                break;
            case PrewarmAction.BeginWarmDraw:
                // 段 0 の画面（不透明な背景）より奥のレイヤーで見せる（利用者には見えない。描いた字形はアトラスに残る）
                NavNode.SetBias(entry.Frame, -UiLayers.StackStep(Theme, LayerStep) * WarmDrawDepthSteps);
                NavNode.SetVisible(entry.Frame, true);
                LogPrewarmReady(entry, $"温め描き {entry.Slot.Options.WarmDrawFrames} フレーム");
                break;
            case PrewarmAction.EndWarmDraw:
                ResetPrewarmFrame(entry.Frame);
                Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きの温め描きを終えた（貸せる）: {entry.Slot.Prefab}");
                break;
            case PrewarmAction.BecameReady:
                LogPrewarmReady(entry, "貸せる");
                break;
        }
    }

    /// <summary>隠した枠を Screens の下に作り始める（中身は枠ができた次のフレーム）。</summary>
    private void StartPrewarmBuild(PrewarmEntry entry)
    {
        ResolveChildren();
        entry.Frame = GameObject.Instantiate(FramePrefab, _screens);
        entry.Content = new GameObject(Entity.None);
        entry.ContentRequested = false;
        entry.FrameSeen = false;
        if (!entry.Frame.IsValid)
        {
            Debug.LogError($"{LogPrefix} {gameObject.Name} 作り置きの枠を作れません: {FramePrefab}（作り置きを捨てます）");
            DiscardPrewarm(entry.Slot.Prefab);
            return;
        }
        entry.Frame.Visible = false;
        // どの段でもない枠として載せる（中の入れ子のスタックは「上の段の中」にならないので戻るを受けない）
        NavigatorRegistry.RegisterFrame(entry.Frame, this, PrewarmEntryId);
        Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きを作り始めた: {entry.Slot.Prefab}");
    }

    /// <summary>温まったことのログ（待つ上限を過ぎて進めたなら警告）。</summary>
    private void LogPrewarmReady(PrewarmEntry entry, string what)
    {
        if (entry.Slot.TimedOut)
            Debug.LogWarning($"{LogPrefix} {gameObject.Name} 作り置きが {PrewarmSlot.MaxWaitFrames} フレームで温まりません（IsPrewarmReady）。{what}: {entry.Slot.Prefab}");
        else
            Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きが温まった（{entry.Slot.BuiltFrames} フレーム）。{what}: {entry.Slot.Prefab}");
    }

    // ── 貸す・返す ──────────────────────────────────────────

    /// <summary>そのプレハブの作り置きを貸せるか（作っている途中・温め描きの途中・貸せる。枠がある）。</summary>
    private bool CanLeasePrewarm(string prefab) =>
        _prewarm.TryGetValue(prefab, out var entry) && entry.Slot.CanLend && entry.Frame.IsValid;

    /// <summary>
    /// 作り置きを枠ごと貸す（CreateInstance から）。温め描きの途中なら打ち切り、前に使ったときの見た目の上書きを戻す。
    /// 中身ができていて、安全領域の指定が作ったときと違えば付け替える。
    /// </summary>
    /// <param name="screen">積む段。</param>
    /// <returns>貸すもの（貸せなければ null）。</returns>
    private PrewarmLease? LeasePrewarm(ScreenEntry screen)
    {
        if (!_prewarm.TryGetValue(screen.Prefab, out var entry) || !entry.Frame.IsValid) return null;
        var from = entry.Slot.Stage;
        if (!entry.Slot.Lend()) return null;
        ResetPrewarmFrame(entry.Frame);
        var phase = BuildPhase.FrameRequested;
        var content = new GameObject(Entity.None);
        if (entry.ContentRequested && entry.Content.IsValid)
        {
            MoveContentForSafeArea(entry, screen.Options.SafeArea);
            content = entry.Content;
            // できあがっている中身は次のフレームで画面のスクリプトを探せる（Ready）。作っている途中ならできあがりを待つ
            phase = NavNode.IsBuilt(entry.Content) ? BuildPhase.Ready : BuildPhase.ContentRequested;
        }
        Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きを貸した（{entry.Slot.LendCount} 回目・{from}）: {screen}");
        return new PrewarmLease(entry, entry.Frame, content, phase);
    }

    /// <summary>中身の置き場（Body か枠の直下）を積む指定に合わせる（違えば付け替える）。</summary>
    private static void MoveContentForSafeArea(PrewarmEntry entry, bool safeArea)
    {
        var body = safeArea ? entry.Frame.FindChild(BodyChild) : new GameObject(Entity.None);
        bool inBody = body.IsValid;
        if (inBody == entry.ContentInBody) return;
        entry.Content.SetParent(inBody ? body : entry.Frame);
        entry.ContentInBody = inBody;
    }

    /// <summary>
    /// 借りた作り置きの画面が外れた（DestroyEntry の ReleaseContent から）。Reuse で今も同じ作り置きなら、枠を隠して見た目の上書きを戻し、
    /// どの段でもない枠として載せ直して作り置きへ戻す（枠を消さない）。それ以外は枠と一緒に消える（Refill は空いた時間に作り直す）。
    /// </summary>
    /// <returns>枠を消さずに残すなら true。</returns>
    private bool ReturnPrewarm(PrewarmEntry entry, Instance instance)
    {
        var stage = entry.Slot.Return();
        bool current = _prewarm.TryGetValue(entry.Slot.Prefab, out var now) && ReferenceEquals(now, entry);
        if (stage != PrewarmStage.Ready || !current || !instance.Frame.IsValid)
        {
            if (current && stage == PrewarmStage.Discarded) _prewarm.Remove(entry.Slot.Prefab);
            if (current && stage == PrewarmStage.Waiting) ClearPrewarmNodes(entry);
            Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きの画面が外れた（{entry.Slot.Options.Mode} → {stage}）: {instance.Entry}");
            return false;
        }
        ResetPrewarmFrame(instance.Frame);
        NavigatorRegistry.RegisterFrame(instance.Frame, this, PrewarmEntryId);
        // 押している最中の指が隠した中身へ残らないよう取り消し、画面のスクリプトから古い手札を外す
        if (instance.Content.IsValid) instance.Content.CancelGestures();
        DetachScreen(instance);
        entry.Frame = instance.Frame;
        entry.Content = instance.Content;
        entry.ContentRequested = instance.Content.IsValid;
        entry.FrameSeen = true;
        entry.ContentInBody = instance.Entry.Options.SafeArea && instance.Frame.FindChild(BodyChild).IsValid;
        Debug.Log($"{LogPrefix} {gameObject.Name} 作り置きへ戻した（使い回す）: {instance.Entry}");
        return true;
    }

    /// <summary>枠を隠し、実行中の見た目の上書き（底上げ・ずらし・倍率）を戻す（温め描きの終わり・貸す前・戻したとき）。</summary>
    private static void ResetPrewarmFrame(GameObject frame)
    {
        NavNode.SetVisible(frame, false);
        NavNode.SetBias(frame, 0);
        NavNode.SetFraction(frame, Vector2.Zero);
        NavNode.SetTranslate(frame, Vector2.Zero);
        NavNode.SetVisualScale(frame, Vector2.One);
    }

    /// <summary>作り置きのノードを消す（隠した枠ごと。登録簿からも外す）。</summary>
    private static void DestroyPrewarmNodes(PrewarmEntry entry)
    {
        if (entry.Frame.IsValid)
        {
            NavigatorRegistry.UnregisterFrame(entry.Frame);
            entry.Frame.Destroy();
        }
        ClearPrewarmNodes(entry);
    }

    /// <summary>作り置きのノードの参照を空にする（ノードは消さない。消えた後・画面と一緒に消える後）。</summary>
    private static void ClearPrewarmNodes(PrewarmEntry entry)
    {
        entry.Frame = new GameObject(Entity.None);
        entry.Content = new GameObject(Entity.None);
        entry.ContentRequested = false;
        entry.FrameSeen = false;
        entry.ContentInBody = false;
    }

    /// <summary>部品が消えるとき: 作り置きの枠を登録簿から外して忘れる（枠はスタックの子なので一緒に消える）。</summary>
    private void ForgetPrewarm()
    {
        foreach (var entry in _prewarm.Values)
            if (entry.Frame.IsValid) NavigatorRegistry.UnregisterFrame(entry.Frame);
        _prewarm.Clear();
    }
}
