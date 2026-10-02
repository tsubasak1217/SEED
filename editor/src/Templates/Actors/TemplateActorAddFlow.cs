// ============================================================
//  TemplateActorAddFlow.cs — テンプレートアクタを 1 件追加する一連の手順（窓と AI ツールの共通の道筋）
//
//  【役割】
//  「編集できるか → 追加先の引き直し → 入れてよいか → 準備（まっさらにする・依存ファイルのコピー・一時ファイル）
//   → 送る直前にもう一度引き直す → ADD_TEMPLATE_ACTOR を送る」を 1 か所にまとめる。
//  テンプレートアクタの窓（TemplateActorPickerWindow）と、MCP の seed_template_actor（action:add。
//  EditorCommandExecutor.TemplateActors.cs → MainWindow.AiHost.Tools.cs）が同じ手順を通るようにするため、
//  窓から切り出した。窓は結果を状態の行へ出し、AI ツールは結果を JSON にして返す。
//
//  【スレッド】
//  UI スレッドから呼ぶこと。準備（重い）だけを Task.Run で外へ出し、続き（追加先の引き直し・送信）は
//  呼び出し元の同期コンテキスト（UI スレッド）へ戻って行う（ConfigureAwait(false) を付けない）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/TemplateImportTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Threading.Tasks;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// 追加の結末の種類。
/// </summary>
public enum TemplateActorAddStatus
{
    /// <summary>ランタイムへ ADD_TEMPLATE_ACTOR を送った。</summary>
    Sent,

    /// <summary>編集できない状態だった（閲覧専用の表示中など）。何もしていない。</summary>
    ReadOnly,

    /// <summary>追加先を見失った（名前の変更・移動・削除・タブの切り替え）。送っていない。</summary>
    TargetLost,

    /// <summary>このテンプレートはこの追加先へ入れられない（2D/3D の規則）。何もしていない。</summary>
    Rejected,

    /// <summary>準備に失敗した（テンプレートが読めない・一時ファイルが書けない など）。送っていない。</summary>
    PrepareFailed,
}

/// <summary>
/// 追加の結果。
/// </summary>
public sealed record TemplateActorAddOutcome
{
    /// <summary>結末の種類。</summary>
    public required TemplateActorAddStatus Status { get; init; }

    /// <summary>送れなかった理由（利用者向けの文。送れたら null）。</summary>
    public string? Message { get; init; }

    /// <summary>最後に引き直せた追加先（一度も引き直せなければ渡されたもの）。</summary>
    public required TemplateActorTarget Target { get; init; }

    /// <summary>準備の結果（準備まで進んだときだけ。コピーしたファイル・警告が入る）。</summary>
    public TemplateActorInstallResult? Result { get; init; }

    /// <summary>ランタイムへ送った 1 行（送ったときだけ）。</summary>
    public string? SentCommand { get; init; }

    /// <summary>送れたか。</summary>
    public bool IsSent => Status == TemplateActorAddStatus.Sent;
}

/// <summary>
/// テンプレートアクタを 1 件追加する一連の手順。状態を持たない。
/// </summary>
public static class TemplateActorAddFlow
{
    /// <summary>追加先を見失ったが理由が分からないときの文。</summary>
    public const string TargetLostFallbackMessage = "追加先が見つかりません。ヒエラルキーで右クリックし直してください";

    /// <summary>ログの行の頭（窓と同じ）。</summary>
    private const string LogPrefix = "[テンプレートアクタ]";

    /// <summary>
    /// テンプレートアクタを 1 件追加する。
    /// </summary>
    /// <param name="context">外部の機能一式（MainWindow が作る。窓と同じもの）。</param>
    /// <param name="target">追加先（作った時点のもの。ここで引き直す）。</param>
    /// <param name="entry">追加するテンプレートアクタ。</param>
    /// <param name="targetRefreshed">追加先を引き直せたたびに呼ぶ（窓が「追加先」の表示を更新する。省略可）。</param>
    /// <param name="preparing">重い準備に入る直前に呼ぶ（窓が「追加しています…」を出す。省略可）。</param>
    /// <param name="stagingDirectory">一時ファイルの置き場（省略時は OS の一時フォルダ。テストで差し替える）。</param>
    /// <returns>結果。</returns>
    public static async Task<TemplateActorAddOutcome> RunAsync(
        TemplateActorPickerContext context,
        TemplateActorTarget target,
        TemplateActorEntry entry,
        Action<TemplateActorTarget>? targetRefreshed = null,
        Action? preparing = null,
        string? stagingDirectory = null)
    {
        // ── 1. 編集できる状態か（閲覧専用の写しを見ている間は追加しない）──
        if (context.ReadOnlyReason() is { } readOnly)
            return new TemplateActorAddOutcome { Status = TemplateActorAddStatus.ReadOnly, Message = readOnly, Target = target };

        // ── 2. 追加先をいまのツリーで引き直す（DFS 番号のずれ・削除・タブの切り替え）──
        var first = context.RefreshTarget(target);
        if (first.Target is null)
            return Lost(first, target, result: null);
        target = first.Target;
        targetRefreshed?.Invoke(target);

        // ── 3. 入れてよいか（2D/3D の規則。既存の「子として追加」と同じ）──
        if (target.RejectReason(entry.Is2D) is { } reject)
        {
            return new TemplateActorAddOutcome
            {
                Status  = TemplateActorAddStatus.Rejected,
                Message = $"「{entry.Name}」はここへ追加できません: {reject}",
                Target  = target,
            };
        }

        // ── 4. 準備（UI スレッドの外）。アセットの場所は UI スレッドで先に読む ──
        preparing?.Invoke();
        TemplateActorInstallResult result;
        try
        {
            var libraryRoot = context.LibraryRoot;
            var assetsRoot  = context.AssetsRoot();
            result = await Task.Run(() => TemplateActorInstaller.Prepare(libraryRoot, assetsRoot, entry, stagingDirectory));
        }
        catch (Exception ex)
        {
            result = new TemplateActorInstallResult { Error = ex.Message };
        }

        // コピーはできていることがあるので、失敗でもプロジェクトパネルは読み直す
        if (result.Copy.HasCopied) context.FilesCopied(result.Copy);
        LogResult(context, entry, target, result);

        if (!result.Success)
        {
            return new TemplateActorAddOutcome
            {
                Status  = TemplateActorAddStatus.PrepareFailed,
                Message = $"追加できませんでした: {result.Error}",
                Target  = target,
                Result  = result,
            };
        }

        // ── 5. 送信の直前にもう一度だけ引き直す（準備の間にツリーが変わりうる）──
        var second = context.RefreshTarget(target);
        if (second.Target is null)
            return Lost(second, target, result);
        target = second.Target;
        targetRefreshed?.Invoke(target);

        // ── 6. 送る。ランタイムが木へ入れ、選択し、Undo を 1 件積む ──
        var command = TemplateActorIpc.BuildAddCommand(target, result.StagedPath);
        context.SendToRuntime(command);
        return new TemplateActorAddOutcome
        {
            Status      = TemplateActorAddStatus.Sent,
            Target      = target,
            Result      = result,
            SentCommand = command,
        };
    }

    /// <summary>追加先を見失った結果を作る。</summary>
    /// <param name="refresh">引き直しの結果（理由が入っている）。</param>
    /// <param name="lastTarget">最後に引き直せた追加先。</param>
    /// <param name="result">準備の結果（準備の後で見失ったときだけ）。</param>
    /// <returns>結果。</returns>
    private static TemplateActorAddOutcome Lost(
        TemplateActorTargetRefresh refresh, TemplateActorTarget lastTarget, TemplateActorInstallResult? result) => new()
    {
        Status  = TemplateActorAddStatus.TargetLost,
        Message = refresh.Reason ?? TargetLostFallbackMessage,
        Target  = lastTarget,
        Result  = result,
    };

    /// <summary>準備の結果をエディタのログ（Output パネル）へ書く。</summary>
    /// <param name="context">外部の機能一式（ログの書き先）。</param>
    /// <param name="entry">追加するテンプレートアクタ。</param>
    /// <param name="target">追加先。</param>
    /// <param name="result">準備の結果。</param>
    private static void LogResult(
        TemplateActorPickerContext context, TemplateActorEntry entry, TemplateActorTarget target, TemplateActorInstallResult result)
    {
        if (context.Log is not { } log) return;
        if (!result.Success)
        {
            log($"{LogPrefix} 追加できませんでした: {entry.TemplateRelPath} — {result.Error}");
            return;
        }
        log($"{LogPrefix} 追加: {entry.TemplateRelPath}（追加先: {target.Describe()}）");
        foreach (var f in result.Copy.Copied)          log($"{LogPrefix}   コピー: {f}");
        foreach (var f in result.Copy.SkippedExisting) log($"{LogPrefix}   既にあるので触らず: {f}");
        foreach (var f in result.Copy.Failures)        log($"{LogPrefix}   コピー失敗: {f.RelPath} — {f.Message}");
        foreach (var m in result.Missing)              log($"{LogPrefix}   見つからない参照: {m}");
        foreach (var w in result.Warnings)             log($"{LogPrefix}   {w}");
    }
}
