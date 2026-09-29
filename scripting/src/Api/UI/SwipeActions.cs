using System;
using SEED.Platform;

namespace SEED.UI;

// ============================================================
//  SwipeActions.cs — 行を横へずらすと操作のボタン（削除・編集）が出る（W2-3）。大きく払うとそのまま確定するフルスワイプ
//                    （W2 の手直し P2-3）。docs/ui_scroll_list.md §7
//
//  【作り】行のプレハブ:
//      Row（CanvasGesture: tap=false・drag=true・fling=true・drag_axis=horizontal、行のスクリプト）
//      ├─ Actions（操作のボタン。ずらすと見える。フルスワイプでは行の幅いっぱいの面を敷き、ずらした分だけ見せる）
//      │   └─ Label（任意。「削除」の文字。FullSwipeLabel に渡すと構えたときに Front の後ろの端へ付いて動く）
//      └─ Front（行の見た目。これを横へずらす。受けるジェスチャーの無い CanvasGesture＝遮る板）
//  Front の部分木（面・文字）の表示のレイヤーは Actions の部分木より上にする（同じレイヤーの中では文字がスプライトより手前に
//  描かれるので、同じレイヤーだと Actions の「削除」の文字が閉じた行の Front の上に出る。renderer/ui_draw_order.rs）。
//  行のスクリプトが OnGestureDragStart / DragUpdate / DragEnd をこの部品へ渡し、Update で Update(dt) を呼ぶ。
//  縦の一覧の中では、最初の指の動きの向きで一覧のスクロールか行のスワイプかが決まる（W2-2 のアリーナの軸の競い）。
//
//  【振る舞い】（規則と値の出典は SwipeMath.cs、状態の移り変わりは SwipeModel.cs。ここはエンジンへの当てはめだけ）
//    - ドラッグで Front をずらす（閉じた 0 〜 開いた量。フルスワイプなら 0 〜 行の幅いっぱい）。離したら、速さか閾値で開いたまま・
//      閉じるを決め、250ms で動く
//    - フルスワイプ（FullSwipe = true）: 行の幅 × ratio.swipe_full で構え（触感 1 回・Armed）、× ratio.swipe_full_cancel を下回ると解く
//      （Disarmed）。構えたまま離すと Front を行の幅の外まで motion.swipe_dismiss で流し切り、FullSwiped を呼ぶ。操作のボタンの
//      タップから Commit() でも同じ流れ。確定の後は Reset まで指も Open・Close も受けない
//    - 「削除」の文字（FullSwipeLabel）: 構えていない間は元の位置、構えている間は Front の後ろの端 ＋ space.l に付いて指と一緒に動く。
//      2 つの置き場の間を motion.swipe_full の時間で補間する
//    - ずらし方: ノードに CanvasLayoutItem があれば実行中だけの見た目のずらし（CanvasLayoutItem.Translate。保存されない。
//      親に合わせる〈fill_width〉ノードでも効く）、無ければ CanvasTransform.Position（W2-3 の使い方のまま）
//    - 同じ SwipeGroup の他の行が開いていたら、この行のドラッグの始まりで閉じる（開いている行は 1 つだけ）
//    - 一覧のスクロールが始まったら閉じる（SwipeGroup.CloseAll を持ち主が OnScrollStart から呼ぶ）
//    - 行を使い回すときは Reset で閉じる（ListView.Recycled から。構え・確定・文字の位置も戻す）
//  動いている間は SEED.Redraw.Request()（W2-10a の約束。on_demand でも止まらない）。
// ============================================================

/// <summary>フルスワイプで構えた・解いたときの触感（<see cref="SwipeActions.ArmHaptic"/>）。</summary>
public enum SwipeHaptic
{
    /// <summary>出さない。</summary>
    None = 0,
    /// <summary><see cref="Haptics.Tap"/>（端末の「タップ時のバイブ」の設定が効く）。</summary>
    Tap = 1,
    /// <summary><see cref="Haptics.Vibrate"/>（<see cref="SwipeActions.VibrateMilliseconds"/> ミリ秒。メディア・ゲームの振動の設定が効く）。</summary>
    Vibrate = 2,
}

/// <summary>行を横へずらすと操作のボタンが出るスワイプの操作（行 1 つぶん）。フルスワイプで確定もできる。</summary>
public sealed class SwipeActions
{
    /// <summary>
    /// Vibrate の長さの既定（ミリ秒。docs/backlog.md の案「強めにするなら Haptics.Vibrate(20) 程度」。
    /// Android の EFFECT_CLICK と同じくらい短い単発）。
    /// </summary>
    public const int DefaultVibrateMilliseconds = 20;
    /// <summary>文字と Front の後ろの端の間の既定（space.l が引けないとき。default_theme.json の space.l と同じ 16）。</summary>
    public const float DefaultLabelMargin = 16f;

    /// <summary>ずらす行の見た目（Front）。</summary>
    public GameObject Front { get; }
    /// <summary>操作のボタンの側。</summary>
    public SwipeSide Side => _model.Side;
    /// <summary>開いたときにずらす長さ（操作のボタンの幅。キャンバスの単位）。</summary>
    public float ActionsExtent { get => _model.ActionsExtent; set => _model.ActionsExtent = value; }
    /// <summary>開くとみなすずらし量の割合（0〜1。既定 0.5）。</summary>
    public float OpenThreshold { get => _model.OpenThreshold; set => _model.OpenThreshold = value; }
    /// <summary>速さで開く・閉じるを決める閾値（dp/秒。既定 120）。</summary>
    public float EscapeVelocityDp { get => _model.EscapeVelocityDp; set => _model.EscapeVelocityDp = value; }
    /// <summary>開く・閉じるの動きの時間（秒。既定 0.25）。</summary>
    public float SettleSeconds { get => _model.SettleSeconds; set => _model.SettleSeconds = value; }
    /// <summary>開いている行を 1 つにする組（null なら組まない）。</summary>
    public SwipeGroup? Group
    {
        get => _group;
        set
        {
            _group?.Remove(this);
            _group = value;
            _group?.Add(this);
        }
    }

    // ── フルスワイプ（W2 の手直し P2-3）──────────────────────────
    /// <summary>フルスワイプ（大きく払うと確定）を有効にする（既定 false = W2-3 の振る舞いのまま）。</summary>
    public bool FullSwipe { get => _model.FullSwipe; set => _model.FullSwipe = value; }
    /// <summary>
    /// 行の幅（キャンバスの単位）。0 以下なら自動: Front のレイアウトの大きさ（<see cref="CanvasTransform.LayoutSize"/>。前のフレームの描画の値）、
    /// まだ無ければ Front の Sprite の幅。
    /// </summary>
    public float RowExtent { get; set; }
    /// <summary>今使っている行の幅（<see cref="RowExtent"/> か自動の値。分からなければ 0）。</summary>
    public float ResolvedRowExtent => RowExtent > 0f ? RowExtent : AutoRowExtent();
    /// <summary>
    /// 構えたときに Front の後ろの端へ付いて動く文字のノード（任意。例 Actions の中の「削除」）。作った時の位置を元の位置として控える。
    /// 親（Actions）は行の左の端から行の幅いっぱいに置く（CanvasComponent と fill_width・fill_height）前提。
    /// </summary>
    public GameObject? FullSwipeLabel
    {
        get => _label;
        set
        {
            _label = value;
            CaptureLabelRest();
            Apply();
        }
    }
    /// <summary>構えたときの触感（既定 Tap）。</summary>
    public SwipeHaptic ArmHaptic { get; set; } = SwipeHaptic.Tap;
    /// <summary>構えを解いたときの触感（既定 None = 出さない）。</summary>
    public SwipeHaptic DisarmHaptic { get; set; } = SwipeHaptic.None;
    /// <summary>触感が Vibrate のときの長さ（ミリ秒。既定 20）。</summary>
    public int VibrateMilliseconds { get; set; } = DefaultVibrateMilliseconds;

    // ── 状態 ────────────────────────────────────────────────
    /// <summary>今のずらし量（0 = 閉じた・<see cref="OpenOffset"/> = 開いた・確定で行の幅の外まで）。</summary>
    public float Offset => _model.Offset;
    /// <summary>開いた状態（開いた・開く途中）か。確定した行は false。</summary>
    public bool IsOpen => _model.IsOpen;
    /// <summary>指でずらしている途中か。</summary>
    public bool IsDragging => _model.IsDragging;
    /// <summary>開く・閉じる・流し切り・文字の動きの途中か。</summary>
    public bool IsAnimating => _model.IsAnimating;
    /// <summary>フルスワイプの閾値を越えて構えているか。</summary>
    public bool IsArmed => _model.IsArmed;
    /// <summary>確定した（流し切りの途中か流し切った）。Reset まで操作を受けない。</summary>
    public bool IsCommitted => _model.IsCommitted;
    /// <summary>「削除」の文字の置き場の重み（0 = 元の位置・1 = Front の後ろの端に付く）。</summary>
    public float LabelWeight => _model.LabelWeight;
    /// <summary>構えた回数（触感を出した回数。診断用）。</summary>
    public int ArmCount => _model.ArmCount;
    /// <summary>開いたときのずらし量（右側の操作なら負）。</summary>
    public float OpenOffset => _model.OpenOffset;

    // ── コールバック ────────────────────────────────────────
    /// <summary>開いた（開くと決まった）ときに呼ばれる。</summary>
    public Action<SwipeActions>? Opened { get; set; }
    /// <summary>閉じた（閉じると決まった）ときに呼ばれる。</summary>
    public Action<SwipeActions>? Closed { get; set; }
    /// <summary>フルスワイプの閾値を越えて構えたときに呼ばれる（触感の後）。</summary>
    public Action<SwipeActions>? Armed { get; set; }
    /// <summary>構えを解いたときに呼ばれる。</summary>
    public Action<SwipeActions>? Disarmed { get; set; }
    /// <summary>確定した（流し切り始めた）ときに呼ばれる（構えたまま離した・<see cref="Commit"/>）。</summary>
    public Action<SwipeActions>? CommitStarted { get; set; }
    /// <summary>流し切ったときに呼ばれる（確定のコールバック。持ち主が行を畳んでデータから消し、使い回す前に <see cref="Reset"/> する）。</summary>
    public Action<SwipeActions>? FullSwiped { get; set; }

    /// <summary>状態の機械。</summary>
    private readonly SwipeModel _model;
    private SwipeGroup? _group;
    /// <summary>閉じたときの Front の位置（X。Position で動かすとき）。</summary>
    private readonly float _baseX;
    /// <summary>Front を CanvasLayoutItem.Translate で動かすか（false なら CanvasTransform.Position）。</summary>
    private readonly bool _frontTranslates;
    /// <summary>前のドラッグのイベントのノードのローカルの X。</summary>
    private float _lastLocalX;
    /// <summary>文字のノード。</summary>
    private GameObject? _label;
    /// <summary>文字を Translate で動かすか。</summary>
    private bool _labelTranslates;
    /// <summary>文字の元の位置（X）・anchor の X・pivot の X（作った時・FullSwipeLabel を渡した時の控え）。</summary>
    private float _labelBaseX, _labelAnchorX, _labelPivotX;

    /// <summary>作る（Front の今の位置を閉じた位置として控える）。</summary>
    /// <param name="front">ずらす行の見た目。</param>
    /// <param name="actionsExtent">開いたときにずらす長さ（操作のボタンの幅）。</param>
    /// <param name="side">操作のボタンの側。</param>
    public SwipeActions(GameObject front, float actionsExtent, SwipeSide side = SwipeSide.Right)
    {
        Front = front;
        _model = new SwipeModel(side, actionsExtent);
        _baseX = front.GetComponent<CanvasTransform>() is { } ct ? ct.Position.x : 0f;
        _frontTranslates = front.GetComponent<CanvasLayoutItem>() is not null;
    }

    /// <summary>行の OnGestureDragStart から呼ぶ（他の開いている行を閉じ、動きを止めてずらし始める）。確定の後は何もしない。</summary>
    public void OnDragStart(GestureEvent e)
    {
        if (_model.IsCommitted) return;
        _group?.CloseOthers(this);
        SyncSettings();
        Emit(_model.DragStart());
        // 押した位置から slop の分は当てない（指の位置から飛ばない）
        _lastLocalX = e.LocalPosition.x;
    }

    /// <summary>行の OnGestureDragUpdate から呼ぶ（指の横の移動だけずらし、構え・解くを決める）。確定の後は何もしない。</summary>
    public void OnDragUpdate(GestureEvent e)
    {
        if (_model.IsCommitted) return;
        if (!_model.IsDragging) OnDragStart(e);
        float delta = e.LocalPosition.x - _lastLocalX;
        _lastLocalX = e.LocalPosition.x;
        SyncSettings();
        var signals = _model.DragBy(delta);
        Apply();
        Emit(signals);
    }

    /// <summary>
    /// 行の OnGestureDragEnd から呼ぶ。構えていれば確定して流し切り、そうでなければ速さか閾値で開いたまま・閉じるを決めて動かす
    /// （取り消しなら構えを解いて元の状態へ）。確定の後は何もしない。
    /// </summary>
    public void OnDragEnd(GestureEvent e)
    {
        if (_model.IsCommitted) return;
        SyncSettings();
        var signals = _model.Release(e.VelocityDp.x, e.Canceled);
        Apply();
        Emit(signals);
    }

    /// <summary>開く。</summary>
    public void Open(bool animated = true) => Run(_model.Open(animated));

    /// <summary>閉じる（構えも解く）。</summary>
    public void Close(bool animated = true) => Run(_model.Close(animated));

    /// <summary>
    /// 確定の流れを始める（Front を行の幅の外まで流し切り → <see cref="FullSwiped"/>）。開いた行の操作のボタンのタップから呼ぶ。
    /// 確定の後は何もしない（二重に確定しない）。
    /// </summary>
    public void Commit()
    {
        if (_model.IsCommitted) return;
        SyncSettings();
        Run(_model.Commit());
    }

    /// <summary>すぐ閉じる（行を使い回すとき。ドラッグの途中・構え・確定・文字の位置もやめる）。</summary>
    public void Reset() => Run(_model.Reset());

    /// <summary>毎フレーム呼ぶ（行のスクリプトの Update から。開く・閉じる・流し切り・文字の動きを進める）。</summary>
    /// <param name="dt">経過時間（秒。Time.UnscaledDeltaTime を渡す）。</param>
    public void Update(float dt)
    {
        if (!_model.IsAnimating && !_model.IsCommitting) return;
        Run(_model.Advance(dt));
    }

    /// <summary>状態を変えた後: 見た目を当ててから知らせる（知らせの中で Reset されても見た目が食い違わない）。</summary>
    private void Run(SwipeSignal signals)
    {
        Apply();
        Emit(signals);
    }

    /// <summary>テーマのトークンと行の幅を状態の機械へ入れ直す（テーマの切り替え・行の幅の変化に追従する）。</summary>
    private void SyncSettings()
    {
        _model.RowExtent = ResolvedRowExtent;
        _model.ArmRatio = UiTheme.Number(UiTokens.RatioSwipeFull, SwipeMath.DefaultFullSwipeRatio);
        _model.CancelRatio = UiTheme.Number(UiTokens.RatioSwipeFullCancel, SwipeMath.DefaultFullSwipeCancelRatio);
        _model.LabelSeconds = UiTheme.Number(UiTokens.MotionSwipeFull, SwipeMath.DefaultFullSwipeLabelSeconds);
        _model.DismissSeconds = UiTheme.Number(UiTokens.MotionSwipeDismiss, SwipeMath.DefaultDismissSeconds);
    }

    /// <summary>知らせを立った順にコールバック・触感へ（構え → 解く → 開く → 閉じる → 確定 → 流し切った）。</summary>
    private void Emit(SwipeSignal signals)
    {
        if (signals == SwipeSignal.None) return;
        if ((signals & SwipeSignal.Armed) != 0)
        {
            PlayHaptic(ArmHaptic);
            Armed?.Invoke(this);
        }
        if ((signals & SwipeSignal.Disarmed) != 0)
        {
            PlayHaptic(DisarmHaptic);
            Disarmed?.Invoke(this);
        }
        if ((signals & SwipeSignal.Opened) != 0) Opened?.Invoke(this);
        if ((signals & SwipeSignal.Closed) != 0) Closed?.Invoke(this);
        if ((signals & SwipeSignal.CommitStarted) != 0) CommitStarted?.Invoke(this);
        if ((signals & SwipeSignal.Dismissed) != 0) FullSwiped?.Invoke(this);
    }

    /// <summary>単発の触感を出す（デスクトップの模擬は振動せず回数をログに残す）。</summary>
    private void PlayHaptic(SwipeHaptic kind)
    {
        switch (kind)
        {
            case SwipeHaptic.Tap:
                Haptics.Tap();
                break;
            case SwipeHaptic.Vibrate:
                Haptics.Vibrate(Math.Max(Haptics.MinVibrateMilliseconds, VibrateMilliseconds));
                break;
        }
    }

    /// <summary>自動の行の幅: Front のレイアウトの大きさ（前のフレームの描画）、無ければ Front の Sprite の幅。</summary>
    private float AutoRowExtent()
    {
        if (Front.GetComponent<CanvasTransform>() is { HasLayout: true } ct)
        {
            float width = ct.LayoutSize.x;
            if (width > 0f) return width;
        }
        return Front.GetComponent<Sprite>() is { } sprite ? MathF.Max(0f, sprite.Width) : 0f;
    }

    /// <summary>文字のノードの元の位置・anchor・pivot と、動かし方（Translate か Position）を控える。</summary>
    private void CaptureLabelRest()
    {
        _labelTranslates = false;
        _labelBaseX = _labelAnchorX = _labelPivotX = 0f;
        if (_label is not { IsValid: true } label) return;
        if (label.GetComponent<CanvasTransform>() is { } ct)
        {
            _labelBaseX = ct.Position.x;
            _labelAnchorX = ct.Anchor.x;
            _labelPivotX = ct.Pivot.x;
        }
        _labelTranslates = label.GetComponent<CanvasLayoutItem>() is not null;
    }

    /// <summary>文字のノードの矩形の幅（レイアウトの大きさ → Text の枠 → Sprite の幅 → 0 = pivot の点）。</summary>
    private static float LabelWidth(GameObject label)
    {
        if (label.GetComponent<CanvasTransform>() is { HasLayout: true } ct) return MathF.Max(0f, ct.LayoutSize.x);
        if (label.GetComponent<Text>() is { } text) return MathF.Max(0f, text.BoxWidth);
        if (label.GetComponent<Sprite>() is { } sprite) return MathF.Max(0f, sprite.Width);
        return 0f;
    }

    /// <summary>Front と文字のノードを今のずらし量・重みの位置へ置き、描き直しを頼む。</summary>
    private void Apply()
    {
        MoveX(Front, _frontTranslates, _baseX, _model.Offset);
        if (_label is { IsValid: true } label)
        {
            float rowExtent = ResolvedRowExtent;
            float width = LabelWidth(label);
            float restLeft = SwipeMath.LabelRestLeft(_labelAnchorX, rowExtent, _labelBaseX, _labelPivotX, width);
            float shift = SwipeMath.LabelShift(_model.LabelWeight, _model.Offset, _model.Direction, rowExtent, restLeft, width,
                UiTheme.Number(UiTokens.SpaceL, DefaultLabelMargin));
            MoveX(label, _labelTranslates, _labelBaseX, shift);
        }
        // 動いている間は描き続ける（W2-10a）
        Redraw.Request();
    }

    /// <summary>ノードを横へずらす（CanvasLayoutItem があれば Translate.x = 量、無ければ Position.x = 元の X + 量。縦は触らない）。</summary>
    private static void MoveX(GameObject node, bool translate, float baseX, float amount)
    {
        if (translate)
        {
            if (node.GetComponent<CanvasLayoutItem>() is not { } item) return;
            item.Translate = new Vector2(amount, item.Translate.y);
            return;
        }
        if (node.GetComponent<CanvasTransform>() is not { } ct) return;
        ct.Position = new Vector2(baseX + amount, ct.Position.y);
    }
}
