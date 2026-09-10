// ============================================================================
//  ChainCatchEntry.cs
//  わらしべ連鎖で「餌になった魚」1 匹ぶんの値の控え（スナップショット）。
// ============================================================================

/// <summary>
/// わらしべ連鎖の途中で<b>餌になった魚 1 匹ぶんの控え</b>（値のスナップショット）
/// 【連鎖履歴の要素の唯一の型】。
///
/// 【なぜ <c>Fish</c> の参照ではなく値で持つのか】
/// 連鎖で乗り換えると、食べられた側の魚は <c>FishingController.SwapHookedFish</c> の中で
/// その場で <c>Actor.Destroy()</c> される。釣り上げ演出（<c>CatchPresenter</c>）まで
/// インスタンスは生き残らないので、表示・記録に必要な値だけをその瞬間に写し取っておく。
///
/// 【何を持つか】
/// 釣果パネル（<c>ResultPanel</c>）と図鑑登録（<c>FishRecords.RecordCatch</c>）が要る値だけ。
/// 図鑑画像は表示名から <c>FishCatalog</c> を引いて後から解決できるので持たない
/// （画像パスまで控えると、カタログを直したときに履歴側が古いままになる）。
///
/// 位置・向き・実体（<c>GameObject</c>）は持たない ―― 跳ねる演出を見せられるのは
/// 実体が残っている「最後に釣り上げた 1 匹」だけなので、控えには不要。
/// </summary>
public readonly struct ChainCatchEntry
{
    /// <summary>魚の表示名（図鑑・釣果パネル・記録キーに使う一次キー）。</summary>
    public string DisplayName { get; }

    /// <summary>個体の大きさ（cm）。書式化は <c>Fish.FormatSize</c> に一元化してある。</summary>
    public float DisplaySize { get; }

    /// <summary>サイズランク（"S" / "A" / "B" / "C"）。判定は <c>Fish.SizeRank</c> に一元化。</summary>
    public string SizeRank { get; }

    /// <summary>魚レベル（連鎖の段位。ログ・将来の演出差し替え用に控える）。</summary>
    public int Level { get; }

    /// <summary>値を指定して控えを作る。</summary>
    /// <param name="displayName">魚の表示名。</param>
    /// <param name="displaySize">個体の大きさ（cm）。</param>
    /// <param name="sizeRank">サイズランク。</param>
    /// <param name="level">魚レベル。</param>
    public ChainCatchEntry(string displayName, float displaySize, string sizeRank, int level)
    {
        DisplayName = displayName;
        DisplaySize = displaySize;
        SizeRank = sizeRank;
        Level = level;
    }

    /// <summary>
    /// 魚の実体から控えを作る【スナップショットの唯一の生成点】。
    /// 呼ぶのは魚を破棄する<b>前</b>であること。
    /// </summary>
    /// <param name="fish">控えを取る魚。</param>
    /// <returns>値を写し取った控え。</returns>
    public static ChainCatchEntry From(Fish fish)
        => new ChainCatchEntry(fish.DisplayName, fish.DisplaySize, fish.SizeRank, fish.Level);
}
