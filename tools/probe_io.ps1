# 只读探测：连真机（默认 192.168.1.10:502），逐项确认 Modbus 通路与 X/Y 地址空间。
# 全程只发读功能码（01/02/03），绝不写。每个请求独立建连，避免超时后的残包干扰下一次读。

param([string] $IP = '192.168.1.10', [int] $端口 = 502)

$ErrorActionPreference = 'Stop'

function 读请求([string] $地址IP, [int] $端口, [byte] $单元, [byte] $功能码, [int] $起始, [int] $数量) {
    $客户端 = New-Object System.Net.Sockets.TcpClient

    try {
        $任务 = $客户端.ConnectAsync($地址IP, $端口)
        if (-not $任务.Wait(2000)) { return 'TCP连接超时' }

        $流 = $客户端.GetStream()
        $流.ReadTimeout = 2500

        $包 = [byte[]](0, 1, 0, 0, 0, 6, $单元, $功能码,
            (($起始 -shr 8) -band 0xFF), ($起始 -band 0xFF),
            (($数量 -shr 8) -band 0xFF), ($数量 -band 0xFF))
        $流.Write($包, 0, $包.Length)

        $读 = New-Object System.IO.BinaryReader($流)

        try {
            $头 = $读.ReadBytes(7)
            $剩余 = $头[5] * 256 + $头[6] - 1
            $体 = $读.ReadBytes($剩余)
        }
        catch {
            return '响应超时（无应答）'
        }

        if ($体.Length -gt 0 -and $体[0] -ge 0x80) {
            return "Modbus异常码 $($体[1])"
        }

        if ($功能码 -eq 3) {
            $值列表 = @()
            for ($i = 0; $i -lt $数量; $i++) {
                $值列表 += ($体[2 + $i * 2] * 256 + $体[3 + $i * 2])
            }
            return ($值列表 -join ',')
        }

        $位串 = ''
        for ($i = 0; $i -lt $数量; $i++) {
            $位串 += if ((($体[2 + [int]($i / 8)] -shr ($i % 8)) -band 1) -eq 1) { '1' } else { '0' }
        }
        return $位串
    }
    catch {
        return "错误 $($_.Exception.Message)"
    }
    finally {
        $客户端.Close()
    }
}

Write-Host "== 探测 $IP`:$端口（只读 FC01/02/03）=="

foreach ($单元 in 1, 255) {
    Write-Host ("── 单元号 {0} ──" -f $单元)

    Write-Host ("  D0-D9   保持寄存器(FC03) : " +
        (读请求 $IP $端口 $单元 3 0 10))

    Write-Host ("  X0-X31  线圈(FC01) 63488: " +
        (读请求 $IP $端口 $单元 1 63488 32))
    Write-Host ("  X0-X31  离散(FC02) 63488: " +
        (读请求 $IP $端口 $单元 2 63488 32))

    Write-Host ("  Y0-Y31  线圈(FC01) 64512: " +
        (读请求 $IP $端口 $单元 1 64512 32))
    Write-Host ("  Y0-Y31  离散(FC02) 64512: " +
        (读请求 $IP $端口 $单元 2 64512 32))

    Write-Host ("  M0-M31  线圈(FC01) 0    : " +
        (读请求 $IP $端口 $单元 1 0 32))
}
