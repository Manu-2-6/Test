# Assistant d'installation de logiciels

Ce dépôt contient une application WPF (.NET 6) destinée à automatiser l'installation de logiciels essentiels (VLC, Google Chrome, Adobe Reader, etc.) sur un PC fraîchement installé sous Windows 11.

## Prérequis

1. **Windows 11** (ou Windows 10 avec `winget` disponible).
2. **Visual Studio 2022 Community** avec la charge de travail **"Développement .NET de bureau"**.
3. L'outil **`winget`** doit être accessible depuis l'invite de commandes (test : `winget list`).
4. (Optionnel) Droits administrateur pour lancer Visual Studio afin que `winget` puisse installer les logiciels.

## Ouvrir le projet

1. Cloner ce dépôt :
   ```powershell
   git clone https://github.com/<ton-compte>/<ton-repo>.git
   ```
2. Ouvrir `SoftwareSetup.sln` dans Visual Studio 2022 (**Fichier > Ouvrir > Projet/Solution**).
3. Laisser Visual Studio restaurer automatiquement les packages NuGet si nécessaire.

## Compiler et exécuter

1. Sélectionner la configuration `Debug` (ou `Release`) et la plateforme `Any CPU`.
2. Construire la solution (**Générer > Générer la solution** ou `Ctrl+Shift+B`).
3. Lancer l'application :
   - `F5` pour exécuter avec le débogueur.
   - `Ctrl+F5` pour exécuter sans débogueur.

La fenêtre principale affiche :
- Une liste de logiciels avec cases à cocher.
- Une case pour tout sélectionner.
- Un bouton "Installer" qui déclenche les installations.
- Un panneau de journal affichant la progression et les erreurs.

## Utiliser `winget`

- Chaque installation lance `winget install <ID>` en arrière-plan.
- Si une installation échoue, le journal affiche la sortie complète de `winget` pour t'aider à diagnostiquer le problème.
- Tu peux adapter la liste des logiciels dans `SoftwareSetupApp/Models/SoftwarePackage.cs`.

## Personnalisation future

- Ajoute de nouveaux logiciels en créant d'autres instances de `SoftwarePackage`.
- Personnalise l'interface (styles XAML, thèmes, icônes).
- Implémente un suivi d'état plus poussé (ex. redémarrage requis, vérification d'installation existante, etc.).

## Publication

1. Passe la solution en configuration `Release`.
2. Va dans **Générer > Publier SoftwareSetupApp** pour créer un exécutable prêt à être utilisé.
3. Choisis un profil `self-contained` si tu veux inclure le runtime .NET.

## Dépannage

- **Erreur : `winget` introuvable** → Vérifie l'installation de `winget` (Microsoft Store) ou mets à jour Windows.
- **Erreur d'accès refusé** → Lance Visual Studio en tant qu'administrateur.
- **Problèmes de compilation XAML** → Vérifie que tu utilises bien .NET 6 et que tous les packages sont restaurés.

Ces étapes te guideront pour tester et faire évoluer l'application même en étant débutant.
