using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SEEDEditor.Debugger;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// デバッガ停止中に、スクリプト中の変数へマウスホバーしたとき、その値を表示する
/// ポップアップ。クラス/構造体などは TreeView で展開して中身（フィールド）を辿れる。
///
/// 展開は遅延取得: TreeViewItem の Expanded で variablesReference を使い、
/// コンストラクタで渡された取得デリゲート（DAP variables）を呼んで子要素を読み込む。
///
/// 表示/非表示: ホバーで <see cref="Show"/>、ホバーが外れると <see cref="ScheduleClose"/> で
/// 短時間後に閉じる。ただしその間にマウスがポップアップへ入れば閉じない
/// （＝ポップアップ内をクリックして展開できる）。
/// </summary>
public sealed class DebugHoverPopup
{
    // 配色（エディタのダークテーマに合わせる）
    private static readonly Brush Bg      = Frozen(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush Fg      = Frozen(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly Brush BorderC = Frozen(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush NameFg  = Frozen(Color.FromRgb(0x9C, 0xDC, 0xFE));  // 変数名（青）
    private static readonly Brush TypeFg  = Frozen(Color.FromRgb(0x4E, 0xC9, 0xB0));  // 型名（緑）
    private static readonly FontFamily Mono = new("Consolas");

    private readonly Popup            _popup;
    private readonly TreeView         _tree;
    private readonly DispatcherTimer  _closeTimer;
    private readonly Func<int, Task<IReadOnlyList<VarInfo>>> _fetchChildren;

    public bool IsOpen => _popup.IsOpen;

    /// <param name="fetchChildren">variablesReference から子要素（フィールド等）を取得するデリゲート。</param>
    public DebugHoverPopup(Func<int, Task<IReadOnlyList<VarInfo>>> fetchChildren)
    {
        _fetchChildren = fetchChildren;

        _tree = new TreeView
        {
            Background       = Bg,
            Foreground       = Fg,
            BorderThickness  = new Thickness(0),
            MaxHeight        = 320,
            MinWidth         = 160,
            MaxWidth         = 560,
        };

        var border = new Border
        {
            Background       = Bg,
            BorderBrush      = BorderC,
            BorderThickness  = new Thickness(1),
            Padding          = new Thickness(4),
            Child            = _tree,
        };
        // ポップアップへ入ったら閉じタイマーを止める（内部で展開操作できるように）
        border.MouseEnter += (_, _) => _closeTimer.Stop();
        // ポップアップから出たら閉じる
        border.MouseLeave += (_, _) => Hide();

        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen          = true,
            Placement          = PlacementMode.MousePoint,
            Child              = border,
        };

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); Hide(); };
    }

    /// <summary>式の評価結果をルートにしてポップアップを表示する。</summary>
    public void Show(UIElement placementTarget, string rootName, EvalResult root)
    {
        _closeTimer.Stop();
        _tree.Items.Clear();
        _tree.Items.Add(BuildNode(rootName, root.Result, root.Type, root.VariablesReference));
        _popup.PlacementTarget = placementTarget;
        _popup.IsOpen = true;
    }

    /// <summary>ホバーが外れた: 少し待って（ポップアップへ入らなければ）閉じる。</summary>
    public void ScheduleClose()
    {
        if (_popup.IsOpen) _closeTimer.Start();
    }

    /// <summary>即座に閉じる。</summary>
    public void Hide()
    {
        _closeTimer.Stop();
        _popup.IsOpen = false;
        _tree.Items.Clear();
    }

    /// <summary>1 変数ノードを作る。展開可能（variablesReference&gt;0）なら遅延読み込みを仕込む。</summary>
    private TreeViewItem BuildNode(string name, string value, string? type, int varRef)
    {
        var item = new TreeViewItem
        {
            Header     = BuildHeader(name, value, type),
            Foreground = Fg,
        };

        if (varRef > 0)
        {
            // 展開可能にするためのダミー子。Expanded 時に実データへ差し替える。
            item.Items.Add(new TextBlock { Text = "…", Foreground = Fg });
            bool loaded = false;
            item.Expanded += async (_, e) =>
            {
                e.Handled = true;
                if (loaded) return;
                loaded = true;
                item.Items.Clear();
                var kids = await _fetchChildren(varRef);
                if (kids.Count == 0)
                {
                    item.Items.Add(new TextBlock { Text = "(なし)", Foreground = Fg });
                    return;
                }
                foreach (var k in kids)
                    item.Items.Add(BuildNode(k.Name, k.Value, k.Type, k.VariablesReference));
            };
        }
        return item;
    }

    /// <summary>「name = value  (type)」のヘッダを作る。</summary>
    private static object BuildHeader(string name, string value, string? type)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = name, Foreground = NameFg, FontFamily = Mono });
        sp.Children.Add(new TextBlock { Text = " = ", Foreground = Fg });
        sp.Children.Add(new TextBlock
        {
            Text         = value,
            Foreground   = Fg,
            FontFamily   = Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth     = 420,
        });
        if (!string.IsNullOrEmpty(type))
            sp.Children.Add(new TextBlock { Text = $"  ({type})", Foreground = TypeFg, FontFamily = Mono });
        return sp;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
