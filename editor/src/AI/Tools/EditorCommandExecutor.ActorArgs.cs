// ============================================================
//  EditorCommandExecutor.ActorArgs.cs — 「親のアクタ」引数（DFS ID か名前パス）の解決
//
//  seed_preview（action:add）と seed_template_actor（action:add）の parent は、
//  DFS ID（JSON の整数）でも "Root/Child" 形式の名前パス（JSON の文字列）でも受け取る。
//  名前の解決規則は seed_find_actor と同じ（HierarchyPanel.ActorDfsIdByPath に一元化。
//  ActorRefJump.ActorDfsIdByPath 経由で引く）。EditorCommandExecutor の partial 実装。
// ============================================================

using System.Globalization;
using System.Text.Json;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    /// <summary>
    /// 親のアクタの引数を DFS ID へ解決する。
    /// </summary>
    /// <param name="args">ツール引数。</param>
    /// <param name="name">引数の名前（"parent"）。</param>
    /// <returns>
    /// (Present = 引数があったか, Dfs = 解決した DFS ID, Error = 解決できなかった理由)。
    /// 引数が無ければ (false, null, null)。
    /// </returns>
    private static (bool Present, int? Dfs, string? Error) ResolveParentActorArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (false, null, null);

        // ── 整数 = DFS ID ──
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (!el.TryGetInt32(out var dfs) || dfs < 0)
                return (true, null, $"'{name}' の DFS ID は 0 以上の整数で指定してください（指定: {el.GetRawText()}）。");
            return (true, dfs, null);
        }

        // ── 文字列 = 名前パス（seed_find_actor と同じ解決規則）──
        if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
            return (true, null, $"'{name}' は DFS ID（整数）か \"Root/Child\" 形式の名前パス（文字列）で指定してください。");

        var text     = el.GetString()!.Trim();
        var resolver = Panels.ActorRefJump.ActorDfsIdByPath;
        if (resolver?.Invoke(text) is int found) return (true, found, null);

        // 数字だけの文字列は、名前で見つからなければ DFS ID として読む（"12" のように文字列で渡されたとき）
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
            return (true, numeric, null);

        return (true, null, resolver is null
            ? "Hierarchy パネルへ接続されていません（エディタ初期化前の可能性）。"
            : $"'{name}' のアクタ '{text}' が見つかりません（seed_hierarchy / seed_find_actor で確かめてください）。");
    }
}
