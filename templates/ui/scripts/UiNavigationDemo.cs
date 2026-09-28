// ============================================================
//  UiNavigationDemo.cs — 画面の組み立ての見本（templates/ui/scenes/ui_navigation.scene）の根（W2-7）
//
//  見本のシーンの根に付ける。画面・タブ・面の部品はそれぞれのスクリプトで動くので、ここは次だけを行う:
//    - 戻るの段の結果（BackDispatcher.Dispatched）・タブの切り替え・画面のスタックの変化をログ（[UI] demo: …）へ出す
//    - デバッグの命令（SCRIPT_DEBUG:nav,<名前>）: state（スタックの段・タブ・開いている面・トースト・フォーカス・ホイールの値をログへ）・
//      back（戻るを配る。Escape と同じ）・mark <文字>（ログの区切り）・toast <文字>・tab <番号>・
//      open <dialog|dialog-modal|sheet|sheet-full|overlay>（面を直接開く。重ねて戻るの順を確かめる）・where <名前>（ノードの画面の位置）
//  PC の検査は IPC の入力の注入（タップ・Escape）で操作し、ログとスクリーンショットで確かめる（docs/ui_navigation.md §8）。
// ============================================================
using System;
using System.Linq;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiNavigationDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] demo:";
    /// <summary>根のスタック・タブのノードの名前。</summary>
    private const string RootStackName = "RootStack";
    private const string TabHostName = "TabHost";

    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;
    /// <summary>つないだタブ（二重につながない）。</summary>
    private TabHost? _tabs;
    /// <summary>つないだ根のスタック。</summary>
    private ScreenStack? _root;
    /// <summary>引き直した登録簿の版。</summary>
    private int _registryVersion = -1;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand("nav", _cmd);
        BackDispatcher.Dispatched += OnBack;
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand("nav", _cmd);
        BackDispatcher.Dispatched -= OnBack;
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        if (_root is null && UiWidget.Of<ScreenStack>(GameObject.Find(RootStackName)) is { } root)
        {
            _root = root;
            root.Changed += s => Debug.Log($"{LogPrefix} root depth={s.Depth} top={s.Top}");
        }
        if (_tabs is null && UiWidget.Of<TabHost>(GameObject.Find(TabHostName)) is { } tabs)
        {
            _tabs = tabs;
            tabs.TabChanged += (_, i) => Debug.Log($"{LogPrefix} tab changed {i}");
            tabs.TabReselected += (_, i) => Debug.Log($"{LogPrefix} tab reselected {i}");
        }
    }

    /// <summary>戻るの段の結果をログへ。</summary>
    private static void OnBack(BackDispatchResult r)
        => Debug.Log($"{LogPrefix} back handled={r.Handled} layer={(r.Handled ? r.Layer : BackDispatcher.LayerMoveTaskToBack)}");

    /// <summary>デバッグの命令（SCRIPT_DEBUG:nav,<名前>[,<値>]）。</summary>
    private void OnCommand(string arg)
    {
        var parts = arg.Split(',', 2);
        string name = parts[0].Trim();
        string value = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        switch (name)
        {
            case "state":
                LogState();
                break;
            case "back":
                BackDispatcher.Dispatch();
                break;
            case "mark":
                Debug.Log($"{LogPrefix} ---- {value} ----");
                break;
            case "toast":
                Toast.Show(value.Length > 0 ? value : "テスト");
                break;
            case "tab":
                if (int.TryParse(value, out int index)) _tabs?.Select(index);
                break;
            case "open":
                Open(value);
                break;
            case "bias":
                foreach (var n in value.Split('|'))
                {
                    var target = GameObject.Find(n);
                    int bias = target.GetComponent<CanvasLayoutItem>() is { } li ? li.LayerBias : int.MinValue;
                    Debug.Log($"{LogPrefix} bias {n} valid={target.IsValid} bias={bias}");
                }
                break;
            case "where":
                var node = GameObject.Find(value);
                var p = node.GetComponent<CanvasTransform>() is { } ct ? ct.ScreenPosition : Vector2.Zero;
                Debug.Log($"{LogPrefix} where {value} valid={node.IsValid} visible={node.Visible} x={p.x:0.#} y={p.y:0.#}");
                break;
            default:
                Debug.LogWarning($"{LogPrefix} 知らない命令: {arg}");
                break;
        }
    }

    /// <summary>面を直接開く（戻るの段の順を確かめる: 覆い・シート・ダイアログを重ねる）。</summary>
    private static void Open(string what)
    {
        ModalHandle? handle = what switch
        {
            "dialog" => Dialog.Show(new DialogOptions { Title = "確認", Message = "デバッグの命令から開いたダイアログ", PositiveText = "OK", NegativeText = "やめる" }),
            "sheet" => BottomSheet.Show(new SheetOptions { ContentPrefab = "assets://ui/prefabs/nav_sheet_content.actor" }),
            "overlay" => TopSheet.Show(new OverlayOptions { ContentPrefab = "assets://ui/prefabs/nav_overlay_content.actor" }),
            "sheet-full" => BottomSheet.Show(new SheetOptions { ContentPrefab = "assets://ui/prefabs/nav_sheet_content.actor", StartHalf = false }),
            "dialog-modal" => Dialog.Show(new DialogOptions { Title = "必ず答える", Message = "幕のタップ・戻るでは閉じない", PositiveText = "OK", DismissOnScrimTap = false, CancelableByBack = false }),
            _ => null,
        };
        if (handle is null) return;
        handle.Closed += h => Debug.Log($"{LogPrefix} {what} closed result={h.Result ?? "null"}");
    }

    /// <summary>状態をログへ（検査の区切りごとに読む）。</summary>
    private void LogState()
    {
        var modal = ModalHost.Current;
        var toasts = ToastHost.Current;
        string tabDepths = _tabs is null ? "-" : string.Join("/", Enumerable.Range(0, _tabs.Count).Select(i => _tabs.StackAt(i)?.Depth ?? 0));
        string focus = UiFocus.Current is { } f ? f.FocusOwner.Name : "-";
        string wheel = UiWidget.Of<WheelPicker>(GameObject.Find("Wheel")) is { } w ? w.SelectedIndex.ToString() : "-";
        Debug.Log($"{LogPrefix} state root={_root?.Depth ?? 0} tab={_tabs?.SelectedIndex ?? -1} tabDepths={tabDepths} " +
                  $"dialogs={modal?.Count(ModalKind.Dialog) ?? 0} sheets={modal?.Count(ModalKind.Sheet) ?? 0} overlays={modal?.Count(ModalKind.Overlay) ?? 0} " +
                  $"toasts={toasts?.ActiveCount ?? 0}+{toasts?.PendingCount ?? 0} focus={focus} scope={UiFocus.TopScope?.Name ?? "root"} wheel={wheel} " +
                  $"redraw={Redraw.Policy}");
    }
}
