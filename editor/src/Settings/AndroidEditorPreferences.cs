// ============================================================
//  AndroidEditorPreferences.cs — エディタの設定のうち Android の実行に関するもの（段階C-3）
//
//  editor/settings/editor_preferences.json（EditorPreferences）の "android" 節として永続化する:
//    "android": { "emulator_avd": "seed_pixel6_api35" }
//  プロジェクトではなくこの PC の好み（AVD はマシンごとに違う）なので、プロジェクト設定ではなくエディタの設定に置く。
//
//  【emulator_avd】実行先が Android（自動）で端末が無いとき（または選んだ端末が見えないとき）に起動する AVD。
//  未設定（null・空）なら emulator -list-avds の一覧から seed_pixel6_api35、無ければ先頭（中核の EmulatorAvdChooser）。
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
}
