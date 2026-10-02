namespace SEED.UI;

// ============================================================
//  TextFieldSelectionPolicy.cs — 入力欄の「選択を許すか」の決め方（2026-10-02。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  Flutter の TextField の enableInteractiveSelection: false に当たる設定（TextField.AllowSelection = false）。Wake or Pay の W3-2b (6)
//  （起床確認の文字入力で、選んでコピーしてから貼るのを防ぎたい）。選択を許さない欄では:
//    - 長押しは全選択にしない（フォーカスとタップの位置へのカーソルだけ。フォーカスがあれば何もしない）
//    - フォーカスで全選択（SelectAllOnFocus）・スクリプトの SelectAll() も選ばない
//    - キーボード（PC の Shift + 矢印・Ctrl + A）・IME（Android の選択の操作）が作った選択は、次のフレームでカーソルの位置へ畳む
//      （畳む先 = 選択の動いた端 = TextInputState.Caret。変換中の文字の区間は選択ではないので触らない）
//  コピー・切り取りは選択が無ければ起きない（貼り付けは別の設定 AllowPaste）。
//  ただし選択を畳むのは C# の Update の後追いなので、Ctrl + A と Ctrl + C が 2 回の Update の間に届くと、エンジンの場は全選択のまま
//  クリップボードへ書けてしまう（2026-10-03。レビュー #10）。そのため選択を許さない欄は、場を始めるときにエンジンへもコピー・切り取りの
//  禁止を渡す（SessionAllowsCopy。エンジンの場は EditKey::Copy / Cut を allow_copy で判定する）。
// ============================================================

/// <summary>入力欄の「選択を許すか」の決め方。</summary>
public static class TextFieldSelectionPolicy
{
    /// <summary>
    /// フォーカスを得たときに全選択するか（選択を許す欄で、フォーカスで全選択の設定か長押しでフォーカスしたとき）。
    /// </summary>
    /// <param name="allowSelection">選択を許すか。</param>
    /// <param name="selectAllOnFocus">フォーカスで全選択の設定（SelectAllOnFocus）。</param>
    /// <param name="longPressed">長押しでフォーカスした。</param>
    public static bool SelectAllOnFocus(bool allowSelection, bool selectAllOnFocus, bool longPressed)
        => allowSelection && (selectAllOnFocus || longPressed);

    /// <summary>
    /// エンジンの文字入力の場（TextInputOptions.AllowCopy）へ渡す「コピー・切り取りを許すか」（2026-10-03。レビュー #10）。
    /// コピーを許す欄（AllowCopy）で、かつ選択を許す欄だけ true。選択を許さない欄は、C# が選択を畳む前に届いた Ctrl + C・Ctrl + X も
    /// エンジンが捨てる（場の設定は始めるときに渡すので、フォーカスの間に選択の許可を変えたら次のフォーカスから効く）。
    /// </summary>
    /// <param name="allowCopy">コピー・切り取りを許すか（TextField.AllowCopy）。</param>
    /// <param name="allowSelection">選択を許すか（TextField.AllowSelection）。</param>
    public static bool SessionAllowsCopy(bool allowCopy, bool allowSelection) => allowCopy && allowSelection;

    /// <summary>
    /// フォーカスのある欄の長押しで全選択するか。
    /// </summary>
    /// <param name="allowSelection">選択を許すか。</param>
    public static bool LongPressSelectsAll(bool allowSelection) => allowSelection;

    /// <summary>
    /// 選択を畳む先（選択を許さない欄に選択があれば、カーソルの位置〈選択の動いた端〉。畳まなくてよければ null）。
    /// </summary>
    /// <param name="allowSelection">選択を許すか。</param>
    /// <param name="state">今の状態。</param>
    public static int? CollapseTo(bool allowSelection, TextInputState state)
    {
        if (allowSelection || !state.HasSelection) return null;
        int length = state.Text?.Length ?? 0;
        int caret = state.Caret;
        return caret < 0 ? 0 : caret > length ? length : caret;
    }
}
