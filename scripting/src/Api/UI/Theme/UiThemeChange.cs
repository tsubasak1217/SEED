namespace SEED.UI;

// ============================================================
//  UiThemeChange.cs — テーマの切り替えの知らせ（UiTheme.Changed の引数。W2-9）
// ============================================================

/// <summary>テーマの切り替えの知らせ。</summary>
/// <param name="Theme">当てたテーマ。</param>
/// <param name="Brightness">表示する明暗。</param>
/// <param name="Animated">色を補間しながら切り替えるか（false = すぐ）。</param>
public readonly record struct UiThemeChange(UiThemeDefinition Theme, UiBrightness Brightness, bool Animated);
