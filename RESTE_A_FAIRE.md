# Reste à faire

## À lancer par Jean

- Essai réel sur un poste où FileZilla peut être mis à jour : `dist\FileZillaUpdater.exe`, installation, puis
  "Vérifier maintenant" et "Installer" (UAC, fermeture et relance de FileZilla, version relue).
- Confirmer que l'installeur NSIS de FileZilla accepte `/user=all` et `/user=current` avec `/S` (le script
  Chocolatey ne passe que `/S`, `/user=` vient de l'installeur NSIS de FileZilla et reste à voir sur une
  vraie installation en portée utilisateur).

## Publication (accord de Jean à demander avant chaque étape)

- Créer le dépôt public `jeanbrowaeyspro/FileZillaUpdater` et pousser `main`.
- Publier la release v1.0.0 avec `dist\FileZillaUpdater.exe`.

## À décider

- Factoriser avec GreenshotUpdater (lanceur, Setup, SelfUpdate, réglages, dialogues, thème) : bibliothèque
  commune ou sous-module, ce qui demande de modifier GreenshotUpdater.
- Mise à jour silencieuse de l'outil lui-même : garder le remplacement sans demande (comme GreenshotUpdater)
  ou demander l'accord.
