// ============================================================
//  MainWindow.TemplateActors.cs — テンプレートアクタの窓を開く
//
//  【役割】（MainWindow の部分クラス）
//  ヒエラルキー／シーンビューの右クリック「アクタを追加」→「テンプレートアクタ...」から、
//  テンプレートアクタの窓（Templates/Actors/TemplateActorPickerWindow）を開く。
//  窓はエディタの主窓・ヒエラルキー・ランタイムを直接知らないので、
//  必要なこと（アセットの場所・追加先の引き直し・送信・プロジェクトパネルの読み直し）を
//  TemplateActorPickerContext に詰めて渡す。正典は docs/template_library.md §9。
// ============================================================

using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>閲覧専用の表示中に追加しようとしたときの文言。</summary>
    private const string TemplateActorReadOnlyReason = "閲覧専用の表示中はアクタを追加できません";

    /// <summary>
    /// テンプレートアクタの窓を開く（開いていれば追加先を差し替えて前へ出す）。
    /// </summary>
    /// <param name="target">追加先（右クリックした場所）。</param>
    private void OpenTemplateActorPicker(TemplateActorTarget target)
    {
        // ライブラリの場所はエディタ exe から解決する（環境変数 SEED_TEMPLATE_LIBRARY で上書き可）。
        var libraryRoot = TemplateLibraryLocator.Resolve();
        if (libraryRoot is null)
        {
            EditorLog.Write("テンプレートライブラリが見つかりません（<repo>/templates または SEED_TEMPLATE_LIBRARY）");
            ShowToast("テンプレートライブラリが見つかりません");
            return;
        }

        var context = new TemplateActorPickerContext
        {
            LibraryRoot    = libraryRoot,
            AssetsRoot     = () => AssetsPath,
            RefreshTarget  = t => PanelHierarchy.TryRefreshTemplateActorTarget(t),
            ReadOnlyReason = () => PanelHierarchy.IsReadOnlyView ? TemplateActorReadOnlyReason : null,
            SendToRuntime  = command => _runtimeManager?.SendToRuntime(command),
            // 依存ファイルはアセットルートの下へコピーされるので、ツリーを作り直して見せる
            FilesCopied    = _ => PanelProject.SetAssetsPath(AssetsPath),
            Log            = EditorLog.Write,
        };
        TemplateActorPickerWindow.ShowFor(this, context, target);
    }
}
