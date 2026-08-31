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
}
