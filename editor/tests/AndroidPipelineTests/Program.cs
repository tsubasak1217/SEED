using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace AndroidPipelineTests;

/// <summary>
/// Android のビルド・配置・起動の中核（editor/src/Android/。docs/android.md §4.6）の単体テスト。
///
/// 検証の柱:
///   1. adb の出力の解析と端末選び・ABI の選び方（AdbTests）
///   2. 実行計画: ハッシュ・前回の記録・明示指定から、どの工程を飛ばすか（PlanTests）
///   3. Gradle の引数（-P と環境変数）とアプリの識別情報の既定値・検査（GradleAndIdentityTests）
///   4. 同梱 .NET の dotnet-root 形式への組み立て（DotnetBundleTests。一時フォルダの偽のパック）
///   5. 指紋・tar・行の読み取り・記録・道具の解決・プロジェクトの読み取り・SeedAndroid の引数（InfrastructureTests）
///   6. 実行先の決め方（自動・エミュレータへの切り替え）・エミュレータの起動と待ち合わせ・AVD・起動するシーンの受け渡し
///      （EmulatorAndSceneTests。段階C-3）
/// 端末・adb・cargo・Gradle は使わない。
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    /// <returns>終了コード。</returns>
    public static int Main()
    {
        var harness = new TestHarness();
        AdbTests.Register(harness);
        PlanTests.Register(harness);
        GradleAndIdentityTests.Register(harness);
        DotnetBundleTests.Register(harness);
        InfrastructureTests.Register(harness);
        EmulatorAndSceneTests.Register(harness);
        return harness.Run();
    }
}
