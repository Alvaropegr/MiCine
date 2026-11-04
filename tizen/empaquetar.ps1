# Empaqueta la app de la tele (MiCine.wgt) y, si se indica la IP de la tele, la instala en ella.
# Antes hace falta Tizen Studio con «TV Extensions» y «Samsung Certificate Extension», y un perfil
# de certificado de Samsung llamado «MiCine» (ver LEEME.md, apartado «Tele Samsung»).
#
#   powershell -ExecutionPolicy Bypass -File tizen\empaquetar.ps1
#   powershell -ExecutionPolicy Bypass -File tizen\empaquetar.ps1 -Tele 192.168.1.50

param([string]$Tele = "", [string]$Perfil = "MiCine")
$ErrorActionPreference = "Stop"

$candidatos = @("C:\tizen-studio", "$env:USERPROFILE\tizen-studio", "D:\tizen-studio")
$raizTizen = $candidatos | Where-Object { Test-Path (Join-Path $_ "tools\ide\bin\tizen.bat") } | Select-Object -First 1
if (-not $raizTizen) { throw "No encuentro Tizen Studio (lo he buscado en $($candidatos -join ', ')). Instálalo siguiendo LEEME.md." }
$tizen = Join-Path $raizTizen "tools\ide\bin\tizen.bat"
$sdb = Join-Path $raizTizen "tools\sdb.exe"

# Se empaqueta una copia, para que los archivos que añade Tizen no se mezclen con el código.
$app = Join-Path $PSScriptRoot "app"
$obra = Join-Path $PSScriptRoot "compilado"
if (Test-Path $obra) { Remove-Item -Recurse -Force $obra }
Copy-Item -Recurse $app $obra

& $tizen package -t wgt -s $Perfil -- $obra
if ($LASTEXITCODE -ne 0) { throw "No se pudo empaquetar. ¿Existe en Tizen Studio el perfil de certificado «$Perfil»?" }
$wgt = Get-ChildItem $obra -Filter *.wgt | Select-Object -First 1
if (-not $wgt) { throw "Tizen no ha generado el archivo .wgt." }
$destino = Join-Path $PSScriptRoot "MiCine.wgt"
Copy-Item $wgt.FullName $destino -Force
Write-Host "Hecho: $destino"

if ($Tele) {
    & $sdb connect $Tele
    $serie = "$($Tele):26101"
    & $tizen install -n $wgt.Name -s $serie -- $obra
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo instalar. ¿Está la tele encendida, en modo desarrollador y con la IP de este ordenador?"
    }
    Write-Host "Instalada en la tele $Tele. Está en Aplicaciones (al final de la lista)."
}
