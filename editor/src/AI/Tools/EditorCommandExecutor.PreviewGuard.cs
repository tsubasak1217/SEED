// ============================================================
//  EditorCommandExecutor.PreviewGuard.cs — AI の編集ツールをプレビューの中へ通さない（EditorCommandExecutor の partial 実装）
//
//  【なぜ要るか】（docs/reviews/2026-10-02_code_review.md の #3）
//  エディタの画面プレビュー（保存されない表示用のアクタ。docs/editor_screen_preview.md）の中を AI が編集すると、
//  以前は普通の編集として成功し、保存・作り直し・Play のどれかで黙って消えていた。2026-10-03 からランタイムが
//  プレビューの中の AI_SET_VALUE / AI_ADD_COMPONENT / AI_MOVE_ACTOR / AI_REMOVE_ACTOR を PREVIEW_ERROR で断る
//  （editor_preview/guard.rs）が、AI のツールは送ったら応答を待たずに「設定しました」と返すので、AI には成功に見える。
//  そこで送る前にヒエラルキーのノードモデルでプレビューの中かを確かめ、中ならエラーをツールの結果として返す
//  （ランタイムの拒否は MCP の seed_send_ipc など、ここを通らない経路の守り）。
// ============================================================

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    /// <summary>プレビューの中を編集しようとしたときのツールの結果（書式: DFS ID, 操作の名前）。</summary>
    private const string PreviewEditRefusedFormat =
        "エラー: DFS ID={0} はエディタの画面プレビュー（保存されない表示用のアクタ）の中なので{1}できません。"
        + "中身を変えるときは元のプレハブ（.actor）を編集してください（seed_find_actor の is_preview で前もって分かります）。";

    /// <summary>
    /// 対象のアクタがプレビューの中なら、断るツールの結果を返す（中でなければ null＝続けてよい）。
    /// ヒエラルキーへ未接続（null）のときは判定できないので通す（ランタイムが断る）。
    /// </summary>
    /// <param name="actorDfsId">対象のアクタの DFS ID。</param>
    /// <param name="action">操作の名前（「値を設定」など。結果の文に入る）。</param>
    private static string? RefuseIfInsidePreview(int actorDfsId, string action)
        => SEEDEditor.Panels.ActorRefJump.ActorIsPreviewByDfsId?.Invoke(actorDfsId) == true
            ? string.Format(PreviewEditRefusedFormat, actorDfsId, action)
            : null;
}
