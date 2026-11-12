# Boucle en tâche de fond, lancée par une tâche planifiée élevée : interroge le SDK officiel AMD
# Ryzen Master (pilote signé, non bloqué par l'Intégrité de la mémoire) pour la température CPU,
# et l'écrit dans un fichier partagé lisible par SysDeck (qui tourne lui en utilisateur standard).
#
# C:\ProgramData est utilisé (pas %LocalAppData%) car cette tâche tourne sous un compte différent
# de celui de SysDeck : %LocalAppData% pointerait vers le profil du compte admin, pas celui de
# l'utilisateur courant. ProgramData est partagé et lisible par tous les comptes par défaut.
#
# Journal : cputemp-bridge.log (même dossier, lisible sans droits administrateur). Seuls les changements
# d'état y sont écrits (démarrage, erreur de l'outil AMD, retour à la normale), plus un rappel toutes les
# 10 minutes si l'échec dure : on sait pourquoi la température manque sans avoir à lancer l'outil en admin.

$ErrorActionPreference = 'SilentlyContinue'

$exePath = "C:\Program Files\AMD\RyzenMasterSDK\AMDRyzenMasterCLI\bin-prebuilt\AMDRyzenMasterCLI.exe"
$outDir = "C:\ProgramData\SysDeck"
$outFile = "$outDir\cputemp.txt"
$logFile = "$outDir\cputemp-bridge.log"
$maxLogBytes = 256KB
# Fichier temporaire propre à ce processus : deux copies ne peuvent plus se bloquer l'une l'autre.
$tempFile = "$outDir\cputemp.$PID.tmp"

function Write-BridgeLog([string]$message) {
    try {
        # Taille bornée : l'ancien journal est gardé une fois (.old), puis remplacé.
        if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt $maxLogBytes) {
            Move-Item -Path $logFile -Destination "$logFile.old" -Force
        }
        $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $PID, $message
        [System.IO.File]::AppendAllText($logFile, $line + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
    } catch { }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Une seule copie à la fois : une ancienne boucle restée en vie (réinstallation, tâche relancée)
# gardait le fichier temporaire verrouillé et plus aucune température n'était écrite.
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Global\SysDeck-CpuTempBridge', [ref]$createdNew)
if (-not $createdNew) {
    try { $createdNew = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $createdNew = $true }
    if (-not $createdNew) {
        Write-BridgeLog 'Une autre copie du pont est déjà active : arrêt de celle-ci.'
        exit
    }
}

# Restes d'anciennes versions ou de copies arrêtées brutalement.
Get-ChildItem -Path $outDir -Filter 'cputemp*.tmp' | Remove-Item -Force

if (Test-Path $exePath) { Write-BridgeLog 'Démarrage du pont de température.' }
else { Write-BridgeLog "Outil AMD introuvable : $exePath (AMD Ryzen Master SDK installé ?)" }

$failure = $null          # dernière erreur constatée (null = lecture correcte)
$failures = 0             # échecs d'affilée
$failingSince = $null
$lastReminder = $null

while ($true) {
    $problem = $null
    try {
        # Via cmd : sortie et erreurs de l'outil en texte simple (PowerShell 5.1 perdrait les messages
        # d'erreur, comme « Platform init failed », avec ErrorActionPreference = SilentlyContinue).
        $output = & cmd.exe /c "`"$exePath`" --api GetPMTableData 2>&1"
        $exitCode = $LASTEXITCODE
        $match = $output | Select-String -Pattern 'GetCurrentTemperature.*?([\d.]+)\s*Celsius'
        if ($match) {
            $temp = $match.Matches[0].Groups[1].Value
            # Écriture atomique (fichier temporaire + renommage) pour que SysDeck ne lise jamais
            # un fichier à moitié écrit.
            [System.IO.File]::WriteAllText($tempFile, $temp)
            Move-Item -Path $tempFile -Destination $outFile -Force -ErrorAction Stop
        } else {
            # Première ligne utile de la réponse (ex. « Platform init failed », « User is not admin... »).
            $first = $output | ForEach-Object { "$_".Trim() } | Where-Object { $_ } | Select-Object -First 1
            if (-not $first) { $first = 'réponse vide' }
            $problem = "aucune température dans la réponse de l'outil AMD (code $exitCode) : $first"
        }
    } catch {
        Remove-Item -Path $tempFile -Force
        $problem = "erreur : $($_.Exception.Message)"
    }

    $now = Get-Date
    if ($problem) {
        $failures++
        if ($problem -ne $failure) {
            if (-not $failure) { $failingSince = $now }
            Write-BridgeLog "Échec de lecture : $problem"
            $failure = $problem
            $lastReminder = $now
        } elseif (($now - $lastReminder).TotalMinutes -ge 10) {
            Write-BridgeLog ("Toujours en échec depuis {0:HH:mm} ({1} tentatives) : {2}" -f $failingSince, $failures, $problem)
            $lastReminder = $now
        }
    } elseif ($failure) {
        Write-BridgeLog ("Température de nouveau lue, après {0} échec(s) depuis {1:HH:mm}." -f $failures, $failingSince)
        $failure = $null
        $failures = 0
    }

    Start-Sleep -Seconds 3
}
