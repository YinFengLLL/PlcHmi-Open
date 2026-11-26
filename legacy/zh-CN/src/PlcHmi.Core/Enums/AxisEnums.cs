namespace 上位机.核心.枚举;

/// <summary>
/// 轴标识。
/// <para><b>枚举值即轴序号</b>（X=0、Y=1、Z=2、R=3），用作四轴数组的下标与
/// 点表内的轴偏移计算。改动这些数值会直接改变 PLC 地址，务必谨慎。</para>
/// </summary>
public enum 轴标识
{
    /// <summary>X 轴</summary>
    X = 0,

    /// <summary>Y 轴</summary>
    Y = 1,

    /// <summary>Z 轴</summary>
    Z = 2,

    /// <summary>R 轴（旋转轴）</summary>
    R = 3,
}
