// ============================================================
//  AndroidRuntimeContract.cs — Android ランタイム（APK・端末側のコード）と一致させる名前と値
//
//  【役割】
//  Android のビルド・配置・起動（SeedAndroid / エディタの実行先「実機・エミュレータ」）が前提にする、
//  他のファイルに正典がある値をここに集める。値を変えるときは「一致させる相手」も必ず直す。
//  以前は runtime/android/build_and_run.ps1 の先頭に同じ定数が並んでいた。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Android;

/// <summary>Android ランタイムと一致させる名前と値。</summary>
public static class AndroidRuntimeContract
{
    // ── アプリ ─────────────────────────────────────────────

    /// <summary>
    /// アプリ ID の既定値（プロジェクトから ID を決められないとき。runtime/android/app/build.gradle.kts の
    /// 既定値 defaultApplicationId と同じ）。Java のクラスの名前空間（namespace）もこの名前のまま変えない。
    /// </summary>
    public const string DefaultApplicationId = "com.seedengine.runtime";

    /// <summary>
    /// 起動する Activity のクラス名（完全修飾）。アプリ ID を変えてもクラスの名前空間は
    /// build.gradle.kts の namespace（com.seedengine.runtime）のままなので、am start には完全修飾名で渡す
    /// （"&lt;アプリ ID&gt;/.MainActivity" の省略形はアプリ ID を名前空間とみなすため、ID を変えると外れる）。
    /// </summary>
    public const string LaunchActivityClassName = "com.seedengine.runtime.MainActivity";

    /// <summary>cargo ndk がリンクする Android API レベル（build.gradle.kts の seedMinSdk と同じ）。</summary>
    public const int MinApiLevel = 29;

    /// <summary>エンジンの共有ライブラリのファイル名（runtime/android/native の [lib] name と MainActivity の loadLibrary）。</summary>
    public const string NativeLibraryFileName = "libSEED.so";

    // ── APK の中の配布物（runtime/android/native/src/apk_package/mod.rs の APK_PACKAGE_ROOT）─────────

    /// <summary>APK の assets/ の中で配布物のルートにするフォルダ名。</summary>
    public const string ApkPackageRootName = "seed";

    // ── 端末の置き場（run-as の作業フォルダ＝アプリの内部データフォルダからの相対）──────────────

    /// <summary>
    /// 開発用のアセットの送り先（runtime/android/native/src/launch.rs が最優先で見る置き場）。
    /// </summary>
    public const string RemoteAssetsDir = "files/assets";

    /// <summary>
    /// スクリプトの DLL の差し替えの送り先（runtime/android/native/src/dotnet_runtime/script_sources.rs の最優先の候補）。
    /// 外部アプリ専用フォルダは実機でアプリから読めないため使わない（docs/android.md §17.7）。
    /// </summary>
    public const string RemoteScriptsDir = "files/bin";

    /// <summary>アセットの転送後に「置けたか」を確かめるファイル（プロジェクト設定。必ずある）。</summary>
    public const string AssetsCheckFileName = "project_settings.json";

    /// <summary>スクリプトの転送後に「置けたか」を確かめるファイル（スクリプトホスト。これがある置き場が選ばれる）。</summary>
    public const string ScriptsCheckFileName = "SEEDScripting.dll";

    /// <summary>
    /// 端末へ送るスクリプトの DLL（SeedPak --scripts-only の bin/ のうち端末が読むもの。
    /// core::scripting::script_binaries が読む SEEDScripting.dll・SEEDUserScripts.dll・runtimeconfig を含む）。
    /// </summary>
    public static readonly IReadOnlyList<string> ScriptBinaryPatterns = new[] { "*.dll", "*.runtimeconfig.json" };

    // ── logcat ────────────────────────────────────────────

    /// <summary>
    /// logcat の絞り込み（タグ:最低の重要度）。SEED = エンジン・糊・MainActivity、DOTNET = 同梱 CoreCLR の Console
    /// （C# スクリプトの SEED.Debug.Log。Mono は標準出力経由で SEED）、RustPanic = android-activity が受け止めた panic。
    /// ほかは Java 例外・ネイティブクラッシュ・Activity の起動終了の手掛かり。最後の *:S で他のタグを黙らせる。
    /// </summary>
    public static readonly IReadOnlyList<string> LogcatFilters = new[]
    {
        "SEED:V", "DOTNET:V", "RustPanic:V", "GameActivity:V", "AndroidRuntime:E", "DEBUG:V", "libc:F", "vulkan:W",
        "ActivityTaskManager:I", "*:S",
    };

    /// <summary>
    /// logcat -T に渡す「この時刻以降」を端末の date で作る書式（logcat の -v threadtime と同じ並び）。
    /// 共用の端末で logcat -c（全消去）をしないため、起動直前の端末の時刻を控えて今回分だけを取り出す。
    /// </summary>
    public const string LogcatSinceDateFormat = "+%m-%d %H:%M:%S.000";
}
