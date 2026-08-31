using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SEEDEditor;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// インスペクタの各パラメータ行に付ける「⟲ 既定値に戻す」と、
/// 「値域が確定している行は自動でスライダーにする」を **1 本の入口**へまとめた部分クラス。
///
/// 【なぜ 1 本にするか】
/// コンポーネントは 20 種以上・フィールドは数百ある。行ごとに
/// 「ここはスライダー」「ここは ⟲ 付き」と個別に書くと、必ず抜けと表記ゆれが出る。
/// そこで各 Build*SlotContent のローカル関数（AddFloatRow 等）が
/// <see cref="BuildResettableFloatRow"/> を呼ぶだけになるようにし、
/// 「スライダーにするか」は <see cref="ComponentFieldRanges"/>、
/// 「既定値は何か」は Rust の `Default`（RESET_COMPONENT_FIELD の受け側）に委ねる。
///
/// 【リセットの仕組み】
/// 押下で `RESET_COMPONENT_FIELD:{actor},{slot},{field_path}` を送る。ランタイムは
/// 対象スロットを JSON 化し、`field_path` が指す 1 フィールドだけを Rust の `Default`
/// 由来の既定値へ戻して再送信する（runtime/.../app/component_reset_ops.rs）。
/// Undo は field_edit.rs の共通機構が担うので、ここで履歴を積んではいけない。
///
/// 【field_path は serde 名】
/// `field_path` は **Rust の `*ComponentData` の serde フィールド名**（`/` 区切りで入れ子・
/// 配列添字も可）であり、`SET_*_FIELD` のキー名とは別体系である。
/// 例: ライトの内側角は SET キー `inner_angle` / serde 名 `inner_angle_deg`。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>⟲ 行のツールチップ文面（ラベルを埋め込む）。</summary>
    private const string FieldResetTooltipFormat = "「{0}」を既定値に戻す（Ctrl+Z で取り消せます）";

    // ── コンポーネント型名（値域表のキー前半）──────────────────
    // ACTOR_COMPONENTS の `type` と同じ綴り。マジックストリングを散らさないため定数化する。

    /// <summary>水ボリュームの型名。</summary>
    private const string WaterVolumeComponentType = "WaterVolumeComponent";
    /// <summary>水位リンクの型名。</summary>
    private const string WaterLinkComponentType = "WaterLinkComponent";
    /// <summary>ライトの型名。</summary>
    private const string LightComponentType = "LightComponent";
    /// <summary>オーディオの型名。</summary>
    private const string AudioComponentType = "AudioComponent";
    /// <summary>スカイボックスの型名。</summary>
    private const string SkyboxComponentType = "SkyboxComponent";
    /// <summary>パーティクルエミッタの型名。</summary>
    private const string ParticleEmitterComponentType = "ParticleEmitterComponent";
    /// <summary>インタラクション書き手の型名。</summary>
    private const string InteractionSourceComponentType = "InteractionSourceComponent";
    /// <summary>カバーエミッタ（地表カバー場への書き手。I3.1）の型名。</summary>
    private const string CoverEmitterComponentType = "CoverEmitterComponent";

    // ── 第2バッチのコンポーネント型名 ───────────────────────────

    /// <summary>カメラの型名。</summary>
    private const string CameraComponentType = "CameraComponent";
    /// <summary>スプライトの型名。</summary>
    private const string SpriteComponentType = "SpriteComponent";
    /// <summary>メッシュ変形スキニング 2D スプライトの型名。</summary>
    private const string SkinnedSpriteComponentType = "SkinnedSpriteComponent";
    /// <summary>キャンバスの型名。</summary>
    private const string CanvasComponentType = "CanvasComponent";
    /// <summary>3D コライダーの型名。</summary>
    private const string ColliderComponentType = "ColliderComponent";
    /// <summary>2D コライダーの型名。</summary>
    private const string Collider2dComponentType = "Collider2dComponent";
    /// <summary>ジョイントアタッチの型名。</summary>
    private const string JointAttachComponentType = "JointAttachComponent";
    /// <summary>アニメータの型名。</summary>
    private const string AnimatorComponentType = "AnimatorComponent";
    /// <summary>モデル（マテリアル上書きの親）の型名。</summary>
    private const string ModelComponentType = "ModelComponent";
    /// <summary>3D ポリライン（釣り糸・ロープ）の型名。</summary>
    private const string LineRendererComponentType = "LineRendererComponent";
    /// <summary>キャンバステキスト（HUD の数値・ラベル）の型名。</summary>
    private const string TextComponentType = "TextComponent";

    // ── 行ラベルの幅 ─────────────────────────────────────────────
    // 行ビルダごとに数値リテラルを散らすと、同じセクション内で行ごとにラベル位置が
    // ずれる（実際にパーティクルの「方向ランダム度」だけがずれていた）。
    // 幅は「セクション単位で 1 つの定数」に集約し、行ビルダは値を受け取るだけにする。

    /// <summary>
    /// インスペクタ標準の行ラベル幅（px）。
    /// ライト・水・カメラ・コライダー等、ほぼ全てのセクションがこの幅でそろえてある。
    /// </summary>
    internal const double InspectorRowLabelWidth = 90;

    /// <summary>
    /// パーティクルセクションの行ラベル幅（px）。
    /// 「放出方向(ローカル) XYZ」「回転速度(度/秒) min/max」のように
    /// ラベルが長い行が多いためこのセクションだけ広く取っている。
    /// 標準幅へ寄せるとラベルが切れるため、幅を統一するのではなく
    /// **セクション内でそろえる**（共通入口へこの値を渡す）方針とする。
    /// </summary>
    internal const double ParticleRowLabelWidth = 110;

    /// <summary>
    /// 既存の行要素の右端に「⟲ 既定値に戻す」ボタンを添えて返す。
    ///
    /// 値の入力欄を持たない行（色スウォッチ等）にも使えるよう、行の中身には一切触れない。
    /// </summary>
    /// <param name="row">元の行要素。</param>
    /// <param name="slotIdx">対象コンポーネントのスロット番号。</param>
    /// <param name="componentType">コンポーネント型名（例 "WaterVolumeComponent"）。ツールチップ生成には使わないが、呼び出し側の取り違え防止のため受け取る。</param>
    /// <param name="field">Rust の serde フィールドパス（`/` 区切り）。</param>
    /// <param name="label">行のラベル（ツールチップに出す）。</param>
    private UIElement WithFieldReset(
        UIElement row, int slotIdx, string componentType, string field, string label)
        => ResetButtonFactory.Wrap(
            row,
            string.Format(CultureInfo.CurrentCulture, FieldResetTooltipFormat, label),
            () =>
            {
                // 選択が外れている間は送らない（アクタ ID が確定していない）。
                if (_currentActorId < 0) return;
                _runtime?.SendToRuntime(
                    $"RESET_COMPONENT_FIELD:{_currentActorId},{slotIdx},{field}");
                // componentType は将来のログ・診断用に受け取っているだけで、
                // ワイヤ表現には現れない（対象はスロット番号で一意に決まるため）。
                _ = componentType;
            });

    /// <summary>
    /// 数値 1 個のパラメータ行を作る **共通入口**。
    ///
    /// ・<see cref="ComponentFieldRanges"/> に値域があれば範囲スライダー行、無ければ数値入力行
    /// ・<paramref name="field"/> が非 null なら右端に「⟲ 既定値に戻す」を添える
    ///   （null は「この行は値の調整ではなく構造の定義なのでリセット対象外」を意味する）
    ///
    /// 戻り値は **ボタンまで含んだ外側の要素**なので、種別による表示切替
    /// （Visibility の付け替え）はこの戻り値に対して行うこと。
    /// </summary>
    /// <param name="slotIdx">対象コンポーネントのスロット番号。</param>
    /// <param name="componentType">コンポーネント型名（値域表の引き当てに使う）。</param>
    /// <param name="label">行ラベル。</param>
    /// <param name="value">現在値。</param>
    /// <param name="field">
    /// Rust の serde フィールド名。値域表の引き当てと ⟲ の送信に使う。
    /// null を渡すと「値域引き当ても ⟲ も行わない（素の数値行）」になる。
    /// </param>
    /// <param name="format">数値行の表示書式（スライダー行では使わない）。</param>
    /// <param name="onCommit">確定時に呼ぶ処理（実際の SET_*_FIELD 送信は呼び出し側の責務）。</param>
    /// <param name="labelWidth">
    /// 行ラベルの幅（px）。既定は <see cref="InspectorRowLabelWidth"/>。
    /// ラベル幅が標準と異なるセクション（パーティクル）は、同セクションの他の行と
    /// ラベル位置がそろうよう自分のセクション幅を渡す。
    /// </param>
    private UIElement BuildResettableFloatRow(
        int slotIdx, string componentType, string label, float value,
        string? field, string format, Action<float> onCommit,
        double labelWidth = InspectorRowLabelWidth)
    {
        UIElement row;

        // 値域が確定している行だけスライダーにする（表に無ければ素の数値行）。
        if (field is not null && ComponentFieldRanges.TryGet(componentType, field, out var range))
        {
            row = BuildRangeSliderRow(label, value, range.Min, range.Max, onCommit, labelWidth);
        }
        else
        {
            // 数値入力行: Enter / フォーカス喪失 / ドラッグ操作で確定する
            //（各 Build*SlotContent が個別に書いていた配線をここへ集約した）。
            var numeric = BuildLabeledNumberRow(label, value, format, labelWidth);
            void Commit()
            {
                if (!float.TryParse(numeric.textBox.Text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v)) return;
                onCommit(v);
            }
            numeric.textBox.KeyDown   += (_, e) =>
            {
                if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
            };
            numeric.textBox.LostFocus += (_, _) => Commit();
            NumericDragBehavior.SetOnDrag(numeric.textBox, Commit);
            row = numeric.element;
        }

        return field is null ? row : WithFieldReset(row, slotIdx, componentType, field, label);
    }
}
