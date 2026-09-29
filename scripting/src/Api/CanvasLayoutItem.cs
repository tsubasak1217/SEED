namespace SEED;

/// <summary>
/// GameObject の「レイアウトの子の側の指定」（CanvasLayoutItemComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// コンテナ（<see cref="CanvasStack"/>・<see cref="CanvasWrap"/>・<see cref="CanvasGrid"/>）の子に付けて並べられ方を指定する
/// （付けなくても子は自分の大きさ・コンテナの揃えで並ぶ）。大きさはキャンバスの単位（Sprite の幅・高さと同じ）。
/// <see cref="FillWidth"/> / <see cref="FillHeight"/> はコンテナの外の子で使う（親の CanvasComponent の領域いっぱいに広げる）。
/// 変更は次のフレームのレイアウトから効く。指定を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasLayoutItem : IComponentHandle<CanvasLayoutItem>
{
    /// <summary>この CanvasLayoutItem が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasLayoutItem";

    internal CanvasLayoutItem(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasLayoutItem>.ComponentKindName => Comp;
    static CanvasLayoutItem IComponentHandle<CanvasLayoutItem>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>コンテナに無視させる（get/set。true なら自分の Anchor・Position のまま。重ねる飾りなど）。</summary>
    public bool IgnoreLayout
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "ignore_layout", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "ignore_layout", value);
    }

    /// <summary>主軸の余りを分ける重み（get/set。CanvasStack のみ。0 = 伸ばさない）。</summary>
    public float Flex
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "flex", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "flex", value);
    }

    /// <summary>大きさの指定（get/set。0 の軸は中身の大きさ）。</summary>
    public Vector2 PreferredSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "preferred_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "preferred_size", value);
    }

    /// <summary>大きさの下限（get/set。0 = 下限なし）。</summary>
    public Vector2 MinSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "min_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "min_size", value);
    }

    /// <summary>大きさの上限（get/set。0 = 上限なし）。</summary>
    public Vector2 MaxSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "max_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "max_size", value);
    }

    /// <summary>交差軸の揃えの上書き（get/set。既定 Auto = コンテナに従う。Grid ではセルの中の置き方）。</summary>
    public ItemAlign AlignSelf
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "align_self", ItemAlign.Auto);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "align_self", value);
    }

    /// <summary>親の幅に合わせる（get/set。コンテナの外の子で使う。親の CanvasComponent の領域の幅いっぱい）。</summary>
    public bool FillWidth
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fill_width", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fill_width", value);
    }

    /// <summary>親の高さに合わせる（get/set。同上）。</summary>
    public bool FillHeight
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fill_height", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fill_height", value);
    }

    // ── 実行中だけの見た目の上書き（W2-7・W2 の手直し 3b。保存しない・インスペクタに出ない）──
    //  レイアウト（大きさ・並び・安全領域）は変えず、置かれた後の見た目だけを変える（CSS の transform に近い）。
    //  画面の組み立て（SEED.UI.ScreenStack・Dialog・BottomSheet・Toast）が出入りの動き・重なりの前後・予測型の戻るのプレビューに使う。
    //  Play の開始・シーンの読み込みで 0（VisualScale は 1）に戻る。

    /// <summary>
    /// 見た目の平行移動（get/set。キャンバスの単位。dp のルートの下なら dp）。置かれた後に足す（親に合わせた・コンテナが並べたノードも動く）。
    /// 子孫も一緒に動き、当たり判定・切り抜きも動いた位置になる。有限でない値は書かない。
    /// </summary>
    public Vector2 Translate
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "translate", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "translate", value);
    }

    /// <summary>
    /// 見た目の平行移動（get/set。自分の置かれた矩形の大きさに対する割合）。(1, 0) で自分の幅だけ右、(0, -1) で高さだけ上。
    /// 画面の大きさを知らずに「右から入る」「上から降りる」を書ける。<see cref="Translate"/> と足し合わせる。
    /// 自分の矩形（コンテナ・親に合わせるの矩形か CanvasComponent の領域）が無いノードでは効かない。
    /// </summary>
    public Vector2 TranslateFraction
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "translate_fraction", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "translate_fraction", value);
    }

    /// <summary>
    /// 自分と子孫の表示（Sprite・SkinnedSprite・Text・2D パーティクル）のレイヤーに足す値（get/set。整数・±16,777,216 まで）。
    /// 祖先の値と足し合わせる。重なる画面（画面のスタック・ダイアログ・シート・トースト）を、中身のレイヤーに依らず前後させる。
    /// 描画の並び・ポインタの最前面・ジェスチャーの遮りが同じ値で比べる。<c>SEED.Draw</c> の図形には効かない。
    /// </summary>
    public int LayerBias
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "layer_bias", out var v) ? (int)v : 0;
        set => ScriptHost.TrySetFloat(_entity, Comp, "layer_bias", value);
    }

    /// <summary>
    /// 見た目の倍率（get/set。軸ごと。既定 (1, 1)。W2 の手直し 3b）。自分の置かれた矩形（レイアウトが割り当てた矩形か
    /// CanvasComponent の領域。無ければ自分の位置）の<b>中心の周り</b>に縮める・広げる。子孫・描画・当たり判定・切り抜き・
    /// <see cref="CanvasTransform.LayoutRect"/> がそろって付いてくる（入れ子のキャンバスの子も中心へ寄る）。
    /// レイアウト（大きさ・並び・安全領域・<see cref="CanvasTransform.LayoutSize"/>）は倍率の前のまま。<see cref="Translate"/> の後に掛かる。
    /// 保存される <see cref="CanvasTransform.Scale"/>（pivot の周り。入れ子のキャンバスの子は左上へ寄る）と違い、実行中だけ・保存しない。
    /// SEED.UI の予測型の戻るのプレビュー（BackDispatcher）が画面・ダイアログの札・シートの板を縮めるのに使う。有限でない値は書かない。
    /// Play の開始・シーンの読み込みで (1, 1) に戻る。
    /// </summary>
    public Vector2 VisualScale
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "visual_scale", out var v) ? v : Vector2.One;
        set => ScriptHost.TrySetVec2(_entity, Comp, "visual_scale", value);
    }
}
