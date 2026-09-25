// ============================================================
//  AndroidRunRequest.cs — Android のビルド・配置・起動の指定（何をどこへ・どの工程を飛ばすか）
//
//  【目的（goal）と工程】
//    Build   … libSEED.so → pak・スクリプト → 同梱 .NET → APK
//    Install … Build ＋ インストール（Gradle の installDebug と同じく、要ればビルドする）
//    Run     … Install ＋（開発用の転送）＋ 起動 ＋ logcat（エディタの「実行」と同じ一気通貫）
//    Push    … スクリプトの DLL（と --assets-dir のアセット）だけを送って起動し直す（APK は作り直さない）
//  どの工程も、入力が前回から変わっていなければ自動で飛ばす（Plan/AndroidBuildPlan.cs）。
//  明示の Skip*・No* は自動判定より強い。Rebuild は自動で飛ばすのをやめる（明示の Skip* はそれでも効く）。
//
//  【ビルドの種類と形式（段階D。docs/android.md §24）】
//    Variant = Debug（既定。デバッグ用の鍵・debuggable）/ Release（配布用。アップロード鍵で署名・Rust は --release）
//    Format  = Apk（既定）/ Aab（配布用の Build だけ）
//    署名の鍵は KeystorePath・KeyAlias（無ければ packaging_settings.json の android.signing）、パスワードは SigningSecrets
//    （メモリだけ。JSON に読み書きしない。無ければ環境変数）。食い違いの検査は AndroidRunPipeline.ValidateVariant。
//
//  コンソールツールの引数・設定 JSON（--config）・エディタの実行先セレクタ（段階C-2）が同じこの型を作る。
//  JSON のキーは snake_case（設定 JSON の書式）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Signing;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Pipeline;

/// <summary>何をするか。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AndroidRunGoal>))]
public enum AndroidRunGoal
{
    /// <summary>APK を作るまで。</summary>
    Build,

    /// <summary>APK を作って端末へ入れるまで。</summary>
    Install,

    /// <summary>作って入れて起動し、logcat を流す（一気通貫）。</summary>
    Run,

    /// <summary>スクリプトの DLL（と開発用のアセット）だけを送り、起動し直す（高速経路）。</summary>
    Push,
}

/// <summary>Android のビルド・配置・起動の指定。</summary>
public sealed record AndroidRunRequest
{
    /// <summary>何をするか。</summary>
    [JsonPropertyName("goal")]
    public AndroidRunGoal Goal { get; init; } = AndroidRunGoal.Run;

    /// <summary>
    /// プロジェクトフォルダ（.seedproj か assets/ を持つフォルダ、またはアセットルートそのもの）。
    /// APK に pak とスクリプトを入れる（パッケージ実行）。アプリの識別情報・画面の向きもここの設定から決める。
    /// </summary>
    [JsonPropertyName("project")]
    public string? ProjectDir { get; init; }

    /// <summary>
    /// 開発用: 端末の内部アプリ専用フォルダへ送るアセットフォルダ（project_settings.json を含む）。
    /// APK は pak の無い開発用になる。<see cref="ProjectDir"/> とは同時に指定できない（端末は APK の pak を優先するため）。
    /// </summary>
    [JsonPropertyName("assets_dir")]
    public string? AssetsDir { get; init; }

    /// <summary>
    /// 対象端末のシリアル（null なら、使える端末がちょうど 1 台のときそれ）。
    /// "auto"（<see cref="Adb.AndroidDeviceTarget.AutoSerial"/>）なら、実機（前回使ったものを優先）→ 起動中のエミュレータ →
    /// AVD を起動、の順で決める（段階C-3。端末の工程を行うときだけエミュレータを起動する）。
    /// </summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; init; }

    /// <summary>
    /// <see cref="Serial"/> の端末が adb に見えないとき、エミュレータ（起動中のもの、無ければ AVD を起動）で実行するか
    /// （段階C-3。エディタの実行先セレクタで特定の端末を選んだときに true。SeedAndroid の --serial は従来どおりエラー）。
    /// </summary>
    [JsonPropertyName("emulator_fallback")]
    public bool EmulatorFallback { get; init; }

    /// <summary>
    /// エミュレータを起動するときの AVD 名（null なら emulator -list-avds の一覧から既定の規則で選ぶ。
    /// Emulator/EmulatorAvdChooser.cs）。エディタはエディタの設定 android.emulator_avd を渡す。
    /// </summary>
    [JsonPropertyName("avd")]
    public string? Avd { get; init; }

    /// <summary>
    /// 端末で起動するシーン（アセットルートからの相対パス・assets:// の仮想パス・アセットルート内の絶対パス。
    /// null なら project_settings.json の開始シーン）。起動の工程で am start の extra（seed.scene）として渡す（段階C-3）。
    /// </summary>
    [JsonPropertyName("scene")]
    public string? ScenePath { get; init; }

    /// <summary>ビルドする ABI（null なら端末から決める。端末が無ければ両方）。</summary>
    [JsonPropertyName("abis")]
    public IReadOnlyList<string>? Abis { get; init; }

    /// <summary>
    /// Rust 側を --release でビルドする（開発用のビルドでも最適化した .so で試すとき。APK はデバッグ署名のまま）。
    /// 配布用（<see cref="Variant"/> = Release）は指定に関わらず --release（<see cref="OptimizesNative"/>）。
    /// </summary>
    [JsonPropertyName("release")]
    public bool Release { get; init; }

    /// <summary>
    /// ビルドの種類（段階D。docs/android.md §24）。Debug（既定）はデバッグ署名の開発用、Release は配布用
    /// （debuggable=false・INTERNET なし・アップロード鍵で署名・Rust は --release）。Release は端末で run-as が使えないので、
    /// 開発用の転送（--assets-dir・--push-scripts・push）とは一緒に使えない。
    /// </summary>
    [JsonPropertyName("variant")]
    [JsonConverter(typeof(JsonStringEnumConverter<AndroidBuildVariant>))]
    public AndroidBuildVariant Variant { get; init; } = AndroidBuildVariant.Debug;

    /// <summary>
    /// 配布物の形式（段階D）。Apk（既定）か Aab（Google Play へ出す形。配布用だけ。端末へは直接入れられないので build だけ）。
    /// </summary>
    [JsonPropertyName("format")]
    [JsonConverter(typeof(JsonStringEnumConverter<AndroidPackageFormat>))]
    public AndroidPackageFormat Format { get; init; } = AndroidPackageFormat.Apk;

    /// <summary>
    /// 配布用の署名のキーストア（段階D。null ならプロジェクトの packaging_settings.json の android.signing.keystore_path。
    /// 決め方は Signing/AndroidSigningResolver）。
    /// </summary>
    [JsonPropertyName("keystore")]
    public string? KeystorePath { get; init; }

    /// <summary>配布用の署名のキーの別名（段階D。null なら android.signing.key_alias）。</summary>
    [JsonPropertyName("key_alias")]
    public string? KeyAlias { get; init; }

    /// <summary>
    /// 配布用の署名のパスワード（段階D。メモリの中だけ。JSON には読み書きしない。null なら環境変数
    /// SEED_ANDROID_KEYSTORE_PASSWORD / SEED_ANDROID_KEY_PASSWORD）。エディタは保護保存から、SeedAndroid は対話の入力から入れる。
    /// </summary>
    [JsonIgnore]
    public AndroidSigningSecrets? SigningSecrets { get; init; }

    /// <summary>libSEED.so を --release で作るか（<see cref="Release"/> の指定か、配布用のビルド）。</summary>
    [JsonIgnore]
    public bool OptimizesNative => Release || Variant == AndroidBuildVariant.Release;

    /// <summary>libSEED.so のビルドを飛ばす。</summary>
    [JsonPropertyName("skip_rust_build")]
    public bool SkipNativeBuild { get; init; }

    /// <summary>APK の作成（pak とスクリプト・同梱 .NET・Gradle）を飛ばす。</summary>
    [JsonPropertyName("skip_gradle")]
    public bool SkipGradle { get; init; }

    /// <summary>インストールを飛ばす。</summary>
    [JsonPropertyName("no_install")]
    public bool NoInstall { get; init; }

    /// <summary>起動を飛ばす。</summary>
    [JsonPropertyName("no_launch")]
    public bool NoLaunch { get; init; }

    /// <summary>logcat を流さない。</summary>
    [JsonPropertyName("no_logcat")]
    public bool NoLogcat { get; init; }

    /// <summary>スクリプトの DLL だけを作り直して端末へ送る（Run でも使える。Push では常に行う）。</summary>
    [JsonPropertyName("push_scripts")]
    public bool PushScripts { get; init; }

    /// <summary>変更の有無で工程を自動で飛ばすのをやめる（すべて作り直し、入れ直す）。</summary>
    [JsonPropertyName("rebuild")]
    public bool Rebuild { get; init; }

    /// <summary>logcat を何秒流して終えるか（0 なら止められるまで）。</summary>
    [JsonPropertyName("logcat_seconds")]
    public int LogcatSeconds { get; init; }

    /// <summary>logcat の保存先（null なら保存しない。UTF-8）。</summary>
    [JsonPropertyName("log_file")]
    public string? LogFile { get; init; }

    /// <summary>
    /// 端末のランタイムがエディタとの IPC を待ち受けるポート（段階D-1）。起動の工程（Run・Push）で am start の extra
    /// seed.ipc_port として渡す。null なら既定（Ipc/AndroidIpcSettings.DefaultDevicePort）、0 なら渡さない（一時停止などは使えない）。
    /// </summary>
    [JsonPropertyName("ipc_port")]
    public int? IpcPort { get; init; }

    /// <summary>
    /// 端末のランタイムとの IPC の接続トークン（起動ごとの使い捨て。段階D-1）。起動の工程が am start の extra seed.ipc_token で
    /// 渡し、プロジェクトの run_state.json（ipc_launches）へ記録する。null なら中核が作る（SeedAndroid の run）。
    /// エディタは実行ごとに作って入れる（同じ値でつなぐため。AndroidRunController）。書式は Ipc/AndroidIpcToken。
    /// </summary>
    [JsonPropertyName("ipc_token")]
    public string? IpcToken { get; init; }
}
