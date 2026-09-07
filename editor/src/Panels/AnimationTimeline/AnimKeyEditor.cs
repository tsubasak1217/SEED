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
}
