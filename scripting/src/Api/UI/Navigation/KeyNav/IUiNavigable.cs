namespace SEED.UI;

// ============================================================
//  IUiNavigable.cs — 方向キー・パッドでフォーカスできる部品の共通の口（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  UiNavigation（UiNavigator が毎フレーム動かす）は、この口だけで部品を扱う:
//    - 矩形（CanvasRect）から押した向きの最寄りを選ぶ（NavigationMath.PickNext）
//    - 移れるか（IsNavigable）で候補を絞る（見えていて・有効で・スクロール以外の切り抜きの外でない）
//    - 決定（OnNavSubmit）で押す、値の軸（AdjustAxis）の向きは OnNavAdjust で値を増減する（フォーカスは移さない）
//  SEED.UI の部品（Button・Toggle・Slider…）には部品ごとのアダプタ（KeyNav/Adapters/）があり、部品の公開 API は変えない。
//  スクリプト独自の部品は、この口を実装して UiNavigation.Register で足す（戻り値を Dispose で外す）。
//  UiFocus.IFocusable（文字入力・ホイールのキーボードの相手）とは別物: こちらは「方向キーで選んでいる部品」（枠を出す相手）。
// ============================================================

/// <summary>方向キー・パッドでフォーカスできる部品。</summary>
public interface IUiNavigable
{
    /// <summary>部品のノード（フォーカスの範囲を祖先から探す起点・スクロールの祖先・枠を重ねる相手）。</summary>
    GameObject NavNode { get; }

    /// <summary>
    /// 画面の上の矩形（画面の画素・左上が原点・Y 下向き。CanvasTransform.LayoutRect と同じ＝前のフレームの描画の値）。
    /// 測れていなければ Rect.Zero（候補にしない）。
    /// </summary>
    Rect CanvasRect { get; }

    /// <summary>移れるか（見えていて・有効で・スクロールの窓以外の切り抜きの外でない。スクロールで隠れているだけなら移れる）。</summary>
    bool IsNavigable { get; }

    /// <summary>方向キーで値を増減する軸（その軸の向きは OnNavAdjust へ回し、フォーカスを移さない。None = いつも移す）。</summary>
    NavAxis AdjustAxis { get; }

    /// <summary>フォーカスになった・外れた（枠は UiNavigator が重ねるので、部品が独自の見た目を変えたいときだけ使う）。</summary>
    /// <param name="focused">フォーカスになったら true。</param>
    void OnNavFocus(bool focused);

    /// <summary>決定（Enter・Space・パッドの South）。指のタップと同じことをする。</summary>
    void OnNavSubmit();

    /// <summary>
    /// 値の増減（AdjustAxis の向きの方向キー。direction = −1〈左・上〉か +1〈右・下〉）。
    /// 方向キーを受けたら true（フォーカスを移さない）、受けなければ false（その向きへフォーカスを移す）。
    /// </summary>
    /// <param name="direction">−1 か +1。</param>
    bool OnNavAdjust(int direction);
}
