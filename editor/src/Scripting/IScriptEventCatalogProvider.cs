using System;
using System.Collections.Generic;

namespace SEEDEditor.Scripting;

/// <summary>
/// ScriptEvent の UI（<see cref="ScriptEventFieldBuilder"/>）が結線先の候補を問い合わせる窓口。
///
/// 【なぜ抽象化するか】
/// 候補の算出には「アクタ名 → DFS ID」（Hierarchy）と
/// 「DFS ID → コンポーネント構成」（GET_ACTOR_COMPONENTS の IPC）が要る。
/// どちらもインスペクタ（<c>InspectorPanel</c>）だけが持つ機能なので、
/// 参照ピッカーの <c>IReferenceDropResolver</c> と同じ形で UI 側から切り離す。
///
/// 【非同期の契約】
/// 実装は IPC 往復とスクリプトのコンパイル（数百 ms 級）を伴い得るため、必ず非同期。
/// コールバックは **UI スレッド** で呼ぶこと（呼び出し側は WPF コントロールを直接触る）。
/// 解決できなかった場合は空リストで呼び戻す（呼ばないままにしない）。
/// ただし呼び出し側は「コールバックが来ないかもしれない」前提でも壊れない作りにしてある
/// （候補が埋まらないだけで、現在値は保持される）。
/// </summary>
public interface IScriptEventCatalogProvider
{
    /// <summary>
    /// アクタ名から、そのアクタに付いている ScriptComponent のスクリプト型名一覧を取得する。
    /// </summary>
    /// <param name="actorName">対象アクタ名（バインディングに保存されている値）。</param>
    /// <param name="onReady">結果を受け取るコールバック（UI スレッドで呼ばれる）。</param>
    void RequestScriptTypes(string actorName, Action<IReadOnlyList<string>> onReady);

    /// <summary>
    /// アクタ名とスクリプト型名から、結線先にできるメソッドの一覧を取得する。
    /// </summary>
    /// <param name="actorName">対象アクタ名。</param>
    /// <param name="scriptTypeName">スクリプト型名（名前空間なし）。</param>
    /// <param name="onReady">結果を受け取るコールバック（UI スレッドで呼ばれる）。</param>
    void RequestMethods(
        string actorName, string scriptTypeName, Action<IReadOnlyList<ScriptEventMethod>> onReady);
}
