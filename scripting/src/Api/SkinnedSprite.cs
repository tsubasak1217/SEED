namespace SEED;

/// <summary>
/// GameObject のメッシュ変形スキニング 2D スプライト（SkinnedSpriteComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 通常の <see cref="Sprite"/> が「矩形 1 枚」なのに対し、こちらは
/// <c>.sprite_mesh</c> アセットが定義する任意のメッシュを、
/// 子アクター（＝ボーン）の Transform で変形しながら描画する。
///
/// ボーンはシーン上の普通の 2D 子アクターなので、動かしたいときは
/// そのアクターの <see cref="CanvasTransform"/> を操作する
/// （このハンドルからボーンを直接触る API は持たない）。
///
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// コンポーネントを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct SkinnedSprite : IComponentHandle<SkinnedSprite>
{
    /// <summary>この SkinnedSprite が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "SkinnedSprite";

    internal SkinnedSprite(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<SkinnedSprite>.ComponentKindName => Comp;
    static SkinnedSprite IComponentHandle<SkinnedSprite>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し SkinnedSprite を保持しているか）。
    /// null は「未設定」、IsValid == false は「未解決または破棄済み」を意味する。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>メッシュアセット（.sprite_mesh）のパス。空文字列 = 未設定（非表示）。</summary>
    public string MeshPath
    {
        get => ScriptHost.TryGetString(_entity, Comp, "mesh_path", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "mesh_path", value);
    }

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

    /// <summary>
    /// 描画優先度レイヤー。大きいほど手前に描画される（既定 0）。
    /// ソート規約は <see cref="Sprite.Layer"/> と完全に同一で、
    /// 矩形スプライトとスキンスプライトは同じ土俵で前後関係が決まる。
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
}
