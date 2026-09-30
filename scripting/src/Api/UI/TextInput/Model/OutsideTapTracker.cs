namespace SEED.UI;

// ============================================================
//  OutsideTapTracker.cs — 入力欄の外のタップの見分け（W2-6b。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  入力欄にフォーカスがある間、欄の外を押して、動かさずに（許容 8 dp 以内で）離したらフォーカスを外す（TextField.UpdateOutsideTap）。
//  **OS に取り消された指（TouchPhase.Canceled）はタップと数えない**。2026-09-30 の実機（Pixel 6a・Android 17）で、Android の戻るの
//  ジェスチャーが画面の左端の指をアプリから奪い（Gesture Monitor の edge-swipe。アプリには Started → Cancelled が届く）、その取り消しを
//  離しと数えて、戻るキーが届く前にフォーカスを外していた（docs/ui_text_input.md §13.1）。通知の引き下ろしなどで指が奪われたときも同じ。
// ============================================================

/// <summary>1 フレームぶんの指（マウスの左ボタン。Android は指 0 が左ボタンを動かす）の入力。</summary>
/// <param name="Pressed">このフレームで押した。</param>
/// <param name="Released">このフレームで離した（取り消しを含む）。</param>
/// <param name="Position">今の位置（画面の画素）。</param>
/// <param name="PressInsideField">押した位置が欄の中か（押したフレームだけ意味を持つ）。</param>
/// <param name="ReleaseCanceled">離しが OS の取り消しか（このフレームに TouchPhase.Canceled の指がある）。</param>
public readonly record struct OutsideTapInput(bool Pressed, bool Released, Vector2 Position, bool PressInsideField, bool ReleaseCanceled);

/// <summary>入力欄の外のタップの見分け（押してから離すまでを覚える）。</summary>
public sealed class OutsideTapTracker
{
    /// <summary>欄の外で押して、まだ離していないか。</summary>
    private bool _pressedOutside;
    /// <summary>押した位置（画面の画素）。</summary>
    private Vector2 _pressPosition;

    /// <summary>覚えている押下を捨てる（フォーカスを得たとき）。</summary>
    public void Reset() => _pressedOutside = false;

    /// <summary>
    /// 1 フレームぶんの入力を当てる。欄の外のタップが終わったフレームだけ true（呼び出し側がフォーカスを外す）。
    /// </summary>
    /// <param name="input">このフレームの入力。</param>
    /// <param name="slopPx">動いてもタップとみなす距離の上限（画面の画素）。</param>
    public bool Update(in OutsideTapInput input, float slopPx)
    {
        if (input.Pressed)
        {
            _pressPosition = input.Position;
            _pressedOutside = !input.PressInsideField;
        }
        if (!input.Released || !_pressedOutside) return false;
        _pressedOutside = false;
        // OS に取り消された指（戻るのジェスチャー・通知の引き下ろし）はタップではない
        if (input.ReleaseCanceled) return false;
        return Vector2.Distance(input.Position, _pressPosition) <= slopPx;
    }
}
