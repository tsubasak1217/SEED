// ============================================================================
//  EscMenuHint.cs
//  画面左上に「Esc でメニュー」を示す画像を出す、待機中（Idle）専用のヒント。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・NativeFrameContext

/// <summary>
/// 「Esc でメニューが開ける」ことを示す画像を、釣り姿勢に入っていない
/// （<see cref="FishingController.FishState.Idle"/>）ときだけ画面左上に出すヒント表示
/// 【この画像の表示・非表示の唯一の持ち主】。
///
/// 【責務】
/// 「いま <see cref="FishingController.Current"/> の状態が Idle かどうか」だけを見て、
/// 自分自身（画像アクタ）の <c>Visible</c> を切り替える。それ以外は一切知らない
/// （Idle 以外の各状態の意味・遷移条件は <see cref="FishingController"/> の責務）。
///
/// 【配置方式】
/// このスクリプトは <c>MainGame.scene</c> の <c>FishingUI</c> キャンバス（1920x1080 /
/// auto_scale）の子アクタに直接付いている（プレハブ化していない、単一シーン専用の
/// ごく単純な表示切替のため）。画像は左上アンカー・左上ピボットで
/// 画面左上からの固定マージンに配置してあり、このスクリプトは位置には触れない。
///
/// 【Current が未設定のときの扱い】
/// <see cref="FishingController.Current"/> が <c>null</c>（＝釣りコントローラがまだ
/// 存在しない、または破棄済み）の間は「釣り中ではない」とみなし表示する。
/// タイトルからシーンへ入った直後の 1 フレーム目のちらつきを避けるための設計。
///
/// 【毎フレーム書き戻さない理由】
/// <c>gameObject.Visible</c> への set は（ドキュメント記載の通り）保存を伴う書き込みで
/// あるため、値が変化したときだけ書く。変化なしなら何もしない。
/// </summary>
public class EscMenuHint : SEEDScript
{
    /// <summary>直前フレームで実際に反映した表示状態（変化検知用）。</summary>
    private bool lastVisible;

    /// <summary>
    /// 初期化。現在の釣り状態から表示状態を求め、初回の値を確定させる。
    /// </summary>
    public override void OnStart()
    {
        // 初回は「前回値」が無いので、いま求めた値を強制的に書き込んで揃える。
        // gameObject はプロパティが毎回新しい値を返す（変数ではない）ため、
        // Visible を直接代入できない。いったんローカル変数へ受けてから書き込む。
        bool visible = ComputeVisible();
        SEED.GameObject self = gameObject;
        self.Visible = visible;
        lastVisible = visible;
    }

    /// <summary>
    /// 毎フレームの更新。表示すべき状態を求め、前回から変化したときだけ反映する。
    /// </summary>
    /// <param name="ctx">エンジンから渡されるフレーム情報（本処理では未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        bool visible = ComputeVisible();
        if (visible == lastVisible) { return; }

        SEED.GameObject self = gameObject;
        self.Visible = visible;
        lastVisible = visible;
    }

    /// <summary>
    /// いま表示すべきかどうかを判定する。
    /// 「<see cref="FishingController.Current"/> が null」または「状態が Idle」のときだけ表示。
    /// </summary>
    /// <returns>表示すべきなら true。</returns>
    private static bool ComputeVisible()
    {
        FishingController? controller = FishingController.Current;
        return controller is null || controller.State == FishingController.FishState.Idle;
    }
}
