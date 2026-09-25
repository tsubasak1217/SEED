// ============================================================
//  PakEntryContent.cs — アセット 1 件を「pak に入れるときの中身」にする（パス書き換えの要否と書き換え。純粋な処理＋読み取り）
//
//  【なぜ 1 か所にするか】
//  pak へ入れるとき、.scene / .json などは中の絶対パスを assets:// へ書き換える（PakWriter。AssetPathRewriter）。
//  Android の実行中の差し替え（docs/android.md §23）で端末の上書き層 files/assets へ送るアセットも、pak の中身と
//  同じ形でなければならない（書き換えずに送ると、端末には存在しない C:\… のパスを読みに行く）。
//  書き換えの要否と手順を 2 か所に持つと片方だけ変わって食い違うので、ここ 1 か所にする（PakWriter と差し替えの両方が使う）。
//
//  WPF 非依存（SeedPak・SeedAndroid・単体テストがリンクで取り込む）。
// ============================================================

using System.IO;
using System.Text;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Packaging.Pak;

/// <summary>アセット 1 件の pak に入れるときの中身。</summary>
public static class PakEntryContent
{
    /// <summary>
    /// pak に入れるときにパスの書き換えをするアセットか（拡張子で決める。PackagingRules.PathRewriteExtensions）。
    /// </summary>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>書き換えるなら true（そのままのバイト列を入れるなら false）。</returns>
    public static bool NeedsRewrite(string relative) =>
        PackagingRules.PathRewriteExtensions.Contains(AssetPathUtil.GetExtensionLower(relative));

    /// <summary>
    /// 書き換え対象のアセットを読み、中の絶対パスを assets:// へ書き換えた UTF-8 のバイト列を返す
    /// （UTF-8 で読む＝BOM は落ちる・BOM なしの UTF-8 で書く。PakWriter の従来の手順そのまま）。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス（書き換えの基準。Collect に渡したものと同じ表記）。</param>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>書き換えた中身。</returns>
    /// <exception cref="IOException">読めない（呼び出し側は生のバイト列へ戻す）。</exception>
    public static byte[] ReadRewritten(string assetsRoot, string relative)
    {
        var text = File.ReadAllText(AssetPathUtil.ToAbsolute(assetsRoot, relative), Encoding.UTF8);
        return Encoding.UTF8.GetBytes(AssetPathRewriter.ToVirtual(text, assetsRoot));
    }
}
