// ============================================================
//  ApiReferenceCompactor.cs — スクリプト API リファレンス（Markdown）を AI 注入用に圧縮する
//
//  【役割】
//  docs/scripting_api.md から「見出し・コードブロック・表・重要注記」だけを残し、
//  説明の散文を落とす。API シグネチャの情報密度はコードブロックと表にほぼ集約されて
//  いるため、これで文字数を約半分にしても AI へ渡す情報はほとんど減らない。
//
//  【規則の正典】
//  抽出の規則はここが唯一の正典（以前は ScriptApiReference.Compact() にあった）。
//  .claude/skills/add-script-api の「抽出器の制約」はこの規則を前提に書かれている。
//  規則を変えたら Skill と docs/editor_inline_completion.md も合わせて直す。
//
//  【WPF 非依存】
//  editor/tests/InlineCompletionTests が Reference フォルダを丸ごとリンクして使うので、
//  WPF 型・EditorLog などエディタ本体のシングルトンへは依存しない。
// ============================================================

using System;
using System.Text;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// スクリプト API リファレンスの Markdown を「見出し＋コードブロック＋表＋重要注記」へ圧縮する。
/// 出力の改行は LF（"\n"）にそろえる（予算の文字数を CRLF の分だけ無駄にしないため）。
/// </summary>
public static class ApiReferenceCompactor
{
    /// <summary>コードフェンスの印（この文字列で始まる行がフェンスの開始/終了）。</summary>
    public const string CodeFence = "```";

    /// <summary>見出し行の印（# 〜 ####）。</summary>
    public const char HeadingMark = '#';

    /// <summary>表の行の印。</summary>
    public const char TableMark = '|';

    /// <summary>引用（注記）の行の印。</summary>
    public const char QuoteMark = '>';

    /// <summary>引用のうち残すものの目印（Unity ではない・SEED. 修飾必須など誤補完を防ぐ注記）。</summary>
    public const string ImportantNoteKeyword = "重要";

    /// <summary>
    /// この語を含む見出し以降は利用者向けではない（メンテナ向けの手順・内部仕様）ので丸ごと捨てる。
    /// 利用者向けの API はこの見出しより前に書く規約（add-script-api Skill §4-1）。
    /// </summary>
    public const string MaintainerSectionKeyword = "メンテナ向け";

    /// <summary>出力の改行。</summary>
    public const char NewLine = '\n';

    /// <summary>入力から取り除く改行の付属文字（CRLF の CR）。</summary>
    private const char CarriageReturn = '\r';

    /// <summary>圧縮後のおおよその縮み具合（StringBuilder の初期容量の見積もりに使う分母）。</summary>
    private const int ExpectedShrinkRatio = 2;

    /// <summary>
    /// Markdown から「見出し・コードブロック・表・重要注記」だけを抽出して圧縮する。
    ///
    /// 残すもの:
    ///  - 見出し行（# 〜 ####。文脈の区切りとして必要）
    ///  - コードフェンス内の全行（API シグネチャの本体。フェンス行そのものも残す）
    ///  - 表の行（| 区切り。コンポーネント一覧・ライフサイクル表など）
    ///  - 「重要」を含む引用行（Unity ではない・SEED. 修飾必須などの誤補完防止情報）
    /// 落とすもの: 説明の散文・補足リスト・空行（コードの中の空行は残す）・メンテナ向けの節以降。
    /// </summary>
    /// <param name="markdown">リファレンスの Markdown 全文。</param>
    /// <returns>圧縮後のテキスト（各行は LF で終わる）。</returns>
    public static string Compact(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var sb = new StringBuilder(markdown.Length / ExpectedShrinkRatio);
        bool inCode = false;

        foreach (var rawLine in markdown.Split(NewLine))
        {
            var line = rawLine.TrimEnd(CarriageReturn);
            var trimmed = line.TrimStart();

            // コードフェンスの開始/終了（フェンス内は全行残す）
            if (trimmed.StartsWith(CodeFence, StringComparison.Ordinal))
            {
                inCode = !inCode;
                sb.Append(line).Append(NewLine);
                continue;
            }
            if (inCode) { sb.Append(line).Append(NewLine); continue; }

            // 見出し（メンテナ向けセクション以降は不要なので打ち切る）
            if (trimmed.Length > 0 && trimmed[0] == HeadingMark)
            {
                if (trimmed.Contains(MaintainerSectionKeyword, StringComparison.Ordinal)) break;
                sb.Append(line).Append(NewLine);
                continue;
            }

            // 表の行（API 一覧表・ライフサイクル表）
            if (trimmed.Length > 0 && trimmed[0] == TableMark) { sb.Append(line).Append(NewLine); continue; }

            // 「重要」を含む引用注記（Unity ではない等、誤補完防止に必須の情報）
            if (trimmed.Length > 0 && trimmed[0] == QuoteMark
                && trimmed.Contains(ImportantNoteKeyword, StringComparison.Ordinal))
            {
                sb.Append(line).Append(NewLine);
            }
        }
        return sb.ToString();
    }
}
