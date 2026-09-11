// ============================================================
//  RuntimeSourceDirLocator.cs — ランタイム exe → cargo を回すフォルダ
//
//  【役割】
//  起動対象の SEED.exe から「cargo build を実行すべき runtime/ フォルダ」を求める。
//  runtime/.cargo/config.toml（target-dir / CRT 静的リンク）はカレントディレクトリで
//  解決されるため、cargo は必ずこのフォルダを作業ディレクトリにして起動する必要がある。
//
//  【なぜ独立させたか】
//  従来は RuntimeManager と PackagingWindow が同じ判定を別々に持ち、しかも
//  PackagingWindow 側は出力フォルダ名を "debug" / "release" で決め打ちしていた。
//  ビルド構成が増える（develop）とその決め打ちは静かに外れる（cargo を
//  runtime/target/develop で起動してしまう）。判定を 1 箇所に集めて、
//  出力フォルダ名ではなく **Cargo.toml の実在** で判断する。
// ============================================================

using System.IO;

namespace SEEDEditor.Runtime.BuildConfig;

/// <summary>
/// ランタイム exe のパスから、cargo の作業ディレクトリ（runtime/）を解決する。
/// </summary>
public static class RuntimeSourceDirLocator
{
    /// <summary>
    /// exe から runtime/ フォルダを解決する。
    ///
    /// <para>
    /// 開発配置は <c>runtime/target/&lt;構成&gt;/SEED.exe</c> なので 2 階層上が runtime/。
    /// その位置に Cargo.toml があることを確かめてから採用する。
    /// 配布形態（exe の隣に置かれた SEED.exe）では見つからないので null を返す。
    /// </para>
    /// </summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    /// <returns>runtime/ の絶対パス。判定できなければ null。</returns>
    public static string? FromExePath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        // runtime/target/<構成>/SEED.exe
        //         ↑      ↑        ← 2 階層上が runtime/
        var exeDir     = Path.GetDirectoryName(exePath) ?? "";   // target/<構成>
        var targetDir  = Path.GetDirectoryName(exeDir)  ?? "";   // target
        var runtimeDir = Path.GetDirectoryName(targetDir) ?? ""; // runtime
        if (runtimeDir.Length == 0) return null;

        var cargoToml = Path.Combine(runtimeDir, RuntimeExeLocator.CargoManifestFileName);
        return File.Exists(cargoToml) ? Path.GetFullPath(runtimeDir) : null;
    }
}
