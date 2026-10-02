using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ScreenStack.cs — 画面のスタック（W2-7。docs/ui_navigation.md §2）
//
//  【作り】スタックのノード（templates/ui/prefabs/screen_stack.actor）:
//      ScreenStack（Canvas・CanvasLayoutItem〈親に合わせる〉・CanvasClip・このスクリプト）
//      ├─ Screens（Canvas・親に合わせる）…… 画面の枠をここへ積む
//      ├─ Veil（Sprite = 背景の色・親に合わせる。隠す）…… フェードの幕
//      └─ Blocker（Sprite〈透明〉・受けるジェスチャーの無い CanvasGesture＝遮る板。隠す）…… 出入りの間の入力を止める
//  画面 1 つ = 枠（screen_frame.actor: Canvas・親に合わせる・背景の Sprite・遮る板の CanvasGesture）の下の
//  Body（安全領域の中）に画面のプレハブを作る（ScreenOptions.SafeArea = false なら枠の直下）。
//  枠の背景は画面の端まで塗り（不透明の画面は color.background）、画面の根は安全領域の中に入る。
//
//  【積み下ろし】Push・Pop・Replace・PopToRoot・SetRoot は並び（ScreenStackModel）をすぐ変え、出入りの計画を順に動かす。
//  動いている途中に次の操作が来たら、今の動きを終わりまで飛ばして次を始める。作る画面の実体（枠・中身）は 2 フレームで
//  できあがる（枠を作る → 枠の Body の下へ中身を作る）。できあがるまで枠は隠しておく（最初の 1 フレームがちらつかない）。
//  【出入りの時計】（遷移の時計の直し。2026-09-30。TransitionClock）入ってくる画面が落ち着く（画面のスクリプトへ Enter を届けて
//  1 フレーム描いた。ContentSettleGate）まで時計を止め、動かし始めたフレームの経過は数えず、1 フレームで足す経過に上限
//  （MotionStep.MaxFrameSeconds）を置く。組み立て・Enter の重いフレームで動きが飛ばず、その分だけ長くかかる。
//
//  【見せ方・遮り・描かない】
//    - 段 i の枠のレイヤーの底上げ = i × LayerStep（上の画面の板が下の画面の文字より手前に出る。UiLayers）
//    - 出入りの間は Blocker を見せて入力を止める（動いている画面のボタンを押させない。入ってくる画面が落ち着くのを待つ間も）
//    - 落ち着いたら、不透明な上の画面より下の枠を隠す（描かない・当たり判定に出ない＝入力を受けない）。
//      KeepState = false の画面は実体を手放し、戻ってきたら作り直す
//    - 動いている間だけ Redraw.KeepAlive（on_demand でも動きの途中で止まらず、終わったら 10 フレームで止まる）
//  【戻る】ナビゲーター（INavigator）として戻るの段に登録する: 上の画面の ScreenOptions.IgnoreBack・UiScreen.OnBackPressed
//  → 根より上なら 1 つ下ろす → 根なら受けない（外のタブ・アプリへ）。
//  予測型の戻る（W2 の手直し 3b）の問い・プレビュー（上の画面を縮めて下の画面を見せ、確定したらその姿勢から下ろす）は
//  ScreenStack.BackPreview.cs。
//  【フォーカス】画面の枠ごとにフォーカスの範囲（FocusScope）を作り、上の画面の範囲を前へ出す。
//  【中身の出所】（2026-10-02。lane3）渡された組み立て済みの中身（Push(GameObject)）・置いてある根（RootAdoptChild）・
//  作り置き（Prewarm）・プレハブの順に決める（ScreenContentPlan）。渡された中身は ScreenStack.Content.cs、作り置きは ScreenStack.Prewarm.cs。
// ============================================================

/// <summary>画面のスタック（画面を積む・戻す・置き換える・根まで戻る）。</summary>
public sealed partial class ScreenStack : UiWidget, INavigator
{
    /// <summary>枠のプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultFramePrefab = "assets://ui/prefabs/screen_frame.actor";
    /// <summary>枠の中の、安全領域の中の子の名前。</summary>
    private const string BodyChild = "Body";
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] nav:";
    /// <summary>実体ができあがるのを待つフレームの上限（これを過ぎたら中身なしで進める・警告）。</summary>
    private const int MaxBuildWaitFrames = 120;
    /// <summary>画面のスクリプト（UiScreen）を探し続けるフレームの上限（付いていないプレハブのため）。</summary>
    private const int MaxScreenLookupFrames = 30;
    /// <summary>KeepAlive に足す余り（秒。最後のフレームを描き切る）。</summary>
    private const float KeepAliveMargin = 0.05f;
    /// <summary>置き換えの入ってくる画面の底上げに足す割合（段の半分。出ていく同じ段の画面より手前）。</summary>
    private const float ReplaceBiasFraction = 0.5f;

    /// <summary>最初の根の画面のプレハブ（空なら積まない。スクリプトから SetRoot してよい）。</summary>
    [SerializeField(Label = "根の画面")]
    public string RootPrefab = "";

    /// <summary>根の画面を安全領域の中に置くか（false = 端まで。タブのバーやヘッダーが自分で安全領域を扱うシェル）。</summary>
    [SerializeField(Label = "根を安全領域の中に")]
    public bool RootSafeArea = true;

    /// <summary>
    /// 根の画面として引き取る、画面を積む子（<see cref="ScreensChild"/>）の下に<b>あらかじめ置いてある子</b>の名前（空なら使わない）。
    /// シーンにプレハブのインスタンスを置いておけば、Edit でも実行時と同じ見た目になり、
    /// 実行時はそれを <see cref="RootPrefab"/> から新しく作る代わりに根として使う（中身は作り直さない）。
    /// 見つからないときは従来どおり <see cref="RootPrefab"/> から作る（docs/ui_navigation.md §2.7「置いてある根」）。
    /// </summary>
    [SerializeField(Label = "置いてある根の子")]
    public string RootAdoptChild = "";

    /// <summary>画面の枠のプレハブ。</summary>
    [SerializeField(Label = "枠のプレハブ")]
    public string FramePrefab = DefaultFramePrefab;

    /// <summary>指定の無い画面の出入りの種類。</summary>
    [SerializeField(Label = "既定の出入り")]
    public NavTransition DefaultTransition = NavTransition.Push;

    /// <summary>1 段のレイヤーの底上げ（0 = テーマの layer.stack_step。タブの中のスタックは小さめ〈例 1000〉にする）。</summary>
    [SerializeField(Label = "1 段のレイヤー")]
    public int LayerStep;

    /// <summary>画面の枠を積む子の名前。</summary>
    [SerializeField(Label = "画面を積む子")]
    public string ScreensChild = "Screens";

    /// <summary>フェードの幕の子の名前。</summary>
    [SerializeField(Label = "幕の子")]
    public string VeilChild = "Veil";

    /// <summary>出入りの間の遮る板の子の名前。</summary>
    [SerializeField(Label = "遮る板の子")]
    public string BlockerChild = "Blocker";

    /// <summary>画面が変わった（落ち着いた後。上の画面の手札は Top）。</summary>
    public event Action<ScreenStack>? Changed;

    /// <summary>画面の実体（枠・中身）の作りかけの段階。</summary>
    private enum BuildPhase
    {
        /// <summary>枠を作った（できあがり待ち）。</summary>
        FrameRequested,
        /// <summary>中身を作った（できあがり待ち）。</summary>
        ContentRequested,
        /// <summary>できあがった。</summary>
        Ready,
    }

    /// <summary>画面 1 段の実体。</summary>
    private sealed class Instance
    {
        public required ScreenEntry Entry { get; init; }
        public required ScreenHandle Handle { get; init; }
        public required GameObject Frame { get; init; }
        public GameObject Content;
        public BuildPhase Phase;
        public int WaitFrames;
        public int LookupFrames;
        public bool Entered;
        /// <summary>落ち着いたか（Enter を届けて 1 フレーム描いた。出入りの時計を進めてよいか。遷移の時計の直し）。</summary>
        public ContentSettleGate Settle;
        public FocusScope? Scope;
        public UiScreen? Screen;
        /// <summary>中身の出所（2026-10-02。置いてある根が見つからなければ Prefab へ落ちる）。</summary>
        public ScreenContentSource Source;
        /// <summary>渡された中身の扱い（Push(GameObject)。それ以外は null）。</summary>
        public SuppliedContent? Supplied;
        /// <summary>借りた作り置き（Prewarm。それ以外は null）。</summary>
        public PrewarmEntry? Prewarm;
    }

    /// <summary>1 回の出入り（計画と動きの状態）。</summary>
    private sealed class Run
    {
        public required ScreenChange Change { get; init; }
        /// <summary>始まりの置き方を当てたか（実体ができあがった。時計は入ってくる画面が落ち着くまで止まっていることがある）。</summary>
        public bool Started;
        /// <summary>動きの時計（落ち着くまで止める・動かし始めたフレームは数えない・1 フレームの上限。Begin で作り直す）。</summary>
        public TransitionClock Clock = new(0f);
        public UiCurve Curve;
        public float Parallax;
        /// <summary>予測型の戻るのプレビューで見せていた下の画面へ戻る（戻る画面は視差の位置から動かさず、最初から見せたまま。3b）。</summary>
        public bool FromPreview;
    }

    /// <summary>並びと状態。</summary>
    private readonly ScreenStackModel _model = new();
    /// <summary>段の番号 → 実体（手放した段には無い）。</summary>
    private readonly Dictionary<int, Instance> _instances = new();
    /// <summary>段の番号 → 手札（手放しても残る）。</summary>
    private readonly Dictionary<int, ScreenHandle> _handles = new();
    /// <summary>これから動かす出入り（古い順）。</summary>
    private readonly Queue<Run> _runs = new();
    /// <summary>今動かしている出入り。</summary>
    private Run? _current;
    /// <summary>画面を積む子・幕・遮る板。</summary>
    private GameObject _screens, _veil, _blocker;
    /// <summary>子を引いたか。</summary>
    private bool _childrenResolved;

    /// <summary>段の数。</summary>
    public int Depth => _model.Count;
    /// <summary>いちばん上の画面の手札（無ければ null）。</summary>
    public ScreenHandle? Top => _model.Top is { } top && _handles.TryGetValue(top.Id, out var h) ? h : null;
    /// <summary>下ろせるか（根より上に段がある）。</summary>
    public bool CanPop => _model.CanPop;
    /// <summary>出入りが動いている・待っているか。</summary>
    public bool IsTransitioning => _current is not null || _runs.Count > 0;

    // ── 操作 ────────────────────────────────────────────────

    /// <summary>画面を積む。</summary>
    /// <param name="prefab">画面のプレハブ（assets:// の .actor）。</param>
    /// <param name="transition">出入りの種類（null = 指定 → スタックの既定）。</param>
    /// <param name="args">画面へ渡す値（UiScreen.OnScreenEnter）。</param>
    /// <param name="options">画面ごとの指定（不透明・状態を保つ・戻るを無視・安全領域）。</param>
    public ScreenHandle Push(string prefab, NavTransition? transition = null, object? args = null, ScreenOptions? options = null)
    {
        var change = _model.Push(prefab, WithTransition(options, transition), args, DefaultTransition);
        return Enqueue(change, "push");
    }

    /// <summary>いちばん上の画面を下ろす（根だけなら false）。結果は下ろした画面の手札へ。</summary>
    public bool Pop(object? result = null)
    {
        var change = _model.Pop(result, DefaultTransition);
        if (change is null) return false;
        Enqueue(change, "pop");
        return true;
    }

    /// <summary>いちばん上の画面を置き換える（下ろした画面の手札は結果 null で閉じる）。</summary>
    public ScreenHandle Replace(string prefab, NavTransition? transition = null, object? args = null, ScreenOptions? options = null)
    {
        var change = _model.Replace(prefab, WithTransition(options, transition), args, DefaultTransition);
        return Enqueue(change, "replace");
    }

    /// <summary>根まで下ろす（根だけなら false）。</summary>
    public bool PopToRoot()
    {
        var change = _model.PopToRoot(DefaultTransition);
        if (change is null) return false;
        Enqueue(change, "pop_to_root");
        return true;
    }

    /// <summary>根からやり直す（旧い画面はすべて閉じる）。</summary>
    public ScreenHandle SetRoot(string prefab, NavTransition transition = NavTransition.None, object? args = null, ScreenOptions? options = null)
    {
        var change = _model.SetRoot(prefab, options, args, transition);
        return Enqueue(change, "set_root");
    }

    /// <summary>手札の画面がいちばん上なら下ろす（UiScreen.Close から）。</summary>
    public bool Close(ScreenHandle handle, object? result = null)
    {
        if (_model.Top is not { } top || top.Id != handle.Id) return false;
        return Pop(result);
    }

    /// <summary>段の番号の画面がいちばん上か（戻るの段・フォーカスが祖先をたどるときに使う）。</summary>
    internal bool IsTopEntry(int entryId) => _model.Top?.Id == entryId;

    /// <summary>
    /// 手札の画面の段の添字（根 = 0。積まれていない・外れた画面は −1。2026-10-02。積んだ時点で並びは変わるので、Push の直後でも分かる）。
    /// </summary>
    /// <param name="handle">画面の手札。</param>
    public int IndexOf(ScreenHandle handle) =>
        handle is not null && _model.Find(handle.Id) is { } entry && _handles.TryGetValue(entry.Id, out var h) && ReferenceEquals(h, handle)
            ? _model.IndexOf(entry)
            : -1;

    /// <summary>画面の枠を積む子（無ければスタック自身。ModalHost.Park が面を置く底上げの起点に使う）。</summary>
    internal GameObject ScreensNode
    {
        get
        {
            ResolveChildren();
            return _screens;
        }
    }

    /// <summary>1 段のレイヤーの底上げ（LayerStep が 0 以下ならテーマの layer.stack_step）。</summary>
    internal int EffectiveLayerStep => UiLayers.StackStep(Theme, LayerStep);

    /// <summary>上の画面のフォーカスの範囲を前へ出す（外のスタックの積み下ろしが終わった後。NavigatorRegistry から）。</summary>
    internal void BringTopScopeToFront()
    {
        if (Get(_model.Top) is { Scope: { } scope }) UiFocus.BringToFront(scope);
    }

    /// <summary>
    /// スタックごと見せた・隠した（タブの切り替え。TabHost から）: 上の画面へ知らせ、フォーカスの範囲を前へ出す・後ろへ回す。
    /// </summary>
    internal void NotifyShownByHost(bool shown)
    {
        if (Get(_model.Top) is not { } top) return;
        if (shown)
        {
            UiFocus.BringToFront(top.Scope);
            NavigatorRegistry.BringNestedToFront(top.Frame);
            if (top.Entered) top.Screen?.NotifyShown();
        }
        else
        {
            UiFocus.SendToBack(top.Scope);
            if (top.Entered) top.Screen?.OnScreenHidden();
        }
    }

    /// <summary>指定に出入りの種類を重ねる（引数の種類が優先）。</summary>
    private static ScreenOptions? WithTransition(ScreenOptions? options, NavTransition? transition)
    {
        if (transition is null) return options;
        var o = options ?? ScreenOptions.Default;
        return new ScreenOptions { Transition = transition, Opaque = o.Opaque, KeepState = o.KeepState, IgnoreBack = o.IgnoreBack, SafeArea = o.SafeArea };
    }

    /// <summary>出入りを積み、入ってくる画面の手札を返す（無ければ作る）。</summary>
    private ScreenHandle Enqueue(ScreenChange change, string op)
    {
        var incoming = change.Incoming!;
        if (!_handles.TryGetValue(incoming.Id, out var handle))
        {
            handle = new ScreenHandle(incoming.Id, incoming.Prefab);
            _handles[incoming.Id] = handle;
        }
        _runs.Enqueue(new Run { Change = change, FromPreview = IsPreviewedPop(change) });
        Debug.Log($"{LogPrefix} {gameObject.Name} {op} in={incoming} out={change.Outgoing?.ToString() ?? "-"} {change.Transition} depth={_model.Count}");
        Redraw.Request();
        return handle;
    }

    // ── 部品の土台 ──────────────────────────────────────────

    /// <inheritdoc />
    GameObject INavigator.NavigatorNode => Owner;

    /// <summary>スタックのノード（登録簿が祖先をたどる起点）。</summary>
    internal GameObject NavigatorNodeInternal => Owner;

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        NavigatorRegistry.Register(this);
        if (RootPrefab.Length > 0 && _model.Count == 0) SetRoot(RootPrefab, options: new ScreenOptions { SafeArea = RootSafeArea });
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy()
    {
        // 予測型の戻るのプレビューの相手はこれで無効になる（BackPreview が次のフレームで手放す。3b）
        _destroyed = true;
        NavigatorRegistry.Unregister(this);
        foreach (var instance in _instances.Values)
        {
            NavigatorRegistry.UnregisterFrame(instance.Frame);
            UiFocus.RemoveScope(instance.Scope);
        }
        _instances.Clear();
        // 作り置きの隠した枠も登録簿から外す（枠そのものはスタックの子なので一緒に消える）
        ForgetPrewarm();
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        BackDispatcher.PollBackKey();
        ResolveChildren();
        float step = Time.UnscaledDeltaTime;
        AdvanceBuilds();
        // 作り置き（Prewarm）を進める（出入りの動きの途中は作り始めない）
        AdvancePrewarm();

        // 動いている途中に次の操作が来たら、今の動きを終わりまで飛ばす
        if (_current is { Started: true } && _runs.Count > 0) Finish(_current);
        if (_current is null && _runs.Count > 0) Begin(_runs.Dequeue());
        if (_current is not null) Step(_current, step);
    }

    /// <summary>子（画面を積む子・幕・遮る板）を引く。無ければ自分へ積む。</summary>
    private void ResolveChildren()
    {
        if (_childrenResolved) return;
        _childrenResolved = true;
        _screens = gameObject.FindChild(ScreensChild);
        if (!_screens.IsValid) _screens = gameObject;
        _veil = gameObject.FindChild(VeilChild);
        _blocker = gameObject.FindChild(BlockerChild);
        NavNode.SetVisible(_veil, false);
        NavNode.SetVisible(_blocker, false);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        // テーマが替わったら枠の背景と幕の色を当て直す
        foreach (var instance in _instances.Values) PaintFrame(instance);
    }

    // ── 実体を作る ──────────────────────────────────────────

    /// <summary>
    /// 段の実体を作り始める（枠 → 次のフレームで中身）。枠はできあがるまで隠す。
    /// 中身の出所（ScreenContentPlan）が作り置きなら、作り置きの隠した枠ごと借りる（中身ができていれば次のフレームで Ready）。
    /// </summary>
    private Instance CreateInstance(ScreenEntry entry)
    {
        ResolveChildren();
        var source = ScreenContentPlan.Choose(_supplied.ContainsKey(entry.Id), AdoptAvailable(entry), CanLeasePrewarm(entry.Prefab));
        var lease = source == ScreenContentSource.Prewarmed ? LeasePrewarm(entry) : null;
        var frame = lease?.Frame ?? GameObject.Instantiate(FramePrefab, _screens);
        if (!frame.IsValid) Debug.LogError($"{LogPrefix} 枠のプレハブを作れません: {FramePrefab}");
        frame.Visible = false;
        var instance = new Instance
        {
            Entry = entry,
            Handle = _handles[entry.Id],
            Frame = frame,
            Content = lease?.Content ?? new GameObject(Entity.None),
            Phase = lease?.Phase ?? BuildPhase.FrameRequested,
            // 作り置きを借りられなかったら（貸す直前に消えた）プレハブから作る
            Source = source == ScreenContentSource.Prewarmed && lease is null ? ScreenContentSource.Prefab : source,
            Prewarm = lease?.Entry,
        };
        // 作り置きの中身ができていれば、手札の中身もすぐ分かる（枠の背景も今の指定で塗る）
        if (instance.Content.IsValid) instance.Handle.Content = instance.Content;
        if (lease is not null) PaintFrame(instance);
        _instances[entry.Id] = instance;
        entry.HasInstance = true;
        NavigatorRegistry.RegisterFrame(frame, this, entry.Id);
        return instance;
    }

    /// <summary>作りかけの実体を進める（枠ができたら中身を作る・中身ができたら画面のスクリプトを探す）。</summary>
    private void AdvanceBuilds()
    {
        // 画面のスクリプトへの知らせ（Enter）の中で積み下ろしが呼ばれても壊れないよう写しを回す
        foreach (var instance in new List<Instance>(_instances.Values))
        {
            switch (instance.Phase)
            {
                case BuildPhase.FrameRequested:
                    if (NavNode.IsBuilt(instance.Frame) || !instance.Frame.IsValid)
                    {
                        PaintFrame(instance);
                        var body = instance.Entry.Options.SafeArea ? instance.Frame.FindChild(BodyChild) : instance.Frame;
                        var parent = body.IsValid ? body : instance.Frame;
                        // 渡された中身（Push(GameObject)）・置いてある根（RootAdoptChild）があれば、作る代わりに枠の中へ移して引き取る
                        // （置いてある根は Edit と同じ見た目で始まる。ScreenStack.Content.cs）
                        var adopted = TakeContent(instance);
                        if (adopted.IsValid)
                        {
                            adopted.SetParent(parent);
                            // 渡された中身は、アプリの置き場で自分を隠していても画面として見せる（置いてある根は従来どおり触らない）
                            if (instance.Source == ScreenContentSource.Supplied) NavNode.SetVisible(adopted, true);
                            instance.Content = adopted;
                            Debug.Log($"{LogPrefix} {gameObject.Name} 中身を引き取った（{instance.Source}）: {instance.Entry}");
                        }
                        else
                        {
                            instance.Content = GameObject.Instantiate(instance.Entry.Prefab, parent);
                            if (!instance.Content.IsValid) Debug.LogError($"{LogPrefix} 画面のプレハブを作れません: {instance.Entry.Prefab}");
                        }
                        instance.Handle.Content = instance.Content;
                        instance.Phase = BuildPhase.ContentRequested;
                        instance.WaitFrames = 0;
                    }
                    else WaitBuild(instance);
                    break;
                case BuildPhase.ContentRequested:
                    if (NavNode.IsBuilt(instance.Content) || !instance.Content.IsValid) instance.Phase = BuildPhase.Ready;
                    else WaitBuild(instance);
                    break;
                case BuildPhase.Ready:
                    // 落ち着き待ちのフレームを数えてから画面のスクリプトを探す（このフレームに届けた Enter は次のフレームから数える。
                    // ContentSettleGate の約束の順）
                    instance.Settle.Frame();
                    LookupScreen(instance);
                    break;
            }
        }
    }

    /// <summary>
    /// 置いてある根の子を 1 回だけ引き取る。対象は、根の画面（<see cref="RootPrefab"/> から積んだ段）だけ。
    /// 名前が空・見つからない・既に引き取った後は無効な GameObject を返す（呼び手はプレハブから作る）。
    /// 引き取った後に同じ段を積み直すときは、通常どおりプレハブから作る（置いてある子はもう無い）。
    /// </summary>
    /// <param name="entry">これから中身を作る段。</param>
    /// <returns>引き取る子。無ければ <see cref="GameObject.IsValid"/> が false のもの。</returns>
    private GameObject TakeAdoptChild(ScreenEntry entry)
    {
        if (_adoptTaken || RootAdoptChild.Length == 0 || RootPrefab.Length == 0 || entry.Prefab != RootPrefab)
            return new GameObject(Entity.None);
        ResolveChildren();
        var child = _screens.FindChild(RootAdoptChild);
        if (!child.IsValid)
        {
            Debug.LogWarning($"{LogPrefix} {gameObject.Name} 置いてある根の子 {RootAdoptChild} が {ScreensChild} の下に無いので、{RootPrefab} から作ります");
            _adoptTaken = true;
            return child;
        }
        _adoptTaken = true;
        return child;
    }

    /// <summary>置いてある根を引き取る試みを済ませたか（1 回だけ。無くても 2 度は探さない）。</summary>
    private bool _adoptTaken;

    /// <summary>できあがりを待つ（次のフレームを描かせる。上限を過ぎたら諦めて進める）。</summary>
    private void WaitBuild(Instance instance)
    {
        Redraw.Request();
        if (++instance.WaitFrames <= MaxBuildWaitFrames) return;
        Debug.LogWarning($"{LogPrefix} {instance.Entry} ができあがりません（{MaxBuildWaitFrames} フレーム）。中身なしで進めます");
        instance.Phase = BuildPhase.Ready;
    }

    /// <summary>画面のスクリプト（UiScreen）を探し、見つかったら渡す値を届ける（1 回）。</summary>
    private void LookupScreen(Instance instance)
    {
        // 中身を作れなかった画面は Enter を届ける相手がいない（出入りの時計を待たせない）
        if (!instance.Content.IsValid) instance.Settle.MarkEntered();
        if (instance.Entered || instance.LookupFrames > MaxScreenLookupFrames || !instance.Content.IsValid) return;
        instance.LookupFrames++;
        var screen = Of<UiScreen>(instance.Content);
        if (screen is null)
        {
            Redraw.Request();
            return;
        }
        instance.Screen = screen;
        instance.Handle.Screen = screen;
        screen.Navigator = this;
        screen.Handle = instance.Handle;
        instance.Entered = true;
        // 出入りの時計は、このフレーム（Enter の中の変更）と画面のスクリプトの最初の Update を描いた後から進める
        instance.Settle.MarkEntered();
        screen.Enter(instance.Entry.Args);
        // 既に落ち着いていて上の画面で、スタックが見えている（選んでいないタブではない）なら、見えたことも知らせる
        if (_current is null && _runs.Count == 0 && _model.Top?.Id == instance.Entry.Id && NavigatorRegistry.IsActiveNode(Owner))
            screen.NotifyShown();
    }

    /// <summary>枠の背景（不透明な画面は背景の色・透ける画面は透明）と幕の色を当てる。</summary>
    private void PaintFrame(Instance instance)
    {
        var background = Theme.Color(UiTokens.ColorBackground);
        NavNode.SetSpriteColor(instance.Frame, instance.Entry.Options.Opaque ? background : UiColorMath.Transparent);
    }

    // ── 出入りを動かす ──────────────────────────────────────

    /// <summary>出入りを始める（入ってくる画面の実体が無ければ作る）。</summary>
    private void Begin(Run run)
    {
        _current = run;
        var change = run.Change;
        if (change.Incoming is { } incoming && !_instances.ContainsKey(incoming.Id)) CreateInstance(incoming);
        run.Clock = new TransitionClock(NavMotion.Duration(Theme, change.Transition));
        run.Curve = NavMotion.Curve(Theme, change.Transition);
        run.Parallax = NavMotion.Parallax(Theme);
    }

    /// <summary>
    /// 出入りを 1 フレーム進める: 実体ができあがるまで待ち（できあがったら始まりの置き方）、入ってくる画面が落ち着くまで時計を止め、
    /// 動かし始めたフレームは数えず、1 フレームの経過に上限を置いて進める（TransitionClock）。
    /// </summary>
    private void Step(Run run, float dt)
    {
        // 実体（枠・中身）ができあがるまで待つ。できあがったら始まりの置き方・遮る板
        if (!run.Started)
        {
            if (!InvolvedReady(run.Change))
            {
                Redraw.Request();
                return;
            }
            Start(run);
        }

        // 時計: 入ってくる画面が落ち着くまで止める（待つ間も描き続けて、Enter の後のフレームを描かせる）
        if (!run.Clock.Tick(IncomingSettled(run.Change), dt))
        {
            Redraw.Request();
            return;
        }
        if (run.Clock.IsDone)
        {
            Finish(run);
            return;
        }
        ApplyPoses(run, run.Curve.Evaluate(run.Clock.Linear));
        // 残りは動きの時計で数える（重いフレームで実時間が延びても、毎フレーム延ばし直すので途中で止まらない）
        Redraw.KeepAlive(run.Clock.Remaining + KeepAliveMargin);
    }

    /// <summary>入ってくる・出ていく画面の実体ができあがったか。</summary>
    private bool InvolvedReady(ScreenChange change)
    {
        if (change.Incoming is { } incoming && _instances.TryGetValue(incoming.Id, out var i) && i.Phase != BuildPhase.Ready) return false;
        return true;
    }

    /// <summary>
    /// 入ってくる画面が落ち着いたか（Enter を届けて 1 フレーム描いた・画面のスクリプトの無い画面は待つ上限を過ぎた。ContentSettleGate）。
    /// 前から居る画面（下ろして戻る下の画面）はとうに落ち着いているので待たない。入ってくる画面の実体が無ければ待たない。
    /// </summary>
    private bool IncomingSettled(ScreenChange change) => Get(change.Incoming) is not { } incoming || incoming.Settle.IsSettled;

    /// <summary>
    /// 動きの始まり（実体ができあがったフレーム。時計は入ってくる画面が落ち着くまで止まる）: 底上げ・最初の置き方・見せる・遮る板を出す。
    /// 動きの無い出入り（None・長さ 0）は入れ替えの姿を当てず（出ていく画面を見せたまま）、時計が動いたフレームの Finish で入れ替える
    /// （落ち着く前の、Enter を受けていない画面を一瞬見せない）。
    /// </summary>
    private void Start(Run run)
    {
        run.Started = true;
        var change = run.Change;
        int step = UiLayers.StackStep(Theme, LayerStep);
        int top = Math.Max(_model.Count, 1);
        if (Get(change.Outgoing) is { } outgoing)
        {
            NavNode.SetBias(outgoing.Frame, BiasOf(change.Outgoing!, step));
            NavNode.SetVisible(outgoing.Frame, true);
        }
        if (Get(change.Incoming) is { } incoming)
        {
            int bias = BiasOf(change.Incoming!, step);
            // 置き換え・根からやり直しは、入ってくる画面を出ていく画面（外れた段）より手前に置く
            if ((change.Kind == ScreenOpKind.Replace || change.Kind == ScreenOpKind.SetRoot) && change.Outgoing is { } replaced)
                bias = BiasOf(replaced, step) + (int)(step * ReplaceBiasFraction);
            NavNode.SetBias(incoming.Frame, bias);
        }
        // 幕と遮る板はすべての画面より手前
        NavNode.SetBias(_veil, UiLayers.ScreenBias(top + 1, step));
        NavNode.SetBias(_blocker, UiLayers.ScreenBias(top + 1, step) + 1);
        bool animated = TransitionMath.IsAnimated(change.Transition) && run.Clock.Duration > 0f;
        // 遮る板は動きの無い出入りでも出す（入ってくる画面が落ち着くのを待つ間、並びの変わった後ろの画面を押させない）
        NavNode.SetVisible(_blocker, true);
        NavNode.SetVisible(_veil, animated && change.Transition == NavTransition.Fade);
        if (animated) ApplyPoses(run, 0f);
    }

    /// <summary>進み具合の置き方を当てる。</summary>
    private void ApplyPoses(Run run, float progress)
    {
        var change = run.Change;
        var poses = TransitionMath.Evaluate(change.Transition, change.Direction, progress, run.Parallax);
        if (Get(change.Incoming) is { } incoming)
        {
            // 予測型の戻るのプレビューで見せていた下の画面は、見せていた位置（0）のまま動かさない（跳ばない。3b）
            NavNode.SetFraction(incoming.Frame, run.FromPreview ? Vector2.Zero : poses.Incoming.Fraction);
            NavNode.SetVisible(incoming.Frame, run.FromPreview || poses.Incoming.Visible);
        }
        if (Get(change.Outgoing) is { } outgoing)
        {
            NavNode.SetFraction(outgoing.Frame, poses.Outgoing.Fraction);
            NavNode.SetVisible(outgoing.Frame, poses.Outgoing.Visible);
        }
        if (change.Transition == NavTransition.Fade)
            NavNode.SetSpriteColor(_veil, Theme.Color(UiTokens.ColorBackground).WithAlpha(poses.VeilAlpha));
    }

    /// <summary>
    /// 出入りを終える: 終わりの置き方・外れた画面を消す・落ち着いた状態（見せる・隠す・手放す）・知らせ・フォーカス。
    /// 次の出入りが待っているときは落ち着いた状態を当てない（次の出入りが両方の画面を見せる）。
    /// </summary>
    private void Finish(Run run)
    {
        if (!run.Started) Start(run);
        var change = run.Change;
        _current = null;
        ApplyPoses(run, 1f);
        NavNode.SetVisible(_veil, false);
        NavNode.SetVisible(_blocker, false);
        if (Get(change.Incoming) is { } incoming) NavNode.SetFraction(incoming.Frame, Vector2.Zero);

        // 外れた画面（下ろした・置き換えた・途中の段・旧い根）を消す
        var closed = new List<ScreenEntry>(change.Removed);
        if (change.OutgoingRemoved) closed.Add(change.Outgoing!);
        foreach (var entry in closed) DestroyEntry(entry, notifyExit: true);

        // 覆われた画面（Push の出ていく画面）
        if (!change.OutgoingRemoved && Get(change.Outgoing) is { } covered)
        {
            NavNode.SetFraction(covered.Frame, Vector2.Zero);
            covered.Screen?.OnScreenHidden();
            UiFocus.SendToBack(covered.Scope);
        }

        if (_runs.Count == 0) Settle();

        if (Get(change.Incoming) is { } top)
        {
            top.Scope ??= UiFocus.CreateScope(top.Frame, $"{gameObject.Name}#{top.Entry.Id}");
            // 見えているスタック（選んでいないタブ・覆われた画面の中ではない）だけが範囲を前へ出し、見えたことを知らせる
            bool active = NavigatorRegistry.IsActiveNode(Owner);
            if (active)
            {
                UiFocus.BringToFront(top.Scope);
                // 上の画面の中の入れ子のスタック（シェルの中のタブ）の上の画面の範囲も前へ（内側がいちばん前）
                NavigatorRegistry.BringNestedToFront(top.Frame);
            }
            else UiFocus.SendToBack(top.Scope);
            if (active && top.Entered) top.Screen?.NotifyShown();
        }
        Debug.Log($"{LogPrefix} {gameObject.Name} settled depth={_model.Count} top={_model.Top?.ToString() ?? "-"}");
        Redraw.Request();
        Changed?.Invoke(this);
    }

    /// <summary>落ち着いた状態を当てる（見せる・隠す・手放す）と、段ごとの底上げをそろえる。</summary>
    private void Settle()
    {
        var settle = _model.Settle();
        int step = UiLayers.StackStep(Theme, LayerStep);
        foreach (var entry in settle.Visible)
            if (Get(entry) is { } instance)
            {
                NavNode.SetVisible(instance.Frame, true);
                NavNode.SetBias(instance.Frame, BiasOf(entry, step));
            }
        foreach (var entry in settle.Hidden)
            if (Get(entry) is { } instance)
            {
                NavNode.SetVisible(instance.Frame, false);
                NavNode.SetBias(instance.Frame, BiasOf(entry, step));
            }
        foreach (var entry in settle.Release) DestroyEntry(entry, notifyExit: false);
    }

    /// <summary>段 entry の底上げ（段の添字 × 1 段）。</summary>
    private int BiasOf(ScreenEntry entry, int step)
    {
        int index = _model.IndexOf(entry);
        // 外れた段（下ろしている画面）はいちばん上の 1 つ上
        return UiLayers.ScreenBias(index >= 0 ? index : _model.Count, step);
    }

    /// <summary>
    /// 段の実体を消す（外れた段は手札を閉じる。手放すだけなら手札は残す）。
    /// 中身の後始末（2026-10-02）: 渡された中身（ReturnToParent）は枠を消す前に元の親へ戻し、使い回す作り置き（PrewarmMode.Reuse）は
    /// 枠ごと隠して作り置きへ戻す（枠を消さない）。OnScreenExit は外れたとき（notifyExit）に届け、手放すだけ（KeepState = false）でも
    /// 使い回す作り置きへ戻すなら届ける（2026-10-03。レビュー #24）。
    /// </summary>
    private void DestroyEntry(ScreenEntry entry, bool notifyExit)
    {
        if (_instances.Remove(entry.Id, out var instance))
        {
            // 画面のスクリプトへ OnScreenExit: 外れたとき（notifyExit）と、覆われて手放すだけでも中身を使い回す作り置き（Reuse）へ戻すとき
            // （次に貸すと同じ画面のスクリプトにまた OnScreenEnter が届くので、入りと出を対にする。2026-10-03。レビュー #24）
            if (ScreenContentPlan.NotifiesExit(notifyExit, KeepsPrewarmContent(instance))) instance.Screen?.OnScreenExit();
            NavigatorRegistry.UnregisterFrame(instance.Frame);
            UiFocus.RemoveScope(instance.Scope);
            if (!ReleaseContent(instance)) instance.Frame.Destroy();
        }
        // 枠へ移す前に外れた渡された中身を忘れる（移していないので触らない。使用中の判定に残さない。2026-10-03。レビュー #22）
        ForgetUntakenSupplied(entry);
        entry.HasInstance = false;
        if (entry.Removed && _handles.Remove(entry.Id, out var handle)) handle.Complete(entry.Result);
    }

    /// <summary>段の実体（無ければ null）。</summary>
    private Instance? Get(ScreenEntry? entry) => entry is not null && _instances.TryGetValue(entry.Id, out var i) ? i : null;

    // ── 戻る ────────────────────────────────────────────────

    /// <inheritdoc />
    bool INavigator.HandleBack() => HandleBack();

    /// <summary>
    /// このスタックへ戻るを渡す（2026-10-02 に公開。戻るの段の Navigation の層と同じ決め方: 動いている途中なら今の動きを終えてから、
    /// 上の画面の ScreenOptions.IgnoreBack → UiScreen.OnBackPressed → 根より上なら 1 つ下ろす）。独自の戻るの層（BackDispatcher.AddLayer）
    /// から特定のスタックへ戻るを渡すときに使う。
    /// </summary>
    /// <returns>受けたら true（根で画面も受けなければ false）。</returns>
    public bool HandleBack()
    {
        if (_model.Top is not { } top) return false;
        // 動いている途中の戻るは、今の動きを終えてから決める
        if (_current is { Started: true }) Finish(_current);
        if (top.Options.IgnoreBack) return true;
        if (Get(top)?.Screen is { } screen && screen.OnBackPressed()) return true;
        return Pop();
    }
}
