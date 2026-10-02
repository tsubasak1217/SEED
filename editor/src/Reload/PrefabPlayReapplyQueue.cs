using System;
using System.Collections.Generic;

namespace SEEDEditor.Reload;

/// <summary>
/// プレハブ（.actor）を保存したときにランタイムへ送るもの（<see cref="PrefabPlayReapplyQueue.DecideOnSave"/> の結果）。
/// </summary>
public enum PrefabSaveAction
{
    /// <summary>何も送らない（Edit で設定「プレハブ保存時にシーンのインスタンスへ自動反映」がオフ）。</summary>
    None,

    /// <summary>Edit のシーンのインスタンスを今すぐ丸ごと再展開する（<c>PREFAB_REAPPLY_PATH</c>。Undo 1 操作）。</summary>
    ReapplyNow,

    /// <summary>
    /// Play 中のインスタンスへ状態を保ったまま当て直し（<c>PREFAB_LIVE_PATCH_PATH</c>）、
    /// パスを覚えておいて Play 停止後に Edit のシーンへも反映する。
    /// </summary>
    LivePatchAndRemember,
}

/// <summary>
/// Play 停止（Edit 復帰）時にすること（<see cref="PrefabPlayReapplyQueue.TakeOnReturnToEdit"/> の結果）。
/// </summary>
/// <param name="ChangedPaths">
/// Play 中に変わったプレハブのパス（覚えた順）。画面プレビューの作り直し（設定に関わらず行う）にも使う。
/// </param>
/// <param name="Reapply">
/// true なら <see cref="ChangedPaths"/> の全部へ <c>PREFAB_REAPPLY_PATH</c> を送る（設定オン）。
/// false なら再展開しない（設定オフ。<see cref="RequestStatus"/> を見る）。
/// </param>
public sealed record PrefabReturnPlan(IReadOnlyList<string> ChangedPaths, bool Reapply)
{
    /// <summary>何もしない計画。</summary>
    public static readonly PrefabReturnPlan Nothing = new(Array.Empty<string>(), false);

    /// <summary>Edit のシーンへ <c>PREFAB_REAPPLY_PATH</c> を送るパス（設定オフなら空）。</summary>
    public IReadOnlyList<string> ReapplyPaths => Reapply ? ChangedPaths : Array.Empty<string>();

    /// <summary>
    /// 設定オフで変わったプレハブがあるとき true。再展開はせず、版ずれの問い合わせ（<c>PREFAB_STATUS</c>）だけ
    /// 送って「プレハブが更新されています」のバナーで知らせる（シーンには触れない）。
    /// </summary>
    public bool RequestStatus => !Reapply && ChangedPaths.Count > 0;
}

/// <summary>
/// Play 中に変わったプレハブを覚えておき、Play 停止後に Edit のシーンへ反映するための待ち行列（純粋なロジック）。
///
/// 【なぜ要るか】
/// Play 中にプレハブを保存（または Play 中の変更を書き戻し）すると、Play のワールドのインスタンスは
/// <c>PREFAB_LIVE_PATCH_PATH</c> で当て直されるが、Play を止めるとワールドは Play 前の写しへ戻る。
/// Edit のシーンのインスタンスは古い版のままなので、停止後に <c>PREFAB_REAPPLY_PATH</c> を送って揃える
/// （<see cref="AutoReloadPolicy"/> の「Play 中は保留して停止時に反映する」と同じ流儀）。
///
/// 【規則】（テスト: editor/tests/PrefabPlayReapplyTests）
///   - 保存したとき
///       Edit         → 設定オンなら ReapplyNow、オフなら None（従来どおり）
///       Play / Pause → 設定に関わらず LivePatchAndRemember（当て直しは Play の表示だけで、停止で消える）
///   - 覚えるのは Play / Pause 中だけ。同じファイルは 1 回だけ（大文字小文字・区切りの違いは同一視）
///   - 停止したとき（覚えたものがあれば）
///       設定オン  → 覚えたパスを全部 PREFAB_REAPPLY_PATH（Undo はパスごとに 1 操作）
///       設定オフ  → 再展開せず PREFAB_STATUS だけ（版ずれのバナーで知らせる。シーンには触れない）
///     どちらでも待ち行列は空になる（次の Play へ持ち越さない）
///
/// パスは呼び出し側が絶対パスへ揃えてから渡す（仮想パスと絶対パスの同一視はここでしない）。
/// WPF・ランタイム非依存（単体テストのため）。
/// </summary>
public sealed class PrefabPlayReapplyQueue
{
    /// <summary>覚えたパス（覚えた順）。</summary>
    private readonly List<string> _paths = new();

    /// <summary>重複判定用（区切りを揃えて大文字小文字を無視）。</summary>
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>覚えているパスの数。</summary>
    public int Count => _paths.Count;

    /// <summary>
    /// プレハブを保存したときにランタイムへ送るものを決める。
    /// </summary>
    /// <param name="state">保存した時点の再生状態。</param>
    /// <param name="autoPropagate">設定「プレハブ保存時にシーンのインスタンスへ自動反映」。</param>
    public static PrefabSaveAction DecideOnSave(PlaybackState state, bool autoPropagate)
    {
        // Play 中の当て直しは Play の表示だけを変え、停止で捨てられる。設定はシーン（保存されるもの）を
        // 守るためのものなので、Play 中の当て直しは設定に関わらず行う。Edit への反映は停止時に設定を見る。
        if (AutoReloadPolicy.IsPlaying(state)) return PrefabSaveAction.LivePatchAndRemember;
        return autoPropagate ? PrefabSaveAction.ReapplyNow : PrefabSaveAction.None;
    }

    /// <summary>
    /// Play 中に変わったプレハブを覚える（Edit 中は覚えない＝その場で反映済みのため）。
    /// </summary>
    /// <param name="absolutePath">プレハブの絶対パス。</param>
    /// <param name="state">今の再生状態。</param>
    /// <returns>新しく覚えたら true（Edit 中・空のパス・既に覚えているときは false）。</returns>
    public bool Remember(string absolutePath, PlaybackState state)
    {
        if (!AutoReloadPolicy.IsPlaying(state)) return false;
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        if (!_keys.Add(Key(absolutePath))) return false;
        _paths.Add(absolutePath);
        return true;
    }

    /// <summary>
    /// Play が止まって Edit へ戻ったときにすることを決め、待ち行列を空にする。
    /// </summary>
    /// <param name="autoPropagate">その時点の設定「プレハブ保存時にシーンのインスタンスへ自動反映」。</param>
    public PrefabReturnPlan TakeOnReturnToEdit(bool autoPropagate)
    {
        if (_paths.Count == 0) return PrefabReturnPlan.Nothing;
        var plan = new PrefabReturnPlan(_paths.ToArray(), autoPropagate);
        _paths.Clear();
        _keys.Clear();
        return plan;
    }

    /// <summary>重複判定の鍵（区切りを '/' に揃える。大文字小文字は HashSet 側で無視する）。</summary>
    private static string Key(string path) => path.Trim().Replace('\\', '/');
}
