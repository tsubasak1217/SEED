using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ModalHost.cs — 覆い・シート・ダイアログを開く所（シーンに 1 つ。W2-7。docs/ui_navigation.md §3）
//
//  【作り】templates/ui/prefabs/modal_host.actor:
//      ModalHost（Canvas・CanvasLayoutItem〈親に合わせる〉・このスクリプト）…… 画面のスタックより後ろ（木の順で後）に置く
//      ├─ Overlays（Canvas・親に合わせる）…… 上からの覆い（帯 layer.overlay）
//      ├─ Sheets（Canvas・親に合わせる）  …… 下からのシート（帯 layer.sheet）
//      └─ Dialogs（Canvas・親に合わせる） …… ダイアログ（帯 layer.dialog）
//  帯のノードのレイヤーの底上げはテーマの帯の値、帯の中の j 番目の面は j × layer.modal_step（あとから開いた面が手前）。
//  面のプレハブを帯の下に作り、面のスクリプト（ModalPlane）が OnWidgetStart で開く約束を受け取る（Claim）。
//  【戻る】ダイアログ → シート → 覆いの順の層（BackOrder）で、その種類の最後に開いた面が受ける。
//  面が 1 つも無い種類の層は受けない（次の層へ）。
// ============================================================

/// <summary>覆い・シート・ダイアログを開く所。</summary>
public sealed class ModalHost : UiWidget
{
    /// <summary>ダイアログのプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultDialogPrefab = "assets://ui/prefabs/dialog.actor";
    /// <summary>下からのシートのプレハブの既定。</summary>
    public const string DefaultSheetPrefab = "assets://ui/prefabs/bottom_sheet.actor";
    /// <summary>上からの覆いのプレハブの既定。</summary>
    public const string DefaultOverlayPrefab = "assets://ui/prefabs/top_sheet.actor";
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] modal:";

    /// <summary>ダイアログのプレハブ。</summary>
    [SerializeField(Label = "ダイアログ")]
    public string DialogPrefab = DefaultDialogPrefab;
    /// <summary>下からのシートのプレハブ。</summary>
    [SerializeField(Label = "シート")]
    public string SheetPrefab = DefaultSheetPrefab;
    /// <summary>上からの覆いのプレハブ。</summary>
    [SerializeField(Label = "覆い")]
    public string OverlayPrefab = DefaultOverlayPrefab;
    /// <summary>覆いの帯の子の名前。</summary>
    [SerializeField(Label = "覆いの帯")]
    public string OverlaysChild = "Overlays";
    /// <summary>シートの帯の子の名前。</summary>
    [SerializeField(Label = "シートの帯")]
    public string SheetsChild = "Sheets";
    /// <summary>ダイアログの帯の子の名前。</summary>
    [SerializeField(Label = "ダイアログの帯")]
    public string DialogsChild = "Dialogs";

    /// <summary>今の ModalHost（最後に始まったもの。無ければ null）。</summary>
    public static ModalHost? Current { get; private set; }

    /// <summary>開く約束（面を作ってから面のスクリプトが受け取るまで）。</summary>
    internal readonly record struct Claimed(ModalHandle Handle, object? Options);

    /// <summary>作った面のノード → 開く約束。</summary>
    private readonly Dictionary<(uint, uint), Claimed> _pending = new();
    /// <summary>種類ごとの開いている面（開いた順）。</summary>
    private readonly Dictionary<ModalKind, List<ModalPlane>> _planes = new()
    {
        [ModalKind.Overlay] = new(),
        [ModalKind.Sheet] = new(),
        [ModalKind.Dialog] = new(),
    };
    /// <summary>種類ごとの作ったが受け取られていない面の数（並びの番号に使う）。</summary>
    private readonly Dictionary<ModalKind, int> _opening = new()
    {
        [ModalKind.Overlay] = 0,
        [ModalKind.Sheet] = 0,
        [ModalKind.Dialog] = 0,
    };

    /// <summary>どれかの面が開いているか（作りかけを含む）。</summary>
    public bool AnyOpen => _pending.Count > 0 || HasAny(_planes.Values);

    /// <summary>種類の開いている面の数（作りかけを含む）。</summary>
    public int Count(ModalKind kind) => _planes[kind].Count + _opening[kind];

    /// <summary>ダイアログを開く。</summary>
    public DialogHandle ShowDialog(DialogOptions options)
    {
        var handle = new DialogHandle(options);
        Open(ModalKind.Dialog, DialogPrefab, handle, options);
        return handle;
    }

    /// <summary>下からのシートを開く（中身は options.ContentPrefab）。</summary>
    public ModalHandle ShowSheet(SheetOptions options)
    {
        var handle = new ModalHandle(ModalKind.Sheet);
        Open(ModalKind.Sheet, SheetPrefab, handle, options);
        return handle;
    }

    /// <summary>上からの覆いを開く（中身は options.ContentPrefab）。</summary>
    public ModalHandle ShowOverlay(OverlayOptions options)
    {
        var handle = new ModalHandle(ModalKind.Overlay);
        Open(ModalKind.Overlay, OverlayPrefab, handle, options);
        return handle;
    }

    /// <summary>面を帯の下に作り、開く約束を記す。作れなければ手札をすぐ閉じる。</summary>
    private void Open(ModalKind kind, string prefab, ModalHandle handle, object options)
    {
        var band = Band(kind);
        var root = GameObject.Instantiate(prefab, band);
        if (!root.IsValid)
        {
            Debug.LogError($"{LogPrefix} 面のプレハブを作れません: {prefab}");
            handle.Complete(null);
            return;
        }
        // 最初のフレーム（面のスクリプトが動く前）に既定の見た目で描かれないよう隠す（面が準備できたら見せる）
        root.Visible = false;
        _pending[NavNode.Key(root)] = new Claimed(handle, options);
        _opening[kind]++;
        Redraw.Request();
    }

    /// <summary>面のスクリプトが開く約束を受け取る（無ければ null）。帯の中の並びの底上げを当てて登録する。</summary>
    internal Claimed? Claim(GameObject root)
    {
        if (!_pending.Remove(NavNode.Key(root), out var claimed)) return null;
        return claimed;
    }

    /// <summary>面が準備を始めた（登録して並びの底上げを当てる。ModalPlane.OnPlaneStart の前に Claim → ここ）。</summary>
    internal void Register(ModalPlane plane)
    {
        var list = _planes[plane.Kind];
        if (list.Contains(plane)) return;
        if (_opening[plane.Kind] > 0) _opening[plane.Kind]--;
        list.Add(plane);
        NavNode.SetBias(plane.Owner, UiLayers.ModalEntryBias(list.Count, UiLayers.ModalStep(Theme)));
    }

    /// <summary>面を外す（閉じた・消えた）。</summary>
    internal void Forget(ModalPlane plane) => _planes[plane.Kind].Remove(plane);

    /// <summary>
    /// 戻るを受ける（種類の最後に開いた面。開いている面が無ければ false）。
    /// </summary>
    public bool HandleBack(ModalKind kind)
    {
        var list = _planes[kind];
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var plane = list[i];
            if (plane.Phase is ModalPhase.Closed) continue;
            // 閉じる動きの途中の面は戻るを受けて何もしない（二度閉じない・後ろへ回さない）
            if (plane.Phase is ModalPhase.Exiting) return true;
            return plane.HandleBack();
        }
        // 作りかけ（まだスクリプトが動いていない）の面があれば、戻るは受けて捨てる（開いた直後の二度押しで後ろが閉じない）
        return _opening[kind] > 0;
    }

    /// <summary>種類の帯のノード（無ければ自分）。</summary>
    private GameObject Band(ModalKind kind)
    {
        string name = kind switch
        {
            ModalKind.Overlay => OverlaysChild,
            ModalKind.Sheet => SheetsChild,
            _ => DialogsChild,
        };
        var band = gameObject.FindChild(name);
        return band.IsValid ? band : gameObject;
    }

    /// <summary>どれかのリストに面があるか。</summary>
    private static bool HasAny(IEnumerable<List<ModalPlane>> lists)
    {
        foreach (var list in lists)
            if (list.Count > 0) return true;
        return false;
    }

    // ── 部品の土台 ──────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        Current = this;
        ApplyBands();
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt) => BackDispatcher.PollBackKey();

    /// <inheritdoc />
    protected override void ApplyLook() => ApplyBands();

    /// <summary>帯のノードへ帯の底上げを当てる（テーマの layer.overlay・layer.sheet・layer.dialog）。</summary>
    private void ApplyBands()
    {
        NavNode.SetBias(gameObject.FindChild(OverlaysChild), UiLayers.Band(Theme, ModalKind.Overlay));
        NavNode.SetBias(gameObject.FindChild(SheetsChild), UiLayers.Band(Theme, ModalKind.Sheet));
        NavNode.SetBias(gameObject.FindChild(DialogsChild), UiLayers.Band(Theme, ModalKind.Dialog));
    }
}
