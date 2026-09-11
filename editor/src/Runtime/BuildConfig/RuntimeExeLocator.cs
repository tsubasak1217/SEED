// ============================================================
//  RuntimeExeLocator.cs — ビルド構成 → ランタイム exe の絶対パス
//
//  【役割】
//  「いま選ばれているビルド構成で、どの SEED.exe を起動するか」だけを決める。
//  ファイルの存在は探索の判断材料に使うが、最終的には
//  **存在しなくてもパスを返す**（まだビルドしていない構成が選ばれた直後は、
//  exe が無いのが正常な状態で、その後に cargo build が作るため）。
//  「ビルドが必要か」は <see cref="NeedsBuild"/> で別に判定する。
//
//  【探索順】
//   1. 環境変数 SEED_RUNTIME_EXE（実在するファイルを指しているときだけ採用）
//      — 計測・MCP から別ビルドの exe を使わせるための上書き。構成より強い。
//   2. エディタ exe と同じフォルダの SEED.exe（配布形態のエディタ）
//   3. <リポジトリ>/runtime/target/<構成の target_dir>/SEED.exe（開発形態）
//
//  【リポジトリの探し方】
//  従来はエディタ exe から 4 階層上（bin/<Cfg>/<tfm>/ → editor/ → リポジトリ）
//  と決め打ちしていた。しかし検証やツールから
//  `dotnet build -p:OutputPath=<一時フォルダ>` でビルドすると階層が変わり、
//  この決め打ちは必ず外れる。そこで
//  「runtime/Cargo.toml を持つフォルダ」を上へ辿って探し、
//  exe の側で見つからなければカレントディレクトリからも探す。
//  どちらでも見つからないときだけ、従来の 4 階層上へフォールバックする。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/RuntimeBuildConfigTests）からリンクして使う。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Runtime.BuildConfig;

/// <summary>
/// ビルド構成から、起動すべきランタイム exe の絶対パスを解決する。
/// </summary>
public static class RuntimeExeLocator
{
    // ── 名前の定数（マジックストリングの一元化）──────────────────

    /// <summary>
    /// ランタイム exe の探索先を上書きする環境変数名。
    ///
    /// 利用者のエディタが起動していると runtime/target/&lt;構成&gt;/SEED.exe は
    /// ロックされていて上書きできない。計測・自動検証で別の target-dir へ
    /// ビルドした SEED.exe を使いたい場合に、この環境変数へ絶対パスを入れて起動する。
    /// （docs/runtime_build_configs.md / docs/editor_mcp.md を参照）
    /// </summary>
    public const string EnvVarName = "SEED_RUNTIME_EXE";

    /// <summary>ランタイム実行ファイル名。</summary>
    public const string RuntimeExeFileName = "SEED.exe";

    /// <summary>リポジトリ直下のランタイムクレートのフォルダ名。</summary>
    public const string RuntimeDirName = "runtime";

    /// <summary>cargo のビルド出力ルート（runtime/.cargo/config.toml の target-dir と一致）。</summary>
    public const string TargetDirName = "target";

    /// <summary>Cargo マニフェストのファイル名（リポジトリ判定の目印）。</summary>
    public const string CargoManifestFileName = "Cargo.toml";

    /// <summary>
    /// リポジトリを上へ辿って探すときの最大階層数。
    /// C:\ まで無限に遡らないための上限（開発配置は 3〜4 階層で足りる）。
    /// </summary>
    private const int MaxUpwardSearchDepth = 10;

    /// <summary>
    /// 従来の決め打ち階層数。
    /// editor/bin/&lt;Cfg&gt;/&lt;tfm&gt;/SEEDEditor.exe から 4 階層上がリポジトリルート。
    /// リポジトリが探索で見つからなかったときの最後のフォールバックに使う。
    /// </summary>
    private const int LegacyRepoRootDepth = 4;

    // ── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// 現在のプロセス環境（エディタ exe の位置・カレント・環境変数）を使って
    /// ランタイム exe のパスを解決する。
    /// </summary>
    /// <param name="config">選択中のビルド構成。</param>
    public static string Resolve(RuntimeBuildConfig config)
        => Resolve(
            config,
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// ランタイム exe のパスを解決する（入力を明示する版。単体テスト用）。
    /// </summary>
    /// <param name="config">選択中のビルド構成。</param>
    /// <param name="editorBaseDir">エディタ exe の置き場（AppDomain.BaseDirectory 相当）。</param>
    /// <param name="currentDir">カレントディレクトリ（null なら使わない）。</param>
    /// <param name="envOverride">環境変数 SEED_RUNTIME_EXE の値（null / 空なら未設定）。</param>
    /// <returns>絶対パス。ファイルが存在しない場合もパスは返す。</returns>
    public static string Resolve(
        RuntimeBuildConfig config, string editorBaseDir, string? currentDir, string? envOverride)
    {
        ArgumentNullException.ThrowIfNull(config);

        // ① 環境変数による明示指定（実在するファイルのときだけ採用）。
        //    構成より強い。ここを構成より後ろに置くと、計測用の別ビルドを
        //    指しているのに構成側のパスへ戻ってしまう。
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return Path.GetFullPath(envOverride);

        // ② エディタ exe と同じフォルダ（配布形態のエディタ）。
        //    この形態では構成を切り替えても同梱の SEED.exe しか無いため、
        //    構成より優先して同梱物を使う。
        var sameDir = Path.Combine(editorBaseDir, RuntimeExeFileName);
        if (File.Exists(sameDir)) return Path.GetFullPath(sameDir);

        // ③ 開発形態: <リポジトリ>/runtime/target/<target_dir>/SEED.exe
        var repoRoot = FindRepoRoot(editorBaseDir)
                       ?? FindRepoRoot(currentDir)
                       ?? LegacyRepoRoot(editorBaseDir);

        return TargetExePath(repoRoot, config);
    }

    /// <summary>
    /// リポジトリルートと構成から、ランタイム exe のパスを組み立てる（存在は問わない）。
    /// </summary>
    /// <param name="repoRoot">リポジトリルート（runtime/ の親）。</param>
    /// <param name="config">ビルド構成。</param>
    public static string TargetExePath(string repoRoot, RuntimeBuildConfig config)
        => Path.GetFullPath(Path.Combine(
            repoRoot, RuntimeDirName, TargetDirName, config.TargetDir, RuntimeExeFileName));

    /// <summary>
    /// 指定フォルダから上へ辿り、<c>runtime/Cargo.toml</c> を持つフォルダ（リポジトリルート）を探す。
    /// 見つからなければ null。
    /// </summary>
    /// <param name="startDir">探索の起点（null / 空なら null を返す）。</param>
    public static string? FindRepoRoot(string? startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir)) return null;

        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(Path.GetFullPath(startDir)); }
        catch { return null; }

        for (var depth = 0; dir is not null && depth < MaxUpwardSearchDepth; depth++, dir = dir.Parent)
        {
            var manifest = Path.Combine(dir.FullName, RuntimeDirName, CargoManifestFileName);
            if (File.Exists(manifest)) return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// 従来の決め打ちでリポジトリルートを求める（探索で見つからなかったときの最後の手段）。
    /// editor/bin/&lt;Cfg&gt;/&lt;tfm&gt;/ から <see cref="LegacyRepoRootDepth"/> 階層上。
    /// </summary>
    /// <param name="editorBaseDir">エディタ exe の置き場。</param>
    private static string LegacyRepoRoot(string editorBaseDir)
    {
        var up = editorBaseDir;
        for (var i = 0; i < LegacyRepoRootDepth; i++)
            up = Path.Combine(up, "..");
        return Path.GetFullPath(up);
    }

    /// <summary>
    /// その exe をいま起動できるか（＝ ビルドが要るか）。
    /// パス解決とは分ける: 未ビルドの構成へ切り替えた直後は「パスは正しいが exe が無い」が正常。
    /// </summary>
    /// <param name="exePath">解決済みのランタイム exe パス。</param>
    /// <returns>exe が存在しないなら true（cargo build が必要）。</returns>
    public static bool NeedsBuild(string exePath)
        => string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath);
}
