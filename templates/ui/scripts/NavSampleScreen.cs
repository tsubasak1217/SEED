// ============================================================
//  NavSampleScreen.cs — 画面の組み立ての見本（templates/ui/scenes/ui_navigation.scene）の画面ごとの結線（W2-7）
//
//  見本の画面のプレハブ（nav_*.actor）の根に付ける。Role で役割を決め、子のボタン（SEED.UI.Button）をつなぐ:
//    shell    … ヘッダー（上の安全領域の分だけ高くする）・「覆い」ボタン（上からの覆い）。下はタブ（TabHost）
//    home     … タブ 0 の根: 押し込み（タブの中）・覆う画面（全体）・フェード（全体）・ダイアログ・シート・トースト
//    list     … タブ 1 の根: 詳細を積む（タブの中）
//    settings … タブ 2 の根: 詳細を積む（タブの中）・数のホイール（フォーカスの範囲の確かめ）
//    detail   … 積んだ画面: 戻る・さらに積む・置き換える・根まで戻る（戻るは未保存の確認ダイアログ）
//    cover    … 覆う画面: 閉じる
//    sheet    … シートの中身: 行を選ぶと結果つきで閉じる
//    overlay  … 覆いの中身: 閉じる
//  出来事はログ（[UI] demo: …）へ出す（IPC の検査がログで確かめる）。画面の全体へ積む（覆う・フェード）は
//  シーンの根のスタック（RootStack）へ、タブの中へ積むのは自分を積んだスタック（Navigator）へ。
// ============================================================
using System;
using System.Collections.Generic;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class NavSampleScreen : UiScreen
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] demo:";
    /// <summary>見本の画面のプレハブ（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    private const string DetailPrefab = "assets://ui/prefabs/nav_detail.actor";
    private const string CoverPrefab = "assets://ui/prefabs/nav_cover.actor";
    private const string SheetContentPrefab = "assets://ui/prefabs/nav_sheet_content.actor";
    private const string OverlayContentPrefab = "assets://ui/prefabs/nav_overlay_content.actor";
    /// <summary>画面の全体を覆うスタック（シーンの根の子）の名前。</summary>
    private const string RootStackName = "RootStack";
    /// <summary>シェルのヘッダーの名前と、安全領域を除いた高さ（dp）。</summary>
    private const string HeaderChild = "Header";
    private const float HeaderHeight = 56f;
    /// <summary>ヘッダーの左右の余白（dp）。</summary>
    private const float HeaderSidePadding = 16f;
    /// <summary>余白が変わったとみなす差（dp）。</summary>
    private const float InsetEpsilon = 0.5f;
    /// <summary>シートの中身の行の数（見本）。</summary>
    private const int SheetRows = 30;
    /// <summary>中身から面（シート・覆い）の部品を探して祖先をたどる深さの上限。</summary>
    private const int MaxModalSearchDepth = 16;

    /// <summary>役割（shell・home・list・settings・detail・cover・sheet・overlay）。</summary>
    [SerializeField(Label = "役割")]
    public string Role = "home";

    /// <summary>詳細の番号（積むたびに 1 ずつ）。</summary>
    private int _depth;
    /// <summary>未保存の変更がある（詳細の画面。戻るで確認する）。</summary>
    private bool _dirty;
    /// <summary>つないだボタン（二重につながない）。</summary>
    private readonly HashSet<string> _bound = new();
    /// <summary>引き直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>当てたヘッダーの上の余白。</summary>
    private float _headerInset = -1f;

    protected override void OnScreenEnter(object? args)
    {
        _depth = args is int n ? n : 1;
        _dirty = Role == "detail" && _depth % 2 == 0;
        Debug.Log($"{LogPrefix} enter {Role} depth={_depth} dirty={_dirty}");
        if (Role == "detail" && gameObject.FindChild("Title").GetComponent<Text>() is { } title)
            title.Content = $"詳細 {_depth}{(_dirty ? "（未保存）" : "")}";
    }

    protected override void OnScreenShown() => Debug.Log($"{LogPrefix} shown {Role} depth={_depth}");

    protected override void OnScreenHidden() => Debug.Log($"{LogPrefix} hidden {Role} depth={_depth}");

    protected override void OnScreenExit() => Debug.Log($"{LogPrefix} exit {Role} depth={_depth}");

    /// <summary>
    /// 今、戻るが来たら受けるか（副作用なしの問い。OnBackPressed と同じ条件＝詳細の画面に未保存の変更があるときだけ）。
    /// 受けない根の画面では、予測型の戻るがシステムへ渡り、ホームへ戻る見た目が出る（W2 の手直し 3b）。
    /// </summary>
    protected override bool WouldConsumeBack() => Role == "detail" && _dirty;

    protected override bool OnBackPressed()
    {
        if (Role != "detail" || !_dirty) return false;
        // 未保存の変更があれば確認する（戻るは受ける＝スタックは下ろさない）
        var handle = Dialog.Show(new DialogOptions
        {
            Title = "変更を保存していません",
            Message = "保存せずに戻りますか？",
            PositiveText = "戻る",
            NegativeText = "とどまる",
        });
        if (handle is not null)
            handle.Completed += r =>
            {
                Debug.Log($"{LogPrefix} unsaved dialog {r}");
                if (r == DialogResult.Positive)
                {
                    _dirty = false;
                    Close();
                }
            };
        return true;
    }

    protected override void OnWidgetUpdate(float dt)
    {
        if (Role == "shell") ApplyHeaderInset();
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        Bind();
    }

    /// <summary>ヘッダーの高さ = 56 + 上の安全領域（背景はステータスバーの下まで、中身はその下）。</summary>
    private void ApplyHeaderInset()
    {
        float inset = SafeInsets.TopUnits();
        if (MathF.Abs(inset - _headerInset) < InsetEpsilon) return;
        _headerInset = inset;
        var header = gameObject.FindChild(HeaderChild);
        if (header.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(0f, HeaderHeight + inset);
        if (header.GetComponent<Sprite>() is { } bg) bg.Height = HeaderHeight + inset;
        if (header.GetComponent<CanvasStack>() is { } stack) stack.Padding = new CanvasPadding(HeaderSidePadding, inset, HeaderSidePadding, 0f);
    }

    /// <summary>役割のボタンをつなぐ。</summary>
    private void Bind()
    {
        switch (Role)
        {
            case "shell":
                Connect("Header/OverlayButton", () => Open("overlay", TopSheet.Show(new OverlayOptions { ContentPrefab = OverlayContentPrefab })));
                break;
            case "home":
                Connect("PushButton", () => Navigator?.Push(DetailPrefab, NavTransition.Push, args: 1));
                Connect("CoverButton", () => RootStack()?.Push(CoverPrefab, NavTransition.Cover));
                Connect("FadeButton", () => RootStack()?.Push(DetailPrefab, NavTransition.Fade, args: 1));
                Connect("DialogButton", ShowDialog);
                Connect("SheetButton", () => Open("sheet", BottomSheet.Show(new SheetOptions { ContentPrefab = SheetContentPrefab, Args = SheetRows })));
                Connect("ToastButton", () => Toast.Show($"保存しました（{DateTime.Now:HH:mm:ss}）"));
                break;
            case "list":
            case "settings":
                Connect("PushButton", () => Navigator?.Push(DetailPrefab, NavTransition.Push, args: 1));
                break;
            case "detail":
                Connect("BackButton", () => BackDispatcher.Dispatch());
                Connect("MoreButton", () => Navigator?.Push(DetailPrefab, NavTransition.Push, args: _depth + 1));
                Connect("ReplaceButton", () => Navigator?.Replace(DetailPrefab, NavTransition.Fade, args: _depth + 1));
                Connect("RootButton", () => Navigator?.PopToRoot());
                break;
            case "cover":
                Connect("CloseButton", () => Close("cover-closed"));
                break;
            case "overlay":
                Connect("CloseButton", () => ModalHandleOf()?.Close("overlay-closed"));
                break;
            case "sheet":
                for (int i = 0; i < SheetRows; i++)
                {
                    int row = i;
                    Connect($"List/Content/Row{i}", () => ModalHandleOf()?.Close($"row{row}"));
                }
                break;
        }
    }

    /// <summary>ボタン（名前・パス）の Clicked へつなぐ（見つかったら 1 回だけ）。</summary>
    private void Connect(string path, Action action)
    {
        if (_bound.Contains(path) || Of<Button>(gameObject.FindChild(path)) is not { } button) return;
        _bound.Add(path);
        button.Clicked += _ =>
        {
            Debug.Log($"{LogPrefix} click {Role}/{path}");
            action();
        };
    }

    /// <summary>ダイアログ（はい・いいえ・中立）を開き、結果をログへ出す。</summary>
    private void ShowDialog()
    {
        var handle = Dialog.Show(new DialogOptions
        {
            Title = "上限を上げますか？",
            Message = "寝坊で失う最大金額が 3,000 円になります。",
            PositiveText = "上げる",
            NegativeText = "やめる",
            NeutralText = "あとで",
        });
        if (handle is not null) handle.Completed += r => Debug.Log($"{LogPrefix} dialog result {r}");
    }

    /// <summary>開いた面の手札の結果をログへ出す。</summary>
    private static void Open(string what, ModalHandle? handle)
    {
        if (handle is not null) handle.Closed += h => Debug.Log($"{LogPrefix} {what} closed result={h.Result ?? "null"}");
    }

    /// <summary>シーンの根の画面のスタック（画面の全体を覆う）。</summary>
    private static ScreenStack? RootStack() => Of<ScreenStack>(GameObject.Find(RootStackName));

    /// <summary>この中身を入れたシート・覆いの手札（祖先の面の部品から）。</summary>
    private ModalHandle? ModalHandleOf()
    {
        var node = gameObject.Parent;
        for (int depth = 0; depth < MaxModalSearchDepth && node.IsValid; depth++)
        {
            if (Of<ModalPlane>(node) is { } plane) return plane.Handle;
            node = node.Parent;
        }
        return null;
    }
}
