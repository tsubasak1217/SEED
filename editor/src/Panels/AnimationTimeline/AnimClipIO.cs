// ============================================================
//  AnimClipIO.cs — .anim ファイルの読み込み／書き出し
//
//  Rust 側（serde_json）と完全互換な JSON 構造で読み書きする。
//  value_type ごとに value の JSON 表現が変わる（float=裸の数値、
//  vec2/vec3/color=配列、bool=真偽値）ため、System.Text.Json の属性ベース
//  デシリアライズではなく JsonDocument / Utf8JsonWriter で手動制御する
//  （InspectorPanel 既存コードの手動 JsonElement 解析パターンを踏襲）。
//
//  数値は常に InvariantCulture で読み書きする。
//
//  【版（format_version）の扱い】
//  .anim は「書き手がエディタにしか無い」形式なので、版を刻むのはここの責務である
//  （docs/asset_migration.md 5.1 / 6.5）。
//    ・書き出し … トップレベルの**先頭**へ現行版を書く。値は AssetFormats の表から取る
//    ・読み込み … AssetMigrationGateway を通す。古ければランタイムが持ち上げ、
//                 未来版（新しいエンジンで保存されたファイル）は開かずに例外にする
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SEEDEditor.Migration;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>.anim ファイルの読み込み／書き出しを担当する静的クラス。</summary>
internal static class AnimClipIO
{
    // ── 読み込み ────────────────────────────────────────────────

    /// <summary>
    /// .anim ファイルを読み込み、編集用モデルへ変換する。
    ///
    /// <para>
    /// 古い形式ならランタイムの変換を通してから解釈する（メモリ上だけ。ファイルは書き換えない）。
    /// 未来版・変換失敗のときは <see cref="InvalidDataException"/> を投げる。
    /// **既定値のクリップを返してはいけない**（そのまま保存すると利用者の作ったキーが全部消える）。
    /// </para>
    /// </summary>
    /// <param name="path">読み込む .anim の絶対パス。</param>
    /// <exception cref="FileNotFoundException">ファイルが存在しない場合。</exception>
    /// <exception cref="InvalidDataException">未来版・変換失敗で開けない場合。</exception>
    public static AnimClip Load(string path)
    {
        // notify: false — 呼び出し元（AnimationTimelinePanel）が例外を捕まえて
        // 自前のダイアログを出すため、ここで出すと同じ文言が 2 回出る。
        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.Anim, notify: false);
        if (read.Status == AssetReadStatus.Missing)
            throw new FileNotFoundException($".anim が見つかりません: {path}", path);
        if (!read.HasText)
            throw new InvalidDataException(read.Message);

        return Parse(read.Text);
    }

    /// <summary>JSON 文字列から編集用モデルを構築する（テスト・ラウンドトリップ検証用に公開）。</summary>
    public static AnimClip Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var clip = new AnimClip
        {
            Name     = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "",
            Duration = root.TryGetProperty("duration", out var dp) ? dp.GetSingle() : 0f,
            // fps は後付けフィールド。旧 .anim には無いので既定値へ落とす
            // （NormalizeFps が 0・負値・NaN もまとめて既定へ矯正する）。
            Fps      = AnimFrameMath.NormalizeFps(
                           root.TryGetProperty("fps", out var fp) && fp.ValueKind == JsonValueKind.Number
                               ? fp.GetSingle() : AnimFrameMath.DefaultFps),
            LoopMode = root.TryGetProperty("loop_mode", out var lp) ? lp.GetString() ?? AnimLoopMode.Once : AnimLoopMode.Once,
        };

        if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var trackEl in tracksEl.EnumerateArray())
            {
                var track = new AnimTrack
                {
                    ValueType = trackEl.TryGetProperty("value_type", out var vtp) ? vtp.GetString() ?? AnimValueType.Float : AnimValueType.Float,
                };

                if (trackEl.TryGetProperty("target", out var targetEl))
                {
                    track.Target = new AnimTarget
                    {
                        ActorPath = targetEl.TryGetProperty("actor_path", out var apEl) ? apEl.GetString() ?? "" : "",
                        Component = targetEl.TryGetProperty("component",  out var cpEl) ? cpEl.GetString() ?? "" : "",
                        Property  = targetEl.TryGetProperty("property",   out var ppEl) ? ppEl.GetString() ?? "" : "",
                    };
                }

                if (trackEl.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var keyEl in keysEl.EnumerateArray())
                    {
                        var key = new AnimKey
                        {
                            Time   = keyEl.TryGetProperty("time", out var tp) ? tp.GetSingle() : 0f,
                            Interp = keyEl.TryGetProperty("interp", out var ip) ? ip.GetString() ?? AnimInterp.Linear : AnimInterp.Linear,
                            Values = keyEl.TryGetProperty("value", out var vEl) ? ReadValue(vEl, track.ValueType) : new float[AnimPropertyRegistry.ComponentCount(track.ValueType)],
                        };
                        if (keyEl.TryGetProperty("in_tan", out var inTanEl) && inTanEl.ValueKind == JsonValueKind.Array)
                            key.InTangent = ReadFloatArray(inTanEl);
                        if (keyEl.TryGetProperty("out_tan", out var outTanEl) && outTanEl.ValueKind == JsonValueKind.Array)
                            key.OutTangent = ReadFloatArray(outTanEl);
                        track.Keys.Add(key);
                    }
                }

                clip.Tracks.Add(track);
            }
        }

        if (root.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var evEl in eventsEl.EnumerateArray())
            {
                clip.Events.Add(new AnimEventMarker
                {
                    Time = evEl.TryGetProperty("time", out var etp) ? etp.GetSingle() : 0f,
                    Name = evEl.TryGetProperty("name", out var enp) ? enp.GetString() ?? "" : "",
                });
            }
        }

        return clip;
    }

    /// <summary>
    /// value_type に応じて JSON の value を float 配列へ変換する。
    /// float → 単一要素配列、vec2/vec3/color → 配列そのまま、bool → [1] or [0]。
    /// </summary>
    private static float[] ReadValue(JsonElement valueEl, string valueType)
    {
        if (valueType == AnimValueType.Bool)
        {
            var b = valueEl.ValueKind == JsonValueKind.True || valueEl.ValueKind == JsonValueKind.False
                ? valueEl.GetBoolean()
                : valueEl.ValueKind == JsonValueKind.Number && valueEl.GetSingle() != 0f;
            return new[] { b ? 1f : 0f };
        }

        if (valueEl.ValueKind == JsonValueKind.Array)
            return ReadFloatArray(valueEl);

        // float 単体（裸の数値）
        return new[] { valueEl.ValueKind == JsonValueKind.Number ? valueEl.GetSingle() : 0f };
    }

    private static float[] ReadFloatArray(JsonElement arrEl)
    {
        var list = new float[arrEl.GetArrayLength()];
        int i = 0;
        foreach (var el in arrEl.EnumerateArray())
            list[i++] = el.GetSingle();
        return list;
    }

    // ── 書き出し ────────────────────────────────────────────────

    /// <summary>
    /// 編集用モデルを .anim ファイルへ書き出す。
    ///
    /// <para>
    /// 書き込みは <see cref="SEEDEditor.Assets.SafeFileWriter"/> 経由の原子的置換
    /// （旧版を .backup/ へ退避 → .tmp へ書き切って rename）で行う。
    /// 途中で落ちても「書きかけの .anim」が残らない。
    /// </para>
    /// </summary>
    /// <param name="clip">保存するクリップ。</param>
    /// <param name="path">保存先の絶対パス。</param>
    /// <param name="assetsRoot">
    /// アセットルート（バックアップを &lt;assets&gt;/.backup/ へ集めるために使う）。
    /// null なら .anim の隣に .backup フォルダができる。
    /// </param>
    public static void Save(AnimClip clip, string path, string? assetsRoot = null)
    {
        var json = Serialize(clip);
        SEEDEditor.Assets.SafeFileWriter.WriteAllTextAtomic(path, json, assetsRoot);
    }

    /// <summary>編集用モデルを Rust serde 互換の JSON 文字列へ変換する（ラウンドトリップ検証用に公開）。</summary>
    public static string Serialize(AnimClip clip)
    {
        var options = new JsonWriterOptions { Indented = true };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            // 版はトップレベルの先頭に置く（差分を見たときに版がすぐ分かるように）。
            // 欄名も値も AssetFormats の表から取る（値をここへ直書きしない）。
            writer.WriteNumber(AssetFormats.Anim.VersionKeyName, AssetFormats.Anim.CurrentVersion);
            writer.WriteString("name", clip.Name);
            writer.WriteNumber("duration", clip.Duration);
            // 編集用フレームレート（Rust 側は #[serde(default)] なので旧エディタとも共存できる）
            writer.WriteNumber("fps", AnimFrameMath.NormalizeFps(clip.Fps));
            writer.WriteString("loop_mode", clip.LoopMode);

            writer.WriteStartArray("tracks");
            foreach (var track in clip.Tracks)
            {
                writer.WriteStartObject();

                writer.WriteStartObject("target");
                writer.WriteString("actor_path", track.Target.ActorPath);
                writer.WriteString("component",  track.Target.Component);
                writer.WriteString("property",   track.Target.Property);
                writer.WriteEndObject();

                writer.WriteString("value_type", track.ValueType);

                writer.WriteStartArray("keys");
                foreach (var key in track.Keys)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("time", key.Time);
                    WriteValue(writer, key.Values, track.ValueType);
                    writer.WriteString("interp", key.Interp);
                    if (key.InTangent is { Length: > 0 })
                        WriteFloatArray(writer, "in_tan", key.InTangent);
                    if (key.OutTangent is { Length: > 0 })
                        WriteFloatArray(writer, "out_tan", key.OutTangent);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("events");
            foreach (var ev in clip.Events)
            {
                writer.WriteStartObject();
                writer.WriteNumber("time", ev.Time);
                writer.WriteString("name", ev.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, float[] values, string valueType)
    {
        writer.WritePropertyName("value");
        if (valueType == AnimValueType.Bool)
        {
            writer.WriteBooleanValue(values.Length > 0 && values[0] != 0f);
            return;
        }
        if (valueType == AnimValueType.Float)
        {
            writer.WriteNumberValue(values.Length > 0 ? values[0] : 0f);
            return;
        }
        // vec2 / vec3 / color → 配列
        writer.WriteStartArray();
        foreach (var v in values) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }

    private static void WriteFloatArray(Utf8JsonWriter writer, string propName, float[] values)
    {
        writer.WriteStartArray(propName);
        foreach (var v in values) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }

    // ── 数値パース補助（InvariantCulture 固定）───────────────────

    /// <summary>UI 入力文字列を float へパースする（InvariantCulture 固定）。失敗時は fallback を返す。</summary>
    public static float ParseFloatOr(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
