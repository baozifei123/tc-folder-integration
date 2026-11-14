# Compatibility entry point. The double-click entry point is 还原.cmd.
param([switch]$Factory)
& (Join-Path $PSScriptRoot 'TC-Manager.ps1') -Action Restore -Factory:$Factory @args
exit $LASTEXITCODE
