# Écrire un plugin SysDeck

Un plugin ajoute de nouvelles **actions de tuiles** à SysDeck. C'est un programme séparé, écrit dans n'importe quel
langage, que SysDeck lance et avec lequel il échange des messages JSON par un **WebSocket local**. Le principe et les
noms des messages sont repris du Stream Deck d'Elgato (`keyDown`, `willAppear`, `setTitle`, `context`…).

Un exemple complet en PowerShell se trouve dans [`plugins/exemple-compteur`](../plugins/exemple-compteur).

## Le dossier d'un plugin

```
fr.moi.monplugin/
├── manifest.json
├── plugin.exe        (ou plugin.js, plugin.py, plugin.ps1)
├── icon.png          (facultatif : icône du plugin)
└── action.png        (facultatif : icône d'une action)
```

Une fois installé, le plugin vit dans `%AppData%\SysDeck\Plugins\<Id>\`. Le nom du dossier doit être son `Id`.

## manifest.json

```json
{
  "Id": "fr.moi.monplugin",
  "Name": "Mon plugin",
  "Version": "1.0.0",
  "Author": "Moi",
  "Description": "Ce que fait le plugin.",
  "URL": "https://exemple.fr",
  "Icon": "icon.png",
  "SdkVersion": 1,
  "CodePath": "plugin.exe",
  "Actions": [
    {
      "Id": "fr.moi.monplugin.action",
      "Name": "Mon action",
      "Tooltip": "Aide affichée sous le choix de l'action.",
      "Icon": "action.png",
      "Settings": [
        { "Key": "texte", "Type": "text", "Label": "Texte", "Default": "", "Placeholder": "Ex. : bonjour" },
        { "Key": "pas", "Type": "number", "Label": "Pas", "Default": 1, "Description": "Aide sous le champ." },
        { "Key": "actif", "Type": "checkbox", "Label": "Actif", "Default": true },
        { "Key": "mode", "Type": "select", "Label": "Mode", "Default": "a",
          "Options": [ { "Value": "a", "Label": "Mode A" }, { "Value": "b", "Label": "Mode B" } ] },
        { "Key": "couleur", "Type": "color", "Label": "Couleur", "Default": "#FF5B8DEF" },
        { "Key": "fichier", "Type": "file", "Label": "Fichier" },
        { "Key": "notes", "Type": "textarea", "Label": "Notes" }
      ]
    }
  ]
}
```

- `Id` : identifiant inversé unique (lettres, chiffres, points, tirets), par exemple `com.auteur.nom`.
- `CodePath` : programme du plugin, **dans son dossier**. Selon l'extension :
  - `.exe` est lancé directement ;
  - `.js` est lancé avec `node` (il faut que Node.js soit installé) ;
  - `.py` est lancé avec `python` (il faut que Python soit installé) ;
  - `.ps1` est lancé avec `powershell -NoProfile -ExecutionPolicy Bypass -File`.
- `Settings` : SysDeck génère le formulaire de la tuile à partir de cette liste. Les valeurs sont enregistrées dans la
  tuile, puis envoyées au plugin dans `payload.settings`. Les types possibles sont `text`, `textarea`, `number`,
  `checkbox`, `select`, `color` et `file`.

## Lancement

SysDeck lance le programme avec les mêmes arguments que le Stream Deck :

```
plugin.exe -port 51234 -pluginUUID 9F2C… -registerEvent registerPlugin -info {"application":{…},"plugin":{…}}
```

Le port et le jeton sont aussi fournis dans les variables d'environnement `SYSDECK_PLUGIN_PORT` et
`SYSDECK_PLUGIN_UUID`, ce qui est plus simple quand le langage découpe mal les arguments JSON.

Le plugin ouvre alors `ws://127.0.0.1:<port>` et envoie **aussitôt** ce message :

```json
{ "event": "registerPlugin", "uuid": "<pluginUUID>" }
```

Sans ce message, ou avec un mauvais jeton, la connexion est refusée au bout de 10 secondes. Une connexion qui envoie
un en-tête `Origin` (venant d'une page web) est refusée elle aussi.

Le programme est arrêté quand SysDeck se ferme. S'il plante, SysDeck le relance avec un délai croissant, et
l'abandonne après 3 plantages en une minute. Tout ce qu'il écrit sur la sortie standard ou la sortie d'erreur va
dans son journal (Paramètres › Plugins › Journal).

## Messages envoyés par SysDeck

Tous les messages qui concernent une tuile ont cette forme :

```json
{
  "event": "keyDown",
  "action": "fr.moi.monplugin.action",
  "context": "3f1c…",
  "device": "sysdeck",
  "payload": { "settings": { "pas": 1 } }
}
```

`context` identifie **une tuile** de façon stable (il ne change pas d'un redémarrage à l'autre).

| Évènement | Quand |
|---|---|
| `didReceiveGlobalSettings` | Juste après l'enregistrement : réglages globaux du plugin (`payload.settings`). |
| `willAppear` | Une tuile de ce plugin existe (à la connexion, ou quand l'utilisateur l'ajoute). |
| `willDisappear` | La tuile a été retirée, ou son action a changé. |
| `didReceiveSettings` | L'utilisateur a modifié les réglages de la tuile, ou réponse à `getSettings`. |
| `keyDown` puis `keyUp` | La tuile a été touchée (sur la Switch ou dans la barre du PC). |

## Messages que le plugin peut envoyer

| Évènement | Contenu | Effet |
|---|---|---|
| `setTitle` | `context`, `payload.title` | Remplace le nom affiché sous la tuile (60 caractères max). Un titre vide rétablit le nom. |
| `setImage` | `context`, `payload.image` | Remplace l'icône par une image `data:image/png;base64,…` (PNG, JPEG, GIF ou BMP, 1 Mo max). Une image vide rétablit l'icône. |
| `setState` | `context`, `payload.state` | `1` : tuile **active** (couleur active, deuxième icône, bouton enfoncé, réglés par l'utilisateur) ; `0` : état normal. |
| `showOk` | `context` | Allume brièvement la tuile en couleur d'accent. |
| `showAlert` | `context` | Allume brièvement la tuile en rouge. |
| `setSettings` | `context`, `payload` = réglages | Enregistre les réglages de la tuile (ils sont gardés dans la configuration). |
| `getSettings` | `context` | Demande les réglages, qui arrivent dans un `didReceiveSettings`. |
| `setGlobalSettings` | `payload` = réglages | Enregistre des réglages communs à toutes les tuiles du plugin. |
| `getGlobalSettings` | — | Demande les réglages globaux, qui arrivent dans un `didReceiveGlobalSettings`. |
| `openUrl` | `payload.url` | Ouvre une adresse `http` ou `https` dans le navigateur. |
| `logMessage` | `payload.message` | Écrit une ligne dans le journal du plugin. |

Un plugin ne peut agir que sur **ses propres tuiles** : un `context` qui appartient à un autre plugin est ignoré.
Un message reçu ne doit pas dépasser 2 Mo.

Le titre et l'image envoyés par le plugin ne sont pas enregistrés : à chaque `willAppear`, renvoie-les.

## Empaqueter et installer

Un fichier `.sysdeckplugin` est un simple **zip** du dossier du plugin :

```powershell
Compress-Archive -Path .\fr.moi.monplugin\* -DestinationPath .\monplugin.zip
Rename-Item .\monplugin.zip monplugin.sysdeckplugin
```

Pour l'installer : Paramètres › Plugins › **Installer un plugin…**. Une version déjà installée est remplacée.
Ensuite, ajoute une tuile depuis Switch › Ajouter une tuile › **Plugin**.

## Publier un plugin dans le catalogue

Paramètres › Plugins › **Catalogue** cherche sur GitHub les dépôts marqués avec le sujet (« topic ») `sysdeck-plugin`
(plugins SysDeck) ou `streamdeck-plugin` / `stream-deck-plugin` (plugins Stream Deck).

1. Ajoute le sujet `sysdeck-plugin` à ton dépôt GitHub (« About » › « Topics »).
2. Publie une **release** avec, en pièce jointe, ton fichier `.sysdeckplugin`.
3. SysDeck installe le fichier de la dernière release et propose les mises à jour quand une nouvelle release sort.

La liste « Vérifiés » est le fichier [`plugins/catalogue.json`](../plugins/catalogue.json) du dépôt SysDeck :
`[{ "repo": "auteur/depot", "name": "…", "description": "…", "kind": "sysdeck" | "streamdeck" }]`.

## Plugins du Stream Deck d'Elgato

SysDeck fait aussi tourner les plugins Stream Deck **non chiffrés** (publiés sur GitHub en `.streamDeckPlugin`,
ou dossier `.sdPlugin`). Ceux du Marketplace d'Elgato sont chiffrés et ne fonctionnent qu'avec le logiciel d'Elgato.

- Manifest Elgato lu tel quel : `UUID`, `CodePathWin`/`CodePath`, `States` (images des états 0 et 1),
  `PropertyInspectorPath`, `Nodejs.Version`. Les images sans extension (`@2x.png`, `.png`, `.svg`) sont retrouvées.
- Plugins en JavaScript : Node.js du PC s'il est assez récent, sinon téléchargé une fois sur nodejs.org
  (empreinte SHA-256 vérifiée) dans `%AppData%\SysDeck\Runtime`.
- La grille de la Switch est annoncée comme un Stream Deck (`deviceDidConnect`, `coordinates`, `state`).
- La page de réglages du plugin (Property Inspector) s'affiche dans l'inspecteur de la tuile, page Switch.
- Non pris en charge : molettes et écran du Stream Deck +, multi-actions, profils, plugins « page web » (ancienne génération).

## Différences avec le Stream Deck

- Les réglages sont décrits en JSON dans le manifest, à la place d'une page HTML (« Property Inspector »).
- `setState` ne connaît que deux états (0 et 1) ; il n'y a ni multi-actions ni molettes.
- Les SVG ne sont pas acceptés par `setImage`.
