namespace SEED.UI;

// ============================================================
//  UiTokenInfo.cs — トークンの表（UiTokenCatalog）の 1 行（W2-9）
//
//  名前・型・使う部品。既定値はデータ（Theme/default_theme.json）が持つ（値を 2 か所に書かない）。
// ============================================================

/// <summary>トークンの表の 1 行。</summary>
/// <param name="Name">トークンの名前（「グループ.名前」。例 color.primary）。</param>
/// <param name="Kind">型。</param>
/// <param name="UsedBy">使う部品（docs の表の「使う部品」の列。部品が読まない目安のトークンはそう書く）。</param>
public readonly record struct UiTokenInfo(string Name, UiTokenKind Kind, string UsedBy)
{
    /// <summary>トークンのグループ（最初の「.」の前。例 color）。</summary>
    public string Group => UiTokenCatalog.GroupOf(Name);
}
