param(
    [string]$Go = "go",
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "zm9011d"

Push-Location $project
try {
    New-Item -ItemType Directory -Force -Path dist | Out-Null

    Write-Host "==> 运行测试"
    & $Go test ./...
    if ($LASTEXITCODE -ne 0) { throw "go test 失败" }

    $ldflags = "-s -w -X main.version=$Version"

    Write-Host "==> 编译 linux/arm64（IPQ6000 / Cortex-A53）"
    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm64"
    & $Go build -trimpath -ldflags $ldflags -o dist/zm9011d-linux-arm64 ./cmd/zm9011d
    if ($LASTEXITCODE -ne 0) { throw "arm64 编译失败" }

    # 打包工具必须在主机平台运行，先清除交叉编译环境变量
    Write-Host "==> 生成安装包（ipk + 通用 tar.gz）"
    Remove-Item Env:CGO_ENABLED -ErrorAction SilentlyContinue
    Remove-Item Env:GOOS -ErrorAction SilentlyContinue
    Remove-Item Env:GOARCH -ErrorAction SilentlyContinue
    & $Go run ./tools/mkpackage -binary dist/zm9011d-linux-arm64 -config ../files/etc/config/zm9011 -init ../files/etc/init.d/zm9011 -version $Version -arch aarch64_cortex-a53 -outdir dist -verify
    if ($LASTEXITCODE -ne 0) { throw "打包失败" }

    Write-Host "==> 同步 SDK 包内的二进制与脚本"
    $sdkFiles = Join-Path $root "package/zm9011d/files"
    New-Item -ItemType Directory -Force -Path $sdkFiles | Out-Null
    Copy-Item -Force dist/zm9011d-linux-arm64 (Join-Path $sdkFiles "zm9011d")
    Copy-Item -Force ../files/etc/config/zm9011 (Join-Path $sdkFiles "zm9011.config")
    Copy-Item -Force ../files/etc/init.d/zm9011 (Join-Path $sdkFiles "zm9011.init")

    Write-Host "==> 编译 linux/armv7（兼容旧设备）"
    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm"
    $env:GOARM = "7"
    & $Go build -trimpath -ldflags $ldflags -o dist/zm9011d-linux-armv7 ./cmd/zm9011d
    if ($LASTEXITCODE -ne 0) { throw "armv7 编译失败" }

    Get-ChildItem dist | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    Remove-Item Env:CGO_ENABLED -ErrorAction SilentlyContinue
    Remove-Item Env:GOOS -ErrorAction SilentlyContinue
    Remove-Item Env:GOARCH -ErrorAction SilentlyContinue
    Remove-Item Env:GOARM -ErrorAction SilentlyContinue
    Pop-Location
}
