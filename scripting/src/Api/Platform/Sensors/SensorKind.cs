namespace SEED.Platform;

/// <summary>センサーの種類（<see cref="Sensors"/>。W1-8）。</summary>
public enum SensorKind
{
    /// <summary>
    /// "linear_acceleration": 重力を除いた加速度（m/s²。端末の座標系）。Android の TYPE_LINEAR_ACCELERATION、無い端末は加速度から
    /// 低域通過で重力を引いた値（<see cref="Sensors.GetSource"/> が <see cref="Sensors.SourceAccelerometerLowPass"/>）。
    /// 端末を振る・揺らすの判定に使う（静置でほぼ 0）。
    /// </summary>
    LinearAcceleration,
}
