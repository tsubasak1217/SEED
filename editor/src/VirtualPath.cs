// ============================================================
//  VirtualPath.cs — アセット仮想パスユーティリティ
//
//  【仮想パス形式】
//  "assets://textures/player.png"
//   ↑ スキーム      ↑ アセットルートからの相対パス（'/' 区切り）
//
//  【設計方針】
//  ・エディタ内部では絶対パスで扱う（ファイルダイアログ等）
//  ・Rust ランタイムへの IPC 送信時と JSON 保存時は仮想パスへ変換する
//  ・Rust 側は assets:// プレフィックスを見てアセットルートからの相対パスとして解釈する
// ============================================================

using System.IO;

namespace SEEDEditor;

/// <summary>
/// アセット仮想パスの変換ユーティリティ。
/// </summary>
public static class VirtualPath
{
    /// <summary>仮想パスのスキーム。</summary>
    public const string Scheme = "assets://";

    // ── 変換 ─────────────────────────────────────────────────

    /// <summary>
    /// 絶対パスを仮想パスへ変換する。
    /// アセットルート配下でない場合は元の絶対パスをそのまま返す。
    /// </summary>
    /// <param name="absolutePath">変換元の絶対パス。</param>
    /// <param name="assetsRoot">アセットルートディレクトリの絶対パス。</param>
    public static string ToVirtual(string absolutePath, string assetsRoot)
    {
        if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(assetsRoot))
            return absolutePath;

        // 区切り文字を '/' に統一して比較する
        var abs  = absolutePath.Replace('\\', '/');
        var root = assetsRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/";

        if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Scheme + abs[root.Length..];

        return absolutePath; // アセット外 → そのまま返す
    }

    /// <summary>
    /// 仮想パスを絶対パスへ変換する。
    /// 仮想パスでない場合はそのまま返す。
    /// </summary>
    /// <param name="virtualOrAbsolute">変換元パス（仮想 or 絶対）。</param>
    /// <param name="assetsRoot">アセットルートディレクトリの絶対パス。</param>
    public static string ToAbsolute(string virtualOrAbsolute, string assetsRoot)
    {
        if (string.IsNullOrEmpty(virtualOrAbsolute))
            return virtualOrAbsolute;

        if (virtualOrAbsolute.StartsWith(Scheme, StringComparison.Ordinal))
        {
            var relative = virtualOrAbsolute[Scheme.Length..].Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(assetsRoot, relative));
        }

        return virtualOrAbsolute; // すでに絶対パス
    }

    // ── 判定 ─────────────────────────────────────────────────

    /// <summary>仮想パスかどうかを返す。</summary>
    public static bool IsVirtual(string path) =>
        path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>
    /// パスを UI 表示用の短い文字列に変換する。
    /// 仮想パスはそのまま、絶対パスはアセットルート相対にする。
    /// </summary>
    public static string ToDisplay(string path, string assetsRoot)
    {
        if (IsVirtual(path)) return path;
        return ToVirtual(path, assetsRoot);
    }
}
