using System.Diagnostics.CodeAnalysis;
using SEED.Scripting;
using SEEDEditor.Scripting;

namespace SEED;

// ============================================================
//  GameObject.Scripts.cs — アクタに付いているスクリプトのインスタンスを型で引く（2026-10-03）
//
//  Unity の GetComponent<MyScript>() / GetComponentInChildren / GetComponentInParent に当たる。
//  元は CLR 側のスクリプトの登録簿（SEED.Scripting.ScriptRegistry）。ランタイムは変えていない。
//  規則の正典は docs/scripting_api.md §7「スクリプトを引く」:
//    - 並びはスロットの順（インスタンスができた順。シーンの読み込み・Instantiate・AddScript はスロットの順に作る）
//    - 派生型も当たる（GetScript<Enemy>() は Boss : Enemy も返す）
//    - まだ OnStart を迎えていないスクリプトも引ける（同じ型の名前が 1 つのアクタに複数あるときは先頭の 1 つだけ）
//    - Destroy() してもフレーム末尾（OnDestroy の後）までは引ける
//  スクリプトを外す RemoveScript<T>() はまだ無い（ランタイムのスロット削除が要る。docs/backlog.md）。
// ============================================================

public readonly partial struct GameObject
{
    /// <summary>
    /// このアクタのスクリプトのうち、<typeparamref name="T"/> に当たる最初のもの（スロットの順）。無ければ null。
    ///
    /// 派生型も当たる。まだ OnStart を迎えていないスクリプト（同じフレームで後から OnStart を迎えるもの・
    /// <see cref="AddScript{T}"/> でできて次のフレームを待つもの・非アクティブで一度も動いていないもの）も返す。
    /// Destroy() 済みでもフレーム末尾の破棄（OnDestroy の後）までは返す。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（SEEDScript の派生。基底の型を渡すと派生も当たる）。</typeparam>
    public T? GetScript<T>() where T : SEEDScript => ScriptRegistry.FirstOn<T>(_entity);

    /// <summary>
    /// このアクタのスクリプトのうち、<typeparamref name="T"/> に当たるもの全部（スロットの順・その時点の写し）。
    /// 無ければ空の配列。規則は <see cref="GetScript{T}"/> と同じ。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（SEEDScript の派生。基底の型を渡すと派生も当たる）。</typeparam>
    public T[] GetScripts<T>() where T : SEEDScript => ScriptRegistry.AllOn<T>(_entity);

    /// <summary>
    /// このアクタのスクリプトを型を問わず全部（スロットの順・その時点の写し）。無ければ空の配列。
    /// 規則は <see cref="GetScript{T}"/> と同じ。
    /// </summary>
    public IScriptComponent[] GetScripts() => ScriptRegistry.AllOn<IScriptComponent>(_entity);

    /// <summary>このアクタが <typeparamref name="T"/> に当たるスクリプトを持つか（<see cref="GetScript{T}"/> が null でないか）。</summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    public bool HasScript<T>() where T : SEEDScript => GetScript<T>() is not null;

    /// <summary>
    /// <see cref="GetScript{T}"/> の Try 版。引けたら true と、そのスクリプト。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="script">引けたスクリプト（false のときは null）。</param>
    public bool TryGetScript<T>([MaybeNullWhen(false)] out T script) where T : SEEDScript
    {
        script = GetScript<T>();
        return script is not null;
    }

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 子孫を行きがけ順（自分 → 子 0 の部分木 → 子 1 の部分木 …）にたどり、
    /// <typeparamref name="T"/> に当たる最初のスクリプトを返す。無ければ null。
    ///
    /// 子は <see cref="Children"/> と同じ論理の子（フォルダは透過）。木はフレームの始めの木
    /// （同じフレームに Create / Instantiate / SetParent したものはまだ入らない）。1 段ごとにランタイムへ子を問い合わせるので、
    /// 大きな部分木で毎フレーム呼ぶ用途には向かない（OnStart で引いてフィールドに持つ）。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="includeSelf">自分のスクリプトも見るか（既定 true。false なら子孫だけ）。</param>
    public T? GetScriptInChildren<T>(bool includeSelf = true) where T : SEEDScript
        => ScriptHierarchySearch.InChildren<T>(_entity, includeSelf);

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 親 → 親の親 … とたどり、<typeparamref name="T"/> に当たる最初のスクリプトを返す。
    /// 無ければ null。親は <see cref="Parent"/> と同じ（フレームの始めの木）。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="includeSelf">自分のスクリプトも見るか（既定 true。false なら祖先だけ）。</param>
    public T? GetScriptInParent<T>(bool includeSelf = true) where T : SEEDScript
        => ScriptHierarchySearch.InParent<T>(_entity, includeSelf);
}
