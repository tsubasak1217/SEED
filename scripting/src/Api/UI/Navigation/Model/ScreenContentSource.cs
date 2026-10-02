namespace SEED.UI;

// ============================================================
//  ScreenContentSource.cs — 画面の中身の出所と、外れたときの中身の扱い（2026-10-02。lane3。純粋な計算。docs/ui_navigation.md §2.8）
//
//  ScreenStack が画面の実体（枠と中身）を作るとき、中身をどこから持ってくるかを 1 か所で決める:
//    1. Supplied  … Push(GameObject 中身)・Replace(GameObject 中身) で渡された、組み立て済みの中身（枠の中へ付け替える）
//    2. Adopted   … 置いてある根（RootAdoptChild。シーンの Screens の下にあらかじめ置いた子。根の段・1 回だけ）
//    3. Prewarmed … 作り置き（Prewarm。隠した枠ごと借りる。中身はできあがっているか作っている途中）
//    4. Prefab    … 従来どおり枠を作ってからプレハブを Instantiate する
//  上ほど優先（渡された中身 > 置いてある根 > 作り置き > プレハブ）。置いてある根を作り置きより先にするのは、
//  置いてある子を使わずに残すと Screens の下に見えたまま残るため。
// ============================================================

/// <summary>画面の中身の出所。</summary>
public enum ScreenContentSource
{
    /// <summary>プレハブから作る（従来どおり）。</summary>
    Prefab = 0,
    /// <summary>渡された組み立て済みの中身（Push(GameObject)・Replace(GameObject)）。</summary>
    Supplied = 1,
    /// <summary>置いてある根（RootAdoptChild）。</summary>
    Adopted = 2,
    /// <summary>作り置き（Prewarm）。</summary>
    Prewarmed = 3,
}

/// <summary>渡された中身（Push(GameObject)）を、画面が外れたときにどうするか。</summary>
public enum ScreenContentRelease
{
    /// <summary>画面と一緒に消す（既定。中身の持ち主はスタックへ移る）。</summary>
    Destroy = 0,
    /// <summary>
    /// 画面が外れる直前（OnScreenExit の後・枠を消す前）に、押下・ドラッグを取り消して積んだときの親へ戻す（消さない。
    /// アプリが自分で作り置きを持ち、何度も積む場合。戻した中身は隠れないので、親を隠した置き場にしておく）。
    /// </summary>
    ReturnToParent = 1,
}

/// <summary>画面の中身の出所の決め方。</summary>
public static class ScreenContentPlan
{
    /// <summary>
    /// 中身の出所を決める（渡された中身 > 置いてある根 > 作り置き > プレハブ）。
    /// </summary>
    /// <param name="hasSupplied">渡された中身があるか。</param>
    /// <param name="adoptAvailable">置いてある根を引き取れるか（根の段で、まだ引き取っていない）。</param>
    /// <param name="prewarmAvailable">同じプレハブの作り置きを貸せるか。</param>
    /// <returns>出所。</returns>
    public static ScreenContentSource Choose(bool hasSupplied, bool adoptAvailable, bool prewarmAvailable)
    {
        if (hasSupplied) return ScreenContentSource.Supplied;
        if (adoptAvailable) return ScreenContentSource.Adopted;
        if (prewarmAvailable) return ScreenContentSource.Prewarmed;
        return ScreenContentSource.Prefab;
    }

    /// <summary>
    /// 渡された中身の画面の「状態を保つ」（ScreenOptions.KeepState）を決める。渡された中身は作り直せない（プレハブが無い）ので、
    /// 覆われても手放さない（常に true）。それ以外は指定のまま。
    /// </summary>
    /// <param name="source">出所。</param>
    /// <param name="requested">指定の KeepState。</param>
    /// <returns>使う KeepState。</returns>
    public static bool EffectiveKeepState(ScreenContentSource source, bool requested) =>
        source == ScreenContentSource.Supplied || requested;

    /// <summary>
    /// 段の実体を消す・手放すときに、画面のスクリプトへ OnScreenExit を届けるか（2026-10-03。2 回目のレビュー #24）。
    /// スタックから外れた（下ろした・置き換えた・根からやり直した）なら届ける。覆われて手放すだけ（KeepState = false）なら従来どおり届けないが、
    /// 中身を使い回す作り置き（PrewarmMode.Reuse）へ戻すときは届ける（次に貸すと同じ画面のスクリプトにまた OnScreenEnter が届くので、
    /// 入りと出を対にする。以前は OnScreenExit なしで OnScreenEnter が 2 回届き、入りで取って出で返す数え上げが漏れた）。
    /// </summary>
    /// <param name="leavingStack">スタックから外れたか（呼び手の notifyExit）。</param>
    /// <param name="keptForReuse">中身を使い回す作り置きへ戻すか（PrewarmSlot.KeepsContentOnReturn など）。</param>
    /// <returns>届けるなら true。</returns>
    public static bool NotifiesExit(bool leavingStack, bool keptForReuse) => leavingStack || keptForReuse;
}
