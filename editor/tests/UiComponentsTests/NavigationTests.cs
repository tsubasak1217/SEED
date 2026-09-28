using System;
using System.Collections.Generic;
using System.Linq;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 画面の組み立て（W2-7）の純粋な計算のテスト（docs/ui_navigation.md）:
/// 動きの曲線（UiCurve）・出入りの置き方（TransitionMath）・画面のスタック（ScreenStackModel）・タブ（TabModel）・
/// 戻るの段（BackChain・NavigatorOrder）・ダイアログ（DialogModel）・シート（SheetMath）・ドラッグで閉じる（DragDismissMath）・
/// トースト（ToastQueue）・フォーカス（FocusModel）・重なりのレイヤー（UiLayers）・タブの見た目（TabLooks）。
/// </summary>
public static class NavigationTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>画面のプレハブの例。</summary>
    private const string A = "assets://a.actor", B = "assets://b.actor", C = "assets://c.actor", D = "assets://d.actor";

    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── テーマと曲線 ───────────────────────────────────────
        h.Add("W2-7 テーマ: 既定のテーマは画面の組み立てのトークンをすべて持つ", () =>
        {
            foreach (var token in NavTokens.All) Check.True(theme.Has(token), $"既定のテーマに {token} がある");
            Check.Close(0.22, theme.Number(NavTokens.MotionOverlay), Eps, "覆いは Flutter 版の 220ms");
            Check.Close(0.54, theme.Number(NavTokens.OpacityScrim), Eps, "覆い・シートの幕は 54%");
        });

        h.Add("W2-7 曲線: 端点・直線・fastOutSlowIn は SwipeMath.Ease と同じ・単調・テーマから読む", () =>
        {
            foreach (var c in new[] { UiCurve.Linear, UiCurve.Standard, UiCurve.EaseOut, UiCurve.FastOutSlowIn })
            {
                Check.Close(0, c.Evaluate(0f), Eps, $"{c} の始まり");
                Check.Close(1, c.Evaluate(1f), Eps, $"{c} の終わり");
                Check.Close(0, c.Evaluate(-1f), Eps, "範囲の外（負）は 0");
                Check.Close(1, c.Evaluate(2f), Eps, "範囲の外（1 超え）は 1");
                Check.Close(0, c.Evaluate(float.NaN), Eps, "NaN は 0");
                float prev = 0f;
                for (int i = 1; i <= 20; i++)
                {
                    float v = c.Evaluate(i / 20f);
                    Check.True(v >= prev - 1e-4f, $"{c} は単調（{i}）");
                    prev = v;
                }
            }
            for (int i = 1; i < 10; i++) Check.Close(i / 10.0, UiCurve.Linear.Evaluate(i / 10f), 1e-3, "直線は恒等");
            for (int i = 1; i < 10; i++) Check.Close(SwipeMath.Ease(i / 10f), UiCurve.FastOutSlowIn.Evaluate(i / 10f), 1e-3, "fastOutSlowIn は SwipeMath.Ease と同じ");
            Check.True(UiCurve.EaseOut.Evaluate(0.5f) > 0.5f, "easeOut は前半が速い");
            var fromTheme = UiCurve.FromTheme(theme, NavTokens.MotionOverlayCurve, UiCurve.Linear);
            Check.Equal(UiCurve.EaseOut, fromTheme, "覆いの曲線 = easeOut（テーマ）");
            var broken = UiThemeData.Parse("{\"motion\":{\"push_curve\":{\"x1\":0.1}}}", null, out _);
            Check.Equal(UiCurve.Linear, UiCurve.FromTheme(broken, NavTokens.MotionPushCurve, UiCurve.Linear), "成分が欠けたら既定");
            Check.Close(1, new UiCurve(5f, 0f, -3f, 1f).X1, Eps, "x の制御点は 0〜1 へ収める");
        });

        // ── 出入りの置き方 ─────────────────────────────────────
        h.Add("W2-7 出入り: 押し込み（右から入る・視差・下ろすと右へ出る）", () =>
        {
            const float par = 0.3f;
            var f0 = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Forward, 0f, par);
            var f5 = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Forward, 0.5f, par);
            var f1 = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Forward, 1f, par);
            Check.Close(1, f0.Incoming.Fraction.x, Eps, "入る画面は右の外から");
            Check.Close(0.5, f5.Incoming.Fraction.x, Eps, "半分で半分");
            Check.Close(0, f1.Incoming.Fraction.x, Eps, "終わりは 0");
            Check.Close(0, f0.Outgoing.Fraction.x, Eps, "覆われる画面は 0 から");
            Check.Close(-par, f1.Outgoing.Fraction.x, Eps, "視差の分だけ左へ");
            Check.True(f5.Incoming.Visible && f5.Outgoing.Visible, "動きの間は両方見せる");
            var b0 = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Backward, 0f, par);
            var b1 = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Backward, 1f, par);
            Check.Close(0, b0.Outgoing.Fraction.x, Eps, "下ろす画面は 0 から");
            Check.Close(1, b1.Outgoing.Fraction.x, Eps, "右の外へ");
            Check.Close(-par, b0.Incoming.Fraction.x, Eps, "戻る画面は視差の位置から");
            Check.Close(0, b1.Incoming.Fraction.x, Eps, "0 へ戻る");
            Check.Close(0, f5.VeilAlpha, Eps, "押し込みに幕は無い");
        });

        h.Add("W2-7 出入り: 覆う（上から降りる・下の画面は動かない）・フェード（幕の半分で入れ替わる）・なし", () =>
        {
            var c0 = TransitionMath.Evaluate(NavTransition.Cover, NavDirection.Forward, 0f, 0.3f);
            var c1 = TransitionMath.Evaluate(NavTransition.Cover, NavDirection.Forward, 1f, 0.3f);
            Check.Close(-1, c0.Incoming.Fraction.y, Eps, "上の外から");
            Check.Close(0, c1.Incoming.Fraction.y, Eps, "降りきる");
            Check.Close(0, c0.Outgoing.Fraction.y, Eps, "下の画面は動かない");
            var cb = TransitionMath.Evaluate(NavTransition.Cover, NavDirection.Backward, 0.25f, 0.3f);
            Check.Close(-0.25, cb.Outgoing.Fraction.y, Eps, "戻ると上へ");
            var fa = TransitionMath.Evaluate(NavTransition.Fade, NavDirection.Forward, 0.25f, 0f);
            var fm = TransitionMath.Evaluate(NavTransition.Fade, NavDirection.Forward, 0.5f, 0f);
            var fz = TransitionMath.Evaluate(NavTransition.Fade, NavDirection.Forward, 0.75f, 0f);
            Check.Close(0.5, fa.VeilAlpha, Eps, "前半は幕が濃くなる");
            Check.True(!fa.Incoming.Visible && fa.Outgoing.Visible, "半分まで古い画面");
            Check.Close(1, fm.VeilAlpha, Eps, "半分で幕がいちばん濃い");
            Check.True(fm.Incoming.Visible && !fm.Outgoing.Visible, "半分で入れ替わる");
            Check.Close(0.5, fz.VeilAlpha, Eps, "後半は薄くなる");
            Check.Close(0, TransitionMath.Evaluate(NavTransition.Fade, NavDirection.Backward, 1f, 0f).VeilAlpha, Eps, "終わりは幕なし");
            var none = TransitionMath.Evaluate(NavTransition.None, NavDirection.Forward, 0f, 0.3f);
            Check.True(none.Incoming.Visible && !none.Outgoing.Visible, "なしはすぐ入れ替わる");
            var clamp = TransitionMath.Evaluate(NavTransition.Push, NavDirection.Forward, 5f, 0.3f);
            Check.Close(0, clamp.Incoming.Fraction.x, Eps, "進み具合は 0〜1 へ収める");
            Check.Close(0.3, NavMotion.Duration(theme, NavTransition.Push), Eps, "押し込みの時間（テーマ）");
            Check.Close(0, NavMotion.Duration(theme, NavTransition.None), Eps, "なしは 0 秒");
            Check.Close(0.3, NavMotion.Parallax(theme), Eps, "視差（テーマ）");
        });

        // ── 画面のスタック ─────────────────────────────────────
        h.Add("W2-7 スタック: 積む・下ろす・根は下ろせない・結果・番号", () =>
        {
            var m = new ScreenStackModel();
            var root = m.Push(A, null, null, NavTransition.Push);
            Check.Equal(ScreenOpKind.SetRoot, root.Kind, "空に積むと根");
            Check.Equal(NavTransition.None, root.Transition, "根は動かさない");
            var push = m.Push(B, null, "args", NavTransition.Push);
            Check.Equal(ScreenOpKind.Push, push.Kind, "積む");
            Check.Equal(NavTransition.Push, push.Transition, "既定の出入り");
            Check.Equal(A, push.Outgoing!.Prefab, "覆われるのは根");
            Check.Equal("args", push.Incoming!.Args, "値を持つ");
            var cover = m.Push(C, new ScreenOptions { Transition = NavTransition.Cover }, null, NavTransition.Push);
            Check.Equal(NavTransition.Cover, cover.Transition, "指定の出入り");
            Check.Equal(3, m.Count, "3 段");
            var pop = m.Pop("done", NavTransition.Push)!;
            Check.Equal(NavTransition.Cover, pop.Transition, "下ろす動きは積んだときと同じ種類");
            Check.Equal(NavDirection.Backward, pop.Direction, "戻る向き");
            Check.True(pop.Outgoing!.Removed && pop.OutgoingRemoved, "外れた");
            Check.Equal("done", pop.Outgoing.Result, "結果");
            Check.Equal(B, pop.Incoming!.Prefab, "B が上に戻る");
            m.Pop(null, NavTransition.Push);
            Check.True(m.Pop(null, NavTransition.Push) is null, "根は下ろせない");
            Check.Equal(1, m.Count, "根だけ");
            var ids = new HashSet<int> { root.Incoming!.Id, push.Incoming.Id, cover.Incoming!.Id };
            Check.Equal(3, ids.Count, "番号は一意");
        });

        h.Add("W2-7 スタック: 置き換え・根まで下ろす（途中の段は動かさずに外す）・根からやり直す", () =>
        {
            var m = new ScreenStackModel();
            m.Push(A, null, null, NavTransition.Push);
            m.Push(B, null, null, NavTransition.Push);
            var replace = m.Replace(C, null, null, NavTransition.Push);
            Check.Equal(ScreenOpKind.Replace, replace.Kind, "置き換え");
            Check.Equal(B, replace.Outgoing!.Prefab, "B が外れる");
            Check.True(replace.OutgoingRemoved, "外れた");
            Check.Equal(2, m.Count, "段の数は同じ");
            m.Push(D, null, null, NavTransition.Push);
            var toRoot = m.PopToRoot(NavTransition.Push)!;
            Check.Equal(D, toRoot.Outgoing!.Prefab, "いちばん上が動いて出る");
            Check.Equal(1, toRoot.Removed.Count, "途中の C は動かさずに外す");
            Check.Equal(C, toRoot.Removed[0].Prefab, "C");
            Check.Equal(A, toRoot.Incoming!.Prefab, "根が戻る");
            Check.True(m.PopToRoot(NavTransition.Push) is null, "根だけなら何もしない");
            m.Push(B, null, null, NavTransition.Push);
            var reset = m.SetRoot(C, null, null, NavTransition.Fade);
            Check.Equal(NavTransition.Fade, reset.Transition, "やり直しの出入り");
            Check.Equal(B, reset.Outgoing!.Prefab, "旧いいちばん上が動く");
            Check.Equal(1, reset.Removed.Count, "旧い根は動かさずに外す");
            Check.Equal(1, m.Count, "新しい根だけ");
            Check.Equal(C, m.Root!.Prefab, "根は C");
        });

        h.Add("W2-7 スタック: 落ち着いた状態（不透明の下は隠す・透ける画面の下は見せる・状態を保たない画面は手放す）", () =>
        {
            var m = new ScreenStackModel();
            m.Push(A, new ScreenOptions { KeepState = false }, null, NavTransition.Push);
            m.Push(B, null, null, NavTransition.Push);
            m.Push(C, new ScreenOptions { Opaque = false }, null, NavTransition.Push);
            foreach (var e in m.Entries) e.HasInstance = true;
            var s = m.Settle();
            Check.Equal("B,C", string.Join(",", s.Visible.Select(e => e.Prefab == B ? "B" : e.Prefab == C ? "C" : "A")), "透ける C の下の B も見せる");
            Check.Equal(1, s.Hidden.Count, "A は隠す");
            Check.Equal(1, s.Release.Count, "状態を保たない A は手放す");
            Check.True(!m.Root!.HasInstance, "A は実体なし");
            // C を下ろすと B が上。A は隠れたまま（手放したまま）。B を下ろすと A を作り直す
            m.Pop(null, NavTransition.Push);
            var s2 = m.Settle();
            Check.Equal(1, s2.Visible.Count, "B だけ見せる");
            Check.Equal(0, s2.Release.Count, "もう手放した");
            var back = m.Pop(null, NavTransition.Push)!;
            Check.True(!back.Incoming!.HasInstance, "戻る A は作り直しが要る");
            ScreenStackModel.MarkInstantiated(back.Incoming);
            Check.True(back.Incoming.HasInstance, "作り直した");
            var s3 = m.Settle();
            Check.True(s3.Visible.Count == 1 && s3.Visible[0].Prefab == A, "A だけ");
        });

        // ── タブ ───────────────────────────────────────────────
        h.Add("W2-7 タブ: 切り替え・もう一度押す・範囲の外・タブごとのスタックは保つ", () =>
        {
            var tabs = new TabModel(3);
            Check.Equal(0, tabs.Selected, "最初は 0");
            var stacks = Enumerable.Range(0, 3).Select(_ => new ScreenStackModel()).ToArray();
            foreach (var s in stacks) s.Push(A, null, null, NavTransition.Push);
            stacks[0].Push(B, null, null, NavTransition.Push);
            var r = tabs.Select(1);
            Check.True(r.Changed && !r.Reselected && r.Previous == 0, "1 へ");
            stacks[1].Push(C, null, null, NavTransition.Push);
            stacks[1].Push(D, null, null, NavTransition.Push);
            tabs.Select(0);
            Check.Equal(2, stacks[0].Count, "タブ 0 のスタックは前の位置のまま");
            Check.Equal(3, stacks[1].Count, "タブ 1 のスタックも保つ");
            var again = tabs.Select(0);
            Check.True(!again.Changed && again.Reselected, "もう一度押した");
            stacks[0].PopToRoot(NavTransition.Push);
            Check.Equal(1, stacks[0].Count, "もう一度押したら根へ");
            var bad = tabs.Select(7);
            Check.True(!bad.Changed && !bad.Reselected && tabs.Selected == 0, "範囲の外は何もしない");
            Check.Equal(2, new TabModel(3, 9).Selected, "最初の選択は範囲へ収める");
            Check.Equal(-1, new TabModel(0).Selected, "タブが無ければ −1");
        });

        h.Add("W2-7 タブ: 戻る（スタックを下ろす → 最初のタブへ → 受けない）", () =>
        {
            var tabs = new TabModel(3, 2);
            Check.Equal(TabBackAction.PopStack, tabs.DecideBack(2, true), "スタックが根より上なら下ろす");
            Check.Equal(TabBackAction.SelectFirst, tabs.DecideBack(1, true), "根で最初のタブ以外なら最初のタブへ");
            Check.Equal(TabBackAction.None, tabs.DecideBack(1, false), "最初のタブへ戻さない設定なら受けない");
            tabs.Select(0);
            Check.Equal(TabBackAction.None, tabs.DecideBack(1, true), "最初のタブの根なら受けない（アプリを背面へ）");
            Check.Equal(TabBackAction.None, new TabModel(0).DecideBack(1, true), "タブが無ければ受けない");
        });

        // ── 戻るの段 ───────────────────────────────────────────
        h.Add("W2-7 戻る: 層の順（小さい値が先）・最初に受けた層で止まる・同じ順は足した順・外す・配る途中で外す", () =>
        {
            var chain = new BackChain();
            var calls = new List<string>();
            chain.Add(BackOrder.Navigation, "nav", () => { calls.Add("nav"); return true; });
            var dialog = chain.Add(BackOrder.Dialog, "dialog", () => { calls.Add("dialog"); return false; });
            chain.Add(BackOrder.Focus, "focus", () => { calls.Add("focus"); return false; });
            chain.Add(BackOrder.Dialog, "dialog2", () => { calls.Add("dialog2"); return false; });
            var r = chain.Dispatch();
            Check.Equal("focus,dialog,dialog2,nav", string.Join(",", calls), "順に尋ねる（同じ順は足した順）");
            Check.True(r.Handled && r.Layer == "nav" && r.Order == BackOrder.Navigation, "nav が受けた");
            dialog.Dispose();
            dialog.Dispose();
            Check.Equal(3, chain.Count, "外す（2 度でも 1 回）");
            var empty = new BackChain();
            Check.True(!empty.Dispatch().Handled, "誰もいなければ受けない");
            var self = new BackChain();
            IDisposable? me = null;
            me = self.Add(1, "self", () => { me!.Dispose(); return false; });
            self.Add(2, "next", () => true);
            Check.Equal("next", self.Dispatch().Layer, "配る途中で自分を外しても次へ進む");
        });

        h.Add("W2-7 戻る: 全パターン（IME → ダイアログ → シート → 覆い → スタック → 最初のタブ → 背面へ）", () =>
        {
            // 状態を持つ小さなアプリ: 入力欄の IME・ダイアログ・シート・覆いの数・根のスタックの段・タブ（選択と各タブの段）
            int patterns = 0;
            for (int bits = 0; bits < 16; bits++)
                foreach (int rootDepth in new[] { 1, 2 })
                    foreach (int tab in new[] { 0, 1 })
                        foreach (int tabDepth in new[] { 1, 2 })
                        {
                            var app = new FakeApp
                            {
                                ImeOpen = (bits & 1) != 0,
                                Dialogs = (bits & 2) != 0 ? 1 : 0,
                                Sheets = (bits & 4) != 0 ? 1 : 0,
                                Overlays = (bits & 8) != 0 ? 1 : 0,
                                RootDepth = rootDepth,
                                SelectedTab = tab,
                                TabDepth = tabDepth,
                            };
                            // 1 回ずつ押して、期待どおりの層が受け、最後は背面へ回る
                            for (int press = 0; press < 12 && !app.MovedToBack; press++)
                            {
                                string expected = app.Expected();
                                string actual = app.Press();
                                Check.Equal(expected, actual, $"状態 {bits}/{rootDepth}/{tab}/{tabDepth} の {press + 1} 回目");
                            }
                            Check.True(app.MovedToBack, $"状態 {bits}/{rootDepth}/{tab}/{tabDepth} は最後に背面へ");
                            patterns++;
                        }
            Check.Equal(128, patterns, "128 通り");
        });

        h.Add("W2-7 戻る: ナビゲーターは見えている物を内側から（深さの多い順・同じなら登録の順）", () =>
        {
            var order = NavigatorOrder.InnermostFirst(new[]
            {
                ("root", 3, true),
                ("tab-hidden", 7, false),
                ("tab", 7, true),
                ("host", 5, true),
                ("tab2", 7, true),
            });
            Check.Equal("tab,tab2,host,root", string.Join(",", order), "内側から・隠れた物は尋ねない");
        });

        // ── ダイアログ ─────────────────────────────────────────
        h.Add("W2-7 ダイアログ: ボタンの並び（中立・いいえ・はい）・既定の OK・幕のタップ・戻る・結果は 1 回", () =>
        {
            var three = new DialogOptions { PositiveText = "上げる", NegativeText = "やめる", NeutralText = "あとで" };
            Check.Equal("Neutral,Negative,Positive", string.Join(",", DialogModel.Buttons(three)), "左から中立・いいえ・はい");
            var none = new DialogOptions { Title = "保存しました" };
            Check.Equal("Positive", string.Join(",", DialogModel.Buttons(none)), "指定が無ければ OK だけ");
            Check.Equal(DialogOptions.DefaultPositiveText, DialogModel.ButtonText(none, DialogResult.Positive), "OK");
            Check.Equal("やめる", DialogModel.ButtonText(three, DialogResult.Negative), "文字");
            Check.Equal(DialogResult.Dismissed, DialogModel.OnScrimTap(three), "幕のタップで閉じる");
            var modal = new DialogOptions { DismissOnScrimTap = false, CancelableByBack = false };
            Check.True(DialogModel.OnScrimTap(modal) is null, "閉じない設定では幕のタップは何もしない");
            var back = DialogModel.OnBack(modal);
            Check.True(back.Consumed && back.Result is null, "閉じないダイアログも戻るは受ける（後ろへ回さない）");
            var cancel = DialogModel.OnBack(three);
            Check.True(cancel.Consumed && cancel.Result == DialogResult.Dismissed, "戻るで Dismissed");
            var latch = new DialogResultLatch();
            Check.True(latch.TryComplete(DialogResult.Positive), "最初の結果");
            Check.True(!latch.TryComplete(DialogResult.Negative), "2 度目は受けない（連打）");
            Check.Equal(DialogResult.Positive, latch.Result, "最初の結果のまま");
        });

        h.Add("W2-7 ダイアログ: 本文の行の数の見積もり（全角・半角・改行・折り返し）", () =>
        {
            Check.Equal(0, DialogLayout.EstimateLines("", 16f, 264f), "空は 0 行");
            Check.Equal(1, DialogLayout.EstimateLines("変更を保存していません", 16f, 264f), "11 文字 × 16 = 176 < 264 は 1 行");
            Check.Equal(2, DialogLayout.EstimateLines(new string('あ', 17), 16f, 264f), "17 文字 = 272 > 264 は 2 行");
            Check.Equal(1, DialogLayout.EstimateLines(new string('a', 29), 16f, 264f), "半角 29 文字 = 255.2 < 264 は 1 行");
            Check.Equal(2, DialogLayout.EstimateLines(new string('a', 31), 16f, 264f), "半角 31 文字 = 272.8 > 264 は 2 行");
            Check.Equal(3, DialogLayout.EstimateLines("一行目\n二行目\n三行目", 16f, 264f), "改行");
            Check.Close(2 * 16 * DialogLayout.LineHeightEm, DialogLayout.EstimateHeight(new string('あ', 17), 16f, 264f), Eps, "高さ = 行 × 行の高さ");
            Check.Equal(1, DialogLayout.EstimateLines("abc", 0f, 264f), "大きさ 0 でも落ちない");
            Check.Close(16 * 2 + 16 * 0.55 * 2, DialogLayout.EstimateWidth("保存OK", 16f), 1e-3, "幅（全角 2 + 半角 2）");
        });

        // ── シート ─────────────────────────────────────────────
        h.Add("W2-7 シート: 板の高さ・段（半分・全体）・スナップの間隔・位置 → 段・幕の濃さ・安全領域の余白", () =>
        {
            float panel = SheetMath.PanelHeight(800f, 0.9f);
            Check.Close(720, panel, Eps, "領域 800 の 0.9");
            Check.Close(360, SheetMath.SnapInterval(panel, true), Eps, "半分あり: 間隔は板の半分（0・360・720 に止まる）");
            Check.Close(720, SheetMath.SnapInterval(panel, false), Eps, "半分なし: 間隔は板（0・720）");
            Check.Equal(SheetDetent.Half, SheetMath.OpenDetent(new SheetOptions()), "既定は半分で開く");
            Check.Equal(SheetDetent.Full, SheetMath.OpenDetent(new SheetOptions { HalfDetent = false }), "半分が無ければ全部");
            Check.Close(360, SheetMath.DetentPosition(SheetDetent.Half, panel), Eps, "半分の位置");
            Check.Equal(SheetDetent.Closed, SheetMath.Classify(0.2f, panel, true), "0 付近は閉じた（ドラッグ・フリックで閉じる判定）");
            Check.Equal(SheetDetent.Half, SheetMath.Classify(360.3f, panel, true), "半分");
            Check.Equal(SheetDetent.Full, SheetMath.Classify(720f, panel, true), "全部");
            Check.Equal(SheetDetent.Between, SheetMath.Classify(360f, panel, false), "半分が無ければ間");
            Check.Equal(SheetDetent.Between, SheetMath.Classify(100f, panel, true), "動いている途中");
            Check.Close(0.27, SheetMath.ScrimAlpha(180f, panel, true, 0.54f), Eps, "半分までの間は比例");
            Check.Close(0.54, SheetMath.ScrimAlpha(700f, panel, true, 0.54f), Eps, "半分より上は最大");
            Check.Close(0, SheetMath.ScrimAlpha(float.NaN, panel, true, 0.54f), Eps, "NaN は 0");
            Check.Close(24, SheetMath.InsetUnits(63f, 2.625f), Eps, "Pixel 6a の下 63 px = 24 dp");
            Check.Close(0, SheetMath.InsetUnits(-5f, 2.625f), Eps, "負は 0");
            Check.Close(0, SheetMath.PanelHeight(0f, 0.9f), Eps, "領域 0 は 0");
        });

        h.Add("W2-7 ドラッグで閉じる: 距離・速さ（フリック）・逆向きの速さで戻す・逆向きの抵抗", () =>
        {
            Check.True(DragDismissMath.ShouldDismiss(100f, 0f, 96f, 300f), "96 以上引いた");
            Check.True(!DragDismissMath.ShouldDismiss(50f, 0f, 96f, 300f), "足りない");
            Check.True(DragDismissMath.ShouldDismiss(10f, 400f, 96f, 300f), "速く払った");
            Check.True(!DragDismissMath.ShouldDismiss(200f, -400f, 96f, 300f), "逆向きに速く戻したら閉じない");
            Check.True(!DragDismissMath.ShouldDismiss(float.NaN, float.NaN, 96f, 300f), "壊れた値は閉じない");
            Check.Close(30, DragDismissMath.ApplyDrag(0f, 30f, 0.3f, 24f), Eps, "閉じる向きはそのまま");
            Check.Close(-6, DragDismissMath.ApplyDrag(0f, -20f, 0.3f, 24f), Eps, "逆向きは 3 割");
            Check.Close(-24, DragDismissMath.ApplyDrag(-20f, -100f, 0.3f, 24f), Eps, "逆向きの上限");
        });

        // ── トースト ───────────────────────────────────────────
        h.Add("W2-7 トースト: 同時に 3 つまで・待たせる・時間で出ていく・消えたら次・押さえている間は止める・スワイプ", () =>
        {
            var q = new ToastQueue { MaxVisible = 3 };
            var t1 = q.Enqueue("1", 2f);
            var t2 = q.Enqueue("2", 3f);
            var t3 = q.Enqueue("3", 2f);
            var t4 = q.Enqueue("4", 2f);
            Check.True(t1.Phase == ToastPhase.Shown && t3.Phase == ToastPhase.Shown, "3 つまで見せる");
            Check.Equal(ToastPhase.Pending, t4.Phase, "4 つ目は待つ");
            Check.Equal("1,2,3", string.Join(",", q.Active.Select(t => t.Message)), "古い順");
            q.SetHeld(t1.Id, true);
            var tick = q.Tick(2.5f);
            Check.Equal("3", string.Join(",", tick.Expired.Select(t => t.Message)), "押さえている 1 は止まり、2 はまだ、3 が時間切れ");
            Check.Equal(0, tick.Shown.Count, "出ていく途中の分はまだ空かない");
            var shown = q.Remove(t3.Id);
            Check.Equal("4", string.Join(",", shown.Select(t => t.Message)), "消えたら待っていた 4 を出す");
            Check.Equal(0, q.PendingCount, "待ちは空");
            Check.True(q.Dismiss(t2.Id, swiped: true) && t2.Swiped && t2.Phase == ToastPhase.Exiting, "スワイプで消す");
            Check.True(!q.Dismiss(t2.Id), "2 度は消さない");
            var t5 = q.Enqueue("5", 1f);
            var t6 = q.Enqueue("6", 1f);
            Check.Equal(ToastPhase.Pending, t5.Phase, "出ていく途中の 2 も場所を取るので 5 は待つ");
            Check.Equal(ToastPhase.Pending, t6.Phase, "満杯なら待つ");
            Check.True(q.Dismiss(t6.Id), "待っているものも消せる");
            Check.Equal(1, q.PendingCount, "6 は待ちから外れ、5 が待つ");
            Check.Equal("5", string.Join(",", q.Remove(t2.Id).Select(t => t.Message)), "2 が出終わったら 5 を出す");
            Check.Equal(ToastPhase.Shown, t5.Phase, "5 は見せている");
            q.SetHeld(t1.Id, false);
            Check.True(q.Tick(2.1f).Expired.Any(t => t.Message == "1"), "放したら数える（押さえていた間は止まっていた）");
        });

        // ── フォーカス ─────────────────────────────────────────
        h.Add("W2-7 フォーカス: 範囲の前後・後ろの範囲は奪わない・閉じたら前の相手へ戻る・外す", () =>
        {
            var focus = new FocusModel<string>();
            var screenA = new object();
            var dialog = new object();
            Check.True(focus.Request("root-wheel", null), "範囲が無ければ根の範囲の相手になる");
            focus.PushScope(screenA);
            Check.True(focus.Current is null, "新しい画面の範囲はまだ相手なし");
            Check.True(focus.Request("wheelA", screenA), "画面 A のホイール");
            focus.PushScope(dialog);
            Check.True(focus.Current is null, "ダイアログが前に出た");
            Check.True(!focus.Request("wheelA2", screenA), "後ろの画面の相手は今のフォーカスを奪わない");
            Check.True(focus.Request("dialogField", dialog), "ダイアログの入力欄");
            focus.RemoveScope(dialog);
            Check.Equal("wheelA2", focus.Current, "閉じたら画面 A の覚えていた相手へ");
            var screenB = new object();
            focus.PushScope(screenB);
            focus.Request("wheelB", screenB);
            Check.Equal("wheelB", focus.Current, "画面 B（別のタブ）が前");
            focus.SendToBack(screenB);
            Check.Equal("wheelA2", focus.Current, "B を後ろへ回すと A の相手（タブを離れた）");
            focus.BringToFront(screenB);
            Check.Equal("wheelB", focus.Current, "B を前へ出すと B の相手（タブへ戻った）");
            focus.RemoveScope(screenB);
            focus.SendToBack(screenA);
            Check.Equal("wheelA2", focus.Current, "後ろへ回しても根の範囲より前（根の範囲はいつもいちばん後ろ）");
            Check.True(focus.Release("wheelA2"), "今のフォーカスを外した");
            Check.True(focus.Current is null, "外した後は相手なし");
            Check.True(!focus.Release("nope"), "知らない相手");
            Check.Equal(screenA, focus.TopScope, "いちばん前の範囲");
            focus.Request("moved", dialog);
            Check.True(focus.HasScope(dialog), "知らない範囲は後ろへ足す");
            Check.Equal(screenA, focus.TopScope, "足しても前には出ない");
        });

        h.Add("W2-7 フォーカス: 重ねる範囲（ダイアログ）は開いている間、下で画面が前へ出ても前のまま", () =>
        {
            var focus = new FocusModel<string>();
            var screen1 = new object();
            var screen2 = new object();
            var sheet = new object();
            focus.PushScope(screen1);
            focus.Request("wheel1", screen1);
            focus.PushScope(sheet, overlay: true);
            focus.Request("sheetField", sheet);
            focus.PushScope(screen2);
            focus.Request("wheel2", screen2);
            Check.Equal("sheetField", focus.Current, "シートが開いている間は、下で積んだ画面の相手にならない");
            focus.BringToFront(screen1);
            Check.Equal("sheetField", focus.Current, "画面を前へ出してもシートの後ろ");
            focus.RemoveScope(sheet);
            Check.Equal("wheel1", focus.Current, "シートを閉じたら、いちばん前の画面（最後に前へ出した 1）の相手");
        });

        // ── 重なりのレイヤー・タブの見た目 ─────────────────────
        h.Add("W2-7 レイヤー: 段・帯の値（覆い < シート < ダイアログ < トースト）・指定の段・上限", () =>
        {
            Check.Equal(10_000, UiLayers.StackStep(theme, 0), "テーマの 1 段");
            Check.Equal(1_000, UiLayers.StackStep(theme, 1_000), "タブの中のスタックの指定");
            Check.Equal(20_000, UiLayers.ScreenBias(2, 10_000), "段 2");
            Check.Equal(0, UiLayers.ScreenBias(-3, 10_000), "負の段は 0");
            int overlay = UiLayers.Band(theme, ModalKind.Overlay), sheet = UiLayers.Band(theme, ModalKind.Sheet);
            int dialog = UiLayers.Band(theme, ModalKind.Dialog), toast = UiLayers.ToastBand(theme);
            Check.True(overlay < sheet && sheet < dialog && dialog < toast, "帯の順");
            Check.True(UiLayers.ScreenBias(50, UiLayers.StackStep(theme, 0)) < overlay, "画面のスタック 50 段でも覆いより下");
            Check.Equal(UiLayers.MaxBias, UiLayers.ScreenBias(int.MaxValue, int.MaxValue), "上限へ収める");
        });

        h.Add("W2-7 タブの見た目: 選んだ（印・文字）・選んでいない・押している・無効", () =>
        {
            var sel = TabLooks.Resolve(true, false, false, theme);
            Check.True(sel.IndicatorVisible, "選んだら印");
            Check.Equal(theme.Color(UiTokens.ColorSelected), sel.Indicator, "印は選択の色");
            Check.Equal(theme.Color(UiTokens.ColorOnSurface), sel.Content, "文字は面の文字");
            var off = TabLooks.Resolve(false, false, false, theme);
            Check.True(!off.IndicatorVisible, "選んでいなければ印なし");
            Check.Equal(theme.Color(UiTokens.ColorOnSurfaceMuted), off.Content, "控えめな文字");
            var pressed = TabLooks.Resolve(false, true, false, theme);
            Check.True(pressed.IndicatorVisible && pressed.Indicator.a > 0f, "押している間は重ね色の印");
            var disabled = TabLooks.Resolve(true, true, true, theme);
            Check.True(!disabled.IndicatorVisible, "無効は印なし");
            Check.Equal(theme.Color(UiTokens.ColorOnDisabled), disabled.Content, "無効の文字");
            Check.Close(56, sel.IndicatorSize.x, Eps, "印の幅（テーマ）");
        });
    }

    /// <summary>
    /// 戻るの段の全パターンの検査に使う小さなアプリ（BackChain の既定の並びと、タブ・スタックの決め方を使う）。
    /// 期待（Expected）は仕様の順を素直に書き下したもの、実際（Press）は BackChain・NavigatorOrder・TabModel が決める。
    /// </summary>
    private sealed class FakeApp
    {
        public bool ImeOpen;
        public int Dialogs, Sheets, Overlays;
        public int RootDepth;
        public int SelectedTab;
        public int TabDepth;
        public bool MovedToBack;

        /// <summary>仕様の順（IME → ダイアログ → シート → 覆い → 根のスタック → 選んだタブのスタック → 最初のタブへ → 背面へ）。</summary>
        public string Expected()
        {
            if (ImeOpen) return "focus";
            if (Dialogs > 0) return "dialog";
            if (Sheets > 0) return "sheet";
            if (Overlays > 0) return "overlay";
            if (RootDepth > 1) return "nav:root";
            if (TabDepth > 1) return "nav:tab";
            if (SelectedTab != TabModel.FirstTab) return "nav:first-tab";
            return "move_task_to_back";
        }

        /// <summary>戻るを 1 回押す（BackChain で配り、受けた層の名前を返す）。</summary>
        public string Press()
        {
            var chain = new BackChain();
            string handledBy = string.Empty;
            chain.Add(BackOrder.Focus, "focus", () => { if (!ImeOpen) return false; ImeOpen = false; return true; });
            chain.Add(BackOrder.Dialog, "dialog", () => { if (Dialogs == 0) return false; Dialogs--; return true; });
            chain.Add(BackOrder.Sheet, "sheet", () => { if (Sheets == 0) return false; Sheets--; return true; });
            chain.Add(BackOrder.Overlay, "overlay", () => { if (Overlays == 0) return false; Overlays--; return true; });
            chain.Add(BackOrder.Navigation, "nav", () =>
            {
                // ナビゲーター: 根のスタック（深さ 1）・タブ（深さ 2）・選んだタブのスタック（深さ 3。根のスタックが上の段のときだけ見える）
                var navigators = new List<(string, int, bool)>
                {
                    ("root", 1, true),
                    ("tabhost", 2, RootDepth == 1),
                    ("tabstack", 3, RootDepth == 1),
                };
                foreach (var n in NavigatorOrder.InnermostFirst(navigators))
                {
                    switch (n)
                    {
                        case "tabstack" when TabDepth > 1:
                            TabDepth--;
                            handledBy = "nav:tab";
                            return true;
                        case "tabhost":
                            var tabs = new TabModel(2, SelectedTab);
                            if (tabs.DecideBack(TabDepth, backToFirstTab: true) == TabBackAction.SelectFirst)
                            {
                                SelectedTab = TabModel.FirstTab;
                                handledBy = "nav:first-tab";
                                return true;
                            }
                            break;
                        case "root" when RootDepth > 1:
                            RootDepth--;
                            handledBy = "nav:root";
                            return true;
                    }
                }
                return false;
            });
            var result = chain.Dispatch();
            if (!result.Handled)
            {
                MovedToBack = true;
                return "move_task_to_back";
            }
            return result.Layer == "nav" ? handledBy : result.Layer;
        }
    }
}
