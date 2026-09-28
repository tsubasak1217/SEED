using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ThemeStyle.cs — 部品でないスプライト・文字をテーマのトークンに結び付ける（W2-9。docs/ui_theme.md §7.3）
//
//  画面の背景・カード・見出し・説明の文字のような「部品のスクリプトを持たない見た目」は、プレハブ・シーンに書いた色のままだと
//  テーマを替えても変わらない。このスクリプトを付けてトークンの名前を書くと、テーマが替わるたびに（部品と同じく登録簿から）
//  自分のアクターの Sprite・Text へ当て直す。空の欄は触らない（プレハブの値のまま）。
//  例: 背景 SpriteColor = "color.background"、見出し TextColor = "color.on_surface"・TextSize = "text.title"・
//      TextWeight = "font.weight_title"、カード SpriteColor = "color.surface"・CornerRadius = "radius.card"。
//  書体（TextFont）と太さ（TextWeight）の既定は font.family・font.weight（部品の文字と同じ書体にそろう）。
// ============================================================

/// <summary>部品でないスプライト・文字をテーマのトークンに結び付ける。</summary>
public sealed class ThemeStyle : UiWidget
{
    /// <summary>Sprite の色のトークン（空なら触らない）。</summary>
    [SerializeField(Label = "面の色")]
    public string SpriteColor = string.Empty;

    /// <summary>Sprite の縁の色のトークン（空なら触らない）。</summary>
    [SerializeField(Label = "縁の色")]
    public string BorderColor = string.Empty;

    /// <summary>Sprite の角丸のトークン（四隅同じ。空なら触らない）。</summary>
    [SerializeField(Label = "角丸")]
    public string CornerRadius = string.Empty;

    /// <summary>Text の色のトークン（空なら触らない）。</summary>
    [SerializeField(Label = "文字の色")]
    public string TextColor = string.Empty;

    /// <summary>Text の大きさのトークン（空なら触らない）。</summary>
    [SerializeField(Label = "文字の大きさ")]
    public string TextSize = string.Empty;

    /// <summary>Text の書体のトークン（既定 font.family。空なら触らない）。</summary>
    [SerializeField(Label = "書体")]
    public string TextFont = UiTokens.FontFamily;

    /// <summary>Text の太さのトークン（既定 font.weight。空なら触らない）。</summary>
    [SerializeField(Label = "文字の太さ")]
    public string TextWeight = UiTokens.FontWeight;

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.UI]";

    /// <summary>引けないトークンを警告したか（同じ部品では 1 度だけ）。</summary>
    private bool _warned;

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        var theme = Theme;
        string missing = string.Empty;
        if (SpriteOf() is { } sprite)
        {
            if (SpriteColor.Length > 0) { if (theme.TryColor(SpriteColor, out var fill)) sprite.Color = fill; else missing += SpriteColor + " "; }
            if (BorderColor.Length > 0) { if (theme.TryColor(BorderColor, out var border)) sprite.BorderColor = border; else missing += BorderColor + " "; }
            if (CornerRadius.Length > 0) { if (theme.TryNumber(CornerRadius, out var radius)) sprite.CornerRadius = radius; else missing += CornerRadius + " "; }
        }
        if (gameObject.GetComponent<Text>() is { } text)
        {
            if (TextColor.Length > 0) { if (theme.TryColor(TextColor, out var color)) text.Color = color; else missing += TextColor + " "; }
            if (TextSize.Length > 0)
            {
                if (theme.TryNumber(TextSize, out var size)) { if (text.FontSize != size) text.FontSize = size; }
                else missing += TextSize + " ";
            }
            if (TextFont.Length > 0)
            {
                if (theme.TryText(TextFont, out var family)) { if (text.FontPath != family) text.FontPath = family; }
                else missing += TextFont + " ";
            }
            if (TextWeight.Length > 0)
            {
                if (theme.TryNumber(TextWeight, out var weight)) { if (text.Weight != weight) text.Weight = weight; }
                else missing += TextWeight + " ";
            }
        }
        // 型の合わない・無いトークンの名前は、データの書き間違いなので 1 度だけ知らせる（見た目はプレハブの値のまま）
        if (missing.Length > 0 && !_warned)
        {
            _warned = true;
            Debug.LogWarning($"{LogPrefix} ThemeStyle（{gameObject.Name}）: テーマで引けないトークン（名前か型の誤り）: {missing.Trim()}");
        }
    }
}
