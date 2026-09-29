namespace SEED.UI;

// ============================================================
//  BackCallbackSync.cs — 「アプリが戻るを受けるか」を基盤へ知らせる頻度の決め方（W2 の手直し 3b。純粋な計算）
//
//  予測型の戻る（Android 13 以上・プロジェクト設定 android.predictive_back。3a）では、アプリが戻るを受けるとき（受ける層がある）は
//  アプリのコールバックを登録したまま、受けない（根）ときはシステムに任せる（背面へ回る見た目が出る）。BackDispatcher は
//  フレームに 1 回「受ける層があるか」（BackChain.WouldHandle）を計算し、App.SetBackCallbackEnabled で知らせる。
//    - 前回と違うときだけ送る（スクリプトの読み直しの後の最初のフレームは必ず送る）
//    - 返り値（予測型の戻るが有効か）が false なら、以後は送らない（PC・predictive_back の無いアプリで毎フレーム基盤〈JNI〉を呼ばない）
//  MoveTaskToBackWhenUnhandled = false のアプリは、どの層も受けなくても戻るを受ける（背面へ回さない＝常に受ける）。
// ============================================================

/// <summary>「アプリが戻るを受けるか」の知らせの状態。</summary>
public sealed class BackCallbackSync
{
    /// <summary>予測型の戻るが無効と分かった（以後は送らない。読み直しで戻る）。</summary>
    public bool Stopped { get; private set; }

    /// <summary>最後に送った値（null = まだ送っていない・読み直しの後）。</summary>
    public bool? LastSent { get; private set; }

    /// <summary>
    /// どの層も受けないときに背面へ回すかを考えに入れて、アプリが戻るを受けるか（背面へ回さないアプリは常に受ける）。
    /// </summary>
    /// <param name="query">戻るの段の問いの結果（BackChain.WouldHandle）。</param>
    /// <param name="moveTaskToBackWhenUnhandled">どの層も受けないときに背面へ回すか（BackDispatcher.MoveTaskToBackWhenUnhandled）。</param>
    public static bool AppHandlesBack(BackDispatchResult query, bool moveTaskToBackWhenUnhandled)
        => query.Handled || !moveTaskToBackWhenUnhandled;

    /// <summary>この値を送るか（止まっていない・前回と違う・まだ送っていない）。</summary>
    public bool ShouldSend(bool handles) => !Stopped && LastSent != handles;

    /// <summary>送った結果を覚える（予測型の戻るが無効なら以後は送らない）。</summary>
    /// <param name="handles">送った値。</param>
    /// <param name="enabled">返り値（基盤が受け付けて予測型の戻るが有効か）。</param>
    public void OnReplied(bool handles, bool enabled)
    {
        LastSent = handles;
        if (!enabled) Stopped = true;
    }

    /// <summary>スクリプトの読み直し: 次のフレームで送り直す。</summary>
    public void Reset()
    {
        Stopped = false;
        LastSent = null;
    }
}
