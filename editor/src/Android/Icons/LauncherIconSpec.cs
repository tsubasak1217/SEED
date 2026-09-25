// ============================================================
//  LauncherIconSpec.cs — ランチャーのアイコンの生成物の決まり（密度の表・大きさ・ファイル名・XML。段階D）
//
//  【作るもの】（res/ の下。docs/android.md §24）
//    mipmap-<密度>/ic_launcher.png             … 従来型のアイコン（48dp。背景色の上に元の画像を収めたもの）
//    mipmap-<密度>/ic_launcher_foreground.png  … アダプティブアイコンの前景（108dp の透明な画像の中央の安全域 66dp に元の画像）
//    mipmap-anydpi-v26/ic_launcher.xml         … アダプティブアイコン（前景 ＋ 背景色）。API 26 以上の端末はこれを使う
//    values/ic_launcher_background.xml         … 背景色（@color/ic_launcher_background）
//  minSdk は 29 なので実際の端末はアダプティブアイコンだけを使うが、従来型も各密度で入れておく（ランチャー以外の表示・
//  道具の一覧で使われることがあるため）。
//
//  【大きさの出どころ】Android の密度の区分（mdpi = 160dpi を 1 倍とした倍率）と、アイコンの大きさの規格
//  （従来型 48dp、アダプティブの層 108dp・どの形に切り抜かれても見える安全域 66dp）。どちらも Android の仕様の値。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Android.Icons;

/// <summary>1 つの密度の区分。</summary>
/// <param name="Qualifier">リソースの修飾子（mdpi 等）。</param>
/// <param name="Scale">mdpi に対する倍率。</param>
public sealed record LauncherIconDensity(string Qualifier, double Scale)
{
    /// <summary>dp を画素にする（四捨五入）。</summary>
    /// <param name="dp">dp。</param>
    /// <returns>画素。</returns>
    public int Pixels(int dp) => (int)Math.Round(dp * Scale);
}

/// <summary>ランチャーのアイコンの生成物の決まり。</summary>
public static class LauncherIconSpec
{
    /// <summary>従来型のアイコンの大きさ（dp）。</summary>
    public const int LegacyIconDp = 48;

    /// <summary>アダプティブアイコンの層の大きさ（dp）。</summary>
    public const int AdaptiveLayerDp = 108;

    /// <summary>アダプティブアイコンの、どの形に切り抜かれても見える安全域の直径（dp）。前景の元の画像はこの正方形に収める。</summary>
    public const int AdaptiveSafeZoneDp = 66;

    /// <summary>従来型のアイコンのファイル名（リソース名 ic_launcher。マニフェストの @mipmap/ic_launcher）。</summary>
    public const string LegacyIconFileName = "ic_launcher.png";

    /// <summary>アダプティブアイコンの前景のファイル名。</summary>
    public const string ForegroundFileName = "ic_launcher_foreground.png";

    /// <summary>アダプティブアイコンの XML の置き場（res からの相対）。</summary>
    public const string AdaptiveIconRelativePath = "mipmap-anydpi-v26/ic_launcher.xml";

    /// <summary>背景色の XML の置き場（res からの相対）。</summary>
    public const string BackgroundColorRelativePath = "values/ic_launcher_background.xml";

    /// <summary>mipmap のフォルダ名の接頭辞。</summary>
    public const string MipmapFolderPrefix = "mipmap-";

    /// <summary>背景色のリソース名（@color/ic_launcher_background）。</summary>
    public const string BackgroundColorName = "ic_launcher_background";

    /// <summary>
    /// 密度の区分（Android の mdpi〜xxxhdpi。倍率は 160dpi を 1 とした値）。
    /// </summary>
    public static readonly IReadOnlyList<LauncherIconDensity> Densities = new[]
    {
        new LauncherIconDensity("mdpi", 1.0),
        new LauncherIconDensity("hdpi", 1.5),
        new LauncherIconDensity("xhdpi", 2.0),
        new LauncherIconDensity("xxhdpi", 3.0),
        new LauncherIconDensity("xxxhdpi", 4.0),
    };

    /// <summary>アダプティブアイコンの XML（前景と背景色。SeedAndroid が作る旨を書いておく）。</summary>
    public const string AdaptiveIconXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<!-- SeedAndroid が生成（プロジェクト設定 android.icon。手で直さない。docs/android.md §24） -->\n" +
        "<adaptive-icon xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
        "    <background android:drawable=\"@color/" + BackgroundColorName + "\" />\n" +
        "    <foreground android:drawable=\"@mipmap/ic_launcher_foreground\" />\n" +
        "</adaptive-icon>\n";

    /// <summary>
    /// 背景色の XML（{0} = #RRGGBB / #AARRGGBB）。
    /// </summary>
    public const string BackgroundColorXmlFormat =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<!-- SeedAndroid が生成（プロジェクト設定 android.icon_background。手で直さない。docs/android.md §24） -->\n" +
        "<resources>\n" +
        "    <color name=\"" + BackgroundColorName + "\">{0}</color>\n" +
        "</resources>\n";

    /// <summary>mipmap のフォルダ（res からの相対）。</summary>
    /// <param name="density">密度。</param>
    /// <returns>フォルダ名。</returns>
    public static string MipmapFolder(LauncherIconDensity density) => MipmapFolderPrefix + density.Qualifier;
}
