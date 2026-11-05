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
# Fichier temporaire propre à ce processus : deux copies ne peuvent plus se bloquer l'une l'autre.
$tempFile = "$outDir\cputemp.$PID.tmp"

# Une seule copie à la fois : une ancienne boucle restée en vie (réinstallation, tâche relancée)
# gardait le fichier temporaire verrouillé et plus aucune température n'était écrite.
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Global\SysDeck-CpuTempBridge', [ref]$createdNew)
if (-not $createdNew) {
    try { $createdNew = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $createdNew = $true }
    if (-not $createdNew) { exit }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
# Restes d'anciennes versions ou de copies arrêtées brutalement.
Get-ChildItem -Path $outDir -Filter 'cputemp*.tmp' | Remove-Item -Force

while ($true) {
    try {
        $output = & $exePath --api GetPMTableData 2>$null
        $match = $output | Select-String -Pattern 'GetCurrentTemperature.*?([\d.]+)\s*Celsius'
        if ($match) {
            $temp = $match.Matches[0].Groups[1].Value
            # Écriture atomique (fichier temporaire + renommage) pour que SysDeck ne lise jamais
            # un fichier à moitié écrit.
            [System.IO.File]::WriteAllText($tempFile, $temp)
            Move-Item -Path $tempFile -Destination $outFile -Force -ErrorAction Stop
        }
    } catch {
        Remove-Item -Path $tempFile -Force
    }
    Start-Sleep -Seconds 3
}
