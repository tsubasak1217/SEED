namespace SEED.UI;

// ============================================================
//  NavInputPolicy.cs — 方向キー・決定の 1 回の入力をどう扱うかの決まり（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  - 今のフォーカスが無い → 読む順で最初の部品へフォーカスし、枠を出す（その 1 回はそれ以上動かない）
//  - 枠を隠している（指・マウスで触った後）→ 最初の 1 回は枠を出すだけ（見えないフォーカスで部品が押されない。
//    Windows の「キーボードで操作したときだけフォーカスの枠」と同じ）。ただし、指でホイール・入力欄を選んだ直後（armed）は
//    すぐ効かせる（以前の「触れたホイールを上下の矢印で回す」を保つ）
//  - それ以外 → そのまま効かせる（値の軸の向きなら値の増減、それ以外はフォーカスを移す・決定）
//  - 文字入力の間（UiFocus の相手が入力欄など）と、それが終わった次のフレームは読まない（Enter で確定したキーで決定しない）
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>1 回の入力の扱い。</summary>
public enum NavInputDecision
{
    /// <summary>最初の部品へフォーカスして枠を出す。</summary>
    FocusFirst = 0,
    /// <summary>枠を出すだけ（動かさない・押さない）。</summary>
    Reveal = 1,
    /// <summary>そのまま効かせる（移す・値を変える・押す）。</summary>
    Act = 2,
}

/// <summary>入力の扱いの決まり（純粋な計算）。</summary>
public static class NavInputPolicy
{
    /// <summary>1 回の入力（方向・決定）の扱い。</summary>
    /// <param name="hasCurrent">今のフォーカスがあり、まだ使えるか。</param>
    /// <param name="ringVisible">枠を見せているか。</param>
    /// <param name="armed">枠を隠していても次の入力をすぐ効かせるか（指でホイール・入力欄を選んだ直後）。</param>
    public static NavInputDecision Decide(bool hasCurrent, bool ringVisible, bool armed)
    {
        if (!hasCurrent) return NavInputDecision.FocusFirst;
        return ringVisible || armed ? NavInputDecision.Act : NavInputDecision.Reveal;
    }

    /// <summary>方向キー・決定を読まないか（文字入力の間と、それが終わった次のフレーム）。</summary>
    /// <param name="capturedNow">今キーボードを入力欄などが受けているか。</param>
    /// <param name="capturedLastFrame">前のフレームに受けていたか。</param>
    public static bool IsSuspended(bool capturedNow, bool capturedLastFrame) => capturedNow || capturedLastFrame;

    /// <summary>押した向きを部品の値の増減へ回すか（部品の値の軸と同じ軸の向き）。</summary>
    /// <param name="adjustAxis">部品の値の軸。</param>
    /// <param name="direction">押した向き。</param>
    public static bool RoutesToAdjust(NavAxis adjustAxis, FocusDirection direction)
        => adjustAxis != NavAxis.None && adjustAxis == FocusDirections.AxisOf(direction);
}
