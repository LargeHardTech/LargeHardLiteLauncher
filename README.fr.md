# LargeHardLiteLauncher

[简体中文](README.md) | [English](README.en.md) | Français | [Русский](README.ru.md) | [微软式中文](README.zh-ms.md)

Un lanceur Minecraft qui s'exécute dans le terminal. Écrit en C# avec
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui), compilé par NativeAOT en un seul exe,
sans dépendance au runtime .NET et sans interface graphique.

La disposition reprend celle de [PCL2](https://github.com/Hex-Dragon/PCL2) ; la partie
multijoueur reprend l'intégration Terracotta de
[HMCL](https://github.com/HMCL-dev/HMCL).

## Fonctionnalités

**Lancement**

- Gestion de plusieurs répertoires de jeu, liste des versions triée par segments numériques
- Réglages par version : mode mémoire, Java, arguments JVM, arguments de jeu, taille de
  fenêtre, mode d'isolation, commande de pré-lancement
- Les bibliothèques manquantes sont téléchargées automatiquement avant le lancement (avec
  repli sur un miroir) ; une bibliothèque absente ne bloque plus le démarrage
- Java est choisi comme la plus petite version majeure satisfaisant l'exigence ; un Java 32 bits
  réduit le tas et affiche un avertissement
- Le processus de jeu est créé avec `DETACHED_PROCESS`, fermer la fenêtre du terminal
  n'arrête donc pas le jeu
- La session est écrite dans `running-game.json` ; en relançant le lanceur, un jeu encore en
  cours est reconnu
- L'analyse des plantages associe les signatures du journal à une cause et une suggestion
  (14 règles)
- Page d'accueil personnalisable : texte prédéfini, fichier local ou URL distante, avec les
  variables `${date}` `${player}` `${version}` et d'autres

**Téléchargement et installation**

- Installation vanilla, source au choix (officielle / miroir BMCLAPI / automatique)
- Installation des loaders : Forge, NeoForge, Fabric, Quilt
- Recherche et installation de ressources communautaires : mods, modpacks, datapacks, packs
  de ressources, shaders (Modrinth sans clé ; CurseForge nécessite la vôtre)
- Téléchargements reprenables : un échec réseau conserve le fichier `.tmp` pour la tentative
  suivante, seule une erreur de somme de contrôle le supprime
- Les dépendances obligatoires sont téléchargées avec le mod
- Installation de modpacks (`.mrpack`) : installe le loader correspondant et extrait les overrides
- Export d'une version en `.mrpack`, avec mise en correspondance en ligne facultative pour
  réduire la taille (mesuré : un pack de 62 mods passé de 518 Mo à 186 Mo)

**Gestion des versions**

- Gestionnaire de mods : analyse du répertoire mods, activation/désactivation par lot
  (renommage en `.disabled`), suppression, recherche de mises à jour
- Vérification des mises à jour de mods : recherche de la dernière version sur Modrinth par
  SHA1 du jar, mise à jour en une étape
- Gestion des packs de ressources et des shaders : lecture et écriture de l'état réel
  d'activation dans `options.txt` et `optionsshaders.txt`
- Vérification des fichiers : contrôle du jar client, des bibliothèques, des natives et des
  ressources d'après le json de version
- Suppression d'une version (avec ses sauvegardes et sa configuration)

**Multijoueur**

- Terracotta est intégré ; création ou rejoindre une salle sans téléchargement séparé
- La liste des nœuds relais publics est récupérée automatiquement

**Lancement de JAR**

- Exécution autonome de n'importe quel jar (serveur, client ou autre programme Java)
- Configuration graphique de la mémoire, des préréglages GC (G1 / ZGC / Parallel / Serial),
  des arguments JVM et de programme supplémentaires, du mode serveur
- Un champ de commande envoie directement l'entrée sur l'entrée standard du processus
- Passer un chemin de jar en ligne de commande, ou déposer un jar sur l'exe, ouvre directement
  cette page

## Interface

Six pages, commutées avec F1 à F6 :

```
Lancement | Téléchargement | Multijoueur | JAR | Réglages | Plus
```

- **Lancement** : compte, liste des versions, bouton de lancement, zone d'accueil
- **Téléchargement** : vanilla, loaders, ressources communautaires
- **Multijoueur** : création et connexion aux salles
- **JAR** : lancement autonome de jar
- **Réglages** : compte, Java, mémoire, isolation, source de téléchargement, apparence,
  performances
- **Plus** : guides, remerciements, petits outils (dont le puits à échos, alimenté par un fil Discussions)

## Configuration requise

- Windows 10 ou ultérieur (la création de processus et l'énumération de fenêtres utilisent
  des API Win32)
- Java est nécessaire pour lancer le jeu, ou téléchargez-en un depuis Réglages → Java
- Terminal de 100 colonnes recommandé, 80 colonnes minimum

## Compilation

Nécessite le SDK .NET 10 et une chaîne d'outils MSVC (utilisée par l'éditeur de liens NativeAOT).

```bash
python scripts/build.py build      # compilation Debug, conserve toutes les entrées d'auto-test
python scripts/build.py publish    # publication Release, sortie dans bin/Release/net10.0/win-x64/publish/
```

`scripts/build.py` gère trois choses : ignorer la restauration, nettoyer les variables d'environnement
MSYS, et ajouter `IlcUseEnvironmentalTools` (pour qu'ILCompiler utilise la chaîne d'outils de
l'environnement au lieu de détecter Visual Studio par le registre).

La compilation Debug contient les entrées d'auto-test, la Release non :

```bash
LargeHardLiteLauncher.exe --uitest        # construction des six pages
LargeHardLiteLauncher.exe --selftest      # 31 blocs d'assertions (interdit sur le binaire AOT)
LargeHardLiteLauncher.exe --vmtest        # navigation du gestionnaire de versions
LargeHardLiteLauncher.exe --packcfgtest   # lecture-écriture de la configuration des packs
LargeHardLiteLauncher.exe --updatetest <répertoire de version>   # vérification des mises à jour
LargeHardLiteLauncher.exe --launchtest <répertoire de version>   # lance le jeu et suit le journal
```

## Arborescence

```
Core/            Logique indépendante de l'interface
  Config.cs          configuration (sérialisation SourceGenerator)
  VersionScanner.cs  analyse du json de version, évaluation des rules
  GameLauncher.cs    chaîne de lancement : arguments, natives, classpath, analyse de plantage
  GameDownloader.cs  plan de téléchargement et reprise
  LoaderApi.cs       manifestes des loaders
  LoaderInstaller.cs installation Forge/NeoForge/Fabric/Quilt
  ModScanner.cs      analyse du répertoire mods et activation
  ModUpdateChecker.cs vérification des mises à jour de mods
  ModpackExporter.cs export .mrpack
  ModpackInstaller.cs installation .mrpack
  PackConfig.cs      état réel d'activation des packs et des shaders
  TerracottaService.cs processus multijoueur et interface REST
  JarLauncher.cs     lancement autonome de jar
  DetachedProcess.cs création de processus DETACHED_PROCESS
  GameSession.cs     enregistrement de la session de jeu en cours
  GameWindowFinder.cs recherche de la fenêtre de jeu par nom de classe
  HomePageLoader.cs  page d'accueil personnalisée et variables
  CrashAnalyzer.cs   analyse des journaux de plantage
  Localization.cs    textes de l'interface
UI/              Interface
  MainWindow.cs      fenêtre principale, changement de page
  PageBase.cs        classe de base des pages et PageId
  LaunchPage.cs      page de lancement
  VersionManager*.cs boîte de gestion des versions (aperçu / réglages / mods / packs / shaders / vérification)
  DownloadPage.cs    page de téléchargement
  DownloadCommunity.cs recherche communautaire et détails
  DownloadLoaderInstall.cs page d'installation des loaders
  TerracottaPage.cs  page multijoueur
  JarLaunchPage.cs   page JAR
  SettingsPage.cs    page de réglages
  MorePage.cs        page Plus
  TaskPanel.cs       barre des tâches
  AccountManagerUi.cs boîte de gestion des comptes
Tests/           code d'auto-test (exclu des compilations Release)
  SelfTest.cs        31 blocs d'assertions
  InheritTest.cs     fusion de l'héritage de version
  LaunchTest.cs      lancement réel avec suivi du journal
  LoaderTest.cs      installation des loaders
  ModTest.cs         analyse du répertoire mods
  MsAuthTest.cs      corps des requêtes de connexion Microsoft
Resources/       ressources intégrées (archive du noyau Terracotta, texte AGPL)
scripts/         scripts de compilation et d'exécution
  build.py           point d'entrée de compilation, gère les particularités locales
  publish.bat        publication par double-clic sous Windows, supprime le pdb
  run.bat            simple dotnet run
Program.cs       point d'entrée et entrées d'auto-test
```

## Données et configuration

```
%USERPROFILE%\.largehardlitelauncher\
  config.json         configuration globale
  logs\               journaux du lanceur
  java\               Java téléchargé par le lanceur
  terracotta\         noyau multijoueur extrait
  cache\              cache de la page d'accueil
  running-game.json   le jeu en cours d'exécution
```

Les fichiers de jeu et les versions restent dans le répertoire `.minecraft` et ne sont pas touchés.

## Limitations connues

- Windows uniquement, pas de portage prévu (la création de processus et la recherche de
  fenêtres dépendent de Win32)
- La vérification des mises à jour de mods ne couvre que Modrinth ; l'API d'empreintes de
  CurseForge exige une clé et limite sévèrement le débit
- Le lanceur ne se met pas à jour lui-même ; changer de version signifie remplacer l'exe
- Instance unique ; un second lancement affiche seulement un message
- L'import de modpacks ne prend en charge que `.mrpack`, les modpacks zip de CurseForge ne
  sont pas pris en charge
- Le puits à échos lit GitHub Discussions via un point d'accès absent de la documentation
  REST officielle (60 requêtes/heure en anonyme) ; en cas d'échec, retour au cache puis aux
  phrases intégrées

## Questions fréquentes

**Pourquoi une interface en terminal ?**

Un lanceur Minecraft passe l'essentiel de son temps à télécharger, extraire et assembler des
arguments ; l'interface ne fait qu'en présenter le résultat. Une interface graphique doit en
plus gérer la mise à l'échelle DPI, le mode sombre, le contraste élevé, les lecteurs d'écran et
le repli de polices. Le terminal évite cette couche et fonctionne aussi à distance via ssh.

**Pourquoi 24 Mo alors que PCL fait 5 Mo ?**

La comparaison n'est pas équivalente. PCL dépend d'un .NET Desktop Runtime installé sur le
système (plusieurs centaines de Mo) et ne livre que le code applicatif. Ce projet est autonome :
tout le runtime est inclus, plus 7,75 Mo de noyau multijoueur intégré. À périmètre égal, le code
applicatif lui-même fait environ 158 Ko.

**Va-t-il supprimer mes sauvegardes ?**

Non. La seule fonction qui supprime des fichiers est « Supprimer la version », qui demande une
confirmation et précise que l'opération est irréversible. Désactiver un mod renomme le fichier ;
désactiver un pack de ressources ou un shader ne modifie qu'une ligne dans `options.txt`.

## Composants tiers

| Composant | Usage | Licence |
|---|---|---|
| [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) | framework d'interface terminal | MIT |
| [Terracotta](https://github.com/burningtnt/Terracotta) | noyau multijoueur, distribué intégré | AGPL-3.0 |
| [EasyTier](https://easytier.cn) | mise en réseau P2P sous-jacente | Apache-2.0 |

Le binaire Terracotta n'est pas modifié ; son code source se trouve dans le dépôt amont. Le
texte intégral de la licence est consultable dans le programme sous Plus → À propos.

Les données de téléchargement proviennent de [BMCLAPI](https://bmclapi2.bangbang93.com),
[Modrinth](https://modrinth.com), [CurseForge](https://www.curseforge.com) et
[Adoptium](https://adoptium.net) ; les noms chinois des mods se réfèrent à
[MCMod](https://www.mcmod.cn).

## Licence

Le code de ce projet est distribué sous [WTFPL v2](LICENSE), sans aucune garantie.

Exception : le binaire Terracotta intégré est distribué sans modification et reste soumis à
l'AGPL-3.0 amont. La licence de ce projet ne couvre que son propre code et ne modifie pas les
conditions de ce binaire.

## Références

- [PCL2](https://github.com/Hex-Dragon/PCL2) : disposition, gestion des versions, export de modpack
- [HMCL](https://github.com/HMCL-dev/HMCL) : règles d'expansion des arguments, installation des
  loaders, intégration Terracotta
