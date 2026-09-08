// ============================================================
//  AnimKeyEditor.cs — トラックへのキー挿入・上書き（純ロジック）
//
//  「プレイヘッド位置にキーを打つ」操作の中身。UI から切り離してあるのは、
//  同じフレームに二重キーを作らない・時刻昇順を保つといった不変条件を
//  単体テストで固定するため。
//
//  【不変条件】
//   1. 1 トラック内に同一フレームのキーは 1 つだけ（既存があれば値を上書きする）。
//   2. Keys は常に時刻昇順（ランタイムの評価が昇順前提のため）。
//   3. 値の要素数は value_type に一致させる（不足は 0 埋め、超過は切り捨て）。
//
//  【サマリー行（全チャンネル）】
//   ドープシート最上段の「全チャンネル」行の操作（挿入・移動・削除・集計）も
//   ここへ純粋関数として置く（InsertOnAllTracks / MoveKeysAtFrame /
//   DeleteKeysAtFrame / SummaryFrames）。個別トラックへの適用を繰り返すだけの
//   薄いラッパーだが、「対象トラックの選び方」「複数トラックへの一括適用」という
//   ロジックそのものを単体テストで固定する意味で分離してある。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>キー挿入・上書きの結果。</summary>
/// <param name="KeyIndex">挿入・更新されたキーの（整列後の）インデックス。</param>
/// <param name="WasInserted">新規挿入なら true、既存キーの上書きなら false。</param>
internal readonly record struct KeyInsertResult(int KeyIndex, bool WasInserted);

/// <summary>トラックのキー編集を行う純粋なロジッククラス。</summary>
internal static class AnimKeyEditor
{
    /// <summary>
    /// 指定時刻（フレームへスナップ済みであること）へキーを挿入、
    /// 同一フレームに既存キーがあれば値だけを上書きする。
    /// </summary>
    /// <param name="track">対象トラック。</param>
    /// <param name="time">キー時刻（秒）。フレーム境界であることを前提とする。</param>
    /// <param name="values">書き込む値。要素数は value_type に合わせて調整される。</param>
    /// <param name="fps">同一フレーム判定に使うフレームレート。</param>
    /// <param name="interp">新規挿入時の補間方式（既存キーの補間は変更しない）。</param>
    public static KeyInsertResult InsertOrUpdate(
        AnimTrack track, float time, IReadOnlyList<float> values, float fps,
        string interp = AnimInterp.Linear)
    {
        var fitted = FitValues(values, track.ValueType);

        // 1) 同一フレームの既存キーを探す → 値のみ差し替える
        for (int i = 0; i < track.Keys.Count; i++)
        {
            if (!AnimFrameMath.SameFrame(track.Keys[i].Time, time, fps)) continue;
            track.Keys[i].Values = fitted;
            // 時刻もフレーム境界へ揃える（旧データの端数を掃除する意味もある）
            track.Keys[i].Time = time;
            return new KeyInsertResult(i, false);
        }

        // 2) 新規挿入 → 時刻昇順を維持する
        var key = new AnimKey { Time = time, Values = fitted, Interp = interp };
        track.Keys.Add(key);
        SortKeys(track);
        return new KeyInsertResult(track.Keys.IndexOf(key), true);
    }

    /// <summary>キー列を時刻昇順へ整列する（移動・挿入のたびに呼ぶ）。</summary>
    public static void SortKeys(AnimTrack track)
        => track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));

    /// <summary>
    /// 指定時刻に「そのトラックが現在示している値」を求める。
    /// キーが無ければ value_type の要素数ぶんの 0 配列、
    /// あれば直前のキー（時刻がそれ以下で最大のもの）の値を複製して返す。
    ///
    /// ※ 補間はしない。ダブルクリックでキーを増やしたときに
    ///    「直前の値を保持する」ステップ的な初期値を与えるためのもの。
    /// </summary>
    public static float[] PreviousOrDefaultValues(AnimTrack track, float time)
    {
        var count = AnimPropertyRegistry.ComponentCount(track.ValueType);
        var prev  = track.Keys.Where(k => k.Time <= time).OrderBy(k => k.Time).LastOrDefault();
        return prev is not null ? (float[])prev.Values.Clone() : new float[count];
    }

    /// <summary>
    /// 値の要素数を value_type に合わせる（不足は 0 埋め、超過は切り捨て）。
    /// アクターの現在値（3 要素）を float トラック（1 要素）へ入れるといった
    /// 取り違えでキー配列が壊れるのを防ぐ。
    /// </summary>
    public static float[] FitValues(IReadOnlyList<float> values, string valueType)
    {
        var count  = AnimPropertyRegistry.ComponentCount(valueType);
        var result = new float[count];
        for (int i = 0; i < count && i < values.Count; i++) result[i] = values[i];
        return result;
    }

    /// <summary>
    /// 指定フレームに存在するキーのインデックスを返す（無ければ -1）。
    /// 「そのフレームに既にキーがあるか」の問い合わせ用。
    /// </summary>
    public static int FindKeyAtFrame(AnimTrack track, float time, float fps)
    {
        for (int i = 0; i < track.Keys.Count; i++)
            if (AnimFrameMath.SameFrame(track.Keys[i].Time, time, fps)) return i;
        return -1;
    }

    // ── トラック種別変換（KindMismatch の解消）────────────────────
    //
    // トラック追加時にドロップダウンの既定選択を誤る／actor_path で意図的に
    // 別種のアクタを狙う等で、対象アクタの実際の種別（2D=CanvasTransform /
    // 3D=Transform）とトラックの種別が食い違う（KindInsertPrecheck.KindMismatch）
    // ことがある。以前はエラーで中断するだけだったが、位置・回転・スケールは
    // 2D⇔3D で機械的に変換できるため、ユーザーの承諾を得たうえで
    // トラックの種別そのものを変換して続行できるようにする。

    /// <summary>
    /// トラックの変換コンポーネント種別（actor_transform ⇔ canvas_transform）を変換する。
    /// 対象は Position/Rotation/Scale の 3 プロパティのみ（このレジストリに変換規則がある組だけ）。
    /// Sprite の色など変換規則を持たないプロパティは <paramref name="toComponent"/> が
    /// レジストリに未登録の組になり、その場合は何もしない（安全側）。
    ///
    /// 【変換規則】
    ///  actor_transform.position (vec3 x,y,z)  ⇔ canvas_transform.position (vec2 x,y)
    ///     3D→2D: z を切り捨てる。2D→3D: z=0 で補う。
    ///  actor_transform.rotation (vec3 Euler)  ⇔ canvas_transform.rotation (float z)
    ///     3D→2D: ez だけを残す。2D→3D: (0, 0, rz) として復元する。
    ///  actor_transform.scale    (vec3 x,y,z)  ⇔ canvas_transform.scale    (vec2 x,y)
    ///     3D→2D: sz を切り捨てる。2D→3D: sz=1 で補う。
    ///
    /// 時刻・補間方式はそのまま保つ。タンジェント（bezier 補間時のみ設定される）は
    /// 有無をそのまま保ちつつ、値配列と同じ規則で要素数を詰め直す。
    /// </summary>
    /// <param name="track">変換対象トラック（in-place で書き換える）。</param>
    /// <param name="toComponent">変換先のコンポーネント種別
    /// （<see cref="AnimActorSnapshot.TransformComponent"/> か <see cref="AnimActorSnapshot.CanvasTransformComponent"/>）。</param>
    public static void ConvertTrackKind(AnimTrack track, string toComponent)
    {
        if (track.Target.Component == toComponent) return; // 既に変換先と同じなら何もしない

        var property     = track.Target.Property;
        var newValueType = AnimPropertyRegistry.ResolveValueType(toComponent, property);
        if (newValueType is null) return; // 変換規則を持たない組（未登録）は変換しない

        var toIs2D = toComponent == AnimActorSnapshot.CanvasTransformComponent;
        foreach (var key in track.Keys)
        {
            key.Values     = ConvertTransformValues(key.Values, property, toIs2D);
            key.InTangent  = key.InTangent  is null ? null : ConvertTransformValues(key.InTangent,  property, toIs2D);
            key.OutTangent = key.OutTangent is null ? null : ConvertTransformValues(key.OutTangent, property, toIs2D);
        }

        track.Target.Component = toComponent;
        track.ValueType        = newValueType;
    }

    /// <summary>
    /// Position/Rotation/Scale 1 プロパティぶんの値配列を 2D⇔3D 間で変換する。
    /// <see cref="ConvertTrackKind"/> の値・タンジェント配列どちらにも使う共通ロジック。
    /// </summary>
    /// <param name="values">変換前の値（要素の欠落は 0 として扱う）。</param>
    /// <param name="property">プロパティ名（<see cref="AnimActorSnapshot"/> の *Property 定数）。</param>
    /// <param name="toIs2D">変換先が 2D（CanvasTransform）なら true、3D（Transform）なら false。</param>
    private static float[] ConvertTransformValues(float[] values, string property, bool toIs2D)
    {
        float At(int i) => i < values.Length ? values[i] : 0f;

        if (property == AnimActorSnapshot.PositionProperty)
            return toIs2D
                ? new[] { At(0), At(1) }         // vec3(x,y,z)    → vec2(x,y)
                : new[] { At(0), At(1), 0f };     // vec2(x,y)      → vec3(x,y,z=0)

        if (property == AnimActorSnapshot.ScaleProperty)
            return toIs2D
                ? new[] { At(0), At(1) }         // vec3(sx,sy,sz) → vec2(sx,sy)
                : new[] { At(0), At(1), 1f };     // vec2(sx,sy)    → vec3(sx,sy,sz=1)

        if (property == AnimActorSnapshot.RotationProperty)
            return toIs2D
                ? new[] { At(2) }                 // vec3 Euler(ex,ey,ez) → float(z=ez)
                : new[] { 0f, 0f, At(0) };         // float(rz)            → vec3 Euler(0,0,rz)

        // 変換規則を持たないプロパティ（呼び出し元が ResolveValueType で弾いているため通常は来ない）
        return values;
    }

    // ── サマリー行（全チャンネル）────────────────────────────────
    //
    // Blender の「Summary」チャンネルに相当する機能。ドープシート最上段に
    // 「いずれかのトラックにキーがあるフレーム」を◆として表示し、そこへの
    // 操作（挿入・移動・削除）を対象トラック全部へ一括適用する。
    // 表示用の集計（SummaryFrames）も、操作の実体（InsertOnAllTracks 等）も
    // ここに純粋関数として置き、DopeSheetPanel / AnimationTimelinePanel から
    // WPF 抜きで単体テストできるようにする。

    /// <summary>
    /// 「いずれかのトラックにキーがあるフレーム」の時刻一覧を昇順・重複無しで返す。
    /// サマリー行の◆はこの結果をそのまま描画に使う（派生データであり、保存はしない）。
    /// </summary>
    public static List<float> SummaryFrames(IEnumerable<AnimTrack> tracks)
        => tracks.SelectMany(t => t.Keys.Select(k => k.Time))
                 .Distinct()
                 .OrderBy(t => t)
                 .ToList();

    /// <summary>
    /// サマリー行からのキー挿入。actor_path がキー対象と一致するトラックへ挿入する
    /// （1 本も一致しなければ、クリップの全トラックへ挿入する＝「対象が絞れないなら全部」）。
    /// 各トラックの値は valueProvider（呼び出し側が現在値スナップショットなどから用意する）に委ねる。
    /// WPF / Runtime 依存を持ち込まないよう、値の出所は関数として注入する形にしてある。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="actorPathFilter">キー対象アクタの actor_path（一致するものだけを対象にする）。</param>
    /// <param name="time">挿入時刻（フレームへスナップ済みであること）。</param>
    /// <param name="valueProvider">トラックごとに書き込む値を返す関数。</param>
    /// <param name="fps">同一フレーム判定に使うフレームレート。</param>
    /// <param name="interp">新規挿入時の補間方式。</param>
    public static List<KeyInsertResult> InsertOnAllTracks(
        IReadOnlyList<AnimTrack> tracks, string actorPathFilter, float time,
        Func<AnimTrack, IReadOnlyList<float>> valueProvider, float fps,
        string interp = AnimInterp.Linear)
    {
        var targets = tracks.Where(t => t.Target.ActorPath == actorPathFilter).ToList();
        if (targets.Count == 0) targets = tracks.ToList();   // 絞り込めないときは全トラック

        var results = new List<KeyInsertResult>(targets.Count);
        foreach (var track in targets)
            results.Add(InsertOrUpdate(track, time, valueProvider(track), fps, interp));
        return results;
    }

    /// <summary>
    /// サマリー行の◆ドラッグ: 指定フレームにあるキーを全トラックについて newTime へ移動する。
    /// そのフレームにキーを持たないトラックは無視する（サマリーは「和集合」なので当然存在しうる）。
    /// </summary>
    public static void MoveKeysAtFrame(IEnumerable<AnimTrack> tracks, float oldTime, float newTime, float fps)
    {
        foreach (var track in tracks)
        {
            var idx = FindKeyAtFrame(track, oldTime, fps);
            if (idx < 0) continue;
            track.Keys[idx].Time = newTime;
            SortKeys(track);
        }
    }

    /// <summary>
    /// サマリー行の◆削除: 指定フレームにあるキーを全トラックから削除する。
    /// </summary>
    public static void DeleteKeysAtFrame(IEnumerable<AnimTrack> tracks, float time, float fps)
    {
        foreach (var track in tracks)
        {
            var idx = FindKeyAtFrame(track, time, fps);
            if (idx >= 0) track.Keys.RemoveAt(idx);
        }
    }

    // ── 複数選択への一括操作 ────────────────────────────────────
    //
    // ドープシートの複数選択（AnimKeySelection）に対する移動・削除・補間変更。
    // いずれも「選択の張り直し」まで面倒を見る（添字は並べ替えでズレるため、
    // 呼び出し側に正規化を任せると必ず取りこぼす）。

    /// <summary>
    /// 選択キーをまとめて動かせる実際のフレーム移動量を求める。
    /// 先頭が 0 フレームより前へ、末尾が最終フレームより後ろへ出ないよう縮める
    /// （選択の相対間隔を保つため、個別にクランプせず移動量そのものを詰める）。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="selection">選択中のキー。</param>
    /// <param name="frameDelta">要求する移動量（フレーム）。</param>
    /// <param name="fps">フレームレート。</param>
    /// <param name="duration">クリップ長（秒）。</param>
    public static int ClampFrameDelta(
        IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection, int frameDelta, float fps, float duration)
    {
        var frames = SelectedFrames(tracks, selection, fps);
        if (frames.Count == 0) return 0;

        var last = AnimFrameMath.LastFrame(fps, duration);
        var min  = frames.Min();
        var max  = frames.Max();

        if (frameDelta < 0) return Math.Max(frameDelta, -min);
        if (frameDelta > 0) return Math.Min(frameDelta, last - max);
        return 0;
    }

    /// <summary>選択キーのフレーム番号一覧（重複あり）。移動量クランプの内部計算用。</summary>
    private static List<int> SelectedFrames(IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection, float fps)
    {
        var frames = new List<int>(selection.Count);
        foreach (var r in selection.Ordered())
        {
            if (r.TrackIndex < 0 || r.TrackIndex >= tracks.Count) continue;
            var keys = tracks[r.TrackIndex].Keys;
            if (r.KeyIndex < 0 || r.KeyIndex >= keys.Count) continue;
            frames.Add(AnimFrameMath.TimeToFrame(keys[r.KeyIndex].Time, fps));
        }
        return frames;
    }

    /// <summary>
    /// 選択キーを frameDelta フレームだけまとめて動かす。
    ///
    /// 移動先に**選択されていない**既存キーがあれば、そのキーを削除して置き換える
    /// （Blender と同じ「上書き」方針。拒否より、見た目どおりに動くほうが編集は速い）。
    /// 移動後は時刻昇順へ並べ直し、選択をキーオブジェクトから引き直す。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="selection">選択中のキー（移動後の添字へ更新される）。</param>
    /// <param name="frameDelta">移動量（フレーム）。クランプ前の値でよい。</param>
    /// <param name="fps">フレームレート。</param>
    /// <param name="duration">クリップ長（秒）。</param>
    /// <returns>実際に移動したフレーム数（0 なら何もしていない）。</returns>
    public static int MoveSelectedKeys(
        IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection, int frameDelta, float fps, float duration)
    {
        var delta = ClampFrameDelta(tracks, selection, frameDelta, fps, duration);
        if (delta == 0) return 0;

        var moved = new List<(int TrackIndex, AnimKey Key)>(selection.Count);

        foreach (var (trackIndex, keyIndices) in selection.ByTrack())
        {
            if (trackIndex < 0 || trackIndex >= tracks.Count) continue;
            var track = tracks[trackIndex];

            // 1) 移動対象のキーオブジェクトと移動先フレームを先に確定する
            //    （添字は削除で動くため、オブジェクト参照で持ち回る）
            var plans = new List<(AnimKey Key, int Frame)>(keyIndices.Count);
            foreach (var ki in keyIndices)
            {
                if (ki < 0 || ki >= track.Keys.Count) continue;
                var key = track.Keys[ki];
                plans.Add((key, AnimFrameMath.TimeToFrame(key.Time, fps) + delta));
            }
            if (plans.Count == 0) continue;

            // 2) 移動先に居座る「選択されていない」キーを退かす（上書き）
            var targetFrames = new HashSet<int>(plans.Select(p => p.Frame));
            var movingKeys   = new HashSet<AnimKey>(plans.Select(p => p.Key));
            track.Keys.RemoveAll(k => !movingKeys.Contains(k)
                                   && targetFrames.Contains(AnimFrameMath.TimeToFrame(k.Time, fps)));

            // 3) 時刻を書き換えて整列する
            foreach (var (key, frame) in plans)
            {
                key.Time = AnimFrameMath.FrameToTime(frame, fps);
                moved.Add((trackIndex, key));
            }
            SortKeys(track);
        }

        selection.SetFromKeys(tracks, moved);
        return delta;
    }

    /// <summary>
    /// 選択キーをすべて削除する。削除後は選択を空にする
    /// （消したものを選び続けないため。呼び出し側での選択解除忘れも防ぐ）。
    /// </summary>
    /// <returns>削除したキー数。</returns>
    public static int DeleteSelectedKeys(IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection)
    {
        var removed = 0;
        foreach (var (trackIndex, keyIndices) in selection.ByTrack())
        {
            if (trackIndex < 0 || trackIndex >= tracks.Count) continue;
            var keys = tracks[trackIndex].Keys;
            // 後ろから消す（前から消すと残りの添字がズレる）
            foreach (var ki in keyIndices.OrderByDescending(i => i))
            {
                if (ki < 0 || ki >= keys.Count) continue;
                keys.RemoveAt(ki);
                removed++;
            }
        }
        selection.Clear();
        return removed;
    }

    /// <summary>選択キーの補間方式をまとめて変更する（値エディタの一括編集）。</summary>
    /// <returns>変更したキー数。</returns>
    public static int SetInterpForSelection(
        IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection, string interp)
    {
        var changed = 0;
        foreach (var r in selection.Ordered())
        {
            if (r.TrackIndex < 0 || r.TrackIndex >= tracks.Count) continue;
            var keys = tracks[r.TrackIndex].Keys;
            if (r.KeyIndex < 0 || r.KeyIndex >= keys.Count) continue;
            keys[r.KeyIndex].Interp = interp;
            changed++;
        }
        return changed;
    }
}
