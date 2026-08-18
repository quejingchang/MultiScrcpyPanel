<#
.SYNOPSIS
    下载官方 scrcpy-server v4.0 到 csharp\assets\scrcpy-server-v4.0.jar。

.DESCRIPTION
    本项目**复用官方 scrcpy-server.jar**，不自行实现设备端逻辑（架构文档 §5.1）。

    版本铁律：jar 的版本必须与 AppConfig.ServerVersion 严格一致，
    因为版本号会作为 app_process 的**第一个位置参数**传给 server，
    不匹配时 server 会直接以 "The server version does not match the client" 退出。

.PARAMETER Version
    scrcpy 版本号，默认 4.0。

.PARAMETER Url
    自定义下载地址。默认从 Genymobile/scrcpy 的 GitHub Release 取 scrcpy-server-v<Version>。

.PARAMETER Force
    即使目标文件已存在也强制重新下载。

.EXAMPLE
    pwsh tools\fetch_scrcpy_server.ps1

.EXAMPLE
    pwsh tools\fetch_scrcpy_server.ps1 -Version 4.0 -Force
#>

[CmdletBinding()]
param(
    [string] $Version = '4.0',
    [string] $Url = '',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 路径

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$CsharpRoot = Split-Path -Parent $ScriptDir
$AssetsDir  = Join-Path $CsharpRoot 'assets'
$TargetFile = Join-Path $AssetsDir "scrcpy-server-v$Version.jar"

if ([string]::IsNullOrWhiteSpace($Url)) {
    $Url = "https://github.com/Genymobile/scrcpy/releases/download/v$Version/scrcpy-server-v$Version"
}

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]   $Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn2([string] $Message) { Write-Host "    $Message" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 主流程

Write-Step "目标文件：$TargetFile"

if ((Test-Path -LiteralPath $TargetFile) -and (-not $Force)) {
    $size = (Get-Item -LiteralPath $TargetFile).Length
    Write-Ok ("已存在，跳过下载（加 -Force 可强制重下）：{0:N0} 字节" -f $size)
    exit 0
}

New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null

$TempFile = Join-Path ([System.IO.Path]::GetTempPath()) ("scrcpy-server-" + [System.Guid]::NewGuid().ToString('N') + '.jar')

try {
    Write-Step "下载：$Url"

    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -OutFile $TempFile -UseBasicParsing -MaximumRedirection 10
    }
    finally {
        $ProgressPreference = $oldProgress
    }

    $size = (Get-Item -LiteralPath $TempFile).Length
    if ($size -lt 10KB) {
        throw "下载内容异常（仅 $size 字节），可能是 404 页面而非 jar。请确认版本号 v$Version 存在。"
    }

    # jar 本质是 zip：校验魔数 "PK\x03\x04"，防止把 HTML 错误页当成 jar 推到设备
    $head = [System.IO.File]::ReadAllBytes($TempFile)[0..3]
    if ($head[0] -ne 0x50 -or $head[1] -ne 0x4B) {
        throw '下载的文件不是合法的 jar/zip（魔数校验失败）。请手动从 GitHub Release 下载。'
    }

    Move-Item -LiteralPath $TempFile -Destination $TargetFile -Force
    Write-Ok ("下载完成：{0:N0} 字节" -f $size)

    $hash = (Get-FileHash -LiteralPath $TargetFile -Algorithm SHA256).Hash
    Write-Ok "SHA256：$hash"

    Write-Host ''
    Write-Host '下一步：' -ForegroundColor Cyan
    Write-Host "  确认 config\settings.json 中 ServerVersion = `"$Version`"（必须与 jar 版本严格一致）"
    Write-Host '  dotnet build MultiScrcpyPanel.sln -c Debug'
}
catch {
    Write-Host ''
    Write-Host "下载失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host '手动方案：' -ForegroundColor Yellow
    Write-Host "  1. 访问 https://github.com/Genymobile/scrcpy/releases/tag/v$Version"
    Write-Host "  2. 下载资产 scrcpy-server-v$Version（无扩展名）"
    Write-Host "  3. 重命名为 scrcpy-server-v$Version.jar 并放到："
    Write-Host "     $AssetsDir"
    exit 1
}
finally {
    if (Test-Path -LiteralPath $TempFile) {
        Remove-Item -LiteralPath $TempFile -Force -ErrorAction SilentlyContinue
    }
}
