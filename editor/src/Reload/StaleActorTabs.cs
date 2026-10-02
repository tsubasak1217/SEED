using System;
using System.Collections.Generic;

namespace SEEDEditor.Reload;

/// <summary>
/// 「読み直しが要る」印の付いたアクタータブを表示するときにすること（<see cref="StaleActorTabs.TakeOnShow"/> の結果）。
/// </summary>
public enum StaleActorTabShowAction
{
    /// <summary>読み直さずにそのまま表示する（印が無い・今は読み直せない〈Edit でない〉）。</summary>
    ShowAsIs,

    /// <summary>ファイルから読み直して表示する（<c>OPEN_ACTOR</c>）。未保存の編集が無いので確かめない。</summary>
    Reload,

    /// <summary>
    /// 未保存の編集があるので、読み直す前に利用者へ確かめる（読み直すとそのタブの未保存の編集が消えるため）。
    /// 答えが「いいえ」なら今の中身のまま表示する（印はもう消えているので、次に表示しても聞き直さない）。
    /// </summary>
    AskBeforeReload,
}

/// <summary>
/// Play 中の書き戻し（<c>PREFAB_WRITE_BACK</c>）でファイルが変わったアクタータブの「読み直しが要る」印（純粋なロジック）。
///
/// <para>
/// 【なぜ要るか】
/// 書き戻しはランタイムがファイルを書くので、そのファイルを開いているアクタータブの中身（世界線）は古い版のまま残る。
/// Play 中はタブを表示できないので、Edit へ戻って次にそのタブを表示するとき（または停止時に表示中なら停止時）に読み直す。
/// </para>
/// <para>
/// 【規則】（2026-10-03 の 2 回目のレビュー #14 で直した。テスト: editor/tests/PrefabPlayReapplyTests の StaleActorTabTests）
///   - 印はタブのパス（<c>ActorTab.Path</c>）ごとに 1 つ（区切りの違い・大文字小文字は同一視）
///   - タブを閉じたとき・同じファイルを新しいタブで開いたときは印を消す（<see cref="Forget"/>）。
///     以前は印が残り、閉じた後に開き直して編集したタブを、次に表示したとき確認なしで読み直して編集と Undo を消していた
///   - 表示するとき（<see cref="TakeOnShow"/>）: 印が無い・Edit でない → そのまま／未保存の編集なし → 読み直す／
///     未保存の編集あり → 読み直す前に確かめる。印は Edit で表示した時点で消える（答えに関わらず、聞くのは 1 回だけ）
///   - 未保存の判定はタブ単位の印が無いので、呼び出し側がエディタ全体の未保存の印（シーンとタブで共通の <c>_isDirty</c>）で代用する。
///     シーンだけに未保存の編集があるときも確かめる（安全側。読み直してもシーンの編集は消えないが、区別できない）
/// </para>
/// WPF・ランタイム非依存（単体テストのため）。
/// </summary>
public sealed class StaleActorTabs
{
    /// <summary>印の付いたタブのパス（区切りを揃えた鍵。大文字小文字は無視）。</summary>
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>印の付いたタブの数。</summary>
    public int Count => _keys.Count;

    /// <summary>タブに「読み直しが要る」印を付ける。</summary>
    /// <param name="tabPath">アクタータブのパス（ファイルの絶対パス）。</param>
    /// <returns>新しく付けたら true（空のパス・既に付いているときは false）。</returns>
    public bool Mark(string tabPath)
    {
        if (string.IsNullOrWhiteSpace(tabPath)) return false;
        return _keys.Add(Key(tabPath));
    }

    /// <summary>
    /// タブの印を消す（タブを閉じたとき・同じファイルを新しいタブで開いたとき。新しいタブはファイルから読むので読み直しは要らない）。
    /// </summary>
    /// <param name="tabPath">アクタータブのパス。</param>
    /// <returns>印が付いていて消したら true。</returns>
    public bool Forget(string tabPath)
        => !string.IsNullOrWhiteSpace(tabPath) && _keys.Remove(Key(tabPath));

    /// <summary>タブに印が付いているか。</summary>
    /// <param name="tabPath">アクタータブのパス。</param>
    public bool IsMarked(string tabPath)
        => !string.IsNullOrWhiteSpace(tabPath) && _keys.Contains(Key(tabPath));

    /// <summary>
    /// タブを表示するときにすることを決める。読み直せるとき（Edit）は印を消す（聞くのは 1 回だけ）。
    /// </summary>
    /// <param name="tabPath">表示するアクタータブのパス。</param>
    /// <param name="canReloadNow">今読み直せるか（Edit 中でタブが実在する。Play 中はタブを表示できないので印を残す）。</param>
    /// <param name="hasUnsavedEdits">
    /// そのタブに未保存の編集があるか。タブ単位の判定が無いので、呼び出し側はエディタ全体の未保存の印で代用する。
    /// </param>
    public StaleActorTabShowAction TakeOnShow(string tabPath, bool canReloadNow, bool hasUnsavedEdits)
    {
        if (!canReloadNow) return StaleActorTabShowAction.ShowAsIs;
        if (!Forget(tabPath)) return StaleActorTabShowAction.ShowAsIs;
        return hasUnsavedEdits ? StaleActorTabShowAction.AskBeforeReload : StaleActorTabShowAction.Reload;
    }

    /// <summary>鍵（区切りを '/' に揃え、前後の空白を落とす。大文字小文字は HashSet 側で無視する）。</summary>
    private static string Key(string path) => path.Trim().Replace('\\', '/');
}
