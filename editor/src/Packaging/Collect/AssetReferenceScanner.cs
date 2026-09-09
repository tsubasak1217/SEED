// ============================================================
//  AssetReferenceScanner.cs — 1 ファイルからのアセット参照抽出
//
//  【役割】
//  テキスト系アセット（.scene / .actor / .json / .cs / .gltf …）1 本を読み、
//  その中に書かれている「他のアセットへの参照」を候補として取り出す。
//  実在チェックや閉包（グラフ探索）は AssetCollector の仕事で、ここでは行わない。
//
//  【抽出する 4 系統】（ランタイム asset_fs::normalize_asset_path と対応）
//   1. assets:// 形式          … 仮想パス。確実な参照。
//   2. アセットルートの絶対パス … エディタが保存した生パス。4 種の表記ゆれがある。
//   3. 参照元からの相対パス     … glTF の "uri"（.bin / テクスチャ）など。
//   4. アセットルート相対パス   … layers.json の "mainGame/terrain/brush/leaf2.png" など。
//
//  【確実な参照 / 推測の参照】
//  1・2・glTF uri は「確実」（IsExplicit=true）。実体が無ければ欠落として報告する。
//  3・4 の一般文字列は「推測」。実在するときだけ採用し、外れても警告は出さない
//  （数値・識別子など、たまたまドットを含む文字列を誤検出しても実害を出さないため）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SEEDEditor.Packaging.Collect;

/// <summary>
/// 抽出された参照候補 1 件。
/// </summary>
/// <param name="Raw">元テキストに現れていた文字列（ログ用）。</param>
/// <param name="Candidates">
/// アセットルート相対パスの候補。先頭から順に実在を試す。
/// 相対参照では「参照元フォルダ基準」「アセットルート基準」の 2 つが入る。
/// </param>
/// <param name="IsExplicit">
/// true なら確実な参照（実体が無ければ欠落として報告する）。
/// false なら推測（実在しなければ黙って捨てる）。
/// </param>
public readonly record struct AssetReferenceCandidate(
    string Raw,
    IReadOnlyList<string> Candidates,
    bool IsExplicit);

/// <summary>テキストアセットからアセット参照を抽出する。状態を持たない純粋な処理。</summary>
public static class AssetReferenceScanner
{
    /// <summary>参照文字列の終端とみなす文字（仕様: ダブルクォート / シングルクォート / 空白 / 山括弧 / 閉じ丸括弧）。</summary>
    private static readonly char[] Terminators =
        ['"', '\'', ' ', '\t', '\r', '\n', '<', '>', ')'];

    /// <summary>参照候補として受け付ける文字列の最大長。これを超える文字列はパスとみなさない。</summary>
    private const int MaxReferenceLength = 512;

    /// <summary>空白区切りのトークンも走査する拡張子（OBJ のマテリアル定義は引用符を使わない）。</summary>
    private static readonly HashSet<string> WhitespaceTokenExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mtl" };

    /// <summary>glTF の "uri" フィールドを拾う正規表現（JSON エスケープを含む文字列に対応）。</summary>
    private static readonly Regex GltfUriRegex =
        new("\"uri\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    // ============================================================
    //  公開 API
    // ============================================================

    /// <summary>
    /// テキスト 1 本から参照候補を抽出する。
    /// </summary>
    /// <param name="text">ファイルの中身（UTF-8 として読んだ文字列）。</param>
    /// <param name="sourceRelPath">参照元ファイルのアセットルート相対パス（相対参照の基準になる）。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス（絶対パス表記の除去に使う）。</param>
    /// <returns>重複を除いた参照候補の一覧。</returns>
    public static IReadOnlyList<AssetReferenceCandidate> Scan(
        string text, string sourceRelPath, string assetsRoot)
    {
        var result = new List<AssetReferenceCandidate>();
        // 同じ参照が何百回も出てくる（.scene の同一プレハブ参照など）ので、
        // 生文字列単位で重複を落としてから返す。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceDir = AssetPathUtil.GetDirectory(sourceRelPath);
        var sourceExt = AssetPathUtil.GetExtensionLower(sourceRelPath);

        // ── 1. assets:// 形式 ──────────────────────────────────
        CollectVirtualPaths(text, seen, result);

        // ── 2. アセットルートの絶対パス（4 種の表記） ──────────
        CollectAbsolutePaths(text, assetsRoot, seen, result);

        // ── 3. glTF の uri（.bin / テクスチャ。参照元からの相対） ─
        if (sourceExt == ".gltf") CollectGltfUris(text, sourceDir, seen, result);

        // ── 4. 一般文字列（ルート相対 / 参照元相対の推測） ──────
        CollectQuotedTokens(text, sourceDir, seen, result);
        if (WhitespaceTokenExtensions.Contains(sourceExt))
            CollectWhitespaceTokens(text, sourceDir, seen, result);

        return result;
    }

    // ============================================================
    //  1. assets:// 形式
    // ============================================================

    /// <summary>"assets://..." 形式の仮想パスをすべて拾う。</summary>
    private static void CollectVirtualPaths(
        string text, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        int from = 0;
        while (true)
        {
            int i = text.IndexOf(AssetPathUtil.AssetsScheme, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;

            int start = i + AssetPathUtil.AssetsScheme.Length;
            int end   = FindTerminator(text, start);
            var raw   = text[i..end];
            from      = end;

            // 仮想パス部分（スキームを除いた相対パス）を正規化する
            var rel = AssetPathUtil.NormalizeRelative(UnescapePath(text[start..end]));
            if (rel.Length == 0 || rel.Length > MaxReferenceLength) continue;
            if (!seen.Add(raw)) continue;

            result.Add(new AssetReferenceCandidate(raw, [rel], IsExplicit: true));
        }
    }

    // ============================================================
    //  2. アセットルートの絶対パス
    // ============================================================

    /// <summary>
    /// アセットルートの絶対パス表記を 4 種すべて拾う。
    ///
    /// 表記ゆれの内訳（PackagingWindow の従来の書き換えと同じ 4 形式）:
    ///   (1) スラッシュ区切り                     C:/proj/assets/
    ///   (2) バックスラッシュ区切り               C:\proj\assets\
    ///   (3) JSON エスケープされたバックスラッシュ C:\\proj\\assets\\
    ///   (4) JSON エスケープされたスラッシュ       C:\/proj\/assets\/
    /// </summary>
    private static void CollectAbsolutePaths(
        string text, string assetsRoot, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        var rootSlash = assetsRoot.Replace('\\', '/').TrimEnd('/');
        var rootBack  = rootSlash.Replace('/', '\\');

        // 長い（エスケープされた）表記から先に走査する。
        // 短い表記が長い表記の一部として二重に拾われるのを避けるため。
        string[] prefixes =
        [
            rootBack.Replace("\\", "\\\\") + "\\\\",  // (3)
            rootSlash.Replace("/", "\\/")  + "\\/",   // (4)
            rootBack  + "\\",                          // (2)
            rootSlash + "/",                           // (1)
        ];

        foreach (var prefix in prefixes)
        {
            if (prefix.Length == 0) continue;
            int from = 0;
            while (true)
            {
                int i = text.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;

                int start = i + prefix.Length;
                int end   = FindTerminator(text, start);
                var raw   = text[i..end];
                from      = end;

                var rel = AssetPathUtil.NormalizeRelative(UnescapePath(text[start..end]));
                if (rel.Length == 0 || rel.Length > MaxReferenceLength) continue;
                if (!seen.Add(raw)) continue;

                result.Add(new AssetReferenceCandidate(raw, [rel], IsExplicit: true));
            }
        }
    }

    // ============================================================
    //  3. glTF の uri
    // ============================================================

    /// <summary>glTF の "uri" フィールド（.bin / テクスチャ）を参照元からの相対として拾う。</summary>
    private static void CollectGltfUris(
        string text, string sourceDir, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        foreach (Match m in GltfUriRegex.Matches(text))
        {
            var raw = m.Groups[1].Value;
            // データ URI（base64 埋め込み）は外部ファイルではないので無視する
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.Length == 0 || raw.Length > MaxReferenceLength) continue;
            if (!seen.Add("gltf-uri:" + raw)) continue;

            // glTF の uri は URL エンコード（%20 など）されうる
            var decoded = UrlDecode(UnescapeJsonString(raw));
            var rel     = AssetPathUtil.NormalizeRelative(decoded);
            if (rel.Length == 0) continue;

            // uri は glTF ファイルからの相対が正。ルート相対も念のため試す。
            var candidates = BuildRelativeCandidates(sourceDir, rel);
            if (candidates.Count == 0) continue;
            result.Add(new AssetReferenceCandidate(raw, candidates, IsExplicit: true));
        }
    }

    // ============================================================
    //  4. 一般文字列（推測の参照）
    // ============================================================

    /// <summary>
    /// 引用符で囲まれた文字列を走査し、パスらしきものを推測の参照候補として拾う。
    /// JSON / C# の両方に効く（両方とも二重引用符で囲む）。
    /// </summary>
    private static void CollectQuotedTokens(
        string text, string sourceDir, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        int i = 0;
        while (i < text.Length)
        {
            char q = text[i];
            if (q != '"' && q != '\'') { i++; continue; }

            // 閉じ引用符を探す（バックスラッシュエスケープを飛ばす）
            int j = i + 1;
            var sb = new StringBuilder();
            bool closed = false;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\' && j + 1 < text.Length) { sb.Append(c).Append(text[j + 1]); j += 2; continue; }
                if (c == q) { closed = true; break; }
                if (c == '\n' || c == '\r') break;            // 行をまたぐ引用符は対象外
                if (sb.Length > MaxReferenceLength) break;     // 長すぎる = パスではない
                sb.Append(c);
                j++;
            }
            // 閉じられていれば閉じ引用符の次から、閉じられていなければ走査位置を進めて再開する
            i = j + 1;
            if (!closed) continue;

            AddGuessCandidate(sb.ToString(), sourceDir, seen, result);
        }
    }

    /// <summary>空白区切りのトークンを走査する（.mtl の map_Kd など、引用符を使わない形式向け）。</summary>
    private static void CollectWhitespaceTokens(
        string text, string sourceDir, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        foreach (var token in text.Split(Terminators, StringSplitOptions.RemoveEmptyEntries))
            AddGuessCandidate(token, sourceDir, seen, result);
    }

    /// <summary>推測の参照候補として妥当なら結果に足す。</summary>
    private static void AddGuessCandidate(
        string token, string sourceDir, HashSet<string> seen, List<AssetReferenceCandidate> result)
    {
        if (token.Length == 0 || token.Length > MaxReferenceLength) return;
        // スキーム付き（assets:// など）は 1 の経路で拾い済み
        if (token.Contains("://", StringComparison.Ordinal)) return;
        // 絶対パスはアセット外か、2 の経路で拾い済み
        if (IsAbsoluteLike(token)) return;
        // 拡張子が無いものはパスとみなさない（推測なので厳しめに絞る）
        if (AssetPathUtil.GetExtensionLower(token).Length == 0) return;
        // 制御文字を含むものはパスではない
        foreach (var c in token) if (char.IsControl(c)) return;
        if (!seen.Add("guess:" + token)) return;

        var rel = AssetPathUtil.NormalizeRelative(UnescapePath(token));
        if (rel.Length == 0) return;

        var candidates = BuildRelativeCandidates(sourceDir, rel);
        if (candidates.Count == 0) return;
        result.Add(new AssetReferenceCandidate(token, candidates, IsExplicit: false));
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// 相対参照の解決候補を「参照元フォルダ基準 → アセットルート基準」の順で組み立てる。
    /// </summary>
    /// <param name="sourceDir">参照元ファイルの親フォルダ（ルート相対）。</param>
    /// <param name="rel">正規化済みの相対参照。</param>
    /// <returns>試す順に並んだ候補。</returns>
    private static List<string> BuildRelativeCandidates(string sourceDir, string rel)
    {
        var list = new List<string>(2);
        if (sourceDir.Length > 0)
        {
            var joined = AssetPathUtil.CollapseDotSegments(sourceDir + "/" + rel);
            if (joined.Length > 0) list.Add(joined);
        }
        if (!list.Contains(rel, StringComparer.OrdinalIgnoreCase)) list.Add(rel);
        return list;
    }

    /// <summary>指定位置から終端文字が現れるまでの位置を返す。</summary>
    /// <param name="text">走査対象。</param>
    /// <param name="start">走査開始位置。</param>
    /// <returns>終端文字の位置（見つからなければ文字列長）。</returns>
    private static int FindTerminator(string text, int start)
    {
        int end = text.IndexOfAny(Terminators, start);
        return end < 0 ? text.Length : end;
    }

    /// <summary>ドライブレター付き・UNC・ルート開始のパスかを判定する。</summary>
    /// <param name="s">判定する文字列。</param>
    /// <returns>絶対パスらしければ true。</returns>
    private static bool IsAbsoluteLike(string s)
    {
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') return true;
        if (s.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        if (s.StartsWith("//", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// パス中のエスケープを解いて '/' 区切りへ揃える。
    /// JSON のバックスラッシュ 2 個・バックスラッシュ + スラッシュ、および生のバックスラッシュに対応する。
    /// </summary>
    /// <param name="s">エスケープを含みうるパス文字列。</param>
    /// <returns>'/' 区切りに揃えたパス。</returns>
    private static string UnescapePath(string s) =>
        s.Replace("\\\\", "/").Replace("\\/", "/").Replace('\\', '/');

    /// <summary>JSON 文字列のエスケープを最小限だけ解く（uri 用）。</summary>
    /// <param name="s">JSON 文字列の中身。</param>
    /// <returns>エスケープを解いた文字列。</returns>
    private static string UnescapeJsonString(string s) =>
        s.Replace("\\/", "/").Replace("\\\\", "\\");

    /// <summary>URL エンコード（%XX）を解く。glTF の uri は空白などが %20 になる。</summary>
    /// <param name="s">エンコードされうる文字列。</param>
    /// <returns>デコード後の文字列。</returns>
    private static string UrlDecode(string s)
    {
        if (!s.Contains('%', StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length &&
                Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
