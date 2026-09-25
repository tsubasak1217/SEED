// ============================================================
//  AndroidHotReloadTable.cs — 変わったファイル（アセットルートからの相対パス）を「どの差し替えにするか」の表（データ・純粋な処理）
//
//  【何を決めるか】（エディタの Android の実行中・SeedAndroid。docs/android.md §23）
//    Scripts … .cs。SeedPak --scripts-only で DLL を作り直し、端末の files/bin/ へ送って RELOAD_SCRIPTS
//    Scene   … .scene。シーンと参照するアセットのうち変わったものを files/assets へ送り、RELOAD_SCENE:{相対パス}
//              （端末で今動いているシーンのときだけ読み直す。違うシーンなら送っただけ＝遷移したときに反映）
//    Asset   … それ以外のアセット（画像・モデル・シェーダ 等）。変わったものを送って RELOAD_ASSET:{相対パス}
//              （端末での取り込み方＝キャッシュを捨てるだけか・シーンを読み直すかは、ランタイムの表 hot_reload/asset_kind.rs）
//    Ignore  … 端末に関係しないファイル（パッケージ化の設定・エディタの作業ファイル・一時ファイル・ビルドの生成物）
//  表（下の定数）だけを直せば変えられる。表に無い拡張子は Asset（送って取り込み方はランタイムに任せる）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEEDEditor.Android.HotReload;

/// <summary>変わったファイルの差し替えのしかた。</summary>
public enum AndroidHotReloadKind
{
    /// <summary>端末に関係しない（何もしない）。</summary>
    Ignore,

    /// <summary>スクリプト（DLL を作り直して RELOAD_SCRIPTS）。</summary>
    Scripts,

    /// <summary>シーン（送って RELOAD_SCENE:{相対パス}）。</summary>
    Scene,

    /// <summary>そのほかのアセット（送って RELOAD_ASSET:{相対パス}）。</summary>
    Asset,
}

/// <summary>変わったファイルの差し替えのしかたの表。</summary>
public static class AndroidHotReloadTable
{
    /// <summary>拡張子（小文字・ドット付き）→ 差し替えのしかた（表に無い拡張子は <see cref="AndroidHotReloadKind.Asset"/>）。</summary>
    private static readonly IReadOnlyDictionary<string, AndroidHotReloadKind> ExtensionKinds =
        new Dictionary<string, AndroidHotReloadKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"]    = AndroidHotReloadKind.Scripts,
            [".scene"] = AndroidHotReloadKind.Scene,
            // C# のプロジェクト・ソリューション（エディタが補完用に作ることがある。端末には送らない）
            [".csproj"] = AndroidHotReloadKind.Ignore,
            [".sln"]    = AndroidHotReloadKind.Ignore,
        };

    /// <summary>ファイル名 → 差し替えのしかた（拡張子の表より優先。大文字小文字を問わない）。</summary>
    private static readonly IReadOnlyDictionary<string, AndroidHotReloadKind> FileNameKinds =
        new Dictionary<string, AndroidHotReloadKind>(StringComparer.OrdinalIgnoreCase)
        {
            // パッケージ化の設定（収録の規則。端末のゲームは読まない）
            ["packaging_settings.json"] = AndroidHotReloadKind.Ignore,
        };

    /// <summary>
    /// 見ないフォルダ名（パスのどこかの階層に一致したら無視。ScriptAutoReloader と同じ組＝ビルドの生成物・VCS・エディタの作業フォルダ）。
    /// </summary>
    private static readonly IReadOnlyList<string> IgnoredDirectories = new[] { "obj", "bin", ".git", ".vs", "node_modules" };

    /// <summary>見ないファイル名の末尾（エディタの一時・控えのファイル。ScriptAutoReloader と同じ組）。</summary>
    private static readonly IReadOnlyList<string> IgnoredSuffixes = new[] { "~", ".tmp", ".swp", ".bak" };

    /// <summary>相対パスの区切り（照合用）。</summary>
    private const char PathSeparator = '/';

    /// <summary>
    /// 変わったファイルの差し替えのしかたを決める（純粋な処理）。
    /// </summary>
    /// <param name="relative">アセットルートからの相対パス（区切りは / か \）。</param>
    /// <returns>差し替えのしかた。</returns>
    public static AndroidHotReloadKind Classify(string relative)
    {
        var unified = relative.Replace('\\', PathSeparator).Trim(PathSeparator);
        if (unified.Length == 0) return AndroidHotReloadKind.Ignore;
        var segments = unified.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // フォルダの階層（最後の 1 つ＝ファイル名を除く）が見ないフォルダなら無視
        if (segments.Take(segments.Length - 1).Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return AndroidHotReloadKind.Ignore;
        }
        var fileName = segments[^1];
        if (IgnoredSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return AndroidHotReloadKind.Ignore;
        }
        if (FileNameKinds.TryGetValue(fileName, out var byName)) return byName;
        var extension = Path.GetExtension(fileName);
        return ExtensionKinds.TryGetValue(extension, out var byExtension) ? byExtension : AndroidHotReloadKind.Asset;
    }
}
