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
/// アクションの Bool 判定は <b>condition（Trigger/Press/Release）</b>を .inputmap 側で設定し、
/// <c>GetAction</c> が「条件適用後の状態」を返す。成立/終了の瞬間は <c>GetActionStart</c> /
/// <c>GetActionEnd</c> で取得する。Key に加えて GamepadButton / GamepadAxis も評価する。
///
/// 取得は <c>gameObject.GetComponent&lt;InputMap&gt;()</c>。InputMap を持たない場合は null。
/// <code>
/// if (gameObject.GetComponent&lt;InputMap&gt;() is { } input)
/// {
///     if (input.GetActionStart("Jump")) Jump();   // アクション成立の瞬間
///     var move = input.GetVector2("Move");         // Axis2D（各 [-1,1]）
/// }
/// </code>
/// </summary>
public readonly struct InputMap : IComponentHandle<InputMap>
{
    /// <summary>この InputMap が属するスロット entity。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント種別名（Rust 側解決キーと一致必須）。</summary>
    private const string Comp = "InputMap";

    // ── アクション評価の種別（Rust 側 host_api の INPUT_ACTION_KIND_* と一致させる）──
    private const int KindAction = 0;  // 条件適用後の状態
    private const int KindStart = 1;   // アクション成立の瞬間
    private const int KindEnd = 2;     // アクション終了の瞬間

    internal InputMap(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（明示的: GetComponent 経由でのみ使われる）──
    static string IComponentHandle<InputMap>.ComponentKindName => Comp;
    static InputMap IComponentHandle<InputMap>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し InputMap を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── Bool アクション ──────────────────────────────────────

    /// <summary>
    /// アクションの状態（.inputmap の condition 適用後）。
    /// condition=Press なら押下中 true / Trigger なら成立フレームのみ / Release なら離したフレームのみ。
    /// </summary>
    public bool GetAction(string name) => ScriptHost.InputAction(_entity, KindAction, name);

    /// <summary>アクションが成立した瞬間のフレームだけ true（条件適用後の立ち上がり）。</summary>
    public bool GetActionStart(string name) => ScriptHost.InputAction(_entity, KindStart, name);

    /// <summary>アクションが終了した瞬間のフレームだけ true（条件適用後の立ち下がり）。</summary>
    public bool GetActionEnd(string name) => ScriptHost.InputAction(_entity, KindEnd, name);

    // ── 軸アクション ─────────────────────────────────────────

    /// <summary>Axis1D アクションの値（[-1, 1]）。正/負バインドの合成。アナログはデッドゾーン適用。</summary>
    public float GetAxis(string name) => ScriptHost.InputActionAxis1D(_entity, name);

    /// <summary>Axis2D アクションの値（各成分 [-1, 1]）。x/y の正負合成。normalize 指定時は正規化。</summary>
    public Vector2 GetVector2(string name) => ScriptHost.InputActionVector2(_entity, name);
}
