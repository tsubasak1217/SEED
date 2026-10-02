using System;
using System.Threading.Tasks;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  UiScreen.cs — 画面のスクリプトの土台と、積んだ画面の手札（ScreenHandle）（W2-7）
//
//  画面（プレハブ）の根に UiScreen の派生を付けると、画面のスタック（ScreenStack）・シート・覆いから知らせを受ける:
//    OnScreenEnter(args) … 作られて渡す値を受けた（作り直したときも。1 回）
//    OnScreenShown()     … 上の画面になった（出入りの動きが終わった後。戻ってきたときも）
//    OnScreenHidden()    … 覆われた・タブを離れた（動きが終わった後）
//    OnScreenExit()      … 下ろされる・置き換えられる（実体を消す直前）
//    OnBackPressed()     … 戻るを受けた。true を返すとスタックは下ろさない（未保存の確認ダイアログを出す・鳴動の画面で無視する）
//    WouldConsumeBack()  … 今、戻るが来たら OnBackPressed が true を返すか（副作用なしの問い。予測型の戻るの判定に使う。
//                           OnBackPressed を上書きしたら、同じ条件でこれも上書きする。上書きしなければ「受ける」とみなす）
//    IsPrewarmReady      … 作り置き（ScreenStack.Prewarm）の画面が温まったか（2026-10-02。重い準備を Update で続ける画面が、
//                           済むまで false を返すと、作り置きはそれまで「作っている途中」のまま。既定は true）
//  付けなくてもよい（ただのプレハブも積める）。Close(result) で自分を下ろし、結果を積んだ側へ返す（ScreenHandle.Closed）。
//  作り置きを使い回す（PrewarmMode.Reuse）画面には、使うたびに OnScreenEnter、外れるたびに OnScreenExit が届く（前の状態は Enter で作り直す）。
// ============================================================

/// <summary>画面のスクリプトの土台。</summary>
public abstract class UiScreen : UiWidget
{
    /// <summary>戻るを無視する（鳴動の画面など。OnBackPressed を上書きしないとき）。</summary>
    [SerializeField(Label = "戻るを無視")]
    public bool IgnoreBack;

    /// <summary>この画面を積んだスタック（シート・覆いの中身なら null）。</summary>
    public ScreenStack? Navigator { get; internal set; }

    /// <summary>この画面の手札（積んだ側と同じもの。シート・覆いの中身なら null）。</summary>
    public ScreenHandle? Handle { get; internal set; }

    /// <summary>渡された値。</summary>
    public object? Args { get; private set; }

    /// <summary>作られて渡す値を受けた（スタック・シート・覆いが呼ぶ）。</summary>
    internal void Enter(object? args)
    {
        Args = args;
        OnScreenEnter(args);
    }

    /// <summary>作られて渡す値を受けた（1 回）。</summary>
    protected internal virtual void OnScreenEnter(object? args) { }

    /// <summary>上の画面になった（動きが終わった後）。</summary>
    protected internal virtual void OnScreenShown() { }

    /// <summary>覆われた・タブを離れた（動きが終わった後）。</summary>
    protected internal virtual void OnScreenHidden() { }

    /// <summary>下ろされる・置き換えられる（実体を消す直前）。</summary>
    protected internal virtual void OnScreenExit() { }

    /// <summary>戻るを受けた。true を返すとスタックは下ろさない（既定は IgnoreBack の値）。</summary>
    protected internal virtual bool OnBackPressed() => IgnoreBack;

    /// <summary>OnBackPressed を上書きした画面の型か（型ごとに 1 度だけ調べる。型を弱く持つので、スクリプトの読み直しで古い型を掴まない）。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, object> OverridesBackByType = new();

    /// <summary>
    /// 今、戻るが来たら <see cref="OnBackPressed"/> が true を返すか（副作用なしの問い。W2 の手直し 3b）。
    /// 画面のスタックが「戻るを受ける層があるか」（<see cref="BackDispatcher.WouldHandle"/>）を答えるのに使い、受ける層が無い
    /// （根で、この画面も受けない）ときは Android の予測型の戻るをシステムへ渡す（ホームへ戻る見た目が出る）。
    /// 既定は、IgnoreBack か、OnBackPressed を上書きしている型なら true（中身は問えないので受けるとみなす＝安全側）。
    /// OnBackPressed を上書きした画面は、同じ条件でこれも上書きすると、受けないときに根の振る舞いが出せる。
    /// ダイアログを開く・状態を変えるなどの副作用を持たせないこと（フレームに 1 回呼ばれうる）。
    /// </summary>
    protected internal virtual bool WouldConsumeBack() => IgnoreBack || OverridesOnBackPressed(GetType());

    /// <summary>戻るを受けそうか（画面のスタックの問いの入口。<see cref="WouldConsumeBack"/> を呼ぶ）。</summary>
    internal bool MayConsumeBack() => WouldConsumeBack();

    /// <summary>
    /// 作り置き（<see cref="ScreenStack.Prewarm(string, PrewarmOptions?)"/>）の画面が温まったか（2026-10-02）。
    /// 隠した枠の中でも部品のスクリプトは動くので、重い準備（ホイールの行・一覧の行の生成など）を Update で続ける画面は、
    /// 済むまで false を返すと、作り置きは貸せる状態にならず作っている途中のまま（積まれたらその場で続ける）。
    /// 既定は true（できあがって数フレーム〈PrewarmSlot.SettleFrames〉で温まったとみなす）。待つ上限は PrewarmSlot.MaxWaitFrames。
    /// </summary>
    protected internal virtual bool IsPrewarmReady => true;

    /// <summary>型が OnBackPressed を上書きしているか（UiScreen 自身の既定の実装でなければ true）。</summary>
    private static bool OverridesOnBackPressed(Type type)
    {
        var known = OverridesBackByType.GetValue(type, t =>
        {
            var method = t.GetMethod(nameof(OnBackPressed),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            return method is not null && method.DeclaringType != typeof(UiScreen);
        });
        return (bool)known;
    }

    /// <summary>自分を下ろす（いちばん上のときだけ。結果は ScreenHandle.Closed へ）。</summary>
    public bool Close(object? result = null) => Navigator is { } nav && Handle is { } handle && nav.Close(handle, result);

    /// <inheritdoc />
    protected override void ApplyLook() { }
}

/// <summary>積んだ画面の手札（結果を待つ・閉じたことを知る）。</summary>
public sealed class ScreenHandle
{
    /// <summary>閉じたときの結果を待つ口（続きはメインスレッドの Update の中で走る）。</summary>
    private readonly TaskCompletionSource<object?> _closed = new();

    /// <summary>スタックの中の番号。</summary>
    public int Id { get; }
    /// <summary>画面のプレハブ。</summary>
    public string Prefab { get; }
    /// <summary>画面の根（できあがる前は IsValid = false）。</summary>
    public GameObject Content { get; internal set; }
    /// <summary>画面のスクリプト（付いていなければ null。できあがった後）。</summary>
    public UiScreen? Screen { get; internal set; }
    /// <summary>閉じた（下ろした・置き換えた・根からやり直した）か。</summary>
    public bool IsClosed { get; private set; }
    /// <summary>閉じたときの結果（Pop の引数）。</summary>
    public object? Result { get; private set; }

    /// <summary>閉じた（結果つき）。</summary>
    public event Action<ScreenHandle>? Closed;

    /// <summary>閉じるまで待つ（結果を返す）。</summary>
    public Task<object?> WhenClosed => _closed.Task;

    internal ScreenHandle(int id, string prefab)
    {
        Id = id;
        Prefab = prefab;
    }

    /// <summary>閉じたことを知らせる（1 回だけ）。</summary>
    internal void Complete(object? result)
    {
        if (IsClosed) return;
        IsClosed = true;
        Result = result;
        Closed?.Invoke(this);
        _closed.TrySetResult(result);
    }

    /// <inheritdoc />
    public override string ToString() => $"#{Id}({Prefab})";
}
