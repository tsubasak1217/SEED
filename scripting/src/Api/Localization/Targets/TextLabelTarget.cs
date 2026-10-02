namespace SEED.Localization;

// ============================================================
//  TextLabelTarget.cs — Text コンポーネント（キャンバスの文字）へ文字を当てる当てる先
//
//  LocalizedText（同じアクタの Text）と、LocalizedLabel の表の最後の行（部品でない文字のアクタ）が使う。
//  Content を丸ごと置き換えるので、Text の差し込みスロットの記法（{num}・{color}…）は文の中に書けばそのまま効く
//  （L10n の差し込みは渡していない名前の {…} を残す。docs/localization.md §4）。
// ============================================================

/// <summary>Text コンポーネントへ文字を当てる。</summary>
public sealed class TextLabelTarget : ILocalizedTarget
{
    /// <summary>表の種類の名前。</summary>
    public const string Kind = "Text";

    /// <summary>当てる Text。</summary>
    private readonly Text _text;

    /// <summary>当てる先を作る。</summary>
    /// <param name="text">当てる Text。</param>
    public TextLabelTarget(Text text)
    {
        _text = text;
    }

    /// <inheritdoc />
    public string KindName => Kind;

    /// <inheritdoc />
    public bool IsAlive => _text.IsValid;

    /// <inheritdoc />
    public bool Apply(LocalizedRequest request)
    {
        if (!_text.IsValid) return false;
        _text.Content = request.Resolve();
        return true;
    }
}
