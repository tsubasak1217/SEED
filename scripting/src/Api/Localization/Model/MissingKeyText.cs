namespace SEED.Localization;

// ============================================================
//  MissingKeyText.cs — 欠けたキーの方針の既定の選び方と、方針どおりの文（純粋な計算）
// ============================================================

/// <summary>欠けたキーの方針の既定と、方針どおりの文。</summary>
public static class MissingKeyText
{
    /// <summary>Marked の囲みの始まり。</summary>
    public const string MarkOpen = "[";
    /// <summary>Marked の囲みの終わり。</summary>
    public const string MarkClose = "]";

    /// <summary>
    /// 既定の方針（デバッグ機能を許す実行〈エディタ・開発用のビルド〉は Marked、配布用のビルドは Key）。
    /// L10n は <c>SEED.Application.IsDebugAllowed</c> を渡す。
    /// </summary>
    /// <param name="debugAllowed">デバッグ機能を許す実行か。</param>
    /// <returns>既定の方針。</returns>
    public static MissingKeyPolicy DefaultPolicy(bool debugAllowed) =>
        debugAllowed ? MissingKeyPolicy.Marked : MissingKeyPolicy.Key;

    /// <summary>方針どおりの文。</summary>
    /// <param name="policy">方針。</param>
    /// <param name="key">欠けたキー。</param>
    /// <returns>返す文。</returns>
    public static string Render(MissingKeyPolicy policy, string key) => policy switch
    {
        MissingKeyPolicy.Marked => MarkOpen + key + MarkClose,
        MissingKeyPolicy.Empty => string.Empty,
        _ => key,
    };
}
