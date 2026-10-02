using System.Collections.Generic;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 2 回目のレビュー（docs/reviews/2026-10-03_code_review.md）の #20〜#24・#31 の SEED.UI の画面の組み立ての直しの再現テスト（2026-10-03。lane3。L3-7）。
/// 直した規則はエンジンの部品（ModalHost・ScreenStack）から純粋な計算へ分けたので、ここではその規則を確かめる:
/// 作りかけの面の帳面（受け取りの上限・取りやめた鍵・全部閉じるの再入。ModalOpeningBook）、全部閉じる・取りやめの結果（手札の型で決める。
/// ModalCloseOrder.ResultForHandle）、渡された中身の受け付けと手放し方（SuppliedContentRules）、使い回す作り置きへ戻すときの
/// OnScreenExit（ScreenContentPlan.NotifiesExit・PrewarmSlot.KeepsContentOnReturn）。docs/ui_navigation.md §2.8・§3.1。
/// </summary>
public static class NavigationReviewTests
{
    /// <summary>テストの作り置きのプレハブ。</summary>
    private const string EditPrefab = "assets://alarm/prefabs/edit.actor";

    /// <summary>上限（ModalOpeningBook.MaxClaimWaitFrames）の短い名前。</summary>
    private const int MaxClaim = ModalOpeningBook<int, string>.MaxClaimWaitFrames;

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        RegisterOpenings(h);
        RegisterCloseResult(h);
        RegisterSupplied(h);
        RegisterReuseExit(h);
    }

    // ── #20・#31: 作りかけの面の帳面（ModalOpeningBook）──────────────────

    /// <summary>作りかけの面の帳面のテストを足す。</summary>
    private static void RegisterOpenings(TestHarness h)
    {
        h.Add("作りかけの面: 受け取られないまま上限のフレーム数が過ぎたら取りやめて返し、数から外れる（戻るを飲み込み続けない。レビュー #20）", () =>
        {
            var book = new ModalOpeningBook<int, string>();
            book.Add(1, ModalKind.Overlay, "popup.actor が無い");
            for (int i = 1; i < MaxClaim; i++)
                Check.Equal(0, book.Tick().Count, $"上限の前（{i} フレーム）は取りやめない");
            Check.Equal(1, book.CountOf(ModalKind.Overlay), "上限の前は作りかけのまま（開いた直後の二度押しの戻るは受けて捨てる）");
            var expired = book.Tick();
            Check.Equal(1, expired.Count, "上限のフレームで取りやめる");
            Check.Equal("popup.actor が無い", expired[0].Claim, "開く約束を返す（呼び手が手札を閉じる）");
            Check.Equal(ModalKind.Overlay, expired[0].Kind, "開いた種類");
            Check.Equal(0, book.CountOf(ModalKind.Overlay), "作りかけの数から外れる（その種類の戻るが次の層へ届く）");
            Check.Equal(0, book.Count, "帳面は空");
            Check.Equal(0, book.Tick().Count, "2 度は返さない");
            Check.True(book.TakeCancelled(1), "後で面のスクリプトが始まっても黙って消える");
            Check.True(!book.TakeCancelled(1), "答えるのは 1 回だけ");
        });

        h.Add("作りかけの面: 上限の前に受け取れば取りやめない・受け取った面は数から外れる・種類ごとに数える", () =>
        {
            var book = new ModalOpeningBook<int, string>();
            book.Add(1, ModalKind.Dialog, "確認");
            book.Add(2, ModalKind.Sheet, "シート");
            book.Tick();
            Check.Equal(1, book.CountOf(ModalKind.Dialog), "ダイアログの作りかけ");
            Check.Equal(1, book.CountOf(ModalKind.Sheet), "シートの作りかけ");
            Check.True(book.TryClaim(1, out var claim) && claim == "確認", "面のスクリプトが受け取る");
            Check.Equal(0, book.CountOf(ModalKind.Dialog), "受け取ったら数から外れる（別の数を減らさなくてよい）");
            Check.True(!book.TryClaim(1, out _), "2 度は受け取れない");
            Check.True(book.TryClaim(2, out _), "シートも受け取る");
            for (int i = 0; i < MaxClaim * 2; i++)
                Check.Equal(0, book.Tick().Count, "受け取った面は取りやめない");
            Check.True(!book.TakeCancelled(1), "受け取った面は取りやめた面ではない");
            Check.True(!book.HasWork, "数えるものは残らない");
        });

        h.Add("作りかけの面: 取りやめた鍵は面のスクリプトが始まらないまま上限が過ぎたら忘れる（根を消した面の鍵が溜まらない）", () =>
        {
            var book = new ModalOpeningBook<int, string>();
            book.Add(1, ModalKind.Sheet, "シート");
            Check.Equal(1, book.CancelAll(null).Count, "全部閉じるで取りやめる");
            Check.Equal(1, book.CancelledCount, "取りやめた鍵を覚える");
            for (int i = 1; i < MaxClaim; i++) book.Tick();
            Check.Equal(1, book.CancelledCount, "上限の前は覚えている");
            book.Tick();
            Check.Equal(0, book.CancelledCount, "上限で忘れる");
            Check.True(!book.TakeCancelled(1), "忘れた鍵は答えない");
            Check.True(!book.HasWork, "数えるものは残らない");
        });

        h.Add("作りかけの面: 全部閉じるは閉じる順に返し、先に帳面から外す（知らせの中で呼び直しても二重に取りやめない・知らせの中で開いた面は残る。レビュー #31）", () =>
        {
            var book = new ModalOpeningBook<int, string>();
            book.Add(1, ModalKind.Overlay, "覆い1");
            book.Add(2, ModalKind.Dialog, "確認");
            book.Add(3, ModalKind.Overlay, "覆い2");
            book.Add(4, ModalKind.Sheet, "シート");
            var cancelled = book.CancelAll(null);
            var names = new List<string>();
            foreach (var opening in cancelled)
            {
                names.Add(opening.Claim);
                // 1 件目の手札の Closed の中: 全部閉じるを呼び直し（取りやめ済みの面は扱わない）、新しい面を開く
                if (names.Count == 1)
                {
                    Check.Equal(0, book.CancelAll(null).Count, "呼び直しは取りやめ済みの面を扱わない（0 件）");
                    book.Add(5, ModalKind.Dialog, "知らせの中で開いた面");
                    Check.Equal(1, book.CountOf(ModalKind.Dialog), "知らせの中で開いた面は新しい作りかけ");
                }
            }
            Check.Equal("確認,シート,覆い2,覆い1", string.Join(",", names), "閉じる順（ダイアログ → シート → 覆い、同じ種類の中は後から開いた面から）");
            Check.Equal(0, book.CountOf(ModalKind.Overlay), "覆いは数から外れる");
            Check.Equal(0, book.CountOf(ModalKind.Sheet), "シートも");
            Check.Equal(1, book.CountOf(ModalKind.Dialog), "外の呼び出しが続いても、知らせの中で開いた面は残る（数は二重に減らない）");
            // 知らせの中でさらに呼び直せば、その面も 1 回だけ取りやめる
            Check.Equal(1, book.CancelAll(null).Count, "新しい面を取りやめる");
            Check.Equal(0, book.CountOf(ModalKind.Dialog), "数は 0（負にならない）");
            Check.True(book.TakeCancelled(5) && book.TakeCancelled(2), "どちらも取りやめた面として答える");
        });

        h.Add("作りかけの面: 全部閉じるの呼び直しは取りやめ済みの面を 0 件として返す・種類を絞れる", () =>
        {
            var book = new ModalOpeningBook<int, string>();
            book.Add(1, ModalKind.Overlay, "覆い");
            book.Add(2, ModalKind.Sheet, "シート");
            Check.Equal(1, book.CancelAll(ModalKind.Sheet).Count, "シートだけ取りやめる");
            Check.Equal(0, book.CancelAll(ModalKind.Sheet).Count, "2 度目は 0 件（同じ面を二度取りやめない）");
            Check.Equal(1, book.CountOf(ModalKind.Overlay), "覆いは残る");
            Check.Equal(1, book.CancelAll(null).Count, "残りを全部");
            Check.Equal(0, book.CancelAll(null).Count, "空なら 0 件");
        });
    }

    // ── #23: 全部閉じる・取りやめの結果（ModalCloseOrder.ResultForHandle）──────

    /// <summary>全部閉じる・取りやめの結果のテストを足す。</summary>
    private static void RegisterCloseResult(TestHarness h)
    {
        h.Add("全部閉じる・取りやめ: 結果は手札の型で決める（DialogHandle は Dismissed・帯が Dialog のポップアップや自前の面の普通の手札は null。レビュー #23）", () =>
        {
            // PopupOptions.Kind = Dialog のポップアップ・ShowPlane(ModalKind.Dialog, …) の自前の面: 帯は Dialog だが手札は普通の ModalHandle
            Check.True(ModalCloseOrder.ResultForHandle(isDialogHandle: false) is null, "普通の手札は null（箱入りの DialogResult を入れない）");
            Check.Equal<object?>(DialogResult.Dismissed, ModalCloseOrder.ResultForHandle(isDialogHandle: true), "ShowDialog の手札は Dismissed（Dismiss と同じ）");
        });
    }

    // ── #21・#22: 渡された中身の受け付けと手放し方（SuppliedContentRules）────────

    /// <summary>渡された中身のテストを足す。</summary>
    private static void RegisterSupplied(TestHarness h)
    {
        h.Add("渡された中身: ほかの段が持っていたら断る（二度押しの Push で 1 段目が空の枠にならない。レビュー #22）", () =>
        {
            Check.Equal(SuppliedAvailability.Free, SuppliedContentRules.CheckSupplied(new SuppliedHolder[0]), "誰も持っていなければ積める");
            Check.Equal(SuppliedAvailability.InUse,
                SuppliedContentRules.CheckSupplied(new[] { new SuppliedHolder(Removed: false, ScreenContentRelease.Destroy) }),
                "スタックに残っている段が持っている（二度押し）→ 断る");
            Check.Equal(SuppliedAvailability.InUse,
                SuppliedContentRules.CheckSupplied(new[] { new SuppliedHolder(Removed: false, ScreenContentRelease.ReturnToParent) }),
                "戻す段でも、スタックに残っていれば断る");
            Check.Equal(SuppliedAvailability.Doomed,
                SuppliedContentRules.CheckSupplied(new[] { new SuppliedHolder(Removed: true, ScreenContentRelease.Destroy) }),
                "外れた段が Destroy で持っている（外れる処理で消える）→ 断る");
            Check.Equal(SuppliedAvailability.Free,
                SuppliedContentRules.CheckSupplied(new[] { new SuppliedHolder(Removed: true, ScreenContentRelease.ReturnToParent) }),
                "外れた段が元の親へ戻す → 積める（下ろす動きの途中にもう一度積む形。外れる処理で戻してから新しい段が枠へ移す）");
            Check.Equal(SuppliedAvailability.InUse,
                SuppliedContentRules.CheckSupplied(new[]
                {
                    new SuppliedHolder(Removed: true, ScreenContentRelease.Destroy),
                    new SuppliedHolder(Removed: false, ScreenContentRelease.ReturnToParent),
                }),
                "使用中が先に立つ");
        });

        h.Add("渡された中身: 外れるとき、元の親があれば戻す・無い／消えたなら根へ移して隠す・もう枠の下に無ければ触らない（レビュー #21・#22）", () =>
        {
            Check.Equal(SuppliedReleaseAction.ReturnToParent,
                SuppliedContentRules.ReleaseAction(ScreenContentRelease.ReturnToParent, underFrame: true, parentAlive: true), "元の親へ戻す");
            Check.Equal(SuppliedReleaseAction.ReturnToRootHidden,
                SuppliedContentRules.ReleaseAction(ScreenContentRelease.ReturnToParent, underFrame: true, parentAlive: false),
                "元の親が無い・消えた → 根へ移して隠す（見えたまま根に出さない・消さない）");
            Check.Equal(SuppliedReleaseAction.LeaveInPlace,
                SuppliedContentRules.ReleaseAction(ScreenContentRelease.ReturnToParent, underFrame: false, parentAlive: true),
                "もう枠の下に無い（アプリが付け替えた）→ 引き戻さない");
            Check.Equal(SuppliedReleaseAction.DestroyWithFrame,
                SuppliedContentRules.ReleaseAction(ScreenContentRelease.Destroy, underFrame: true, parentAlive: true), "Destroy は枠と一緒に消す");
            Check.Equal(SuppliedReleaseAction.LeaveInPlace,
                SuppliedContentRules.ReleaseAction(ScreenContentRelease.Destroy, underFrame: false, parentAlive: false),
                "Destroy でも、もう枠の下に無ければ消さない（枠を消しても巻き込まれない）");
        });
    }

    // ── #24: 使い回す作り置きへ戻すときの OnScreenExit ─────────────────────

    /// <summary>使い回す作り置きの入りと出のテストを足す。</summary>
    private static void RegisterReuseExit(TestHarness h)
    {
        h.Add("作り置き（Reuse）: 覆われて手放すだけ（KeepState = false）でも、作り置きへ戻すなら OnScreenExit を届ける（入りと出が対になる。レビュー #24）", () =>
        {
            // 貸す（Enter）→ 覆われて手放す（作り置きへ戻す）→ もう一度貸す（Enter）の列で、Enter と Exit を数える
            var slot = ReadySlot(PrewarmMode.Reuse);
            int enters = 0, exits = 0;
            Check.True(slot.Lend(), "1 回目を貸す");
            enters++;
            Check.True(slot.KeepsContentOnReturn, "貸している Reuse は戻すと中身を残す");
            if (ScreenContentPlan.NotifiesExit(leavingStack: false, keptForReuse: slot.KeepsContentOnReturn)) exits++;
            Check.Equal(PrewarmStage.Ready, slot.Return(), "作り置きへ戻る");
            Check.True(slot.Lend(), "2 回目を貸す（同じ画面のスクリプトにまた OnScreenEnter）");
            enters++;
            Check.Equal(1, exits, "2 回目の Enter の前に Exit が 1 回届いている");
            Check.Equal(enters - 1, exits, "入りと出が対（貸している 1 回分だけ多い）");
        });

        h.Add("作り置き: 戻すと中身を残すのは貸している Reuse だけ・Exit を届けるのは外れたときと Reuse へ戻すとき", () =>
        {
            Check.True(ScreenContentPlan.NotifiesExit(leavingStack: true, keptForReuse: false), "外れたら届ける（従来どおり）");
            Check.True(ScreenContentPlan.NotifiesExit(leavingStack: true, keptForReuse: true), "外れて Reuse へ戻しても 1 回（二重にしない呼び手の決め方）");
            Check.True(!ScreenContentPlan.NotifiesExit(leavingStack: false, keptForReuse: false), "手放すだけ（作り置きでない・Once・Refill）は従来どおり届けない");
            foreach (var mode in new[] { PrewarmMode.Once, PrewarmMode.Refill })
            {
                var slot = ReadySlot(mode);
                slot.Lend();
                Check.True(!slot.KeepsContentOnReturn, $"{mode}: 中身は画面と一緒に消える");
            }
            var ready = ReadySlot(PrewarmMode.Reuse);
            Check.True(!ready.KeepsContentOnReturn, "貸していなければ false");
            var discarded = ReadySlot(PrewarmMode.Reuse);
            discarded.Lend();
            discarded.Discard();
            Check.True(!discarded.KeepsContentOnReturn, "貸している間に捨てたら、外れたときに一緒に消える");
        });
    }

    /// <summary>貸せる状態の作り置き（温め描きなし）。</summary>
    /// <param name="mode">使い終わった後の扱い。</param>
    private static PrewarmSlot ReadySlot(PrewarmMode mode)
    {
        var slot = new PrewarmSlot(EditPrefab, new PrewarmOptions { Mode = mode });
        slot.Tick(false, false, true);
        for (int i = 0; i < PrewarmSlot.SettleFrames; i++) slot.Tick(false, true, true);
        Check.Equal(PrewarmStage.Ready, slot.Stage, $"{mode}: 貸せる状態にした");
        return slot;
    }
}
