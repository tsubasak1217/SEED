// ============================================================
//  AndroidEnginePaths.cs — エンジン側（リポジトリ）の Android 関連の置き場
//
//  【役割】
//  Android のビルドが読み書きするエンジン側のファイル・フォルダの場所を 1 か所で決める。
//  どれもリポジトリの中（runtime/android/ ほか）にあり、プロジェクトに依らず 1 つずつしかない
//  （Gradle の置き場は 1 つなので、別のプロジェクトをビルドすると中身が入れ替わる。
//  そのため「いま置き場に何が入っているか」の記録も置き場の隣に置く。State/AndroidStepStamps.cs）。
//
//  【リポジトリの探し方】
//  起点（ツール・エディタの exe の場所、カレントフォルダ）から上へ辿り、
//  runtime/Cargo.toml と runtime/android/gradlew.bat の両方を持つフォルダをリポジトリのルートとする。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Android.Toolchain;

/// <summary>エンジン側（リポジトリ）の Android 関連の置き場。</summary>
public sealed class AndroidEnginePaths
{
    /// <summary>上へ辿る段数の上限（深い一時フォルダへビルドしたツールからも見つけられる程度）。</summary>
    private const int MaxUpwardSearchDepth = 16;

    /// <summary>リポジトリのルート。</summary>
    public string RepositoryRoot { get; }

    /// <summary>runtime/。</summary>
    public string RuntimeDir => Path.Combine(RepositoryRoot, "runtime");

    /// <summary>runtime/android/（Gradle のプロジェクト）。</summary>
    public string AndroidDir => Path.Combine(RuntimeDir, "android");

    /// <summary>runtime/android/native/（libSEED.so を作る cdylib クレート。cargo ndk の作業フォルダ）。</summary>
    public string NativeCrateDir => Path.Combine(AndroidDir, "native");

    /// <summary>Gradle の wrapper（Windows 用）。</summary>
    public string GradleWrapper => Path.Combine(AndroidDir, "gradlew.bat");

    /// <summary>cargo ndk が .so を書き出す置き場（app/src/main/jniLibs/&lt;ABI&gt;/libSEED.so。生成物）。</summary>
    public string JniLibsDir => Path.Combine(AndroidDir, "app", "src", "main", "jniLibs");

    /// <summary>APK に同梱する配布物（assets.pak・bin/）の置き場（app/src/main/assets/seed/。生成物）。</summary>
    public string ApkPackageDir => Path.Combine(AndroidDir, "app", "src", "main", "assets", AndroidRuntimeContract.ApkPackageRootName);

    /// <summary>同梱 .NET の置き場（app/src/seedDotnet/。build.gradle.kts の sourceSets が jniLibs・assets として足す。生成物）。</summary>
    public string DotnetStagingDir => Path.Combine(AndroidDir, "app", "src", "seedDotnet");

    /// <summary>同梱 .NET の設定（版・パック名・coreclr / mono。唯一の置き場）。</summary>
    public string DotnetSettingsPath => Path.Combine(AndroidDir, "dotnet_runtime.json");

    /// <summary>Gradle の出力（デバッグ版 APK）。</summary>
    public string DebugApkPath => Path.Combine(AndroidDir, "app", "build", "outputs", "apk", "debug", "app-debug.apk");

    /// <summary>このツール群の作業用の生成物の置き場（app/build/seed/。gradlew clean で消える）。</summary>
    public string WorkDir => Path.Combine(AndroidDir, "app", "build", "seed");

    /// <summary>NuGet のパックを取り寄せるだけの一時プロジェクトの置き場。</summary>
    public string DotnetRestoreDir => Path.Combine(WorkDir, "dotnet_restore");

    /// <summary>スクリプトの DLL だけを作り直すときの SeedPak の出力先。</summary>
    public string PushScriptsStagingDir => Path.Combine(WorkDir, "push_scripts");

    /// <summary>置き場に何が入っているかの記録（State/AndroidStepStamps.cs）。</summary>
    public string StepStampsPath => Path.Combine(WorkDir, "step_stamps.json");

    /// <summary>プロジェクトが無いときの実行状態の置き場（State/AndroidRunState.cs）。</summary>
    public string FallbackRunStatePath => Path.Combine(WorkDir, "run_state.json");

    /// <summary>pak とスクリプトの DLL を作るコンソールツール（editor/tools/SeedPak）。</summary>
    public string SeedPakProjectDir => Path.Combine(RepositoryRoot, "editor", "tools", "SeedPak");

    /// <summary>リポジトリのルートを指定して作る。</summary>
    /// <param name="repositoryRoot">リポジトリのルート（runtime/ の親）。</param>
    public AndroidEnginePaths(string repositoryRoot)
    {
        RepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
    }

    /// <summary>
    /// 起点から上へ辿ってリポジトリを探す（最初に見つかったもの）。見つからなければ null。
    /// </summary>
    /// <param name="startDirectories">探し始めるフォルダ（先頭から順に試す。null・空は飛ばす）。</param>
    /// <returns>置き場。</returns>
    public static AndroidEnginePaths? Locate(params string?[] startDirectories)
    {
        foreach (var start in startDirectories)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            for (var depth = 0; directory is not null && depth < MaxUpwardSearchDepth; depth++, directory = directory.Parent)
            {
                var candidate = new AndroidEnginePaths(directory.FullName);
                if (candidate.LooksValid()) return candidate;
            }
        }
        return null;
    }

    /// <summary>リポジトリらしいか（runtime/Cargo.toml と Gradle の wrapper がある）。</summary>
    /// <returns>それらしければ true。</returns>
    public bool LooksValid() =>
        File.Exists(Path.Combine(RuntimeDir, "Cargo.toml")) && File.Exists(GradleWrapper);

    /// <summary>その ABI の libSEED.so の置き場（cargo ndk の出力）。</summary>
    /// <param name="abi">ABI。</param>
    /// <returns>.so のパス。</returns>
    public string NativeLibraryPath(AndroidAbi abi) =>
        Path.Combine(JniLibsDir, abi.Name, AndroidRuntimeContract.NativeLibraryFileName);
}
