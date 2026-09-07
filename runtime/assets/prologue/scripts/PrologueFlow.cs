// ============================================================================
//  PrologueFlow.cs
//  プロローグシーンの進行（会話終了 → チュートリアルへ）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// プロローグシーンの進行スクリプト。
///
/// 【責務】
///  - 会話が終わったら本編（mainGame）シーンへ遷移する。
///
/// 遷移の合図は自分では判断しない。会話の進行は DialogueDirector が持ち、
/// その「会話終了時」イベント（ScriptEvent）から <see cref="GoToMainGame"/> が
/// 呼ばれる。フェード演出とシーン切替の手順は SceneFlow が持つ（単一責任）。
///
/// 【シーン側の設定】
///  - 同じアクター（Flow）に SceneFlow と本スクリプトを付ける。
///  - 参照フィールド sceneFlow に、そのアクター自身を指定する。
///  - DialogueDirector の「会話終了時」に
///    Flow / PrologueFlow / GoToMainGame（引数なし）を結線する。
/// </summary>
public class PrologueFlow : SEEDScript
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>
    /// プロローグの次に進むシーン名（プロジェクト設定の登録名）。
    /// チュートリアルは専用シーンではなく本編（mainGame）内のチュートリアルモードとして
    /// 動くようになったため、プロローグからは直接本編へ入る。
    /// </summary>
    private const string SceneNameMainGame = "mainGame";

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>遷移演出を担う SceneFlow（同じアクターに付いているものを指定する）。</summary>
    [SerializeField(Label = "シーン遷移", Tooltip = "同じアクターに付けた SceneFlow を指定する")]
    public SceneFlow? sceneFlow;

    // ── 公開メソッド（ScriptEvent から呼べる）───────────────

    /// <summary>
    /// 本編（mainGame）シーンへ遷移する。
    /// DialogueDirector の「会話終了時」イベントから呼ばれる想定。
    /// チュートリアルは本編シーン内の TutorialDirector が担当する。
    /// </summary>
    public void GoToMainGame()
    {
        // 参照未設定・解決失敗時は必ず null になるためチェックする
        if (sceneFlow is null)
        {
            SEED.Debug.LogWarning("[PrologueFlow] sceneFlow が未設定のため遷移できません。");
            return;
        }

        // 遷移演出中の重複要求は無視する（二重遷移の防止）
        if (sceneFlow.IsTransitioning) return;

        sceneFlow.GoTo(SceneNameMainGame);
    }
}
