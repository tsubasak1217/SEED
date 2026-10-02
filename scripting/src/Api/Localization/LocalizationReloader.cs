using System;
using SEEDEditor.Scripting;

namespace SEED.Localization;

// ============================================================
//  LocalizationReloader.cs — 開発中だけ、多言語のデータファイルの書き換えを数秒ごとに拾うスクリプト（docs/localization.md §8）
//
//  【付け方】シーンのどこか 1 か所（起動のアクタなど）に ScriptComponent（型名 "SEED.Localization.LocalizationReloader"）を付ける。
//  Application.IsDebugAllowed のとき（エディタの Play・開発用のビルド）だけ、IntervalSeconds ごとに L10n.PollChanges() を呼ぶ
//  （index.json と読んだ言語の表の更新の印を比べ、変わっていれば読み直して L10n.Changed → LocalizedText などが入れ直す）。
//  OnStart でも 1 回調べる（エディタの常駐の Play は前の Play の表を持ち越すので、Play の前に書き換えた分をここで拾う）。
//  時間は実時間（Time.Scale の影響を受けない）。render_policy: on_demand で描画を止めている間は Update が来ないので調べない。
// ============================================================

/// <summary>開発中だけ、多言語のデータファイルの書き換えを数秒ごとに拾う。</summary>
public sealed class LocalizationReloader : SEEDScript
{
    /// <summary>調べる間隔の既定（秒）。</summary>
    public const float DefaultIntervalSeconds = 1f;
    /// <summary>調べる間隔の下限（秒。ファイルの更新の印を読みすぎないように）。</summary>
    public const float MinIntervalSeconds = 0.25f;
    /// <summary>調べる間隔の上限（秒）。</summary>
    public const float MaxIntervalSeconds = 60f;

    /// <summary>調べる間隔（秒。実時間）。</summary>
    [SerializeField(Label = "間隔（秒）", Tooltip = "データファイルの書き換えを調べる間隔（実時間）。開発中だけ動く")]
    [Range(MinIntervalSeconds, MaxIntervalSeconds)]
    public float IntervalSeconds = DefaultIntervalSeconds;

    /// <summary>前に調べてから過ぎた実時間（秒）。</summary>
    private float _elapsed;

    /// <summary>開始: 開発中なら 1 回調べる。</summary>
    public override void OnStart()
    {
        if (!Application.IsDebugAllowed) return;
        L10n.PollChanges();
    }

    /// <summary>毎フレーム: 開発中なら間隔ごとに調べる。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!Application.IsDebugAllowed) return;
        _elapsed += ctx.UnscaledDeltaTime;
        if (_elapsed < Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds)) return;
        _elapsed = 0f;
        L10n.PollChanges();
    }
}
