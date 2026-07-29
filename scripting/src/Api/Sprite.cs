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
}
