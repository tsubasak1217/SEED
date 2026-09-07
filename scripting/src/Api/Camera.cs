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

    // ── ワールド → スクリーン射影 ───────────────────────────

    // 射影モード（Rust 側 host_api.rs の CAMERA_PROJECT_MODE_* と一致させる）
    private const int ProjectModeScreen = 0;
    private const int ProjectModeCanvas = 1;

    /// <summary>
    /// ワールド座標を、このカメラで見たときのスクリーン座標へ変換する。
    ///
    /// <para>
    /// 戻り値の <c>x</c> / <c>y</c> は<b>ゲーム画面左上を原点とするピクセル</b>
    /// （右が +X・下が +Y）で、<see cref="Input.MousePos"/> と同じ座標系。
    /// レターボックス／ピラーボックスの帯も考慮した「実際に描かれている位置」を返す。
    /// </para>
    /// <para>
    /// 戻り値の <c>z</c> は<b>カメラ前方距離</b>（ワールド単位）。
    /// <b>正ならカメラの前方、負なら背後</b>である。背後の点は x/y が
    /// 画面内に見える値になることがあるため、可視判定には必ず <c>z &gt; 0</c> を使うこと。
    /// </para>
    /// <para>
    /// メインカメラでなくても使える（このハンドルが指すカメラで計算する）。
    /// エディタ埋め込み Play・ウィンドウ Play のどちらでも同じ基準になる。
    /// カメラが解決できないときは <see cref="Vector3.Zero"/> を返す。
    /// </para>
    /// </summary>
    /// <param name="world">変換したいワールド座標。</param>
    public Vector3 WorldToScreen(Vector3 world)
    {
        return ScriptHost.TryCameraWorldToScreen(
            _entity, world.x, world.y, world.z, ProjectModeScreen, out var r)
            ? r : Vector3.Zero;
    }

    /// <summary>
    /// ワールド座標を、スクリーンスペースキャンバスの座標系へ変換する。
    ///
    /// <para>
    /// 座標系は<b>画面中央が原点・Y 下向き・1 単位 = 1px</b>で、
    /// <see cref="Input.MousePositionCanvas"/> および
    /// <see cref="CanvasTransform.Position"/> と同じ。
    /// 返り値をそのまま 2D アクターの <c>CanvasTransform.Position</c> へ代入すれば、
    /// その UI が対象のワールド位置に重なる（キャラクターの頭上 HP バーなど）。
    /// </para>
    /// <para>
    /// カメラ背後の判定が必要な場合は <see cref="WorldToScreen"/> の z を使うこと
    /// （このメソッドは 2 成分しか返さないため前後の情報が落ちる）。
    /// </para>
    /// </summary>
    /// <param name="world">変換したいワールド座標。</param>
    public Vector2 WorldToCanvas(Vector3 world)
    {
        return ScriptHost.TryCameraWorldToScreen(
            _entity, world.x, world.y, world.z, ProjectModeCanvas, out var r)
            ? new Vector2(r.x, r.y) : Vector2.Zero;
    }
}
