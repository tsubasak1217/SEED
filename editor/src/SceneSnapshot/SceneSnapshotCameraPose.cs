// ============================================================
//  SceneSnapshotCameraPose.cs — シーンの写しに付いてくる「メインカメラの位置と向き」（純粋な値）
//
//  【どこから来るか】
//    ランタイムの SNAPSHOT_SCENE の応答 SNAPSHOT_DONE の欄 cam=<px>,<py>,<pz>,<ex>,<ey>,<ez>
//    （書式の正典はランタイムの runtime/src/engine/core/app_base/scene_snapshot/wire.rs）。
//    位置はワールド座標、向きは Transform と同じ YXZ オイラー角（度。R = Ry * Rx * Rz）。
//    メインカメラが無いシーンでは cam=none（このクラスでは null で表す）。
//
//  【どこへ行くか】
//    エディタの編集用ランタイムのデバッグカメラ。既存のカメラの命令 CAM_TRANSFORM:{px},{py},{pz},{ex},{ey},{ez}
//    （ランタイムの ipc.rs・MainWindow.Camera.cs の SendCameraTransform と同じ書式・同じ角度の規約）で当てる。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.SceneSnapshot;

/// <summary>メインカメラの位置（ワールド）と向き（YXZ オイラー角・度）。</summary>
/// <param name="X">位置 X。</param>
/// <param name="Y">位置 Y。</param>
/// <param name="Z">位置 Z。</param>
/// <param name="EulerX">X 軸回り（ピッチ）の角度（度）。</param>
/// <param name="EulerY">Y 軸回り（ヨー）の角度（度）。</param>
/// <param name="EulerZ">Z 軸回り（ロール）の角度（度）。</param>
public sealed record SceneSnapshotCameraPose(float X, float Y, float Z, float EulerX, float EulerY, float EulerZ)
{
    /// <summary>欄の数（位置 3 ＋ 角度 3）。</summary>
    public const int FieldCount = 6;

    /// <summary>数値の区切り（ランタイムの応答・CAM_TRANSFORM と同じ）。</summary>
    public const char ValueSeparator = ',';

    /// <summary>メインカメラが無いことを表す値（cam=none）。</summary>
    public const string NoneText = "none";

    /// <summary>既存のカメラの命令の接頭辞（デバッグカメラの位置と向きを当てる。ランタイムの ipc.rs の CAM_TRANSFORM:）。</summary>
    public const string CameraTransformPrefix = "CAM_TRANSFORM:";

    /// <summary>Output パネル向けの表示の書式（{0}〜{2}=位置〈小数 2 桁〉・{3}〜{5}=角度〈小数 1 桁〉）。</summary>
    private const string DescribeFormat = "位置 ({0:F2}, {1:F2}, {2:F2})・向き ({3:F1}°, {4:F1}°, {5:F1}°)";

    /// <summary>
    /// 「px,py,pz,ex,ey,ez」を読む（純粋な処理）。none・空・欄の数の違い・数でない値・有限でない値は null。
    /// </summary>
    /// <param name="text">欄の中身（cam= の後ろ）。</param>
    /// <returns>読めた姿勢（無い・読めなければ null）。</returns>
    public static SceneSnapshotCameraPose? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (string.Equals(trimmed, NoneText, StringComparison.OrdinalIgnoreCase)) return null;

        var parts = trimmed.Split(ValueSeparator);
        if (parts.Length != FieldCount) return null;
        var values = new float[FieldCount];
        for (var i = 0; i < FieldCount; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !float.IsFinite(values[i]))
            {
                return null;
            }
        }
        return new SceneSnapshotCameraPose(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    /// <summary>
    /// デバッグカメラへ当てる既存の命令（CAM_TRANSFORM:{px},{py},{pz},{ex},{ey},{ez}）を作る。
    /// </summary>
    /// <returns>1 行の命令。</returns>
    public string ToCameraTransformCommand()
    {
        var ci = CultureInfo.InvariantCulture;
        return CameraTransformPrefix + string.Join(ValueSeparator,
            X.ToString(ci), Y.ToString(ci), Z.ToString(ci),
            EulerX.ToString(ci), EulerY.ToString(ci), EulerZ.ToString(ci));
    }

    /// <summary>Output パネル向けの短い表示。</summary>
    /// <returns>例「位置 (1.00, 2.00, -3.00)・向き (10.0°, 20.0°, 0.0°)」。</returns>
    public string Describe() =>
        string.Format(CultureInfo.InvariantCulture, DescribeFormat, X, Y, Z, EulerX, EulerY, EulerZ);
}
