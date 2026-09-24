// =============================================================================
// PluginComponent.cs
// collectible ALC へ読み込まれる「ユーザースクリプト相当」のコンポーネント。
// SpikeLib.IPluginComponent を実装し、値型ジェネリクス（List<PluginVec>）も使う。
// =============================================================================
using System.Collections.Generic;
using SpikeLib;

namespace SpikePlugin;

/// <summary>プラグイン側のコンポーネント。Tick の結果で動作確認する。</summary>
public sealed class PluginComponent : IPluginComponent
{
    /// <summary>速度（ホストからリフレクションで書き換えられる）。</summary>
    public float Speed = 2.0f;

    /// <summary>加算値。</summary>
    public int Bias = 1;

    /// <summary>過去の入力（値型ジェネリクスを collectible アセンブリ内で使う確認）。</summary>
    private readonly List<PluginVec> _history = new();

    /// <inheritdoc />
    public string Name => nameof(PluginComponent);

    /// <summary>input * Speed + Bias + (履歴数 - 1) を返す。</summary>
    public int Tick(int input)
    {
        _history.Add(new PluginVec(input, Speed));
        return (int)(input * Speed) + Bias + _history.Count - 1;
    }
}

/// <summary>プラグイン内で定義する値型。</summary>
public readonly record struct PluginVec(float Input, float Speed);
