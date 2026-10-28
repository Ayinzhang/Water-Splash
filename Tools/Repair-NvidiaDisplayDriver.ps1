# Restores an existing, signed, hardware-matching local NVIDIA display package.
# Run from an elevated PowerShell. No download, forced reboot or Unity termination.
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$principalGpu = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principalGpu.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator, then run this script again.'
}
if (Get-Process Unity -ErrorAction SilentlyContinue) {
    throw 'Save your Unity work and close Unity before restoring the display driver.'
}
$packageGpu = 'C:\Program Files\NVIDIA Corporation\Installer2\Display.Driver.{3A8C74A6-36FD-482B-B572-D0B88A84D072}'
$infGpu = Join-Path $packageGpu 'nvtfsi.inf'
$catalogGpu = Join-Path $packageGpu 'NV_DISP.CAT'
$deviceGpu = Get-PnpDevice | Where-Object InstanceId -like 'PCI\VEN_10DE&DEV_2F18&SUBSYS_604E1D05*'
if (-not $deviceGpu) { throw 'The expected RTX 5070 Ti Laptop PCI device was not found.' }
if (-not (Test-Path (Join-Path $packageGpu 'nvlddmkm.sys'))) { throw 'Cached driver package is incomplete.' }
if ((Get-AuthenticodeSignature $catalogGpu).Status -ne 'Valid') { throw 'Driver catalog signature is not valid.' }
if (-not (Select-String -LiteralPath $infGpu -SimpleMatch 'PCI\VEN_10DE&DEV_2F18&SUBSYS_604E1D05')) {
    throw 'This package does not match the GPU hardware.'
}
if ($PSCmdlet.ShouldProcess('RTX 5070 Ti Laptop GPU', 'Install signed cached NVIDIA 617.42 display driver')) {
    & pnputil.exe /add-driver $infGpu /install
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) { throw "Driver installation failed: exit $LASTEXITCODE" }
    Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, Status
    Write-Host 'Reopen Unity after driver installation. If Windows requests a restart, restart before benchmarking.'
    Write-Host 'Verify the comparison UI reports NVIDIA GeForce RTX 5070 Ti Laptop GPU.'
}
