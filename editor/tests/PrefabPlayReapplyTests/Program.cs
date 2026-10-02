using System;
using SEEDEditor.Reload;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace PrefabPlayReapplyTests;

/// <summary>
/// Play 中に変わったプレハブの待ち行列（<see cref="PrefabPlayReapplyQueue"/>）の判定テスト。
///
/// <para>
/// Play 中にプレハブを保存（または Play 中の変更を書き戻し）すると、Play のインスタンスは状態を保ったまま
/// 当て直される（PREFAB_LIVE_PATCH_PATH）が、Play を止めるとワールドは Play 前の写しへ戻る。
/// Edit のシーンのインスタンスは古い版のままになるので、停止後に PREFAB_REAPPLY_PATH を送って揃える。
/// ここでは「いつ何を送り、何を覚え、停止で何をするか」を固定する。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>テストに使うプレハブの絶対パス。</summary>
    private const string CardPath = @"C:\proj\assets\ui\Card.actor";

    /// <summary>同じファイルを区切りと大文字小文字だけ変えた書き方。</summary>
    private const string CardPathOtherSpelling = "c:/PROJ/assets/ui/card.actor";

    /// <summary>別のプレハブの絶対パス。</summary>
    private const string ScreenPath = @"C:\proj\assets\ui\Screen.actor";

    public static int Main()
    {
        var h = new TestHarness();

        // ── 保存したとき ─────────────────────────────────────────
        h.Add("保存: Edit・設定オンは今すぐ再展開", () =>
            Check.Equal(PrefabSaveAction.ReapplyNow,
                PrefabPlayReapplyQueue.DecideOnSave(PlaybackState.Edit, autoPropagate: true),
                "従来どおり PREFAB_REAPPLY_PATH"));
        h.Add("保存: Edit・設定オフは何もしない", () =>
            Check.Equal(PrefabSaveAction.None,
                PrefabPlayReapplyQueue.DecideOnSave(PlaybackState.Edit, autoPropagate: false),
                "手動更新のみ（従来どおり）"));
        h.Add("保存: Play は当て直して覚える", () =>
            Check.Equal(PrefabSaveAction.LivePatchAndRemember,
                PrefabPlayReapplyQueue.DecideOnSave(PlaybackState.Play, autoPropagate: true),
                "Play 中は丸ごとの再展開をしない（OnStart が走り直すため）"));
        h.Add("保存: Pause も Play と同じ", () =>
            Check.Equal(PrefabSaveAction.LivePatchAndRemember,
                PrefabPlayReapplyQueue.DecideOnSave(PlaybackState.Pause, autoPropagate: true),
                "Pause もワールドは Play のまま"));
        h.Add("保存: Play・設定オフでも当て直す", () =>
            Check.Equal(PrefabSaveAction.LivePatchAndRemember,
                PrefabPlayReapplyQueue.DecideOnSave(PlaybackState.Play, autoPropagate: false),
                "当て直しは Play の表示だけ（停止で消える）なので設定に関わらない"));

        // ── 覚える ───────────────────────────────────────────────
        h.Add("覚える: Edit 中は覚えない", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            Check.True(!q.Remember(CardPath, PlaybackState.Edit), "Edit はその場で反映済み");
            Check.Equal(0, q.Count, "空のまま");
        });
        h.Add("覚える: Play と Pause で覚える", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            Check.True(q.Remember(CardPath, PlaybackState.Play), "Play で覚える");
            Check.True(q.Remember(ScreenPath, PlaybackState.Pause), "Pause でも覚える");
            Check.Equal(2, q.Count, "2 本");
        });
        h.Add("覚える: 同じファイルは 1 回（区切り・大文字小文字の違いは同一視）", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            Check.True(q.Remember(CardPath, PlaybackState.Play), "1 回目は覚える");
            Check.True(!q.Remember(CardPath, PlaybackState.Play), "2 回目は覚えない");
            Check.True(!q.Remember(CardPathOtherSpelling, PlaybackState.Play), "書き方違いも同じファイル");
            Check.Equal(1, q.Count, "1 本だけ");
        });
        h.Add("覚える: 空のパスは覚えない", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            Check.True(!q.Remember("  ", PlaybackState.Play), "空白だけのパス");
            Check.Equal(0, q.Count, "空のまま");
        });

        // ── 停止したとき ─────────────────────────────────────────
        h.Add("停止: 設定オンなら覚えた順に再展開し、空になる", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            q.Remember(ScreenPath, PlaybackState.Play);
            q.Remember(CardPath, PlaybackState.Play);
            var plan = q.TakeOnReturnToEdit(autoPropagate: true);
            Check.Equal(2, plan.ReapplyPaths.Count, "2 本を再展開");
            Check.Equal(ScreenPath, plan.ReapplyPaths[0], "覚えた順");
            Check.Equal(CardPath, plan.ReapplyPaths[1], "覚えた順");
            Check.True(!plan.RequestStatus, "問い合わせは不要");
            Check.Equal(0, q.Count, "次の Play へ持ち越さない");
        });
        h.Add("停止: 設定オフなら再展開せず版ずれの問い合わせだけ", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            q.Remember(CardPath, PlaybackState.Play);
            var plan = q.TakeOnReturnToEdit(autoPropagate: false);
            Check.Equal(0, plan.ReapplyPaths.Count, "シーンには触れない");
            Check.True(plan.RequestStatus, "バナーで知らせる");
            Check.Equal(1, plan.ChangedPaths.Count, "画面プレビューの作り直し用に変わったパスは返す");
            Check.Equal(0, q.Count, "空になる");
        });
        h.Add("停止: 何も覚えていなければ何もしない", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            var plan = q.TakeOnReturnToEdit(autoPropagate: true);
            Check.Equal(0, plan.ReapplyPaths.Count, "再展開なし");
            Check.True(!plan.RequestStatus, "問い合わせもしない");
        });
        h.Add("停止: 2 回目の停止では何もしない（1 回で消化する）", () =>
        {
            var q = new PrefabPlayReapplyQueue();
            q.Remember(CardPath, PlaybackState.Play);
            q.TakeOnReturnToEdit(autoPropagate: true);
            var again = q.TakeOnReturnToEdit(autoPropagate: true);
            Check.Equal(0, again.ReapplyPaths.Count, "消化済み");
        });

        return h.Run();
    }
}
