namespace SEED;

/// <summary>
/// GameObject の 2D スプライト（SpriteComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// スプライトを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Sprite
{
    /// <summary>この Sprite が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Sprite";

    internal Sprite(Entity entity) { _entity = entity; }

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
}
