using System;

namespace SEED.UI;

// ============================================================
//  BackDispatcher.cs — 戻るの段（W2-7。roadmap W2-P4・X-3。docs/ui_navigation.md §5）
//
//  戻る（Android の戻るキー・戻るジェスチャーは runtime の key_remap.rs で Escape になる。PC の Esc も同じ）を、
//  上の層から順に配り、最初に「受けた」層で止める:
//    1. Focus（100）      … 今のフォーカス（W2-6 の入力欄が IME を閉じる・フォーカスを外す。IBackConsumer）
//    2. Dialog（200）     … いちばん上のダイアログ（閉じる。閉じられないダイアログも戻るは受ける）
//    3. Sheet（300）      … いちばん上の下からのシート
//    4. Overlay（400）    … いちばん上の上からの覆い
//    5. Navigation（500） … 画面のスタック・タブを内側から（画面が受ける〈鳴動の画面は無視〉→ 1 つ下ろす → 最初のタブへ）
//    6. どれも受けなければ App.MoveTaskToBack()（閉じずに背面へ。デスクトップの模擬はログだけ）
//  トーストは戻るを受けない。スクリプトは AddLayer で自分の層を好きな順に足せる（例: 450 = 覆いの後・画面の前）。
//  Android の実機では、キーボードが出ているときの 1 回目の戻るは IME 自身が閉じる（アプリには届かない。W2-0 の I-11）。
//
//  キーの読み取りは、画面の組み立ての部品（ScreenStack・TabHost・ModalHost・ToastHost）が毎フレーム PollBackKey を呼ぶ。
//  同じフレームに何度呼ばれても 1 回だけ配る（Time.UnscaledElapsedTime で見分ける）。
//
//  【予測型の戻る（W2 の手直し 3b。Android 13 以上・プロジェクト設定 android.predictive_back。docs/android.md §25.18）】
//    - 受ける層があるか（WouldHandle）: 各層の副作用の無い問いで決める（BackChain.WouldHandle）。PollBackKey の中で計算し、前回と
//      違うとき（とスクリプトの読み直しの後の最初のフレーム）だけ App.SetBackCallbackEnabled で基盤へ知らせる（BackCallbackSync）。
//      無効（PC・predictive_back の無いアプリ）と分かったら以後は計算も送りもしない。根（受ける層なし）ではシステムが背面へ回す。
//    - プレビュー（BackPreview.cs）: 手ぶりの間、最初に受ける層の相手（画面・ダイアログの札・シートの板）を縮めて見せ、Escape で確定する。
// ============================================================

/// <summary>戻るの段（シーンに 1 つ。静的）。</summary>
public static class BackDispatcher
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] back:";
    /// <summary>層の名前（ログ）。</summary>
    public const string LayerFocus = "focus";
    /// <summary>層の名前（ログ）。</summary>
    public const string LayerDialog = "dialog";
    /// <summary>層の名前（ログ）。</summary>
    public const string LayerSheet = "sheet";
    /// <summary>層の名前（ログ）。</summary>
    public const string LayerOverlay = "overlay";
    /// <summary>層の名前（ログ）。</summary>
    public const string LayerNavigation = "navigation";
    /// <summary>どの層も受けなかったときの名前（ログ）。</summary>
    public const string LayerMoveTaskToBack = "move_task_to_back";

    /// <summary>層の並び。</summary>
    private static readonly BackChain Chain = CreateChain();
    /// <summary>「アプリが戻るを受けるか」の基盤への知らせの状態（予測型の戻る。W2 の手直し 3b）。</summary>
    private static readonly BackCallbackSync CallbackSync = new();
    /// <summary>最後にキーを読んだフレームの時刻（同じフレームで 2 度配らない）。</summary>
    private static float _lastPollTime = float.NaN;

    /// <summary>どの層も受けなかったときに App.MoveTaskToBack() を呼ぶか（既定 true。テスト・特別な画面で止められる）。</summary>
    public static bool MoveTaskToBackWhenUnhandled { get; set; } = true;

    /// <summary>戻るを配った（結果。ログ・検査・画面の演出に使う）。</summary>
    public static event Action<BackDispatchResult>? Dispatched;

    /// <summary>既定の層を並べる（受けるかの問いと、予測型の戻るのプレビューの相手つき）。</summary>
    private static BackChain CreateChain()
    {
        var chain = new BackChain();
        // フォーカス: 相手が IBackConsumer なら受けるとみなす（中身は問えない）。プレビューはしない
        chain.Add(BackOrder.Focus, LayerFocus, UiFocus.HandleBack, UiFocus.WantsBack);
        chain.Add(BackOrder.Dialog, LayerDialog,
            () => ModalHost.Current?.HandleBack(ModalKind.Dialog) ?? false,
            () => ModalHost.Current?.WantsBack(ModalKind.Dialog) ?? false,
            () => ModalHost.Current?.BackPreviewTarget(ModalKind.Dialog));
        chain.Add(BackOrder.Sheet, LayerSheet,
            () => ModalHost.Current?.HandleBack(ModalKind.Sheet) ?? false,
            () => ModalHost.Current?.WantsBack(ModalKind.Sheet) ?? false,
            () => ModalHost.Current?.BackPreviewTarget(ModalKind.Sheet));
        chain.Add(BackOrder.Overlay, LayerOverlay,
            () => ModalHost.Current?.HandleBack(ModalKind.Overlay) ?? false,
            () => ModalHost.Current?.WantsBack(ModalKind.Overlay) ?? false,
            () => ModalHost.Current?.BackPreviewTarget(ModalKind.Overlay));
        chain.Add(BackOrder.Navigation, LayerNavigation, NavigatorRegistry.DispatchBack, NavigatorRegistry.WouldHandleBack, NavigatorRegistry.BackPreviewTarget);
        return chain;
    }

    /// <summary>
    /// 層を足す（戻り値を Dispose すると外れる。スクリプトの OnDestroy で外すこと）。
    /// 受けるかの問いを持たない層は「いつも受ける」とみなす（予測型の戻るの根の判定。必要なら問いつきの版を使う）。
    /// </summary>
    /// <param name="order">順の値（BackOrder。小さいほど先）。</param>
    /// <param name="name">名前（ログ）。</param>
    /// <param name="handler">戻るを受けたら true。</param>
    public static IDisposable AddLayer(int order, string name, Func<bool> handler) => Chain.Add(order, name, handler);

    /// <summary>
    /// 層を足す（受けるかの問いとプレビューの相手つき。W2 の手直し 3b）。戻り値を Dispose すると外れる。
    /// </summary>
    /// <param name="order">順の値（BackOrder。小さいほど先）。</param>
    /// <param name="name">名前（ログ）。</param>
    /// <param name="handler">戻るを受けたら true（閉じる・下ろすまでする）。</param>
    /// <param name="wants">副作用の無い「今押されたら受けるか」の問い（null = いつも受ける）。予測型の戻るで、受けないときは
    /// システムに任せる（根なら背面へ回る見た目が出る）。</param>
    /// <param name="preview">予測型の戻るで縮めて見せる相手（null・null を返す = 何も縮めない）。</param>
    public static IDisposable AddLayer(int order, string name, Func<bool> handler, Func<bool>? wants, Func<IBackPreviewTarget?>? preview = null)
        => Chain.Add(order, name, handler, wants, preview);

    /// <summary>
    /// 今戻るが押されたら、アプリが受けるか（副作用なし。どれかの層が受ける、または <see cref="MoveTaskToBackWhenUnhandled"/> が false）。
    /// false のとき戻るを押すと背面へ回る（予測型の戻るではシステムが回し、ホームへ戻る見た目が出る）。
    /// </summary>
    public static bool WouldHandle() => BackCallbackSync.AppHandlesBack(Chain.WouldHandle(), MoveTaskToBackWhenUnhandled);

    /// <summary>
    /// このフレームに戻る（Escape）が押されていたら配る（1 フレームに 1 回だけ）。画面の組み立ての部品が Update から呼ぶ。
    /// 予測型の戻るのプレビューを進め、受ける層の有無が変わっていれば基盤へ知らせる（W2 の手直し 3b）。
    /// </summary>
    public static void PollBackKey()
    {
        float now = Time.UnscaledElapsedTime;
        if (now == _lastPollTime) return;
        _lastPollTime = now;
        BackPreview.Tick(Time.UnscaledDeltaTime);
        if (Input.GetKeyDown(KeyCode.Escape)) BackPreview.OnBackKey();
        SyncBackCallback();
    }

    /// <summary>
    /// 戻るを配る（キーを待たずにスクリプト・画面の「戻る」ボタンから呼んでもよい）。
    /// どの層も受けなければ App.MoveTaskToBack()（MoveTaskToBackWhenUnhandled のとき）。
    /// </summary>
    public static BackDispatchResult Dispatch()
    {
        var result = Chain.Dispatch();
        if (result.Handled)
        {
            Debug.Log($"{LogPrefix} {result.Layer}");
        }
        else
        {
            Debug.Log($"{LogPrefix} {LayerMoveTaskToBack}");
            if (MoveTaskToBackWhenUnhandled) SEED.Platform.App.MoveTaskToBack();
        }
        Redraw.Request();
        Dispatched?.Invoke(result);
        return result;
    }

    /// <summary>層の並び（診断）。</summary>
    public static string Describe()
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var (order, name) in Chain.Describe()) parts.Add($"{order}:{name}");
        return string.Join(", ", parts);
    }

    /// <summary>最初に受ける層のプレビューの相手（BackPreview が手ぶりの始まりに問う）。</summary>
    internal static IBackPreviewTarget? PreviewTargetOfFirstLayer() => Chain.PreviewTarget();

    /// <summary>
    /// スクリプトを読み直す前（ScriptBridge から）: 次のフレームで受ける層の有無を送り直し、プレビューを捨てる（相手は次のフレームで元へ）。
    /// </summary>
    internal static void ResetForReload()
    {
        CallbackSync.Reset();
        BackPreview.ResetForReload();
        _lastPollTime = float.NaN;
    }

    /// <summary>
    /// 受ける層の有無が前回と違えば基盤へ知らせる（予測型の戻るが無効と分かったら以後は計算もしない）。
    /// </summary>
    private static void SyncBackCallback()
    {
        if (CallbackSync.Stopped) return;
        bool handles = WouldHandle();
        if (!CallbackSync.ShouldSend(handles)) return;
        bool enabled = SEED.Platform.App.SetBackCallbackEnabled(handles);
        CallbackSync.OnReplied(handles, enabled);
        Debug.Log($"{LogPrefix} callback on={handles} predictive={enabled}{(enabled ? string.Empty : "（無効なので以後は送らない）")}");
    }
}
