using System.IO;
using System.Text.Json;
using SEEDEditor.Packaging;
using SpriteRigTests;

namespace ProjectSystemTests;

/// <summary>
/// パッケージ化の「開発用のビルドの印」の決め方（DebugBuildMarkPolicy）と、packaging_settings.json の
/// debug_build_mark の往復（2026-10-02。パッケージ化ウィンドウにチェックを出したとき）。
///
/// 守ること:
///   - 既定の挙動は変えない（上書きが無ければビルド種別に合わせる＝ Debug なら入れる・Release なら入れない）
///   - 既定と同じ値を選んだら欄を書かない／古いファイル（欄なし）は自動
///   - Android はビルドの種類で決まる（開発用だけ入れる）
/// </summary>
public static class DebugBuildMarkTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("開発用のビルドの印: 既定はビルド種別に合わせ、上書き・Release の注意・Android の規則を守る", PolicyRules);
        harness.Add("packaging_settings.json の debug_build_mark: 上書きが無ければ書かない・往復・古いファイルは自動", PackagingRoundTrip);
    }

    /// <summary>決め方の規則。</summary>
    private static void PolicyRules()
    {
        // 既定（上書きなし）は 2026-10-01 からの挙動のまま
        Check.True(DebugBuildMarkPolicy.Resolve(BuildType.Debug, null), "Debug は入れる");
        Check.True(!DebugBuildMarkPolicy.Resolve(BuildType.Release, null), "Release は入れない");

        // 上書き
        Check.True(DebugBuildMarkPolicy.Resolve(BuildType.Release, true), "Release でも上書きで入れられる");
        Check.True(!DebugBuildMarkPolicy.Resolve(BuildType.Debug, false), "Debug でも上書きで外せる");

        // 画面で選んだ値 → 保存する上書き（既定と同じなら null ＝ 欄を書かない）
        Check.True(DebugBuildMarkPolicy.OverrideFor(BuildType.Debug, true) is null, "Debug で入れる＝既定 → 書かない");
        Check.True(DebugBuildMarkPolicy.OverrideFor(BuildType.Release, false) is null, "Release で外す＝既定 → 書かない");
        Check.Equal<bool?>(false, DebugBuildMarkPolicy.OverrideFor(BuildType.Debug, false), "Debug で外す → false を書く");
        Check.Equal<bool?>(true, DebugBuildMarkPolicy.OverrideFor(BuildType.Release, true), "Release で入れる → true を書く");

        // 注意は Release で印を入れるときだけ
        Check.True(DebugBuildMarkPolicy.NeedsReleaseWarning(BuildType.Release, true), "Release ＋ 印 → 注意");
        Check.True(!DebugBuildMarkPolicy.NeedsReleaseWarning(BuildType.Release, null), "Release の既定 → 注意なし");
        Check.True(!DebugBuildMarkPolicy.NeedsReleaseWarning(BuildType.Debug, null), "Debug → 注意なし");

        // 説明は自動か手で指定かを書き分ける
        Check.True(DebugBuildMarkPolicy.DescribeDesktop(BuildType.Debug, null).Contains("自動"), "自動の説明");
        Check.True(DebugBuildMarkPolicy.DescribeDesktop(BuildType.Release, true).Contains("手で指定"), "手で指定の説明");

        // Android はビルドの種類で決まる
        Check.True(DebugBuildMarkPolicy.ForAndroid(AndroidBuildVariant.Debug), "開発用の APK は入れる");
        Check.True(!DebugBuildMarkPolicy.ForAndroid(AndroidBuildVariant.Release), "配布用は入れない");
    }

    /// <summary>packaging_settings.json の往復。</summary>
    private static void PackagingRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine(PackagingData.SettingsFileName);

        // 既定（上書きなし）は欄を書かない
        new PackagingData().SaveTo(path);
        Check.True(!File.ReadAllText(path).Contains("debug_build_mark"), "上書きが無ければ欄を書かない");

        // 上書きの往復（Windows は入れる・macOS は外す・iOS は自動のまま）
        var data = new PackagingData();
        data.Windows.DebugBuildMark = true;
        data.MacOs.DebugBuildMark = false;
        data.SaveTo(path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Check.Equal(JsonValueKind.True, doc.RootElement.GetProperty("windows").GetProperty("debug_build_mark").ValueKind, "windows");
            Check.Equal(JsonValueKind.False, doc.RootElement.GetProperty("macos").GetProperty("debug_build_mark").ValueKind, "macos");
            Check.True(!doc.RootElement.GetProperty("ios").TryGetProperty("debug_build_mark", out _), "ios は書かない");
        }
        var loaded = PackagingData.LoadFrom(path);
        Check.Equal<bool?>(true, loaded.Windows.DebugBuildMark, "windows の読み戻し");
        Check.Equal<bool?>(false, loaded.MacOs.DebugBuildMark, "macos の読み戻し");
        Check.True(loaded.Ios.DebugBuildMark is null, "ios は自動のまま");

        // 古いファイル（欄なし）は自動＝ビルド種別に合わせる（Debug なら入れる）
        File.WriteAllText(path, "{ \"windows\": { \"build_type\": \"Debug\" } }");
        var old = PackagingData.LoadFrom(path);
        Check.True(old.Windows.DebugBuildMark is null, "欄なしは上書きなし");
        Check.True(DebugBuildMarkPolicy.Resolve(old.Windows.BuildType, old.Windows.DebugBuildMark), "古い Debug の設定は引き続き印を入れる");
    }
}
