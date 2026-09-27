namespace SEED;

/// <summary>
/// GameObject の 2D スプライト（SpriteComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// スプライトを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Sprite : IComponentHandle<Sprite>
{
    /// <summary>この Sprite が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Sprite";

    internal Sprite(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Sprite>.ComponentKindName => Comp;
    static Sprite IComponentHandle<Sprite>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Sprite を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>テクスチャファイルパス（assets:// 仮想パス）。空文字列 = テクスチャなし（単色表示）。</summary>
    public string TexturePath
    {
        get => ScriptHost.TryGetString(_entity, Comp, "texture_path", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "texture_path", value);
    }

    /// <summary>表示カラー（RGBA 正規化値）。テクスチャに乗算される。</summary>
    public Color Color
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "color", out var c) ? c : Color.White;
        set => ScriptHost.TrySetColor(_entity, Comp, "color", value);
    }

    /// <summary>表示幅（キャンバスユニット）。</summary>
    public float Width
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "width", value);
    }

    /// <summary>表示高さ（キャンバスユニット）。</summary>
    public float Height
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "height", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "height", value);
    }

    /// <summary>表示サイズ（幅・高さ）をまとめて扱う簡易プロパティ。</summary>
    public Vector2 Size
    {
        get => new(Width, Height);
        set { Width = value.x; Height = value.y; }
    }

    /// <summary>
    /// 描画優先度レイヤー。大きいほど手前に描画される（既定 0）。
    /// 同値はヒエラルキー順。比較は同一描画ゾーン内で行われる
    /// （ビューポートキャンバスはゾーン単位で全キャンバス横断、
    /// ワールドキャンバスはそのキャンバス内）。
    /// </summary>
    public int Layer
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "layer", out var v) ? (int)v : 0;
        set => ScriptHost.TrySetFloat(_entity, Comp, "layer", value);
    }

    /// <summary>
    /// ポインタイベント（OnPointerEnter / Down / Up / Click / Exit）の判定対象にするか。
    /// 既定 false のオプトイン。true にしたスプライトだけがクリック判定に参加する。
    /// </summary>
    public bool RaycastTarget
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "raycast_target", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "raycast_target", value);
    }

    // ============================================================
    //  形と塗り（W2-4。docs/ui_components.md）
    //  長さはキャンバスの単位（Width・Height と同じ）。欄がすべて既定なら従来と同じ見た目・同じ描画経路。
    // ============================================================

    // ── 形 ─────────────────────────────────────────────

    /// <summary>形（矩形・楕円・弧）。既定 Rect。</summary>
    public SpriteShapeKind Shape
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "shape", SpriteShapeKind.Rect);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "shape", value);
    }

    /// <summary>四隅の角丸（左上・右上・右下・左下）。Rect のときだけ効く。辺より大きい指定は描くときに縮む。</summary>
    public CornerRadii CornerRadii
    {
        get => SpriteStyleFieldAccess.GetRadii(_entity, Comp, "corner_radii");
        set => SpriteStyleFieldAccess.SetRadii(_entity, Comp, "corner_radii", value);
    }

    /// <summary>四隅を同じ半径にする簡易プロパティ（読むと左上の半径）。</summary>
    public float CornerRadius
    {
        get => CornerRadii.TopLeft;
        set => CornerRadii = CornerRadii.All(value);
    }

    /// <summary>縁の線の太さ（0 = 縁なし）。形の内側に引く。</summary>
    public float BorderWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "border_width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "border_width", value);
    }

    /// <summary>縁の線の色（Color〈塗りの色〉とは独立。塗りを透明にしても縁は見える）。</summary>
    public Color BorderColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "border_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "border_color", value);
    }

    /// <summary>弧の開始角（度。0 = +X・時計回り。既定 -90 = 真上）。Arc のときだけ効く。</summary>
    public float ArcStart
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "arc_start", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "arc_start", value);
    }

    /// <summary>弧の角度（度。0〜360。360 は一周のリング）。Arc のときだけ効く（進捗の輪は 値 × 360）。</summary>
    public float ArcSweep
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "arc_sweep", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "arc_sweep", value);
    }

    /// <summary>弧の太さ。Arc のときだけ効く。</summary>
    public float ArcThickness
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "arc_thickness", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "arc_thickness", value);
    }

    /// <summary>弧の端を丸くするか。Arc のときだけ効く。</summary>
    public bool ArcRoundCaps
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "arc_round_caps", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "arc_round_caps", value);
    }

    // ── 塗り ───────────────────────────────────────────

    /// <summary>塗り（単色・線形・放射）。既定 Solid（= Color）。グラデーションの色にも Color が掛かる。</summary>
    public SpriteFillKind Fill
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "fill", SpriteFillKind.Solid);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "fill", value);
    }

    /// <summary>グラデーションの色（2〜4 色。範囲外の数の代入は無視される）。</summary>
    public Color[] GradientColors
    {
        get => SpriteStyleFieldAccess.GetGradientColors(_entity, Comp);
        set => SpriteStyleFieldAccess.SetGradientColors(_entity, Comp, value);
    }

    /// <summary>グラデーションの色の位置（0..1・昇順。空 = 等間隔）。</summary>
    public float[] GradientStops
    {
        get => SpriteStyleFieldAccess.GetStops(_entity, Comp);
        set => SpriteStyleFieldAccess.SetStops(_entity, Comp, value ?? System.Array.Empty<float>());
    }

    /// <summary>線形グラデーションの角度（度。0 = 左 → 右、90 = 上 → 下）。</summary>
    public float GradientAngle
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "fill_angle", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "fill_angle", value);
    }

    /// <summary>放射グラデーションの中心（矩形に対する割合。0,0 = 左上）。</summary>
    public Vector2 RadialCenter
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "fill_center", out var v) ? v : new Vector2(0.5f, 0.5f);
        set => ScriptHost.TrySetVec2(_entity, Comp, "fill_center", value);
    }

    /// <summary>放射グラデーションの半径（幅・高さに対する割合。0.5 = 内接する楕円の縁で最後の色）。</summary>
    public Vector2 RadialRadius
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "fill_radius", out var v) ? v : new Vector2(0.5f, 0.5f);
        set => ScriptHost.TrySetVec2(_entity, Comp, "fill_radius", value);
    }

    /// <summary>
    /// グラデーションをまとめて設定する（種類と 2〜4 色。色の位置は等間隔へ戻す）。
    /// </summary>
    /// <param name="kind">Linear か Radial（Solid なら色は変えずに単色へ）。</param>
    /// <param name="colors">2〜4 色。</param>
    public void SetGradient(SpriteFillKind kind, params Color[] colors)
    {
        if (kind != SpriteFillKind.Solid && !SpriteStyleFieldAccess.SetGradientColors(_entity, Comp, colors)) return;
        SpriteStyleFieldAccess.SetStops(_entity, Comp, System.ReadOnlySpan<float>.Empty);
        Fill = kind;
    }

    // ── 9 スライス ─────────────────────────────────────

    /// <summary>画像を 9 スライスで描くか（テクスチャがあるときだけ効く）。</summary>
    public bool NineSlice
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "nine_slice", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "nine_slice", value);
    }

    /// <summary>9 スライスの枠の 4 辺の幅（テクスチャの画素）。</summary>
    public NineSliceBorder NineSliceBorder
    {
        get => SpriteStyleFieldAccess.GetBorder(_entity, Comp, "nine_slice_border");
        set => SpriteStyleFieldAccess.SetBorder(_entity, Comp, "nine_slice_border", value);
    }

    /// <summary>9 スライスの描く枠の倍率（キャンバスの単位 ÷ テクスチャの画素。2 倍の画像なら 0.5）。</summary>
    public float NineSliceScale
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "nine_slice_scale", out var v) ? v : 1f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "nine_slice_scale", value);
    }

    /// <summary>9 スライスの辺（上下左右の帯）の埋め方。</summary>
    public NineSliceMode NineSliceEdgeMode
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "nine_slice_edge", NineSliceMode.Stretch);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "nine_slice_edge", value);
    }

    /// <summary>9 スライスの中央の埋め方。</summary>
    public NineSliceMode NineSliceCenterMode
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "nine_slice_center", NineSliceMode.Stretch);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "nine_slice_center", value);
    }

    /// <summary>9 スライスの中央を描くか（false = 枠だけ）。</summary>
    public bool NineSliceFillCenter
    {
        get => !ScriptHost.TryGetBool(_entity, Comp, "nine_slice_fill_center", out var b) || b;
        set => ScriptHost.TrySetBool(_entity, Comp, "nine_slice_fill_center", value);
    }

    // ── 影 ─────────────────────────────────────────────

    /// <summary>ぼかしの影を描くか（形と同じ形を後ろに描く）。</summary>
    public bool Shadow
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "shadow", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "shadow", value);
    }

    /// <summary>影の色（Color〈塗りの色〉とは独立）。</summary>
    public Color ShadowColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "shadow_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "shadow_color", value);
    }

    /// <summary>影のずれ（X 右・Y 下）。</summary>
    public Vector2 ShadowOffset
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "shadow_offset", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "shadow_offset", value);
    }

    /// <summary>影のぼかしの幅（0 = くっきり）。</summary>
    public float ShadowBlur
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "shadow_blur", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "shadow_blur", value);
    }
}
