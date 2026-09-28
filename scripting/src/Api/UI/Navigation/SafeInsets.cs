namespace SEED.UI;

// ============================================================
//  SafeInsets.cs — 安全領域の辺の余白（キャンバスの単位＝dp。W2-7）
//
//  シート・覆い・下のタブの中身をジェスチャーバー・ステータスバーから離すための余白。背景は画面の端まで塗り、
//  中身の並べ方（CanvasStack の余白・高さ）にだけこの値を足す。
//  CanvasSafeArea（W2-1b）を使わない所: スクロールの窓の中を動く板（下からのシート）・中身に合わせて高さの決まる板
//  （上からの覆い・タブ）。前者は位置で縮み直し、後者は背景の板まで縮むため。画素 → dp は Screen.DpScale（dp のルートの前提）。
// ============================================================

/// <summary>安全領域の辺の余白（dp）。</summary>
public static class SafeInsets
{
    /// <summary>画面の下端から安全領域までの高さ（dp。安全領域が取れなければ 0）。</summary>
    public static float BottomUnits()
    {
        var safe = Screen.SafeArea;
        float bottomPx = Screen.Height - safe.YMax;
        return safe.width > 0f ? SheetMath.InsetUnits(bottomPx, Screen.DpScale) : 0f;
    }

    /// <summary>画面の上端から安全領域までの高さ（dp。安全領域が取れなければ 0）。</summary>
    public static float TopUnits()
    {
        var safe = Screen.SafeArea;
        return safe.width > 0f ? SheetMath.InsetUnits(safe.YMin, Screen.DpScale) : 0f;
    }
}
