namespace SEED.UI;

// ============================================================
//  TextFieldBackPolicy.cs — 入力欄が戻るを受けたときの決め方（W2-6b。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  入力欄（TextField）は戻るの段（BackDispatcher）の Focus の層で最初に戻るを受け、**必ずフォーカスを外す**。そのうえで、戻るを
//  受ける（後ろの層へ回さない）か、後ろの層へ回すかを次で決める:
//    - ソフトキーボードが出ていた → 受ける（Android はふつう IME が先に閉じるのでアプリへは届かない。届いたときの安全側）
//    - PC（模擬を含む） → 受ける（閉じるソフトキーボードが無いので、Esc の 1 回目はフォーカスを外すだけ）
//    - Android でキーボードを閉じた後 → 後ろに戻るを受ける層（ダイアログ・シート・覆い・画面のスタック・スクリプトの層）があれば回す
//      （ダイアログは 2 回目の戻るで閉じる。Flutter・Android の EditText と同じ）。**無ければ受ける**（根の画面で回すとアプリが背面へ回る。
//      2026-09-30 の実機の手順 11: 2 回目の戻るでアプリが背面へ回った。フォーカスを外すだけにして、3 回目で背面へ）
// ============================================================

/// <summary>入力欄が戻るを受けたときの決め方。</summary>
public static class TextFieldBackPolicy
{
    /// <summary>
    /// 戻るを受けるか（true = 後ろの層へ回さない）。フォーカスは呼び出し側が必ず外す。
    /// </summary>
    /// <param name="keyboardShown">戻るが届いたときにソフトキーボードが出ていたか。</param>
    /// <param name="isDevice">実機か（PC・模擬は false）。</param>
    /// <param name="laterLayerHandles">フォーカスの層より後ろの層が戻るを受けるか（BackDispatcher.WouldHandleAfterFocus）。</param>
    public static bool Consumes(bool keyboardShown, bool isDevice, bool laterLayerHandles)
        => keyboardShown || !isDevice || !laterLayerHandles;
}
