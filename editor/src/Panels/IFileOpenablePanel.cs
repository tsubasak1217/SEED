// ============================================================
//  IFileOpenablePanel.cs — ファイルを渡して開けるドッキングパネルの約束
//
//  プロジェクトパネルのダブルクリックを「専用のパネル」へ回す規則（editor/config/panel_open_rules.json。
//  Assets/PanelOpenRuleCatalog）は、開くパネルを ContentId で指す。MainWindow はその ContentId のパネルを
//  前に出し、中身がこの約束を満たしていれば OpenFile へパスを渡す。パネルを足すときは、この約束を満たして
//  規則の JSON に 1 行足すだけでよい（MainWindow に分岐を増やさない）。
// ============================================================

namespace SEEDEditor.Panels;

/// <summary>ファイルを渡して開けるドッキングパネル。</summary>
public interface IFileOpenablePanel
{
    /// <summary>ファイルを開く（プロジェクトパネルのダブルクリックから）。</summary>
    /// <param name="path">ファイルの絶対パス。</param>
    void OpenFile(string path);
}
