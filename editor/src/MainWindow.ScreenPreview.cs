// ============================================================
//  MainWindow.ScreenPreview.cs — Edit 上の画面プレビューの配線（MainWindow の部分クラス）
//
//  【役割】（正典は docs/editor_screen_preview.md。ランタイム側は runtime/src/engine/core/app_base/app/editor_preview/）
//  Edit 中に任意のプレハブを「保存されないプレビュー」として任意のノードの下へ差し込み、画面を見ながら直せるようにする。
//  ここはパネル（ヒエラルキー・インスペクタ）とランタイムの間をつなぐだけで、判断の多くは WPF 非依存の Preview/ にある。
//    - 差し込み: ヒエラルキーの右クリック・インスペクタの案内 → RequestPreview（窓で選ぶ → 親の引き直し → 最近の一覧 → 送信）
//    - 消す: PREVIEW_CLEAR / PREVIEW_CLEAR_ALL、元のプレハブを開く（既存の OnActorFileOpened）
//    - 応答: PREVIEW_ADDED（行を見せる）・CLEARED / REFRESHED（件数のトースト）・ERROR（トースト＋ログ）
//    - 未保存にしない印: HIERARCHY_QUIET → 直後の HIERARCHY 1 通は未保存にしない（SendNavCommand と同じ数の仕組み）
//    - プレハブ保存後の作り直し: PropagateSavedPrefabToScene から RequestPreviewRefresh（自動反映の設定に関わらず）
//    - Delete: プレビューの根は PREVIEW_CLEAR、中は消さない、普通のノードは番号のずれを直して従来どおり
//    - AI ツール（MCP の seed_preview）: MainWindow.AiHost.Tools.cs が PreviewNotEditableReason・SendPreviewRequest・
//      SendPreviewClear を呼ぶ（UI と同じ道筋。違いは「最近使ったもの」へ足さないことと、結果をトーストでなく戻り値で返すこと）
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using SEEDEditor.Preview;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 文言 ────────────────────────────────────────────────

    /// <summary>ログの行の頭。</summary>
    private const string PreviewLogPrefix = "[Preview]";

    /// <summary>Edit 以外で差し込もうとしたときのトースト。</summary>
    private const string PreviewNotEditToast = "Play 中はプレビューを使えません（Edit のときに使えます）";

    /// <summary>閲覧専用の表示中に差し込もうとしたときのトースト。</summary>
    private const string PreviewReadOnlyToast = "閲覧専用の表示中はプレビューを使えません";

    /// <summary>プレビューを出したときのトースト。</summary>
    private const string PreviewAddedToast = "プレビューを出しました（シーンには保存されません）";

    /// <summary>プレビューを消したときのトーストの書式（{0} = 数）。</summary>
    private const string PreviewClearedToastFormat = "プレビューを {0} 個消しました";

    /// <summary>プレハブの保存でプレビューを作り直したときのトーストの書式（{0} = 数）。</summary>
    private const string PreviewRefreshedToastFormat = "プレハブの変更をプレビュー {0} 個へ反映しました";

    /// <summary>ランタイムがプレビューの命令を断ったときのトーストの書式（{0} = 理由）。</summary>
    private const string PreviewErrorToastFormat = "プレビュー: {0}";

    /// <summary>プレビューの中のノードを Delete で消そうとしたときのトースト。</summary>
    private const string PreviewInnerDeleteToast = "プレビューの中は消せません（根を選んで消すと、プレビューごと消えます）";

    /// <summary>シーンビューの「アクタファイル化」をプレビューの中で選んだとき（レビュー #14）。</summary>
    private const string PreviewExportRefusedToast =
        "プレビューの中はアクタファイル化できません（保存されない表示用のアクタです。元のプレハブを開いて編集してください）";

    /// <summary>差し込み先を見失ったが理由が分からないときのトースト。</summary>
    private const string PreviewTargetLostToast = "差し込み先が見つかりません。ヒエラルキーで選び直してください";

    /// <summary>ランタイムの管理がまだ無い（起動の途中）ときの理由。</summary>
    private const string PreviewRuntimeMissingReason = "ランタイムがまだ準備できていません";

    /// <summary>元のプレハブが見つからないときの文言の書式（{0} = パス）。</summary>
    private const string PreviewSourceMissingFormat = "元のプレハブが見つかりません:\n{0}";

    /// <summary>メッセージの見出し。</summary>
    private const string PreviewDialogCaption = "プレビュー";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>差し込み先の案内の表（editor/config/screen_preview_hosts.json。読めなければ組み込みの表）。</summary>
    private PreviewHostCatalog _previewHosts = PreviewHostCatalog.BuiltIn();

    /// <summary>最近プレビューしたプレハブの保存先（editor/settings/screen_preview_recent.json）。</summary>
    private PreviewRecentStore? _previewRecent;

    // ============================================================
    //  配線
    // ============================================================

    /// <summary>
    /// 画面プレビューを配線する（OnWindowLoaded のパネル配線から 1 回呼ぶ）。
    /// 差し込み先の案内の表と最近の一覧をここで読み、パネルへ渡す。
    /// </summary>
    private void InitScreenPreview()
    {
        _previewHosts  = PreviewHostCatalog.Load(SEEDEditor.Settings.EditorPaths.ConfigDir,
                                                 warning => EditorLog.Write($"{PreviewLogPrefix} {warning}"));
        _previewRecent = PreviewRecentStore.ForSettingsDir(SEEDEditor.Settings.EditorPaths.SettingsDir);

        // ── ランタイムの知らせ（受信スレッドから上がる。UI は Dispatcher で触る）──
        if (_runtimeManager is not null)
        {
            _runtimeManager.HierarchyQuietAnnounced += OnHierarchyQuietAnnounced;
            _runtimeManager.PreviewAdded            += OnPreviewAdded;
            _runtimeManager.PreviewCleared          += OnPreviewCleared;
            _runtimeManager.PreviewRefreshed        += OnPreviewRefreshed;
            _runtimeManager.PreviewError            += OnPreviewError;
        }

        // ── ヒエラルキー（右クリック）──
        PanelHierarchy.ConfigureScreenPreview(GetRecentPreviewPrefabs, IsPreviewEditMode, _previewHosts.DefaultLayerBias);
        PanelHierarchy.PreviewPrefabRequested     += RequestPreview;
        PanelHierarchy.PreviewClearRequested      += RequestPreviewClear;
        PanelHierarchy.PreviewClearAllRequested   += RequestPreviewClearAll;
        PanelHierarchy.PreviewSourceOpenRequested += OpenPreviewSource;

        // ── インスペクタ（帯・差し込み先の案内）──
        PanelInspector.SetPreviewHosts(_previewHosts);
        PanelInspector.PreviewClearRequested += RequestPreviewClear;
        // 案内は親の番号しか知らないので、表示中のタブ・親の名前と安定キーをヒエラルキーから埋める
        PanelInspector.PreviewHostRequested  += target => RequestPreview(PanelHierarchy.DescribePreviewParent(target));
    }

    /// <summary>最近プレビューしたプレハブ（いまのプロジェクト。最近の順）。</summary>
    /// <returns>仮想パスの一覧。</returns>
    private IReadOnlyList<string> GetRecentPreviewPrefabs()
    {
        if (_previewRecent is null) return Array.Empty<string>();
        var list = _previewRecent.Get(AssetsPath);
        if (_previewRecent.LastError is { } error) EditorLog.Write($"{PreviewLogPrefix} {error}");
        return list;
    }

    /// <summary>プレビューを使える状態（Edit）か。右クリックの項目を押せるかに使う。</summary>
    /// <returns>Edit なら true。</returns>
    private bool IsPreviewEditMode() => _runtimeManager?.State == EditorState.Edit;

    // ============================================================
    //  ランタイムの知らせ
    // ============================================================

    /// <summary>
    /// HIERARCHY_QUIET: 直後の HIERARCHY 1 通は未保存にしない（受信スレッドで同期的に数を足す。
    /// 既存の MarkDirtyFromHierarchy が次の HIERARCHY で 1 減らす。SendNavCommand と同じ仕組み）。
    /// </summary>
    private void OnHierarchyQuietAnnounced() => Interlocked.Increment(ref _suppressHierarchyDirtyCount);

    /// <summary>PREVIEW_ADDED: 表示中のタブなら出した根の行を見せ、トーストで知らせる。</summary>
    /// <param name="worldLine">世界線。</param>
    /// <param name="rootDfs">出した根の DFS 番号。</param>
    private void OnPreviewAdded(uint worldLine, int rootDfs)
    {
        // 直前の HIERARCHY の反映（同じく Dispatcher に積まれている）の後に走る
        Dispatcher.BeginInvoke(() =>
        {
            if (worldLine == PanelHierarchy.ActiveWorldLine) PanelHierarchy.RevealActor(rootDfs);
            ShowToast(PreviewAddedToast);
        });
    }

    /// <summary>PREVIEW_CLEARED: 1 つ以上消したらトーストで知らせる。</summary>
    /// <param name="count">消した数。</param>
    private void OnPreviewCleared(int count)
    {
        if (count <= 0) return;
        Dispatcher.BeginInvoke(() => ShowToast(string.Format(PreviewClearedToastFormat, count)));
    }

    /// <summary>PREVIEW_REFRESHED: 1 つ以上作り直したらトーストで知らせる。</summary>
    /// <param name="count">作り直した数。</param>
    /// <param name="path">保存したプレハブ。</param>
    private void OnPreviewRefreshed(int count, string path)
    {
        if (count <= 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            ShowToast(string.Format(PreviewRefreshedToastFormat, count));
            EditorLog.Write($"{PreviewLogPrefix} 作り直し: {path} → {count} 個");
        });
    }

    /// <summary>PREVIEW_ERROR: 理由をトーストとログに出す。</summary>
    /// <param name="reason">理由。</param>
    private void OnPreviewError(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EditorLog.Write($"{PreviewLogPrefix} ランタイムが断りました: {reason}");
            ShowToast(string.Format(PreviewErrorToastFormat, reason));
        });
    }

    // ============================================================
    //  要求（パネルから）
    // ============================================================

    /// <summary>
    /// プレハブをプレビューとして差し込む。中身が決まっていなければ窓で選ばせ、
    /// 送る直前に親をいまの木で引き直してから PREVIEW_PREFAB を送る。
    /// </summary>
    /// <param name="target">差し込み先（ヒエラルキーの右クリック・インスペクタの案内から）。</param>
    private void RequestPreview(PreviewInsertTarget target)
    {
        if (RefusePreviewIfNotEditable()) return;

        // ── 中身（決まっていなければ窓で選ぶ。キャンセルなら何もしない）──
        var prefab = target.Prefab;
        if (string.IsNullOrWhiteSpace(prefab))
        {
            prefab = PrefabPreviewPickerWindow.Pick(this, target.Label, AssetsPath, GetRecentPreviewPrefabs());
            if (prefab is null) return;
            // 窓を開いている間に状態が変わったかもしれない
            if (RefusePreviewIfNotEditable()) return;
        }

        // ── 親の引き直し → 最近の一覧 → 送信（AI ツールと共通の道筋）。断ったら理由をトーストで出す ──
        if (SendPreviewRequest(target, prefab, rememberRecent: true) is { } refused)
            ShowToast(refused);
    }

    /// <summary>
    /// 中身が決まった差し込み先を、送る直前にいまの木で引き直してから PREVIEW_PREFAB を送る
    /// （ヒエラルキーの右クリック・インスペクタの案内・AI ツール seed_preview の共通の道筋。
    /// Edit か・閲覧専用かの判定は呼び出し側が先に済ませる）。
    /// </summary>
    /// <param name="target">差し込み先（作った時点のもの）。</param>
    /// <param name="prefab">中身のプレハブ（assets:// 仮想パスか絶対パス）。</param>
    /// <param name="rememberRecent">「最近使ったもの」へ足すか（利用者の操作なら true。AI ツールは false）。</param>
    /// <returns>送れなかった理由（送れたら null）。</returns>
    private string? SendPreviewRequest(PreviewInsertTarget target, string prefab, bool rememberRecent)
    {
        // ── 親をいまの木で引き直す（窓の間の編集で番号がずれても同じノードへ。見失ったら断る）──
        var resolved = PanelHierarchy.TryResolvePreviewParent(target);
        if (resolved.ParentDfs is not int parentDfs)
        {
            var reason = resolved.Reason ?? PreviewTargetLostToast;
            EditorLog.Write($"{PreviewLogPrefix} 差し込みをやめました: {reason}");
            return reason;
        }
        if (_runtimeManager is null) return PreviewRuntimeMissingReason;

        string command;
        try
        {
            command = ScreenPreviewIpc.BuildPreviewPrefab(target.WorldLine, parentDfs, target.ToRequest(prefab));
        }
        catch (ArgumentException ex)
        {
            EditorLog.Write($"{PreviewLogPrefix} 命令を組み立てられません: {ex.Message}");
            return ex.Message;
        }

        // ── 最近の一覧へ足してから送る（利用者の操作のときだけ）──
        if (rememberRecent)
        {
            _previewRecent?.Push(AssetsPath, prefab);
            if (_previewRecent?.LastError is { } saveError) EditorLog.Write($"{PreviewLogPrefix} {saveError}");
        }
        _runtimeManager.SendToRuntime(command);
        EditorLog.Write($"{PreviewLogPrefix} 差し込み: {prefab}（{target.Label}・DFS {parentDfs}）");
        return null;
    }

    /// <summary>
    /// dfs を含むプレビューを 1 つ消す（ヒエラルキーの「プレビューを消す」・インスペクタの帯の［プレビューを消す］）。
    /// </summary>
    /// <param name="dfs">プレビューの根、または中のノードの DFS 番号（表示中のタブ）。</param>
    private void RequestPreviewClear(int dfs)
    {
        if (dfs < 0) return;
        if (SendPreviewClear(dfs) is { } refused) ShowToast(refused);
    }

    /// <summary>表示中のタブのプレビューを全部消す。</summary>
    private void RequestPreviewClearAll()
    {
        if (SendPreviewClear(dfs: null) is { } refused) ShowToast(refused);
    }

    /// <summary>
    /// プレビューを消す命令を送る（UI と AI ツール seed_preview の共通の道筋）。
    /// </summary>
    /// <param name="dfs">消すプレビューの根か中のノードの DFS 番号（null なら表示中のタブのプレビューを全部）。</param>
    /// <returns>送れなかった理由（送れたら null）。</returns>
    private string? SendPreviewClear(int? dfs)
    {
        if (PreviewNotEditableReason() is { } reason) return reason;
        if (_runtimeManager is null) return PreviewRuntimeMissingReason;

        var worldLine = PanelHierarchy.ActiveWorldLine;
        _runtimeManager.SendToRuntime(dfs is int target
            ? ScreenPreviewIpc.BuildClear(worldLine, target)
            : ScreenPreviewIpc.BuildClearAll(worldLine));
        return null;
    }

    /// <summary>
    /// 元のプレハブ（プレビューの根の中身）をアクタータブで開く（既存の OnActorFileOpened）。
    /// </summary>
    /// <param name="source">中身のプレハブ（assets:// 仮想パス or 絶対パス）。</param>
    private void OpenPreviewSource(string source)
    {
        var absolute = VirtualPath.ToAbsolute(source, AssetsPath);
        if (string.IsNullOrEmpty(absolute) || !File.Exists(absolute))
        {
            SEEDEditor.Headless.EditorDialogs.Show(string.Format(PreviewSourceMissingFormat, source), PreviewDialogCaption,
                                                   MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OnActorFileOpened(absolute);
    }

    /// <summary>
    /// 保存したプレハブを中身か枠に使うプレビューを作り直す（PropagateSavedPrefabToScene から。
    /// 「プレハブ保存時にシーンのインスタンスへ自動反映」の設定に関わらず送る。プレビューはシーンの内容を変えないため）。
    /// </summary>
    /// <param name="savedPrefabPath">保存したプレハブの絶対パス。</param>
    private void RequestPreviewRefresh(string savedPrefabPath)
    {
        if (_runtimeManager is null || string.IsNullOrWhiteSpace(savedPrefabPath)) return;
        try
        {
            _runtimeManager.SendToRuntime(ScreenPreviewIpc.BuildRefreshPath(savedPrefabPath));
        }
        catch (ArgumentException ex)
        {
            EditorLog.Write($"{PreviewLogPrefix} 作り直しの命令を組み立てられません: {ex.Message}");
            return;
        }
        EditorLog.Write($"{PreviewLogPrefix} プレハブの保存に続けてプレビューの作り直しを要求: {savedPrefabPath}");
    }

    /// <summary>
    /// プレビューを使えない状態（Edit 以外・閲覧専用の表示中）ならトーストで断る。
    /// </summary>
    /// <returns>断ったら true。</returns>
    private bool RefusePreviewIfNotEditable()
    {
        if (PreviewNotEditableReason() is not { } reason) return false;
        ShowToast(reason);
        return true;
    }

    /// <summary>
    /// プレビューを使えない状態（Edit 以外・閲覧専用の表示中）なら、その理由を返す（UI と AI ツールの共通の判定）。
    /// </summary>
    /// <returns>使えない理由。使えるなら null。</returns>
    private string? PreviewNotEditableReason()
    {
        if (_runtimeManager?.State != EditorState.Edit) return PreviewNotEditToast;
        if (CurrentReadOnlyState.DeniesEdit) return PreviewReadOnlyToast;
        return null;
    }

    // ============================================================
    //  Delete（TryDeleteSelected・シーンビューの「削除」から）
    // ============================================================

    /// <summary>
    /// 消す選択からプレビューを分けて先に片付け、残り（普通のノード）を返す。
    /// <list type="bullet">
    ///   <item>プレビューの根 → PREVIEW_CLEAR（番号の大きい順。後ろから消せば残りの根の番号はずれない）</item>
    ///   <item>プレビューの中（根が選ばれていない）→ 消さない（トースト）</item>
    ///   <item>普通のノード → 返す。プレビューを消した後の番号（前で消えた部分木の分だけ詰めた番号）で消すこと</item>
    /// </list>
    /// プレビューに触れない選択はそのまま返す（従来どおり）。
    /// </summary>
    /// <param name="selected">選択（DFS 番号）。</param>
    /// <returns>
    /// Ids = 普通のノードの、プレビューを消した後の番号（DELETE に使う）。
    /// IdsBeforeClear = 同じノードのいまの番号（ヒエラルキーで子の有無を見るのに使う）。
    /// </returns>
    private (List<int> Ids, List<int> IdsBeforeClear) RouteDeletionAroundPreviews(List<int> selected)
    {
        var plan = PanelHierarchy.PlanPreviewDeletion(selected);
        if (!plan.TouchesPreview) return (selected, selected);

        var worldLine = PanelHierarchy.ActiveWorldLine;
        foreach (var root in plan.ClearRoots)
            _runtimeManager?.SendToRuntime(ScreenPreviewIpc.BuildClear(worldLine, root));
        if (plan.BlockedInnerCount > 0) ShowToast(PreviewInnerDeleteToast);

        EditorLog.Write($"{PreviewLogPrefix} Delete の振り分け: 消すプレビュー [{string.Join(",", plan.ClearRoots)}]" +
                        $" / 消せない中のノード {plan.BlockedInnerCount} 個 / 普通のノード [{string.Join(",", plan.NormalIds)}]" +
                        $" → [{string.Join(",", plan.NormalIdsAfterClear)}]");
        return (plan.NormalIdsAfterClear.ToList(), plan.NormalIds.ToList());
    }
}
