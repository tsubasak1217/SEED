// ============================================================
//  PanelOpenRuleCatalogProvider.cs — パネルで開く規則のアプリ全体での共有
//
//  PanelOpenRuleCatalog（データ）をアプリで 1 つだけ持って使い回す。読み込むのは起動時の MainWindow
//  （LoadPanelOpenRuleCatalog）で、ここは置き場に徹する（ShellOpenCatalogProvider と同じ形。
//  このフォルダは AssetsRootProbeTests が丸ごと取り込むので、EditorPaths・EditorLog へ触らない）。
// ============================================================

namespace SEEDEditor.Assets;

/// <summary>
/// パネルで開く規則のアプリ全体での置き場（ShellOpenCatalogProvider と同じ形。読み込みは起動時の MainWindow）。
/// </summary>
public static class PanelOpenRuleCatalogProvider
{
    /// <summary>今の規則（UseCatalog が呼ばれるまでは組み込み既定）。</summary>
    public static PanelOpenRuleCatalog Current { get; private set; } = PanelOpenRuleCatalog.BuiltIn();

    /// <summary>規則を差し替える（起動時に 1 度だけ呼ぶ）。</summary>
    /// <param name="catalog">読み込み済みの規則。null は無視する。</param>
    public static void UseCatalog(PanelOpenRuleCatalog? catalog)
    {
        if (catalog is not null) Current = catalog;
    }
}
