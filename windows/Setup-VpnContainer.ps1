<#
.SYNOPSIS
  One-time Windows setup for vpncontainer (Georgia Tech GlobalProtect in an isolated container).

.DESCRIPTION
  vpncontainer runs inside WSL2 (Ubuntu 24.04) with Docker Engine, and uses WSLg as
  the X server so Cadence Virtuoso / Vivado windows appear on your Windows
  desktop with no extra X server. This script:

    1. checks WSL2 + WSLg and the distro,
    2. optionally sets WSL resource limits (.wslconfig),
    3. runs `./vpncontainer setup` inside WSL,
    4. creates a Windows SSH key and authorizes it in the container,
    5. writes %USERPROFILE%\.ssh\vpncontainer\config (+ Include) for ssh and your
       gt-<alias> hosts, and installs VS Code's Dev Containers extension so
       `vpncontainer code` opens VS Code inside the container (through Docker),
    6. registers the globalprotectcallback: link handler so the browser
       hands the GT sign-in straight back to the container,
    7. installs a `vpncontainer` command for PowerShell / cmd.

  Run it from PowerShell in the repo folder (no admin needed). The repo can
  live in a Windows folder such as Documents\Projects, or in your WSL home:
    powershell -ExecutionPolicy Bypass -File .\windows\Setup-VpnContainer.ps1

.PARAMETER Distro      WSL distro name (see `wsl -l -v`). Default: Ubuntu-24.04,
                       else Ubuntu.
.PARAMETER RepoPath    Path of this repo as WSL sees it. Default: the folder
                       this script is in (Windows or \\wsl.localhost path).
.PARAMETER Memory      Optional WSL VM memory cap, e.g. 6GB (writes .wslconfig).
.PARAMETER Processors  Optional WSL VM CPU cap, e.g. 4.
#>
param(
  [string]$Distro = "",
  [string]$RepoPath = "",
  [string]$Memory = "",
  [int]$Processors = 0
)
$ErrorActionPreference = "Stop"
function Say($m)  { Write-Host "[vpncontainer] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[vpncontainer] $m" -ForegroundColor Yellow }
function InWsl([string]$cmd) {
  $out = & wsl.exe -d $Distro -- bash -lc $cmd
  if ($LASTEXITCODE -ne 0) { throw "WSL command failed: $cmd" }
  return $out
}

# 1. WSL / WSLg ------------------------------------------------------------
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw "WSL is not installed. Run: wsl --install -d Ubuntu-24.04" }
$distros = (& wsl.exe -l -q) -replace "`0", "" | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_.Trim() }
if (-not $Distro) {
  $Distro = @("Ubuntu-24.04", "Ubuntu") | Where-Object { $distros -contains $_ } | Select-Object -First 1
  if (-not $Distro) { throw "No Ubuntu WSL distro found. Installed: $($distros -join ', '). Install one with: wsl --install -d Ubuntu-24.04 (or pass -Distro <name>)" }
  Say "using WSL distro '$Distro'"
}
if ($distros -notcontains $Distro) { throw "WSL distro '$Distro' not found. Installed: $($distros -join ', '). Install with: wsl --install -d Ubuntu-24.04" }
$ver = (& wsl.exe --version) -replace "`0", "" | Out-String
if ($ver -notmatch "WSLg") { Warn "Could not confirm WSLg. Update WSL with: wsl --update (WSLg provides the X server for Cadence)." }
$wslgOk = InWsl "test -S /mnt/wslg/.X11-unix/X0 && echo yes || echo no"
if ($wslgOk -ne "yes") { Warn "WSLg X socket not found in $Distro; GUI apps will not show until WSLg works (wsl --update; wsl --shutdown)." }

# 2. Optional resource limits ---------------------------------------------
if ($Memory -or $Processors -gt 0) {
  $cfg = Join-Path $env:USERPROFILE ".wslconfig"
  $lines = @()
  if (Test-Path $cfg) { $lines = Get-Content $cfg | Where-Object { $_ -notmatch '^\s*(memory|processors)\s*=' } }
  if ($lines -notcontains "[wsl2]") { $lines = @("[wsl2]") + $lines }
  $idx = [array]::IndexOf($lines, "[wsl2]")
  $add = @()
  if ($Memory) { $add += "memory=$Memory" }
  if ($Processors -gt 0) { $add += "processors=$Processors" }
  $lines = $lines[0..$idx] + $add + $(if ($idx + 1 -lt $lines.Count) { $lines[($idx + 1)..($lines.Count - 1)] } else { @() })
  Set-Content -Path $cfg -Value $lines -Encoding ascii
  Say "wrote WSL limits to $cfg (applies after: wsl --shutdown)"
}

# 3. Linux-side setup -------------------------------------------------------
if (-not $RepoPath) {
  # The repo is the folder above this script, wherever it was cloned.
  $winRepo = (Resolve-Path (Join-Path $PSScriptRoot "..")).ProviderPath
  if ($winRepo -match '^\\\\wsl(\.localhost|\$)\\([^\\]+)\\(.*)$') {
    if ($Matches[2] -ne $Distro) { Warn "This repo is inside WSL distro '$($Matches[2])' but setup targets '$Distro'." }
    $RepoPath = "/" + ($Matches[3] -replace '\\', '/')
  } else {
    $RepoPath = (& wsl.exe -d $Distro -e wslpath -a -u "$winRepo") | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or -not $RepoPath) { throw "Could not translate '$winRepo' to a WSL path." }
  }
}
$quoted = if ($RepoPath -like '~/*') { '"$HOME"/' + "'" + $RepoPath.Substring(2) + "'" } else { "'" + $RepoPath + "'" }
$repo = InWsl "cd $quoted 2>/dev/null && test -x ./vpncontainer -o -f ./vpncontainer && pwd"
if (-not $repo) { throw "vpncontainer repo not found at $RepoPath inside $Distro. Pass -RepoPath, or run this script from the repo's windows folder." }
Say "repo: $repo (in $Distro)"
$hasDocker = InWsl "docker info >/dev/null 2>&1 && echo yes || echo no"
if ($hasDocker -ne "yes") { Warn "Docker does not answer inside $Distro. With Docker Desktop: Settings > Resources > WSL integration > turn on '$Distro'. Without it: install Docker Engine in WSL (see README)." }
InWsl "cd '$repo' && ./vpncontainer setup" | Write-Host

# 4. Windows SSH key, authorized in the container ---------------------------
$sshDir = Join-Path $env:USERPROFILE ".ssh\vpncontainer"
New-Item -ItemType Directory -Force -Path $sshDir | Out-Null
$key = Join-Path $sshDir "id_ed25519"
if (-not (Test-Path $key)) {
  if (-not (Get-Command ssh-keygen.exe -ErrorAction SilentlyContinue)) { throw "OpenSSH client missing: Settings > Apps > Optional features > OpenSSH Client" }
  # Empty passphrase: how "" reaches a native exe differs between PowerShell versions.
  $modern = ($PSVersionTable.PSVersion.Major -ge 7) -and ($PSNativeCommandArgumentPassing -and $PSNativeCommandArgumentPassing -ne 'Legacy')
  if ($modern) { & ssh-keygen.exe -q -t ed25519 -N "" -C "vpncontainer-windows" -f $key }
  else         { & ssh-keygen.exe -q -t ed25519 -N '""' -C "vpncontainer-windows" -f $key }
}
$pub = (Get-Content "$key.pub" -Raw).Trim()
$pub | & wsl.exe -d $Distro -- bash -lc "cd '$repo' && ./vpncontainer authorize" | Write-Host

# 5. SSH config for VS Code Remote-SSH on Windows ---------------------------
#    (Windows OpenSSH has no ControlMaster, hence "none".)
$idWin = ($key -replace '\\','/')
$khWin = ((Join-Path $sshDir "known_hosts") -replace '\\','/')
$cfgText = InWsl "cd '$repo' && ./vpncontainer ssh-config '$idWin' '$khWin' none"
Set-Content -Path (Join-Path $sshDir "config") -Value $cfgText -Encoding ascii
$userCfg = Join-Path $env:USERPROFILE ".ssh\config"
$include = "Include $($sshDir -replace '\\','/')/config"
$existing = if (Test-Path $userCfg) { Get-Content $userCfg -Raw } else { "" }
if ($existing -notmatch [regex]::Escape($include)) {
  Set-Content -Path $userCfg -Value ($include + "`r`n`r`n" + $existing) -Encoding ascii
  Say "added '$include' to $userCfg"
}

# VS Code: Dev Containers attaches to the container through Docker Desktop.
# (`vpncontainer setup` above installed its attach config: log in as dev.)
if (Get-Command code -ErrorAction SilentlyContinue) {
  $exts = & code --list-extensions 2>$null
  if ($exts -notcontains "ms-vscode-remote.remote-containers") {
    & code --install-extension ms-vscode-remote.remote-containers | Write-Host
  }
} else { Warn "VS Code ('code') not found on PATH; install it, add the Dev Containers extension, then re-run this script." }

# 6. globalprotectcallback: link handler -> container -----------------------
$base = "HKCU:\Software\Classes\globalprotectcallback"
if (Test-Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\globalprotectcallback") {
  Warn "The official GlobalProtect app also handles globalprotectcallback: links. The per-user vpncontainer handler takes precedence for you. Keep the official app disconnected/uninstalled or it will route ALL Windows traffic to GT."
}
New-Item -Force -Path $base | Out-Null
Set-ItemProperty -Path $base -Name "(default)" -Value "URL:GlobalProtect sign-in (vpncontainer)"
Set-ItemProperty -Path $base -Name "URL Protocol" -Value ""
New-Item -Force -Path "$base\shell\open\command" | Out-Null
$wsl = Join-Path $env:WINDIR "System32\wsl.exe"
Set-ItemProperty -Path "$base\shell\open\command" -Name "(default)" -Value "`"$wsl`" -d $Distro -e `"$repo/vpncontainer`" callback `"%1`""
Say "registered the globalprotectcallback: link handler"

# 7. vpncontainer command for PowerShell/cmd -------------------------------
$bin = Join-Path $env:LOCALAPPDATA "vpncontainer\bin"
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Set-Content -Path (Join-Path $bin "vpncontainer.cmd") -Encoding ascii -Value "@`"$wsl`" -d $Distro --cd `"$repo`" -e `"$repo/vpncontainer`" %*"
$path = [Environment]::GetEnvironmentVariable("Path", "User")
if ($path -notlike "*$bin*") {
  [Environment]::SetEnvironmentVariable("Path", "$path;$bin", "User")
  Say "added $bin to your PATH (open a new terminal)"
}

# Pin the container's SSH host key for Windows ssh (after the first `up`).
try {
  InWsl "cd '$repo' && ./vpncontainer up" | Write-Host
  $hk = InWsl "cd '$repo' && ./vpncontainer hostkey"
  $port = (InWsl "cd '$repo' && grep -E '^VPNCONTAINER_SSH_PORT=' .env | cut -d= -f2") ; if (-not $port) { $port = 2222 }
  Set-Content -Path (Join-Path $sshDir "known_hosts") -Value "[127.0.0.1]:$port $hk" -Encoding ascii
} catch { Warn "Could not start the container yet ($_). Run 'vpncontainer up' later, then re-run this script to pin the host key." }

Say "Done. Next:"
Write-Host "   vpncontainer connect          # GT username, GT password, then the Duo passcode; the tunnel starts"
Write-Host "   vpncontainer code             # VS Code inside the container (Dev Containers)"
Write-Host "   In that terminal: ssh <alias>  then run virtuoso (windows appear via WSLg)"
