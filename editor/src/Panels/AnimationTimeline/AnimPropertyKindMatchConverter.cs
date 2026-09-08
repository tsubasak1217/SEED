// ============================================================
//  AnimPropertyKindMatchConverter.cs — トラック追加ドロップダウンの
//  「対象アクタの種別と合っているか」判定コンバータ
//
//  【解決したい問題】
//  トラック追加ドロップダウン（CmbNewTrackProperty）は component/property の
//  全組み合わせを常に一覧表示するため、2D アクタを対象にしているのに
//  3D 用の「Transform / 位置」を選べてしまい、その状態で I キーを押すと
//  KindMismatch エラーになる（実際に起きた不具合）。
//
//  デフォルト選択はキー対象の種別へ自動で合わせる（AnimationTimelinePanel 側）が、
//  それだけでは「他の項目も選べてしまう」ことは変わらないため、対象種別と
//  食い違う項目はドロップダウン上でグレー表示にして視覚的に区別する
//  （選択自体は禁止しない＝actor_path で別アクタを明示的に狙う上級者の使い方を妨げない）。
//
//  ItemContainerStyle の DataTrigger から MultiBinding 経由で使う。
//  Binding 1: 各項目（AnimPropertyEntry）の Component
//  Binding 2: ComboBox.Tag（bool? = 対象アクタが 2D かどうか。未確定なら null）
// ============================================================

using System;
using System.Globalization;
using System.Windows.Data;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// (component, is2D?) から「その項目が対象アクタの種別と一致するか」を求める MultiValueConverter。
/// is2D が未確定（null）のときは判定できないため常に true（グレーアウトしない）を返す。
/// </summary>
internal sealed class AnimPropertyKindMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return true;
        if (values[0] is not string component) return true;
        if (values[1] is not bool is2D) return true; // Tag 未設定＝種別不明のうちは全項目を通常表示のまま

        var wantComponent = is2D ? AnimActorSnapshot.CanvasTransformComponent : AnimActorSnapshot.TransformComponent;

        // CanvasTransform / Transform 以外（Sprite の色など）は種別に無関係なので常に一致扱いにする
        var isTransformKind = component == AnimActorSnapshot.CanvasTransformComponent
                            || component == AnimActorSnapshot.TransformComponent;
        return !isTransformKind || component == wantComponent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
