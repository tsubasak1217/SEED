using System.Windows;
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
/// 画面プレビュー（保存されない表示用のアクタ。docs/editor_screen_preview.md §3）の中のアクタも読み取り専用にする。
/// 「編集できる = 閲覧専用でない かつ プレビューの中でない」を <see cref="ApplyEditability"/> の 1 か所で当てる
/// （プレビューのときは最上部の帯〈InspectorPanel.Preview.cs〉のボタンだけを押せるまま残す）。
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

    /// <summary>閲覧専用の理由（ツールチップに出す。閲覧専用でなければ null）。</summary>
    private string? _readOnlyDenialReason;

    /// <summary>
    /// 閲覧専用を切り替える（同じ状態なら何もしない）。
    /// </summary>
    /// <param name="denialReason">編集できない理由（ツールチップに出す）。null なら編集できる状態へ戻す。</param>
    public void SetReadOnly(string? denialReason)
    {
        var readOnly = denialReason is not null;
        if (IsReadOnlyView == readOnly) return;
        IsReadOnlyView        = readOnly;
        _readOnlyDenialReason = denialReason;
        ApplyEditability();
    }

    /// <summary>
    /// 編集の UI を押せるかを当てる（閲覧専用の切り替え・アクタの表示の作り直し・選択なしのたびに呼ぶ）。
    /// <list type="bullet">
    ///   <item>閲覧専用（写しの表示中）… 従来どおり、値の欄・アコーディオン全体・アクティブ/表示・追加を無効にする</item>
    ///   <item>プレビューの中 … 同じものを無効にするが、アコーディオンは最上部の帯（プレハブを開く・プレビューを消す）だけ残す</item>
    /// </list>
    /// </summary>
    private void ApplyEditability()
    {
        var preview  = IsPreviewSelected && !IsReadOnlyView;
        var editable = !IsReadOnlyView && !IsPreviewSelected;

        // 値の欄（コンポーネントの一覧）とヘッダーの切り替え・追加ボタン
        ComponentStack.IsEnabled         = editable;
        ActorActiveCheck.IsEnabled       = editable;
        ActorVisibleToggleHost.IsEnabled = editable;
        BtnAddComponent.IsEnabled        = editable;

        // アコーディオン: 閲覧専用は丸ごと（従来どおり）。プレビューは帯以外の子だけを無効にし、
        // 無効な子の上ではプレビューの理由をツールチップで示す（無効な要素でも出す）
        AccordionStack.IsEnabled = !IsReadOnlyView;
        foreach (UIElement child in AccordionStack.Children)
        {
            var isBanner = IsPreviewBanner(child);
            child.IsEnabled = editable || isBanner;
            if (child is FrameworkElement element && !isBanner)
            {
                element.ToolTip = preview ? PreviewReadOnlyReason : null;
                ToolTipService.SetShowOnDisabled(element, preview);
            }
        }

        // 閲覧専用・プレビューの理由はツールチップで示す（無効な要素でもツールチップを出す）
        ComponentStack.ToolTip = IsReadOnlyView ? _readOnlyDenialReason : preview ? PreviewReadOnlyReason : null;
        ToolTipService.SetShowOnDisabled(ComponentStack, !editable);
    }
}
