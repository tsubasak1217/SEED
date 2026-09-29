// ============================================================
//  UiGalleryListRow.cs — ギャラリーの一覧の行（templates/ui/prefabs/list_row.actor の根に付ける。フルスワイプで削除の見本。
//                        W2 の手直し P2-3。docs/ui_scroll_list.md §7）
//
//  【作り】行のプレハブ:
//      ListRow（CanvasComponent・fill_width・CanvasGesture 横のドラッグとフリック・このスクリプト）
//      ├─ Actions（削除の面 color.error を行いっぱいに敷く・タップ〈SEED.UI.GestureRelay〉）
//      │   └─ Label（「削除」。右の端のボタンの枠〈96〉の真ん中）
//      └─ Front（行の見た目。遮る板。W2 の手直し P2-5 で CanvasComponent と縦の CanvasStack〈左右の余白 16・上 6〉を付けた）
//          ├─ Title・Sub（文字。幅は行の幅 − 余白に伸びる）
//          └─ Divider（区切り線。同じく行の幅 − 余白）
//  【行の幅】（P2-5）レイアウトは文字の枠（BoxWidth）を伸ばさないので、Title・Sub の枠の幅をレイアウトが伸ばした幅（LayoutSize）へ
//  このスクリプトが合わせる。合わせるのは行ができた最初の描画の後と、一覧の持ち主が一覧の幅の変化を知らせた（RefitTextBoxes）後だけ
//  （止まっている間は毎フレーム読まない）。区切り線は Sprite なのでレイアウトが伸ばす。
//  【振る舞い】SEED.UI.SwipeActions（フルスワイプ有効・文字は Actions/Label）を作り、行のジェスチャーを渡し、一覧ごとの
//  SwipeGroup に入れる。左へ払うと削除のボタンが出て、大きく払う（行の幅の 0.6）と構え（触感 1 回）、離すと行を外へ流し切る。
//  開いた行の削除の面のタップでも同じ確定の流れ（SwipeActions.Commit）。流し切ったら SEED.Events の FullSwipedEvent（引数 = 行）で
//  一覧の持ち主（UiGallerySections）へ知らせる。持ち主が行の高さを畳んでからデータを消し、行を書き直す（Bound で項目が変われば Reset）。
//  【触感】見本の既定は Vibrate（利用者の端末〈Pixel 6a〉は「タップ時のバイブ」がオフで Haptics.Tap を感じないため、メディア・ゲームの
//  振動の設定が効く Haptics.Vibrate にする）。デバッグの命令 gallery,haptic,<none|tap|vibrate> で切り替える（UiGallerySections）。
//  【ログ】構える・解く・開く・閉じる・確定・流し切りを `[UI] gallery: swipe …` の 1 行で出す（実機の確かめで logcat から読む）。
// ============================================================
using System.Collections.Generic;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiGalleryListRow : SEEDScript
{
    /// <summary>流し切ったことを一覧の持ち主へ知らせるイベントの名前（SEED.Events。引数 = 行の GameObject）。</summary>
    public const string FullSwipedEvent = "ui_gallery.list_row.full_swiped";

    /// <summary>ログの接頭辞（UiGallerySections の `[UI] gallery:` にそろえる）。</summary>
    private const string LogPrefix = "[UI] gallery: swipe";
    /// <summary>
    /// 開いたときの削除のボタンの幅（dp。list_row.actor の Label の置き場〈右の端から 48 = この半分〉と合わせる。
    /// W2-3 の確認用の行と同じ 96 = Material の 48 dp の押せる大きさの 2 つぶん）。
    /// </summary>
    private const float ActionsWidth = 96f;
    /// <summary>行の見た目・削除の面・「削除」の文字の子のパス。</summary>
    private const string FrontPath = "Front", ActionsPath = "Actions", LabelPath = "Actions/Label";
    /// <summary>枠の幅を行の幅に合わせる文字の子のパス（W2 の手直し P2-5。Front の縦の CanvasStack が幅いっぱいに伸ばす）。</summary>
    private static readonly string[] FitTextPaths = { "Front/Title", "Front/Sub" };
    /// <summary>まだ文字の枠を合わせていない印（合わせ直しの版）。</summary>
    private const int NoFitVersion = -1;
    /// <summary>項目が付いていない印。</summary>
    private const int NoItem = -1;
    /// <summary>高さの割合の既定（畳んでいない）。</summary>
    private const float FullHeight = 1f;
    /// <summary>VisualScale は矩形の中心の周りに縮めるので、上の端をそろえるには縮んだ分の半分だけ上へずらす（その「半分」）。</summary>
    private const float CenterFraction = 0.5f;

    /// <summary>行のエンティティ → スクリプト（一覧の持ち主の Bind・Recycled から引く）。</summary>
    private static readonly Dictionary<(uint, uint), UiGalleryListRow> Rows = new();
    /// <summary>構えたときの触感（全部の行で共通。見本の既定は Vibrate＝冒頭の理由）。</summary>
    private static SwipeHaptic _haptic = SwipeHaptic.Vibrate;
    /// <summary>文字の枠の合わせ直しの版（一覧の持ち主が一覧の幅の変化で進める。行は自分の版と違えば合わせ直す）。</summary>
    private static int _textFitVersion;

    /// <summary>行のスワイプの操作。</summary>
    private SwipeActions? _swipe;
    /// <summary>削除の面（タップで確定）。</summary>
    private GameObject _actions;
    /// <summary>削除の面のタップの受け口（UiWidget の登録の後に引ける）。</summary>
    private GestureRelay? _relay;
    /// <summary>今見せている項目の番号（NoItem = まだ）。</summary>
    private int _item = NoItem;
    /// <summary>今の高さの割合（1 = 畳んでいない）。</summary>
    private float _heightRatio = FullHeight;
    /// <summary>枠の幅を合わせる文字のノード（FitTextPaths の順）。</summary>
    private GameObject[] _fitTexts = System.Array.Empty<GameObject>();
    /// <summary>この行が文字の枠を合わせた版（NoFitVersion = まだ）。</summary>
    private int _fittedVersion = NoFitVersion;

    /// <summary>構えたときの触感（全部の行へ当てる）。</summary>
    public static SwipeHaptic Haptic
    {
        get => _haptic;
        set
        {
            _haptic = value;
            foreach (var row in Rows.Values)
            {
                if (row._swipe is { } swipe) swipe.ArmHaptic = value;
            }
        }
    }

    /// <summary>今見せている項目の番号（付いていなければ −1）。</summary>
    public int Item => _item;

    public override void OnStart()
    {
        _actions = gameObject.FindChild(ActionsPath);
        _fitTexts = System.Array.ConvertAll(FitTextPaths, path => gameObject.FindChild(path));
        _swipe = new SwipeActions(gameObject.FindChild(FrontPath), ActionsWidth)
        {
            FullSwipe = true,
            FullSwipeLabel = gameObject.FindChild(LabelPath),
            ArmHaptic = _haptic,
            Group = SwipeGroup.For(gameObject.Parent),
            Armed = s => Log($"armed item={_item} offset={s.Offset:0.#} width={s.ResolvedRowExtent:0.#} haptic={s.ArmHaptic} count={s.ArmCount}"),
            Disarmed = s => Log($"disarmed item={_item} offset={s.Offset:0.#}"),
            Opened = s => Log($"opened item={_item}"),
            Closed = s => Log($"closed item={_item}"),
            CommitStarted = s => Log($"commit item={_item} offset={s.Offset:0.#}"),
            FullSwiped = OnFullSwiped,
        };
        Rows[Key(gameObject)] = this;
    }

    public override void OnDestroy()
    {
        Rows.Remove(Key(gameObject));
        if (_relay is not null) _relay.Tapped -= OnActionsTapped;
        if (_swipe is not null) _swipe.Group = null;
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        BindRelay();
        FitTextBoxes();
        _swipe?.Update(SEED.Time.UnscaledDeltaTime);
    }

    /// <summary>
    /// 一覧の持ち主から: 一覧の幅が変わった（画面の回転・大きさの変化）。全部の行が次の Update で文字の枠を合わせ直す（W2 の手直し P2-5）。
    /// </summary>
    public static void RefitTextBoxes() => _textFitVersion++;

    /// <summary>
    /// 文字（Title・Sub）の枠の幅を、レイアウトが伸ばした幅（CanvasTransform.LayoutSize。前のフレームの描画の値）へ合わせる
    /// （W2 の手直し P2-5）。レイアウトは文字の枠を伸ばさない（docs/canvas_camera_rework.md §6.3 の規則 3）ので、一覧が狭くなっても
    /// 枠が行の右の端を越えないよう書き直す。合わせるのは版が変わったとき（行ができた後・RefitTextBoxes の後）だけで、
    /// まだ描画されていない（HasLayout = false）文字があれば次のフレームにやり直す。
    /// </summary>
    private void FitTextBoxes()
    {
        if (_fittedVersion == _textFitVersion) return;
        foreach (var node in _fitTexts)
        {
            if (node.GetComponent<CanvasTransform>() is not { HasLayout: true } ct) return;
            if (node.GetComponent<Text>() is { } text) text.BoxWidth = ct.LayoutSize.x;
        }
        _fittedVersion = _textFitVersion;
    }

    public override void OnGestureDragStart(GestureEvent e) => _swipe?.OnDragStart(e);

    public override void OnGestureDragUpdate(GestureEvent e) => _swipe?.OnDragUpdate(e);

    public override void OnGestureDragEnd(GestureEvent e) => _swipe?.OnDragEnd(e);

    /// <summary>
    /// 一覧の持ち主の Bind から: 行に項目と高さを当てる。項目が変わった（使い回し・消した行の後の書き直し）ならスワイプを戻す。
    /// 行のスクリプトがまだ始まっていなければ何もしない（作ったばかりの行は閉じた状態）。
    /// </summary>
    /// <param name="row">行。</param>
    /// <param name="item">項目の番号。</param>
    /// <param name="extent">今の行の高さ（畳んでいる途中なら縮んだ値）。</param>
    /// <param name="fullExtent">元の行の高さ。</param>
    public static void Bound(GameObject row, int item, float extent, float fullExtent)
    {
        if (!Rows.TryGetValue(Key(row), out var r)) return;
        if (r._item != item)
        {
            r._item = item;
            r._swipe?.Reset();
        }
        r.ApplyHeight(extent, fullExtent);
    }

    /// <summary>一覧の持ち主の Recycled から: 行が番号から外れる（スワイプを戻し、項目を外す）。</summary>
    public static void Released(GameObject row)
    {
        if (!Rows.TryGetValue(Key(row), out var r)) return;
        r._item = NoItem;
        r._swipe?.Reset();
    }

    /// <summary>畳んでいる途中の行の高さを当てる（一覧の持ち主が毎フレーム）。</summary>
    public static void SetHeight(GameObject row, float extent, float fullExtent)
    {
        if (Rows.TryGetValue(Key(row), out var r)) r.ApplyHeight(extent, fullExtent);
    }

    /// <summary>行の項目の番号（行のスクリプトが無ければ −1）。</summary>
    public static int ItemOf(GameObject row) => Rows.TryGetValue(Key(row), out var r) ? r._item : NoItem;

    /// <summary>
    /// 行の高さの見た目（畳む動き）: 上の端をそろえたまま縦に縮める。CanvasLayoutItem.VisualScale は矩形の中心の周りに縮めるので、
    /// 縮んだ分の半分だけ上へずらす（Translate）。一覧の並び（ListView.SetExtentOf）が下の行を同じ量だけ詰めるので重ならない。
    /// </summary>
    private void ApplyHeight(float extent, float fullExtent)
    {
        float ratio = fullExtent > 0f ? System.Math.Clamp(extent / fullExtent, 0f, FullHeight) : FullHeight;
        if (ratio == _heightRatio) return;
        _heightRatio = ratio;
        if (gameObject.GetComponent<CanvasLayoutItem>() is not { } item) return;
        item.VisualScale = new Vector2(FullHeight, ratio);
        item.Translate = new Vector2(item.Translate.x, -(fullExtent - fullExtent * ratio) * CenterFraction);
    }

    /// <summary>削除の面のタップの受け口をつなぐ（GestureRelay の OnStart の後に引ける）。</summary>
    private void BindRelay()
    {
        if (_relay is not null || !_actions.IsValid) return;
        _relay = UiWidget.Of<GestureRelay>(_actions);
        if (_relay is not null) _relay.Tapped += OnActionsTapped;
    }

    /// <summary>開いた行の削除の面のタップ: 同じ確定の流れ（流し切り → FullSwiped）。</summary>
    private void OnActionsTapped(GestureRelay relay, GestureEvent e)
    {
        if (_swipe is not { IsOpen: true } swipe) return;
        Log($"tap item={_item}");
        swipe.Commit();
    }

    /// <summary>流し切った: 一覧の持ち主へ知らせる（持ち主が畳んでデータから消し、書き直しで Reset する）。</summary>
    private void OnFullSwiped(SwipeActions swipe)
    {
        Log($"dismissed item={_item}");
        SEED.Events.Raise(FullSwipedEvent, gameObject);
    }

    /// <summary>行の鍵（エンティティの番号と世代）。</summary>
    private static (uint, uint) Key(GameObject row) => (row.Entity.Index, row.Entity.Generation);

    /// <summary>ログへ 1 行（`[UI] gallery: swipe …`）。</summary>
    private static void Log(string message) => SEED.Debug.Log($"{LogPrefix} {message}");
}
