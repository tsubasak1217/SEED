// ============================================================================
//  PauseMenuItem.cs
//  ポーズメニュー 1 行ぶんのポインタ判定（ホバーで選択・クリックで決定）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// ポーズメニューの 1 行に付けるポインタ受け【行のマウス操作の唯一の置き場】。
///
/// 【責務】
/// 「自分が何番目の行か」だけを持ち、ホバー／クリックを <see cref="PauseMenu"/> へ中継する。
/// 何が起きるか（閉じる・遷移する・拡大する）は一切知らない
/// （動作も見た目も、決めるのは PauseMenu の責務）。
///
/// 【ホバーと選択が同じである理由】
/// ホバーは <see cref="PauseMenu.Select"/> を呼ぶだけなので、
/// 「マウスで乗っている行」＝「キーボードで選んでいる行」になる。
/// その結果、拡大アニメーション（PauseMenu 側）はホバーでもキー選択でも同じ経路で走り、
/// 2 つの選択状態が食い違うことがない。
///
/// 【使い方（シーン／プレハブ側）】
/// 行の当たり判定になる Sprite（行の下敷きスプライトそのものでよい）を持つ 2D アクタへ付け、
/// その Sprite の「ポインタ判定」（raycast_target）を ON にする。
/// <see cref="itemIndex"/> に行番号（0 から）を入れる
/// （PauseMenu 側の「行アクタのパス」リストの並びと一致させること）。
///
/// 【なぜ行ごとにスクリプトを置くのか】
/// ポインタイベントは「Sprite を持つアクタ自身」にしか届かないため、
/// 親（PauseMenu）側でまとめて受けることができない。
/// 行の増減はプレハブへ行アクタを足すだけで済む形にしてある。
/// </summary>
public class PauseMenuItem : SEEDScript
{
    /// <summary>この行の番号（0 = 先頭）。<see cref="PauseMenu"/> の行の並びと対応させる。</summary>
    [SerializeField(Label = "行番号(0始まり)")]
    private int itemIndex = 0;

    /// <summary>カーソルが乗ったら、その行を選択状態にする。</summary>
    public override void OnPointerEnter()
    {
        if (!PauseMenu.IsOpen) { return; }
        PauseMenu.Current?.Select(itemIndex);
    }

    /// <summary>
    /// 行の上で押下されたら、その行を選択する。
    ///
    /// 決定そのものは <see cref="PauseMenu"/> 側が同じフレームの
    /// 左クリック押下で行うので、ここでは選択を合わせるだけにしてある
    /// （ここでも決定すると、キー経路と合わせて 2 回実行されてしまう）。
    /// </summary>
    public override void OnPointerDown()
    {
        if (!PauseMenu.IsOpen) { return; }
        PauseMenu.Current?.Select(itemIndex);
    }
}
