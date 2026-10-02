namespace SEED.Binding;

// ============================================================
//  SpriteColorTarget.cs — Sprite の色（Color）を当てる先（Bind.Color）
//
//  Sprite が消えたら（Sprite.IsValid が false）結び付けは外れる。
// ============================================================

/// <summary>Sprite の色を当てる先。</summary>
internal sealed class SpriteColorTarget : IBindTarget<Color>
{
    /// <summary>当てる Sprite。</summary>
    private readonly Sprite _sprite;

    /// <summary>当てる先を作る。</summary>
    /// <param name="sprite">当てる Sprite。</param>
    internal SpriteColorTarget(Sprite sprite)
    {
        _sprite = sprite;
    }

    /// <inheritdoc />
    public bool IsAlive => _sprite.IsValid;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public void Write(Color value) => _sprite.Color = value;
}
