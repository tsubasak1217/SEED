// ============================================================
//  AndroidEditorPreferences.cs — エディタの設定のうち Android の実行に関するもの（段階C-3・D-1）
//
//  editor/settings/editor_preferences.json（EditorPreferences）の "android" 節として永続化する:
//    "android": { "emulator_avd": "seed_pixel6_api35", "ipc_port": 52735 }
//  プロジェクトではなくこの PC の好み（AVD はマシンごとに違う）なので、プロジェクト設定ではなくエディタの設定に置く。
//
//  【emulator_avd】実行先が Android（自動）で端末が無いとき（または選んだ端末が見えないとき）に起動する AVD。
//  未設定（null・空）なら emulator -list-avds の一覧から seed_pixel6_api35、無ければ先頭（中核の EmulatorAvdChooser）。
//
//  【ipc_port（段階D-1）】端末のランタイムが一時停止などの IPC を待ち受けるポート（端末の 127.0.0.1。エディタは
//  adb forward でつなぐ）。未設定なら既定（中核の Android/Ipc/AndroidIpcSettings.DefaultDevicePort）、0 なら使わない
//  （一時停止できない）。端末の別のアプリと同じポートがぶつかるときだけ変える。
//
//  設定の画面はまだ無い（docs/backlog.md）。手で書くときはエディタを閉じてから（開いている間の保存で上書きされる）。
// ============================================================

using System.Text.Json.Serialization;

namespace SEEDEditor.Settings;

/// <summary>エディタの設定のうち Android の実行に関するもの。</summary>
public sealed class AndroidEditorPreferences
{
    /// <summary>エミュレータを起動するときの AVD（null・空なら既定の規則）。</summary>
    [JsonPropertyName("emulator_avd")]
    public string? EmulatorAvd { get; set; }

    /// <summary>端末のランタイムが IPC を待ち受けるポート（null なら既定、0 なら使わない。段階D-1）。</summary>
    [JsonPropertyName("ipc_port")]
    public int? IpcPort { get; set; }
}
