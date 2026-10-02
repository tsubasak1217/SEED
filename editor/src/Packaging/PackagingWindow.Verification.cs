// ============================================================
//  PackagingWindow.Verification.cs — 検証用の口（オフスクリーン描画のプローブから使う）
//
//  editor/tests/ProjectSettingsPreviewProbe が、窓を表示せずに（OnLoaded を通さずに）設定ペインを組み立て、
//  画面の操作が書き込む先（保存前のパッケージ化の設定）を読むための口。エディタ本体からは呼ばない
//  （TemplateActorPickerWindow.CreateForVerification と同じ流儀）。
// ============================================================

namespace SEEDEditor.Packaging;

public partial class PackagingWindow
{
    /// <summary>【検証用】プラットフォームの一覧を作り、そのプラットフォームを選んだ状態にする（窓は表示しない）。</summary>
    /// <param name="platform">選ぶプラットフォーム。</param>
    public void SelectPlatformForVerification(TargetPlatform platform)
    {
        BuildPlatformList();
        SelectPlatform(platform);
    }

    /// <summary>【検証用】編集中のパッケージ化の設定（保存前。画面の操作が書き込む先）。</summary>
    public PackagingData DataForVerification => _data;
}
