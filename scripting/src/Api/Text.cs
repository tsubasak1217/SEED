namespace SEED;

/// <summary>
/// キャンバス上のテキスト表示（TextComponent）へのアクセサ。
///
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
/// TextComponent を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
///
/// <para><b>用途</b><br/>
/// 所持金・釣った魚のサイズ・ゲージの数値など、Play 中に毎フレーム書き換わる HUD。
/// <c>Content</c> の代入は文字列を丸ごと差し替えるだけなので毎フレーム呼んで良い。
/// </para>
///
/// <example>
/// <code>
/// if (gameObject.GetComponent&lt;Text&gt;() is { } label)
/// {
///     label.Content = $"所持金: {money}";
/// }
/// </code>
/// </example>
/// </summary>
public readonly struct Text : IComponentHandle<Text>
{
    /// <summary>この Text が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "Text";

    internal Text(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Text>.ComponentKindName => Comp;
    static Text IComponentHandle<Text>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>表示する文字列（get/set。改行 "\n" で複数行になる）。</summary>
    public string Content
    {
        get => ScriptHost.TryGetString(_entity, Comp, "content", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "content", value ?? "");
    }

    /// <summary>フォントサイズ（get/set。キャンバスピクセル）。</summary>
    public float FontSize
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "font_size", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "font_size", value);
    }

    /// <summary>文字色（get/set。RGBA 0..1）。</summary>
    public Color Color
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "color", out var c) ? c : Color.White;
        set => ScriptHost.TrySetColor(_entity, Comp, "color", value);
    }

    /// <summary>
    /// 使用フォントの assets:// 仮想パス（get/set。空文字 = 組み込みフォント）。
    /// .otf / .ttf を指す。読み込みに失敗した場合は組み込みフォントで描画される。
    /// </summary>
    public string FontPath
    {
        get => ScriptHost.TryGetString(_entity, Comp, "font_path", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "font_path", value ?? "");
    }

    /// <summary>
    /// アイコンセット（.icons）の assets:// 仮想パス（get/set。空文字 = 未使用）。
    ///
    /// <para>本文の <c>[icon:名前]</c> 記法は、このファイルの表で名前 → 画像パスを引く。
    /// 未設定でも <c>[img:assets://...]</c>（パス直接指定）は使える。
    /// 名前が引けない・画像が読めない場合は 1em 幅の空白になり、本文は崩れない。</para>
    ///
    /// <example>
    /// <code>
    /// label.IconSet = "assets://ui/keys.icons";
    /// label.Content = "移動: [icon:key_w][icon:key_a][icon:key_s][icon:key_d]";
    /// </code>
    /// </example>
    /// </summary>
    public string IconSet
    {
        get => ScriptHost.TryGetString(_entity, Comp, "icon_set", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "icon_set", value ?? "");
    }

    /// <summary>
    /// 縁取りの太さ（get/set。キャンバスピクセル。0 = 縁取りなし）。
    /// フォントサイズの約 1/8 が実効上限で、それを超える値は上限で頭打ちになる。
    /// </summary>
    public float OutlineWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "outline_width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "outline_width", value);
    }

    /// <summary>縁取りの色（get/set。RGBA 0..1。既定は不透明な黒）。</summary>
    public Color OutlineColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "outline_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "outline_color", value);
    }

    /// <summary>行送り倍率（get/set。フォントサイズに対する倍率）。</summary>
    public float LineSpacing
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "line_spacing", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "line_spacing", value);
    }

    /// <summary>描画レイヤー（get/set。大きいほど手前。Sprite と共通の順序で解決される）。</summary>
    public int Layer
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "layer", out var v) ? (int)v : 0;
        set => ScriptHost.TrySetFloat(_entity, Comp, "layer", value);
    }

    /// <summary>
    /// 水平方向の基準位置（get/set）。"left" / "center" / "right"。
    /// 未知の値を設定した場合は無視される（既存値が保たれる）。
    /// </summary>
    public string Align
    {
        get => ScriptHost.TryGetString(_entity, Comp, "align", out var s) ? s : "left";
        set => ScriptHost.TrySetString(_entity, Comp, "align", value ?? "left");
    }

    /// <summary>
    /// 垂直方向の基準位置（get/set）。"top" / "middle" / "bottom"。
    /// 未知の値を設定した場合は無視される（既存値が保たれる）。
    /// </summary>
    public string VerticalAlign
    {
        get => ScriptHost.TryGetString(_entity, Comp, "vertical_align", out var s) ? s : "top";
        set => ScriptHost.TrySetString(_entity, Comp, "vertical_align", value ?? "top");
    }

    /// <summary>
    /// 枠の幅（get/set。キャンバスピクセル。0 = 枠なし）。
    ///
    /// <para>0 のときは従来どおり「アクター位置に対してブロックを置く」レイアウトで、
    /// 自動折り返しも <c>CanvasTransform.Pivot</c> も効かない。
    /// 正の値にすると枠が有効になり、<see cref="Align"/> / <see cref="VerticalAlign"/> は
    /// 「枠の中でのどこへ置くか」を意味し、pivot が Sprite と同じ意味で効く。</para>
    /// </summary>
    public float BoxWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "box_width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "box_width", value);
    }

    /// <summary>
    /// 枠の最小高さ（get/set。キャンバスピクセル）。
    /// 実際の高さは「この値」と「行数から決まる内容高さ」の**大きいほう**になる（自動伸縮）。
    /// </summary>
    public float BoxHeight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "box_height", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "box_height", value);
    }

    /// <summary>
    /// 枠幅での自動折り返しを行うか（get/set）。<see cref="BoxWidth"/> が 0 のときは無視される。
    /// </summary>
    public bool Wrap
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "wrap", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "wrap", value);
    }

    /// <summary>
    /// 文字の太さ（get/set。キャンバスピクセル。負で細く・正で太く。0 = フォント本来）。
    /// 縁取りと同じく、フォントサイズの約 1/8 が実効上限で、それを超えると頭打ちになる。
    /// </summary>
    public float Weight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "weight", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "weight", value);
    }

    /// <summary>
    /// ドロップシャドウのオフセット（get/set。キャンバスピクセル。X 右・Y 下）。
    /// (0, 0) で影なし。
    /// </summary>
    public Vector2 ShadowOffset
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "shadow_offset_x", out var x)
            && ScriptHost.TryGetFloat(_entity, Comp, "shadow_offset_y", out var y)
            ? new Vector2(x, y)
            : Vector2.Zero;
        set
        {
            ScriptHost.TrySetFloat(_entity, Comp, "shadow_offset_x", value.x);
            ScriptHost.TrySetFloat(_entity, Comp, "shadow_offset_y", value.y);
        }
    }

    /// <summary>ドロップシャドウの色（get/set。RGBA 0..1。既定は半透明の黒）。</summary>
    public Color ShadowColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "shadow_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "shadow_color", value);
    }

    /// <summary>
    /// ドロップシャドウのぼかし幅（get/set。キャンバスピクセル。0 = シャープ）。
    /// </summary>
    public float ShadowSoftness
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "shadow_softness", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "shadow_softness", value);
    }
}
