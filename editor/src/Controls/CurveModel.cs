// ============================================================
//  CurveModel.cs — パーティクル用パラメータカーブのデータモデル
//
//  Rust 側 `ParamCurve` / `CurveChannel` / `CurveKey`
//  （runtime/src/engine/components/particle_emitter_component.rs）と
//  serde JSON 形を 1:1 で対応させた C# 側モデル。
//
//  JSON 形（Rust serde と完全一致させる契約）:
//    ParamCurve   … {"channels":[ CurveChannel, ... ]}
//    CurveChannel … {"keys":[ CurveKey, ... ]}
//    CurveKey     … {"t":<f32>,"v":<f32>}
//
//  ・チャンネル数は 1..4（速度/回転=1、スケール=3(xyz)、色=4(HSVA)）。
//  ・各チャンネルのキーは t 昇順、t∈[0,1]、線形補間。
//
//  このファイルはデータ＋純粋計算のみを持ち、UI へは依存しない
//  （単一責任原則。UI は CurveEditorControl 側）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Controls;

/// <summary>
/// カーブの 1 キー（t=正規化寿命 0..1、v=値）。線形補間の制御点。
/// </summary>
public sealed class CurveKey
{
    /// <summary>正規化寿命（0..1）。チャンネル内で昇順であること。</summary>
    [JsonPropertyName("t")] public float T { get; set; }

    /// <summary>値。</summary>
    [JsonPropertyName("v")] public float V { get; set; }

    public CurveKey() { }
    public CurveKey(float t, float v) { T = t; V = v; }
}

/// <summary>
/// カーブの 1 チャンネル（1 成分の時系列。キーは t 昇順）。
/// </summary>
public sealed class CurveChannel
{
    /// <summary>キー列（t 昇順）。</summary>
    [JsonPropertyName("keys")] public List<CurveKey> Keys { get; set; } = new();

    /// <summary>
    /// 正規化寿命 t での値を線形補間で評価する（Rust CurveChannel::eval と同挙動）。
    /// キー無し→0、端点はクランプ。
    /// </summary>
    public float Eval(float t)
    {
        var keys = Keys;
        if (keys.Count == 0) return 0f;
        if (t <= keys[0].T) return keys[0].V;
        int last = keys.Count - 1;
        if (t >= keys[last].T) return keys[last].V;
        for (int i = 0; i < last; i++)
        {
            var k0 = keys[i];
            var k1 = keys[i + 1];
            if (t >= k0.T && t <= k1.T)
            {
                float span = k1.T - k0.T;
                if (span <= float.Epsilon) return k0.V;
                float f = (t - k0.T) / span;
                return k0.V + (k1.V - k0.V) * f;
            }
        }
        return keys[last].V;
    }

    /// <summary>定数チャンネル（両端 [0,v]..[1,v]）を作る。</summary>
    public static CurveChannel Constant(float v) => new()
    {
        Keys = new List<CurveKey> { new(0f, v), new(1f, v) },
    };
}

/// <summary>
/// 複数チャンネル（1..4 本）のパラメータカーブ。Rust ParamCurve に対応。
/// </summary>
public sealed class ParamCurve
{
    /// <summary>チャンネル列（1..4 本）。</summary>
    [JsonPropertyName("channels")] public List<CurveChannel> Channels { get; set; } = new();

    // ── System.Text.Json 設定 ───────────────────────────────
    // WriteIndented=false でコンパクト JSON（IPC ペイロードを小さく保つ）。
    // Rust serde は数値をそのまま読むため、既定のシリアライザ設定で形が一致する。
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>この ParamCurve を Rust serde 互換 JSON にシリアライズする。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

    /// <summary>ParamCurve 配列（random_color_curves 用）を JSON にシリアライズする。</summary>
    public static string ToJsonArray(IReadOnlyList<ParamCurve> curves)
        => JsonSerializer.Serialize(curves, s_jsonOptions);

    /// <summary>
    /// Rust から届いた JSON（オブジェクト）を ParamCurve へパースする。
    /// 失敗・欠落時は null を返す（呼び出し側でフォールバックする）。
    /// </summary>
    public static ParamCurve? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<ParamCurve>(json, s_jsonOptions);
            // channels が無い / 空でも「壊れていない空カーブ」として扱わず null 化し、
            // 呼び出し側の既定生成に委ねる（UI が最低 1 チャンネルを要求するため）。
            if (c == null || c.Channels.Count == 0) return null;
            return c;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rust から届いた JSON 配列（random_color_curves 用）を ParamCurve リストへパースする。
    /// 失敗・欠落時は空リストを返す。
    /// </summary>
    public static List<ParamCurve> ListFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<ParamCurve>();
        try
        {
            var list = JsonSerializer.Deserialize<List<ParamCurve>>(json, s_jsonOptions);
            return list ?? new List<ParamCurve>();
        }
        catch (JsonException)
        {
            return new List<ParamCurve>();
        }
    }

    /// <summary>ディープコピーを返す（UI 編集用に受信データと分離する）。</summary>
    public ParamCurve Clone()
    {
        var copy = new ParamCurve();
        foreach (var ch in Channels)
        {
            var nch = new CurveChannel();
            foreach (var k in ch.Keys) nch.Keys.Add(new CurveKey(k.T, k.V));
            copy.Channels.Add(nch);
        }
        return copy;
    }

    /// <summary>
    /// 指定チャンネル数の既定カーブ（各チャンネル定数 1.0）を作る。
    /// パース失敗時のフォールバックに使う。
    /// </summary>
    public static ParamCurve DefaultWithChannels(int channelCount)
    {
        var c = new ParamCurve();
        int n = Math.Clamp(channelCount, 1, 4);
        for (int i = 0; i < n; i++) c.Channels.Add(CurveChannel.Constant(1f));
        return c;
    }
}

/// <summary>
/// 色空間ヘルパー。HSVA カーブ（色カーブ）のグラデーションプレビュー描画に使う。
/// </summary>
public static class ColorSpace
{
    /// <summary>
    /// HSV(0..1) → RGB(0..1) 変換。Rust 側 rgb_to_hsv の逆変換に相当し、
    /// H は 0..1 正規化（度ではない）。範囲外の H はラップ、S/V はクランプする。
    /// </summary>
    public static (float r, float g, float b) HsvToRgb(float h, float s, float v)
    {
        // H を [0,1) にラップ、S/V を [0,1] にクランプ。
        h -= MathF.Floor(h);
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        // 6 セクタ法（度なら 60° 刻み。ここでは 0..1 を 6 分割）。
        const float sectors = 6f;
        float hh = h * sectors;
        int sector = (int)MathF.Floor(hh) % 6;
        if (sector < 0) sector += 6;
        float f = hh - MathF.Floor(hh);
        float p = v * (1f - s);
        float q = v * (1f - s * f);
        float t = v * (1f - s * (1f - f));

        return sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
