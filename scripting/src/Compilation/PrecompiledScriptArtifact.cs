// ============================================================
//  PrecompiledScriptArtifact.cs — 事前コンパイル成果物の規約
//
//  【役割】
//  パッケージ版に同梱する「ユーザースクリプト DLL」の名前と、
//  その中へ埋め込む **型マップ** の書式を 1 か所で定義する。
//
//  【型マップとは】
//  .scene には型ではなくソースファイルのパス（例 assets://ui/Title.cs）が保存される。
//  実行時にパスから型を引くには「パス → 型の FullName」の対応表が要るが、
//  パッケージ版にはソースを同梱しないため、対応表そのものを DLL の
//  マニフェストリソースとして焼き込んでおく。
//
//  【この規約に触れる場所】
//   ・生成 : ScriptAssemblyManager.CompileToFile（エディタのパッケージ化）
//   ・消費 : ScriptAssemblyManager.LoadPrecompiled（ランタイム起動時）
//   ・配置 : editor/src/Packaging/Scripts/ScriptPackager.cs（出力先へのコピー）
//   ・探索 : runtime/src/engine/core/scripting/mod.rs（PRECOMPILED_SCRIPTS_DLL_NAME）
//  ファイル名を変える場合は上記 4 か所すべてを合わせること。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace SEEDEditor.Scripting.Compilation;

/// <summary>
/// 事前コンパイル済みユーザースクリプト DLL の命名規約と、
/// 埋め込み型マップのシリアライズ形式。
/// </summary>
public static class PrecompiledScriptArtifact
{
    /// <summary>
    /// 事前コンパイル済みユーザースクリプト DLL のファイル名。
    /// Rust 側 <c>PRECOMPILED_SCRIPTS_DLL_NAME</c> と一致させること。
    /// </summary>
    public const string AssemblyFileName = "SEEDUserScripts.dll";

    /// <summary>ユーザースクリプトアセンブリの単純名（拡張子なし）。</summary>
    public const string AssemblyName = "SEEDUserScripts";

    /// <summary>DLL へ埋め込む型マップのマニフェストリソース名。</summary>
    public const string TypeMapResourceName = "SEEDUserScripts.typemap.txt";

    /// <summary>型マップ 1 行の区切り（キーと型名の間）。</summary>
    private const char FieldSeparator = '\t';

    /// <summary>型マップの改行（書き出し時。読み込みは \r\n も許容する）。</summary>
    private const string LineSeparator = "\n";

    /// <summary>
    /// 型マップをテキストへ直す。
    /// 1 行 = 「アセットルート相対キー(TAB)型の FullName」。
    /// </summary>
    /// <param name="entries">キーと型 FullName の対応。</param>
    /// <returns>UTF-8 で保存するテキスト。</returns>
    public static string SerializeTypeMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        foreach (var (key, typeName) in entries)
        {
            // 区切り文字が値に混ざると行が壊れるため、含む行は捨てる（キーは正規化済みなので通常発生しない）
            if (key.Contains(FieldSeparator) || typeName.Contains(FieldSeparator)) continue;
            sb.Append(key).Append(FieldSeparator).Append(typeName).Append(LineSeparator);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 型マップのテキストを解析する。壊れた行は黙って読み飛ばす
    /// （1 行の破損で全スクリプトが解決不能になるのを防ぐ）。
    /// </summary>
    /// <param name="text">SerializeTypeMap が書いたテキスト。</param>
    /// <returns>キーと型 FullName の組の列。</returns>
    public static IEnumerable<KeyValuePair<string, string>> ParseTypeMap(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ');
            if (line.Length == 0) continue;

            var sep = line.IndexOf(FieldSeparator);
            if (sep <= 0 || sep >= line.Length - 1) continue;

            var key      = line[..sep];
            var typeName = line[(sep + 1)..];
            if (key.Length == 0 || typeName.Length == 0) continue;

            yield return new KeyValuePair<string, string>(key, typeName);
        }
    }
}
