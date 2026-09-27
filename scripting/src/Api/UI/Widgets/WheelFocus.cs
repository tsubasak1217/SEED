namespace SEED.UI;

// ============================================================
//  WheelFocus.cs — キーボードで動かすホイールの「今の相手」（W2-5）
//
//  キーボード（上下の矢印）は画面に 1 つだけのホイールへ届ける。相手は最後に指で触れた（ドラッグ・タップ）か、
//  スクリプトが Focus() したホイール。時刻ホイールは左右の矢印で自分の列の間を移す。
//  入力欄のフォーカス（W2-6）・画面のスタック（W2-7）ができたら、そちらのフォーカスの仕組みへ寄せる（docs/backlog.md）。
// ============================================================

/// <summary>キーボードで動かすホイール（最後に触れた・Focus したもの）。</summary>
public static class WheelFocus
{
    /// <summary>今の相手（無ければ null）。</summary>
    public static WheelPicker? Current { get; private set; }

    /// <summary>相手にする。</summary>
    public static void Set(WheelPicker wheel) => Current = wheel;

    /// <summary>相手が <paramref name="wheel"/> なら外す（破棄のとき）。</summary>
    public static void Clear(WheelPicker wheel)
    {
        if (ReferenceEquals(Current, wheel)) Current = null;
    }
}
