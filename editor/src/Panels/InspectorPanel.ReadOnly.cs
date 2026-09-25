using System.Windows.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// インスペクタの閲覧専用表示（<see cref="InspectorPanel"/> の部分クラス。docs/android.md §20.17）。
///
/// <para>
/// 端末の一時停止の写しをシーンパネルに出している間は、値を見られるが変えられないようにする。
/// 値の欄（コンポーネントの一覧・アクター編集の欄）・アクティブ/表示の切り替え・コンポーネントの追加を無効表示にし、
/// 一覧のスクロール（<c>ComponentScroll</c>）とアクターの選択の追従（SELECTED・ACTOR_COMPONENTS の反映）は止めない
/// （無効な要素の上でもホイールは親のスクロールへ届く）。
/// </para>
///
/// <para>
/// 判断は WPF 非依存の EditorReadOnlyPolicy（MainWindow が当てる）。ランタイム側でも写しの表示中は編集の命令を捨てるので、
/// ここは「押せない見た目」と誤操作の防止を受け持つ。
/// </para>
/// </summary>
public partial class InspectorPanel
{
    /// <summary>閲覧専用か（写しの表示中）。</summary>
    public bool IsReadOnlyView { get; private set; }

    /// <summary>
    /// 閲覧専用を切り替える（同じ状態なら何もしない）。
    /// </summary>
    /// <param name="denialReason">編集できない理由（ツールチップに出す）。null なら編集できる状態へ戻す。</param>
    public void SetReadOnly(string? denialReason)
    {
        var readOnly = denialReason is not null;
        if (IsReadOnlyView == readOnly) return;
        IsReadOnlyView = readOnly;
        var enabled = !readOnly;
        // 値の欄（コンポーネントの一覧・アクター編集の欄）とヘッダーの切り替え・追加ボタン
        ComponentStack.IsEnabled         = enabled;
        AccordionStack.IsEnabled         = enabled;
        ActorActiveCheck.IsEnabled       = enabled;
        ActorVisibleToggleHost.IsEnabled = enabled;
        BtnAddComponent.IsEnabled        = enabled;
        // 閲覧専用の理由はツールチップで示す（無効な要素でもツールチップを出す）
        ComponentStack.ToolTip = denialReason;
        ToolTipService.SetShowOnDisabled(ComponentStack, readOnly);
    }
}
