using System;
using System.IO;

namespace SEEDEditor.Assets;

/// <summary>
/// アセットルート（runtime/assets）の可用性の判定結果。
/// </summary>
public enum AssetsRootStatus
{
    /// <summary>
    /// パスが空・不正でそもそも判定できない。
    /// 既定値（=0）を「利用不可」にしておくことで、未判定の状態を誤って
    /// 「利用可能」と扱ってしまう事故を防ぐ。
    /// </summary>
    Invalid,

    /// <summary>利用可能（実際に列挙できた）。</summary>
    Ok,

    /// <summary>フォルダが存在しない。</summary>
    Missing,

    /// <summary>
    /// ジャンクション／シンボリックリンクは存在するが、リンク先へ到達できない。
    /// 未接続ドライブ（D: を外した状態）・切断されたネットワークパスがこれに当たる。
    /// </summary>
    BrokenLink,

    /// <summary>存在するが権限が無くて列挙できない。</summary>
    AccessDenied,
}

/// <summary>
/// <see cref="AssetsRootProbe.Check(string)"/> の結果。
/// 状態・対象パス・（リンクなら）リンク先・人間向けの理由文を 1 つにまとめて持ち回る。
/// </summary>
/// <param name="Status">判定結果。</param>
/// <param name="Path">判定対象の絶対パス（正規化に失敗した場合は入力そのまま）。</param>
/// <param name="LinkTarget">対象がリンクだった場合のリンク先。リンクでない／取得不能なら null。</param>
/// <param name="Reason">UI とログに出す 1 行の理由文（日本語）。</param>
/// <param name="Detail">例外メッセージなどの補助情報。無ければ null。</param>
public readonly record struct AssetsRootProbeResult(
    AssetsRootStatus Status,
    string           Path,
    string?          LinkTarget,
    string           Reason,
    string?          Detail)
{
    /// <summary>アセットフォルダを実際に読める状態か。</summary>
    public bool IsAvailable => Status == AssetsRootStatus.Ok;

    /// <summary>EditorLog へ 1 行で書くための整形文字列。</summary>
    public string LogLine
        => $"{Status} path={Path}"
         + (LinkTarget is null ? "" : $" link->{LinkTarget}")
         + $" reason={Reason}"
         + (Detail is null ? "" : $" detail={Detail}");
}

/// <summary>
/// アセットルートが「本当に使えるか」を 1 箇所で判定するユーティリティ。
///
/// 【なぜ Directory.Exists では不十分か】
/// runtime/assets を別ドライブへのジャンクションにしている構成では、リンク先ドライブが
/// 未接続でも <see cref="Directory.Exists(string)"/> は true を返す（親フォルダの
/// ディレクトリエントリとしてリンク自身が存在するため）。実際に開こうとした瞬間に
/// <see cref="DirectoryNotFoundException"/> が飛び、Exists を信じたコードが落ちる。
///
/// そこで本クラスは「最上位 1 階層を 1 件だけ実際に列挙してみる」ことで可用性を判定する。
/// 判定コストは FindFirstFile 1 回分で、再帰は一切しない（起動時間に影響しない）。
///
/// ファイル監視・シーン自動オープン・ランタイム起動など、アセットに依存する初期化は
/// すべてこの判定を参照すること（各所で Exists を書かない）。
/// 本クラスは WPF に一切依存しない（単体テストからそのままリンクして使える）。
/// </summary>
public static class AssetsRootProbe
{
    /// <summary>
    /// アセットルートの可用性を判定する。例外は投げない。
    /// </summary>
    /// <param name="path">アセットルートの絶対パス。</param>
    /// <returns>判定結果。</returns>
    public static AssetsRootProbeResult Check(string? path)
    {
        // ── 入力の正規化 ──────────────────────────────────────
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AssetsRootProbeResult(
                AssetsRootStatus.Invalid, path ?? "", null,
                "アセットフォルダのパスが設定されていません。", null);
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new AssetsRootProbeResult(
                AssetsRootStatus.Invalid, path, null,
                "アセットフォルダのパスが不正です。", ex.Message);
        }

        // リンク（ジャンクション/シンボリックリンク）かどうかを先に調べる。
        // 失敗の原因が「フォルダが無い」なのか「リンク先へ到達できない」なのかを
        // 区別するために必要で、リンク先へのアクセスは行わない（壊れていても取得できる）。
        string? linkTarget = TryGetLinkTarget(full, out bool isLink);

        // ── 実際に 1 件だけ列挙してみる（これが唯一の可用性判定）──
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories     = false,   // 最上位 1 階層のみ（起動時間を変えない）
                IgnoreInaccessible        = false,   // 権限エラーを握りつぶさない（AccessDenied を出す）
                ReturnSpecialDirectories  = false,
                AttributesToSkip          = 0,
            };
            // GetEnumerator() の時点でディレクトリハンドルが開かれるため、
            // 壊れたジャンクションはここで例外になる（MoveNext まで到達しない場合もある）。
            using var e = new DirectoryInfo(full)
                .EnumerateFileSystemInfos("*", options)
                .GetEnumerator();
            e.MoveNext();   // 空フォルダなら false が返るだけで、これは正常
            return new AssetsRootProbeResult(
                AssetsRootStatus.Ok, full, isLink ? linkTarget : null, "利用可能", null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new AssetsRootProbeResult(
                AssetsRootStatus.AccessDenied, full, isLink ? linkTarget : null,
                "アセットフォルダを読み取る権限がありません。", ex.Message);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or IOException)
        {
            // リンクなら「リンク先に到達できない」（未接続ドライブ等）、
            // リンクでないなら単純に「フォルダが無い」。
            return isLink
                ? new AssetsRootProbeResult(
                      AssetsRootStatus.BrokenLink, full, linkTarget,
                      linkTarget is null
                          ? "リンク先に到達できません（ドライブ未接続の可能性があります）。"
                          : $"リンク先「{linkTarget}」に到達できません（ドライブ未接続の可能性があります）。",
                      ex.Message)
                : new AssetsRootProbeResult(
                      AssetsRootStatus.Missing, full, null,
                      "アセットフォルダが存在しません。", ex.Message);
        }
        catch (Exception ex)
        {
            // 想定外（ArgumentException など）。起動を止めないため Missing 扱いにする。
            return new AssetsRootProbeResult(
                AssetsRootStatus.Missing, full, isLink ? linkTarget : null,
                "アセットフォルダを開けませんでした。", ex.Message);
        }
    }

    /// <summary>
    /// パスがリンク（ジャンクション/シンボリックリンク）かどうかを、リンク先へアクセスせずに調べる。
    /// </summary>
    /// <param name="fullPath">正規化済み絶対パス。</param>
    /// <param name="isLink">リンクなら true。</param>
    /// <returns>リンク先のパス。リンクでない／リンク先を読み取れない場合は null。</returns>
    private static string? TryGetLinkTarget(string fullPath, out bool isLink)
    {
        // 第 1 手: リパースポイントのデータを直接読む（リンク先が消えていても読める）。
        try
        {
            var target = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: false);
            if (target is not null)
            {
                isLink = true;
                return target.FullName;
            }
        }
        catch
        {
            // ResolveLinkTarget はパス自体が無いと例外を投げる。第 2 手へ落とす。
        }

        // 第 2 手: 親フォルダを名前指定で列挙し、ReparsePoint 属性の有無だけを見る。
        // 列挙で得た属性は FindFirstFile の結果のキャッシュなので、リンク先を開かない。
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            var name   = Path.GetFileName(fullPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(name))
            {
                foreach (var d in new DirectoryInfo(parent).EnumerateDirectories(name))
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        isLink = true;
                        return null;   // リンクではあるが、リンク先は特定できない
                    }
                }
            }
        }
        catch
        {
            // 親フォルダも読めない場合は判定不能。リンクでない扱いにする。
        }

        isLink = false;
        return null;
    }
}
