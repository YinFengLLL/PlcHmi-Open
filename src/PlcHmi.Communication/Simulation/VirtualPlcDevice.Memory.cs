namespace PlcHmi.Communication.Simulation;

/// <summary>
/// <see cref="VirtualPlcDevice"/> 的「内存映像初始化」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>主文件（节拍状态机）已接近 400 行规模门禁基线，
/// 软元件布局常量 + 初始内存值是独立的"数据"职责，与状态机逻辑分开后各自可读。</para>
/// </summary>
public sealed partial class VirtualPlcDevice
{
    private void InitializeMemory()
    {
        _memory.RunAloneOccupy(() =>
        {
            // 初始状态：空闲（状态字 0）、安全门常闭触点闭合、料仓有料
            _memory.UnsafeSetHoldingRegister(registerD6RunState, stateIdle);

            // X2 安全门为常闭触点：门关好时触点闭合 → 原始位为 1
            // （X 是物理输入，写离散输入区 FC02；Y 是输出写线圈区 FC01——与真机口径一致）
            _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 2, true);

            // X7 急停按钮为常闭触点：未按下时触点闭合 → 原始位为 1
            _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 7, true);

            _memory.UnsafeSetHoldingRegister(registerD200AxisTarget, 120);

            // 六轴当前位置：Float32 全 0
            SetFloat(registerAxisXPosition, 0f);
            SetFloat(registerAxisYPosition, 0f);
            SetFloat(registerAxisZPosition, 0f);
            SetFloat(registerAxisZZPosition, 0f);
            SetFloat(registerAxisGlueZPosition, 0f);
            SetFloat(registerAxisGlueXPosition, 0f);
            SetFloat(registerAxisRPosition, 0f);

            SetUInt32(registerD1100WeekPeriodTime, 0);
            SetUInt32(registerD1102OutputCount, 0);
            SetUInt32(registerD1104TargetOutput, 5000);
            SetUInt32(registerD1106GoodCount, 0);
            SetUInt32(registerD1108DefectiveCount, 0);

            // 示教点表区 D2500–D3299 保持全 0（未示教），让上位机读取时能识别为空点

            // 参数区：写入一组现场生效值（与 config/parameters.json 的当前参数一致），
            // 每项 Float32 占 2 字、字序 CDAB（低字在前）。
            SetFloat(registerXPositioningSpeed, 80.00f);      // D1400 锁附 X 轴定位速度
            SetFloat(registerYPositioningSpeed, 100.00f);     // D1402 锁附 Y 轴定位速度
            SetFloat(registerZPositioningSpeed, 50.00f);      // D1404 锁附 Z 轴定位速度
            SetFloat(registerZZPositioningSpeed, 10.00f);     // D1406 锁附 ZZ 轴定位速度
            SetFloat(registerGlueZPositioningSpeed, 50.00f);  // D1408 点胶 Z 轴定位速度
            SetFloat(registerGlueXPositioningSpeed, 60.00f);  // D1410 点胶 X 轴定位速度

            // 机种控制区
            _memory.UnsafeSetHoldingRegister(registerRecipeNumber, 1);
            _memory.UnsafeSetHoldingRegister(registerRecipeCount, 20);
            _memory.UnsafeSetHoldingRegister(registerTeachIndex, 0);
            _memory.UnsafeSetHoldingRegister(registerTeachFace, 0);

            // 面启用标志：全部停用（与需求一致 —— 默认不开启，开启的面才写入）
            for (var face = 0; face < registerFaceCount; face++)
            {
                _memory.UnsafeSetHoldingRegister(registerFaceEnableBaseAddress + face, 0);
            }
        });
    }
}
