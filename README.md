# FileZillaUpdater

Petit utilitaire Windows qui tient le client [FileZilla](https://filezilla-project.org) à
jour : à l'ouverture de session, il compare la version installée à la dernière version
publiée et, si une version plus récente existe, propose de l'installer en silence. Il
n'installe jamais rien sans que l'utilisateur l'ait accepté.

Outil indépendant, non affilié au projet FileZilla ni approuvé par lui.

Livrable : un seul fichier, `FileZillaUpdater.exe` (environ 460 Ko), sans droits
administrateur pour l'installer.

## Pourquoi passer par Chocolatey

Le site de FileZilla refuse les téléchargements automatisés : ses liens de téléchargement
sont générés pour un navigateur et les requêtes d'un programme sont rejetées. FileZillaUpdater
n'appelle donc aucune adresse de filezilla-project.org, ni pour connaître la dernière version
ni pour la télécharger.

Il utilise le paquet `filezilla` du dépôt communautaire [Chocolatey](https://community.chocolatey.org/packages/filezilla),
qui embarque l'installeur officiel 64 bits :

| Étape | Moyen |
|---|---|
| Dernière version | Requête HEAD sur `https://community.chocolatey.org/api/v2/package/filezilla`, version lue dans l'adresse de redirection (`filezilla.<version>.nupkg`), sans télécharger le paquet |
| Téléchargement | Le paquet `.nupkg` (une archive zip) à cette adresse, uniquement si l'utilisateur a accepté l'installation |
| Installeur | `tools/FileZilla_<version>_win64-setup.exe`, extrait du paquet. Le script d'installation Chocolatey n'est pas utilisé et Chocolatey n'a pas besoin d'être installé |
| Contrôle | Signature Authenticode de l'installeur : valide et émise pour `Tim Kosse` (auteur de FileZilla). Sinon rien n'est installé et un message le dit |

Chocolatey limite le nombre de requêtes : FileZillaUpdater l'interroge au plus une fois par
ouverture de session (plus "Vérifier maintenant" dans la fenêtre de réglages), jamais en
boucle. Une réponse "trop de requêtes" n'est pas répétée.

## Installation

1. Lancer `FileZillaUpdater.exe` (depuis Téléchargements, une clé USB…).
2. Si le runtime .NET Desktop 10 (x64) manque, le lanceur propose de le télécharger
   depuis Microsoft (`https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe`)
   et de l'installer. L'installeur demande lui-même l'élévation (UAC).
3. La fenêtre d'installation ne montre que les options et le bouton "Installer".
4. "Installer" copie le lanceur dans `%LOCALAPPDATA%\Programs\FileZillaUpdater`, applique
   les options et affiche "Installation terminée.". Après OK, le lanceur installé est
   démarré avec `/demarrage /installation` : première vérification, silencieuse s'il n'y a
   rien à proposer.
5. Si FileZilla n'est pas installé sur le poste, cette première vérification demande
   "FileZilla n'est pas installé. L'installer maintenant ?" (Installer / Non). "Installer"
   l'installe en silence pour tous les utilisateurs. Aux ouvertures de session suivantes,
   la question n'est plus posée, seul "Vérifier maintenant" la repose.

Lancé depuis le menu Démarrer ou le Bureau, la fenêtre de réglages complète s'ouvre :
versions installée et disponible, "Vérifier maintenant", "Appliquer", "Désinstaller".
Relancer un `FileZillaUpdater.exe` plus récent puis "Installer" met à jour la copie
installée.

## Options

| Option | Par défaut | Effet |
|---|---|---|
| Démarrer avec Windows | cochée | Valeur `FileZillaUpdater` dans `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` : `"<dossier>\FileZillaUpdater.exe" /demarrage` |
| Raccourci sur le Bureau | décochée | `FileZillaUpdater.lnk` sur le Bureau de l'utilisateur |
| Raccourci dans le menu Démarrer | cochée | `FileZillaUpdater.lnk` dans le menu Démarrer de l'utilisateur |
| Thème | Thème de Windows | Clair, sombre, ou celui de Windows (suivi s'il change) |
| Langue | Langue de Windows | Français ou anglais (anglais si Windows n'est pas en français) |

Les options sont mémorisées dans `%APPDATA%\FileZillaUpdater\settings.json`.

## Fonctionnement au démarrage (`/demarrage`)

Aucune fenêtre au départ :

1. Lecture de la version installée de FileZilla dans les programmes installés (clé
   `...\CurrentVersion\Uninstall\FileZilla Client`, HKLM 64 bits, HKLM 32 bits et HKCU, la
   plus récente si plusieurs).
2. Requête à Chocolatey (voir plus haut). Si le réseau n'est pas encore prêt, nouvelles
   tentatives après 5, 10, 15 et 30 s, puis abandon silencieux.
3. FileZilla à jour, absent, ou Chocolatey injoignable : fermeture sans rien afficher.
4. Sinon, une petite fenêtre demande "FileZilla x est disponible (version installée : y).
   Installer maintenant ?" :
   - "Installer" : mise à jour silencieuse (voir plus bas),
   - "Plus tard" : rien n'est mémorisé, la question revient à la prochaine ouverture de session.

### Mise à jour silencieuse

1. Si FileZilla est ouvert, FileZillaUpdater propose de le fermer ("Fermer FileZilla" /
   "Annuler"). Annuler : rien n'est fait.
2. La fenêtre devient une fenêtre d'attente ("Téléchargement et installation de FileZilla x…",
   "Autoriser les modifications de l'appareil si Windows le demande.").
3. Téléchargement du paquet dans un dossier temporaire, extraction de l'installeur,
   contrôle de la signature. Signature refusée : arrêt, FileZilla n'est pas fermé.
4. FileZilla est fermé (fermeture propre, puis arrêt forcé après 10 s) et l'installeur NSIS
   est lancé avec `/S`, et `/user=all` ou `/user=current` selon la portée de l'installation
   existante (tous les utilisateurs pour une première installation). Windows demande
   l'élévation (UAC).
5. À la fin :
   - succès : la version installée est relue dans le registre, "FileZilla x est installé." avec OK,
   - UAC refusée : fermeture sans message,
   - échec : message d'erreur avec le code de sortie.

   FileZilla est relancé s'il était ouvert avant (sauf si son installeur l'a déjà relancé).
   Le dossier temporaire est supprimé dans tous les cas.

Le déroulement est noté dans `%LOCALAPPDATA%\FileZillaUpdater\journal.log` (au-delà de
256 Ko, l'ancien journal devient `journal.old.log`).

## Mise à jour automatique de FileZillaUpdater

À chaque vérification, FileZillaUpdater regarde aussi sa propre dernière version sur GitHub
(`https://api.github.com/repos/jeanbrowaeyspro/FileZillaUpdater/releases/latest`). Si elle
est plus récente, il télécharge `FileZillaUpdater.exe` et remplace en silence le lanceur
installé, sans droits administrateur. La nouvelle version prend effet au lancement suivant.

## Désinstallation

Bouton "Désinstaller" de la fenêtre de réglages, après confirmation : retrait du démarrage
avec Windows, des raccourcis et des réglages, puis suppression du dossier d'installation
quelques secondes après la fermeture. FileZilla reste installé. Le journal est conservé.

## Lanceur et application

`FileZillaUpdater.exe` est un lanceur .NET Framework 4.8 (présent sur tout Windows 10 et 11)
qui embarque l'application WPF `FileZillaUpdater.App.exe` (.NET 10). À chaque démarrage, il
vérifie le runtime .NET Desktop 10, extrait l'application dans `%LOCALAPPDATA%\FileZillaUpdater`
si elle manque ou diffère, puis la démarre avec `/lanceur "<chemin du lanceur>"`.

## Construction

Prérequis : SDK .NET 10.

```
build.cmd
```

Produit `dist\FileZillaUpdater.exe`.

Tests (hors écran, sans toucher au registre ni au Bureau réels, sans jamais lancer
d'installeur) :

```
dotnet test tests\FileZillaUpdater.Tests
```

Avec la variable `FILEZILLA_NUPKG` pointant vers un vrai paquet `filezilla.<version>.nupkg`
téléchargé à part, un test supplémentaire en extrait l'installeur et contrôle sa signature
réelle (sans l'exécuter).

## Structure

```
build.cmd                         construction du livrable
Directory.Build.props             version et réglages communs
assets\icon.ico, icon.png         icône (générée par build_tools\MakeIcon.cs)
src\Shared\                       arguments, emplacements, traductions (lanceur et application)
src\FileZillaUpdater.Core\        logique sans interface et modèles de vue
    Chocolatey                     dernière version, téléchargement et extraction du paquet
    Signature                      contrôle Authenticode de l'installeur
    FileZilla                      installation détectée dans le registre, arguments NSIS
    UpdateService                  vérification et installation
    SelfUpdate, GitHubRelease      mise à jour de FileZillaUpdater lui-même
    Setup, Shortcuts, AppSettings  installation de l'outil, raccourcis, réglages
    ViewModels\                    fenêtre de réglages, boîtes de dialogue, proposition
src\FileZillaUpdater\             application WPF (FileZillaUpdater.App.exe)
src\FileZillaUpdater.Launcher\    lanceur net48 (FileZillaUpdater.exe)
tests\FileZillaUpdater.Tests\     tests xUnit
```

## Licence

Distribué sous licence MIT (voir le fichier `LICENSE`).
