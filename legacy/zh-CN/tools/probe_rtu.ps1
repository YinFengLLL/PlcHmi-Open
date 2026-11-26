# 只读探测（RTU over TCP 形态）：无 MBAP 头，[站号][功能码][起始][数量][CRC16]。
param([string] $IP = '192.168.1.10', [int] $端口 = 502)

$ErrorActionPreference = 'Stop'

function CRC16([byte[]] $数据) {
    $crc = 0xFFFF
    foreach ($b in $数据) {
        $crc = $crc -bxor $b
        for ($i = 0; $i -lt 8; $i++) {
            if ($crc -band 1) { $crc = ($crc -shr 1) -bxor 0xA001 } else { $crc = $crc -shr 1 }
        }
    }
    $低字节 = $crc -band 0xFF
    $高字节 = ($crc -shr 8) -band 0xFF
    return [byte[]]($低字节, $高字节)
}

function RTU读([string] $地址IP, [int] $端口, [byte] $站号, [byte] $功能码, [int] $起始, [int] $数量) {
    $帧前 = [byte[]]($站号, $功能码,
        (($起始 -shr 8) -band 0xFF), ($起始 -band 0xFF),
        (($数量 -shr 8) -band 0xFF), ($数量 -band 0xFF))
    $帧 = $帧前 + (CRC16 $帧前)

    $客户端 = New-Object System.Net.Sockets.TcpClient
    try {
        $任务 = $客户端.ConnectAsync($地址IP, $端口)
        if (-not $任务.Wait(2000)) { return 'TCP连接超时' }

        $流 = $客户端.GetStream()
        $流.ReadTimeout = 2500
        $流.Write($帧, 0, $帧.Length)

        $读 = New-Object System.IO.BinaryReader($流)
        try {
            $回 = $读.ReadBytes(256)
        }
        catch {
            return '响应超时（无应答）'
        }

        if ($回.Length -eq 0) { return '空响应' }
        return (($回 | ForEach-Object { $_.ToString('X2') }) -join ' ')
    }
    catch {
        return "错误 $($_.Exception.Message)"
    }
    finally {
        $客户端.Close()
    }
}

Write-Host "== RTU over TCP 探测 $IP`:$端口（只读）=="
Write-Host ("  站1 FC03 D0-D9   : " + (RTU读 $IP $端口 1 3 0 10))
Write-Host ("  站1 FC01 X0-X31 63488: " + (RTU读 $IP $端口 1 1 63488 32))
Write-Host ("  站1 FC02 X0-X31 63488: " + (RTU读 $IP $端口 1 2 63488 32))
Write-Host ("  站1 FC01 Y0-Y31 64512: " + (RTU读 $IP $端口 1 1 64512 32))
Write-Host ("  站2 FC03 D0-D9   : " + (RTU读 $IP $端口 2 3 0 10))
