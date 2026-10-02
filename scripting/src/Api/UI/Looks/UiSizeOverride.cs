namespace SEED.UI;

// ============================================================
//  UiSizeOverride.cs — 部品ごとの大きさ・太さの上書きとテーマの既定の選び方（2026-10-02。純粋な計算）
//
//  部品の欄（ProgressRing.Thickness・ProgressSpinner.Size / Thickness など）は「0 以下 = テーマのトークンのまま」。
//  正の有限の値を書いた部品だけがその値を使う（テーマを替えても上書きした部品はその値のまま。上書きしていない部品はテーマに追従）。
// ============================================================

/// <summary>部品ごとの大きさ・太さの上書きの選び方。</summary>
public static class UiSizeOverride
{
    /// <summary>
    /// 上書き（正の有限の値）があればそれ、無ければテーマの値（有限でない・負のテーマの値は 0）。
    /// </summary>
    /// <param name="overrideValue">部品の欄の値（0 以下・有限でない = 上書きなし）。</param>
    /// <param name="themeValue">テーマのトークンの値。</param>
    public static float Resolve(float overrideValue, float themeValue)
    {
        if (float.IsFinite(overrideValue) && overrideValue > 0f) return overrideValue;
        return float.IsFinite(themeValue) && themeValue > 0f ? themeValue : 0f;
    }
}
