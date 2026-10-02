// ============================================================
//  ProjectSettingsWindow.Verification.cs — 検証用の口（オフスクリーン描画のプローブから使う）
//
//  editor/tests/ProjectSettingsPreviewProbe が、窓を表示せずに（OnLoaded を通さずに）右パネルを組み立て、
//  画面の操作が書き込む先（保存前の設定）を読むための口。エディタ本体からは呼ばない
//  （TemplateActorPickerWindow.CreateForVerification と同じ流儀）。
// ============================================================

using System.Linq;

namespace SEEDEditor.ProjectSettings;

public partial class ProjectSettingsWindow
{
    /// <summary>
    /// 【検証用】小項目を含む大項目を開いてカテゴリツリーを作り、その小項目を選んだ状態にする（窓は表示しない）。
    /// </summary>
    /// <param name="subItemId">小項目の ID（例 "render_profile"）。</param>
    public void SelectSubItemForVerification(string subItemId)
    {
        var category = Categories.FirstOrDefault(c => c.SubItems.Any(s => s.Id == subItemId));
        if (category is not null) _expandedCategories.Add(category.Id);
        SelectSubItem(subItemId);
    }

    /// <summary>【検証用】編集中の設定（保存前。画面の操作が書き込む先）。</summary>
    public ProjectSettingsData DataForVerification => _data;
}
