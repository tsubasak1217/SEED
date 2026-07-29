namespace SEED;

/// <summary>
/// GameObject の 3D カメラ（CameraComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// カメラの位置・向きは同じ GameObject の Transform（position / rotation）で制御する。
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// </summary>
public readonly struct Camera : IComponentHandle<Camera>
{
    /// <summary>この Camera が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Camera";

    internal Camera(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Camera>.ComponentKindName => Comp;
    static Camera IComponentHandle<Camera>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Camera を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>垂直視野角（度）。</summary>
    public float FieldOfView
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "fov_y_deg", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "fov_y_deg", value);
    }

    /// <summary>ニアクリップ距離。</summary>
    public float Near
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "near", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "near", value);
    }

    /// <summary>ファークリップ距離。</summary>
    public float Far
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "far", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "far", value);
    }

    /// <summary>Play モードで使用するメインカメラか。</summary>
    public bool IsMain
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "is_main", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "is_main", value);
    }

    /// <summary>背景クリアカラー（RGBA, linear）。</summary>
    public Color ClearColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "clear_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "clear_color", value);
    }

    /// <summary>スケーリングのベース解像度（横）。</summary>
    public int TargetWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "target_width", out var v) ? (int)v : 0;
        set => ScriptHost.TrySetFloat(_entity, Comp, "target_width", value);
    }

    /// <summary>スケーリングのベース解像度（縦）。</summary>
    public int TargetHeight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "target_height", out var v) ? (int)v : 0;
        set => ScriptHost.TrySetFloat(_entity, Comp, "target_height", value);
    }

    /// <summary>LetterBox / PillarBox 時の帯カラー（RGBA, linear）。</summary>
    public Color BarColor
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "bar_color", out var c) ? c : Color.Black;
        set => ScriptHost.TrySetColor(_entity, Comp, "bar_color", value);
    }

    /// <summary>投影方式（"perspective" / "orthographic"）。</summary>
    public string Projection
    {
        get => ScriptHost.TryGetString(_entity, Comp, "projection", out var s) ? s : "perspective";
        set => ScriptHost.TrySetString(_entity, Comp, "projection", value);
    }

    /// <summary>正射投影時の縦方向の描画範囲（ワールド単位・全高）。透視投影時は未使用。</summary>
    public float OrthoHeight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "ortho_height", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "ortho_height", value);
    }
}
