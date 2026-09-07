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

        return harness.Run();
    }

    // ── 共通ヘルパー ────────────────────────────────────────────

    /// <summary>vec3 トラックを 1 本作る。</summary>
    private static AnimTrack Vec3Track(string actorPath = "", string property = "position") => new()
    {
        Target    = new AnimTarget { ActorPath = actorPath, Component = "actor_transform", Property = property },
        ValueType = AnimValueType.Vec3,
    };

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
        undo.Reset("A");
        undo.Push("B");

        Check.True(undo.CanUndo, "Undo できる");
        Check.Equal("A", undo.Undo() ?? "", "1 つ前へ戻る");
        Check.True(!undo.CanUndo, "これ以上は戻れない");
        Check.True(undo.Undo() is null, "戻れないときは null");
    }

    private static void RedoReappliesEdit()
    {
        var undo = new AnimUndoStack();
        undo.Reset("A");
        undo.Push("B");
        undo.Undo();

        Check.True(undo.CanRedo, "Redo できる");
        Check.Equal("B", undo.Redo() ?? "", "編集後の状態へ進む");
        Check.True(!undo.CanRedo, "これ以上は進めない");
    }

    private static void NewEditClearsRedo()
    {
        var undo = new AnimUndoStack();
        undo.Reset("A");
        undo.Push("B");
        undo.Undo();          // 現在 = A、Redo に B
        undo.Push("C");       // 新しい編集

        Check.True(!undo.CanRedo, "Redo 系列が破棄される");
        Check.Equal("A", undo.Undo() ?? "", "Undo は新しい編集の前へ戻る");
    }

    private static void IdenticalPushIsIgnored()
    {
        var undo = new AnimUndoStack();
        undo.Reset("A");
        undo.Push("A");
        Check.True(!undo.CanUndo, "同じ内容では履歴が増えない");
    }

    private static void UndoRespectsCapacity()
    {
        var undo = new AnimUndoStack(capacity: 3);
        undo.Reset("s0");
        for (int i = 1; i <= 10; i++) undo.Push("s" + i);

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
}
