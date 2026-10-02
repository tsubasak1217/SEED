// ============================================================
//  DebugBuildMarkPolicy.cs — 開発用のビルドの印（pak の .seed/build.json）を入れるかの決め方
//
//  【何の印か】（書き手は Pak/PakBuildManifest.cs、読み手の正典は runtime/src/engine/pak/build_manifest.rs）
//  印のある pak で動くと、ランタイムはスクリプトの SEED.Application.IsDebugBuild を true にし、
//  IsDebugAllowed（= !IsPackaged || IsDebugBuild）が pak 実行でも true になる＝デバッグ用の機能が使える。
//  配布するパッケージでは外す。
//
//  【決め方】
//    デスクトップ（Windows / macOS / iOS）… packaging_settings.json の debug_build_mark（上書き）があればそれ、
//        無ければビルド種別に合わせる（Debug なら入れる・Release なら入れない＝2026-10-01 からの既定の挙動のまま）。
//        パッケージ化ウィンドウの「開発用のビルド」のチェックは、ビルド種別の既定と違う値を選んだときだけ上書きを保存する。
//        ビルド種別を選び直すと上書きを捨ててビルド種別に合わせ直す（Release へ切り替えたときに印が残らない側へ倒す）。
//    Android … ビルドの種類で決まる（開発用〈debug〉の APK は入れる・配布用〈release〉は入れない。
//        中核の AndroidRunRequest.MarksDebugBuild と同じ規則。配布前の検査が印のある配布物を不合格にする）。
//        パッケージ化ウィンドウでは決まり方を見せるだけで、上書きはできない。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Packaging;

/// <summary>開発用のビルドの印を入れるかの決め方【純粋な処理】。</summary>
public static class DebugBuildMarkPolicy
{
    // ── 決め方 ───────────────────────────────────────────────

    /// <summary>ビルド種別から決まる既定（Debug なら入れる。2026-10-01 からの挙動）。</summary>
    /// <param name="buildType">ビルド種別。</param>
    /// <returns>印を入れるなら true。</returns>
    public static bool AutomaticFor(BuildType buildType) => buildType == BuildType.Debug;

    /// <summary>実効（上書きがあればそれ、無ければビルド種別の既定）。</summary>
    /// <param name="buildType">ビルド種別。</param>
    /// <param name="markOverride">packaging_settings.json の debug_build_mark（null ＝ 上書きなし）。</param>
    /// <returns>印を入れるなら true。</returns>
    public static bool Resolve(BuildType buildType, bool? markOverride) => markOverride ?? AutomaticFor(buildType);

    /// <summary>
    /// 画面でチェックを変えたときに保存する上書き（ビルド種別の既定と同じなら null ＝ 欄を書かない）。
    /// </summary>
    /// <param name="buildType">ビルド種別。</param>
    /// <param name="chosen">チェックの状態。</param>
    /// <returns>保存する上書き。</returns>
    public static bool? OverrideFor(BuildType buildType, bool chosen) => chosen == AutomaticFor(buildType) ? null : chosen;

    /// <summary>Release なのに印を入れるか（画面に注意を出す条件）。</summary>
    /// <param name="buildType">ビルド種別。</param>
    /// <param name="markOverride">上書き。</param>
    /// <returns>注意が要るなら true。</returns>
    public static bool NeedsReleaseWarning(BuildType buildType, bool? markOverride) =>
        buildType == BuildType.Release && Resolve(buildType, markOverride);

    /// <summary>
    /// Android の印（ビルドの種類で決まる。中核の AndroidRunRequest.MarksDebugBuild と同じ規則。食い違いは
    /// editor/tests/AndroidRunUiTests が見張る）。
    /// </summary>
    /// <param name="variant">ビルドの種類。</param>
    /// <returns>印を入れるなら true。</returns>
    public static bool ForAndroid(AndroidBuildVariant variant) => variant == AndroidBuildVariant.Debug;

    // ── 画面の文言 ───────────────────────────────────────────

    /// <summary>チェックの文言（何が起きるか・配布版では外す）。</summary>
    public const string CheckBoxText =
        "SEED.Application.IsDebugAllowed を pak 実行でも真にする（デバッグ用の機能が使える。配布版では外す）";

    /// <summary>
    /// デスクトップの「いまの決まり方」の説明（自動か手で指定したか・ビルド種別の既定・ログの見方）。
    /// </summary>
    /// <param name="buildType">ビルド種別。</param>
    /// <param name="markOverride">上書き。</param>
    /// <returns>説明。</returns>
    public static string DescribeDesktop(BuildType buildType, bool? markOverride)
    {
        var automatic = AutomaticFor(buildType);
        var how = markOverride is null
            ? $"いまの決まり方: ビルド種別（{buildType}）に合わせて自動 → {MarkText(automatic)}"
            : $"いまの決まり方: 手で指定 → {MarkText(markOverride.Value)}（ビルド種別 {buildType} の自動なら{MarkText(automatic)}）";
        return how + "\n" +
               "自動の規則: ビルド種別が Debug なら入れる・Release なら入れない。ビルド種別を選び直すと自動に戻ります。\n" +
               "印（pak の .seed/build.json）があると SEED.Application.IsDebugBuild / IsDebugAllowed が pak 実行でも true になります。\n" +
               "入れたかはビルドのログの「開発用のビルドの印: 入れる／入れない」の行で分かります。";
    }

    /// <summary>Release で印を入れるときの注意。</summary>
    public const string ReleaseWarningText =
        "ビルド種別が Release でも開発用のビルドの印を入れます。このパッケージを配ると、利用者の手元でデバッグ用の機能が開きます。" +
        "配布するパッケージではチェックを外してください。";

    /// <summary>Android の「決まり方」の説明（ビルドの種類で決まる・ここでは変えられない）。</summary>
    /// <param name="variant">ビルドの種類。</param>
    /// <returns>説明。</returns>
    public static string DescribeAndroid(AndroidBuildVariant variant) =>
        $"いまの決まり方: ビルドの種類（{(variant == AndroidBuildVariant.Debug ? "開発用" : "配布用")}）で決まる → {MarkText(ForAndroid(variant))}\n" +
        "Android は、開発用（debug）の APK には必ず入れ、配布用（release）の APK / AAB には入れません（ここでは変えられません）。\n" +
        "配布前の検査（Google Play の要件）は、印のある配布物を不合格にします。";

    /// <summary>入れる／入れないの表示。</summary>
    /// <param name="marks">入れるか。</param>
    /// <returns>表示。</returns>
    private static string MarkText(bool marks) => marks ? "入れる" : "入れない";
}
