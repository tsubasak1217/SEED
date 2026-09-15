// ============================================================
//  EngineVersionCheck.cs — .seedproj の engine_version とエディタ版の比較（純粋ロジック）
//
//  【役割】
//  「このプロジェクトを作った（あるいは最後に engine_version を更新した）エンジンの版」と
//  「いま実行しているエディタの版」を数値として比較し、Same / ProjectOlder / ProjectNewer /
//  Unknown のどれかに分類する。チーム制作でエディタ（エンジン）を更新しながらゲームを作る
//  運用の下で、「古いエディタで新しいプロジェクトを開いて壊してしまう」事故を未然に知らせる
//  ための判定部分だけをここに閉じ込める。
//
//  【比較の規則】
//  ・"major.minor.patch[.build...][-prerelease][+metadata]" 形式を受け付ける。
//    ドット区切りの要素数は 3 に固定しない（"0.1.0.0" のような 4 要素以上も可）。
//  ・比較前に、まず "+" 以降のビルドメタデータを、次に "-" 以降のプレリリース識別子を
//    それぞれ切り落とす（SemVer の並び "major.minor.patch-prerelease+metadata" を前提にする）。
//  ・残った各要素は非負整数でなければならない。1 つでも解釈できない・空になったら
//    そのバージョン全体を「解釈不能」として扱う。
//  ・要素数が異なる場合は短い方を 0 で補って比較する（"1.2" と "1.2.0" は同値）。
//  ・入力のどちらかが空文字・null・解釈不能なら判定は Unknown
//    （.seedproj に engine_version が無い＝旧プロジェクトを想定した扱い）。
//
//  【呼び出し側との責務分担】
//  このクラスは比較結果（列挙値）と、ダイアログ・ログの文言を組み立てるための
//  「材料」（元の文字列・正規化した文字列）を返すだけで、ダイアログ表示や .seedproj の
//  保存は一切行わない（WPF・ファイル I/O に一切依存しない。単体テストからそのまま
//  リンクして使える）。実際の通知・更新は EngineVersionGate.cs が担う。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Project;

/// <summary>
/// プロジェクトの engine_version と、実行中エディタの版を比べた結果の分類。
/// </summary>
public enum EngineVersionComparison
{
    /// <summary>一致（正規化した数値表現がすべて同じ）。</summary>
    Same,

    /// <summary>プロジェクトの方が古い版で作られた（＝エディタの方が新しい）。</summary>
    ProjectOlder,

    /// <summary>プロジェクトの方が新しい版で作られた（＝エディタの方が古い）。</summary>
    ProjectNewer,

    /// <summary>どちらか（または両方）が解釈できない、または空。比較不能。</summary>
    Unknown,
}

/// <summary>
/// <see cref="EngineVersionCheck.Compare"/> の結果。
/// 判定そのものに加えて、ダイアログ・ログの文言を組み立てるための元データを持つ。
/// </summary>
/// <param name="Comparison">比較結果の分類。</param>
/// <param name="ProjectVersionRaw">.seedproj の engine_version をそのまま渡した値（null なら空文字）。</param>
/// <param name="EditorVersionRaw">比較に使ったエディタのバージョン文字列をそのまま渡した値（null なら空文字）。</param>
/// <param name="ProjectVersionNormalized">
/// プロジェクト側を数値比較用に正規化した文字列（例 "0.1.0"）。解釈できなければ空文字。
/// </param>
/// <param name="EditorVersionNormalized">
/// エディタ側を数値比較用に正規化した文字列。解釈できなければ空文字。
/// </param>
public readonly record struct EngineVersionCheckResult(
    EngineVersionComparison Comparison,
    string ProjectVersionRaw,
    string EditorVersionRaw,
    string ProjectVersionNormalized,
    string EditorVersionNormalized);

/// <summary>
/// engine_version の数値比較を行う純粋な判定ロジック。
/// WPF・ファイル I/O に一切依存しないため、editor/tests/ProjectSystemTests から
/// そのままリンクして検証できる。
/// </summary>
public static class EngineVersionCheck
{
    // ── 区切り文字（マジック文字列にしない） ──────────────────

    /// <summary>バージョン要素（メジャー・マイナー・パッチ...）の区切り。</summary>
    private const char SEGMENT_SEPARATOR = '.';

    /// <summary>プレリリース識別子の開始位置（例 "1.0.0-beta"）。この文字以降を比較前に落とす。</summary>
    private const char PRERELEASE_SEPARATOR = '-';

    /// <summary>ビルドメタデータの開始位置（例 "1.0.0+abcdef"）。この文字以降を比較前に落とす。</summary>
    private const char METADATA_SEPARATOR = '+';

    /// <summary>
    /// プロジェクトの engine_version とエディタのバージョンを比較する。
    /// </summary>
    /// <param name="projectEngineVersion">.seedproj の engine_version（null・空可）。</param>
    /// <param name="editorVersion">実行中エディタのバージョン（通常は <see cref="EditorVersion.Current"/>）。</param>
    /// <returns>比較結果と、文言組み立て用の元データ。</returns>
    public static EngineVersionCheckResult Compare(string? projectEngineVersion, string? editorVersion)
    {
        var projectParsed = TryNormalize(projectEngineVersion, out var projectSegments, out var projectNormalized);
        var editorParsed  = TryNormalize(editorVersion,        out var editorSegments,  out var editorNormalized);

        EngineVersionComparison comparison;
        if (!projectParsed || !editorParsed)
        {
            // どちらかが解釈できない（旧プロジェクトで engine_version が空、
            // または不正な文字列が書き込まれている）場合は比較そのものを諦める。
            comparison = EngineVersionComparison.Unknown;
        }
        else
        {
            var cmp = CompareSegments(projectSegments!, editorSegments!);
            comparison = cmp < 0 ? EngineVersionComparison.ProjectOlder
                       : cmp > 0 ? EngineVersionComparison.ProjectNewer
                       : EngineVersionComparison.Same;
        }

        return new EngineVersionCheckResult(
            comparison,
            projectEngineVersion ?? string.Empty,
            editorVersion ?? string.Empty,
            projectParsed ? projectNormalized! : string.Empty,
            editorParsed  ? editorNormalized!  : string.Empty);
    }

    /// <summary>
    /// バージョン文字列を「非負整数の並び」に正規化する。
    /// ビルドメタデータ・プレリリース識別子を切り落とし、"." 区切りの各要素を int として解釈する。
    /// </summary>
    /// <param name="raw">元の文字列（null・空・前後の空白を許容）。</param>
    /// <param name="segments">解釈できた場合の要素配列。失敗時は null。</param>
    /// <param name="normalized">解釈できた場合の正規化済み文字列（"." 連結）。失敗時は null。</param>
    /// <returns>解釈できたら true。</returns>
    private static bool TryNormalize(string? raw, out int[]? segments, out string? normalized)
    {
        segments   = null;
        normalized = null;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var core = raw.Trim();

        // ビルドメタデータ ("+" 以降) を先に落とす。SemVer では末尾に付くため、
        // プレリリース識別子より先に切り落として問題ない。
        var metadataAt = core.IndexOf(METADATA_SEPARATOR);
        if (metadataAt >= 0) core = core[..metadataAt];

        // 続けてプレリリース識別子 ("-" 以降) を落とす。
        var prereleaseAt = core.IndexOf(PRERELEASE_SEPARATOR);
        if (prereleaseAt >= 0) core = core[..prereleaseAt];

        if (core.Length == 0) return false;

        var parts   = core.Split(SEGMENT_SEPARATOR);
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            // NumberStyles.None: 符号・前後空白・桁区切りを一切許さず、数字だけを受け付ける。
            // "1.+2.3" や "1. 2.3" のような壊れた表記を確実に Unknown 側へ倒すため。
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;
            numbers[i] = n;
        }

        segments   = numbers;
        normalized = string.Join(SEGMENT_SEPARATOR, numbers);
        return true;
    }

    /// <summary>
    /// 正規化済みの要素配列を比較する。要素数が異なる場合は短い方を 0 で補う
    /// （"1.2" と "1.2.0" を同値にするため）。
    /// </summary>
    /// <param name="project">プロジェクト側の要素配列。</param>
    /// <param name="editor">エディタ側の要素配列。</param>
    /// <returns>project &lt; editor なら負、等しければ 0、project &gt; editor なら正。</returns>
    private static int CompareSegments(int[] project, int[] editor)
    {
        var length = Math.Max(project.Length, editor.Length);
        for (var i = 0; i < length; i++)
        {
            var p = i < project.Length ? project[i] : 0;
            var e = i < editor.Length  ? editor[i]  : 0;
            if (p != e) return p.CompareTo(e);
        }
        return 0;
    }
}
