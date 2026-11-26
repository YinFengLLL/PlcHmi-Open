using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 扫码枪数据帧切分（参考 SmartScanner 的 <c>FrameParser</c>）。
///
/// <para>扫码枪可配 CR / CRLF / LF 三种结尾，现场没人会去翻说明书确认是哪一种：
/// 三种都认，未收完的半截留在缓冲里等下一块数据。</para>
/// </summary>
public static class 扫码帧解析器
{
    /// <summary>把新到的字符块追加进 <paramref name="缓冲"/>，切出所有完整帧（不含结尾符）。</summary>
    public static IReadOnlyList<string> 切分(string 块, StringBuilder 缓冲)
    {
        ArgumentNullException.ThrowIfNull(缓冲);

        if (!string.IsNullOrEmpty(块))
        {
            缓冲.Append(块);
        }

        var 帧集合 = new List<string>();

        while (true)
        {
            var 待处理 = 缓冲.ToString();
            var 换行 = 待处理.IndexOf('\n');
            var 回车 = 待处理.IndexOf('\r');

            if (换行 < 0 && 回车 < 0)
            {
                break;   // 还没收到行结束符：等下一块
            }

            var 位置 = 换行 >= 0 && 回车 >= 0 ? Math.Min(换行, 回车) : Math.Max(换行, 回车);
            var 行 = 待处理[..位置];
            缓冲.Remove(0, 位置 + 1);

            // CRLF：CR 已被消费，紧跟的 LF 一并吃掉，避免产生空帧
            if (待处理[位置] == '\r' && 缓冲.Length > 0 && 缓冲[0] == '\n')
            {
                缓冲.Remove(0, 1);
            }

            if (行.Length > 0)
            {
                帧集合.Add(行);
            }
        }

        return 帧集合;
    }
}

/// <summary>串口扫码枪（USB 转 COM）。</summary>
public sealed class 串口扫码传输 : 扫码枪传输接口
{
    private readonly StringBuilder _缓冲 = new();
    private SerialPort? _端口;

    /// <inheritdoc />
    public bool 已打开 => _端口 is { IsOpen: true };

    /// <inheritdoc />
    public event EventHandler<string>? 条码已接收;

    /// <inheritdoc />
    public event EventHandler<bool>? 连接状态已变更;

    /// <inheritdoc />
    public bool 打开(扫码枪配置 配置, out string? 错误)
    {
        ArgumentNullException.ThrowIfNull(配置);

        错误 = null;
        关闭();

        try
        {
            var 端口 = new SerialPort(
                配置.串口号.Trim(),
                配置.波特率,
                校验位(配置.校验),
                配置.数据位,
                停止位(配置.停止位))
            {
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };

            端口.DataReceived += 端口_数据到达;
            端口.Open();

            _端口 = 端口;
            连接状态已变更?.Invoke(this, true);
            return true;
        }
        catch (UnauthorizedAccessException 异常)
        {
            错误 = $"串口 {配置.串口号} 被占用或无权限：{异常.Message}";
            return false;
        }
        catch (IOException 异常)
        {
            错误 = $"串口 {配置.串口号} 打开失败：{异常.Message}";
            return false;
        }
        catch (ArgumentException 异常)
        {
            错误 = $"串口参数无效（{配置.串口号}）：{异常.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public void 关闭()
    {
        var 端口 = _端口;
        _端口 = null;

        if (端口 is null)
        {
            return;
        }

        try
        {
            端口.DataReceived -= 端口_数据到达;
            端口.Close();
            端口.Dispose();
        }
        catch (IOException)
        {
            // 端口已被拔掉：释放即可
        }
        catch (InvalidOperationException)
        {
            // 已关闭：释放即可
        }

        连接状态已变更?.Invoke(this, false);
    }

    /// <inheritdoc />
    public bool 发送指令(string 指令)
    {
        if (_端口 is not { IsOpen: true } 端口 || string.IsNullOrEmpty(指令))
        {
            return false;
        }

        try
        {
            端口.Write(指令);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => 关闭();

    private void 端口_数据到达(object 发送者, SerialDataReceivedEventArgs 事件参数)
    {
        var 端口 = _端口;

        if (端口 is null)
        {
            return;
        }

        string 块;

        try
        {
            块 = 端口.ReadExisting();
        }
        catch (IOException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var 帧 in 扫码帧解析器.切分(块, _缓冲))
        {
            条码已接收?.Invoke(this, 帧);
        }
    }

    private static Parity 校验位(string 值) => 值?.Trim() switch
    {
        "奇" => Parity.Odd,
        "偶" => Parity.Even,
        _ => Parity.None,
    };

    private static StopBits 停止位(string 值) => 值?.Trim() switch
    {
        "2" => StopBits.Two,
        "1.5" => StopBits.OnePointFive,
        _ => StopBits.One,
    };
}

/// <summary>TCP/IP 扫码枪（上位机作客户端，扫码枪侧配成 Server）。</summary>
public sealed class Tcp扫码传输 : 扫码枪传输接口
{
    private readonly StringBuilder _缓冲 = new();
    private TcpClient? _客户端;
    private NetworkStream? _流;
    private CancellationTokenSource? _取消;

    /// <inheritdoc />
    public bool 已打开 => _流 is not null;

    /// <inheritdoc />
    public event EventHandler<string>? 条码已接收;

    /// <inheritdoc />
    public event EventHandler<bool>? 连接状态已变更;

    /// <inheritdoc />
    public bool 打开(扫码枪配置 配置, out string? 错误)
    {
        ArgumentNullException.ThrowIfNull(配置);

        错误 = null;
        关闭();

        var 客户端 = new TcpClient();

        try
        {
            // 扫码枪没上电时 Connect 会挂很久：3 秒拿不到就连不上，别让启动流程等它
            var 任务 = 客户端.ConnectAsync(配置.网口地址.Trim(), 配置.网口端口);

            if (!任务.Wait(TimeSpan.FromSeconds(3)))
            {
                客户端.Dispose();
                错误 = $"连接扫码枪 {配置.网口地址}:{配置.网口端口} 超时。";
                return false;
            }

            _客户端 = 客户端;
            _流 = 客户端.GetStream();
            _取消 = new CancellationTokenSource();

            var 令牌 = _取消.Token;
            _ = Task.Run(() => 读循环(_流, 令牌), 令牌);

            连接状态已变更?.Invoke(this, true);
            return true;
        }
        catch (AggregateException 异常)
        {
            客户端.Dispose();
            错误 = $"连接扫码枪 {配置.网口地址}:{配置.网口端口} 失败：{异常.InnerException?.Message ?? 异常.Message}";
            return false;
        }
        catch (SocketException 异常)
        {
            客户端.Dispose();
            错误 = $"连接扫码枪 {配置.网口地址}:{配置.网口端口} 失败：{异常.Message}";
            return false;
        }
        catch (ArgumentException 异常)
        {
            客户端.Dispose();
            错误 = $"扫码枪网络参数无效：{异常.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public void 关闭()
    {
        _取消?.Cancel();
        _取消?.Dispose();
        _取消 = null;

        try
        {
            _流?.Dispose();
        }
        catch (IOException)
        {
            // 断开时释放即可
        }

        _流 = null;

        try
        {
            _客户端?.Dispose();
        }
        catch (SocketException)
        {
            // 断开时释放即可
        }

        _客户端 = null;
    }

    /// <inheritdoc />
    public bool 发送指令(string 指令)
    {
        var 流 = _流;

        if (流 is null || string.IsNullOrEmpty(指令))
        {
            return false;
        }

        try
        {
            var 字节 = Encoding.ASCII.GetBytes(指令);
            流.Write(字节, 0, 字节.Length);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => 关闭();

    private void 读循环(NetworkStream 流, CancellationToken 取消)
    {
        var 缓冲 = new byte[4096];
        var 主动关闭 = false;

        try
        {
            while (!取消.IsCancellationRequested)
            {
                var 已读 = 流.Read(缓冲, 0, 缓冲.Length);

                if (已读 <= 0)
                {
                    break;   // 对端关闭
                }

                var 块 = Encoding.ASCII.GetString(缓冲, 0, 已读);

                foreach (var 帧 in 扫码帧解析器.切分(块, _缓冲))
                {
                    条码已接收?.Invoke(this, 帧);
                }
            }
        }
        catch (IOException)
        {
            // 网络异常（对端强制断开）按断线处理
        }
        catch (ObjectDisposedException)
        {
            主动关闭 = 取消.IsCancellationRequested;
        }
        catch (SocketException)
        {
            // 同上
        }
        finally
        {
            if (!主动关闭)
            {
                _流 = null;
                连接状态已变更?.Invoke(this, false);
            }
        }
    }
}
