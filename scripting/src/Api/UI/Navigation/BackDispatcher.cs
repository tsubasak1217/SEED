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
    /// <summary>最後にキーを読んだフレームの時刻（同じフレームで 2 度配らない）。</summary>
    private static float _lastPollTime = float.NaN;

    /// <summary>どの層も受けなかったときに App.MoveTaskToBack() を呼ぶか（既定 true。テスト・特別な画面で止められる）。</summary>
    public static bool MoveTaskToBackWhenUnhandled { get; set; } = true;

    /// <summary>戻るを配った（結果。ログ・検査・画面の演出に使う）。</summary>
    public static event Action<BackDispatchResult>? Dispatched;

    /// <summary>既定の層を並べる。</summary>
    private static BackChain CreateChain()
    {
        var chain = new BackChain();
        chain.Add(BackOrder.Focus, LayerFocus, UiFocus.HandleBack);
        chain.Add(BackOrder.Dialog, LayerDialog, () => ModalHost.Current?.HandleBack(ModalKind.Dialog) ?? false);
        chain.Add(BackOrder.Sheet, LayerSheet, () => ModalHost.Current?.HandleBack(ModalKind.Sheet) ?? false);
        chain.Add(BackOrder.Overlay, LayerOverlay, () => ModalHost.Current?.HandleBack(ModalKind.Overlay) ?? false);
        chain.Add(BackOrder.Navigation, LayerNavigation, NavigatorRegistry.DispatchBack);
        return chain;
    }

    /// <summary>
    /// 層を足す（戻り値を Dispose すると外れる。スクリプトの OnDestroy で外すこと）。
    /// </summary>
    /// <param name="order">順の値（BackOrder。小さいほど先）。</param>
    /// <param name="name">名前（ログ）。</param>
    /// <param name="handler">戻るを受けたら true。</param>
    public static IDisposable AddLayer(int order, string name, Func<bool> handler) => Chain.Add(order, name, handler);

    /// <summary>
    /// このフレームに戻る（Escape）が押されていたら配る（1 フレームに 1 回だけ）。画面の組み立ての部品が Update から呼ぶ。
    /// </summary>
    public static void PollBackKey()
    {
        float now = Time.UnscaledElapsedTime;
        if (now == _lastPollTime) return;
        _lastPollTime = now;
        if (Input.GetKeyDown(KeyCode.Escape)) Dispatch();
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
}
