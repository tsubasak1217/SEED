// ============================================================================
//  PrologueFlow.cs
//  プロローグシーンの進行（決定入力 → チュートリアルへ）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// プロローグシーンの進行スクリプト。
///
/// 【責務】
///  - 決定入力を待ち、チュートリアルシーンへ遷移する。
///
/// フェード演出とシーン切替の手順は SceneFlow が持つ（単一責任）。
///
/// 【シーン側の設定】
///  - 同じアクターに SceneFlow と本スクリプトを付ける。
///  - 参照フィールド sceneFlow に、そのアクター自身を指定する。
/// </summary>
public class PrologueFlow : SEEDScript
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>プロローグの次に進むシーン名（プロジェクト設定の登録名）。</summary>
    private const string SceneNameTutorial = "tutorial";

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>遷移演出を担う SceneFlow（同じアクターに付いているものを指定する）。</summary>
    [SerializeField(Label = "シーン遷移", Tooltip = "同じアクターに付けた SceneFlow を指定する")]
    public SceneFlow? sceneFlow;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 毎フレーム、決定入力を監視して遷移を発行する。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 参照未設定・解決失敗時は必ず null になるため毎回チェックする
        if (sceneFlow is null) return;

        // 遷移演出中の入力は無視する（二重遷移の防止）
        if (sceneFlow.IsTransitioning) return;

        // 決定入力でチュートリアルへ
        if (!SceneFlow.IsConfirmPressed()) return;
        sceneFlow.GoTo(SceneNameTutorial);
    }
}
