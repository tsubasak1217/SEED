// ============================================================================
//  IMission.cs
//  ミッション 1 種類ぶんの「判定」の共通インターフェース。
// ============================================================================

using System.Collections.Generic;

/// <summary>
/// ミッション 1 種類ぶんの判定【ミッションの振る舞いの共通口】。
///
/// 【責務】
/// 「いま何をすればよいか（進捗・サブ目標）」と「達成したか」を答えること、
/// および達成のために必要な台本（魚を出す・漂流物を流す）を仕込むこと。
///
/// 表示（パネル・吹き出し・バナー）と進行（次のミッションへ送る）は
/// <see cref="TutorialDirector"/> と <see cref="MissionPanel"/> の責務で、
/// 実装クラスは一切触らない（単一責任）。
///
/// 【寿命】
/// <see cref="Begin"/> → <see cref="Update"/> を毎フレーム → <see cref="End"/> の 1 回きり。
/// <see cref="Begin"/> で張ったイベント購読は<b>必ず</b> <see cref="End"/> で外すこと
/// （ミッションは SEEDScript ではないので自動解除が効かない）。
///
/// 【時間軸】
/// <see cref="Update"/> には実時間（Time.UnscaledDeltaTime）が渡る。
/// 説明の吹き出しでゲーム時間が止まっていても、ミッション側のタイマは動かしたいため。
/// </summary>
public interface IMission
{
    /// <summary>このミッションの種類（データとの対応確認・ログ用）。</summary>
    MissionKind Kind { get; }

    /// <summary>
    /// ミッションを開始する【開始処理の唯一の入口】。
    /// 台本の仕込み・サブ目標の作成・イベント購読はここで行う。
    /// </summary>
    /// <param name="ctx">ミッションから触れてよい周辺への窓口。</param>
    void Begin(MissionContext ctx);

    /// <summary>
    /// 毎フレームの判定。達成したら <see cref="IsCleared"/> を true にする。
    /// </summary>
    /// <param name="ctx">ミッションから触れてよい周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    void Update(MissionContext ctx, float unscaledDelta);

    /// <summary>
    /// ミッションを終える【後始末の唯一の出口】。
    /// イベント購読の解除・生成した目印の破棄はここで必ず行う。
    /// 達成で終わるときも、チュートリアルごと打ち切られるときも呼ばれる。
    /// </summary>
    /// <param name="ctx">ミッションから触れてよい周辺への窓口。</param>
    void End(MissionContext ctx);

    /// <summary>
    /// 失敗して途中からやり直すときの再開処理。
    /// 失敗の概念が無いミッションは何もしなくてよい。
    /// </summary>
    /// <param name="ctx">ミッションから触れてよい周辺への窓口。</param>
    void Restart(MissionContext ctx);

    /// <summary>達成したか（true になった次のフレームにディレクタがクリア演出へ進む）。</summary>
    bool IsCleared { get; }

    /// <summary>
    /// パネル下段に出す進捗の 1 行（例「3 匹目 / 5 匹」）。
    /// 進捗を出さないミッションは空文字を返す。
    /// </summary>
    string ProgressText { get; }

    /// <summary>
    /// パネルに並べるサブ目標のチェック（無ければ空のリスト）。
    /// 中身はミッションが書き換え、パネルは読むだけ。
    /// </summary>
    IReadOnlyList<MissionObjective> Objectives { get; }
}
