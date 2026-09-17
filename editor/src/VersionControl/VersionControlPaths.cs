// ============================================================
//  VersionControlPaths.cs — 絶対パスとリポジトリ相対パスの相互変換
//
//  【役割】
//  エディタ側は絶対パスで物を扱うが、Lore へ渡すのはリポジトリルートからの
//  相対パスでなければならない。変換をここ 1 か所に閉じ、
//  「どちらのパスが流れているか分からない」状態を作らない。
//
//  【区切り文字について】
//  Lore の内部表現はスラッシュ区切り（`assets/common/foo.png`）。
//  Windows の絶対パスから作ると円記号になるので、必ずスラッシュへ正規化する。
//
//  【作業コピーの外を弾く理由】
//  プロジェクトフォルダの外のファイル（テンプレート・エンジン側のアセット）を
//  誤って Lore へ渡すと、Lore が「リポジトリ外」のエラーを出すか、
//  最悪リポジトリルートを跨いだ相対パス（`../..`）を作ってしまう。
//  境界で弾いて、上の層には「対象外」と返す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SEEDEditor.VersionControl;

/// <summary>
/// バージョン管理で使うパスの変換。
/// </summary>
public static class VersionControlPaths
{
    /// <summary>Lore が使うパス区切り文字。</summary>
    public const char LORE_PATH_SEPARATOR = '/';

    /// <summary>
    /// 絶対パスをリポジトリルートからの相対パスへ変換する。
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（絶対パス）。</param>
    /// <param name="absolutePath">変換したい絶対パス。</param>
    /// <returns>
    /// スラッシュ区切りの相対パス。作業コピーの外だった場合や
    /// パスが空だった場合は null。
    /// </returns>
    public static string? ToRepositoryRelative(string workingCopyRoot, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyRoot)) return null;
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        string fullRoot;
        string fullPath;
        try
        {
            // 相対指定・`..` を含む指定・大文字小文字のゆれを吸収するため
            // 両方を正規化してから比較する。
            fullRoot = Path.GetFullPath(workingCopyRoot);
            fullPath = Path.GetFullPath(absolutePath);
        }
        catch (Exception)
        {
            // 不正な文字を含むパスなど。呼び出し側には「対象外」として返す。
            return null;
        }

        var relative = Path.GetRelativePath(fullRoot, fullPath);

        // GetRelativePath は作業コピーの外だと `..\...` か絶対パスを返す。
        // どちらも Lore へ渡してはいけない。
        if (relative.StartsWith("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(relative)) return null;

        // ルート自身を指していた場合（"."）はそのまま返す。
        return NormalizeSeparators(relative);
    }

    /// <summary>
    /// 複数の絶対パスをまとめて相対パスへ変換する。作業コピーの外は取り除く。
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（絶対パス）。</param>
    /// <param name="absolutePaths">変換したい絶対パス。</param>
    /// <param name="skipped">変換できなかった（作業コピー外の）パス。</param>
    /// <returns>変換できた相対パス（重複は除く。順序は入力順）。</returns>
    public static IReadOnlyList<string> ToRepositoryRelative(
        string workingCopyRoot,
        IReadOnlyList<string>? absolutePaths,
        out IReadOnlyList<string> skipped)
    {
        var converted   = new List<string>();
        var skippedList = new List<string>();
        var seen        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (absolutePaths is not null)
        {
            foreach (var absolute in absolutePaths)
            {
                var relative = ToRepositoryRelative(workingCopyRoot, absolute);
                if (relative is null)
                {
                    skippedList.Add(absolute ?? string.Empty);
                    continue;
                }
                // 同じファイルを 2 回渡されても Lore へは 1 回だけ伝える。
                if (seen.Add(relative)) converted.Add(relative);
            }
        }

        skipped = skippedList;
        return converted;
    }

    /// <summary>
    /// リポジトリ相対パスを絶対パスへ戻す。
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（絶対パス）。</param>
    /// <param name="relativePath">スラッシュ区切りの相対パス。</param>
    /// <returns>絶対パス。引数が空なら null。</returns>
    public static string? ToAbsolute(string workingCopyRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyRoot)) return null;
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        try
        {
            return Path.GetFullPath(Path.Combine(
                workingCopyRoot,
                relativePath.Replace(LORE_PATH_SEPARATOR, Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 区切り文字を Lore の表現（スラッシュ）へ揃える。
    /// </summary>
    /// <param name="path">相対パス。</param>
    public static string NormalizeSeparators(string path)
        => string.IsNullOrEmpty(path)
            ? string.Empty
            : path.Replace(Path.DirectorySeparatorChar, LORE_PATH_SEPARATOR)
                  .Replace(Path.AltDirectorySeparatorChar, LORE_PATH_SEPARATOR);

    /// <summary>
    /// 指定フォルダが Lore の作業コピーか（`.lore/` があるか）を判定する。
    /// </summary>
    /// <param name="rootDir">判定するフォルダ（プロジェクトルート）。</param>
    public static bool IsLoreWorkingCopy(string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir)) return false;
        try
        {
            return Directory.Exists(Path.Combine(
                rootDir, VersionControlSettings.LORE_METADATA_DIR_NAME));
        }
        catch (Exception)
        {
            // アクセス権が無い等。バージョン管理下ではない扱いにする。
            return false;
        }
    }

    /// <summary>
    /// 作業コピーのリポジトリ ID（`.lore/id`）を読み、API へ渡す形で返す。
    ///
    /// <para>
    /// SEED アカウントの窓口は、権限をリポジトリ ID 単位で持つ
    /// （docs/seed_accounts.md 2・3 章）。オーナー登録・招待コード発行・
    /// 参加者一覧のすべてでこの値が要る。
    /// </para>
    /// <para>
    /// ★`.lore/id` は **生の 16 バイト**であってテキストではない。
    /// そのまま文字列として読むと、制御文字混じりの化けた値になる。
    /// API へ渡すのは **32 桁の 16 進小文字**なので、ここで必ず変換する。
    /// </para>
    /// <para>
    /// 長さが違うファイルは「読めなかった」として空文字を返す。
    /// 中途半端に変換した ID を返すと、サーバ側の権限と一致せず
    /// 「オーナーなのにオーナーとして扱われない」という分かりにくい失敗になる。
    /// 読めなくても例外は投げない（バージョン管理そのものは動くため）。
    /// </para>
    /// </summary>
    /// <param name="rootDir">作業コピーのルート。</param>
    /// <returns>32 桁の 16 進小文字（読めなければ空文字）。</returns>
    public static string ReadRepositoryId(string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir)) return string.Empty;

        try
        {
            var path = Path.Combine(
                rootDir,
                VersionControlSettings.LORE_METADATA_DIR_NAME,
                VersionControlSettings.LORE_ID_FILE_NAME);

            if (!File.Exists(path)) return string.Empty;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != VersionControlSettings.REPOSITORY_ID_BYTE_LENGTH)
                return string.Empty;

            return ToLowerHex(bytes);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// バイト列を 16 進小文字の文字列にする。
    /// </summary>
    /// <param name="bytes">変換するバイト列。</param>
    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
