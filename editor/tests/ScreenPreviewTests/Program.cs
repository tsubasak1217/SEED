using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace ScreenPreviewTests;

/// <summary>
/// Edit 上の画面プレビュー（docs/editor_screen_preview.md）のエディタ側の単体テストの入口。
///
/// 検証の柱:
///   1. IPC の組み立てと応答の解釈（ランタイムの ipc.rs / editor_preview/wire.rs と同じ書式）
///   2. 差し込み先の案内の表（同梱の JSON = 組み込みの表・照合・欄の値の当てはめ・スクリプトの既定とのずれ）
///   3. 右クリックの項目（普通のノード・プレビューの根・中・空白・最近の一覧・Play 中）
///   4. プレハブの一覧・検索・並びと、最近の一覧の規則・保存
///   5. ヒエラルキー・インスペクタの印の読み取り（旧 JSON・null・根・中）
///   6. Delete の振り分け（プレビューの根・中・普通のノードと番号のずれ）
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();
        IpcTests.Register(harness);
        HostCatalogTests.Register(harness);
        MenuModelTests.Register(harness);
        PrefabCatalogTests.Register(harness);
        RecentTests.Register(harness);
        FlagsTests.Register(harness);
        DeletionPlannerTests.Register(harness);
        HostSlotSelectorTests.Register(harness);
        return harness.Run();
    }
}
