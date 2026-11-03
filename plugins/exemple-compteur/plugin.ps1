# Plugin d'exemple SysDeck : compteur.
# SysDeck le lance avec les arguments du Stream Deck : -port <p> -pluginUUID <jeton> -registerEvent registerPlugin -info <json>
# (aussi disponibles dans les variables d'environnement SYSDECK_PLUGIN_PORT et SYSDECK_PLUGIN_UUID).
param(
    [string]$port,
    [string]$pluginUUID,
    [string]$registerEvent,
    [string]$info,
    [Parameter(ValueFromRemainingArguments = $true)]$rest
)
$ErrorActionPreference = 'Stop'
if (-not $port) { $port = $env:SYSDECK_PLUGIN_PORT }
if (-not $pluginUUID) { $pluginUUID = $env:SYSDECK_PLUGIN_UUID }
if (-not $registerEvent) { $registerEvent = 'registerPlugin' }

$none = [Threading.CancellationToken]::None
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$null = $ws.ConnectAsync([Uri]"ws://127.0.0.1:$port", $none).GetAwaiter().GetResult()

function Send($message) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Depth 10 -Compress))
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $bytes)
    $null = $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $none).GetAwaiter().GetResult()
}

function Receive {
    $buffer = New-Object byte[] 65536
    $stream = New-Object IO.MemoryStream
    do {
        $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $buffer)
        $result = $ws.ReceiveAsync($segment, $none).GetAwaiter().GetResult()
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
        $stream.Write($buffer, 0, $result.Count)
    } until ($result.EndOfMessage)
    [Text.Encoding]::UTF8.GetString($stream.ToArray())
}

function Number($value, $default) {
    $n = 0
    if ($null -ne $value -and [int]::TryParse("$value", [ref]$n)) { return $n }
    return $default
}

# Etat de chaque tuile, par context : libelle, pas et valeur.
$tiles = @{}

function Show($context) {
    $tile = $tiles[$context]
    $title = ("{0} {1}" -f $tile.label, $tile.value).Trim()
    Send @{ event = 'setTitle'; context = $context; payload = @{ title = $title } }
}

function Load($message) {
    $settings = $message.payload.settings
    $tiles[$message.context] = @{
        label = "$($settings.label)"
        step  = Number $settings.step 1
        value = Number $settings.value 0
    }
}

Send @{ event = $registerEvent; uuid = $pluginUUID }
Send @{ event = 'logMessage'; payload = @{ message = 'Compteur pret.' } }

while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
    $text = Receive
    if ($null -eq $text) { break }
    $message = $text | ConvertFrom-Json
    $context = $message.context
    switch ($message.event) {
        'willAppear' { Load $message; Show $context }
        'didReceiveSettings' { Load $message; Show $context }
        'willDisappear' { $tiles.Remove($context) }
        'keyDown' {
            if (-not $tiles.ContainsKey($context)) { Load $message }
            $tile = $tiles[$context]
            $tile.value += $tile.step
            Show $context
            # Valeur enregistree dans la tuile : elle survit au redemarrage de SysDeck.
            Send @{ event = 'setSettings'; context = $context; payload = @{ label = $tile.label; step = $tile.step; value = $tile.value } }
        }
    }
}
