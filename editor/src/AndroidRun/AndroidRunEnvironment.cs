// ============================================================
//  AndroidRunEnvironment.cs — エディタで Android の実行先を使えるか（エンジンの置き場・道具・プロジェクト）を調べる
//
//  【使えない場合】（どれもエラーにはせず、実行先セレクタに PC と理由の行だけを出す）
//    - プロジェクトが開かれていない
//    - エンジンのリポジトリ（runtime/Cargo.toml と runtime/android/gradlew.bat）が見つからない
//      （配布版のエディタなど。Android のビルドはリポジトリの Gradle・cargo の置き場を使う）
//    - adb が見つからない（Android SDK が無い・platform-tools が無い）
//  NDK・JDK・cargo・dotnet が無いことはここでは見ない（端末の一覧は adb だけで出せる。足りない道具は
//  それを使う工程で、対処付きのエラーとして Output パネルへ出る）。
//
//  ファイルの有無を見るだけ（プロセスは起動しない）なので、エディタの起動時に呼んでよい。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.AndroidRun;

/// <summary>Android の実行先の使える・使えないと、その材料。</summary>
/// <param name="Engine">エンジン側の置き場（見つからなければ null）。</param>
/// <param name="Toolchain">道具の場所（見つからない道具は理由を持つ）。</param>
/// <param name="Availability">使えるか（使えなければ理由）。</param>
public sealed record AndroidRunEnvironment(AndroidEnginePaths? Engine, AndroidToolchain Toolchain, AndroidTargetAvailability Availability)
{
    /// <summary>プロジェクトが無いときの理由。</summary>
    public const string NoProjectReason = "プロジェクトが開かれていません。";

    /// <summary>エンジンのリポジトリが無いときの理由。</summary>
    public const string NoEngineReason =
        "エンジンのリポジトリ（runtime/android）が見つかりません。Android の実行は、リポジトリの中でビルドしたエディタから使えます。";

    /// <summary>
    /// 調べる。
    /// </summary>
    /// <param name="projectRoot">プロジェクトのルート（空なら使えない）。</param>
    /// <param name="toolchain">道具の場所（AndroidToolchain.Detect の結果）。</param>
    /// <param name="engineSearchStarts">リポジトリを探し始めるフォルダ（エディタの exe の場所・カレント）。</param>
    /// <returns>結果。</returns>
    public static AndroidRunEnvironment Detect(string? projectRoot, AndroidToolchain toolchain, params string?[] engineSearchStarts)
    {
        var engine = AndroidEnginePaths.Locate(engineSearchStarts);
        return new AndroidRunEnvironment(engine, toolchain, Judge(projectRoot, engine, toolchain));
    }

    /// <summary>
    /// 使えるかを決める（見る順: プロジェクト → リポジトリ → adb）。
    /// </summary>
    /// <param name="projectRoot">プロジェクトのルート。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="toolchain">道具の場所。</param>
    /// <returns>使えるか。</returns>
    public static AndroidTargetAvailability Judge(string? projectRoot, AndroidEnginePaths? engine, AndroidToolchain toolchain)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return AndroidTargetAvailability.Unavailable(NoProjectReason);
        if (engine is null) return AndroidTargetAvailability.Unavailable(NoEngineReason);
        try
        {
            toolchain.RequireAdb();
        }
        catch (AndroidPipelineException ex)
        {
            return AndroidTargetAvailability.Unavailable(ex.Message);
        }
        return AndroidTargetAvailability.Available;
    }
}
