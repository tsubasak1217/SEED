// ============================================================
//  MainWindow.Localization.cs — 文字列表（ローカライズ）パネルの配線と「パネルで開く規則」の読み込み
//
//  【配線】（docs/localization.md §15）
//    - アセットのフォルダを渡して既定の置き場（assets://locale）を開かせる
//    - 未保存の印（*）をタブ名へ写す（SpriteRig・アニメーションと同じ）
//    - 見本から作った（ファイルが増えた）らプロジェクトパネルを作り直す（テンプレートのインポートと同じ）
//    - プロジェクトパネルのダブルクリックを「パネルで開く規則」（editor/config/panel_open_rules.json）で
//      専用のパネルへ回す。規則は ContentId でパネルを指し、パネルは IFileOpenablePanel でファイルを受け取る
//      （パネルを足しても、ここに分岐を増やさない）
//  終了時の未保存の確認・監視の停止は OnWindowClosing、レイアウトの復元は LoadLayout が受け持つ。
// ============================================================

using System.Linq;
using AvalonDock.Layout;
using SEEDEditor.Panels;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>文字列表パネルの ContentId（layout.xml の鍵。変えない）。</summary>
    private const string LocalizationContentId = SEEDEditor.Assets.PanelOpenRuleCatalog.LocalizationPanel;

    /// <summary>文字列表パネルを配線する（起動時に 1 度）。</summary>
    private void InitLocalizationPanel()
    {
        PanelLocalization.SetAssetsPath(AssetsPath);

        // 未保存の印（*）を LayoutAnchorable のタイトルへ反映する
        PanelLocalization.TitleChanged += title =>
        {
            var anchorable = DockManager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(a => a.ContentId == LocalizationContentId);
            if (anchorable is not null) anchorable.Title = title;
        };

        // 見本から作った: 新しい locale フォルダをプロジェクトパネルの木へ出す
        PanelLocalization.FilesCreated += () => PanelProject.SetAssetsPath(AssetsPath);

        // ダブルクリックを専用のパネルへ回す（規則に当たったファイルだけ。ProjectPanel が判定する）
        PanelProject.PanelFileOpenRequested += OpenFileInPanel;
    }

    /// <summary>
    /// 規則で決まったパネル（ContentId）を前に出し、ファイルを渡す。パネルが見つからない・ファイルを受け取れない
    /// ときは、テキストとして開ける形式なら内蔵エディタで開く（ダブルクリックが何も起こさないことを避ける）。
    /// </summary>
    /// <param name="contentId">パネルの ContentId。</param>
    /// <param name="path">ファイルの絶対パス。</param>
    private void OpenFileInPanel(string contentId, string path)
    {
        EnsureScriptSidePanels();
        var anchorable = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (anchorable?.Content is IFileOpenablePanel panel)
        {
            ShowAnchorable(contentId);
            panel.OpenFile(path);
            return;
        }

        EditorLog.Write($"[パネルで開く] ContentId「{contentId}」のパネルがファイルを受け取れません。テキストで開きます: {path}");
        if (SEEDEditor.Panels.ScriptEditor.EditorLanguages.IsEditableExtension(System.IO.Path.GetExtension(path)))
            OnScriptFileOpened(path);
    }

    /// <summary>
    /// 「直上のフォルダ名で専用のパネルへ回す規則」を editor/config から読み込み、アプリ全体へ反映する。
    /// 読み込み自体は失敗しない（必ず組み込み既定＝locale/*.json → 文字列表へフォールバックする）。
    /// 詳細は docs/editor_project_panel.md §10。
    /// </summary>
    private static void LoadPanelOpenRuleCatalog()
    {
        var catalog = SEEDEditor.Assets.PanelOpenRuleCatalog.LoadFromDir(SEEDEditor.Settings.EditorPaths.ConfigDir);
        foreach (var warning in catalog.Warnings)
            EditorLog.Write($"[パネルで開く] {warning}");
        SEEDEditor.Assets.PanelOpenRuleCatalogProvider.UseCatalog(catalog);
        EditorLog.Write(
            $"[パネルで開く] 規則の読み込み完了 — source={catalog.SourcePath ?? "(組み込み既定)"}  規則={catalog.Rules.Count}");
    }
}
