// ============================================================
//  AssetPathRewriter.cs — テキスト内の絶対パスを仮想パスへ書き換える
//
//  【役割】
//  エディタが保存したアセット（.scene / .actor / .json …）には
//  「C:\...\runtime\assets\...」という**このマシン固有の絶対パス**が入っている。
//  配布先ではそのパスは存在しないので、PAK へ入れる前に assets:// へ書き換える。
//
//  【4 形式に対応する理由】
//  同じ絶対パスでも、保存経路によって次の 4 通りの表記になる:
//    (1) C:/proj/assets/     … スラッシュ区切り
//    (2) C:\proj\assets\     … バックスラッシュ区切り
//    (3) C:\\proj\\assets\\  … JSON エスケープされたバックスラッシュ
//    (4) C:\/proj\/assets\/  … JSON エスケープされたスラッシュ
//  1 形式でも取りこぼすと、その参照だけパッケージ版で読めなくなる。
// ============================================================

using System;

namespace SEEDEditor.Packaging.Pak;

/// <summary>アセット絶対パスを仮想パス（assets://）へ書き換える。状態を持たない。</summary>
public static class AssetPathRewriter
{
    /// <summary>仮想パスのスキーム。</summary>
    public const string AssetsScheme = "assets://";

    /// <summary>
    /// テキスト中のアセットルート絶対パスを、4 形式すべて仮想パスへ書き換える。
    /// </summary>
    /// <param name="text">書き換え対象のテキスト。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>書き換え後のテキスト。</returns>
    public static string ToVirtual(string text, string assetsRoot)
    {
        var rootSlash = assetsRoot.Replace('\\', '/').TrimEnd('/');
        var rootBack  = rootSlash.Replace('/', '\\');

        // 長い（エスケープされた）表記から先に置換する。
        // 短い表記を先に消すと、エスケープ表記の一部だけが書き換わって壊れる。
        text = text.Replace(rootBack.Replace("\\", "\\\\") + "\\\\", AssetsScheme, StringComparison.OrdinalIgnoreCase); // (3)
        text = text.Replace(rootSlash.Replace("/", "\\/")  + "\\/",  AssetsScheme, StringComparison.OrdinalIgnoreCase); // (4)
        text = text.Replace(rootBack  + "\\",                        AssetsScheme, StringComparison.OrdinalIgnoreCase); // (2)
        text = text.Replace(rootSlash + "/",                         AssetsScheme, StringComparison.OrdinalIgnoreCase); // (1)
        return text;
    }
}
