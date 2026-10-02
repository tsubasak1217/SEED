namespace SEED.UI;

// ============================================================
//  ModalPlane.cs — 覆い・シート・ダイアログの面の共通の土台（W2-7）
//
//  面のプレハブ（dialog.actor・bottom_sheet.actor・top_sheet.actor）の根に付く部品の親。ModalHost が面を作り、
//  面のスクリプトは OnWidgetStart で ModalHost から「開く約束」（手札・中身）を受け取る（Claim）。
//  段階: 準備（Preparing。作りかけ・大きさ待ち）→ 入る（Entering）→ 開いている（Open）→ 出る（Exiting）→ 閉じた（消す）。
//  - 開いている間はフォーカスの範囲を前へ出す（下の画面のホイールは矢印キーを受けない）
//  - 戻る: ModalHost が種類ごとに最後に開いた面の HandleBack を呼ぶ（閉じられない面も戻るは受ける）
//  - 閉じる（RequestClose）: 出る動きの後に手札を閉じて自分を消す（ModalHost から外す）
//  - 予測型の戻るのプレビュー（W2 の手直し 3b。IBackPreviewTarget）: 開いていて戻るで閉じる面は、手ぶりの間に派生が決めたノード
//    （ダイアログの札・シートの板）を縮める。確定すると、縮めた姿勢のまま出る動きを始める（出終わったら消えるので元へ戻す必要もない）
//  - 動きなしで閉じる（2026-10-02）: RequestClose(結果, animate: false) は派生の後片付け（OnBeginExit）の後、その場で閉じ終える
//  - 自前の面（2026-10-02）: ModalHost.ShowPlane(種類, プレハブ, 指定) で開き、中身の画面のスクリプトへは DeliverEnter で値を届ける
//  - 画面の下へ回す（2026-10-02。ModalHost.Park）: IsParked の間は戻るの層で数えず、プレビューの相手にもならない
// ============================================================

/// <summary>面の段階。</summary>
public enum ModalPhase
{
    /// <summary>作りかけ・大きさ待ち（まだ見せていない）。</summary>
    Preparing = 0,
    /// <summary>入る動きの途中。</summary>
    Entering = 1,
    /// <summary>開いている。</summary>
    Open = 2,
    /// <summary>出る動きの途中。</summary>
    Exiting = 3,
    /// <summary>閉じた（消す）。</summary>
    Closed = 4,
}

/// <summary>覆い・シート・ダイアログの面の共通の土台。</summary>
public abstract class ModalPlane : UiWidget, IBackPreviewTarget
{
    /// <summary>ログの接頭辞。</summary>
    protected const string LogPrefix = "[UI] modal:";

    /// <summary>面の種類。</summary>
    public abstract ModalKind Kind { get; }
    /// <summary>段階。</summary>
    public ModalPhase Phase { get; private set; } = ModalPhase.Preparing;
    /// <summary>手札（ModalHost が作った。Claim の後）。</summary>
    public ModalHandle? Handle { get; private set; }
    /// <summary>持ち主の ModalHost。</summary>
    protected ModalHost? Host { get; private set; }
    /// <summary>開く約束の中身（DialogOptions・SheetOptions・OverlayOptions）。</summary>
    protected object? Options { get; private set; }
    /// <summary>閉じるときの結果。</summary>
    protected object? CloseResult { get; private set; }

    /// <summary>フォーカスの範囲。</summary>
    private FocusScope? _scope;
    /// <summary>部品が消えたか（予測型の戻るのプレビューの相手を無効にする。3b）。</summary>
    private bool _destroyed;

    /// <summary>フォーカスの範囲（開いている間。覆いを全画面の下に残す〈ModalHost.Park〉ときに後ろへ回す）。</summary>
    internal FocusScope? Scope => _scope;

    /// <summary>
    /// 画面のスタックの画面の下へ回されているか（2026-10-02。ModalHost.Park）。回されている面は戻るを受けず、予測型の戻るの相手にもならない。
    /// </summary>
    public bool IsParked { get; internal set; }

    /// <inheritdoc />
    protected sealed override void OnWidgetStart()
    {
        Host = ModalHost.Current;
        var claim = Host?.Claim(Owner);
        if (claim is null)
        {
            // 全部閉じる（ModalHost.CloseAll）で、面のスクリプトが動く前に取りやめた面は黙って消す
            if (Host is null || !Host.TakeCancelled(Owner))
                Debug.LogWarning($"{LogPrefix} ModalHost から開く約束がありません（{Owner.Name}）。消します");
            Owner.Destroy();
            Phase = ModalPhase.Closed;
            return;
        }
        Handle = claim.Value.Handle;
        Options = claim.Value.Options;
        Handle.Plane = this;
        Handle.Root = Owner;
        // 開いている面として登録し、帯の中の並びの底上げ（あとから開いた面が手前）を当てる
        Host!.Register(this, claim.Value.Kind);
        _scope = UiFocus.CreateScope(Owner, $"{Kind}:{Owner.Name}", overlay: true);
        OnPlaneStart();
        // 面ができる前に手札の Close が呼ばれていた: 外から閉じる入口で閉じる（ダイアログはボタンと同じ決め方を通る。2026-10-03）
        if (Handle.EarlyClose.Requested) CloseFromOutside(Handle.EarlyClose.Result, Handle.EarlyClose.Animate);
        // 開く前に頼まれた「画面の下へ回す」を当てる（面ができてから。ModalHost.Park）
        Host.ApplyPendingPark(this);
    }

    /// <inheritdoc />
    protected sealed override void OnWidgetDestroy()
    {
        _destroyed = true;
        UiFocus.RemoveScope(_scope);
        Host?.Forget(this);
        // 消えるまでに閉じていなければ（シーンの切り替え・Play の終わり）結果なしで閉じる
        Handle?.Complete(CloseResult);
    }

    /// <inheritdoc />
    protected sealed override void OnWidgetUpdate(float dt)
    {
        BackDispatcher.PollBackKey();
        if (Phase == ModalPhase.Closed) return;
        OnPlaneUpdate(Time.UnscaledDeltaTime);
    }

    /// <summary>入る動きを始める（準備ができたら派生が呼ぶ）。</summary>
    protected void BeginEnter()
    {
        if (Phase != ModalPhase.Preparing) return;
        Phase = ModalPhase.Entering;
        Debug.Log($"{LogPrefix} {Kind} open {Owner.Name}");
    }

    /// <summary>入る動きが終わった（派生が呼ぶ）。</summary>
    protected void EndEnter()
    {
        if (Phase == ModalPhase.Entering) Phase = ModalPhase.Open;
    }

    /// <summary>
    /// 閉じる（結果つき）。出る動きの後に手札を閉じて自分を消す。準備中なら動かさずに消す。
    /// </summary>
    public void RequestClose(object? result) => RequestClose(result, animate: true);

    /// <summary>
    /// 閉じる（結果つき。2026-10-02: 動きの有無を選べる）。animate = false なら出る動きを見せずにこのフレームで閉じ終える
    /// （手札の Closed・Completed もこの呼び出しの中で届く。派生の後片付け〈フォーカス・キーボード〉は OnBeginExit で同じく行う）。
    /// 閉じる動きの途中に animate = false で呼ぶと、今すぐ閉じ終える（結果は最初の頼みのまま）。
    /// </summary>
    /// <param name="result">結果（手札へ）。</param>
    /// <param name="animate">出る動きを見せるか。</param>
    public void RequestClose(object? result, bool animate)
    {
        if (Phase == ModalPhase.Closed) return;
        if (Phase == ModalPhase.Exiting)
        {
            // 閉じる動きの途中: 動きなしの頼みなら今すぐ閉じ終える（二度は閉じない）
            if (!animate) FinishClose();
            return;
        }
        CloseResult = result;
        bool wasPreparing = Phase == ModalPhase.Preparing;
        Phase = ModalPhase.Exiting;
        Debug.Log($"{LogPrefix} {Kind} close {Owner.Name} result={result ?? "null"}{(animate ? string.Empty : " (no motion)")}");
        if (wasPreparing) FinishClose();
        else
        {
            // 派生の後片付けと出る動きの始まり。動きなしなら続けて閉じ終える（始めた動きは面と一緒に消える）
            OnBeginExit();
            if (!animate) FinishClose();
        }
        Redraw.Request();
    }

    /// <summary>
    /// 外から閉じる（2026-10-03。レビュー #9）: 手札の Close・Dismiss、ModalHost.CloseAll、面ができる前に頼まれた Close の当て直しの入口。
    /// 既定は <see cref="RequestClose(object?, bool)"/> と同じ。ダイアログ（Dialog）はボタンと同じ決め方（結果の留め金・入力欄の文字の確定・
    /// フォーカスとキーボードの持ち上げの片付け）を通すよう上書きする。
    /// </summary>
    /// <param name="result">結果（手札が整えた値）。</param>
    /// <param name="animate">出る動きを見せるか。</param>
    internal virtual void CloseFromOutside(object? result, bool animate) => RequestClose(result, animate);

    /// <summary>
    /// 中身の画面のスクリプト（UiScreen）へ渡す値を届ける（2026-10-02。自前の面〈ModalPlane の派生〉が中身のプレハブを作ったときに使う。
    /// UiScreen の Enter は内部の口なので、派生はこれを通す）。中身の根に UiScreen が無い（まだ始まっていない）なら false。
    /// 1 回だけ届けるのは呼び手の責任（届いたら true を覚えておく）。
    /// </summary>
    /// <param name="contentRoot">中身の根。</param>
    /// <param name="args">渡す値（UiScreen.OnScreenEnter）。</param>
    /// <returns>届けたら true。</returns>
    protected static bool DeliverEnter(GameObject contentRoot, object? args)
    {
        if (!contentRoot.IsValid || Of<UiScreen>(contentRoot) is not { } screen) return false;
        screen.Enter(args);
        return true;
    }

    /// <summary>出る動きが終わった: 手札を閉じて自分を消す（派生が呼ぶ）。</summary>
    protected void FinishClose()
    {
        if (Phase == ModalPhase.Closed) return;
        Phase = ModalPhase.Closed;
        UiFocus.RemoveScope(_scope);
        _scope = null;
        Host?.Forget(this);
        Handle?.Complete(CloseResult);
        Owner.Destroy();
        Redraw.Request();
    }

    /// <summary>戻るを受けた（ModalHost から）。既定は閉じる（結果 null）。閉じられない面も true を返す（後ろへ回さない）。</summary>
    internal virtual bool HandleBack()
    {
        RequestClose(null);
        return true;
    }

    /// <summary>派生の初期化（Claim の後。子の参照を引く・最初の見た目）。</summary>
    protected abstract void OnPlaneStart();

    /// <summary>派生の毎フレームの更新（準備・動き）。</summary>
    protected abstract void OnPlaneUpdate(float dt);

    /// <summary>出る動きを始める（終わったら FinishClose を呼ぶ）。</summary>
    protected abstract void OnBeginExit();

    /// <inheritdoc />
    protected override void ApplyLook() { }

    // ── 予測型の戻るのプレビュー（W2 の手直し 3b。IBackPreviewTarget）─────────

    /// <summary>プレビューで縮めるノード（無効ならプレビューしない。既定は無し。派生が決める）。</summary>
    protected virtual GameObject BackPreviewNode => new(Entity.None);

    /// <summary>戻るで閉じる面か（閉じない面は縮めない。既定は閉じる＝<see cref="HandleBack"/> の既定）。</summary>
    protected virtual bool ClosesOnBack => true;

    /// <inheritdoc />
    /// <remarks>開いていて（入る・出る動きの途中でない）、戻るで閉じ、縮めるノードがあり、部品が消えていない。画面の下へ回された面は縮めない。</remarks>
    bool IBackPreviewTarget.IsBackPreviewValid =>
        !_destroyed && !IsParked && Phase == ModalPhase.Open && ClosesOnBack && BackPreviewNode.IsValid;

    /// <inheritdoc />
    bool IBackPreviewTarget.IsBackPreviewExiting => !_destroyed && Phase == ModalPhase.Exiting;

    /// <inheritdoc />
    void IBackPreviewTarget.ApplyBackPreview(BackPreviewPose pose) => ApplyBackPreviewPose(pose);

    /// <inheritdoc />
    void IBackPreviewTarget.ClearBackPreview() => ApplyBackPreviewPose(BackPreviewPose.Identity);

    /// <summary>
    /// プレビューの姿勢を当てる（既定: 縮めるノードの真ん中の周りに縮める。面は横へずらさない）。端を留める面（シート・覆い）と、
    /// 出入りの動きにも同じ見た目の倍率を使う面（ダイアログ。開き具合の倍率との積を書く）は派生で上書きする。
    /// </summary>
    protected virtual void ApplyBackPreviewPose(BackPreviewPose pose)
        => NavNode.SetVisualScale(BackPreviewNode, new Vector2(pose.Scale, pose.Scale));
}
