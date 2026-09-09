using System;
using SEEDEditor.Reload;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace AutoReloadPolicyTests;

/// <summary>
/// 自動再読込ポリシー（<see cref="AutoReloadPolicy"/>）の判定テスト。
///
/// <para>直した不具合:</para>
/// <para>
/// 編集しながら Play していると、ゲームが重くなり・最初からやり直しになり・
/// 一部が描画されなくなることがあった。原因は Play 中にスクリプトの
/// ホットリロード（およびシーンの自動再読込）が走り、全スクリプトインスタンスが
/// 作り直されて OnStart が再実行されること。OnStart で Instantiate している
/// 補助アクタが二重生成され、static シングルトンは古いハンドルを持ち続けていた。
/// </para>
/// <para>
/// ここでは「状態 × 種別 × 設定 → 適用 / 保留 / 破棄」の対応表を固定する。
/// 特に <b>シーンは Play 中に決して適用しない</b>（設定に関わらず）ことと、
/// <b>設定オフのときは保留すらしない</b>（＝Play 停止時に勝手に反映しない）ことを守る。
/// </para>
/// </summary>
public static class Program
{
    public static int Main()
    {
        var h = new TestHarness();

        // ── IsPlaying: Pause も「実行中」──────────────────────────
        // Pause はワールドが Play のまま生きているため、作り直すと同じ破壊が起きる。
        h.Add("IsPlaying: Edit は false", () =>
            Check.True(!AutoReloadPolicy.IsPlaying(PlaybackState.Edit), "Edit は実行中ではない"));
        h.Add("IsPlaying: Play は true", () =>
            Check.True(AutoReloadPolicy.IsPlaying(PlaybackState.Play), "Play は実行中"));
        h.Add("IsPlaying: Pause も true", () =>
            Check.True(AutoReloadPolicy.IsPlaying(PlaybackState.Pause), "Pause も実行中扱い"));

        // ── 設定オフ: 種別・状態によらず必ず Drop ────────────────
        // 「保留」にしてしまうと、オフにしているのに Play 停止時へ反映が漏れる。
        h.Add("設定オフ: スクリプト/Edit は Drop", () =>
            Check.Equal(AutoReloadDecision.Drop,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Edit,
                    autoReloadEnabled: false, applyScriptsDuringPlay: false),
                "設定オフでは何もしない"));
        h.Add("設定オフ: スクリプト/Play は Drop（即時反映設定がオンでも）", () =>
            Check.Equal(AutoReloadDecision.Drop,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Play,
                    autoReloadEnabled: false, applyScriptsDuringPlay: true),
                "自動再読込オフが最優先"));
        h.Add("設定オフ: シーン/Play は Drop", () =>
            Check.Equal(AutoReloadDecision.Drop,
                AutoReloadPolicy.Decide(AutoReloadKind.Scene, PlaybackState.Play,
                    autoReloadEnabled: false, applyScriptsDuringPlay: false),
                "設定オフでは保留もしない"));

        // ── Edit 中: 常に即時適用 ────────────────────────────────
        h.Add("Edit: スクリプトは ApplyNow", () =>
            Check.Equal(AutoReloadDecision.ApplyNow,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Edit,
                    autoReloadEnabled: true, applyScriptsDuringPlay: false),
                "Edit 中は従来どおり即反映"));
        h.Add("Edit: シーンは ApplyNow", () =>
            Check.Equal(AutoReloadDecision.ApplyNow,
                AutoReloadPolicy.Decide(AutoReloadKind.Scene, PlaybackState.Edit,
                    autoReloadEnabled: true, applyScriptsDuringPlay: false),
                "Edit 中は従来どおり即反映"));

        // ── Play/Pause 中のスクリプト: 既定は保留、設定オンなら即時 ──
        h.Add("Play: スクリプトは既定で Defer", () =>
            Check.Equal(AutoReloadDecision.Defer,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Play,
                    autoReloadEnabled: true, applyScriptsDuringPlay: false),
                "既定では Play 中に再生成しない"));
        h.Add("Pause: スクリプトは既定で Defer", () =>
            Check.Equal(AutoReloadDecision.Defer,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Pause,
                    autoReloadEnabled: true, applyScriptsDuringPlay: false),
                "Pause もワールドは生きている"));
        h.Add("Play: スクリプトは設定オンなら ApplyNow", () =>
            Check.Equal(AutoReloadDecision.ApplyNow,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Play,
                    autoReloadEnabled: true, applyScriptsDuringPlay: true),
                "明示的にオンにした人だけ Play 中も即時反映"));
        h.Add("Pause: スクリプトは設定オンなら ApplyNow", () =>
            Check.Equal(AutoReloadDecision.ApplyNow,
                AutoReloadPolicy.Decide(AutoReloadKind.Script, PlaybackState.Pause,
                    autoReloadEnabled: true, applyScriptsDuringPlay: true),
                "Pause でも設定に従う"));

        // ── Play/Pause 中のシーン: 設定に関わらず必ず Defer ──────
        // ここが崩れると Play 中にワールドごと作り直され、進行が完全に失われる。
        h.Add("Play: シーンは Defer", () =>
            Check.Equal(AutoReloadDecision.Defer,
                AutoReloadPolicy.Decide(AutoReloadKind.Scene, PlaybackState.Play,
                    autoReloadEnabled: true, applyScriptsDuringPlay: false),
                "Play 中のシーン再読込は禁止"));
        h.Add("Play: シーンは即時反映設定がオンでも Defer", () =>
            Check.Equal(AutoReloadDecision.Defer,
                AutoReloadPolicy.Decide(AutoReloadKind.Scene, PlaybackState.Play,
                    autoReloadEnabled: true, applyScriptsDuringPlay: true),
                "スクリプト用の設定はシーンへ波及しない"));
        h.Add("Pause: シーンは Defer", () =>
            Check.Equal(AutoReloadDecision.Defer,
                AutoReloadPolicy.Decide(AutoReloadKind.Scene, PlaybackState.Pause,
                    autoReloadEnabled: true, applyScriptsDuringPlay: true),
                "Pause 中も禁止"));

        // ── Edit 復帰時の保留分の消化 ────────────────────────────
        h.Add("復帰: 保留があり設定オンなら ApplyNow", () =>
            Check.Equal(AutoReloadDecision.ApplyNow,
                AutoReloadPolicy.DecideOnReturnToEdit(hasPending: true, autoReloadEnabled: true),
                "Play 停止直後に反映する"));
        h.Add("復帰: 保留が無ければ Drop", () =>
            Check.Equal(AutoReloadDecision.Drop,
                AutoReloadPolicy.DecideOnReturnToEdit(hasPending: false, autoReloadEnabled: true),
                "何も検出していないなら再読込しない"));
        h.Add("復帰: Play 中に設定をオフにしたら Drop", () =>
            Check.Equal(AutoReloadDecision.Drop,
                AutoReloadPolicy.DecideOnReturnToEdit(hasPending: true, autoReloadEnabled: false),
                "オフにした意思を復帰時にも尊重する"));

        return h.Run();
    }
}
