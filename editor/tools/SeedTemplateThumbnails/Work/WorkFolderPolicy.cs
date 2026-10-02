// ============================================================
//  WorkFolderPolicy.cs — 作業の置き場（--work）を使ってよいか・中身を消してよいかの判定（純粋な処理）
//
//  【なぜ要るか（docs/reviews/2026-10-02_code_review.md #2）】
//  以前の道具は --work の中身を確かめずに <work>/assets を再帰削除していた。実プロジェクト
//  （D:\SEED_projects\WakeOrPay など。リポジトリの git に入っていないので戻せない）を渡すと assets が丸ごと消える。
//  既定の置き場も固定名（%TEMP%\seed_template_thumbnails）だったので、2 つ同時に動かすと互いの assets とログを消し合った。
//
//  【規則】
//    - 道具が作った印のファイル（<work>/.seed_thumbnails）がある置き場だけを「道具のもの」とみなし、中身を消してよい
//    - 印が無くプロジェクトに見える置き場（assets/project_settings.json がある）は使わない（拒否）
//    - 印があっても .seedproj が直下にある置き場は使わない（道具は .seedproj を作らない＝プロジェクトに印が紛れた）
//    - 無い置き場・空の置き場は印を書いて使う
//    - それ以外の中身のある置き場（印が無い）は使わない（消してよいか分からない。利用者のフォルダかもしれない）
//    - パスがファイルなら使わない
//  ここはファイルシステムに触らない。調べた結果（WorkFolderSnapshot）から判定だけを返す（単体テストで全分岐を確かめる）。
//  実際に調べる・印を書く・同時実行を断る錠・消すのは ThumbnailWorkFolder。
// ============================================================

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Work;

/// <summary>作業の置き場を調べた結果（判定の入力。ファイルシステムの写し）。</summary>
/// <param name="Exists">フォルダとしてあるか。</param>
/// <param name="IsFile">同じパスにファイルがあるか（フォルダとしては使えない）。</param>
/// <param name="HasMarker">道具が作った印のファイル（<see cref="WorkFolderPolicy.MarkerFileName"/>）があるか。</param>
/// <param name="HasProjectSettings">プロジェクトの設定（assets/project_settings.json）があるか。</param>
/// <param name="HasSeedProject">プロジェクトのファイル（*.seedproj）が直下にあるか。</param>
/// <param name="IsEmpty">中身（ファイル・フォルダ）が 1 つも無いか。</param>
public readonly record struct WorkFolderSnapshot(
    bool Exists,
    bool IsFile,
    bool HasMarker,
    bool HasProjectSettings,
    bool HasSeedProject,
    bool IsEmpty);

/// <summary>作業の置き場の判定。</summary>
public enum WorkFolderVerdict
{
    /// <summary>無いので作って印を書く（使ってよい）。</summary>
    CreateNew,

    /// <summary>空のフォルダなので印を書く（使ってよい）。</summary>
    AdoptEmpty,

    /// <summary>道具の印がある（使ってよい・中身を消してよい）。</summary>
    ReuseOwned,

    /// <summary>プロジェクトに見える（使わない）。</summary>
    RefuseProject,

    /// <summary>印の無い・中身のあるフォルダ（使わない。消してよいか分からない）。</summary>
    RefuseForeign,

    /// <summary>パスがファイル（使わない）。</summary>
    RefuseNotDirectory,
}

/// <summary>作業の置き場の判定の規則と、置き場の名前の決まり。</summary>
public static class WorkFolderPolicy
{
    /// <summary>道具が作った置き場の印のファイル（置き場の直下）。中身は説明の文（判定は有無だけを見る）。</summary>
    public const string MarkerFileName = ".seed_thumbnails";

    /// <summary>同時に同じ置き場を使う実行を断る錠のファイル（置き場の直下。閉じると消える）。</summary>
    public const string LockFileName = ".seed_thumbnails.lock";

    /// <summary>一時のプロジェクトのアセットルートのフォルダ名（置き場の直下）。プロジェクトの見分けにも使う。</summary>
    public const string AssetsFolderName = "assets";

    /// <summary>プロジェクトの設定のファイル名（アセットルートの直下）。これがあり印が無ければプロジェクトとみなす。</summary>
    public const string ProjectSettingsFileName = "project_settings.json";

    /// <summary>プロジェクトのファイルを探す形（置き場の直下。道具はこれを作らない）。</summary>
    public const string SeedProjectSearchPattern = "*.seedproj";

    /// <summary>既定の置き場の親（%TEMP% の下。実行ごとの下位フォルダをここに作る）。</summary>
    public const string DefaultParentFolderName = "seed_template_thumbnails";

    /// <summary>既定の置き場（実行ごとの下位フォルダ）の名前の書式（{0} = プロセス ID・{1} = 乱数の 16 進）。</summary>
    public const string DefaultRunFolderNameFormat = "run_{0}_{1}";

    /// <summary>
    /// 調べた結果から、置き場を使ってよいかを判定する。
    /// </summary>
    /// <param name="snapshot">置き場を調べた結果。</param>
    /// <returns>判定。</returns>
    public static WorkFolderVerdict Judge(WorkFolderSnapshot snapshot)
    {
        // ── フォルダとして使えない ──
        if (snapshot.IsFile) return WorkFolderVerdict.RefuseNotDirectory;
        if (!snapshot.Exists) return WorkFolderVerdict.CreateNew;

        // ── プロジェクト（印があっても .seedproj があればプロジェクト。道具は .seedproj を作らない）──
        if (snapshot.HasSeedProject) return WorkFolderVerdict.RefuseProject;
        if (snapshot.HasProjectSettings && !snapshot.HasMarker) return WorkFolderVerdict.RefuseProject;

        // ── 道具のもの・空のもの ──
        if (snapshot.HasMarker) return WorkFolderVerdict.ReuseOwned;
        if (snapshot.IsEmpty) return WorkFolderVerdict.AdoptEmpty;

        // ── 印の無い中身のあるフォルダ（利用者のフォルダかもしれないので消さない・使わない）──
        return WorkFolderVerdict.RefuseForeign;
    }

    /// <summary>判定が「使ってよい」か。</summary>
    /// <param name="verdict">判定。</param>
    /// <returns>使ってよければ true。</returns>
    public static bool MayUse(WorkFolderVerdict verdict) =>
        verdict is WorkFolderVerdict.CreateNew or WorkFolderVerdict.AdoptEmpty or WorkFolderVerdict.ReuseOwned;

    /// <summary>
    /// 使わない理由を利用者向けの文にする（どうすれば使えるかも添える）。
    /// </summary>
    /// <param name="verdict">判定（使ってよい判定なら空文字）。</param>
    /// <param name="root">置き場の絶対パス。</param>
    /// <returns>理由の文。</returns>
    public static string DescribeRefusal(WorkFolderVerdict verdict, string root) => verdict switch
    {
        WorkFolderVerdict.RefuseProject =>
            $"--work にプロジェクトのフォルダは使えません（中の {AssetsFolderName}/ を作り直すため。{root}）。" +
            "新しいフォルダか空のフォルダを指定してください",
        WorkFolderVerdict.RefuseForeign =>
            $"--work のフォルダに道具の印（{MarkerFileName}）が無く、中身があります（消してよいか分からないので使いません。{root}）。" +
            "新しいフォルダか空のフォルダを指定してください",
        WorkFolderVerdict.RefuseNotDirectory =>
            $"--work のパスはファイルです（フォルダを指定してください。{root}）",
        _ => "",
    };

    /// <summary>
    /// 既定の置き場（実行ごとの下位フォルダ）のパスを作る。同時に動かしても互いの置き場が重ならない。
    /// </summary>
    /// <param name="tempRoot">一時フォルダ（%TEMP%）。</param>
    /// <param name="processId">この実行のプロセス ID（どの実行の置き場か分かるように名前に入れる）。</param>
    /// <param name="randomToken">乱数の 16 進（同じプロセス ID が後で使い回されても重ならない）。</param>
    /// <returns>置き場の絶対パス。</returns>
    public static string DefaultRunFolder(string tempRoot, int processId, string randomToken) =>
        Path.GetFullPath(Path.Combine(tempRoot, DefaultParentFolderName,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, DefaultRunFolderNameFormat, processId, randomToken)));
}
