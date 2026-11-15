[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'dist' }
Set-StrictMode -Version 2.0
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework 4.x C# compiler not found.' }

$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $destination 'build'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$redirector = Join-Path $stage 'TCWeChatRedirector.exe'
$executable = Join-Path $destination 'TC-Folder-Integration.exe'
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

Write-Host 'Building the redirector into the isolated release directory...'
$compileRedirector = @('/nologo', '/codepage:65001', '/target:winexe', ('/out:' + $redirector)) + $references + @((Join-Path $PSScriptRoot 'TCWeChatRedirector.cs'))
& $compiler @compileRedirector
if ($LASTEXITCODE -ne 0) { throw "Redirector build failed: $LASTEXITCODE" }

$resources = @(
    @{ File = 'TC-Core.ps1'; Name = 'payload.core' },
    @{ File = 'TC-Manager.ps1'; Name = 'payload.manager' },
    @{ File = $redirector; Name = 'payload.redirector' },
    @{ File = '安装-TC默认.cmd'; Name = 'payload.install' },
    @{ File = '还原.cmd'; Name = 'payload.restore' },
    @{ File = '全局转交-安装.cmd'; Name = 'payload.redirect.install' },
    @{ File = '全局转交-卸载.cmd'; Name = 'payload.redirect.uninstall' },
    @{ File = '全局转交-状态.cmd'; Name = 'payload.redirect.status' }
)
$resourceArgs = foreach ($resource in $resources) {
    $path = if ([System.IO.Path]::IsPathRooted($resource.File)) { $resource.File } else { Join-Path $PSScriptRoot $resource.File }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing payload file: $path" }
    '/resource:' + $path + ',' + $resource.Name
}
Write-Host 'Building the single-file manager...'
$compileManager = @('/nologo', '/codepage:65001', '/target:winexe', ('/out:' + $executable)) + $references + @($resourceArgs) + @((Join-Path $PSScriptRoot 'TC-Folder-Integration.cs'))
& $compiler @compileManager
if ($LASTEXITCODE -ne 0) { throw "Manager build failed: $LASTEXITCODE" }

Write-Host 'Running isolated registry and INI tests...'
& (Join-Path $PSScriptRoot 'tests\Test-TCIntegration.ps1')
Write-Host 'Running package extraction and redirector self-test...'
$errorLog = Join-Path $stage 'selftest-error.txt'
$child = Start-Process -FilePath $executable -ArgumentList '--selftest' -NoNewWindow -Wait -PassThru -RedirectStandardError $errorLog
if ($child.ExitCode -ne 0) {
    $details = if (Test-Path -LiteralPath $errorLog) { Get-Content -LiteralPath $errorLog -Raw -Encoding UTF8 } else { '' }
    throw "Package self-test failed (exit $($child.ExitCode)): $details"
}
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = Join-Path $destination 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText($checksum, ($hash + '  ' + (Split-Path -Leaf $executable) + "`r`n"), [System.Text.Encoding]::ASCII)
Write-Host ('PASS: built and tested ' + $executable)
Write-Host ('SHA-256: ' + $hash)
