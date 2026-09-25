using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace AndroidRunUiTests;

/// <summary>
/// エディタからの Android の実行（段階C-2。docs/android.md §20）の「UI の判断」の単体テスト。
///
/// 検証の柱:
///   1. 実行先の一覧の組み立てと前回の選択の復元（RunTargetCatalogTests）
///   2. プレイバーのボタンの有効/無効・絵柄・文言と PC との排他（PlayBarPolicyTests）
///   3. 状態機械の遷移（StateMachineTests）と、停止・アプリの終了・失敗の段取り（ControllerTests。偽の中核）
///   4. Output パネルへ出す文言と色（OutputFormattingTests）
///   5. Android の実行先を使えるか・道具の一覧・パッケージ化の出力・中核への指定（EnvironmentAndPackagingTests）
///   6. Android（自動）・エミュレータへの切り替えの表示、起動するシーン、未保存の変更の確認（AutoTargetAndSceneTests。段階C-3）
///   7. Android の実行中の一時停止・再開（端末のアプリとの IPC。プレイバー・状態機械・段取り。IpcPauseTests。段階D-1）
///   8. Android の実行中の差し替え（変わったファイルのまとめ方・監視の始め方と止め方・Output。HotReloadControllerTests。§23）
///   9. パッケージ化の配布用（release の APK / AAB の名前・選択肢・中核への指定・署名のパスワードの保護保存。ReleasePackagingTests。§24）
///  10. Android の実行中のビューポート（PC のランタイムを隠して「Android で実行中（端末: …）」を出す。ViewportPolicyTests。§20.16）
///  11. 一時停止中の端末のシーンの写し（取り出しの状態機械と段取り・ビューポート・Output。PauseSnapshotTests。§20.17）
///  12. 写しをシーンパネルへ閲覧専用で出す段取り（応答の読み方・編集用ランタイムへの命令・一時停止に合わせた出し入れ・
///      閲覧専用の判断。SnapshotViewTests。§20.17）
/// 端末・adb・cargo・Gradle は使わない。
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    /// <returns>終了コード。</returns>
    public static int Main()
    {
        var harness = new TestHarness();
        RunTargetCatalogTests.Register(harness);
        PlayBarPolicyTests.Register(harness);
        StateMachineTests.Register(harness);
        ControllerTests.Register(harness);
        OutputFormattingTests.Register(harness);
        EnvironmentAndPackagingTests.Register(harness);
        AutoTargetAndSceneTests.Register(harness);
        IpcPauseTests.Register(harness);
        HotReloadControllerTests.Register(harness);
        ReleasePackagingTests.Register(harness);
        ViewportPolicyTests.Register(harness);
        PauseSnapshotTests.Register(harness);
        SnapshotViewTests.Register(harness);
        return harness.Run();
    }
}
