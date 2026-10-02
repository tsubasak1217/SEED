using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ModalHost.cs — 覆い・シート・ダイアログを開く所（シーンに 1 つ。W2-7。docs/ui_navigation.md §3）
//
//  【作り】templates/ui/prefabs/modal_host.actor:
//      ModalHost（Canvas・CanvasLayoutItem〈親に合わせる〉・このスクリプト）…… 画面のスタックより後ろ（木の順で後）に置く
//      ├─ Overlays（Canvas・親に合わせる）…… 上からの覆い・中央のポップアップ（帯 layer.overlay）
//      ├─ Sheets（Canvas・親に合わせる）  …… 下からのシート（帯 layer.sheet）
//      └─ Dialogs（Canvas・親に合わせる） …… ダイアログ（帯 layer.dialog）
//  帯のノードのレイヤーの底上げはテーマの帯の値、帯の中の j 番目の面は j × layer.modal_step（あとから開いた面が手前）。
//  面のプレハブを帯の下に作り、面のスクリプト（ModalPlane）が OnWidgetStart で開く約束を受け取る（Claim）。
//  【任意の面のプレハブ】（2026-10-02。lane3）ShowOverlay・ShowSheet・ShowDialog・ShowPopup に面のプレハブを渡す多重定義と、
//  種類と面のプレハブと指定を渡す ShowPlane（自前の ModalPlane の派生を開く）。欄（OverlayPrefab など）を一時的に替えなくてよい。
//  【戻る】ダイアログ → シート → 覆いの順の層（BackOrder）で、その種類の最後に開いた面が受ける。
//  面が 1 つも無い種類の層は受けない（次の層へ）。受けるかの問いは面の数（WantsBack）、予測型の戻るのプレビューの相手は
//  その面（ModalPlane が IBackPreviewTarget。戻るで閉じる開いた面だけ。W2 の手直し 3b）。画面の下へ回した面（Park）は数えない。
//  【全部閉じる】ModalHost.CloseAll.cs。【画面の下へ回す】ModalHost.Parking.cs。
// ============================================================

/// <summary>覆い・シート・ダイアログを開く所。</summary>
public sealed partial class ModalHost : UiWidget
{
    /// <summary>ダイアログのプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultDialogPrefab = "assets://ui/prefabs/dialog.actor";
    /// <summary>下からのシートのプレハブの既定。</summary>
    public const string DefaultSheetPrefab = "assets://ui/prefabs/bottom_sheet.actor";
    /// <summary>上からの覆いのプレハブの既定。</summary>
    public const string DefaultOverlayPrefab = "assets://ui/prefabs/top_sheet.actor";
    /// <summary>中央のポップアップのプレハブの既定（2026-10-02）。</summary>
    public const string DefaultPopupPrefab = "assets://ui/prefabs/popup.actor";
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
    /// <summary>中央のポップアップのプレハブ（2026-10-02。SEED.UI.Popup）。</summary>
    [SerializeField(Label = "ポップアップ")]
    public string PopupPrefab = DefaultPopupPrefab;
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

    /// <summary>開く約束（面を作ってから面のスクリプトが受け取るまで。開いた種類と面の根も持つ）。</summary>
    internal readonly record struct Claimed(ModalHandle Handle, object? Options, ModalKind Kind, GameObject Root);

    /// <summary>作った面のノード → 開く約束。</summary>
    private readonly Dictionary<(uint, uint), Claimed> _pending = new();
    /// <summary>面のスクリプトが動く前に取りやめた面のノード（CloseAll。面のスクリプトが始まっても黙って消える）。</summary>
    private readonly HashSet<(uint, uint)> _cancelled = new();
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

    /// <summary>どれかの面が開いているか（作りかけ・画面の下へ回した面を含む）。</summary>
    public bool AnyOpen => _pending.Count > 0 || HasAny(_planes.Values);

    /// <summary>種類の開いている面の数（作りかけ・画面の下へ回した面を含む）。</summary>
    public int Count(ModalKind kind) => _planes[kind].Count + _opening[kind];

    // ── 開く ────────────────────────────────────────────────

    /// <summary>ダイアログを開く。</summary>
    public DialogHandle ShowDialog(DialogOptions options) => ShowDialog(options, DialogPrefab);

    /// <summary>ダイアログを任意の面のプレハブで開く（2026-10-02。面の根に SEED.UI.Dialog を付けたプレハブ）。</summary>
    /// <param name="options">ダイアログの中身。</param>
    /// <param name="planePrefab">面のプレハブ（assets:// の .actor）。</param>
    public DialogHandle ShowDialog(DialogOptions options, string planePrefab)
    {
        var handle = new DialogHandle(options);
        Open(ModalKind.Dialog, planePrefab, handle, options);
        return handle;
    }

    /// <summary>下からのシートを開く（中身は options.ContentPrefab）。</summary>
    public ModalHandle ShowSheet(SheetOptions options) => ShowSheet(options, SheetPrefab);

    /// <summary>下からのシートを任意の面のプレハブで開く（2026-10-02。面の根に SEED.UI.BottomSheet を付けたプレハブ）。</summary>
    /// <param name="options">シートの指定。</param>
    /// <param name="planePrefab">面のプレハブ。</param>
    public ModalHandle ShowSheet(SheetOptions options, string planePrefab) => ShowPlane(ModalKind.Sheet, planePrefab, options);

    /// <summary>上からの覆いを開く（中身は options.ContentPrefab）。</summary>
    public ModalHandle ShowOverlay(OverlayOptions options) => ShowOverlay(options, OverlayPrefab);

    /// <summary>上からの覆いを任意の面のプレハブで開く（2026-10-02。面の根に SEED.UI.TopSheet を付けたプレハブ）。</summary>
    /// <param name="options">覆いの指定。</param>
    /// <param name="planePrefab">面のプレハブ。</param>
    public ModalHandle ShowOverlay(OverlayOptions options, string planePrefab) => ShowPlane(ModalKind.Overlay, planePrefab, options);

    /// <summary>中央のポップアップを開く（2026-10-02。SEED.UI.Popup。中身は options.ContentPrefab。帯は options.Kind〈既定は覆い〉）。</summary>
    /// <param name="options">ポップアップの指定。</param>
    public ModalHandle ShowPopup(PopupOptions options) => ShowPopup(options, PopupPrefab);

    /// <summary>中央のポップアップを任意の面のプレハブで開く（2026-10-02。面の根に SEED.UI.Popup を付けたプレハブ）。</summary>
    /// <param name="options">ポップアップの指定。</param>
    /// <param name="planePrefab">面のプレハブ。</param>
    public ModalHandle ShowPopup(PopupOptions options, string planePrefab) => ShowPlane(options.Kind, planePrefab, options);

    /// <summary>
    /// 任意の面を開く（2026-10-02。自前の ModalPlane の派生を根に付けたプレハブ）。面は種類の帯の下に作られ、面のスクリプトの
    /// OnPlaneStart で <paramref name="options"/> を ModalPlane.Options として受け取る。種類は面のスクリプトの Kind と合わせること
    /// （違うと警告し、面は自分の Kind の戻るの層に入る）。
    /// </summary>
    /// <param name="kind">面の種類（帯と戻るの層）。</param>
    /// <param name="planePrefab">面のプレハブ（assets:// の .actor）。</param>
    /// <param name="options">面へ渡す指定（面のスクリプトが読む。null 可）。</param>
    /// <returns>手札（作れなければ閉じた手札）。</returns>
    public ModalHandle ShowPlane(ModalKind kind, string planePrefab, object? options = null)
    {
        var handle = new ModalHandle(kind);
        Open(kind, planePrefab, handle, options);
        return handle;
    }

    /// <summary>面を帯の下に作り、開く約束を記す。作れなければ手札をすぐ閉じる。</summary>
    private void Open(ModalKind kind, string prefab, ModalHandle handle, object? options)
    {
        var band = Band(kind);
        var root = string.IsNullOrEmpty(prefab) ? new GameObject(Entity.None) : GameObject.Instantiate(prefab, band);
        if (!root.IsValid)
        {
            Debug.LogError($"{LogPrefix} 面のプレハブを作れません: {prefab}");
            handle.Complete(null);
            return;
        }
        // 最初のフレーム（面のスクリプトが動く前）に既定の見た目で描かれないよう隠す（面が準備できたら見せる）
        root.Visible = false;
        _pending[NavNode.Key(root)] = new Claimed(handle, options, kind, root);
        _opening[kind]++;
        Redraw.Request();
    }

    /// <summary>面のスクリプトが開く約束を受け取る（無ければ null）。</summary>
    internal Claimed? Claim(GameObject root)
    {
        if (!_pending.Remove(NavNode.Key(root), out var claimed)) return null;
        return claimed;
    }

    /// <summary>面のスクリプトが動く前に取りやめた面か（1 回だけ答える。CloseAll）。</summary>
    internal bool TakeCancelled(GameObject root) => _cancelled.Remove(NavNode.Key(root));

    /// <summary>
    /// 面が準備を始めた（登録して並びの底上げを当てる。ModalPlane.OnPlaneStart の前に Claim → ここ）。
    /// 作りかけの数は開いた種類から引き、面は自分の Kind の並びに入る（違えば警告）。
    /// </summary>
    /// <param name="plane">面。</param>
    /// <param name="claimedKind">開いたときの種類。</param>
    internal void Register(ModalPlane plane, ModalKind claimedKind)
    {
        var list = _planes[plane.Kind];
        if (list.Contains(plane)) return;
        if (_opening[claimedKind] > 0) _opening[claimedKind]--;
        if (claimedKind != plane.Kind)
            Debug.LogWarning($"{LogPrefix} 面の種類（{plane.Kind}）が開いた種類（{claimedKind}）と違います: {plane.Owner.Name}");
        list.Add(plane);
        NavNode.SetBias(plane.Owner, UiLayers.ModalEntryBias(list.Count, UiLayers.ModalStep(Theme)));
    }

    /// <summary>面を外す（閉じた・消えた）。画面の下へ回していたら、その記録も捨てる（元へ戻すものは無い）。</summary>
    internal void Forget(ModalPlane plane)
    {
        _planes[plane.Kind].Remove(plane);
        ForgetPark(plane);
    }

    // ── 戻る ────────────────────────────────────────────────

    /// <summary>
    /// 戻るを受ける（種類の最後に開いた面。開いている面が無ければ false）。画面の下へ回した面（Park）は飛ばす。
    /// </summary>
    public bool HandleBack(ModalKind kind)
    {
        var list = _planes[kind];
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var plane = list[i];
            if (plane.Phase is ModalPhase.Closed || plane.IsParked) continue;
            // 閉じる動きの途中の面は戻るを受けて何もしない（二度閉じない・後ろへ回さない）
            if (plane.Phase is ModalPhase.Exiting) return true;
            return plane.HandleBack();
        }
        // 作りかけ（まだスクリプトが動いていない）の面があれば、戻るは受けて捨てる（開いた直後の二度押しで後ろが閉じない）
        return _opening[kind] > 0;
    }

    /// <summary>
    /// 種類の層が戻るを受けるか（副作用なし。W2 の手直し 3b）。面が 1 つでもあれば（作りかけを含む・画面の下へ回した面は除く）
    /// <see cref="HandleBack"/> は必ず true。
    /// </summary>
    public bool WantsBack(ModalKind kind) => Count(kind) - ParkedCount(kind) > 0;

    /// <summary>
    /// 種類の層の予測型の戻るのプレビューの相手（<see cref="HandleBack"/> が尋ねる面＝最後に開いた閉じていない面が、開いていて
    /// 戻るで閉じるときだけ。閉じる動きの途中・作りかけ・閉じない面・画面の下へ回した面は null。W2 の手直し 3b）。
    /// </summary>
    internal IBackPreviewTarget? BackPreviewTarget(ModalKind kind)
    {
        var list = _planes[kind];
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var plane = list[i];
            if (plane.Phase is ModalPhase.Closed || plane.IsParked) continue;
            IBackPreviewTarget target = plane;
            return target.IsBackPreviewValid ? target : null;
        }
        return null;
    }

    /// <summary>種類の画面の下へ回した面の数（閉じていないもの）。</summary>
    private int ParkedCount(ModalKind kind)
    {
        int count = 0;
        foreach (var plane in _planes[kind])
            if (plane.IsParked && plane.Phase != ModalPhase.Closed) count++;
        return count;
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
        ForgetAllParks();
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
