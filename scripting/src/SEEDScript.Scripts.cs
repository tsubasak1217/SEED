using System.Diagnostics.CodeAnalysis;

namespace SEEDEditor.Scripting;

// ============================================================
//  SEEDScript.Scripts.cs — 他のスクリプトのインスタンスを型で引く（2026-10-03。docs/scripting_api.md §7「スクリプトを引く」）
//
//  インスタンスのメソッド（GetScript 系）は自分の gameObject の同名のメソッドの短縮。gameObject / transform と同じく
//  protected（自分のアクタを引くためのもの。他のアクタは GameObject の public の同名のメソッドで引く）。
//  static の Instances<T>() / FindInstance<T>() はシーン全体（OnStart を迎えたインスタンス）から引く。
//  元は CLR 側のスクリプトの登録簿（SEED.Scripting.ScriptRegistry）。
// ============================================================

public abstract partial class SEEDScript
{
    // ── 自分のアクタから引く（gameObject.GetScript 系の短縮）────────────

    /// <summary>
    /// 自分のアクタのスクリプトのうち、<typeparamref name="T"/> に当たる最初のもの（スロットの順）。無ければ null。
    /// <c>gameObject.GetScript&lt;T&gt;()</c> の短縮（規則は <see cref="SEED.GameObject.GetScript{T}"/>）。
    /// 自分自身も対象（<c>GetScript&lt;自分の型&gt;()</c> は自分を返しうる）。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    protected T? GetScript<T>() where T : SEEDScript => gameObject.GetScript<T>();

    /// <summary>自分のアクタのスクリプトのうち、<typeparamref name="T"/> に当たるもの全部（スロットの順・写し）。</summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    protected T[] GetScripts<T>() where T : SEEDScript => gameObject.GetScripts<T>();

    /// <summary>自分のアクタのスクリプトを型を問わず全部（スロットの順・写し。自分自身も入る）。</summary>
    protected IScriptComponent[] GetScripts() => gameObject.GetScripts();

    /// <summary>自分のアクタが <typeparamref name="T"/> に当たるスクリプトを持つか。</summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    protected bool HasScript<T>() where T : SEEDScript => gameObject.HasScript<T>();

    /// <summary><see cref="GetScript{T}"/> の Try 版。引けたら true と、そのスクリプト。</summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="script">引けたスクリプト（false のときは null）。</param>
    protected bool TryGetScript<T>([MaybeNullWhen(false)] out T script) where T : SEEDScript
        => gameObject.TryGetScript(out script);

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 子孫を行きがけ順にたどり、<typeparamref name="T"/> に当たる最初のスクリプト。
    /// <c>gameObject.GetScriptInChildren&lt;T&gt;()</c> の短縮。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="includeSelf">自分のアクタのスクリプトも見るか（既定 true）。</param>
    protected T? GetScriptInChildren<T>(bool includeSelf = true) where T : SEEDScript
        => gameObject.GetScriptInChildren<T>(includeSelf);

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 親 → 親の親 … とたどり、<typeparamref name="T"/> に当たる最初のスクリプト。
    /// <c>gameObject.GetScriptInParent&lt;T&gt;()</c> の短縮。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    /// <param name="includeSelf">自分のアクタのスクリプトも見るか（既定 true）。</param>
    protected T? GetScriptInParent<T>(bool includeSelf = true) where T : SEEDScript
        => gameObject.GetScriptInParent<T>(includeSelf);

    // ── シーン全体から引く（static）────────────────────────────

    /// <summary>
    /// 生存中の <typeparamref name="T"/> のインスタンス全部（生成順・その時点の写し）。無ければ空の配列。
    ///
    /// 載るのは OnStart を迎えた（迎えている最中の）インスタンスで、OnDestroy の後（OnStart 前の破棄は DestroyComponent）に外れる。
    /// 同じフレームに OnStart を迎えるスクリプト同士では、先に OnStart したものから後のものは見えない（全員がそろうのは
    /// 最初のフレームの BeginFrame の後）。アクタ編集タブで開いているだけのアクタのスクリプトは OnStart が走らないので載らない。
    /// 毎回配列を作る（O(n) の写し）ので、毎フレーム大量に呼ぶ用途には向かない。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    public static T[] Instances<T>() where T : SEEDScript => SEED.Scripting.ScriptRegistry.AllInstances<T>();

    /// <summary>
    /// 生存中の <typeparamref name="T"/> のインスタンスのうち最初のもの（生成順。シングルトン的な用途）。無ければ null。
    /// 載る規則は <see cref="Instances{T}"/> と同じ。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（派生型も当たる）。</typeparam>
    public static T? FindInstance<T>() where T : SEEDScript => SEED.Scripting.ScriptRegistry.FirstInstance<T>();
}
