// ============================================================
//  AnimKeyClipboard.cs — キーのコピー / 貼り付け（純ロジック）
//
//  選択中のキーを JSON へ書き出し、別のフレーム（＝プレイヘッド位置）や
//  別のクリップへ貼り付ける。クリップボード内容はアプリ内の静的フィールドと
//  システムクリップボード（テキスト）の両方に置くため、表現は JSON 文字列にする。
//
//  【フレームは相対で持つ】
//   コピー時に「選択中の最小フレーム」を基準（アンカー）とし、各キーは
//   そこからの相対フレーム数で記録する。貼り付けはプレイヘッドを基準に
//   相対位置を復元するので、テンポを保ったままクリップ内・クリップ間で
//   キーの塊を移動できる。
//
//  【トラックの対応付け】
//   トラックは添字ではなく対象（actor_path + component.property）で照合する。
//   添字はクリップごとに違うため、別クリップへ貼ると必ず壊れるから。
//   一致するトラックが無ければ value_type ごと新規作成する。
//
//  【貼り付け時の衝突】
//   同一フレームに既存キーがあれば上書きする（Blender と同じ「置き換え」方針）。
//   duration を超えるフレームは最終フレームへ丸める（クリップ外のキーは作らない）。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>クリップボード上の 1 キー（アンカーからの相対フレーム + 値）。</summary>
/// <param name="FrameOffset">アンカーからの相対フレーム数（0 以上）。</param>
/// <param name="Values">値本体（コピー元 value_type の要素数）。</param>
/// <param name="Interp">補間方式。</param>
/// <param name="InTangent">ベジェ入タンジェント（無ければ null）。</param>
/// <param name="OutTangent">ベジェ出タンジェント（無ければ null）。</param>
internal sealed record AnimClipboardKey(
    int FrameOffset,
    float[] Values,
    string Interp,
    float[]? InTangent,
    float[]? OutTangent);

/// <summary>クリップボード上の 1 トラック（対象の同定情報 + キー列）。</summary>
/// <param name="ActorPath">対象アクターへの相対パス。</param>
/// <param name="Component">コンポーネント種別。</param>
/// <param name="Property">プロパティ名。</param>
/// <param name="ValueType">値の型（トラック新規作成時に使う）。</param>
/// <param name="Keys">キー列（相対フレーム昇順）。</param>
internal sealed record AnimClipboardTrack(
    string ActorPath,
    string Component,
    string Property,
    string ValueType,
    List<AnimClipboardKey> Keys);

/// <summary>クリップボード内容そのもの。</summary>
/// <param name="Tracks">トラック単位のキー列。</param>
internal sealed record AnimClipboardData(List<AnimClipboardTrack> Tracks)
{
    /// <summary>1 キーも入っていないか。</summary>
    public bool IsEmpty => Tracks.Count == 0 || Tracks.All(t => t.Keys.Count == 0);
}

/// <summary>キーのコピー／貼り付けを行う純粋なロジッククラス。</summary>
internal static class AnimKeyClipboard
{
    /// <summary>クリップボード JSON の種別を示すマーカー（他アプリのテキストを誤って読み込まないため）。</summary>
    public const string FormatMarker = "seed.anim.keys";

    /// <summary>クリップボード JSON の書式バージョン（将来の形式変更を検出するため）。</summary>
    public const int FormatVersion = 1;

    // ── コピー ──────────────────────────────────────────────────

    /// <summary>
    /// 選択中のキーをクリップボード内容へ抜き出す。
    /// フレームは「選択中の最小フレーム」を 0 とした相対値で記録する。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="selection">選択中のキー。</param>
    /// <param name="fps">時刻⇔フレーム変換に使うフレームレート。</param>
    public static AnimClipboardData Copy(IReadOnlyList<AnimTrack> tracks, AnimKeySelection selection, float fps)
    {
        var refs = selection.Ordered()
                            .Where(r => r.TrackIndex >= 0 && r.TrackIndex < tracks.Count
                                     && r.KeyIndex   >= 0 && r.KeyIndex   < tracks[r.TrackIndex].Keys.Count)
                            .ToList();
        if (refs.Count == 0) return new AnimClipboardData(new List<AnimClipboardTrack>());

        // アンカー = 選択中の最小フレーム（貼り付け時にプレイヘッドへ来る位置）
        var anchor = refs.Min(r => AnimFrameMath.TimeToFrame(tracks[r.TrackIndex].Keys[r.KeyIndex].Time, fps));

        var result = new List<AnimClipboardTrack>();
        foreach (var group in refs.GroupBy(r => r.TrackIndex).OrderBy(g => g.Key))
        {
            var track = tracks[group.Key];
            var keys  = group.Select(r => track.Keys[r.KeyIndex])
                             .OrderBy(k => k.Time)
                             .Select(k => new AnimClipboardKey(
                                 AnimFrameMath.TimeToFrame(k.Time, fps) - anchor,
                                 (float[])k.Values.Clone(),
                                 k.Interp,
                                 k.InTangent  is null ? null : (float[])k.InTangent.Clone(),
                                 k.OutTangent is null ? null : (float[])k.OutTangent.Clone()))
                             .ToList();

            result.Add(new AnimClipboardTrack(
                track.Target.ActorPath, track.Target.Component, track.Target.Property, track.ValueType, keys));
        }
        return new AnimClipboardData(result);
    }

    // ── 貼り付け ────────────────────────────────────────────────

    /// <summary>
    /// クリップボード内容を貼り付ける。先頭キーが pasteFrame に来るよう相対位置を復元する。
    /// 対象トラックが無ければ作成し、同一フレームの既存キーは上書きする。
    /// </summary>
    /// <param name="clip">貼り付け先クリップ（トラックが増えることがある）。</param>
    /// <param name="data">貼り付ける内容。</param>
    /// <param name="pasteFrame">貼り付け基準フレーム（通常はプレイヘッド位置）。</param>
    /// <returns>貼り付けたキーの (トラック添字, キーオブジェクト) 列（選択の張り直しに使う）。</returns>
    public static List<(int TrackIndex, AnimKey Key)> Paste(AnimClip clip, AnimClipboardData data, int pasteFrame)
    {
        var pasted = new List<(int, AnimKey)>();
        var fps    = AnimFrameMath.NormalizeFps(clip.Fps);
        var last   = AnimFrameMath.LastFrame(fps, clip.Duration);

        foreach (var src in data.Tracks)
        {
            var trackIndex = FindTrackIndex(clip, src);
            if (trackIndex < 0)
            {
                // 対象トラックが無ければ作る（別クリップへの貼り付けを成立させるため）
                clip.Tracks.Add(new AnimTrack
                {
                    Target    = new AnimTarget { ActorPath = src.ActorPath, Component = src.Component, Property = src.Property },
                    ValueType = src.ValueType,
                });
                trackIndex = clip.Tracks.Count - 1;
            }

            var track = clip.Tracks[trackIndex];
            foreach (var srcKey in src.Keys)
            {
                var frame = Math.Clamp(pasteFrame + srcKey.FrameOffset, 0, last);
                var time  = AnimFrameMath.FrameToTime(frame, fps);

                // 同一フレームの既存キーは値ごと置き換える（InsertOrUpdate と同じ「上書き」方針）
                var existing = AnimKeyEditor.FindKeyAtFrame(track, time, fps);
                AnimKey key;
                if (existing >= 0)
                {
                    key = track.Keys[existing];
                    key.Time = time;
                }
                else
                {
                    key = new AnimKey { Time = time };
                    track.Keys.Add(key);
                }

                key.Values     = AnimKeyEditor.FitValues(srcKey.Values, track.ValueType);
                key.Interp     = srcKey.Interp;
                key.InTangent  = srcKey.InTangent  is null ? null : (float[])srcKey.InTangent.Clone();
                key.OutTangent = srcKey.OutTangent is null ? null : (float[])srcKey.OutTangent.Clone();
                pasted.Add((trackIndex, key));
            }
            AnimKeyEditor.SortKeys(track);
        }
        return pasted;
    }

    /// <summary>対象（actor_path + component.property）が一致するトラックの添字を探す（無ければ -1）。</summary>
    private static int FindTrackIndex(AnimClip clip, AnimClipboardTrack src)
    {
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            var t = clip.Tracks[i].Target;
            if (t.ActorPath == src.ActorPath && t.Component == src.Component && t.Property == src.Property)
                return i;
        }
        return -1;
    }

    // ── JSON 直列化（システムクリップボードへ載せるため）──────────

    /// <summary>クリップボード内容を JSON 文字列へ変換する。</summary>
    public static string Serialize(AnimClipboardData data)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatMarker);
            w.WriteNumber("version", FormatVersion);
            w.WriteStartArray("tracks");
            foreach (var track in data.Tracks)
            {
                w.WriteStartObject();
                w.WriteString("actor_path", track.ActorPath);
                w.WriteString("component",  track.Component);
                w.WriteString("property",   track.Property);
                w.WriteString("value_type", track.ValueType);
                w.WriteStartArray("keys");
                foreach (var key in track.Keys)
                {
                    w.WriteStartObject();
                    w.WriteNumber("frame_offset", key.FrameOffset);
                    WriteFloats(w, "value", key.Values);
                    w.WriteString("interp", key.Interp);
                    if (key.InTangent  is { Length: > 0 }) WriteFloats(w, "in_tan",  key.InTangent);
                    if (key.OutTangent is { Length: > 0 }) WriteFloats(w, "out_tan", key.OutTangent);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// JSON 文字列からクリップボード内容を復元する。
    /// 形式マーカーが一致しない・壊れている場合は null（＝貼り付け不可）。
    /// システムクリップボードには無関係なテキストが入りうるため、必ず判定する。
    /// </summary>
    public static AnimClipboardData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != FormatMarker) return null;

            var tracks = new List<AnimClipboardTrack>();
            if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tracksEl.EnumerateArray())
                {
                    var keys = new List<AnimClipboardKey>();
                    if (t.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in keysEl.EnumerateArray())
                        {
                            keys.Add(new AnimClipboardKey(
                                k.TryGetProperty("frame_offset", out var fo) ? fo.GetInt32() : 0,
                                ReadFloats(k, "value"),
                                k.TryGetProperty("interp", out var ip) ? ip.GetString() ?? AnimInterp.Linear : AnimInterp.Linear,
                                k.TryGetProperty("in_tan",  out _) ? ReadFloats(k, "in_tan")  : null,
                                k.TryGetProperty("out_tan", out _) ? ReadFloats(k, "out_tan") : null));
                        }
                    }
                    tracks.Add(new AnimClipboardTrack(
                        ReadString(t, "actor_path"), ReadString(t, "component"),
                        ReadString(t, "property"),
                        t.TryGetProperty("value_type", out var vt) ? vt.GetString() ?? AnimValueType.Float : AnimValueType.Float,
                        keys));
                }
            }
            return new AnimClipboardData(tracks);
        }
        catch (JsonException)
        {
            return null;   // 他アプリのテキストなど、JSON ですらない内容
        }
    }

    /// <summary>文字列プロパティを読む（無ければ空文字列）。</summary>
    private static string ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

    /// <summary>float 配列プロパティを読む（数値単体も 1 要素配列として受ける）。</summary>
    private static float[] ReadFloats(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return Array.Empty<float>();
        if (p.ValueKind == JsonValueKind.Number) return new[] { p.GetSingle() };
        if (p.ValueKind != JsonValueKind.Array)  return Array.Empty<float>();

        var arr = new float[p.GetArrayLength()];
        int i = 0;
        foreach (var v in p.EnumerateArray()) arr[i++] = v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
        return arr;
    }

    /// <summary>float 配列プロパティを書く。</summary>
    private static void WriteFloats(Utf8JsonWriter w, string name, float[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
