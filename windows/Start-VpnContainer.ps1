<#
.SYNOPSIS
  GT VPN in one go: set up the first time, start Docker Desktop if needed,
  bring up the vpncontainer container and sign in with vpncontainer-login.conf, so you
  type only the Duo code.

.DESCRIPTION
  Start-VpnContainer.cmd in the repo folder runs this script (double-click it).
  Everything it uses is in this repo. Steps:
    1. starts Docker Desktop if Docker does not answer, and waits for it,
    2. the first time (no .env or no vpncontainer command yet), runs
       windows\Setup-VpnContainer.ps1,
    3. keeps your saved password encrypted: a plain-text password in
       vpncontainer-login.conf becomes password_dpapi (readable only by your Windows
       account on this PC) and the plain line is emptied,
    4. runs `vpncontainer connect` in WSL: it starts the container if needed, hands
       the saved username and password to the sign-in through the
       container's root-only memory, and asks you only for the Duo code. If
       GT refuses the saved password it stops after that one try and does
       not use it again until you change vpncontainer-login.conf, so a wrong saved
       password cannot lock your GT account.

  vpncontainer-login.conf sits next to Start-VpnContainer.cmd and is never committed (it
  is in .gitignore). vpncontainer-login.example.conf is its empty template.

.PARAMETER SavePassword  Ask for your GT password (hidden), store it in
                         vpncontainer-login.conf encrypted for your Windows account
                         (DPAPI), then exit.
.PARAMETER Check         Only report what is set up and running; start
                         nothing and sign in nowhere.
.PARAMETER Setup         Run windows\Setup-VpnContainer.ps1 first even if it ran
                         before.
#>
param(
  [switch]$SavePassword,
  [switch]$Check,
  [switch]$Setup,
  # For tests only: another config file.
  [Parameter(DontShow)][string]$ConfPath = ""
)
$ErrorActionPreference = "Continue"   # native tools report through exit codes
$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).ProviderPath
$Conf = if ($ConfPath) { $ConfPath } else { Join-Path $Repo "vpncontainer-login.conf" }
$Example = Join-Path $Repo "vpncontainer-login.example.conf"
# Its name before the rename (gtvpn): renamed, keeping its times (which say
# whether a .refused next to it is still current).
if (-not $ConfPath) {
  foreach ($x in @("", ".refused")) {
    $old = Join-Path $Repo "gtvpn-login.conf$x"
    if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath "$Conf$x")) { Move-Item -LiteralPath $old -Destination "$Conf$x" }
  }
}

function Say($m)  { Write-Host "[vpncontainer] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[vpncontainer] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "[vpncontainer] $m" -ForegroundColor Red; exit 1 }

# ---- vpncontainer-login.conf: key=value lines, # comments (the same rules as ./vpncontainer) ----
function Read-Conf {
  $c = @{}
  if (-not (Test-Path -LiteralPath $Conf)) { return $c }
  foreach ($line in Get-Content -LiteralPath $Conf -Encoding UTF8) {
    if ($line -match '^\s*([#;].*)?$') { continue }
    $i = $line.IndexOf("=")
    if ($i -lt 1) { continue }
    $c[$line.Substring(0, $i).Trim().ToLowerInvariant()] = $line.Substring($i + 1).Trim()
  }
  return $c
}
function Set-ConfValue([string]$Key, [string]$Value) {
  $src = if (Test-Path -LiteralPath $Conf) { $Conf } else { $Example }
  $lines = @(Get-Content -LiteralPath $src -Encoding UTF8)
  $found = $false
  $out = @(foreach ($l in $lines) {
    if ($l -match "^\s*$([regex]::Escape($Key))\s*=") { $found = $true; "$Key=$Value" } else { $l }
  })
  if (-not $found) { $out += "$Key=$Value" }
  # LF line ends and no byte-order mark, so WSL reads it the same way.
  [IO.File]::WriteAllText($Conf, (($out -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
}
function Test-Dpapi([string]$Blob) {
  try { $null = ConvertTo-SecureString $Blob -ErrorAction Stop; return $true } catch { return $false }
}

# ---- where WSL finds this repo -----------------------------------------------------
$vpncontainerCmd = Join-Path $env:LOCALAPPDATA "vpncontainer\bin\vpncontainer.cmd"
$distro = ""
# (gtvpn.cmd: the command setup installed before the rename. Only the new
# one counts as setup done, so the first start after the rename runs setup
# again, which installs it.)
foreach ($c in @($vpncontainerCmd, (Join-Path $env:LOCALAPPDATA "gtvpn\bin\gtvpn.cmd"))) {
  if (-not $distro -and (Test-Path $c) -and ((Get-Content $c -Raw) -match '-d\s+(\S+)')) { $distro = $Matches[1] }
}
$wslRepo = $Repo
if ($Repo -match '^\\\\wsl(\.localhost|\$)\\([^\\]+)\\(.*)$') {
  # The repo is inside a WSL distro: use that distro and its Linux path.
  $distro = $Matches[2]; $wslRepo = "/" + ($Matches[3] -replace '\\', '/')
}
if (-not $distro) {
  $have = (& wsl.exe -l -q 2>$null) -replace "`0", "" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
  $distro = @("Ubuntu-24.04", "Ubuntu") | Where-Object { $have -contains $_ } | Select-Object -First 1
}
$docker = (Get-Command docker.exe -ErrorAction SilentlyContinue).Source
if (-not $docker) { $docker = Join-Path $env:ProgramFiles "Docker\Docker\resources\bin\docker.exe" }
$dockerDesktop = Join-Path $env:ProgramFiles "Docker\Docker\Docker Desktop.exe"
function Test-Docker { if (-not (Test-Path $docker)) { return $false }; & $docker info --format "{{.ServerVersion}}" 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }

# ---- -SavePassword ---------------------------------------------------------------------
if ($SavePassword) {
  $s = Read-Host "GT password to save (hidden; encrypted so only your Windows account on this PC can read it)" -AsSecureString
  if ($s.Length -eq 0) { Fail "nothing typed; nothing saved" }
  Set-ConfValue "password_dpapi" (ConvertFrom-SecureString $s)
  Set-ConfValue "password" ""
  Say "saved, encrypted, in $Conf (the plain-text password line is empty)"
  if (-not (Read-Conf)["username"]) { Warn "also put your GT username in it: username=<your GT username>" }
  exit 0
}

# ---- -Check ------------------------------------------------------------------------------
$cfg = Read-Conf
if ($Check) {
  Say "repo:      $Repo (WSL distro: $(if ($distro) { $distro } else { 'NONE: no Ubuntu distro found' }))"
  Say "config:    $Conf $(if (Test-Path -LiteralPath $Conf) { '(found)' } else { '(missing: you are asked for everything)' })"
  Say "username:  $(if ($cfg['username']) { $cfg['username'] } else { '(not set)' })"
  $dpOk = $cfg['password_dpapi'] -and (Test-Dpapi $cfg['password_dpapi'])
  $pwState = if ($dpOk) { 'saved, encrypted (DPAPI), readable here' }
             elseif ($cfg['password']) { 'saved in plain text (the next start encrypts it)' }
             elseif ($cfg['password_dpapi']) { 'password_dpapi CANNOT be decrypted by this account (run -SavePassword)' }
             else { '(not set: you are asked)' }
  Say "password:  $pwState"
  if ($dpOk -and $cfg['password']) { Warn "vpncontainer-login.conf also has a plain-text password; the encrypted one is used, so you can empty the password= line" }
  if ($cfg['password_dpapi'] -and -not $dpOk -and $cfg['password']) { Warn "password_dpapi holds no usable encrypted value; the next start replaces it with one made from password=" }
  Say "setup:     $(if ((Test-Path $vpncontainerCmd) -and (Test-Path (Join-Path $Repo '.env'))) { 'done' } else { 'not yet (the first start runs it)' })"
  if (Test-Docker) {
    $ct = "vpncontainer"
    $st = & $docker container inspect -f "{{.State.Running}}" $ct 2>$null
    if (-not $st) {  # the container from before the rename, if it is still there
      $o = & $docker container inspect -f "{{.State.Running}}" gtvpn 2>$null
      if ($o) { $ct = "gtvpn"; $st = $o }
    }
    $tun = if ("$st".Trim() -eq "true") { & $docker exec $ct ip link show tun0 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { ', tunnel UP' } else { ', no tunnel' } } else { '' }
    $was = if ($ct -eq "gtvpn") { " (from before the rename; the next start without a tunnel replaces it)" } else { "" }
    Say "docker:    running; container ${ct}$($was): $(if ("$st".Trim() -eq 'true') { 'running' } elseif ($st) { 'stopped' } else { 'not created' })$tun"
  } else { Say "docker:    not running (a start would start Docker Desktop)" }
  exit 0
}
if (-not $distro) { Fail "no Ubuntu WSL distro found. Install one with: wsl --install -d Ubuntu-24.04" }

# ---- 1. Docker ------------------------------------------------------------------------------
# (Without docker.exe on Windows, Docker Engine runs inside WSL: only the WSL
# check below applies.)
if ((Test-Path $docker) -and -not (Test-Docker)) {
  if (-not (Test-Path $dockerDesktop)) { Fail "Docker is not running, and Docker Desktop is not at $dockerDesktop" }
  Say "starting Docker Desktop (after a restart this takes a minute or two)"
  Start-Process -FilePath $dockerDesktop
  $t = [Diagnostics.Stopwatch]::StartNew()
  while (-not (Test-Docker)) {
    if ($t.Elapsed.TotalSeconds -gt 300) { Write-Host ""; Fail "Docker did not answer within 5 minutes" }
    Write-Host -NoNewline "."; Start-Sleep -Seconds 3
  }
  Write-Host ""; Say "Docker is running"
}
# Docker Desktop's WSL integration can lag behind the Windows engine.
$t = [Diagnostics.Stopwatch]::StartNew()
while ($true) {
  & wsl.exe -d $distro -e docker info 2>&1 | Out-Null
  if ($LASTEXITCODE -eq 0) { break }
  if ($t.Elapsed.TotalSeconds -gt 120) { Fail "docker does not answer inside WSL ($distro). Docker Desktop: Settings > Resources > WSL integration > turn on '$distro'." }
  Start-Sleep -Seconds 3
}

# ---- 2. first-time setup --------------------------------------------------------------------
if ($Setup -or -not (Test-Path $vpncontainerCmd) -or -not (Test-Path (Join-Path $Repo ".env"))) {
  Say "first-time setup (windows\Setup-VpnContainer.ps1)"
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Setup-VpnContainer.ps1") -Distro $distro
  if ($LASTEXITCODE -ne 0) { Fail "setup failed (see above)" }
}

# ---- 3. the saved sign-in -------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Conf)) {
  Copy-Item -LiteralPath $Example -Destination $Conf
  Say "created vpncontainer-login.conf next to Start-VpnContainer.cmd. Put your GT username and password in it"
  Say "(or run Start-VpnContainer.cmd -SavePassword) to type only the Duo code from now on."
  $cfg = Read-Conf
}
$dpOk = $cfg['password_dpapi'] -and (Test-Dpapi $cfg['password_dpapi'])
if ($cfg['password'] -and -not $dpOk) {
  # A plain-text password, and no working encrypted one: encrypt it. (This
  # also replaces a password_dpapi that is not really an encrypted value.)
  $s = ConvertTo-SecureString $cfg['password'] -AsPlainText -Force
  Set-ConfValue "password_dpapi" (ConvertFrom-SecureString $s)
  Set-ConfValue "password" ""
  $s = $null; $cfg = Read-Conf
  Say "encrypted the password in vpncontainer-login.conf for your Windows account (password_dpapi); the plain-text line is now empty"
} elseif ($cfg['password'] -and $dpOk) {
  Warn "vpncontainer-login.conf has both an encrypted and a plain-text password. The encrypted one is used; you can empty the password= line."
} elseif ($cfg['password_dpapi'] -and -not $dpOk) {
  Warn "password_dpapi in vpncontainer-login.conf cannot be decrypted by this Windows account, so you will be asked for your password. Fix: Start-VpnContainer.cmd -SavePassword"
}
if (($cfg['password'] -or $cfg['password_dpapi']) -and -not $cfg['username']) {
  Warn "vpncontainer-login.conf has a password but no username= line filled in, so it is not used this time."
}

# ---- 4. sign in -----------------------------------------------------------------------------
Say "signing in (vpncontainer connect)"
& wsl.exe -d $distro --cd $wslRepo -e bash ./vpncontainer connect
if ($LASTEXITCODE -ne 0) { Fail "the sign-in did not finish (see above); 'vpncontainer logs' shows the transcript" }
Say "connected. You can close this window; the tunnel keeps running in the container."
