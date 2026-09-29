namespace SEED.Platform;

/// <summary>
/// 戻るの手ぶりを始めた端（<see cref="BackGestureEvent.Edge"/>。予測型の戻る。W2 の手直し P1-3）。
/// Android の BackEvent.getSwipeEdge（EDGE_LEFT / EDGE_RIGHT、Android 16 の EDGE_NONE）。
/// </summary>
public enum BackEdge
{
    /// <summary>端からの手ぶりでない（ボタンの戻る〈Android 16 以上〉・知らない値・取れない）。</summary>
    None = 0,
    /// <summary>左の端から（画面の左端から右へスワイプ）。</summary>
    Left = 1,
    /// <summary>右の端から（画面の右端から左へスワイプ）。</summary>
    Right = 2,
}
