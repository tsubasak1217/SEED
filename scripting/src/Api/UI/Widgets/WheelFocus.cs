namespace SEED.UI;

// ============================================================
//  WheelFocus.cs — キーボードで動かすホイールの「今の相手」（W2-5。W2-7 で UiFocus の窓口にした）
//
//  キーボード（上下の矢印）は画面に 1 つだけのホイールへ届ける。相手は最後に指で触れた（ドラッグ・タップ）か、
//  スクリプトが Focus() したホイール。時刻ホイールは左右の矢印で自分の列の間を移す。
//  W2-7: 相手の管理はフォーカスの範囲（UiFocus・FocusScope）へ寄せた。ホイールはいちばん前の範囲（上の画面・
//  開いているダイアログ）の中にあるときだけ今の相手になる（覆われた画面のホイールは矢印キーを受けない）。
//  API（Current・Set・Clear）は W2-5 のまま（ホイール・時刻ホイールのコードは変えていない）。
// ============================================================

/// <summary>キーボードで動かすホイール（最後に触れた・Focus したもの。UiFocus の窓口）。</summary>
public static class WheelFocus
{
    /// <summary>今の相手（今のフォーカスがホイールでなければ null）。</summary>
    public static WheelPicker? Current => UiFocus.Current as WheelPicker;

    /// <summary>相手にする（ホイールの範囲がいちばん前でなければ、その範囲が前に出たときの相手として覚える）。</summary>
    public static void Set(WheelPicker wheel) => UiFocus.Request(wheel);

    /// <summary>相手から外す（破棄のとき）。</summary>
    public static void Clear(WheelPicker wheel) => UiFocus.Release(wheel);
}
