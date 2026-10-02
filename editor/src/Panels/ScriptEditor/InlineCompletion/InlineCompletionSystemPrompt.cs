using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// インライン補完のシステムプロンプトの組み立て（提供元に共通。今は Groq だけが使う）。
///
/// 基本方針（SEED は Unity ではない・カーソル位置へ挿入するコードだけを出す）に、
/// 編集中のファイルの文脈で選んだ API リファレンスの節（<see cref="ScriptApiReference.LoadFor"/>）を続ける。
/// 注入した節の名前と文字数をログへ出す（[インライン補完] 注入: §… §…（N 文字））。
/// 同じ選択が続くときは短い行にする（補完は打鍵ごとに走り、同じ一覧が並ぶとログが読めないため）。
/// </summary>
public static class InlineCompletionSystemPrompt
{
    /// <summary>補完エンジンとしての役割を固定するシステムプロンプト（基本方針）。</summary>
    public const string Base =
        "You are an inline code completion engine for the C# scripting of a custom game engine called SEED (NOT Unity). " +
        "Given the code before and after the cursor, output ONLY the raw code to insert at the cursor to continue it. " +
        "Do not repeat the existing code. Do not use the UnityEngine namespace or MonoBehaviour. " +
        "Use ONLY the SEED engine API described in the reference below, plus the .NET base class library. " +
        "Do not add explanations or markdown code fences.";

    /// <summary>リファレンスの前に置く区切りの見出し。</summary>
    public const string ReferenceHeader =
        "\n\n=== SEED Script API reference (authoritative; the only engine API you may use) ===\n";

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[インライン補完]";

    /// <summary>前回ログへ出した注入の要約（同じなら短い行にする）。</summary>
    private static string? _lastInjection;

    /// <summary>
    /// システムプロンプトを組み立てる（基本方針 ＋ 文脈で選んだリファレンス）。
    /// </summary>
    /// <param name="prefix">カーソルより前の全テキスト。</param>
    /// <param name="suffix">カーソルより後の全テキスト。</param>
    /// <returns>システムプロンプト。</returns>
    public static string Build(string prefix, string suffix)
    {
        // 編集中のファイル＝前後をつないだ全文、カーソル＝前の長さ
        var selection = ScriptApiReference.LoadFor(prefix + suffix, prefix.Length);
        LogInjection(selection);
        return selection.Length == 0 ? Base : Base + ReferenceHeader + selection.Text;
    }

    /// <summary>注入した節の名前と文字数をログへ出す（前回と同じなら短く）。</summary>
    /// <param name="selection">選択。</param>
    private static void LogInjection(ApiReferenceSelection selection)
    {
        string summary = selection.Length == 0
            ? "なし（リファレンスが見つからないか予算が 0）"
            : $"{selection.DescribeLabels()}（{selection.Length} 文字）";
        if (selection.AlwaysIncludedOverflow)
            summary += $"（予算 {selection.BudgetChars} 文字が常に入れる節より小さいため一部を省いた）";

        if (summary == _lastInjection)
        {
            SEEDEditor.EditorLog.Write($"{LogPrefix} 注入: 前回と同じ（{selection.Labels.Count} 節・{selection.Length} 文字）");
            return;
        }
        _lastInjection = summary;
        SEEDEditor.EditorLog.Write($"{LogPrefix} 注入: {summary}");
    }
}
