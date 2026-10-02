// ============================================================
//  ThumbnailGenerator.cs — 計画した件をランタイムで順に撮って書き出す
//
//  【流れ】
//    0. （呼び出し側）作業の置き場を借りて前の実行の中身を消してある（ThumbnailWorkFolder。ここは何も消さない）
//    1. 一時のプロジェクトを作る（起動用の舞台・件ごとの舞台・参照するファイル）
//    2. 窓の大きさが同じ件をまとめ、まとまりごとにランタイムを 1 回だけ起動する
//       （1 件ずつ起動すると起動〈GPU の準備・スクリプトのコンパイル〉の時間が件数ぶんかかる。
//        起動後に窓の大きさを変えると画素のルートのキャンバスの自動の拡大が最初の大きさのまま残って歪むので、
//        大きさの違う舞台は起動を分ける。ふつうは UI・3D の 1 回と、横長の 2D の見本の 1 回）
//       起動用の舞台の合図（[THUMB] ready boot）で、スクリプトが動いていることを確かめる
//    3. 件ごとに: LOAD_SCENE → 合図を待つ → 撮る → 切り出して縮める → PNG を書く
//       失敗した件は飛ばして次へ（理由は結果の一覧へ）
//       合図が時間切れになった（ランタイムのフレームが止まった疑い）・ランタイムが終わったときは、起動し直して
//       その件を 1 度だけ撮り直す（見張り。止まったまま残りの全件が時間切れを待つのを防ぐ。起動し直しは上限つき）
//    4. STOP で終わらせる
// ============================================================

using System.Diagnostics;
using System.Globalization;
using SEEDEditor.Tools.SeedTemplateThumbnails.Imaging;
using SEEDEditor.Tools.SeedTemplateThumbnails.Runtime;
using SEEDEditor.Tools.SeedTemplateThumbnails.Stage;
using SEEDEditor.Tools.SeedTemplateThumbnails.Work;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>撮影の実行。</summary>
public sealed class ThumbnailGenerator
{
    /// <summary>
    /// ランタイムのログのファイル名の書式（{0}x{1} = 窓の大きさ・{2} = その大きさで何回目の起動か）。
    /// 次の実行で消す形 <see cref="ThumbnailWorkFolder.RuntimeLogSearchPattern"/>（runtime*.log）に当たる名前にすること。
    /// </summary>
    private const string RuntimeLogNameFormat = "runtime_{0}x{1}_{2}.log";

    /// <summary>同じ窓の大きさで起動し直してよい回数（止まった件の撮り直し。暴走の歯止め）。</summary>
    private const int MaxRestartsPerWindowSize = 3;

    /// <summary>起動用の舞台の合図を待つ上限（スクリプトのコンパイルを含む）。</summary>
    private static readonly TimeSpan BootReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>件ごとの合図を待つ時間の余裕（やり直しの上限・待つ秒に足す）。</summary>
    private static readonly TimeSpan StageReadyMargin = TimeSpan.FromSeconds(10);

    /// <summary>「ほぼ背景だけ」とみなす中身の割合の上限（部品が写っていない疑い）。</summary>
    private const double MinContentRatio = 0.01;

    /// <summary>件ごとに結果へ載せるログの行の数の上限。</summary>
    private const int MaxLogLinesPerEntry = 6;

    /// <summary>ログの行を結果へ載せるときの長さの上限（文字）。</summary>
    private const int MaxLogLineLength = 240;

    /// <summary>件ごとに結果へ載せるログの行の目印（読めない画像・スクリプトの例外・誤り）。</summary>
    private static readonly string[] NotableLogMarks =
        ["error", "Error", "ERROR", "panic", "例外", "Exception", "failed", "見つかりません", "[WARN]"];

    /// <summary>入力。</summary>
    private readonly ThumbnailInputs _inputs;

    /// <summary>借りた作業の置き場（一時のプロジェクト・撮った元の画像・ログ・セーブの置き場）。</summary>
    private readonly ThumbnailWorkFolder _workFolder;

    /// <summary>書き出す一辺（画素）。</summary>
    private readonly int _size;

    /// <summary>IPC の最初のポート。</summary>
    private readonly int _firstPort;

    /// <summary>出力先（標準出力）。</summary>
    private readonly Action<string> _write;

    /// <summary>
    /// 実行を作る。
    /// </summary>
    /// <param name="inputs">確定した入力。</param>
    /// <param name="workFolder">借りた作業の置き場（前の実行の中身は消してあること）。</param>
    /// <param name="size">書き出す一辺（画素）。</param>
    /// <param name="firstPort">IPC の最初のポート。</param>
    /// <param name="write">出力先。</param>
    public ThumbnailGenerator(ThumbnailInputs inputs, ThumbnailWorkFolder workFolder, int size, int firstPort, Action<string> write)
    {
        _inputs = inputs;
        _workFolder = workFolder;
        _size = size;
        _firstPort = firstPort;
        _write = write;
    }

    /// <summary>
    /// 計画した件を撮る。
    /// </summary>
    /// <param name="plans">撮る計画。</param>
    /// <param name="results">結果の積み先。</param>
    /// <returns>ランタイムを一度も使えなかったら理由、使えたら null（使えなかったまとまりの件は失敗として積む）。</returns>
    public string? Run(IReadOnlyList<StagePlan> plans, List<ThumbnailResult> results)
    {
        if (plans.Count == 0) return null;

        // ── 1. 一時のプロジェクト（前の実行の assets/・shots/・ログは借りたときに消してある。ここは作るだけ）──
        var project = new StageProject(_inputs.LibraryRoot, _workFolder);
        project.WriteBoot();
        var stages = new Dictionary<StagePlan, (string ScenePath, IReadOnlyList<string> Missing)>();
        foreach (var plan in plans) stages[plan] = project.WriteStage(plan);
        var shots = _workFolder.ShotsRoot;
        Directory.CreateDirectory(shots);
        _write($"一時のプロジェクト: {project.AssetsRoot}（舞台 {plans.Count} 件）");

        // ── 2〜4. 窓の大きさのまとまりごとに起動して撮る（カタログに現れた順のまとまり）──
        string? lastError = null;
        int usableSessions = 0;
        foreach (var group in plans.GroupBy(p => (p.WindowWidthPx, p.WindowHeightPx)))
        {
            var error = RunSession(project, group.Key.WindowWidthPx, group.Key.WindowHeightPx, group.ToList(), stages, shots, results);
            if (error is null) usableSessions++;
            else lastError = error;
        }
        return usableSessions == 0 ? lastError : null;
    }

    /// <summary>
    /// 1 つの窓の大きさの件を撮る。ランタイムが止まった疑い（合図の時間切れ・終了）があれば起動し直し、
    /// その件を 1 度だけ撮り直す（起動し直しは <see cref="MaxRestartsPerWindowSize"/> 回まで）。
    /// </summary>
    /// <returns>ランタイムを一度も使えなかったら理由（そのとき件はすべて失敗として積む）、使えたら null。</returns>
    private string? RunSession(StageProject project, int width, int height, IReadOnlyList<StagePlan> plans,
        IReadOnlyDictionary<StagePlan, (string ScenePath, IReadOnlyList<string> Missing)> stages,
        string shots, List<ThumbnailResult> results)
    {
        var pending = new Queue<StagePlan>(plans);
        var retried = new HashSet<StagePlan>();
        int launches = 0;
        bool usable = false;
        while (pending.Count > 0)
        {
            // ── 起動（止まった後の起動し直しを含む）──
            if (launches > MaxRestartsPerWindowSize)
            {
                FailAll(pending, results, $"ランタイムが {MaxRestartsPerWindowSize} 回起動し直しても止まりました");
                break;
            }
            launches++;
            var session = Launch(project, width, height, launches, out var launchError);
            if (session is null)
            {
                FailAll(pending, results, launchError);
                return usable ? null : launchError;
            }
            usable = true;

            using (session)
            {
                while (pending.Count > 0)
                {
                    var plan = pending.Peek();
                    var (result, stalled) = session.IsAlive
                        ? Shoot(session, plan, stages[plan].ScenePath, stages[plan].Missing, shots)
                        : (Failed(plan, "ランタイムが途中で終わりました", [], TimeSpan.Zero), true);
                    // 止まった疑い: 1 度目なら起動し直して同じ件から撮り直す（件は待ち行列に残す）
                    if (stalled && retried.Add(plan))
                    {
                        _write($"  止まった疑い: {plan.Name}（{result.Detail}）。ランタイムを起動し直して撮り直します");
                        break;
                    }
                    pending.Dequeue();
                    results.Add(result);
                    _write($"  {(result.Outcome == ThumbnailOutcome.Written ? "書きました" : "撮れません")}: {plan.Name}" +
                           (result.Outcome == ThumbnailOutcome.Written ? "" : $"（{result.Detail}）"));
                    // 撮り直しでもまた止まった: この件は失敗のまま、残りの件のためにランタイムも起動し直す
                    if (stalled) break;
                }

                if (!session.IsAlive)
                {
                    _write("ランタイムは既に終わっていました");
                    continue;
                }
                bool stopped = session.Stop();
                _write(stopped ? "ランタイムを STOP で終えました" : "ランタイムが STOP で終わらないので止めました");
            }
        }
        return null;
    }

    /// <summary>
    /// 窓の大きさを設定して起動し、起動用の舞台の合図（スクリプトが動き始めた）まで待つ。
    /// </summary>
    /// <param name="project">一時のプロジェクト。</param>
    /// <param name="width">窓の幅（画素）。</param>
    /// <param name="height">窓の高さ（画素）。</param>
    /// <param name="launch">この窓の大きさで何回目の起動か（ログのファイル名に使う）。</param>
    /// <param name="error">使えなかった理由。</param>
    /// <returns>使えるセッション（使えなければ null。そのときプロセスは止めてある）。</returns>
    private ThumbnailRuntimeSession? Launch(StageProject project, int width, int height, int launch, out string error)
    {
        project.WriteSettings(width, height);
        string logName = string.Format(CultureInfo.InvariantCulture, RuntimeLogNameFormat, width, height, launch);
        var startClock = Stopwatch.StartNew();
        ThumbnailRuntimeSession session;
        try
        {
            session = ThumbnailRuntimeSession.Start(_inputs.RuntimeExe, _inputs.RuntimeWorkingDirectory, project.AssetsRoot,
                project.BootSceneVirtualPath, _firstPort, _workFolder.Root, logName, ThumbnailStageDefaults.RenderScale, _write);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            error = "ランタイムを起動できません: " + ex.Message;
            return null;
        }

        var bootLines = new List<string>();
        var boot = session.WaitForStageReady(StageProject.BootTicket, BootReadyTimeout, bootLines);
        if (!boot.IsReady)
        {
            var notable = Notable(bootLines);
            error = $"起動用の舞台の合図がありません（{boot.Reason}。スクリプトのコンパイルの失敗など。{session.Log.Path} を見てください）"
                    + (notable.Count > 0 ? " / " + string.Join(" / ", notable) : "");
            session.Stop();
            session.Dispose();
            return null;
        }
        _write($"ランタイムの準備ができました（窓 {width}x{height}・ポート {session.Port}・" +
               $"{startClock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒・" +
               $"窓が前面を奪ったか: {(session.StoleForeground == true ? "はい" : session.StoleForeground == false ? "いいえ" : "不明")}）");
        error = "";
        return session;
    }

    /// <summary>
    /// 1 件を撮って書き出す。
    /// </summary>
    /// <returns>結果と、ランタイムが止まった疑い（合図の時間切れ・終了。起動し直せば撮れるかもしれない）か。</returns>
    private (ThumbnailResult Result, bool Stalled) Shoot(ThumbnailRuntimeSession session, StagePlan plan, string scenePath,
        IReadOnlyList<string> missing, string shotsFolder)
    {
        var clock = Stopwatch.StartNew();
        var warnings = missing.Select(m => "見つからない参照: " + m).ToList();
        var seen = new List<string>();

        // ── 読み込み → 合図 → 撮影 ──
        if (session.LoadScene(scenePath) is { } loadError)
            return (Failed(plan, "舞台のシーンを読めません: " + loadError, warnings, clock.Elapsed), !session.IsAlive);
        var wait = TimeSpan.FromSeconds(ThumbnailStageDefaults.GiveUpSeconds + plan.Sample.SettleSeconds) + StageReadyMargin;
        var ready = session.WaitForStageReady(plan.Ticket, wait, seen);
        if (!ready.IsReady)
        {
            bool stalled = ready.Kind is StageWaitKind.TimedOut or StageWaitKind.RuntimeEnded;
            return (Failed(plan, ready.Reason, [.. warnings, .. Notable(seen)], clock.Elapsed), stalled);
        }
        return (Capture(session, plan, shotsFolder, warnings, seen, clock), false);
    }

    /// <summary>撮って、切り出して縮め、書き出す（合図を受けた後）。</summary>
    private ThumbnailResult Capture(ThumbnailRuntimeSession session, StagePlan plan, string shotsFolder,
        List<string> warnings, List<string> seen, Stopwatch clock)
    {
        var shotPath = Path.Combine(shotsFolder, Path.GetFileNameWithoutExtension(plan.SceneRelPath) + ".png");
        if (session.Screenshot(shotPath, out var shotError) is not { } shot)
            return Failed(plan, "撮れません: " + shotError, warnings, clock.Elapsed);
        warnings.AddRange(Notable(seen.Concat(session.Log.Drain())));
        if (shot.Width != plan.WindowWidthPx || shot.Height != plan.WindowHeightPx)
            warnings.Add($"撮れた大きさ {shot.Width}x{shot.Height} が舞台の窓 {plan.WindowWidthPx}x{plan.WindowHeightPx} と違います（比で直して切り出しました）");

        // ── 切り出して縮め、書き出す ──
        try
        {
            var source = ThumbnailImage.Load(shot.Path);
            var pixels = ThumbnailImage.Compose(source, plan, _size, ThumbnailStageDefaults.LetterboxSrgb);
            var stats = ThumbnailImage.Measure(pixels, _size, ThumbnailStageDefaults.BackgroundSrgb, ThumbnailStageDefaults.LetterboxSrgb);
            if (stats.ContentRatio < MinContentRatio)
                warnings.Add("ほぼ背景だけです（部品が写っていない疑い。frame・focus・script を見直してください）");
            ThumbnailImage.SavePng(plan.OutputPath, pixels, _size, _size);
            long bytes = new FileInfo(plan.OutputPath).Length;
            return new ThumbnailResult(plan.Name, plan.Entry.TemplateRelPath, plan.Entry.Name, ThumbnailOutcome.Written, "", plan.OutputPath,
                bytes, _size, stats, warnings, clock.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or InvalidOperationException)
        {
            return Failed(plan, "画像を作れません: " + ex.Message, warnings, clock.Elapsed);
        }
    }

    /// <summary>ログの行から結果へ載せる行を選ぶ（目印を含む行。舞台のスクリプトの合図の行は理由として別に出すので除く）。</summary>
    private static List<string> Notable(IEnumerable<string> lines) => lines
        .Where(l => !l.Contains(ThumbnailRuntimeSession.SignalPrefix, StringComparison.Ordinal))
        .Where(l => NotableLogMarks.Any(m => l.Contains(m, StringComparison.Ordinal)))
        .Distinct()
        .Take(MaxLogLinesPerEntry)
        .Select(l => "ログ: " + (l.Length > MaxLogLineLength ? l[..MaxLogLineLength] + "…" : l))
        .ToList();

    /// <summary>撮れなかった結果。</summary>
    private static ThumbnailResult Failed(StagePlan plan, string reason, IReadOnlyList<string> warnings, TimeSpan elapsed) =>
        new(plan.Name, plan.Entry.TemplateRelPath, plan.Entry.Name, ThumbnailOutcome.Failed, reason, null, 0, 0, null, warnings, elapsed);

    /// <summary>まとまりの件をすべて同じ理由で失敗として積み、理由を返す。</summary>
    private static string FailAll(IEnumerable<StagePlan> plans, List<ThumbnailResult> results, string reason)
    {
        foreach (var plan in plans) results.Add(Failed(plan, reason, [], TimeSpan.Zero));
        return reason;
    }
}
