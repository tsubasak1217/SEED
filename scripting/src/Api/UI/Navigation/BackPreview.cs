using System.Collections.Generic;
using SEED.Platform;

namespace SEED.UI;

// ============================================================
//  BackPreview.cs — 予測型の戻るのプレビュー（手ぶりの間、戻ると閉じるものを縮めて見せる。W2 の手直し 3b）
//
//  3a の知らせ（platform.back_started・back_progressed・back_cancelled・back_invoked）をエンジンの受け口
//  （PlatformEvents.AddEngineListener。スクリプトの読み直しで外れない。プロセスで 1 度）で受け、純粋な移り変わり
//  （Model/BackPreviewModel.cs）に従って、戻るの段（BackDispatcher）で最初に受ける層の相手（IBackPreviewTarget）へ姿勢を当てる。
//    started     … 受ける層の相手を問う（無ければ何も縮めない）→ 姿勢を当て始める
//    progressed  … 姿勢 = 1 − (1 − ratio.back_preview_scale) × 曲線(進み具合)、画面は指の向きへ size.back_preview_shift まで
//    cancelled   … 今の姿勢から元へ（motion.short）
//    invoked     … Escape を待つ（KeyWaitTimeout を過ぎたら元へ）
//    Escape      … 手ぶりの途中・確定待ちなら確定: 姿勢を保ったまま戻るを配る（BackDispatcher.Dispatch）。相手が閉じる動きに入れば
//                  その姿勢から閉じ（終わったら元の姿勢へ）、入らなければ（別の層が受けた等）元へ戻す
//  取り残し防止: 相手が無効になった（画面の切り替え・面が閉じた・層の破棄）らすぐ元へ。スクリプトの読み直しでは次のフレームで元へ。
//  フレームの処理（Tick・OnBackKey）は BackDispatcher.PollBackKey（フレームに 1 回）が呼ぶ。
// ============================================================

/// <summary>予測型の戻るのプレビュー（シーンに 1 つ。静的）。</summary>
internal static class BackPreview
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] back-preview:";

    /// <summary>受ける知らせの名前（3a の 4 つ）。</summary>
    private static readonly string[] EventNames =
    {
        App.BackStartedEvent, App.BackProgressedEvent, App.BackCancelledEvent, App.BackInvokedEvent,
    };

    /// <summary>移り変わりの本体。</summary>
    private static readonly BackPreviewModel Model = new();

    /// <summary>確定して閉じる動きの途中の相手（終わったら元の姿勢へ戻して手放す）。</summary>
    private static readonly List<IBackPreviewTarget> Exiting = new();

    /// <summary>次のフレームで元の姿勢へ戻す相手（スクリプトの読み直しで手放したもの）。</summary>
    private static readonly List<IBackPreviewTarget> PendingClear = new();

    /// <summary>今の手ぶりの相手（無ければ null＝何も縮めない）。</summary>
    private static IBackPreviewTarget? _target;

    /// <summary>エンジンの受け口を付けたか（プロセスで 1 度）。</summary>
    private static bool _listening;

    /// <summary>今の段階（診断）。</summary>
    public static BackPreviewPhase Phase => Model.Phase;

    /// <summary>今の姿勢（診断）。</summary>
    public static BackPreviewPose Pose => Model.Pose;

    /// <summary>エンジンの受け口を付ける（2 度目からは何もしない）。</summary>
    public static void EnsureListening()
    {
        if (_listening) return;
        _listening = true;
        foreach (string name in EventNames) PlatformEvents.AddEngineListener(name, OnEvent);
    }

    /// <summary>
    /// フレームに 1 回（BackDispatcher.PollBackKey から）: 取り残しの片付け・相手の有効性・元へ戻る動き・確定の待ちを進める。
    /// </summary>
    /// <param name="dt">経過（秒。止めた時間でも進む UnscaledDeltaTime）。</param>
    public static void Tick(float dt)
    {
        EnsureListening();
        // スクリプトの読み直しで手放した相手を元の姿勢へ（読み直しの最中には基盤を呼ばないので、ここで）
        foreach (var stale in PendingClear) stale.ClearBackPreview();
        PendingClear.Clear();
        // 確定して閉じる動きに入った相手: 終わったら元の姿勢へ（ノードが消えていれば書き込みは無視される）
        for (int i = Exiting.Count - 1; i >= 0; i--)
        {
            if (Exiting[i].IsBackPreviewExiting) continue;
            Exiting[i].ClearBackPreview();
            Exiting.RemoveAt(i);
        }
        // 取り残し防止: 相手が無効になった（画面の切り替え・面が閉じた・層の破棄）らすぐ元へ
        if (_target is { IsBackPreviewValid: false })
        {
            Debug.Log($"{LogPrefix} abandon #{Model.Gesture}（相手が無効になった）");
            Model.Abandon();
            ReleaseTarget();
        }
        Follow(Model.Tick(dt));
    }

    /// <summary>
    /// 戻るのキー（Escape）: 手ぶりの途中・確定待ちなら確定し、姿勢を保ったまま戻るを配る。それ以外はふつうに配る。
    /// </summary>
    public static void OnBackKey()
    {
        if (!Model.OnKey())
        {
            BackDispatcher.Dispatch();
            return;
        }
        // この手ぶりの相手はここで手放す（閉じる動きに入れば Exiting へ、入らなければ元へ戻す）
        var target = _target;
        _target = null;
        var result = BackDispatcher.Dispatch();
        if (target is null)
        {
            Debug.Log($"{LogPrefix} commit #{Model.Gesture} (相手なし) → {result.Layer}");
            return;
        }
        if (target.IsBackPreviewExiting)
        {
            Exiting.Add(target);
            Debug.Log($"{LogPrefix} commit #{Model.Gesture} → {result.Layer}（プレビューの姿勢から閉じる）");
            return;
        }
        // 閉じなかった（別の層が受けた・画面が下ろさなかった）: 確定したときの姿勢から元へ戻す
        _target = target;
        Model.SettleBack();
        Debug.Log($"{LogPrefix} commit #{Model.Gesture} → {(result.Handled ? result.Layer : "unhandled")}（相手は閉じなかったので元へ戻す）");
        Apply();
    }

    /// <summary>スクリプトの読み直し: 移り変わりを忘れ、相手は次のフレームで元の姿勢へ戻す。</summary>
    public static void ResetForReload()
    {
        if (_target is not null) PendingClear.Add(_target);
        PendingClear.AddRange(Exiting);
        Exiting.Clear();
        _target = null;
        Model.Reset();
    }

    /// <summary>手ぶりの知らせ（エンジンの受け口）。</summary>
    private static void OnEvent(string json)
    {
        if (!BackGestureEvent.TryParse(json, out var e)) return;
        Configure();
        var step = Model.Receive(e.Phase, e.Gesture, e.Progress, e.Edge);
        if (step == BackPreviewStep.Begin)
        {
            Begin();
            return;
        }
        if (step == BackPreviewStep.Await)
        {
            Debug.Log($"{LogPrefix} invoked #{Model.Gesture}（Escape を待つ）");
            // 待つ間も時間切れを数えられるよう、描画を止めさせない（on_demand でもフレームが進む）
            Redraw.KeepAlive(BackPreviewModel.KeyWaitTimeout);
        }
        if (step == BackPreviewStep.Cancel) Debug.Log($"{LogPrefix} cancel #{Model.Gesture}");
        Follow(step);
    }

    /// <summary>移り変わりの結果に従う（姿勢を当てる・手放す）。</summary>
    private static void Follow(BackPreviewStep step)
    {
        switch (step)
        {
            case BackPreviewStep.Update:
            case BackPreviewStep.Cancel:
                Apply();
                break;
            case BackPreviewStep.Finish:
                ReleaseTarget();
                break;
        }
    }

    /// <summary>新しい手ぶり: 前の相手をすぐ元へ戻し、最初に受ける層の相手を問い、姿勢を当て始める。</summary>
    private static void Begin()
    {
        ReleaseTarget();
        var target = BackDispatcher.WouldHandle() ? BackDispatcher.PreviewTargetOfFirstLayer() : null;
        _target = target is { IsBackPreviewValid: true } ? target : null;
        Debug.Log($"{LogPrefix} start #{Model.Gesture} edge={EdgeWord()} target={(_target is null ? "none" : _target.GetType().Name)}");
        Apply();
    }

    /// <summary>ログ向けの端の語。</summary>
    private static string EdgeWord() => Model.Edge switch
    {
        BackEdge.Left => "left",
        BackEdge.Right => "right",
        _ => "none",
    };

    /// <summary>今の姿勢を相手へ当てる（動いている間は描画を止めさせない）。</summary>
    private static void Apply()
    {
        if (_target is null) return;
        _target.ApplyBackPreview(Model.Pose);
        if (Model.Phase == BackPreviewPhase.Settling) Redraw.KeepAlive(Model.SettleDuration);
        Redraw.Request();
    }

    /// <summary>相手を元の姿勢へ戻して手放す。</summary>
    private static void ReleaseTarget()
    {
        if (_target is null) return;
        _target.ClearBackPreview();
        _target = null;
        Redraw.Request();
    }

    /// <summary>テーマから倍率・ずらし・曲線・戻る時間を読む（テーマの切り替えに追従する。知らせのたびに読む軽い引き）。</summary>
    private static void Configure()
    {
        var theme = UiTheme.Current;
        Model.MinScale = Finite(theme.Number(NavTokens.RatioBackPreviewScale, BackPreviewModel.DefaultMinScale), BackPreviewModel.DefaultMinScale);
        Model.MaxShift = Finite(theme.Number(NavTokens.SizeBackPreviewShift, BackPreviewModel.DefaultMaxShift), BackPreviewModel.DefaultMaxShift);
        Model.Curve = UiCurve.FromTheme(theme, NavTokens.MotionBackPreviewCurve, UiCurve.Decelerate);
        Model.SettleDuration = Finite(theme.Number(UiTokens.MotionShort, BackPreviewModel.DefaultSettleDuration), BackPreviewModel.DefaultSettleDuration);
    }

    /// <summary>有限でなければ既定。</summary>
    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
