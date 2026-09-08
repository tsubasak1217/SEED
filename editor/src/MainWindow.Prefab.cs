// ============================================================
//  MainWindow.Prefab.cs — プレハブ変更のシーンへの反映（伝播）
//
//  担当:
//   - プレハブ（.actor / .actor2d）をアクタータブで保存したときの、
//     今開いているシーン内インスタンスへの自動反映（設定でオン／オフ）
//   - シーンを開いた直後の「版ずれ」検出と、非モーダルバナーでの提示
//   - バナーの［更新する］［無視］操作
//
//  設計の前提（正典: runtime/src/engine/core/app_base/app/prefab_ops.rs 冒頭、
//  および docs/editor_prefab.md）:
//   - ランタイムはロード時にもプレハブ保存時にも**自動では再展開しない**。
//     過去にそれをやってインスタンス側の編集が黙って消えるデータ損失を起こしたため。
//   - 反映は必ずエディタからの明示コマンド（PREFAB_REAPPLY_PATH）で行い、
//     (1) 設定でオフにできる (2) Undo 1 操作で戻せる (3) 件数を必ず知らせる
//     の 3 条件を満たす。ロード時は**検出して知らせるだけ**で、決して上書きしない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── IPC コマンド文字列 ───────────────────────────────────────
    // マジックストリングを散らさないよう、送信するコマンド名はここへ集約する。

    /// <summary>指定した 1 本のプレハブを参照する全インスタンスを再展開する IPC の接頭辞。</summary>
    private const string PrefabReapplyPathCommandPrefix = "PREFAB_REAPPLY_PATH:";

    /// <summary>シーン内プレハブの版ずれを問い合わせる IPC（引数なし・読み取りのみ）。</summary>
    private const string PrefabStatusCommand = "PREFAB_STATUS";

    /// <summary>
    /// 直近に保存したプレハブ（.actor / .actor2d）の絶対パス。
    /// 保存は非同期（SAVE_ACTOR → SAVE_OK）なので、完了時に「どのファイルを保存したか」を
    /// 思い出すために保持する。保存を開始していないときは null。
    /// </summary>
    private string? _savingActorPath;

    /// <summary>
    /// 版ずれバナーで［更新する］を押したときに再展開する対象の参照パス一覧。
    /// バナーを出した時点の PREFAB_STATUS の結果（stale が 1 件以上のものだけ）を保持する。
    /// </summary>
    private readonly List<string> _stalePrefabSources = new();

    // ── プレハブ保存時の自動反映 ─────────────────────────────────

    /// <summary>
    /// プレハブの保存を開始したことを記録する（<c>ExecuteActorSave</c> から呼ぶ）。
    /// </summary>
    /// <param name="path">保存先の絶対パス。</param>
    private void NotifyActorSaveStarted(string path) => _savingActorPath = path;

    /// <summary>
    /// プレハブの保存が完了したときに、シーン内インスタンスへの自動反映を行う
    /// （<c>OnSaveCompleted</c> の成功経路から呼ぶ）。
    ///
    /// 設定「プレハブ保存時にシーンのインスタンスへ自動反映」がオフのときは何もしない。
    /// 反映結果（件数）は <see cref="OnPrefabReapplyCompleted"/> がトーストで知らせる。
    /// </summary>
    private void PropagateSavedPrefabToScene()
    {
        var path = _savingActorPath;
        _savingActorPath = null;

        if (string.IsNullOrEmpty(path)) return;
        if (!EditorPreferences.Instance.PrefabAutoPropagateOnSave) return;

        // ランタイムがシーンを持っていなければ反映先が無い（アクタータブ単独編集など）。
        if (_runtimeManager is null) return;

        _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{path}");
        EditorLog.Write($"[Prefab] 保存に続けて自動反映を要求: {path}");
    }

    /// <summary>
    /// 参照パス指定の再展開が終わったときの通知（IPC <c>PREFAB_REAPPLY_DONE</c>）。
    ///
    /// 0 件（＝このシーンにそのプレハブのインスタンスが無い）のときは黙っている。
    /// 1 件以上ならトーストで件数と「Ctrl+Z で戻せる」ことを知らせ、シーンを未保存扱いにする
    /// （再展開の結果は .scene を保存して初めて残るため）。
    /// </summary>
    /// <param name="count">再展開したインスタンス数。</param>
    /// <param name="source">プレハブの assets:// 仮想パス。</param>
    private void OnPrefabReapplyCompleted(int count, string source)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (count <= 0) return;

            var name = PrefabDisplayName(source);
            ShowToast($"プレハブ {name} の変更を {count} 個のインスタンスへ反映しました（Ctrl+Z で戻せます）");
            // 再展開はシーンの内容を変えるので、保存を促すために未保存扱いにする。
            MarkDirty();
            EditorLog.Write($"[Prefab] 自動反映: {source} → {count} 件");
        });
    }

    // ── シーンを開いた直後の版ずれ検出 ───────────────────────────

    /// <summary>
    /// 今開いているシーンのプレハブ版ずれをランタイムへ問い合わせる
    /// （シーン読み込み完了 <c>SCENE_LOADED</c> の後に呼ぶ）。
    ///
    /// 読み取りのみのコマンドで、シーンには一切触れない。
    /// </summary>
    private void RequestPrefabStatus()
    {
        HidePrefabStaleBanner();
        _runtimeManager?.SendToRuntime(PrefabStatusCommand);
    }

    /// <summary>
    /// 版ずれ問い合わせの応答（IPC <c>PREFAB_STATUS</c>）を受けてバナーを出す。
    ///
    /// stale（＝取り込んだ版とファイルの現在の版が食い違う）が 1 件以上あるときだけ出す。
    /// 版が不明な旧シーン由来のインスタンス（unknown）は対象にしない
    /// ＝ 勝手に「更新しますか」と促さない。次に再展開したときに版が記録される。
    /// </summary>
    /// <param name="json">
    /// <c>[{"source":..,"total":N,"stale":N,"unknown":N,"missing":bool}, ..]</c> の JSON。
    /// </param>
    private void OnPrefabStatusReceived(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _stalePrefabSources.Clear();
            int staleInstances = 0;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("stale",  out var staleEl)) continue;
                    if (!item.TryGetProperty("source", out var srcEl))   continue;
                    int stale = staleEl.TryGetInt32(out int v) ? v : 0;
                    if (stale <= 0) continue;

                    var source = srcEl.GetString();
                    if (string.IsNullOrEmpty(source)) continue;

                    _stalePrefabSources.Add(source);
                    staleInstances += stale;
                }
            }
            catch (JsonException e)
            {
                EditorLog.Write($"[Prefab] PREFAB_STATUS の解析に失敗: {e.Message}");
                return;
            }

            if (_stalePrefabSources.Count == 0) { HidePrefabStaleBanner(); return; }

            ShowPrefabStaleBanner(staleInstances);
        });
    }

    /// <summary>版ずれバナーを、対象プレハブ名とインスタンス数付きで表示する。</summary>
    /// <param name="staleInstances">更新が来ているインスタンスの合計数。</param>
    private void ShowPrefabStaleBanner(int staleInstances)
    {
        if (PrefabStaleBanner is null || PrefabStaleText is null) return;

        // 対象が 1 本ならファイル名を、複数なら本数を出す（バナーを 1 行に収めるため）。
        var target = _stalePrefabSources.Count == 1
            ? PrefabDisplayName(_stalePrefabSources[0])
            : $"{_stalePrefabSources.Count} 個のプレハブ";

        PrefabStaleText.Text = $"プレハブが更新されています: {target}（インスタンス {staleInstances} 個）";
        PrefabStaleBanner.Visibility = Visibility.Visible;
        EditorLog.Write($"[Prefab] 版ずれ検出: {target} / インスタンス {staleInstances} 個");
    }

    /// <summary>版ずれバナーを閉じる。</summary>
    private void HidePrefabStaleBanner()
    {
        if (PrefabStaleBanner is not null)
            PrefabStaleBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 版ずれバナーの［更新する］。検出した参照パスごとに再展開を要求する。
    ///
    /// 破壊的操作（インスタンス側の変更がファイル内容で上書きされる）だが、
    /// Undo 1 操作で戻せることを結果のトーストで案内する。
    /// </summary>
    private void OnPrefabStaleUpdate(object sender, RoutedEventArgs e)
    {
        HidePrefabStaleBanner();
        if (_runtimeManager is null) return;

        foreach (var source in _stalePrefabSources)
            _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{source}");

        _stalePrefabSources.Clear();
    }

    /// <summary>版ずれバナーの［無視］。今回の表示を閉じるだけで、シーンには触れない。</summary>
    private void OnPrefabStaleIgnore(object sender, RoutedEventArgs e)
    {
        HidePrefabStaleBanner();
        _stalePrefabSources.Clear();
    }

    // ── 設定メニュー ─────────────────────────────────────────────

    /// <summary>
    /// 「表示 &gt; シーン &gt; プレハブ保存時にシーンのインスタンスへ自動反映」トグル。
    /// <see cref="EditorPreferences.PrefabAutoPropagateOnSave"/> へ永続化する。
    /// </summary>
    private void OnTogglePrefabAutoPropagate(object sender, RoutedEventArgs e)
    {
        bool on = MenuItemPrefabAutoPropagate.IsChecked;
        EditorPreferences.Instance.PrefabAutoPropagateOnSave = on;
        EditorPreferences.Save();
        EditorLog.Write($"PrefabAutoPropagateOnSave = {on}");
    }

    // ── 表示用ヘルパ ─────────────────────────────────────────────

    /// <summary>
    /// プレハブ参照パス（assets:// 仮想パス or 絶対パス）から、通知に出す短い名前を取り出す。
    /// 区切りは Windows / 仮想パス両方を考慮して '/' と '\\' の両方を見る。
    /// </summary>
    private static string PrefabDisplayName(string source)
    {
        if (string.IsNullOrEmpty(source)) return "(不明なプレハブ)";
        int cut = source.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 && cut + 1 < source.Length ? source[(cut + 1)..] : source;
    }
}
