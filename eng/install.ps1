<#
.SYNOPSIS
  Instala o git-remote-svn no Windows: copia o executavel, poe a pasta no PATH do usuario e confere git/svn.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File eng\install.ps1
#>
param(
  [string]$Origem = (Join-Path $PSScriptRoot '..\dist\win-x64\git-remote-svn.exe'),
  [string]$Destino = (Join-Path $env:LOCALAPPDATA 'Programs\git-remote-svn'),
  # para testes: instala sem mexer no PATH do usuario
  [switch]$NaoAlterarPath
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Origem)) {
  throw "Executavel nao encontrado em $Origem. Gere com: dotnet publish src/GitRemoteSvn -c Release -r win-x64 -o dist/win-x64"
}
New-Item -ItemType Directory -Force -Path $Destino | Out-Null
Copy-Item -Force $Origem (Join-Path $Destino 'git-remote-svn.exe')
Write-Host "git-remote-svn instalado em $Destino"

if (-not $NaoAlterarPath) {
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  $partes = @($userPath -split ';' | Where-Object { $_ })
  if ($partes -notcontains $Destino) {
    [Environment]::SetEnvironmentVariable('Path', (($partes + $Destino) -join ';'), 'User')
    Write-Host "Pasta adicionada ao PATH do usuario (vale para terminais novos)."
  }
  if (($env:Path -split ';') -notcontains $Destino) { $env:Path = "$env:Path;$Destino" }
}

$faltando = @()
foreach ($cmd in 'git', 'svn') {
  $c = Get-Command $cmd -ErrorAction SilentlyContinue
  if ($c) { Write-Host ("  {0}: {1}" -f $cmd, ((& $c.Source --version 2>$null | Select-Object -First 1))) }
  else { $faltando += $cmd }
}
if ($faltando) {
  Write-Warning ("Nao encontrado no PATH: {0}. O git-remote-svn precisa dos dois (svn: TortoiseSVN com 'command line client tools' ou SlikSVN)." -f ($faltando -join ', '))
}
Write-Host 'Pronto. Em um terminal novo: git clone svn::https://servidor/svn/repo/projeto'
