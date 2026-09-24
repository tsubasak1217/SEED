// ============================================================
//  AndroidToolchainReport.cs — Android のビルドに使う道具が見つかったかの一覧（パッケージ化ウィンドウの「道具」の欄）
//
//  道具の探し方の正典は editor/src/Android/Toolchain/AndroidToolchain.cs（環境変数 → 既定の場所。docs/android.md §3）。
//  ここはその結果を「見つかった場所／見つからない理由と対処」の行に並べるだけ（ファイルの有無だけを見る）。
//  cargo-ndk は一覧に無い（cargo ndk --version を libSEED.so のビルドの直前に確かめる）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.AndroidRun;

/// <summary>道具 1 つの見つかり方。</summary>
/// <param name="Name">道具の名前。</param>
/// <param name="Found">見つかったか。</param>
/// <param name="Detail">場所（見つからなければ理由と対処）。</param>
public sealed record AndroidToolStatus(string Name, bool Found, string Detail);

/// <summary>道具の見つかり方の一覧。</summary>
public static class AndroidToolchainReport
{
    /// <summary>見つかった道具の行の書式（{0}=名前、{1}=場所）。</summary>
    private const string FoundFormat = "見つかった    {0}: {1}";

    /// <summary>見つからない道具の行の書式（{0}=名前、{1}=理由）。</summary>
    private const string MissingFormat = "見つからない  {0}: {1}";

    /// <summary>
    /// 道具ごとに見つかったか調べる。
    /// </summary>
    /// <param name="toolchain">道具の場所。</param>
    /// <returns>道具ごとの結果（Android SDK・NDK・adb・JDK・cargo・dotnet の順）。</returns>
    public static IReadOnlyList<AndroidToolStatus> Build(AndroidToolchain toolchain) => new[]
    {
        Probe("Android SDK", toolchain.RequireSdk),
        Probe("Android NDK", toolchain.RequireNdk),
        Probe("adb", toolchain.RequireAdb),
        Probe("JDK", toolchain.RequireJavaHome),
        Probe("cargo", toolchain.RequireCargo),
        Probe("dotnet", toolchain.RequireDotnet),
    };

    /// <summary>一覧を複数行の文字列にする（説明欄用）。</summary>
    /// <param name="statuses">道具ごとの結果。</param>
    /// <returns>文字列。</returns>
    public static string Describe(IEnumerable<AndroidToolStatus> statuses) =>
        string.Join("\n", statuses.Select(status => string.Format(status.Found ? FoundFormat : MissingFormat, status.Name, status.Detail)));

    /// <summary>すべて見つかったか。</summary>
    /// <param name="statuses">道具ごとの結果。</param>
    /// <returns>すべて見つかれば true。</returns>
    public static bool AllFound(IEnumerable<AndroidToolStatus> statuses) => statuses.All(status => status.Found);

    /// <summary>道具 1 つを調べる。</summary>
    /// <param name="name">名前。</param>
    /// <param name="require">場所を返す（無ければ理由付きの例外）。</param>
    /// <returns>結果。</returns>
    private static AndroidToolStatus Probe(string name, Func<string> require)
    {
        try
        {
            return new AndroidToolStatus(name, true, require());
        }
        catch (AndroidPipelineException ex)
        {
            return new AndroidToolStatus(name, false, ex.Message);
        }
    }
}
