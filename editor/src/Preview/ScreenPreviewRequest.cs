// ============================================================
//  ScreenPreviewRequest.cs — プレビューを作る命令（PREVIEW_PREFAB）の中身
//
//  【役割】
//  ランタイムへ送る PREVIEW_PREFAB:{world_line},{parent_dfs},{json} の json 部分を表す値。
//  欄の意味はランタイムの PreviewPrefabRequest（runtime/src/engine/core/app_base/ipc.rs）と同じ。
//  正典は docs/editor_screen_preview.md §8。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

namespace SEEDEditor.Preview;

/// <summary>
/// プレビューを作る命令の中身（PREVIEW_PREFAB の json）。
/// </summary>
/// <param name="Prefab">中身のプレハブ（assets:// 仮想パス or 絶対パス）。必須。</param>
/// <param name="Under">親の下で差し込む子のパス（'/' 区切りの名前。空 = 親の直下）。</param>
/// <param name="Frame">枠のプレハブ（ScreenStack の screen_frame.actor 等。null = 枠なし）。</param>
/// <param name="FrameBody">枠の中で中身を入れる子のパス（'/' 区切りの名前。空 = 枠の直下）。</param>
/// <param name="LayerBias">根のレイヤーの底上げ（0 = 付けない）。</param>
public sealed record ScreenPreviewRequest(
    string Prefab,
    string Under = "",
    string? Frame = null,
    string FrameBody = "",
    int LayerBias = 0);
