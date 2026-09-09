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
