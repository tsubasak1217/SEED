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

    // ── 起動オプション（段階C-3。am start の extra → MainActivity → ネイティブ）──────────────

    /// <summary>
    /// am start の文字列の extra のうち、起動オプションとしてネイティブへ渡すものの接頭辞。
    /// MainActivity.java の LAUNCH_OPTION_EXTRA_PREFIX と一致させる（この接頭辞を外した名前が、ネイティブへ渡す JSON のキーになる）。
    /// </summary>
    public const string LaunchOptionExtraPrefix = "seed.";

    /// <summary>
    /// 起動オプション「起動するシーン」の JSON のキー（値はアセットルートからの相対パス。例 scenes/Main.scene）。
    /// runtime/src/engine/platform/launch_options.rs の SCENE_KEY と一致させる。
    /// </summary>
    public const string LaunchOptionSceneKey = "scene";

    /// <summary>起動するシーンの extra の名前（seed.scene）。</summary>
    public const string SceneExtraName = LaunchOptionExtraPrefix + LaunchOptionSceneKey;

    /// <summary>
    /// 起動オプション「エディタとの IPC を TCP で待ち受けるポート」の JSON のキー（値は 10 進の文字列。段階D-1）。
    /// runtime/src/engine/platform/launch_options.rs の IPC_PORT_KEY と一致させる。
    /// </summary>
    public const string LaunchOptionIpcPortKey = "ipc_port";

    /// <summary>IPC のポートの extra の名前（seed.ipc_port）。</summary>
    public const string IpcPortExtraName = LaunchOptionExtraPrefix + LaunchOptionIpcPortKey;

    // ── アプリの内部データフォルダ（run-as の作業フォルダ）の絶対パス ──────────────

    /// <summary>
    /// アプリの内部データフォルダの絶対パスの書式（{0}=アプリ ID。主ユーザー＝ユーザー 0。run-as の作業フォルダと同じ場所）。
    /// ランタイムへ絶対パスを渡す命令（IPC の SCREENSHOT）で使う。
    /// </summary>
    public const string AppDataDirFormat = "/data/user/0/{0}";

    /// <summary>
    /// IPC のスクリーンショットを端末に置く場所（内部データフォルダからの相対。アプリのキャッシュ。取り出した後に消す。段階D-1）。
    /// </summary>
    public const string RemoteScreenshotPath = "cache/seed_ipc_screenshot.png";

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
