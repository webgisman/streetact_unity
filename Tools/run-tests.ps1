# Lance les auto-tests de Novgov.TacticalCore HORS de l'Editeur Unity, puis le compile-check
# des 3 jeux de defines (client Android / serveur dedie / Editeur).
#
# Pourquoi ce script existe : ce harnais a ete reconstruit de zero a trois sessions differentes
# parce qu'il vivait dans un dossier temporaire. Il est desormais VERSIONNE ici, hors de Assets/
# (donc invisible pour Unity, qui ne compile que Assets/).
#
#   .\Tools\run-tests.ps1              # tests + compile-check des 3 configurations
#   .\Tools\run-tests.ps1 -TestsOnly   # tests seulement (~5 s)
#
# Prerequis : le SDK .NET livre avec Unity (aucune installation separee).

param(
    [switch]$TestsOnly,
    [string]$UnityVersion = '6000.5.8f1'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Data\DotNetSdk\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    Write-Host "SDK .NET de Unity introuvable : $dotnet" -ForegroundColor Red
    Write-Host "Passe -UnityVersion <version> si l'Editeur installe est different." -ForegroundColor Yellow
    exit 3
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$failed = 0

Write-Host "`n=== Auto-tests TacticalCore (hors Editeur) ===" -ForegroundColor Cyan
& $dotnet run --project "$root\Tools\TacticalCoreTests\Harness.csproj" -p:NovgovRoot="$root" -v:q --nologo
if ($LASTEXITCODE -ne 0) { $failed++; Write-Host 'TESTS EN ECHEC' -ForegroundColor Red }

if (-not $TestsOnly) {
    # La liste de fichiers des *.check.csproj est desormais un glob (voir Tools/globify_check_csproj.py) :
    # elle ne peut plus se perimer et laisser des fichiers non compiles passer au vert.
    foreach ($proj in @('Assembly-CSharp.check.csproj',
                        'Assembly-CSharp-Server.check.csproj',
                        'Assembly-CSharp-Editor.check.csproj')) {
        Write-Host "`n=== Compile-check : $proj ===" -ForegroundColor Cyan
        if (-not (Test-Path "$root\$proj")) {
            Write-Host "$proj absent — regenere-le en ouvrant le projet dans Unity, puis relance Tools\globify_check_csproj.py." -ForegroundColor Yellow
            continue
        }
        & $dotnet build "$root\$proj" -v:q --nologo | Select-String -Pattern 'error|Erreur\(s\)|ECHEC|ÉCHEC|réussi'
        if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "COMPILE-CHECK EN ECHEC : $proj" -ForegroundColor Red }
    }
}

if ($failed -eq 0) {
    Write-Host "`nTout est vert." -ForegroundColor Green
} else {
    Write-Host "`n$failed etape(s) en echec." -ForegroundColor Red
}
exit $failed
