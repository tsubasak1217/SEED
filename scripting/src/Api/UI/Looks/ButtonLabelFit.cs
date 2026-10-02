using System;

namespace SEED.UI;

// ============================================================
//  ButtonLabelFit.cs — ボタンの文字の枠をボタンの大きさに合わせる（2026-10-02。純粋な計算）
//
//  エンジンのレイアウトは文字の枠（Text.BoxWidth・BoxHeight）を伸ばさない（docs/canvas_camera_rework.md §6.3 の規則 3）ので、
//  コンテナが幅いっぱいに伸ばしたボタン（fill_width・Stretch・flex）では、文字の枠がプレハブの幅のまま左に寄り、真ん中寄せの文字が
//  ボタンの真ん中に来なかった（Wake or Pay の W3-3 (2)。プロジェクトは WrappedText.FitWidth で回避した）。
//  ここでは「プレハブでの文字の枠とボタンの大きさの差」を保ったまま、ボタンのレイアウトの大きさに合わせる:
//    文字の枠 = レイアウトの大きさ ＋（プレハブの文字の枠 − プレハブのボタンの大きさ）   （軸ごと。0 未満にしない）
//  button.actor（ボタン 120×48・文字の枠 120×48）なら枠 = ボタンの大きさ、dialog.actor のボタン（枠 320・真ん中に置いた文字）なら
//  差 232 を保つ（真ん中に置いた文字はどちらでも真ん中）。レイアウトの大きさがプレハブの大きさと同じ（伸ばしていない）なら枠は変わらない。
//  プレハブの枠が 0 の軸（枠なし＝文字の大きさで描く）は合わせない（0 のまま）。
// ============================================================

/// <summary>ボタンの文字の枠をボタンの大きさに合わせる計算。</summary>
public static class ButtonLabelFit
{
    /// <summary>
    /// 文字の枠（軸ごと）。
    /// </summary>
    /// <param name="layoutSize">ボタンのレイアウトの大きさ（CanvasTransform.LayoutSize。0 以下・有限でない軸はプレハブの枠のまま）。</param>
    /// <param name="baseSize">プレハブのボタンの大きさ（開始時の Sprite の大きさ）。</param>
    /// <param name="baseBox">プレハブの文字の枠（開始時の Text.BoxWidth・BoxHeight）。</param>
    public static Vector2 BoxSize(Vector2 layoutSize, Vector2 baseSize, Vector2 baseBox)
        => new(Axis(layoutSize.x, baseSize.x, baseBox.x), Axis(layoutSize.y, baseSize.y, baseBox.y));

    /// <summary>1 つの軸の枠。</summary>
    private static float Axis(float layout, float baseSize, float baseBox)
    {
        // 枠なし（0）の軸・測れない大きさはプレハブのまま
        if (!(float.IsFinite(baseBox) && baseBox > 0f)) return baseBox;
        if (!(float.IsFinite(layout) && layout > 0f) || !float.IsFinite(baseSize)) return baseBox;
        return Math.Max(0f, layout + (baseBox - baseSize));
    }
}
