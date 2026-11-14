# Compatibility entry point. The double-click entry point is 安装-TC默认.cmd.
& (Join-Path $PSScriptRoot 'TC-Manager.ps1') -Action Install @args
exit $LASTEXITCODE
