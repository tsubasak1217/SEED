using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Panels.AnimationTimeline;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace AnimationTimelineTests;

/// <summary>
/// アニメーションタイムラインの純ロジック単体テスト。
///
/// <para>検証の柱:</para>
/// <list type="number">
///   <item>フレーム⇔秒の変換とスナップ（AnimFrameMath）</item>
///   <item>キー挿入・同一フレーム上書き・整列（AnimKeyEditor）</item>
///   <item>祖先 Animator の探索と actor_path 生成（AnimHierarchyNav）</item>
///   <item>Undo / Redo スタック（AnimUndoStack）</item>
///   <item>ACTOR_COMPONENTS からの現在値抽出（AnimActorSnapshot）</item>
///   <item>.anim の fps ラウンドトリップと旧ファイル互換（AnimClipIO）</item>
///   <item>複数選択モデルと正規化（AnimKeySelection）</item>
///   <item>選択キーの一括移動・上書き・削除（AnimKeyEditor）</item>
///   <item>キーのコピー / 貼り付け（AnimKeyClipboard）</item>
///   <item>矩形選択・ズームの座標計算（AnimDopeSheetLayout / AnimTimelineZoom）</item>
///   <item>選択込み Undo / Redo（AnimUndoStack + スナップショット規約）</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── 1. フレーム⇔秒 ──
        harness.Add("秒とフレームが往復する",                       FrameTimeRoundTrip);
        harness.Add("端数の時刻は最寄りフレームへスナップする",       SnapRoundsToNearestFrame);
        harness.Add("不正な fps は既定値へ矯正される",               FpsIsNormalized);
        harness.Add("duration に収まる最終フレームを求められる",     LastFrameFitsDuration);
        harness.Add("スナップは [0, duration] にクランプされる",     SnapClampsToRange);

        // ── 2. キー挿入 / 上書き ──
        harness.Add("空トラックへキーを挿入できる",                 InsertIntoEmptyTrack);
        harness.Add("同一フレームのキーは上書きされる（増えない）", InsertMergesOnSameFrame);
        harness.Add("挿入後もキーは時刻昇順に保たれる",             InsertKeepsSortOrder);
        harness.Add("値の要素数は value_type に合わせられる",       ValuesAreFittedToValueType);
        harness.Add("直前キーの値を初期値として取れる",             PreviousValuesAreCloned);

        // ── 2b. サマリー行（全チャンネル）──
        harness.Add("サマリーはいずれかのトラックのキー時刻を集約する",   SummaryFramesUnionsAllTracks);
        harness.Add("サマリー挿入は actor_path 一致トラックだけへ打つ",   InsertOnAllTracksFiltersByActorPath);
        harness.Add("サマリー挿入は一致が無ければ全トラックへ打つ",       InsertOnAllTracksFallsBackToAllTracks);
        harness.Add("サマリー移動は該当フレームのキーだけ動かす",         MoveKeysAtFrameMovesOnlyMatchingKeys);
        harness.Add("サマリー削除は該当フレームのキーだけ消す",           DeleteKeysAtFrameDeletesOnlyMatchingKeys);

        // ── 3. 祖先探索 / actor_path ──
        harness.Add("祖先を遡って最も近い Animator を見つける",     FindsNearestAnimatorAncestor);
        harness.Add("Animator が無ければ null を返す",              NoAnimatorReturnsNull);
        harness.Add("actor_path はフォルダを飛ばして作られる",      ActorPathSkipsFolders);
        harness.Add("Animator 自身への actor_path は空文字列",      ActorPathForSelfIsEmpty);
        harness.Add("祖先関係にないアクタへのパスは null",          ActorPathForUnrelatedIsNull);
        harness.Add("親リンクが循環しても無限ループしない",         CyclicParentDoesNotHang);
        harness.Add("HIERARCHY JSON を解析できる",                  ParsesHierarchyJson);

        // ── 4. Undo / Redo ──
        harness.Add("Undo で 1 つ前の状態へ戻る",                   UndoRestoresPrevious);
        harness.Add("Redo で戻した編集をやり直せる",                RedoReappliesEdit);
        harness.Add("新しい編集で Redo 系列が破棄される",           NewEditClearsRedo);
        harness.Add("同じ内容の Push は履歴を汚さない",             IdenticalPushIsIgnored);
        harness.Add("履歴は上限を超えて溜まらない",                 UndoRespectsCapacity);

        // ── 5. 現在値スナップショット ──
        harness.Add("3D の transform から現在値を取れる",           SnapshotReadsTransform);
        harness.Add("2D の canvas_transform から現在値を取れる",    SnapshotReadsCanvasTransform);
        harness.Add("Sprite / Text の色を取れる",                   SnapshotReadsColors);
        harness.Add("壊れた JSON でも例外を出さない",               SnapshotSurvivesBrokenJson);

        // ── 6. .anim の fps 互換 ──
        harness.Add("fps 無しの旧 .anim は既定 fps で読める",       LegacyClipGetsDefaultFps);
        harness.Add("fps は保存・再読込で往復する",                 FpsRoundTripsThroughJson);

        // ── 7. 複数選択モデル ──
        harness.Add("Ctrl クリック相当のトグルで選択が反転する",     SelectionTogglesKeys);
        harness.Add("Shift クリック相当の追加で選択が増える",        SelectionAddsKeys);
        harness.Add("削除後に選択の添字が詰められる",                SelectionNormalizesAfterDelete);
        harness.Add("範囲外の選択は正規化で捨てられる",              SelectionNormalizeDropsOutOfRange);
        harness.Add("同一フレームのキーをまとめて選択できる",        SelectionSelectsWholeFrame);
        harness.Add("選択は JSON で往復する",                        SelectionRoundTripsThroughJson);

        // ── 8. 選択キーの一括移動・削除・補間変更 ──
        harness.Add("選択キーはまとめて移動する",                    MoveSelectionShiftsAllKeys);
        harness.Add("移動先の既存キーは置き換えられる",              MoveSelectionReplacesOverlappedKey);
        harness.Add("移動量は 0 フレーム未満へはみ出さない",         MoveSelectionClampsAtFrameZero);
        harness.Add("移動量は最終フレームを超えない",                MoveSelectionClampsAtLastFrame);
        harness.Add("選択キーをまとめて削除できる",                  DeleteSelectionRemovesAllKeys);
        harness.Add("選択キーの補間をまとめて変えられる",            SetInterpAppliesToSelection);

        // ── 9. コピー / 貼り付け ──
        harness.Add("コピーは先頭キーからの相対フレームで持つ",      CopyStoresRelativeFrames);
        harness.Add("貼り付けはプレイヘッド基準で相対位置を保つ",    PastePlacesKeysRelativeToPlayhead);
        harness.Add("貼り付け先に無いトラックは作られる",            PasteCreatesMissingTrack);
        harness.Add("同一フレームのキーは貼り付けで上書きされる",    PasteReplacesKeysOnSameFrame);
        harness.Add("クリップボード JSON は往復する",                ClipboardJsonRoundTrips);
        harness.Add("無関係なテキストは貼り付け対象にならない",      ClipboardRejectsForeignText);

        // ── 10. 座標計算（矩形選択・ズーム）──
        harness.Add("矩形内のキーだけを拾う",                        MarqueeSelectsKeysInsideRect);
        harness.Add("矩形は始点と終点の順序を問わない",              MarqueeAcceptsReversedRect);
        harness.Add("ズームしてもカーソル下の時刻が動かない",        ZoomKeepsTimeUnderCursor);
        harness.Add("ズーム倍率は上下限でクランプされる",            ZoomClampsToRange);
        harness.Add("F はクリップ全体が収まる倍率を返す",            FitFillsViewportWidth);

        // ── 11. 選択込み Undo / Redo ──
        harness.Add("一括削除の Undo でキーと選択が戻る",            UndoRestoresDeletedKeysAndSelection);
        harness.Add("Redo で一括削除がやり直される",                 RedoReappliesDeletion);
        harness.Add("N 回の移動は N 回の Undo で元へ戻る",           UndoUnwindsRepeatedNudges);
        harness.Add("貼り付けの Undo で作られたトラックも消える",    UndoRemovesPastedTrack);

        return harness.Run();
    }

    // ── 共通ヘルパー ────────────────────────────────────────────

    /// <summary>vec3 トラックを 1 本作る。</summary>
    private static AnimTrack Vec3Track(string actorPath = "", string property = "position") => new()
    {
        Target    = new AnimTarget { ActorPath = actorPath, Component = "actor_transform", Property = property },
        ValueType = AnimValueType.Vec3,
    };

    /// <summary>Undo テスト用: クリップ JSON だけを持つスナップショット（選択は空）。</summary>
    private static AnimUndoSnapshot Snap(string clipJson) => new(clipJson, "[]");

    /// <summary>Undo テスト用: スナップショットのクリップ JSON を取り出す（null は空文字列）。</summary>
    private static string ClipOf(AnimUndoSnapshot? snapshot) => snapshot?.ClipJson ?? "";

    /// <summary>フレーム番号を指定してキーを打つ（テスト記述を短くするためのヘルパー）。</summary>
    private static void KeyAt(AnimTrack track, int frame, float fps, params float[] values)
        => AnimKeyEditor.InsertOrUpdate(track, AnimFrameMath.FrameToTime(frame, fps), values, fps);

    /// <summary>トラック内のキーのフレーム番号一覧を返す。</summary>
    private static List<int> FramesOf(AnimTrack track, float fps)
        => track.Keys.Select(k => AnimFrameMath.TimeToFrame(k.Time, fps)).ToList();

    /// <summary>vec3 トラック 1 本を持つ、fps 30 / 長さ 2 秒のクリップを作る。</summary>
    private static AnimClip ClipWithTrack(string actorPath = "", string property = "position")
    {
        var clip = new AnimClip { Name = "test", Duration = 2f, Fps = 30f };
        clip.Tracks.Add(Vec3Track(actorPath, property));
        return clip;
    }

    /// <summary>テスト用のヒエラルキー表を組み立てる。</summary>
    private static Dictionary<int, AnimHierarchyNode> Nodes(params AnimHierarchyNode[] nodes)
        => nodes.ToDictionary(n => n.Id);

    // ── 1. フレーム⇔秒 ──────────────────────────────────────────

    private static void FrameTimeRoundTrip()
    {
        const float fps = 30f;
        for (int f = 0; f <= 120; f++)
        {
            var t = AnimFrameMath.FrameToTime(f, fps);
            Check.Equal(f, AnimFrameMath.TimeToFrame(t, fps), $"frame {f} の往復");
        }
        // 60fps でも同様（1/60 は 2 進小数で割り切れないので誤差の確認になる）
        for (int f = 0; f <= 300; f++)
        {
            var t = AnimFrameMath.FrameToTime(f, 60f);
            Check.Equal(f, AnimFrameMath.TimeToFrame(t, 60f), $"60fps frame {f} の往復");
        }
    }

    private static void SnapRoundsToNearestFrame()
    {
        const float fps = 30f;                 // 1 フレーム = 0.0333… 秒
        Check.Equal(3, AnimFrameMath.TimeToFrame(0.104f, fps), "0.104s は 3 フレーム目寄り");
        Check.Equal(3, AnimFrameMath.TimeToFrame(0.096f, fps), "0.096s も 3 フレーム目寄り");
        Check.Equal(0, AnimFrameMath.TimeToFrame(-5f, fps),    "負の時刻は 0 フレーム");

        var snapped = AnimFrameMath.SnapTime(0.104f, fps);
        Check.Equal(AnimFrameMath.FrameToTime(3, fps), snapped, "スナップ結果はフレーム境界");
    }

    private static void FpsIsNormalized()
    {
        Check.Equal(AnimFrameMath.DefaultFps, AnimFrameMath.NormalizeFps(0f),          "0 は既定へ");
        Check.Equal(AnimFrameMath.DefaultFps, AnimFrameMath.NormalizeFps(-10f),        "負値は既定へ");
        Check.Equal(AnimFrameMath.DefaultFps, AnimFrameMath.NormalizeFps(float.NaN),   "NaN は既定へ");
        Check.Equal(AnimFrameMath.MaxFps,     AnimFrameMath.NormalizeFps(100000f),     "上限でクランプ");
        Check.Equal(24f,                      AnimFrameMath.NormalizeFps(24f),         "正常値はそのまま");
    }

    private static void LastFrameFitsDuration()
    {
        Check.Equal(30, AnimFrameMath.LastFrame(30f, 1.0f),  "1 秒 @30fps の最終フレーム");
        Check.Equal(15, AnimFrameMath.LastFrame(30f, 0.5f),  "0.5 秒 @30fps");
        Check.Equal(0,  AnimFrameMath.LastFrame(30f, 0f),    "長さ 0 は 0 フレーム");
        // 端数の duration は切り捨て（duration を超えるフレームは作らない）
        Check.Equal(21, AnimFrameMath.LastFrame(30f, 0.72f), "0.72 秒 @30fps は 21 フレームまで");
    }

    private static void SnapClampsToRange()
    {
        const float fps = 30f, duration = 1f;
        Check.Equal(0f, AnimFrameMath.ClampAndSnapTime(-1f, fps, duration), "下限クランプ");
        Check.Equal(duration, AnimFrameMath.ClampAndSnapTime(99f, fps, duration), "上限クランプ");
        // 0.16 秒 @30fps = 4.8 フレーム → duration を超えないよう切り捨てて 4 フレーム目が上限
        Check.Equal(4, AnimFrameMath.ClampFrame(99, fps, 0.16f), "ClampFrame も最終フレームで止まる");
        Check.Equal(0, AnimFrameMath.ClampFrame(-99, fps, 0.16f), "負のフレームは 0 で止まる");
    }

    // ── 2. キー挿入 / 上書き ────────────────────────────────────

    private static void InsertIntoEmptyTrack()
    {
        var track = Vec3Track();
        var r = AnimKeyEditor.InsertOrUpdate(track, 0.5f, new[] { 1f, 2f, 3f }, 30f);

        Check.True(r.WasInserted, "新規挿入として報告される");
        Check.Equal(0, r.KeyIndex, "先頭キー");
        Check.Equal(1, track.Keys.Count, "キーが 1 本");
        Check.Equal(2f, track.Keys[0].Values[1], "値が入っている");
    }

    private static void InsertMergesOnSameFrame()
    {
        const float fps = 30f;
        var track = Vec3Track();
        var time  = AnimFrameMath.FrameToTime(9, fps);

        AnimKeyEditor.InsertOrUpdate(track, time, new[] { 1f, 1f, 1f }, fps);
        // 同じフレームだが浮動小数がわずかにずれた時刻で挿入し直す
        var r = AnimKeyEditor.InsertOrUpdate(track, time + 0.0001f, new[] { 9f, 9f, 9f }, fps);

        Check.True(!r.WasInserted, "上書きとして報告される");
        Check.Equal(1, track.Keys.Count, "キーは増えない");
        Check.Equal(9f, track.Keys[0].Values[0], "値が上書きされている");
        Check.Equal(time + 0.0001f, track.Keys[0].Time, "時刻は指定値へ揃えられる");
    }

    private static void InsertKeepsSortOrder()
    {
        const float fps = 30f;
        var track = Vec3Track();
        foreach (var f in new[] { 20, 5, 12, 0 })
            AnimKeyEditor.InsertOrUpdate(track, AnimFrameMath.FrameToTime(f, fps), new[] { (float)f, 0f, 0f }, fps);

        Check.Equal(4, track.Keys.Count, "4 本");
        for (int i = 1; i < track.Keys.Count; i++)
            Check.True(track.Keys[i - 1].Time <= track.Keys[i].Time, "時刻昇順が保たれる");
        Check.Equal(0f,  track.Keys[0].Values[0], "先頭は frame 0");
        Check.Equal(20f, track.Keys[3].Values[0], "末尾は frame 20");
    }

    private static void ValuesAreFittedToValueType()
    {
        // vec3 トラックへ 4 要素（色）を渡しても 3 要素に収まること
        var track = Vec3Track();
        AnimKeyEditor.InsertOrUpdate(track, 0f, new[] { 1f, 2f, 3f, 4f }, 30f);
        Check.Equal(3, track.Keys[0].Values.Length, "超過分は切り捨て");

        // float トラックへ 3 要素を渡しても 1 要素
        var scalar = new AnimTrack
        {
            Target    = new AnimTarget { Component = "canvas_transform", Property = "rotation" },
            ValueType = AnimValueType.Float,
        };
        AnimKeyEditor.InsertOrUpdate(scalar, 0f, new[] { 45f, 0f, 0f }, 30f);
        Check.Equal(1, scalar.Keys[0].Values.Length, "float は 1 要素");
        Check.Equal(45f, scalar.Keys[0].Values[0], "先頭要素が採用される");

        // 不足は 0 埋め
        var filled = AnimKeyEditor.FitValues(new[] { 7f }, AnimValueType.Color);
        Check.Equal(4, filled.Length, "color は 4 要素");
        Check.Equal(0f, filled[3], "不足分は 0");
    }

    private static void PreviousValuesAreCloned()
    {
        var track = Vec3Track();
        AnimKeyEditor.InsertOrUpdate(track, 0f,   new[] { 1f, 2f, 3f }, 30f);
        AnimKeyEditor.InsertOrUpdate(track, 1.0f, new[] { 9f, 9f, 9f }, 30f);

        var v = AnimKeyEditor.PreviousOrDefaultValues(track, 0.5f);
        Check.Equal(1f, v[0], "0.5s 時点では frame 0 のキーの値");

        // 返るのは複製であり、書き換えても元のキーは壊れない
        v[0] = 999f;
        Check.Equal(1f, track.Keys[0].Values[0], "複製が返っている");

        // キーが無ければ 0 埋め
        var empty = AnimKeyEditor.PreviousOrDefaultValues(Vec3Track(), 0.5f);
        Check.Equal(3, empty.Length, "vec3 の要素数");
        Check.Equal(0f, empty[0], "0 埋め");
    }

    // ── 2b. サマリー行（全チャンネル）────────────────────────────

    private static void SummaryFramesUnionsAllTracks()
    {
        const float fps = 30f;
        var a = Vec3Track("Arm", "position");
        var b = Vec3Track("Leg", "position");
        AnimKeyEditor.InsertOrUpdate(a, AnimFrameMath.FrameToTime(0, fps), new[] { 1f, 1f, 1f }, fps);
        AnimKeyEditor.InsertOrUpdate(a, AnimFrameMath.FrameToTime(10, fps), new[] { 2f, 2f, 2f }, fps);
        AnimKeyEditor.InsertOrUpdate(b, AnimFrameMath.FrameToTime(5, fps), new[] { 3f, 3f, 3f }, fps);
        // Arm の frame 10 と重複させて、和集合が重複を除去することも確認する
        AnimKeyEditor.InsertOrUpdate(b, AnimFrameMath.FrameToTime(10, fps), new[] { 4f, 4f, 4f }, fps);

        var frames = AnimKeyEditor.SummaryFrames(new[] { a, b });
        Check.Equal(3, frames.Count, "0 / 5 / 10 の 3 フレーム分（10 は重複除去済み）");
        Check.Equal(0,  AnimFrameMath.TimeToFrame(frames[0], fps), "昇順の先頭は frame 0");
        Check.Equal(5,  AnimFrameMath.TimeToFrame(frames[1], fps), "次は frame 5");
        Check.Equal(10, AnimFrameMath.TimeToFrame(frames[2], fps), "最後は frame 10");
    }

    private static void InsertOnAllTracksFiltersByActorPath()
    {
        const float fps = 30f;
        var armTrack  = Vec3Track("Arm", "position");
        var legTrack  = Vec3Track("Leg", "position");
        var tracks    = new List<AnimTrack> { armTrack, legTrack };
        var time      = AnimFrameMath.FrameToTime(3, fps);

        AnimKeyEditor.InsertOnAllTracks(tracks, "Arm", time, _ => new[] { 9f, 9f, 9f }, fps);

        Check.Equal(1, armTrack.Keys.Count, "actor_path が一致する Arm だけに挿入される");
        Check.Equal(0, legTrack.Keys.Count, "一致しない Leg には挿入されない");
    }

    private static void InsertOnAllTracksFallsBackToAllTracks()
    {
        const float fps = 30f;
        var armTrack = Vec3Track("Arm", "position");
        var legTrack = Vec3Track("Leg", "position");
        var tracks   = new List<AnimTrack> { armTrack, legTrack };
        var time     = AnimFrameMath.FrameToTime(1, fps);

        // "Head" に一致するトラックが 1 本も無い → 全トラックへフォールバックする
        var results = AnimKeyEditor.InsertOnAllTracks(tracks, "Head", time, _ => new[] { 1f, 1f, 1f }, fps);

        Check.Equal(2, results.Count, "一致が無いので全トラックへ挿入される");
        Check.Equal(1, armTrack.Keys.Count, "Arm にも挿入される");
        Check.Equal(1, legTrack.Keys.Count, "Leg にも挿入される");
    }

    private static void MoveKeysAtFrameMovesOnlyMatchingKeys()
    {
        const float fps = 30f;
        var a = Vec3Track("Arm");
        var b = Vec3Track("Leg");
        var oldTime = AnimFrameMath.FrameToTime(4, fps);
        var newTime = AnimFrameMath.FrameToTime(8, fps);
        AnimKeyEditor.InsertOrUpdate(a, oldTime, new[] { 1f, 0f, 0f }, fps);
        AnimKeyEditor.InsertOrUpdate(b, AnimFrameMath.FrameToTime(4, fps), new[] { 2f, 0f, 0f }, fps);
        // b には frame 4 と別に frame 20 のキーもある（動かないことを確認する対照）
        AnimKeyEditor.InsertOrUpdate(b, AnimFrameMath.FrameToTime(20, fps), new[] { 3f, 0f, 0f }, fps);

        AnimKeyEditor.MoveKeysAtFrame(new[] { a, b }, oldTime, newTime, fps);

        Check.Equal(8, AnimFrameMath.TimeToFrame(a.Keys[0].Time, fps), "Arm の frame 4 キーが frame 8 へ動く");
        Check.Equal(2, b.Keys.Count, "Leg のキー本数は変わらない");
        Check.True(b.Keys.Any(k => AnimFrameMath.TimeToFrame(k.Time, fps) == 8), "Leg の frame 4 キーも frame 8 へ動く");
        Check.True(b.Keys.Any(k => AnimFrameMath.TimeToFrame(k.Time, fps) == 20), "Leg の frame 20 キーは動かない");
    }

    private static void DeleteKeysAtFrameDeletesOnlyMatchingKeys()
    {
        const float fps = 30f;
        var a = Vec3Track("Arm");
        var b = Vec3Track("Leg");
        var target = AnimFrameMath.FrameToTime(6, fps);
        AnimKeyEditor.InsertOrUpdate(a, target, new[] { 1f, 0f, 0f }, fps);
        AnimKeyEditor.InsertOrUpdate(b, target, new[] { 2f, 0f, 0f }, fps);
        AnimKeyEditor.InsertOrUpdate(b, AnimFrameMath.FrameToTime(12, fps), new[] { 3f, 0f, 0f }, fps);

        AnimKeyEditor.DeleteKeysAtFrame(new[] { a, b }, target, fps);

        Check.Equal(0, a.Keys.Count, "Arm の frame 6 キーが消える");
        Check.Equal(1, b.Keys.Count, "Leg は frame 6 だけ消えて frame 12 は残る");
        Check.Equal(12, AnimFrameMath.TimeToFrame(b.Keys[0].Time, fps), "残ったのは frame 12");
    }

    // ── 3. 祖先探索 / actor_path ────────────────────────────────

    /// <summary>Player(1) > Items(2, フォルダ) > Arm(3) > Hand(4) というツリー。</summary>
    private static Dictionary<int, AnimHierarchyNode> SampleTree() => Nodes(
        new AnimHierarchyNode(1, null, "Player", false),
        new AnimHierarchyNode(2, 1,    "Items",  true),
        new AnimHierarchyNode(3, 2,    "Arm",    false),
        new AnimHierarchyNode(4, 3,    "Hand",   false));

    private static void FindsNearestAnimatorAncestor()
    {
        var tree = SampleTree();
        // Animator を持つのは Player(1) だけ
        var found = AnimHierarchyNav.FindNearestAnimator(tree, 4, id => id == 1);
        Check.Equal(1, found ?? -1, "Hand から遡って Player が見つかる");

        // 自分自身が Animator を持つ場合は自分が選ばれる
        var self = AnimHierarchyNav.FindNearestAnimator(tree, 3, id => id is 1 or 3);
        Check.Equal(3, self ?? -1, "より近い Arm が優先される");
    }

    private static void NoAnimatorReturnsNull()
    {
        var tree = SampleTree();
        Check.True(AnimHierarchyNav.FindNearestAnimator(tree, 4, _ => false) is null,
            "誰も Animator を持たなければ null");
    }

    private static void ActorPathSkipsFolders()
    {
        var tree = SampleTree();
        // Player → Hand。間の Items はフォルダなのでパスに現れない
        Check.Equal("Arm/Hand", AnimHierarchyNav.BuildActorPath(tree, 1, 4) ?? "",
            "フォルダは actor_path から除外される");
        Check.Equal("Arm", AnimHierarchyNav.BuildActorPath(tree, 1, 3) ?? "",
            "1 段でも同様");
    }

    private static void ActorPathForSelfIsEmpty()
    {
        var tree = SampleTree();
        Check.Equal("", AnimHierarchyNav.BuildActorPath(tree, 1, 1) ?? "x", "自分自身は空文字列");
    }

    private static void ActorPathForUnrelatedIsNull()
    {
        var tree = Nodes(
            new AnimHierarchyNode(1, null, "A", false),
            new AnimHierarchyNode(2, null, "B", false));
        Check.True(AnimHierarchyNav.BuildActorPath(tree, 1, 2) is null, "祖先関係になければ null");
    }

    private static void CyclicParentDoesNotHang()
    {
        // 壊れたデータ（1 の親が 2、2 の親が 1）でも打ち切られること
        var tree = Nodes(
            new AnimHierarchyNode(1, 2, "A", false),
            new AnimHierarchyNode(2, 1, "B", false));
        var chain = AnimHierarchyNav.SelfAndAncestors(tree, 1);
        Check.Equal(2, chain.Count, "訪問済みで打ち切られる");
    }

    private static void ParsesHierarchyJson()
    {
        const string json = """
        [
          {"id":0,"name":"Root","parent":null,"is_folder":false},
          {"id":1,"name":"Group","parent":0,"is_folder":true},
          {"id":2,"name":"Child","parent":1,"is_folder":false}
        ]
        """;
        var nodes = AnimHierarchyNav.ParseHierarchy(json);
        Check.Equal(3, nodes.Count, "3 ノード");
        Check.True(nodes[1].IsFolder, "is_folder が読める");
        Check.Equal(1, nodes[2].ParentId ?? -1, "parent が読める");
        Check.Equal("Child/", nodes[2].Name + "/", "name が読める");
        Check.Equal("Child", AnimHierarchyNav.BuildActorPath(nodes, 0, 2) ?? "", "フォルダを飛ばす");

        // 壊れた入力でも例外にしない
        Check.Equal(0, AnimHierarchyNav.ParseHierarchy("{ not json").Count, "壊れた JSON は空表");
        Check.Equal(0, AnimHierarchyNav.ParseHierarchy("").Count, "空文字も空表");
    }

    // ── 4. Undo / Redo ──────────────────────────────────────────

    private static void UndoRestoresPrevious()
    {
        var undo = new AnimUndoStack();
        undo.Reset(Snap("A"));
        undo.Push(Snap("B"));

        Check.True(undo.CanUndo, "Undo できる");
        Check.Equal("A", ClipOf(undo.Undo()), "1 つ前へ戻る");
        Check.True(!undo.CanUndo, "これ以上は戻れない");
        Check.True(undo.Undo() is null, "戻れないときは null");
    }

    private static void RedoReappliesEdit()
    {
        var undo = new AnimUndoStack();
        undo.Reset(Snap("A"));
        undo.Push(Snap("B"));
        undo.Undo();

        Check.True(undo.CanRedo, "Redo できる");
        Check.Equal("B", ClipOf(undo.Redo()), "編集後の状態へ進む");
        Check.True(!undo.CanRedo, "これ以上は進めない");
    }

    private static void NewEditClearsRedo()
    {
        var undo = new AnimUndoStack();
        undo.Reset(Snap("A"));
        undo.Push(Snap("B"));
        undo.Undo();          // 現在 = A、Redo に B
        undo.Push(Snap("C"));       // 新しい編集

        Check.True(!undo.CanRedo, "Redo 系列が破棄される");
        Check.Equal("A", ClipOf(undo.Undo()), "Undo は新しい編集の前へ戻る");
    }

    private static void IdenticalPushIsIgnored()
    {
        var undo = new AnimUndoStack();
        undo.Reset(Snap("A"));
        undo.Push(Snap("A"));
        Check.True(!undo.CanUndo, "同じ内容では履歴が増えない");
    }

    private static void UndoRespectsCapacity()
    {
        var undo = new AnimUndoStack(capacity: 3);
        undo.Reset(Snap("s0"));
        for (int i = 1; i <= 10; i++) undo.Push(Snap("s" + i));

        // 3 段までしか戻れない
        int steps = 0;
        while (undo.Undo() is not null) steps++;
        Check.Equal(3, steps, "上限段数まで");
    }

    // ── 5. 現在値スナップショット ────────────────────────────────

    private static void SnapshotReadsTransform()
    {
        const string json = """
        {"id":7,"transform":{"px":1,"py":2,"pz":3,"ex":10,"ey":20,"ez":30,"sx":2,"sy":2,"sz":2}}
        """;
        var snap = AnimActorSnapshot.Parse(json);

        Check.Equal(7, snap.ActorDfsId, "id が読める");
        var pos = snap.TryGet(AnimActorSnapshot.TransformComponent, AnimActorSnapshot.PositionProperty)!;
        Check.Equal(3f, pos[2], "位置 z");
        var rot = snap.TryGet(AnimActorSnapshot.TransformComponent, AnimActorSnapshot.RotationProperty)!;
        Check.Equal(20f, rot[1], "回転 y（度）");
        var scale = snap.TryGet(AnimActorSnapshot.TransformComponent, AnimActorSnapshot.ScaleProperty)!;
        Check.Equal(2f, scale[0], "スケール x");
    }

    private static void SnapshotReadsCanvasTransform()
    {
        const string json = """
        {"id":3,"canvas_transform":{"px":100,"py":-40,"rotation":45,"sx":1.5,"sy":0.5}}
        """;
        var snap = AnimActorSnapshot.Parse(json);

        var pos = snap.TryGet(AnimActorSnapshot.CanvasTransformComponent, AnimActorSnapshot.PositionProperty)!;
        Check.Equal(2, pos.Length, "2D の位置は 2 要素");
        Check.Equal(-40f, pos[1], "位置 y");

        var rot = snap.TryGet(AnimActorSnapshot.CanvasTransformComponent, AnimActorSnapshot.RotationProperty)!;
        Check.Equal(1, rot.Length, "2D の回転はスカラー");
        Check.Equal(45f, rot[0], "回転角");

        // 3D の transform が無いので取得できないこと
        Check.True(!snap.Has(AnimActorSnapshot.TransformComponent, AnimActorSnapshot.PositionProperty),
            "3D transform は無い");
    }

    private static void SnapshotReadsColors()
    {
        const string json = """
        {"id":1,
         "components":[
           {"type":"SpriteComponent","cr":1,"cg":0.5,"cb":0.25,"ca":0.75},
           {"type":"TextComponent","text_r":0.1,"text_g":0.2,"text_b":0.3,"text_a":1,"font_size":32}
         ]}
        """;
        var snap = AnimActorSnapshot.Parse(json);

        var sprite = snap.TryGet(AnimActorSnapshot.SpriteComponent, AnimActorSnapshot.ColorProperty)!;
        Check.Equal(4, sprite.Length, "色は 4 要素");
        Check.Equal(0.25f, sprite[2], "Sprite の B");

        var text = snap.TryGet(AnimActorSnapshot.TextComponent, AnimActorSnapshot.ColorProperty)!;
        Check.Equal(0.2f, text[1], "Text の G");

        var size = snap.TryGet(AnimActorSnapshot.TextComponent, AnimActorSnapshot.FontSizeProperty)!;
        Check.Equal(32f, size[0], "フォントサイズ");
    }

    private static void SnapshotSurvivesBrokenJson()
    {
        Check.True(AnimActorSnapshot.Parse("{ broken").IsEmpty, "壊れた JSON は空スナップショット");
        Check.True(AnimActorSnapshot.Parse("").IsEmpty,          "空文字も空スナップショット");
        Check.True(AnimActorSnapshot.Parse("[1,2,3]").IsEmpty,   "配列は対象外");
    }

    // ── 6. .anim の fps 互換 ────────────────────────────────────

    private static void LegacyClipGetsDefaultFps()
    {
        // fps を持たない旧 .anim（Rust 側 #[serde(default)] と同じ挙動になること）
        const string legacy = """{"name":"old","duration":1.0,"loop_mode":"once","tracks":[]}""";
        var clip = AnimClipIO.Parse(legacy);
        Check.Equal(AnimFrameMath.DefaultFps, clip.Fps, "既定 fps が入る");

        // 不正な fps も既定へ矯正される
        const string bad = """{"name":"bad","duration":1.0,"fps":0,"tracks":[]}""";
        Check.Equal(AnimFrameMath.DefaultFps, AnimClipIO.Parse(bad).Fps, "0 は既定へ");
    }

    private static void FpsRoundTripsThroughJson()
    {
        var clip = new AnimClip { Name = "walk", Duration = 2f, Fps = 24f, LoopMode = AnimLoopMode.Loop };
        clip.Tracks.Add(Vec3Track("Arm"));
        AnimKeyEditor.InsertOrUpdate(clip.Tracks[0], AnimFrameMath.FrameToTime(12, 24f),
                                     new[] { 1f, 2f, 3f }, 24f);

        var reloaded = AnimClipIO.Parse(AnimClipIO.Serialize(clip));

        Check.Equal(24f, reloaded.Fps, "fps が往復する");
        Check.Equal(2f,  reloaded.Duration, "duration が往復する");
        Check.Equal(1,   reloaded.Tracks.Count, "トラックが往復する");
        Check.Equal("Arm", reloaded.Tracks[0].Target.ActorPath, "actor_path が往復する");
        Check.Equal(12, AnimFrameMath.TimeToFrame(reloaded.Tracks[0].Keys[0].Time, 24f),
            "キーのフレーム位置が往復する");
    }

    // ── 7. 複数選択モデル（AnimKeySelection）────────────────────

    private static void SelectionTogglesKeys()
    {
        var sel = new AnimKeySelection();
        Check.True(sel.IsEmpty, "初期状態は未選択");

        Check.True(sel.Toggle(0, 1), "1 回目のトグルで選択される");
        Check.True(sel.Contains(0, 1), "選択に含まれる");
        Check.True(!sel.Toggle(0, 1), "2 回目のトグルで解除される");
        Check.True(sel.IsEmpty, "解除後は空");
    }

    private static void SelectionAddsKeys()
    {
        var sel = new AnimKeySelection();
        sel.SelectSingle(0, 0);
        sel.Add(1, 2);
        sel.Add(1, 2);                       // 重複追加は増えない（集合であること）

        Check.Equal(2, sel.Count, "選択件数");
        Check.True(sel.IsMultiple, "複数選択と判定される");
        var ordered = sel.Ordered();
        Check.Equal(0, ordered[0].TrackIndex, "トラック昇順で並ぶ");
        Check.Equal(2, ordered[1].KeyIndex,   "キー添字も保持される");
    }

    private static void SelectionNormalizesAfterDelete()
    {
        // トラック 0 のキー 0,1,2,3 のうち 1 を削除 → 2,3 は 1,2 へ詰まる
        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 2); sel.Add(0, 3); sel.Add(1, 5);
        sel.NormalizeAfterDelete(new[] { new AnimKeyRef(0, 1) });

        Check.True(sel.Contains(0, 0), "削除位置より前はそのまま");
        Check.True(sel.Contains(0, 1), "削除位置より後ろは 1 つ詰まる");
        Check.True(sel.Contains(0, 2), "同上");
        Check.True(sel.Contains(1, 5), "別トラックは影響を受けない");
        Check.Equal(4, sel.Count, "件数は変わらない");

        // 削除されたキー自身は選択から外れる
        var sel2 = new AnimKeySelection();
        sel2.Add(0, 1);
        sel2.NormalizeAfterDelete(new[] { new AnimKeyRef(0, 1) });
        Check.True(sel2.IsEmpty, "消えたキーは選択から外れる");
    }

    private static void SelectionNormalizeDropsOutOfRange()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        KeyAt(clip.Tracks[0], 0, fps, 1, 2, 3);

        var sel = new AnimKeySelection();
        sel.Add(0, 0);
        sel.Add(0, 5);      // 存在しないキー
        sel.Add(3, 0);      // 存在しないトラック
        sel.Normalize(clip.Tracks);

        Check.Equal(1, sel.Count, "範囲内の 1 個だけが残る");
        Check.True(sel.Contains(0, 0), "残るのは実在するキー");
    }

    private static void SelectionSelectsWholeFrame()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        clip.Tracks.Add(Vec3Track("", "scale"));
        KeyAt(clip.Tracks[0], 10, fps, 1, 1, 1);
        KeyAt(clip.Tracks[1], 10, fps, 2, 2, 2);
        KeyAt(clip.Tracks[1], 20, fps, 3, 3, 3);

        var sel = new AnimKeySelection();
        sel.SelectFrame(clip.Tracks, AnimFrameMath.FrameToTime(10, fps), fps);

        Check.Equal(2, sel.Count, "フレーム 10 のキーが両トラックぶん選ばれる");
        Check.True(sel.Contains(0, 0) && sel.Contains(1, 0), "各トラックの該当キー");

        sel.SelectAll(clip.Tracks);
        Check.Equal(3, sel.Count, "全選択は全キー");
    }

    private static void SelectionRoundTripsThroughJson()
    {
        var sel = new AnimKeySelection();
        sel.Add(0, 1); sel.Add(2, 3);

        var restored = new AnimKeySelection();
        restored.Restore(sel.Serialize());

        Check.Equal(2, restored.Count, "件数が往復する");
        Check.True(restored.Contains(0, 1) && restored.Contains(2, 3), "内容が往復する");

        restored.Restore("{ broken");
        Check.True(restored.IsEmpty, "壊れた JSON は選択なしとして扱う");
    }

    // ── 8. 選択キーの一括移動・削除・補間変更 ────────────────────

    private static void MoveSelectionShiftsAllKeys()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 0, fps, 1, 0, 0);
        KeyAt(track, 5, fps, 2, 0, 0);
        KeyAt(track, 20, fps, 3, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 1);        // フレーム 0 と 5

        var moved = AnimKeyEditor.MoveSelectedKeys(clip.Tracks, sel, 3, fps, clip.Duration);

        Check.Equal(3, moved, "要求どおり 3 フレーム動く");
        Check.Equal(3, FramesOf(track, fps)[0], "先頭キーは 3 へ");
        Check.Equal(8, FramesOf(track, fps)[1], "2 番目は 8 へ（相対間隔を保つ）");
        Check.Equal(20, FramesOf(track, fps)[2], "非選択キーは動かない");
        Check.True(sel.Contains(0, 0) && sel.Contains(0, 1), "移動後も同じキーが選択されている");
    }

    private static void MoveSelectionReplacesOverlappedKey()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 0, fps, 1, 0, 0);
        KeyAt(track, 5, fps, 9, 9, 9);       // 移動先に居座る非選択キー

        var sel = new AnimKeySelection();
        sel.Add(0, 0);

        AnimKeyEditor.MoveSelectedKeys(clip.Tracks, sel, 5, fps, clip.Duration);

        Check.Equal(1, track.Keys.Count, "重なったキーは置き換えられて 1 本になる");
        Check.Equal(5, FramesOf(track, fps)[0], "移動先フレーム");
        Check.Equal(1f, track.Keys[0].Values[0], "残ったのは動かしてきた側の値");
        Check.True(sel.Contains(0, 0), "移動後の添字で選択が張り直される");
    }

    private static void MoveSelectionClampsAtFrameZero()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 2, fps, 1, 0, 0);
        KeyAt(track, 8, fps, 2, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 1);

        var moved = AnimKeyEditor.MoveSelectedKeys(clip.Tracks, sel, -10, fps, clip.Duration);

        Check.Equal(-2, moved, "先頭が 0 フレームで止まるぶんだけ動く");
        Check.Equal(0, FramesOf(track, fps)[0], "先頭は 0 フレーム");
        Check.Equal(6, FramesOf(track, fps)[1], "相対間隔は保たれる");
    }

    private static void MoveSelectionClampsAtLastFrame()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();               // duration 2 秒 → 最終フレーム 60
        var track = clip.Tracks[0];
        KeyAt(track, 58, fps, 1, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0);

        var moved = AnimKeyEditor.MoveSelectedKeys(clip.Tracks, sel, 10, fps, clip.Duration);

        Check.Equal(2, moved, "最終フレームまでしか動かない");
        Check.Equal(60, FramesOf(track, fps)[0], "最終フレームで止まる");
    }

    private static void DeleteSelectionRemovesAllKeys()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        clip.Tracks.Add(Vec3Track("", "scale"));
        KeyAt(clip.Tracks[0], 0, fps, 1, 0, 0);
        KeyAt(clip.Tracks[0], 5, fps, 2, 0, 0);
        KeyAt(clip.Tracks[1], 5, fps, 3, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 1); sel.Add(1, 0);

        var removed = AnimKeyEditor.DeleteSelectedKeys(clip.Tracks, sel);

        Check.Equal(3, removed, "3 個削除される");
        Check.Equal(0, clip.Tracks[0].Keys.Count, "トラック 0 は空");
        Check.Equal(0, clip.Tracks[1].Keys.Count, "トラック 1 も空");
        Check.True(sel.IsEmpty, "削除後の選択は空");
    }

    private static void SetInterpAppliesToSelection()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 0, fps, 1, 0, 0);
        KeyAt(track, 5, fps, 2, 0, 0);
        KeyAt(track, 10, fps, 3, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 2);

        var changed = AnimKeyEditor.SetInterpForSelection(clip.Tracks, sel, AnimInterp.Step);

        Check.Equal(2, changed, "選択ぶんだけ変わる");
        Check.Equal(AnimInterp.Step,   track.Keys[0].Interp, "1 個目");
        Check.Equal(AnimInterp.Linear, track.Keys[1].Interp, "非選択キーは変わらない");
        Check.Equal(AnimInterp.Step,   track.Keys[2].Interp, "3 個目");
    }

    // ── 9. コピー / 貼り付け（AnimKeyClipboard）──────────────────

    private static void CopyStoresRelativeFrames()
    {
        const float fps = 30f;
        var clip = ClipWithTrack("Arm");
        var track = clip.Tracks[0];
        KeyAt(track, 10, fps, 1, 0, 0);
        KeyAt(track, 14, fps, 2, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 1);

        var data = AnimKeyClipboard.Copy(clip.Tracks, sel, fps);

        Check.Equal(1, data.Tracks.Count, "トラック 1 本ぶん");
        Check.Equal("Arm", data.Tracks[0].ActorPath, "対象の actor_path を持つ");
        Check.Equal(0, data.Tracks[0].Keys[0].FrameOffset, "先頭キーはアンカー（相対 0）");
        Check.Equal(4, data.Tracks[0].Keys[1].FrameOffset, "2 個目は +4 フレーム");
    }

    private static void PastePlacesKeysRelativeToPlayhead()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 10, fps, 1, 0, 0);
        KeyAt(track, 14, fps, 2, 0, 0);

        var sel = new AnimKeySelection();
        sel.Add(0, 0); sel.Add(0, 1);
        var data = AnimKeyClipboard.Copy(clip.Tracks, sel, fps);

        var pasted = AnimKeyClipboard.Paste(clip, data, 20);

        Check.Equal(2, pasted.Count, "2 個貼り付けられる");
        var frames = FramesOf(track, fps);
        Check.Equal(4, frames.Count, "元の 2 個 + 貼り付け 2 個");
        Check.Equal(20, frames[2], "先頭キーはプレイヘッド位置へ");
        Check.Equal(24, frames[3], "相対間隔が保たれる");
    }

    private static void PasteCreatesMissingTrack()
    {
        const float fps = 30f;
        var source = ClipWithTrack("Arm/Hand");
        KeyAt(source.Tracks[0], 6, fps, 7, 8, 9);

        var sel = new AnimKeySelection();
        sel.Add(0, 0);
        var data = AnimKeyClipboard.Copy(source.Tracks, sel, fps);

        // 貼り付け先には該当トラックが無い（＝別クリップへの貼り付け）
        var target = new AnimClip { Name = "other", Duration = 2f, Fps = 30f };
        var pasted = AnimKeyClipboard.Paste(target, data, 0);

        Check.Equal(1, target.Tracks.Count, "トラックが作られる");
        Check.Equal("Arm/Hand", target.Tracks[0].Target.ActorPath, "actor_path が引き継がれる");
        Check.Equal("position", target.Tracks[0].Target.Property, "property が引き継がれる");
        Check.Equal(AnimValueType.Vec3, target.Tracks[0].ValueType, "value_type が引き継がれる");
        Check.Equal(1, pasted.Count, "キーが 1 個入る");
        Check.Equal(9f, target.Tracks[0].Keys[0].Values[2], "値も引き継がれる");
    }

    private static void PasteReplacesKeysOnSameFrame()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        var track = clip.Tracks[0];
        KeyAt(track, 0, fps, 1, 1, 1);
        KeyAt(track, 10, fps, 5, 5, 5);      // 貼り付け先に既存キー

        var sel = new AnimKeySelection();
        sel.Add(0, 0);                        // フレーム 0 のキーをコピー
        var data = AnimKeyClipboard.Copy(clip.Tracks, sel, fps);

        AnimKeyClipboard.Paste(clip, data, 10);

        Check.Equal(2, track.Keys.Count, "キーは増えない（上書き）");
        Check.Equal(1f, track.Keys[1].Values[0], "フレーム 10 の値が貼り付け内容で置き換わる");
    }

    private static void ClipboardJsonRoundTrips()
    {
        const float fps = 30f;
        var clip = ClipWithTrack("Arm");
        var track = clip.Tracks[0];
        KeyAt(track, 3, fps, 1, 2, 3);
        track.Keys[0].Interp     = AnimInterp.Bezier;
        track.Keys[0].InTangent  = new[] { 0.5f, 0f, 0f };
        track.Keys[0].OutTangent = new[] { -0.5f, 0f, 0f };

        var sel = new AnimKeySelection();
        sel.Add(0, 0);

        var json     = AnimKeyClipboard.Serialize(AnimKeyClipboard.Copy(clip.Tracks, sel, fps));
        var restored = AnimKeyClipboard.Parse(json);

        Check.True(restored is not null, "パースできる");
        Check.Equal("Arm", restored!.Tracks[0].ActorPath, "actor_path が往復する");
        Check.Equal(AnimInterp.Bezier, restored.Tracks[0].Keys[0].Interp, "補間が往復する");
        Check.Equal(0.5f,  restored.Tracks[0].Keys[0].InTangent![0],  "入タンジェントが往復する");
        Check.Equal(-0.5f, restored.Tracks[0].Keys[0].OutTangent![0], "出タンジェントが往復する");
        Check.Equal(2f,    restored.Tracks[0].Keys[0].Values[1],      "値が往復する");
    }

    private static void ClipboardRejectsForeignText()
    {
        Check.True(AnimKeyClipboard.Parse("hello world") is null, "ただのテキストは対象外");
        Check.True(AnimKeyClipboard.Parse("""{"format":"other","tracks":[]}""") is null, "別形式の JSON も対象外");
        Check.True(AnimKeyClipboard.Parse(null) is null, "null も対象外");
        Check.True(AnimKeyClipboard.Parse("") is null,   "空文字も対象外");
    }

    // ── 10. 座標計算（矩形選択・ズーム）─────────────────────────

    private static void MarqueeSelectsKeysInsideRect()
    {
        const float fps = 30f;
        const double pps = 100.0;             // 1 秒 = 100px
        var clip = ClipWithTrack();
        clip.Tracks.Add(Vec3Track("", "scale"));
        KeyAt(clip.Tracks[0], 3, fps, 0, 0, 0);    // 0.1 秒 → x=10
        KeyAt(clip.Tracks[0], 15, fps, 0, 0, 0);   // 0.5 秒 → x=50
        KeyAt(clip.Tracks[1], 3, fps, 0, 0, 0);    // 別トラック・同じ x=10

        var layout = AnimDopeSheetLayout.Create(pps, 0);
        var row0 = layout.TrackRowCenterY(0);
        var row1 = layout.TrackRowCenterY(1);

        // トラック 0 の行だけを x=0..30 で囲う → x=10 のキー 1 個
        var one = layout.KeysInRect(clip.Tracks, 0, row0 - 5, 30, row0 + 5);
        Check.Equal(1, one.Count, "矩形内は 1 個");
        Check.Equal(0, one[0].TrackIndex, "トラック 0 のキー");

        // 2 トラックにまたがって x=0..30 → 各トラック 1 個ずつ
        var across = layout.KeysInRect(clip.Tracks, 0, row0 - 5, 30, row1 + 5);
        Check.Equal(2, across.Count, "トラックをまたいで拾える");

        // 全部囲えば 3 個
        Check.Equal(3, layout.KeysInRect(clip.Tracks, -10, 0, 1000, row1 + 20).Count, "全キーを拾える");
    }

    private static void MarqueeAcceptsReversedRect()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        KeyAt(clip.Tracks[0], 3, fps, 0, 0, 0);

        var layout = AnimDopeSheetLayout.Create(100.0, 0);
        var row0 = layout.TrackRowCenterY(0);

        var forward  = layout.KeysInRect(clip.Tracks, 0, row0 - 5, 30, row0 + 5);
        var backward = layout.KeysInRect(clip.Tracks, 30, row0 + 5, 0, row0 - 5);

        Check.Equal(forward.Count, backward.Count, "右下→左上のドラッグでも同じ結果");
        Check.Equal(1, backward.Count, "1 個拾える");
    }

    private static void ZoomKeepsTimeUnderCursor()
    {
        const double oldPps = 100.0;
        const double scrollX = 120.0;
        const double cursorX = 40.0;

        var newPps     = AnimTimelineZoom.ZoomedPixelsPerSecond(oldPps, 1);
        var newScrollX = AnimTimelineZoom.ScrollXForZoomAtCursor(oldPps, newPps, scrollX, cursorX);

        var timeBefore = (scrollX + cursorX) / oldPps;
        var timeAfter  = (newScrollX + cursorX) / newPps;
        Check.Close(timeBefore, timeAfter, 1e-9, "カーソル下の時刻が固定される");
        Check.True(newPps > oldPps, "1 ノッチで拡大する");

        // 縮小方向でも同じ
        var zoomOut   = AnimTimelineZoom.ZoomedPixelsPerSecond(oldPps, -1);
        var outScroll = AnimTimelineZoom.ScrollXForZoomAtCursor(oldPps, zoomOut, scrollX, cursorX);
        Check.Close(timeBefore, (outScroll + cursorX) / zoomOut, 1e-9, "縮小でも時刻が固定される");

        // 左端付近では負のスクロールにならない
        Check.True(AnimTimelineZoom.ScrollXForZoomAtCursor(oldPps, zoomOut, 0, cursorX) >= 0, "スクロール量は 0 以上");
    }

    private static void ZoomClampsToRange()
    {
        var max = AnimTimelineZoom.ZoomedPixelsPerSecond(AnimationTimelineConstants.MaxPixelsPerSecond, 10);
        Check.Equal(AnimationTimelineConstants.MaxPixelsPerSecond, max, "上限でクランプ");

        var min = AnimTimelineZoom.ZoomedPixelsPerSecond(AnimationTimelineConstants.MinPixelsPerSecond, -10);
        Check.Equal(AnimationTimelineConstants.MinPixelsPerSecond, min, "下限でクランプ");
    }

    private static void FitFillsViewportWidth()
    {
        const double viewport = 640.0;
        var pps = AnimTimelineZoom.FitPixelsPerSecond(viewport, 2f);

        Check.Close((viewport - AnimationTimelineConstants.FitContentMarginPx) / 2.0, pps, 1e-9,
            "余白を除いた幅にクリップ全体が収まる");
        // 極端に短いクリップでも上限を超えない
        Check.True(AnimTimelineZoom.FitPixelsPerSecond(viewport, 0.0001f) <= AnimationTimelineConstants.MaxPixelsPerSecond,
            "上限を超えない");
        // 幅が取れないうちは既定値
        Check.Equal(AnimationTimelineConstants.DefaultPixelsPerSecond, AnimTimelineZoom.FitPixelsPerSecond(0, 1f),
            "レイアウト前は既定倍率");
    }

    // ── 11. 選択込み Undo / Redo ────────────────────────────────
    //
    // パネル（WPF）は動かせないため、AnimationTimelinePanel が守っている規約
    //  「編集 → AnimUndoStack.Push(クリップ JSON, 選択 JSON)」
    //  「Undo → クリップを Parse して選択を Restore」
    // をここで再現し、スタック・直列化・選択復元の組み合わせを検証する。

    /// <summary>パネルの CommitEdit 相当（編集後のスナップショットを積む）。</summary>
    private static void PushSnapshot(AnimUndoStack undo, AnimClip clip, AnimKeySelection selection)
        => undo.Push(new AnimUndoSnapshot(AnimClipIO.Serialize(clip), selection.Serialize()));

    /// <summary>パネルの RestoreSnapshot 相当（クリップと選択を戻す）。</summary>
    private static AnimClip ApplySnapshot(AnimUndoSnapshot? snapshot, AnimKeySelection selection)
    {
        var snap = snapshot!.Value;
        var clip = AnimClipIO.Parse(snap.ClipJson);
        selection.Restore(snap.SelectionJson);
        selection.Normalize(clip.Tracks);
        return clip;
    }

    private static void UndoRestoresDeletedKeysAndSelection()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        KeyAt(clip.Tracks[0], 0, fps, 1, 0, 0);
        KeyAt(clip.Tracks[0], 10, fps, 2, 0, 0);

        var selection = new AnimKeySelection();
        selection.Add(0, 0); selection.Add(0, 1);

        var undo = new AnimUndoStack();
        undo.Reset(new AnimUndoSnapshot(AnimClipIO.Serialize(clip), selection.Serialize()));

        AnimKeyEditor.DeleteSelectedKeys(clip.Tracks, selection);
        PushSnapshot(undo, clip, selection);
        Check.Equal(0, clip.Tracks[0].Keys.Count, "削除後はキーが無い");

        clip = ApplySnapshot(undo.Undo(), selection);

        Check.Equal(2, clip.Tracks[0].Keys.Count, "Undo でキーが戻る");
        Check.Equal(2, selection.Count, "選択も戻る");
        Check.True(selection.Contains(0, 0) && selection.Contains(0, 1), "戻ったキーが選択されている");
    }

    private static void RedoReappliesDeletion()
    {
        const float fps = 30f;
        var clip = ClipWithTrack();
        KeyAt(clip.Tracks[0], 0, fps, 1, 0, 0);

        var selection = new AnimKeySelection();
        selection.Add(0, 0);

        var undo = new AnimUndoStack();
        undo.Reset(new AnimUndoSnapshot(AnimClipIO.Serialize(clip), selection.Serialize()));

        AnimKeyEditor.DeleteSelectedKeys(clip.Tracks, selection);
        PushSnapshot(undo, clip, selection);

        clip = ApplySnapshot(undo.Undo(), selection);
        Check.Equal(1, clip.Tracks[0].Keys.Count, "Undo で戻る");

        clip = ApplySnapshot(undo.Redo(), selection);
        Check.Equal(0, clip.Tracks[0].Keys.Count, "Redo で再び削除される");
        Check.True(selection.IsEmpty, "Redo 後は選択も削除時の状態（空）");
    }

    private static void UndoUnwindsRepeatedNudges()
    {
        const float fps = 30f;
        const int nudges = 5;
        var clip = ClipWithTrack();
        KeyAt(clip.Tracks[0], 10, fps, 1, 0, 0);

        var selection = new AnimKeySelection();
        selection.Add(0, 0);

        var undo = new AnimUndoStack();
        undo.Reset(new AnimUndoSnapshot(AnimClipIO.Serialize(clip), selection.Serialize()));

        // ← → キーの連打相当。1 回の移動 = Undo 1 段。
        for (int i = 0; i < nudges; i++)
        {
            AnimKeyEditor.MoveSelectedKeys(clip.Tracks, selection, 1, fps, clip.Duration);
            PushSnapshot(undo, clip, selection);
        }
        Check.Equal(15, FramesOf(clip.Tracks[0], fps)[0], "5 回で 5 フレーム進む");

        for (int i = 0; i < nudges; i++)
            clip = ApplySnapshot(undo.Undo(), selection);

        Check.Equal(10, FramesOf(clip.Tracks[0], fps)[0], "同じ回数の Undo で元の位置へ戻る");
        Check.True(!undo.CanUndo, "これ以上戻せない");
        Check.True(selection.Contains(0, 0), "選択は保たれている");
    }

    private static void UndoRemovesPastedTrack()
    {
        const float fps = 30f;
        var source = ClipWithTrack("Arm");
        KeyAt(source.Tracks[0], 4, fps, 1, 2, 3);

        var sourceSel = new AnimKeySelection();
        sourceSel.Add(0, 0);
        var data = AnimKeyClipboard.Copy(source.Tracks, sourceSel, fps);

        var clip      = new AnimClip { Name = "target", Duration = 2f, Fps = 30f };
        var selection = new AnimKeySelection();
        var undo      = new AnimUndoStack();
        undo.Reset(new AnimUndoSnapshot(AnimClipIO.Serialize(clip), selection.Serialize()));

        var pasted = AnimKeyClipboard.Paste(clip, data, 12);
        selection.SetFromKeys(clip.Tracks, pasted);
        PushSnapshot(undo, clip, selection);

        Check.Equal(1, clip.Tracks.Count, "貼り付けでトラックが作られる");
        Check.Equal(1, selection.Count,   "貼り付けたキーが選択される");

        clip = ApplySnapshot(undo.Undo(), selection);

        Check.Equal(0, clip.Tracks.Count, "Undo で作られたトラックごと消える");
        Check.True(selection.IsEmpty, "選択も貼り付け前（空）に戻る");
    }
}
