// ============================================================
//  ThumbnailWorkFolder.cs — 作業の置き場（--work）を借りる・前の実行の中身を消す・返す
//
//  【流れ】
//    1. Acquire: 置き場を調べ（Inspect）、WorkFolderPolicy.Judge で使ってよいかを決める
//       → 使ってよければフォルダを作り、印のファイル（.seed_thumbnails）を書き、錠のファイルを開く
//         （FileShare.None・閉じると消える。同じ置き場を同時に使う別の実行を断る）
//    2. CleanForRun: 前の実行の一時のプロジェクト（assets/）・撮った元の画像（shots/）・ランタイムのログ（runtime*.log）を消す。
//       モデルのキャッシュ（cache/）とセーブ（save/）は残す。消すのは錠を持っている間だけ（＝印のある置き場だけ）
//    3. Release: 錠を閉じる。既定の置き場（実行ごとの下位フォルダ）は、成功したときだけフォルダごと消す
//       （失敗したときはランタイムのログを見られるよう残す）。--work で指定した置き場は消さない
//
//  消すのはこのクラスだけ（StageProject・ThumbnailGenerator は作るだけ）。判定の規則は WorkFolderPolicy（純粋な処理）。
// ============================================================

using System.Security.Cryptography;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Work;

/// <summary>借りた作業の置き場（錠を持っている間だけ中身を消せる）。</summary>
public sealed class ThumbnailWorkFolder : IDisposable
{
    /// <summary>撮った元の画像の置き場（置き場の直下）。</summary>
    public const string ShotsFolderName = "shots";

    /// <summary>前の起動のランタイムのログを探す形（置き場の直下。起動のたびに消す）。</summary>
    public const string RuntimeLogSearchPattern = "runtime*.log";

    /// <summary>既定の置き場の名前に入れる乱数のバイト数（16 進で 2 倍の文字数）。</summary>
    private const int RandomTokenBytes = 4;

    /// <summary>既定の置き場を消すのを試す回数（止めた直後のランタイムがログを掴んでいることがある）。</summary>
    private const int RemoveAttempts = 3;

    /// <summary>既定の置き場を消すのをやり直すまでの間（ミリ秒）。</summary>
    private const int RemoveRetryDelayMs = 300;

    /// <summary>印のファイルに書く説明（判定は有無だけを見る。人が見たときに何のフォルダか分かるように）。</summary>
    private const string MarkerText =
        "SeedTemplateThumbnails（editor/tools/SeedTemplateThumbnails）の作業の置き場です。\n" +
        "道具は起動のたびにここの assets/・shots/・runtime*.log を作り直します。要らなければフォルダごと消してかまいません。\n";

    /// <summary>置き場の絶対パス。</summary>
    public string Root { get; }

    /// <summary>既定の置き場（実行ごとの下位フォルダ。成功したら消す）か。</summary>
    public bool IsTemporary { get; }

    /// <summary>判定（借りたときの）。</summary>
    public WorkFolderVerdict Verdict { get; }

    /// <summary>一時のプロジェクトのアセットルート（置き場の assets/）。</summary>
    public string AssetsRoot => Path.Combine(Root, WorkFolderPolicy.AssetsFolderName);

    /// <summary>撮った元の画像の置き場（置き場の shots/）。</summary>
    public string ShotsRoot => Path.Combine(Root, ShotsFolderName);

    /// <summary>同時実行を断る錠（閉じると錠のファイルも消える。null = 返した後）。</summary>
    private FileStream? _lock;

    private ThumbnailWorkFolder(string root, bool isTemporary, WorkFolderVerdict verdict, FileStream lockStream)
    {
        Root = root;
        IsTemporary = isTemporary;
        Verdict = verdict;
        _lock = lockStream;
    }

    // ============================================================
    //  既定の置き場
    // ============================================================

    /// <summary>
    /// 既定の置き場（%TEMP%\seed_template_thumbnails\run_&lt;プロセス ID&gt;_&lt;乱数&gt;）のパスを作る。
    /// </summary>
    /// <returns>置き場の絶対パス（まだ作らない）。</returns>
    public static string NewDefaultRunFolder() =>
        WorkFolderPolicy.DefaultRunFolder(Path.GetTempPath(), Environment.ProcessId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomTokenBytes)).ToLowerInvariant());

    // ============================================================
    //  調べる・借りる
    // ============================================================

    /// <summary>
    /// 置き場を調べる（判定の入力を作る。読むだけ）。
    /// </summary>
    /// <param name="root">置き場の絶対パス。</param>
    /// <returns>調べた結果。</returns>
    public static WorkFolderSnapshot Inspect(string root)
    {
        if (File.Exists(root)) return new WorkFolderSnapshot(Exists: false, IsFile: true, false, false, false, IsEmpty: false);
        if (!Directory.Exists(root)) return new WorkFolderSnapshot(Exists: false, IsFile: false, false, false, false, IsEmpty: true);
        return new WorkFolderSnapshot(
            Exists: true,
            IsFile: false,
            HasMarker: File.Exists(Path.Combine(root, WorkFolderPolicy.MarkerFileName)),
            HasProjectSettings: File.Exists(Path.Combine(root, WorkFolderPolicy.AssetsFolderName, WorkFolderPolicy.ProjectSettingsFileName)),
            HasSeedProject: Directory.EnumerateFiles(root, WorkFolderPolicy.SeedProjectSearchPattern).Any(),
            IsEmpty: !Directory.EnumerateFileSystemEntries(root).Any());
    }

    /// <summary>
    /// 置き場を借りる（使ってよければ作り・印を書き・錠を開く）。中身はまだ消さない（<see cref="CleanForRun"/>）。
    /// </summary>
    /// <param name="root">置き場（相対なら絶対にする）。</param>
    /// <param name="isTemporary">既定の置き場（成功したら消す）か。</param>
    /// <param name="error">借りられなかった理由。</param>
    /// <returns>借りた置き場（借りられなければ null。そのとき置き場には何も書いていない）。</returns>
    public static ThumbnailWorkFolder? Acquire(string root, bool isTemporary, out string error)
    {
        error = "";
        string full = Path.GetFullPath(root);
        WorkFolderVerdict verdict;
        try
        {
            verdict = WorkFolderPolicy.Judge(Inspect(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"--work のフォルダを調べられません（{full}）: {ex.Message}";
            return null;
        }
        if (!WorkFolderPolicy.MayUse(verdict))
        {
            error = WorkFolderPolicy.DescribeRefusal(verdict, full);
            return null;
        }

        try
        {
            // ── 作って印を書く（既にあれば書き直さない）──
            Directory.CreateDirectory(full);
            string marker = Path.Combine(full, WorkFolderPolicy.MarkerFileName);
            if (!File.Exists(marker)) File.WriteAllText(marker, MarkerText);

            // ── 錠（同じ置き場を同時に使う別の実行を断る。プロセスが落ちても OS が閉じ、ファイルも消える）──
            var lockStream = new FileStream(Path.Combine(full, WorkFolderPolicy.LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            return new ThumbnailWorkFolder(full, isTemporary, verdict, lockStream);
        }
        catch (IOException ex)
        {
            error = $"--work のフォルダは別の実行が使っているか、書けません（{full}）: {ex.Message}";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"--work のフォルダに書けません（{full}）: {ex.Message}";
            return null;
        }
    }

    // ============================================================
    //  前の実行の中身を消す
    // ============================================================

    /// <summary>
    /// 前の実行の一時のプロジェクト（assets/）・撮った元の画像（shots/）・ランタイムのログを消し、空の assets/・shots/ を作る。
    /// モデルのキャッシュ（cache/）・セーブ（save/）・印・錠には触れない。
    /// </summary>
    /// <exception cref="ObjectDisposedException">返した後に呼んだ（錠の無い置き場は消さない）。</exception>
    public void CleanForRun()
    {
        ObjectDisposedException.ThrowIf(_lock is null, this);

        if (Directory.Exists(AssetsRoot)) Directory.Delete(AssetsRoot, recursive: true);
        if (Directory.Exists(ShotsRoot)) Directory.Delete(ShotsRoot, recursive: true);
        foreach (var oldLog in Directory.EnumerateFiles(Root, RuntimeLogSearchPattern)) File.Delete(oldLog);
        Directory.CreateDirectory(AssetsRoot);
        Directory.CreateDirectory(ShotsRoot);
    }

    // ============================================================
    //  返す
    // ============================================================

    /// <summary>
    /// 錠を閉じて置き場を返す。既定の置き場は <paramref name="succeeded"/> のときだけフォルダごと消す。
    /// </summary>
    /// <param name="succeeded">撮影が全部うまくいったか（失敗したらログを見られるよう既定の置き場も残す）。</param>
    /// <returns>残した置き場の絶対パス（消したら null）。消せなかったときも残した扱いでパスを返す。</returns>
    public string? Release(bool succeeded)
    {
        Dispose();
        if (!IsTemporary || !succeeded) return Root;

        for (int attempt = 1; attempt <= RemoveAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 止めた直後のランタイムがログを掴んでいることがある。少し待ってやり直し、だめなら残す
                if (attempt < RemoveAttempts) Thread.Sleep(RemoveRetryDelayMs);
            }
        }
        return Root;
    }

    /// <summary>錠を閉じる（錠のファイルも消える）。置き場の中身には触れない。2 回呼んでもよい。</summary>
    public void Dispose()
    {
        var lockStream = Interlocked.Exchange(ref _lock, null);
        lockStream?.Dispose();
    }

    /// <summary>判定を表示用の短い語にする（ログ用）。</summary>
    /// <returns>語。</returns>
    public string DescribeVerdict() => Verdict switch
    {
        WorkFolderVerdict.CreateNew => "新しく作った",
        WorkFolderVerdict.AdoptEmpty => "空のフォルダに印を書いた",
        WorkFolderVerdict.ReuseOwned => "道具の印のある置き場を使い直す",
        _ => Verdict.ToString(),
    } + (IsTemporary ? TemporaryNote : "");

    /// <summary>既定の置き場であることの注記（ログ用）。</summary>
    private const string TemporaryNote = "・実行ごとの置き場（成功したら消す。失敗したらログを見られるよう残す）";
}
