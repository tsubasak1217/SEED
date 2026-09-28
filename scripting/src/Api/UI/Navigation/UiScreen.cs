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
//  付けなくてもよい（ただのプレハブも積める）。Close(result) で自分を下ろし、結果を積んだ側へ返す（ScreenHandle.Closed）。
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
