using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 扫码枪数据帧切分（参考 SmartScanner 的 <c>FrameParser</c>）。
///
/// <para>扫码枪可配 CR / CRLF / LF 三种结尾，现场没人会去翻说明书确认是哪一种：
/// 三种都认，未收完的半截留在缓冲里等下一块数据。</para>
/// </summary>
public static class ScanFrameParser
{
    /// <summary>把新到的字符块追加进 <paramref name="buffer"/>，切出所有完整帧（不含结尾符）。</summary>
    public static IReadOnlyList<string> Slice(string block, StringBuilder buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!string.IsNullOrEmpty(block))
        {
            buffer.Append(block);
        }

        var frameCollection = new List<string>();

        while (true)
        {
            var PendingProcess = buffer.ToString();
            var newLine = PendingProcess.IndexOf('\n');
            var carriageReturn = PendingProcess.IndexOf('\r');

            if (newLine < 0 && carriageReturn < 0)
            {
                break;   // 还没收到行结束符：等下一块
            }

            var Position = newLine >= 0 && carriageReturn >= 0 ? Math.Min(newLine, carriageReturn) : Math.Max(newLine, carriageReturn);
            var Row = PendingProcess[..Position];
            buffer.Remove(0, Position + 1);

            // CRLF：CR 已被消费，紧跟的 LF 一并吃掉，避免产生空帧
            if (PendingProcess[Position] == '\r' && buffer.Length > 0 && buffer[0] == '\n')
            {
                buffer.Remove(0, 1);
            }

            if (Row.Length > 0)
            {
                frameCollection.Add(Row);
            }
        }

        return frameCollection;
    }
}

/// <summary>串口扫码枪（USB 转 COM）。</summary>
public sealed class SerialPortScanTransport : IScannerTransport
{
    private readonly StringBuilder _buffer = new();
    private SerialPort? _port;

    /// <inheritdoc />
    public bool Opened => _port is { IsOpen: true };

    /// <inheritdoc />
    public event EventHandler<string>? barcodeReceived;

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public bool Open(ScannerConfig Config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(Config);

        error = null;
        Close();

        try
        {
            var Port = new SerialPort(
                Config.SerialPortName.Trim(),
                Config.BaudRate,
                ParseParity(Config.Validate),
                Config.DataBit,
                ParseStopBits(Config.StopBits))
            {
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };

            Port.DataReceived += Port_DataToReach;
            Port.Open();

            _port = Port;
            ConnectionStateChanged?.Invoke(this, true);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            error = $"串口 {Config.SerialPortName} 被占用或无权限：{exception.Message}";
            return false;
        }
        catch (IOException exception)
        {
            error = $"串口 {Config.SerialPortName} 打开失败：{exception.Message}";
            return false;
        }
        catch (ArgumentException exception)
        {
            error = $"串口参数无效（{Config.SerialPortName}）：{exception.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        var Port = _port;
        _port = null;

        if (Port is null)
        {
            return;
        }

        try
        {
            Port.DataReceived -= Port_DataToReach;
            Port.Close();
            Port.Dispose();
        }
        catch (IOException)
        {
            // 端口已被拔掉：释放即可
        }
        catch (InvalidOperationException)
        {
            // 已关闭：释放即可
        }

        ConnectionStateChanged?.Invoke(this, false);
    }

    /// <inheritdoc />
    public bool SenderCommand(string command)
    {
        if (_port is not { IsOpen: true } Port || string.IsNullOrEmpty(command))
        {
            return false;
        }

        try
        {
            Port.Write(command);
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
    public void Dispose() => Close();

    private void Port_DataToReach(object sender, SerialDataReceivedEventArgs eventArgs)
    {
        var Port = _port;

        if (Port is null)
        {
            return;
        }

        string block;

        try
        {
            block = Port.ReadExisting();
        }
        catch (IOException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var frame in ScanFrameParser.Slice(block, _buffer))
        {
            barcodeReceived?.Invoke(this, frame);
        }
    }

    private static Parity ParseParity(string Value) => Value?.Trim() switch
    {
        "奇" => Parity.Odd,
        "偶" => Parity.Even,
        _ => Parity.None,
    };

    private static StopBits ParseStopBits(string Value) => Value?.Trim() switch
    {
        "2" => StopBits.Two,
        "1.5" => StopBits.OnePointFive,
        _ => StopBits.One,
    };
}

/// <summary>TCP/IP 扫码枪（上位机作客户端，扫码枪侧配成 Server）。</summary>
public sealed class TcpScanTransport : IScannerTransport
{
    private readonly StringBuilder _buffer = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cancel;

    /// <inheritdoc />
    public bool Opened => _stream is not null;

    /// <inheritdoc />
    public event EventHandler<string>? barcodeReceived;

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public bool Open(ScannerConfig Config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(Config);

        error = null;
        Close();

        var Client = new TcpClient();

        try
        {
            // 扫码枪没上电时 Connect 会挂很久：3 秒拿不到就连不上，别让启动流程等它
            var task = Client.ConnectAsync(Config.EthernetAddress.Trim(), Config.EthernetPort);

            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                Client.Dispose();
                error = $"连接扫码枪 {Config.EthernetAddress}:{Config.EthernetPort} 超时。";
                return false;
            }

            _client = Client;
            _stream = Client.GetStream();
            _cancel = new CancellationTokenSource();

            var token = _cancel.Token;
            _ = Task.Run(() => ReadLoopRing(_stream, token), token);

            ConnectionStateChanged?.Invoke(this, true);
            return true;
        }
        catch (AggregateException exception)
        {
            Client.Dispose();
            error = $"连接扫码枪 {Config.EthernetAddress}:{Config.EthernetPort} 失败：{exception.InnerException?.Message ?? exception.Message}";
            return false;
        }
        catch (SocketException exception)
        {
            Client.Dispose();
            error = $"连接扫码枪 {Config.EthernetAddress}:{Config.EthernetPort} 失败：{exception.Message}";
            return false;
        }
        catch (ArgumentException exception)
        {
            Client.Dispose();
            error = $"扫码枪网络参数无效：{exception.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;

        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
            // 断开时释放即可
        }

        _stream = null;

        try
        {
            _client?.Dispose();
        }
        catch (SocketException)
        {
            // 断开时释放即可
        }

        _client = null;
    }

    /// <inheritdoc />
    public bool SenderCommand(string command)
    {
        var stream = _stream;

        if (stream is null || string.IsNullOrEmpty(command))
        {
            return false;
        }

        try
        {
            var byteValue = Encoding.ASCII.GetBytes(command);
            stream.Write(byteValue, 0, byteValue.Length);
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
    public void Dispose() => Close();

    private void ReadLoopRing(NetworkStream stream, CancellationToken Cancel)
    {
        var buffer = new byte[4096];
        var masterMoveClose = false;

        try
        {
            while (!Cancel.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);

                if (read <= 0)
                {
                    break;   // 对端关闭
                }

                var block = Encoding.ASCII.GetString(buffer, 0, read);

                foreach (var frame in ScanFrameParser.Slice(block, _buffer))
                {
                    barcodeReceived?.Invoke(this, frame);
                }
            }
        }
        catch (IOException)
        {
            // 网络异常（对端强制断开）按断线处理
        }
        catch (ObjectDisposedException)
        {
            masterMoveClose = Cancel.IsCancellationRequested;
        }
        catch (SocketException)
        {
            // 同上
        }
        finally
        {
            if (!masterMoveClose)
            {
                _stream = null;
                ConnectionStateChanged?.Invoke(this, false);
            }
        }
    }
}
