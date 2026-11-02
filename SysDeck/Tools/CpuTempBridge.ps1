# Boucle en tâche de fond, lancée par une tâche planifiée élevée : interroge le SDK officiel AMD
# Ryzen Master (pilote signé, non bloqué par l'Intégrité de la mémoire) pour la température CPU,
# et l'écrit dans un fichier partagé lisible par SysDeck (qui tourne lui en utilisateur standard).
#
# C:\ProgramData est utilisé (pas %LocalAppData%) car cette tâche tourne sous un compte différent
# de celui de SysDeck : %LocalAppData% pointerait vers le profil du compte admin, pas celui de
# l'utilisateur courant. ProgramData est partagé et lisible par tous les comptes par défaut.

$ErrorActionPreference = 'SilentlyContinue'

$exePath = "C:\Program Files\AMD\RyzenMasterSDK\AMDRyzenMasterCLI\bin-prebuilt\AMDRyzenMasterCLI.exe"
$outDir = "C:\ProgramData\SysDeck"
$outFile = "$outDir\cputemp.txt"
$tempFile = "$outDir\cputemp.tmp"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

while ($true) {
    try {
        $output = & $exePath --api GetPMTableData 2>$null
        $match = $output | Select-String -Pattern 'GetCurrentTemperature.*?([\d.]+)\s*Celsius'
        if ($match) {
            $temp = $match.Matches[0].Groups[1].Value
            # Écriture atomique (fichier temporaire + renommage) pour que SysDeck ne lise jamais
            # un fichier à moitié écrit.
            Set-Content -Path $tempFile -Value $temp -NoNewline
            Move-Item -Path $tempFile -Destination $outFile -Force
        }
    } catch { }
    Start-Sleep -Seconds 3
}
