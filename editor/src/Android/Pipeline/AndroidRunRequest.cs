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
//  コンソールツールの引数・設定 JSON（--config）・エディタの実行先セレクタ（段階C-2）が同じこの型を作る。
//  JSON のキーは snake_case（設定 JSON の書式）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    /// <summary>Rust 側を --release でビルドする（APK はデバッグ署名のまま）。</summary>
    [JsonPropertyName("release")]
    public bool Release { get; init; }

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
}
