using System;
using System.Collections.Generic;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 方向キー・パッドで UI 部品のフォーカスを移して押す仕組み（2026-10-03。lane3。L3-6。docs/ui_navigation.md §7.2）の純粋な計算のテスト:
/// 向きの最寄りの選び方（NavigationMath.PickNext: Unity の Automatic と同じ点数・内積が負の候補を除く・同点の決め方・端で止まる・Wrap）、
/// 最初の部品（読む順）・消えたときの最寄り・スクロールで見せる量、連続移動の時計（NavRepeatClock）、同時押しとスティック（NavInputMath）、
/// 入力の扱いの決まり（NavInputPolicy）、フォーカスの範囲での絞り込み（NavScopeFilter。偽の候補と偽の範囲）、範囲ごとの覚え（NavScopeMemory）、
/// 枠の置き場（FocusRingMath）、セグメント・ラジオの左右の選び方（SelectionModel.NextSelectable）、既定のテーマの枠のトークン。
/// </summary>
public static class KeyNavigationTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;

    // ── 格子の見本（3 列 × 2 行のボタン 120×48・間 16。左上 (0, 0)）──
    private const float W = 120f, H = 48f, G = 16f;

    /// <summary>格子の c 列 r 行のボタンの矩形。</summary>
    private static Rect Cell(int c, int r) => new(c * (W + G), r * (H + G), W, H);

    /// <summary>3 × 2 の格子（添字 = r × 3 + c）。</summary>
    private static List<Rect> Grid()
    {
        var list = new List<Rect>();
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 3; c++) list.Add(Cell(c, r));
        return list;
    }

    /// <summary>今の部品を除いた候補と、元の添字への写し。</summary>
    private static (List<Rect> rects, List<int> map) Without(List<Rect> all, int current)
    {
        var rects = new List<Rect>();
        var map = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            if (i == current) continue;
            rects.Add(all[i]);
            map.Add(i);
        }
        return (rects, map);
    }

    /// <summary>格子の current から向きへ移った先の元の添字（-1 = 動かない）。</summary>
    private static int Step(List<Rect> all, int current, FocusDirection direction, bool wrap = false)
    {
        var (rects, map) = Without(all, current);
        int i = NavigationMath.PickNext(all[current], rects, direction, wrap);
        return i < 0 ? -1 : map[i];
    }

    public static void Register(TestHarness h, UiThemeData theme)
    {
        RegisterPick(h);
        RegisterHelpers(h);
        RegisterClock(h);
        RegisterInput(h);
        RegisterScope(h);
        RegisterRing(h, theme);
    }

    // ── 1. 向きの最寄りの選び方 ────────────────────────────────

    private static void RegisterPick(TestHarness h)
    {
        h.Add("方向キー: 格子の上下左右は隣のボタン（3 × 2・右下からも左上からも）", () =>
        {
            var g = Grid();
            Check.Equal(1, Step(g, 0, FocusDirection.Right), "左上 → 右");
            Check.Equal(3, Step(g, 0, FocusDirection.Down), "左上 → 下");
            Check.Equal(4, Step(g, 1, FocusDirection.Down), "上の真ん中 → 下の真ん中（斜めの 3・5 ではない）");
            Check.Equal(4, Step(g, 5, FocusDirection.Left), "右下 → 左");
            Check.Equal(2, Step(g, 5, FocusDirection.Up), "右下 → 上");
            Check.Equal(0, Step(g, 1, FocusDirection.Left), "上の真ん中 → 左");
        });

        h.Add("方向キー: 内積が負・0（後ろ・真横の延長の上）の候補は選ばない＝端で止まる", () =>
        {
            var g = Grid();
            Check.Equal(-1, Step(g, 0, FocusDirection.Left), "左端で左 → 動かない");
            Check.Equal(-1, Step(g, 0, FocusDirection.Up), "上端で上 → 動かない");
            Check.Equal(-1, Step(g, 2, FocusDirection.Right), "右端で右 → 動かない（下の行の左端へ回らない）");
            Check.Equal(-1, Step(g, 4, FocusDirection.Down), "下端で下 → 動かない");
            // 起点は辺の中点: 中心が今の部品の中（辺より手前）にある候補は選ばない
            var inner = new List<Rect> { new(40f, 10f, 20f, 20f) };
            Check.Equal(-1, NavigationMath.PickNext(new Rect(0f, 0f, 100f, 50f), inner, FocusDirection.Right), "中に中心がある候補は右に無い");
            Check.True(float.IsNaN(NavigationMath.Score(new Vector2(0f, 0f), new Rect(-30f, -5f, 10f, 10f), FocusDirection.Right)), "後ろの候補の点数は NaN");
        });

        h.Add("方向キー: 点数は Unity の Automatic（内積 / 距離²）。まっすぐな近い候補が、斜めの近い候補・まっすぐな遠い候補に勝つ", () =>
        {
            var current = new Rect(0f, 0f, 100f, 40f);
            // 起点 = 右の辺の中点 (100, 20)
            var straightNear = new Rect(150f, 0f, 100f, 40f);   // 中心 (200, 20): 内積 100・距離² 10000 → 0.01
            var diagonal = new Rect(150f, 80f, 100f, 40f);      // 中心 (200, 100): 内積 100・距離² 16400 → 0.0061
            var straightFar = new Rect(400f, 0f, 100f, 40f);    // 中心 (450, 20): 内積 350・距離² 122500 → 0.00286
            Check.Close(0.01, NavigationMath.Score(new Vector2(100f, 20f), straightNear, FocusDirection.Right), Eps, "点数");
            var list = new List<Rect> { straightFar, diagonal, straightNear };
            Check.Equal(2, NavigationMath.PickNext(current, list, FocusDirection.Right), "まっすぐで近い候補");
            list.RemoveAt(2);
            Check.Equal(1, NavigationMath.PickNext(current, list, FocusDirection.Right), "斜めでも近い方が遠いまっすぐより上（Unity と同じ）");
        });

        h.Add("方向キー: 同点は読む順（上 → 左）、それも同じなら先の添字", () =>
        {
            // 幅の広いスライダの真ん中から下: 左右対称の 2 つは同点 → 左
            var slider = new Rect(0f, 0f, 300f, 40f);
            var left = new Rect(30f, 80f, 100f, 40f);
            var right = new Rect(170f, 80f, 100f, 40f);
            Check.Equal(1, NavigationMath.PickNext(slider, new List<Rect> { right, left }, FocusDirection.Down), "左右対称は左");
            // 上下対称の 2 つ（右へ）: 上
            var current = new Rect(0f, 100f, 100f, 40f);
            var upper = new Rect(200f, 40f, 100f, 40f);
            var lower = new Rect(200f, 160f, 100f, 40f);
            Check.Equal(1, NavigationMath.PickNext(current, new List<Rect> { lower, upper }, FocusDirection.Right), "上下対称は上");
            // まったく同じ矩形 2 つ: 先の添字
            Check.Equal(0, NavigationMath.PickNext(current, new List<Rect> { upper, upper }, FocusDirection.Right), "同じなら先");
        });

        h.Add("方向キー: Wrap なら端で反対側の端へ回る（行・列の中で）。いちばん端だけの行では動かない", () =>
        {
            var g = Grid();
            Check.Equal(0, Step(g, 2, FocusDirection.Right, wrap: true), "上の行の右端 → 右 → 上の行の左端");
            Check.Equal(5, Step(g, 3, FocusDirection.Left, wrap: true), "下の行の左端 → 左 → 下の行の右端");
            Check.Equal(1, Step(g, 4, FocusDirection.Down, wrap: true), "下の真ん中 → 下 → 上の真ん中");
            Check.Equal(3, Step(g, 0, FocusDirection.Up, wrap: true), "左上 → 上 → 左下");
            Check.Equal(4, Step(g, 3, FocusDirection.Right, wrap: true), "端でなければ Wrap でもふつうに隣へ");
            // 縦 1 列の一覧で右: 右にも回った先にも自分しかいない → 動かない（別の行へ跳ばない）
            var column = new List<Rect> { new(0f, 0f, 100f, 40f), new(0f, 50f, 100f, 40f), new(0f, 100f, 100f, 40f) };
            Check.Equal(-1, Step(column, 1, FocusDirection.Right, wrap: true), "1 列で右 → 動かない");
            Check.Equal(-1, Step(column, 1, FocusDirection.Right, wrap: false), "Wrap なしでも動かない");
        });

        h.Add("方向キー: 使えない矩形（NaN・負の大きさ）と None の向きは選ばない", () =>
        {
            var current = new Rect(0f, 0f, 100f, 40f);
            var list = new List<Rect> { new(float.NaN, 0f, 10f, 10f), new(200f, 0f, -5f, 10f), new(300f, 0f, 100f, 40f) };
            Check.Equal(2, NavigationMath.PickNext(current, list, FocusDirection.Right), "壊れた矩形を飛ばす");
            Check.Equal(-1, NavigationMath.PickNext(current, list, FocusDirection.None), "向きなし");
            Check.Equal(-1, NavigationMath.PickNext(new Rect(float.NaN, 0f, 1f, 1f), list, FocusDirection.Right), "今の矩形が壊れている");
        });
    }

    // ── 2. 最初の部品・最寄り・スクロールで見せる量・セグメントの左右 ─────

    private static void RegisterHelpers(TestHarness h)
    {
        h.Add("最初の部品: いちばん上の行の左端（少しずれた行も同じ行）", () =>
        {
            var g = Grid();
            // 並びを入れ替えても左上
            var shuffled = new List<Rect> { g[5], g[1], g[3], g[0], g[4], g[2] };
            Check.Equal(3, NavigationMath.PickFirst(shuffled), "左上（並びの 3 番目）");
            // 上の行の右のボタンが 2 px 高い: 同じ行とみなして左端
            var tilted = new List<Rect> { new(200f, -2f, 100f, 40f), new(0f, 0f, 100f, 40f) };
            Check.Equal(1, NavigationMath.PickFirst(tilted), "少し高い右より左");
            Check.Equal(-1, NavigationMath.PickFirst(new List<Rect>()), "空");
        });

        h.Add("最寄り: 消えた部品の矩形にいちばん近い候補（同じ距離なら読む順）", () =>
        {
            var g = Grid();
            Check.Equal(2, NavigationMath.PickNearest(g[4], new List<Rect> { g[0], g[2], g[4] }), "同じ位置（並びの 2 番目）");
            var gone = Cell(1, 0);
            Check.Equal(1, NavigationMath.PickNearest(gone, new List<Rect> { g[2], g[0] }), "左右の同じ距離は左（並びの 1 番目の g[0]）");
            Check.Equal(-1, NavigationMath.PickNearest(new Rect(float.NaN, 0f, 1f, 1f), g), "壊れた矩形");
        });

        h.Add("スクロールで見せる量: 窓の下にはみ出たら正（先へ送る）、上なら負、入っていれば 0、窓より大きければ上の辺を合わせる", () =>
        {
            var viewport = new Rect(0f, 100f, 300f, 400f);   // y 100〜500
            var below = new Rect(10f, 480f, 100f, 50f);        // x 10〜110（余白込みで窓の中）・y 480〜530: 下へ 30 + 余白 5
            Check.Close(35, NavigationMath.RevealDelta(below, viewport, 5f).y, Eps, "下");
            var above = new Rect(0f, 80f, 100f, 50f);          // y 80〜130: 上へ 20 + 余白 5
            Check.Close(-25, NavigationMath.RevealDelta(above, viewport, 5f).y, Eps, "上");
            Check.Close(0, NavigationMath.RevealDelta(new Rect(0f, 200f, 100f, 50f), viewport, 5f).y, Eps, "入っている");
            var tall = new Rect(0f, 300f, 100f, 600f);
            Check.Close(195, NavigationMath.RevealDelta(tall, viewport, 5f).y, Eps, "窓より大きい: 上の辺 − 余白を窓の上へ");
            Check.Close(0, NavigationMath.RevealDelta(below, viewport, 5f).x, Eps, "横は入っている");
            Check.Close(0, NavigationMath.RevealDelta(below, viewport, float.NaN).y - 30f, Eps, "NaN の余白は 0");
        });

        h.Add("セグメント・ラジオの左右: 次の選べる項目（選べない項目は飛ばす・端で止まる・何も選んでいなければ端から）", () =>
        {
            var model = new SelectionModel { Mode = SelectionMode.Single };
            model.Resize(4);
            model.SetDisabled(2, true);
            Check.Equal(1, model.NextSelectable(0, 1), "0 → 右 → 1");
            Check.Equal(3, model.NextSelectable(1, 1), "1 → 右 → 2 は選べないので 3");
            Check.Equal(-1, model.NextSelectable(3, 1), "右端");
            Check.Equal(1, model.NextSelectable(3, -1), "3 → 左 → 2 を飛ばして 1");
            Check.Equal(-1, model.NextSelectable(0, -1), "左端");
            Check.Equal(0, model.NextSelectable(-1, 1), "何も選んでいない → 右 → 先頭");
            Check.Equal(3, model.NextSelectable(-1, -1), "何も選んでいない → 左 → 末尾");
            Check.Equal(-1, model.NextSelectable(0, 0), "向き 0");
        });
    }

    // ── 3. 連続移動の時計 ─────────────────────────────────────

    private static void RegisterClock(TestHarness h)
    {
        h.Add("連続移動: 押した瞬間に 1 回、最初の遅延（0.5 秒）の後にもう 1 回、以後 0.1 秒ごと・離したら数え直す", () =>
        {
            var clock = new NavRepeatClock();
            const float Frame = 1f / 60f;
            Check.Equal(FocusDirection.Down, clock.Update(FocusDirection.Down, Frame), "押した瞬間");
            int moves = 0;
            float t = 0f;
            // 0.49 秒まで押し続ける: 動かない
            while (t + Frame < NavRepeatClock.DefaultInitialDelay - 0.01f)
            {
                t += Frame;
                if (clock.Update(FocusDirection.Down, Frame) != FocusDirection.None) moves++;
            }
            Check.Equal(0, moves, "遅延の間は動かない");
            // さらに 1 秒: 遅延の後 1 回 + 0.1 秒ごと ≒ 10 回（フレームの丸めで ±1）
            int repeats = 0;
            for (int i = 0; i < 60; i++)
                if (clock.Update(FocusDirection.Down, Frame) != FocusDirection.None) repeats++;
            Check.True(repeats >= 9 && repeats <= 11, $"1 秒の繰り返し {repeats} 回（9〜11）");
            Check.Equal(FocusDirection.None, clock.Update(FocusDirection.None, Frame), "離す");
            Check.Equal(FocusDirection.Down, clock.Update(FocusDirection.Down, Frame), "押し直すとすぐ 1 回");
        });

        h.Add("連続移動: 向きを変えたらその場で 1 回・重いフレームでもまとめて動かない・dt の NaN と負は 0", () =>
        {
            var clock = new NavRepeatClock();
            clock.Update(FocusDirection.Down, 0.016f);
            Check.Equal(FocusDirection.Right, clock.Update(FocusDirection.Right, 0.016f), "向きを変えた瞬間");
            Check.Equal(FocusDirection.Right, clock.Update(FocusDirection.Right, 3f), "3 秒止まったフレーム: 1 回だけ");
            Check.Equal(FocusDirection.None, clock.Update(FocusDirection.Right, 0.05f), "次の間隔まで待つ（まとめて取り戻さない）");
            Check.Equal(FocusDirection.Right, clock.Update(FocusDirection.Right, 0.06f), "間隔が来たら 1 回");
            var other = new NavRepeatClock();
            other.Update(FocusDirection.Up, 0f);
            Check.Equal(FocusDirection.None, other.Update(FocusDirection.Up, float.NaN), "NaN は進まない");
            Check.Equal(FocusDirection.None, other.Update(FocusDirection.Up, -1f), "負は進まない");
            Check.Equal(FocusDirection.Up, other.Held, "押し続けている向き");
        });
    }

    // ── 4. 同時押し・スティック・入力の扱い ─────────────────────────

    private static void RegisterInput(TestHarness h)
    {
        h.Add("入力: このフレームに押した向きを優先・前の向きを保つ・反対の同時押しは打ち消す・離せば None", () =>
        {
            var up = FocusDirectionSet.Up;
            var right = FocusDirectionSet.Right;
            Check.Equal(FocusDirection.Up, NavInputMath.ResolveDigital(up, up, FocusDirection.None), "押した瞬間");
            Check.Equal(FocusDirection.Right, NavInputMath.ResolveDigital(up | right, right, FocusDirection.Up), "上を押したまま右を押した → 右");
            Check.Equal(FocusDirection.Right, NavInputMath.ResolveDigital(up | right, FocusDirectionSet.None, FocusDirection.Right), "両方押し続け → 前の右を保つ");
            Check.Equal(FocusDirection.Up, NavInputMath.ResolveDigital(up, FocusDirectionSet.None, FocusDirection.Right), "右を離した → 上");
            Check.Equal(FocusDirection.None, NavInputMath.ResolveDigital(up | FocusDirectionSet.Down, FocusDirectionSet.None, FocusDirection.Up), "上と下 → 打ち消し");
            Check.Equal(FocusDirection.Left, NavInputMath.ResolveDigital(up | FocusDirectionSet.Down | FocusDirectionSet.Left, FocusDirectionSet.None, FocusDirection.None), "上下を打ち消して左");
            Check.Equal(FocusDirection.None, NavInputMath.ResolveDigital(FocusDirectionSet.None, FocusDirectionSet.None, FocusDirection.Up), "離した");
        });

        h.Add("入力: スティックは主な軸・押す閾値 0.5・放す閾値 0.3（ヒステリシス）・Y の正は上・同じ大きさは縦", () =>
        {
            Check.Equal(FocusDirection.None, NavInputMath.StickDirection(new Vector2(0.4f, 0f), FocusDirection.None), "閾値の下");
            Check.Equal(FocusDirection.Right, NavInputMath.StickDirection(new Vector2(0.6f, 0.2f), FocusDirection.None), "右");
            Check.Equal(FocusDirection.Up, NavInputMath.StickDirection(new Vector2(0.1f, 0.7f), FocusDirection.None), "Y の正 = 上");
            Check.Equal(FocusDirection.Down, NavInputMath.StickDirection(new Vector2(0f, -0.9f), FocusDirection.None), "下");
            Check.Equal(FocusDirection.Up, NavInputMath.StickDirection(new Vector2(0.6f, 0.6f), FocusDirection.None), "斜めの同じ大きさ → 縦");
            Check.Equal(FocusDirection.Right, NavInputMath.StickDirection(new Vector2(0.35f, 0f), FocusDirection.Right), "放す閾値の上では保つ");
            Check.Equal(FocusDirection.None, NavInputMath.StickDirection(new Vector2(0.25f, 0f), FocusDirection.Right), "放す閾値の下で放す");
            Check.Equal(FocusDirection.Up, NavInputMath.StickDirection(new Vector2(0.4f, 0.8f), FocusDirection.Right), "もう一方の軸が押す閾値を超えて大きい → 乗り換え");
            Check.Equal(FocusDirection.None, NavInputMath.StickDirection(new Vector2(float.NaN, 0f), FocusDirection.None), "NaN は 0");
            Check.Equal(FocusDirection.Left, NavInputMath.Combine(FocusDirection.Left, FocusDirection.Up), "キーが先");
            Check.Equal(FocusDirection.Up, NavInputMath.Combine(FocusDirection.None, FocusDirection.Up), "キーが無ければスティック");
        });

        h.Add("入力の扱い: フォーカスが無ければ最初へ・枠を隠していれば 1 回目は枠を出すだけ（指で選んだホイールはすぐ）・文字入力の間と次のフレームは読まない", () =>
        {
            Check.Equal(NavInputDecision.FocusFirst, NavInputPolicy.Decide(false, true, true), "フォーカスなし");
            Check.Equal(NavInputDecision.Reveal, NavInputPolicy.Decide(true, false, false), "枠を隠している");
            Check.Equal(NavInputDecision.Act, NavInputPolicy.Decide(true, false, true), "指でホイール・入力欄を選んだ直後");
            Check.Equal(NavInputDecision.Act, NavInputPolicy.Decide(true, true, false), "枠を出している");
            Check.True(NavInputPolicy.IsSuspended(true, false) && NavInputPolicy.IsSuspended(false, true), "入力の間とその次");
            Check.True(!NavInputPolicy.IsSuspended(false, false), "それ以外は読む");
            Check.True(NavInputPolicy.RoutesToAdjust(NavAxis.Horizontal, FocusDirection.Left), "横の部品の左 → 値");
            Check.True(!NavInputPolicy.RoutesToAdjust(NavAxis.Horizontal, FocusDirection.Down), "横の部品の下 → 移る");
            Check.True(NavInputPolicy.RoutesToAdjust(NavAxis.Vertical, FocusDirection.Up), "ホイールの上 → 値");
            Check.True(!NavInputPolicy.RoutesToAdjust(NavAxis.None, FocusDirection.Up), "値の無い部品 → 移る");
            Check.Equal(-1, FocusDirections.SignOf(FocusDirection.Up), "上 = −1（ホイールの前の行）");
            Check.Equal(1, FocusDirections.SignOf(FocusDirection.Right), "右 = +1");
            Check.Equal(FocusDirection.Left, FocusDirections.Opposite(FocusDirection.Right), "逆");
        });
    }

    // ── 5. フォーカスの範囲での絞り込みと覚え ──────────────────────

    /// <summary>偽の候補（名前と範囲）。</summary>
    private sealed record FakeItem(string Name, object? Scope);

    private static void RegisterScope(TestHarness h)
    {
        h.Add("範囲: いちばん前の範囲・それを中に含む範囲（シェルのタブのバー）・重ねる範囲でなければ根の部品へ移れる", () =>
        {
            var shell = new object();      // 根の画面（シェル）
            var tabScreen = new object();  // シェルの中のタブの画面（いちばん前）
            var hidden = new object();     // 選んでいないタブの画面（後ろ）
            var dialog = new object();     // ダイアログ（重ねる範囲）
            var items = new List<FakeItem>
            {
                new("tab-bar", shell), new("page-button", tabScreen), new("hidden-tab", hidden), new("hud", null), new("dialog-ok", dialog),
            };
            bool Contains(object a, object b) => ReferenceEquals(a, shell) && ReferenceEquals(b, tabScreen);

            var onScreen = NavScopeFilter.Filter(items, i => i.Scope, tabScreen, Contains, topIsOverlay: false);
            Check.Equal("tab-bar,page-button,hud", string.Join(",", onScreen.ConvertAll(i => i.Name)), "画面がいちばん前: 自分・含む範囲・根");

            var onDialog = NavScopeFilter.Filter(items, i => i.Scope, dialog, Contains, topIsOverlay: true);
            Check.Equal("dialog-ok", string.Join(",", onDialog.ConvertAll(i => i.Name)), "ダイアログがいちばん前: ダイアログの中だけ（下の画面・根へ移らない）");

            var onRoot = NavScopeFilter.Filter(items, i => i.Scope, null, Contains, topIsOverlay: false);
            Check.Equal("hud", string.Join(",", onRoot.ConvertAll(i => i.Name)), "範囲が無い: 根の部品だけ");

            Check.True(NavScopeFilter.Allows(null, shell, false, topIsOverlay: false), "根の部品は画面の上で移れる");
            Check.True(!NavScopeFilter.Allows(null, dialog, false, topIsOverlay: true), "根の部品はダイアログの上で移れない");
            Check.True(!NavScopeFilter.Allows(hidden, tabScreen, false, false), "兄弟の範囲へは移れない");
            Check.True(!NavScopeFilter.Allows(shell, null, true, false), "根がいちばん前なら範囲の部品へ移れない");
        });

        h.Add("覚え: 範囲ごとに最後の相手・同じ相手は 1 つの範囲だけ・生きていなければ忘れる・根は null の鍵", () =>
        {
            var memory = new NavScopeMemory<FakeItem>();
            var screen = new object();
            var dialog = new object();
            var a = new FakeItem("a", screen);
            var b = new FakeItem("b", dialog);
            var root = new FakeItem("root", null);
            memory.Remember(screen, a);
            memory.Remember(dialog, b);
            memory.Remember(null, root);
            Check.Equal(a, memory.Recall(screen, _ => true), "画面の覚え");
            Check.Equal(root, memory.Recall(null, _ => true), "根の覚え");
            memory.Remember(dialog, a);
            Check.True(memory.Recall(screen, _ => true) is null, "同じ相手を別の範囲で覚えたら前の範囲は忘れる");
            Check.Equal(a, memory.Recall(dialog, _ => true), "新しい範囲で覚えている");
            Check.True(memory.Recall(dialog, _ => false) is null, "生きていなければ null");
            Check.True(memory.Recall(dialog, _ => true) is null, "そして忘れている");
            memory.Prune(item => item.Name != "root");
            Check.Equal(0, memory.Count, "生きていない相手を掃除");
        });
    }

    // ── 6. 枠の置き場と既定のテーマ ────────────────────────────────

    private static void RegisterRing(TestHarness h, UiThemeData theme)
    {
        h.Add("枠: 部品の矩形（画素）を親の単位へ写し、間 + 太さだけ外へ広げる・角丸は同心（角丸でなければ既定）", () =>
        {
            // 2 倍の画面（1 dp = 2 px）・親の左上 (10, 20) px・部品 (110, 220) px に 240×96 px（= 120×48 dp）
            var ring = FocusRingMath.RingRect(new Rect(110f, 220f, 240f, 96f), new Vector2(10f, 20f), 2f, 2f, 3f);
            Check.Close(45, ring.x, Eps, "左 = (110 − 10) / 2 − 5");
            Check.Close(95, ring.y, Eps, "上 = (220 − 20) / 2 − 5");
            Check.Close(130, ring.width, Eps, "幅 = 120 + 10");
            Check.Close(58, ring.height, Eps, "高さ = 48 + 10");
            Check.Close(17, FocusRingMath.RingRadius(12f, 2f, 3f, 4f), Eps, "角丸 12 のボタン → 12 + 2 + 3");
            Check.Close(4, FocusRingMath.RingRadius(0f, 2f, 3f, 4f), Eps, "角丸でない → 既定");
            Check.Close(2, FocusRingMath.PxPerUnit(720f, 360f, 1f), Eps, "1 単位の画素数");
            Check.Close(3, FocusRingMath.PxPerUnit(0f, 360f, 3f), Eps, "測れていなければ代わり");
            Check.Close(1, FocusRingMath.PxPerUnit(float.NaN, 0f, float.NaN), Eps, "どれも無ければ 1");
        });

        h.Add("枠: 既定のテーマが枠のトークンを持ち、枠のレイヤーはトーストより上（明暗とも色がある）", () =>
        {
            Check.True(theme.Has(NavTokens.ColorFocusRing), "color.focus_ring");
            Check.Close(3, theme.Number(NavTokens.SizeFocusRingWidth), Eps, "size.focus_ring_width");
            Check.Close(2, theme.Number(NavTokens.SizeFocusRingGap), Eps, "size.focus_ring_gap");
            Check.Close(4, theme.Number(NavTokens.RadiusFocusRing), Eps, "radius.focus_ring");
            Check.True(UiLayers.FocusRingBand(theme) > UiLayers.ToastBand(theme), "トーストの帯より上");
            Check.True(UiLayers.FocusRingBand(theme) <= UiLayers.MaxBias, "底上げの上限の中");
            Check.Equal(UiLayers.DefaultFocusRingBand, UiLayers.FocusRingBand(UiThemeData.Empty()), "テーマに無ければ既定の帯");
        });
    }
}
