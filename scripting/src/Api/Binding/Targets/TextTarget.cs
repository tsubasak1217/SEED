namespace SEED.Binding;

// ============================================================
//  TextTarget.cs — Text コンポーネントの文字（Content）を当てる先（Bind.Text）
//
//  Text が消えたら（アクタの破棄・コンポーネントの削除。Text.IsValid が false）結び付けは外れる。
// ============================================================

/// <summary>Text の文字を当てる先。</summary>
internal sealed class TextTarget : IBindTarget<string>
{
    /// <summary>当てる Text。</summary>
    private readonly Text _text;

    /// <summary>当てる先を作る。</summary>
    /// <param name="text">当てる Text。</param>
    internal TextTarget(Text text)
    {
        _text = text;
    }

    /// <inheritdoc />
    public bool IsAlive => _text.IsValid;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public void Write(string value) => _text.Content = value ?? string.Empty;
}
