using System;
using System.Collections.Generic;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 2026-10-02 の画面の遷移・面の口の拡充（lane3。Wake or Pay の W3 の回避コードを消すための汎用の口。docs/ui_navigation.md §2.8・§3）の
/// 純粋な計算のテスト: 画面の作り置きの段階（PrewarmSlot: 出入りの途中は作らない・温まるまで待つ・温め描き・貸す・返す〈Once・Refill・Reuse〉・
/// 消えた・上限）、中身の出所の決め方（ScreenContentPlan）。
/// </summary>
public static class NavigationExtensionTests
{
    /// <summary>テストの作り置きのプレハブ。</summary>
    private const string EditPrefab = "assets://alarm/prefabs/edit.actor";

    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;

    public static void Register(TestHarness h, UiThemeData theme)
    {
        RegisterPrewarm(h);
        RegisterCloseAll(h);
        RegisterSheets(h, theme);
        RegisterPopup(h, theme);
        RegisterParking(h, theme);
    }

    // ── 2. 全部閉じる（ModalHost.CloseAll。W3-2 (1)）─────────────────────

    /// <summary>全部閉じる順と結果のテストを足す。</summary>
    private static void RegisterCloseAll(TestHarness h)
    {
        h.Add("全部閉じる: ダイアログ → シート → 覆いの順、同じ種類の中は後から開いた面から（戻るを 1 回ずつ押したのと同じ）", () =>
        {
            var opened = new Dictionary<ModalKind, IReadOnlyList<string>>
            {
                [ModalKind.Overlay] = new[] { "覆い1", "ポップアップ" },
                [ModalKind.Sheet] = new[] { "シート" },
                [ModalKind.Dialog] = new[] { "確認", "入力" },
            };
            var order = ModalCloseOrder.Sequence<string>(k => opened[k]);
            Check.Equal("入力,確認,シート,ポップアップ,覆い1", string.Join(",", order), "閉じる順");
            Check.Equal("ポップアップ,覆い1", string.Join(",", ModalCloseOrder.Sequence<string>(k => opened[k], ModalKind.Overlay)), "種類を絞る");
            Check.Equal(0, ModalCloseOrder.Sequence<string>(_ => Array.Empty<string>()).Count, "何も開いていなければ空");
            Check.Equal("Dialog,Sheet,Overlay", string.Join(",", ModalCloseOrder.KindsTopFirst), "種類の順は戻るの段（200 → 300 → 400）と同じ");
        });

        h.Add("全部閉じる: 結果はダイアログが Dismissed（Dismiss と同じ）・シートと覆いは null（幕・戻ると同じ）", () =>
        {
            Check.Equal<object?>(DialogResult.Dismissed, ModalCloseOrder.ResultFor(ModalKind.Dialog), "ダイアログ");
            Check.True(ModalCloseOrder.ResultFor(ModalKind.Sheet) is null, "シート");
            Check.True(ModalCloseOrder.ResultFor(ModalKind.Overlay) is null, "覆い・ポップアップ");
        });
    }

    // ── 3. シート・覆いの動きなし・高さいっぱい（W3-6 (4)・W3-3 (5)）──────────

    /// <summary>覆い・シートの指定と高さいっぱいの計算のテストを足す。</summary>
    private static void RegisterSheets(TestHarness h, UiThemeData theme)
    {
        h.Add("覆い・シート: 動きの既定は今までどおり（動きあり・高さは中身・下の余白はテーマ）", () =>
        {
            var overlay = new OverlayOptions();
            Check.True(overlay.Animate, "覆いは既定で降りる動き");
            Check.True(!overlay.FillHeight, "覆いは既定で中身の高さ（従来どおり）");
            Check.True(overlay.FillBottomMargin < 0f, "下の余白は既定でテーマ（space.m）");
            Check.True(new SheetOptions().Animate, "シートは既定で開く動き");
            Check.True(new PopupOptions().Animate, "ポップアップは既定で開く動き");
            Check.Close(12, theme.Number(UiTokens.SpaceM), Eps, "space.m = 12（Flutter 版の top_sheet の下の余白）");
        });

        h.Add("覆いの高さいっぱい: 全体 − 上の安全領域 − つまみの行 − 下の余白 − 下の安全領域（0 未満・壊れた値は 0）", () =>
        {
            // 1080x2340・2.625 倍の Pixel 6a の dp（高さ 891.4・上 24・下 24）、つまみの行 24、余白 12
            Check.Close(891.4 - 24 - 24 - 12 - 24, SheetMath.OverlayFillHeight(891.4f, 24f, 24f, 12f, 24f), Eps, "Pixel 6a");
            Check.Close(800 - 24 - 12, SheetMath.OverlayFillHeight(800f, 0f, 24f, 12f, 0f), Eps, "安全領域なし（PC）");
            Check.Close(0, SheetMath.OverlayFillHeight(40f, 24f, 24f, 12f, 24f), Eps, "足りなければ 0（負にしない）");
            Check.Close(800 - 24, SheetMath.OverlayFillHeight(800f, float.NaN, 24f, -5f, float.PositiveInfinity), Eps, "壊れた値・負は 0 として引く");
            Check.Close(0, SheetMath.OverlayFillHeight(float.NaN, 0f, 0f, 0f, 0f), Eps, "全体が壊れていれば 0");
        });
    }

    // ── 4. 中央のポップアップ（Popup・任意の面の口。W3-6 (3)）──────────────

    /// <summary>ポップアップの札の大きさ・トークン・プレハブのテストを足す。</summary>
    private static void RegisterPopup(TestHarness h, UiThemeData theme)
    {
        h.Add("ポップアップ: 既定のテーマのトークン（余白 16・幅の上限 560・内側の余白 8・高さの上限 0.8・角丸 28）", () =>
        {
            Check.Close(16, theme.Number(NavTokens.SizePopupMargin), Eps, "size.popup_margin");
            Check.Close(560, theme.Number(NavTokens.SizePopupMaxWidth), Eps, "size.popup_max_width");
            Check.Close(8, theme.Number(NavTokens.SizePopupPadding), Eps, "size.popup_padding");
            Check.Close(0.8, theme.Number(NavTokens.RatioPopupMaxHeight), Eps, "ratio.popup_max_height");
            Check.Close(theme.Number(NavTokens.RadiusDialog), theme.Number(NavTokens.RadiusPopup), Eps, "radius.popup はダイアログと同じ");
            foreach (var token in NavTokens.All) Check.True(theme.Has(token), $"既定のテーマに {token} がある");
            var o = new PopupOptions();
            Check.Equal(ModalKind.Overlay, o.Kind, "既定の帯は覆い（Wake or Pay と同じ）");
            Check.True(o.DismissOnScrimTap && o.CancelableByBack && o.ShowCloseButton, "幕・戻る・× で閉じる");
        });

        h.Add("ポップアップ: 幅は画面 − 余白 × 2（上限 560・指定の幅も画面まで）、高さは中身 ＋ 内側の余白 × 2", () =>
        {
            // 411 dp の画面・安全領域 24/24・余白 16・内側の余白 8・中身 300
            var size = PopupCardMath.Card(411f, 891.4f, 24f, 24f, 16f, 560f, 0.8f, 8f, 0f, 300f);
            Check.Close(379, size.Width, Eps, "幅 = 411 − 32");
            Check.Close(316, size.Height, Eps, "高さ = 300 ＋ 16");
            Check.True(!size.Clipped, "切っていない");
            Check.Close(560, PopupCardMath.Card(1280f, 800f, 0f, 0f, 16f, 560f, 0.8f, 8f, 0f, 300f).Width, Eps, "広い画面は上限 560");
            Check.Close(1280 - 32, PopupCardMath.Card(1280f, 800f, 0f, 0f, 16f, 0f, 0.8f, 8f, 0f, 300f).Width, Eps, "上限 0 以下は無し");
            Check.Close(200, PopupCardMath.Card(411f, 800f, 0f, 0f, 16f, 560f, 0.8f, 8f, 200f, 300f).Width, Eps, "指定の幅");
            Check.Close(411, PopupCardMath.Card(411f, 800f, 0f, 0f, 16f, 900f, 0.8f, 8f, 600f, 300f).Width, Eps, "指定の幅も画面の幅まで");
            Check.Close(0, PopupCardMath.Card(20f, 800f, 0f, 0f, 16f, 560f, 0.8f, 8f, 0f, 300f).Width, Eps, "余白で幅が無くなれば 0");
        });

        h.Add("ポップアップ: 高さの上限 = min(安全領域 × 0.8, 安全領域 − 余白 × 2)、超えたら切る・中身が分からなければ上限", () =>
        {
            float safe = 891.4f - 48f;
            Check.Close(safe * 0.8, PopupCardMath.MaxHeight(891.4f, 24f, 24f, 16f, 0.8f), 1e-3, "ふつうは割合で決まる");
            Check.Close(100 - 32, PopupCardMath.MaxHeight(100f, 0f, 0f, 16f, 0.99f), Eps, "低い画面は余白で決まる");
            Check.Close(safe - 32, PopupCardMath.MaxHeight(891.4f, 24f, 24f, 16f, 1.5f), 1e-3, "割合が 1 を超えたら 1（余白で決まる）");
            Check.Close(0, PopupCardMath.MaxHeight(20f, 24f, 24f, 16f, 0.8f), Eps, "安全領域が無ければ 0");
            var tall = PopupCardMath.Card(411f, 891.4f, 24f, 24f, 16f, 560f, 0.8f, 8f, 0f, 2000f);
            Check.Close(safe * 0.8, tall.Height, 1e-3, "長い中身は上限");
            Check.True(tall.Clipped, "切った（中身が自分でスクロールする）");
            var unknown = PopupCardMath.Card(411f, 891.4f, 24f, 24f, 16f, 560f, 0.8f, 8f, 0f, 0f);
            Check.Close(safe * 0.8, unknown.Height, 1e-3, "中身の高さが分からなければ上限");
            Check.True(!unknown.Clipped, "分からないのは切ったと言わない");
            Check.Close(safe * 0.8, PopupCardMath.Card(411f, 891.4f, 24f, 24f, 16f, 560f, 0.8f, 8f, 0f, float.NaN).Height, 1e-3, "NaN は分からない");
        });

        h.Add("ポップアップ: プレハブ popup.actor の作り（根に Popup・幕は並べない・札は真ん中の周り・中身は残りの高さ・× は並べない）", () =>
        {
            var root = WidgetExtensionTests.Load("prefabs", "popup.actor");
            Check.Equal("SEED.UI.Popup", WidgetExtensionTests.Data(root, "ScriptComponent").GetProperty("type_name").GetString(), "根のスクリプト");
            var rootStack = WidgetExtensionTests.Data(root, "CanvasStackComponent");
            Check.Equal("center", rootStack.GetProperty("main_align").GetString(), "札は縦の真ん中");
            Check.Equal("center", rootStack.GetProperty("cross_align").GetString(), "札は横の真ん中");
            var scrim = WidgetExtensionTests.Child(root, "Scrim");
            Check.True(WidgetExtensionTests.Data(scrim, "CanvasLayoutItemComponent").GetProperty("ignore_layout").GetBoolean(), "幕は並べない（親いっぱい）");
            Check.True(WidgetExtensionTests.Data(scrim, "CanvasGestureComponent").GetProperty("tap").GetBoolean(), "幕はタップを受ける");
            Check.Equal("SEED.UI.GestureRelay", WidgetExtensionTests.Data(scrim, "ScriptComponent").GetProperty("type_name").GetString(), "幕のタップの中継");
            var card = WidgetExtensionTests.Child(root, "Card");
            Check.True(!WidgetExtensionTests.Data(card, "CanvasGestureComponent").GetProperty("tap").GetBoolean(), "札は受けるジェスチャーの無い遮る板");
            var cardPadding = WidgetExtensionTests.Data(card, "CanvasStackComponent").GetProperty("padding");
            Check.Close(theme.Number(NavTokens.SizePopupPadding), cardPadding.GetProperty("left").GetDouble(), Eps, "札の内側の余白はトークンと同じ（Edit の見た目）");
            var content = WidgetExtensionTests.Child(card, "Content");
            Check.Close(1, WidgetExtensionTests.Data(content, "CanvasLayoutItemComponent").GetProperty("flex").GetDouble(), Eps, "中身は残りの高さ");
            var close = WidgetExtensionTests.Child(card, "CloseButton");
            Check.True(WidgetExtensionTests.Data(close, "CanvasLayoutItemComponent").GetProperty("ignore_layout").GetBoolean(), "× は並べない（札の角）");
            Check.Equal("SEED.UI.GestureRelay", WidgetExtensionTests.Data(close, "ScriptComponent").GetProperty("type_name").GetString(), "× のタップの中継");
            Check.True(WidgetExtensionTests.Data(close, "CanvasGestureComponent").GetProperty("min_hit_size_dp").GetDouble() >= theme.Number(UiTokens.SizeTouchMin) - Eps,
                "× の当たりは押せる大きさ 48 以上");
            WidgetExtensionTests.Child(close, "Label");
        });
    }

    // ── 5. 覆いを全画面の下に残す（ModalHost.Park。W3-7 (2)）──────────────

    /// <summary>画面の下へ回す面の底上げとフォーカスの範囲のテストを足す。</summary>
    private static void RegisterParking(TestHarness h, UiThemeData theme)
    {
        h.Add("覆いを下に残す: 面の実効の底上げは全画面の枠より半段奥・下の画面の枠より手前（覆いの帯を差し引いて書く）", () =>
        {
            int step = UiLayers.StackStep(theme, 0);
            int band = UiLayers.Band(theme, ModalKind.Overlay);
            // 根のスタックの Screens の実効 0・全画面は段 1
            int page = ParkedPlaneLayers.PageFrameBias(0, 1, step);
            Check.Equal(step, page, "段 1 の枠 = 1 段");
            int effective = ParkedPlaneLayers.EffectiveBias(page, step);
            Check.True(effective < page && effective > page - step, "全画面の枠より奥・下の画面の枠より手前");
            Check.Equal(page - step / 2, effective, "半段奥");
            // 面の根の祖先の底上げ = 覆いの帯（ModalHost の Overlays）。面の根自身の値（帯の中の並び）は書き換えるので数えない
            int own = ParkedPlaneLayers.PlaneBias(page, step, band);
            Check.Equal(effective - band, own, "面の根に書く値 = 実効 − 帯");
            Check.True(own < 0, "覆いの帯より奥へ下げるので負");
            // タブの中のスタック（1 段 1,000）・祖先の底上げつき
            Check.Equal(20_000 + 3 * 1000 - 500, ParkedPlaneLayers.EffectiveBias(ParkedPlaneLayers.PageFrameBias(20_000, 3, 1000), 1000), "入れ子のスタック");
            Check.Equal(-UiLayers.MaxBias, ParkedPlaneLayers.PlaneBias(0, step, int.MaxValue), "底上げの範囲へ収める");
        });

        h.Add("覆いを下に残す: 範囲を重ねる範囲から外して後ろへ回すと全画面の範囲が前に出て、戻すと覆いが前へ戻る", () =>
        {
            var model = new FocusModel<string>();
            object screen = "下の画面", overlay = "覆い", page = "全画面";
            model.PushScope(screen);
            model.Request("下の画面の欄", screen);
            model.PushScope(overlay, overlay: true);
            model.Request("覆いの欄", overlay);
            Check.Equal("覆いの欄", model.Current, "覆いが前");
            // Park: 重ねる範囲から外して後ろへ（全画面が落ち着く前は下の画面が前）
            model.SetOverlay(overlay, false);
            model.SendToBack(overlay);
            Check.True(!model.IsOverlay(overlay), "重ねる範囲から外れた");
            Check.Equal("下の画面の欄", model.Current, "全画面が落ち着く前は下の画面");
            // 全画面が落ち着く（ScreenStack.Finish の BringToFront）
            model.PushScope(page);
            model.Request("全画面の欄", page);
            Check.Equal("全画面の欄", model.Current, "全画面の範囲がいちばん前（覆いの後ろへ入らない）");
            // 全画面が閉じる: 範囲を外し、Unpark（重ねる範囲へ戻して前へ）→ 下の画面が上になる（BringToFront）
            model.RemoveScope(page);
            model.SetOverlay(overlay, true);
            model.BringToFront(overlay);
            model.BringToFront(screen);
            Check.Equal("覆いの欄", model.Current, "覆いが前へ戻る（下の画面は覆いの後ろ）");

            // 外さずに後ろへ回しただけ（従来の SendToBack）では、全画面の範囲は覆いの後ろへ入る（Wake or Pay が落ち着いた後に回し直していた理由）
            var old = new FocusModel<string>();
            old.PushScope(screen);
            old.PushScope(overlay, overlay: true);
            old.Request("覆いの欄", overlay);
            old.SendToBack(overlay);
            old.PushScope(page);
            Check.True(!old.Request("全画面の欄", page), "全画面の欄はフォーカスになれない");
            Check.True(!ReferenceEquals(old.TopScope, page), "重ねる範囲のままだと全画面の範囲は覆いの後ろへ入る（だから重ねる範囲から外す）");
        });
    }

    // ── 1. 画面の作り置き（ScreenStack.Prewarm。W3-6 (1)）────────────────

    /// <summary>作り置きの段階のテストを足す。</summary>
    private static void RegisterPrewarm(TestHarness h)
    {
        h.Add("作り置き: 出入りの途中は作り始めない・空いたら作り始める（1 回だけ StartBuild）", () =>
        {
            var slot = new PrewarmSlot(EditPrefab, null);
            Check.Equal(PrewarmStage.Waiting, slot.Stage, "始めは作り始めを待つ");
            Check.Equal(PrewarmAction.None, slot.Tick(transitioning: true, built: false, screenReady: true), "出入りの途中は作らない");
            Check.Equal(PrewarmStage.Waiting, slot.Stage, "待ったまま");
            Check.Equal(PrewarmAction.StartBuild, slot.Tick(false, false, true), "空いたら作り始める");
            Check.Equal(PrewarmStage.Building, slot.Stage, "作っている");
            Check.Equal(PrewarmAction.None, slot.Tick(false, false, true), "できあがるまで何もしない（2 度作らない）");
            Check.Equal(PrewarmAction.None, slot.Tick(true, false, true), "作り始めた後は出入りの途中でも続ける");
        });

        h.Add("作り置き: できあがって SettleFrames 経ち、画面のスクリプトが温まったら貸せる（温め描きなし）", () =>
        {
            var slot = new PrewarmSlot(EditPrefab, PrewarmOptions.Default);
            slot.Tick(false, false, true);
            for (int i = 1; i < PrewarmSlot.SettleFrames; i++)
                Check.Equal(PrewarmAction.None, slot.Tick(false, true, true), $"できあがって {i} フレームはまだ（部品のスクリプトの開始を待つ）");
            Check.Equal(PrewarmAction.BecameReady, slot.Tick(false, true, true), "SettleFrames で貸せる");
            Check.Equal(PrewarmStage.Ready, slot.Stage, "貸せる");
            Check.True(!slot.TimedOut, "上限で進めたのではない");

            var slow = new PrewarmSlot(EditPrefab, null);
            slow.Tick(false, false, true);
            for (int i = 0; i < PrewarmSlot.SettleFrames * 3; i++) slow.Tick(false, true, screenReady: false);
            Check.Equal(PrewarmStage.Building, slow.Stage, "IsPrewarmReady が false の間は作っている途中のまま");
            Check.Equal(PrewarmAction.BecameReady, slow.Tick(false, true, true), "温まったら貸せる");
        });

        h.Add("作り置き: 温まるのを待つ上限（MaxWaitFrames）で先へ進める（TimedOut）", () =>
        {
            var slot = new PrewarmSlot(EditPrefab, null);
            slot.Tick(false, false, true);
            var last = PrewarmAction.None;
            int frames = 0;
            while (slot.Stage == PrewarmStage.Building && frames < PrewarmSlot.MaxWaitFrames * 2)
            {
                last = slot.Tick(false, true, screenReady: false);
                frames++;
            }
            Check.Equal(PrewarmSlot.MaxWaitFrames, frames, "上限のフレームで進む");
            Check.Equal(PrewarmAction.BecameReady, last, "貸せる");
            Check.True(slot.TimedOut, "上限で進めた印");
        });

        h.Add("作り置き: 温め描き（WarmDrawFrames）は決めたフレーム数だけ描いてから貸せる", () =>
        {
            const int drawFrames = 2;
            var slot = new PrewarmSlot(EditPrefab, new PrewarmOptions { WarmDrawFrames = drawFrames });
            slot.Tick(false, false, true);
            slot.Tick(false, true, true);
            Check.Equal(PrewarmAction.BeginWarmDraw, slot.Tick(false, true, true), "温まったら温め描きを始める");
            Check.Equal(PrewarmStage.WarmDrawing, slot.Stage, "描いている");
            Check.True(slot.CanLend, "描いている途中でも貸せる");
            Check.Equal(PrewarmAction.None, slot.Tick(false, true, true), "1 フレーム目");
            Check.Equal(PrewarmAction.EndWarmDraw, slot.Tick(false, true, true), "2 フレーム描いたら終える");
            Check.Equal(PrewarmStage.Ready, slot.Stage, "貸せる");
        });

        h.Add("作り置き: 作っている途中・温め描きの途中・貸せる で貸せる（待つ・貸している・捨てた後は貸せない）", () =>
        {
            var waiting = new PrewarmSlot(EditPrefab, null);
            Check.True(!waiting.CanLend && !waiting.Lend(), "作り始める前は貸せない（プレハブから作る）");
            var building = new PrewarmSlot(EditPrefab, null);
            building.Tick(false, false, true);
            Check.True(building.Lend(), "作っている途中でも貸す（もう 1 つ作るより軽い）");
            Check.Equal(PrewarmStage.Lent, building.Stage, "貸している");
            Check.Equal(1, building.LendCount, "貸した回数");
            Check.True(!building.Lend(), "貸している間は 2 度貸さない（同じプレハブの 2 つ目は作る）");
            Check.Equal(PrewarmAction.None, building.Tick(false, true, true), "貸している間は進めない");
            building.Discard();
            Check.True(!building.CanLend, "捨てた後は貸せない");
        });

        h.Add("作り置き: 返したら Once は終わり・Refill は作り直しを待つ・Reuse はすぐ貸せる", () =>
        {
            foreach (var (mode, expected) in new[]
            {
                (PrewarmMode.Once, PrewarmStage.Discarded),
                (PrewarmMode.Refill, PrewarmStage.Waiting),
                (PrewarmMode.Reuse, PrewarmStage.Ready),
            })
            {
                var slot = ReadySlot(mode);
                Check.True(slot.Lend(), $"{mode}: 貸せる");
                Check.Equal(expected, slot.Return(), $"{mode}: 返した後");
                Check.Equal(expected, slot.Return(), $"{mode}: 2 度返しても変わらない");
            }
            var refill = ReadySlot(PrewarmMode.Refill);
            refill.Lend();
            refill.Return();
            Check.Equal(PrewarmAction.None, refill.Tick(true, false, true), "Refill: 出入りの途中は作り直さない");
            Check.Equal(PrewarmAction.StartBuild, refill.Tick(false, false, true), "Refill: 空いたら作り直す");
            var reuse = ReadySlot(PrewarmMode.Reuse);
            reuse.Lend();
            reuse.Return();
            Check.True(reuse.Lend(), "Reuse: 戻したらまた貸せる（作り直さない）");
            Check.Equal(2, reuse.LendCount, "Reuse: 貸した回数");
        });

        h.Add("作り置き: 実体が消えたら Once は終わり、Refill・Reuse は作り直しを待つ", () =>
        {
            Check.Equal(PrewarmStage.Discarded, ReadySlot(PrewarmMode.Once).Lost(), "Once");
            Check.Equal(PrewarmStage.Waiting, ReadySlot(PrewarmMode.Refill).Lost(), "Refill");
            var reuse = ReadySlot(PrewarmMode.Reuse);
            Check.Equal(PrewarmStage.Waiting, reuse.Lost(), "Reuse");
            Check.Equal(0, reuse.BuiltFrames, "数え直す");
            Check.Equal(PrewarmAction.StartBuild, reuse.Tick(false, false, true), "作り直す");
        });

        h.Add("作り置き: 中身の出所は 渡された中身 > 置いてある根 > 作り置き > プレハブ・渡された中身は常に状態を保つ", () =>
        {
            Check.Equal(ScreenContentSource.Supplied, ScreenContentPlan.Choose(true, true, true), "渡された中身がいちばん先");
            Check.Equal(ScreenContentSource.Adopted, ScreenContentPlan.Choose(false, true, true), "置いてある根は作り置きより先（残すと見えたまま）");
            Check.Equal(ScreenContentSource.Prewarmed, ScreenContentPlan.Choose(false, false, true), "作り置き");
            Check.Equal(ScreenContentSource.Prefab, ScreenContentPlan.Choose(false, false, false), "無ければプレハブ");
            Check.True(ScreenContentPlan.EffectiveKeepState(ScreenContentSource.Supplied, false), "渡された中身は作り直せないので手放さない");
            Check.True(!ScreenContentPlan.EffectiveKeepState(ScreenContentSource.Prefab, false), "プレハブは指定のまま");
            Check.True(!ScreenContentPlan.EffectiveKeepState(ScreenContentSource.Prewarmed, false), "作り置きも指定のまま（作り直すときは作り置きか新しく作る）");
        });
    }

    /// <summary>貸せる状態の作り置きを作る（温め描きなし）。</summary>
    private static PrewarmSlot ReadySlot(PrewarmMode mode)
    {
        var slot = new PrewarmSlot(EditPrefab, new PrewarmOptions { Mode = mode });
        slot.Tick(false, false, true);
        for (int i = 0; i < PrewarmSlot.SettleFrames; i++) slot.Tick(false, true, true);
        Check.Equal(PrewarmStage.Ready, slot.Stage, $"{mode}: 貸せる状態にした");
        return slot;
    }
}
