// ============================================================
//  ScriptFixture.cs — テスト用の仮スクリプトツリー生成
//
//  【役割】
//  一時フォルダに「実プロジェクトの縮小版アセットルート」を作る。
//  事前コンパイル経路で一番危ないのは「別フォルダの同名ファイルの取り違え」
//  なので、同名 .cs を 2 か所へ置いた構成を既定にしてある。
//
//  【なぜ実ファイルを作るか】
//  コンパイル対象の収集はディレクトリ走査そのものなので、
//  ファイルシステムを模擬すると検証したい部分が丸ごと抜け落ちる。
// ============================================================

using System;
using System.IO;
using System.Text;
using SEEDEditor.Scripting;

namespace SEEDEditor.Tests.ScriptPrecompile;

/// <summary>
/// 一時フォルダに仮のスクリプトツリーを作り、破棄時に消す使い捨てフィクスチャ。
/// </summary>
public sealed class ScriptFixture : IDisposable
{
    /// <summary>アセットルートの絶対パス。</summary>
    public string Root { get; }

    /// <summary>事前コンパイル DLL の出力先フォルダ（アセットルートの外）。</summary>
    public string OutputDir { get; }

    /// <summary>一時フォルダを作る（中身は空。呼び出し側が Write で足す）。</summary>
    public ScriptFixture()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seed_script_precompile_" + Guid.NewGuid().ToString("N"));
        Root      = Path.Combine(baseDir, "assets");
        OutputDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(OutputDir);
    }

    /// <summary>
    /// 「別フォルダに同名ファイル」を含む標準構成を作る。
    ///   a/Foo.cs  … Alpha.Foo
    ///   b/Foo.cs  … Beta.Foo   （a と同じファイル名・別の型）
    ///   c/Solo.cs … Solo       （名前空間なし。型名・ファイル名解決の確認用）
    ///   d/Plain.cs… PlainHelper（スクリプトではない普通のクラス。型マップに載らない）
    /// </summary>
    public void BuildStandardTree()
    {
        WriteScript("a/Foo.cs",  "Alpha", "Foo");
        WriteScript("b/Foo.cs",  "Beta",  "Foo");
        WriteScript("c/Solo.cs", null,    "Solo");

        WriteText("d/Plain.cs", """
        namespace Helpers;

        /// <summary>スクリプトではない普通のクラス（型マップに載ってはいけない）。</summary>
        public class PlainHelper
        {
            public int Value => 1;
        }
        """);
    }

    /// <summary>
    /// SEEDScript を継承する最小のスクリプトを書く。
    /// </summary>
    /// <param name="relative">アセットルート相対パス。</param>
    /// <param name="namespaceName">名前空間（null なら名前空間なし）。</param>
    /// <param name="className">クラス名。</param>
    public void WriteScript(string relative, string? namespaceName, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using SEEDEditor.Scripting;");
        sb.AppendLine();
        if (namespaceName is not null) sb.AppendLine($"namespace {namespaceName};").AppendLine();
        sb.AppendLine("/// <summary>テスト用の最小スクリプト。</summary>");
        sb.AppendLine($"public class {className} : SEEDScript");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>解決できた型を見分けるための印。</summary>");
        sb.AppendLine($"    public string Mark => \"{namespaceName ?? "global"}.{className}\";");
        sb.AppendLine("}");

        WriteText(relative, sb.ToString());
    }

    /// <summary>テキストファイルを書く（親フォルダは自動作成）。</summary>
    /// <param name="relative">ルート相対パス。</param>
    /// <param name="text">中身。</param>
    public void WriteText(string relative, string text)
    {
        var abs = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>事前コンパイル DLL の出力先パスを返す。</summary>
    /// <param name="fileName">DLL のファイル名。</param>
    /// <returns>絶対パス。</returns>
    public string OutputPath(string fileName) => Path.Combine(OutputDir, fileName);

    // ── スクリプトホストのビルド出力（ScriptPackager の入力）────

    /// <summary>ホスト出力に置くダミーのデバッグシンボル名（同梱されないことの確認用）。</summary>
    private const string HostSymbolFileName = "SEEDScripting.pdb";

    /// <summary>ホスト出力に置く runtimeconfig の名前（.NET 同梱の入力になるファイル）。</summary>
    private const string HostRuntimeConfigFileName = "SEEDScripting.runtimeconfig.json";

    /// <summary>
    /// <see cref="SEEDEditor.Packaging.Scripts.ScriptPackager"/> が探す
    /// 「スクリプトホストのビルド出力」を模したフォルダを作る。
    ///
    /// <para>
    /// パッケージャは <c>{runtimePath}/../scripting/bin/Debug/net9.0/</c> を見るため、
    /// 一時フォルダにその形を作り、テストプロセスが実際に読み込んでいる本物の
    /// <c>SEEDScripting.dll</c> を置く（参照アセンブリとしても使われるので本物が要る）。
    /// あわせて runtimeconfig（同梱の入力）とダミーの <c>.pdb</c>
    /// （除外されることの確認用）を置く。
    /// </para>
    /// </summary>
    /// <returns>パッケージャへ渡す runtime フォルダの絶対パス。</returns>
    public string BuildHostBuildOutput()
    {
        var baseDir    = Path.GetDirectoryName(Root)!;
        var runtimeDir = Path.Combine(baseDir, "runtime");
        var hostDir    = Path.Combine(baseDir, "scripting", "bin", "Debug", "net9.0");
        Directory.CreateDirectory(runtimeDir);
        Directory.CreateDirectory(hostDir);

        // 本物の SEEDScripting.dll（テストプロセスがロード済みのもの）を写す
        var hostSource = typeof(SEEDScript).Assembly.Location;
        File.Copy(hostSource, Path.Combine(hostDir, Path.GetFileName(hostSource)), overwrite: true);

        // .NET 同梱フェーズが読む runtimeconfig（中身は最小限で十分）
        File.WriteAllText(
            Path.Combine(hostDir, HostRuntimeConfigFileName),
            """{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 配布物に含めてはいけないデバッグシンボル（除外されることの確認用）
        File.WriteAllText(Path.Combine(hostDir, HostSymbolFileName), "dummy");

        return runtimeDir;
    }

    /// <summary>一時フォルダごと削除する。</summary>
    public void Dispose()
    {
        try
        {
            // Root と OutputDir の親（テスト 1 回分の作業フォルダ）ごと消す
            var baseDir = Path.GetDirectoryName(Root)!;
            Directory.Delete(baseDir, recursive: true);
        }
        catch { /* 掃除に失敗してもテスト結果には影響させない */ }
    }
}
