namespace SEED;

/// <summary>
/// GameObject の入力マップ（InputMapComponent）へのアクセサ。
/// アクターにアタッチされた .inputmap（アクション名 → 物理入力のマッピング）を評価する
/// 薄いラッパー（評価はエンジン側の action_map が行う）。
///
/// エディタの InputMap エディタで定義したアクションを、アクション名で参照する。
/// PC プラットフォームのバインディング（Key / WASD 合成軸）のみ評価する
/// （ゲームパッド等の基盤は未実装）。
///
/// 取得は <c>gameObject.GetComponent&lt;InputMap&gt;()</c>。InputMap を持たない場合は null。
/// <code>
/// if (gameObject.GetComponent&lt;InputMap&gt;() is { } input)
/// {
///     if (input.GetActionDown("Jump")) Jump();
///     var move = input.GetVector2("Move");
/// }
/// </code>
/// </summary>
public readonly struct InputMap : IComponentHandle<InputMap>
{
    /// <summary>この InputMap が属するスロット entity。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント種別名（Rust 側解決キーと一致必須）。</summary>
    private const string Comp = "InputMap";

    // ── 判定種別（Rust 側 action_map の ACTION_KIND_* と一致させる）──
    private const int KindPress = 0;   // 押している間
    private const int KindDown = 1;    // 押した瞬間
    private const int KindUp = 2;      // 離した瞬間

    internal InputMap(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（明示的: GetComponent 経由でのみ使われる）──
    static string IComponentHandle<InputMap>.ComponentKindName => Comp;
    static InputMap IComponentHandle<InputMap>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── Bool アクション ──────────────────────────────────────

    /// <summary>アクションに割り当てたキーを押している間 true（Bool アクション向け）。</summary>
    public bool GetAction(string name) => ScriptHost.InputAction(_entity, KindPress, name);

    /// <summary>アクションに割り当てたキーを押した瞬間のフレームだけ true。</summary>
    public bool GetActionDown(string name) => ScriptHost.InputAction(_entity, KindDown, name);

    /// <summary>アクションに割り当てたキーを離した瞬間のフレームだけ true。</summary>
    public bool GetActionUp(string name) => ScriptHost.InputAction(_entity, KindUp, name);

    // ── 軸アクション ─────────────────────────────────────────

    /// <summary>Axis1D アクションの値（[-1, 1]）。WASD 合成軸 or キー押下=1.0。</summary>
    public float GetAxis(string name) => ScriptHost.InputActionAxis1D(_entity, name);

    /// <summary>Vector2 アクションの値（各成分 [-1, 1]）。Horizontal→x, Vertical→y。</summary>
    public Vector2 GetVector2(string name) => ScriptHost.InputActionVector2(_entity, name);
}
