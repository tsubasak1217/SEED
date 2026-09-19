// ============================================================
//  ShellOpenCatalogProvider.cs — 関連付けカタログのアプリ全体での共有
//
//  【役割】
//  ShellOpenCatalog（データ）を、アプリで 1 つだけ持って使い回す。
//  読み込むのは起動時の MainWindow（LoadShellOpenCatalog）で、ここは置き場に徹する。
//
//  【なぜ自分で読みに行かないのか】
//  このフォルダ（editor/src/Assets/）は「WPF にもエディタのシングルトンにも
//  依存しない純ロジック」の置き場で、editor/tests/AssetsRootProbeTests が
//  **ワイルドカードで丸ごと取り込んでビルドしている**。
//  ここで EditorPaths や EditorLog へ触ると、無関係なテストのビルドが壊れる
//  （実際に一度壊して気づいた）。読み込みと記録は呼び出し側の責務。
//
//  【前例】
//  Panels/ScriptEditor/EditorLanguage.cs の EditorLanguages。
//  「カタログはデータ、現在値の保持は薄い静的クラス、読み込みは MainWindow」
//  という同じ形にそろえてある。
// ============================================================

namespace SEEDEditor.Assets;

/// <summary>
/// 関連付けカタログのアプリ全体での置き場（静的クラス）。
/// </summary>
public static class ShellOpenCatalogProvider
{
    /// <summary>
    /// 現在有効なカタログ。<see cref="UseCatalog"/> が呼ばれるまでは組み込み既定。
    ///
    /// <para>
    /// 既定を最初から入れておくことで、構成の読み込みより先に
    /// ダブルクリックされても（あるいはヘッドレス起動で読み込みを通らなくても）
    /// 機能が死なない。
    /// </para>
    /// </summary>
    public static ShellOpenCatalog Current { get; private set; } = ShellOpenCatalog.BuiltIn();

    /// <summary>
    /// カタログを差し替える（起動時に 1 度だけ呼ぶ）。
    /// </summary>
    /// <param name="catalog">読み込み済みのカタログ。null は無視する。</param>
    public static void UseCatalog(ShellOpenCatalog? catalog)
    {
        if (catalog is not null) Current = catalog;
    }
}
