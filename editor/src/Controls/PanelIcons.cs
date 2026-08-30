using System;
using System.Collections.Generic;
using System.Linq;
using AvalonDock;
using AvalonDock.Layout;

namespace SEEDEditor.Controls;

/// <summary>
/// ドッキングパネル（AvalonDock）のタブ見出しアイコンを、ContentId から引いて
/// 一括で適用する対応表。
///
/// AvalonDock の <c>LayoutContent.IconSource</c> は <c>ImageSource</c> 型で
/// Foreground を継承できないため、<see cref="AppIcon"/> ではなく
/// <see cref="IconImages"/> で作った DrawingImage を割り当てる。
///
/// XAML 側で IconSource を書かずここへ集約しているのは、保存レイアウト
/// （editor/settings/layout.xml）から復元されたパネルは XAML の
/// LayoutAnchorable ではなく逆シリアライズで作り直されるため、XAML に書いた
/// IconSource が失われるから。レイアウト復元後にこのメソッドを一度呼べば、
/// XAML 既定レイアウト・復元レイアウト・コードで動的追加したパネルの
/// いずれにも同じアイコンが付く。
///
/// 新しいパネルを追加したら <see cref="IconKeyByContentId"/> へ 1 行足すこと
/// （手順は .claude/rules/editor-icons.md と add-editor-panel Skill を参照）。
/// </summary>
internal static class PanelIcons
{
    /// <summary>ContentId -> Icons.xaml のアイコンキー。</summary>
    private static readonly Dictionary<string, string> IconKeyByContentId = new(StringComparer.Ordinal)
    {
        ["hierarchy"]          = "Icon.Panel.Hierarchy",
        ["open_documents"]     = "Icon.Panel.OpenDocuments",
        ["viewport"]           = "Icon.Panel.Viewport",
        ["script_editor"]      = "Icon.Panel.ScriptEditor",
        ["project"]            = "Icon.Panel.Project",
        ["output"]             = "Icon.Panel.Output",
        ["animation_timeline"] = "Icon.Panel.AnimationTimeline",
        ["error_list"]         = "Icon.Panel.ErrorList",
        ["profiler"]           = "Icon.Panel.Profiler",
        ["inspector"]          = "Icon.Panel.Inspector",
        ["ai_assistant"]       = "Icon.Panel.AiAssistant",
        ["sprite_rig"]         = "Icon.Panel.SpriteRig",
    };

    /// <summary>
    /// DockingManager 配下の全パネル（LayoutAnchorable / LayoutDocument）へ
    /// ContentId に対応するアイコンを設定する。
    /// 表に無い ContentId のパネルはアイコン無しのまま素通しする。
    /// </summary>
    /// <param name="dockManager">対象の DockingManager。</param>
    public static void Apply(DockingManager dockManager)
    {
        if (dockManager.Layout == null) return;

        foreach (var content in dockManager.Layout.Descendents().OfType<LayoutContent>())
        {
            if (content.ContentId == null) continue;
            if (!IconKeyByContentId.TryGetValue(content.ContentId, out var iconKey)) continue;

            var image = IconImages.Get(iconKey);
            if (image != null) content.IconSource = image;
        }
    }
}
