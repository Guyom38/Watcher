# Watcher

Surveillance des accès aux fichiers de vos disques, en tâche de fond, avec icône
dans la zone de notification et interface WPF à fond de vagues animées.

## L'exe

`dist\Watcher.exe` — un seul fichier, autonome (68 Mo, le runtime .NET est embarqué).
Rien à installer, il est copiable où vous voulez.

Au premier lancement il se replie directement dans la zone de notification, la
surveillance à l'arrêt. Clic gauche sur l'icône pour ouvrir la fenêtre.

## Les deux moteurs de capture

C'est le point à comprendre pour bien s'en servir.

| | Sans administrateur | **En administrateur** |
|---|---|---|
| Moteur | `FileSystemWatcher` | **Session ETW noyau** |
| Écritures, créations, suppressions, renommages | oui | oui |
| **Lectures de fichiers** | non | **oui** |
| **Processus responsable (nom + PID)** | non | **oui** |

Savoir *qui* accède à un fichier n'est possible qu'en interrogeant le noyau via ETW,
ce qui exige l'élévation. Watcher démarre donc sans UAC en mode dégradé, et propose
un bouton **Mode administrateur** (panneau de gauche et onglet Paramètres) qui le
relance élevé. Le bandeau orange de l'onglet Paramètres disparaît une fois élevé, et
un badge `ADMIN` apparaît dans la barre de titre.

Le moteur réellement actif est toujours affiché dans la barre de titre, dans le
panneau de gauche, et écrit dans le journal au démarrage.

## L'interface

**Tableau de bord** — quatre compteurs (accès capturés et cadence, fichiers distincts,
processus, événements écartés et perdus), le flux en direct horodaté avec pastille de
couleur par type d'accès, et le classement des processus les plus actifs. L'interrupteur
du flux le gèle pour pouvoir lire tranquillement.

**Activité des fichiers** — un fichier par ligne : nombre d'accès, détail
lectures / écritures / suppressions, date et heure du dernier accès, dernière action,
processus accédants et PID, dossier. Recherche libre, filtre par disque et par type
d'accès. Sélectionner une ligne ouvre un volet d'inspection avec le chemin complet, le
premier et le dernier accès, et le détail de tous les accédants avec leur nombre de hits.
Clic droit : ignorer le fichier, ignorer le dossier, ouvrir l'emplacement, copier le chemin.
`CSV` exporte les lignes affichées (avec BOM UTF-8, Excel garde les accents).

### Le menu contextuel

Un clic droit sur une ligne d'activité ouvre deux sous-menus symétriques, **★ Surveiller**
et **Ignorer**, construits à la volée d'après la ligne visée :

```
★ Surveiller  ▸   Le fichier    —  rapport.txt
                  Le dossier    —  C:\Users\Moi\Documents\Projets\app\src
                  ── Dossiers parents ──
                       C:\Users\Moi\Documents\Projets\app
                       C:\Users\Moi\Documents\Projets
                       C:\Users\Moi\Documents
                       C:\Users\Moi
                       C:\Users
                       C:\
                  ─────────
                  Le processus  —  devenv.exe
                  Le processus  —  MSBuild.exe
Ignorer       ▸   (structure identique)
```

La **chaîne des dossiers parents** évite de devoir remonter à la main quand la ligne est
profondément enfouie : un clic suffit pour viser le bon niveau. Les chemins longs sont
raccourcis par le milieu pour garder le menu lisible.

Les **processus** listés sont ceux qui ont réellement touché ce fichier. Ils ne sont
disponibles qu'avec le moteur ETW ; sans lui, l'entrée l'indique explicitement plutôt que
de rester vide.

Sur une sélection multiple, « Le fichier » s'applique à toutes les lignes ; un dossier ou un
processus reste une cible unique et explicite.

### Surveillance ciblée

L'onglet dédié liste vos cibles — dossiers, fichiers et processus. Chacune affiche son
nombre d'accès, ses fichiers distincts, son dernier accès et son accédant principal ; la
sélectionner filtre le tableau de droite sur elle seule. Pour une cible processus, les
compteurs ne retiennent **que les accès de ce processus**, pas tous ceux des fichiers qu'il
touche.

Dans l'onglet Activité, les lignes surveillées sont **mises en surbrillance** : fond ambré,
liseré à gauche et libellé en demi-gras, plus une colonne **★** triable. Le liseré reste
visible au survol et à la sélection.

« Surveiller » ne se contente pas de filtrer — la cible est **garantie observée** :

- si un motif d'exclusion la bloquait, il est **levé** (sinon la cible resterait muette) ;
- si elle n'était pas dans la portée de capture, la portée est **étendue** ;
- si la portée était **Rien**, elle bascule en **Sélection spécifique** ;
- surveiller un processus explicitement ignoré le **retire des processus ignorés**, et
  inversement — les deux listes ne peuvent pas se contredire.

Chaque ajustement est indiqué à l'écran et écrit dans le journal, pour qu'aucun changement
de configuration ne soit silencieux. Clic droit → **Retirer de la surveillance ciblée**
dépingle ; la portée et les exclusions ne sont alors pas retouchées.

### Ignorer un processus

C'est le filtre le plus efficace : il s'applique **avant toute analyse de chemin**, par une
simple recherche dans un ensemble de noms. Idéal pour faire taire un service bavard sans
exclure les dossiers qu'il touche, souvent légitimes par ailleurs. Comme l'attribution des
processus, il nécessite le moteur ETW. Les lignes déjà collectées sont conservées — seuls
les accès suivants sont écartés. La liste se gère depuis Paramètres → **Processus ignorés**.

**Paramètres** — portée en trois modes : **Tout sélectionner** (tous les disques fixes),
**Rien**, ou **Sélection spécifique** avec une arborescence à cases tri-état sur les
disques. L'arbre se charge à la demande, dossier par dossier : ouvrir `C:` ne parcourt
pas le disque entier. Un dossier coché couvre tout son contenu ; une coche partielle
s'affiche en tiret. Puis les types d'accès à capturer, la liste des exclusions, et les
options d'application. Rien n'est appliqué avant **Appliquer et enregistrer**.

**Journal** — les lignes du journal en direct, filtrables par niveau, avec accès au
fichier du jour.

## Exclusions

Deux formes de motifs :

- **sans joker** → préfixe de chemin. `D:\Jeux` exclut le dossier et tout son contenu ;
  `C:\dump.bin` exclut ce seul fichier. La frontière de dossier est respectée :
  `C:\Temp` n'exclut pas `C:\Temporaire`.
- **avec `*` ou `?`** → joker appliqué au chemin complet, insensible à la casse.
  Par exemple `*\AppData\Local\Temp\*` ou `*.tmp`.

Une quinzaine de motifs par défaut écartent déjà le bruit connu (WinSxS, Prefetch,
pagefile, corbeille, dossiers temporaires…). Le bouton **Valeurs par défaut** les
rétablit.

Ajouter une exclusion depuis l'onglet Activité l'applique immédiatement et retire du
tableau les lignes désormais couvertes — pas besoin de repasser par Paramètres.

## Emplacement des données

`%LOCALAPPDATA%\Watcher\`

- `settings.json` — configuration, éditable à la main
- `logs\watcher-AAAA-MM-JJ.log` — un fichier par jour, purge automatique au-delà de 30 jours
- `exports\` — les CSV

Ces chemins sont toujours exclus de la surveillance : sans cela, l'écriture du journal
déclencherait un accès qui serait journalisé à son tour, en boucle.

## Notes de fonctionnement

- **Volume.** Une session ETW sur un disque entier peut produire des dizaines de
  milliers d'événements par seconde. La file de capture est bornée à 200 000 entrées ;
  au-delà les événements sont abandonnés et comptés dans la vignette
  « événements écartés ». Le tableau est plafonné (20 000 lignes par défaut) et retire
  les accès les plus anciens.
- **Tri.** Le tableau est retrié toutes les deux secondes, jamais à chaque lot, et le
  tri se gèle dès qu'une ligne est sélectionnée : les lignes ne bougent pas sous le curseur.
- **Fond animé.** Recalculé à 25 images/s, et complètement arrêté dès que la fenêtre
  n'est plus visible — replié dans le tray, l'app ne consomme rien pour l'animation.
  Désactivable dans Paramètres.
- **Ouverture de la fenêtre.** Les événements de l'icône de notification arrivent à
  l'intérieur d'un rappel Win32 (`NotifyIcon.WmMouseDown`). Appeler `Window.Show()` à cet
  endroit le rendait réentrant : `Show()` pompe des messages pendant qu'il crée sa fenêtre,
  un clic en attente relançait `Show()` sur la même fenêtre, et WPF échouait avec
  « Le Visual racine d'un VisualTarget ne peut pas avoir de parent ». Résultat : une fenêtre
  Win32 sans contenu attaché, donc **entièrement noire**, et des fenêtres qui s'empilaient.
  L'ouverture est désormais toujours différée via le Dispatcher, protégée par un verrou de
  réentrance, `Show()` n'est appelé que sur une fenêtre non visible, et une fenêtre dont
  l'affichage échoue est fermée au lieu de rester à l'écran.
- **Rendu logiciel.** Paramètres → **Rendu logiciel** bascule le tracé sur le processeur.
  Utile si WPF n'arrive plus à allouer sa surface de rendu sur le GPU (mémoire vidéo
  saturée, perte du périphérique Direct3D). L'option prend effet immédiatement, sans
  redémarrage. Ce n'est pas le correctif de la fenêtre noire décrite ci-dessus.

  Coût mesuré sur cette machine, fenêtre 1320×820 :

  | Mode | Charge processeur |
  |---|---|
  | Matériel (GPU) | ~14 % d'un cœur |
  | Logiciel, qualité pleine | ~64 % d'un cœur |
  | **Logiciel, allégé automatiquement** | **~35 % d'un cœur** |

  En rendu logiciel l'animation passe donc d'elle-même en mode économe (3 nappes au lieu
  de 5, 12 images/s, tracé plus grossier) — visuellement indiscernable. Pour descendre
  à zéro, décochez **Fond animé**.

  Watcher force également le rendu logiciel de lui-même si Windows ne rapporte aucune
  accélération matérielle utilisable, et l'écrit dans le journal.
- **Session ETW résiduelle.** Une session noyau survit au processus qui l'a créée. Après
  un arrêt brutal, Watcher détecte et ferme la session restante au démarrage suivant.
  Si un autre outil de trace (WPR, Perfmon, Process Monitor) la détient, Watcher bascule
  sur `FileSystemWatcher` et l'indique dans le journal.
- **Fermeture.** La croix replie dans la zone de notification. Pour quitter réellement :
  clic droit sur l'icône → **Quitter**.

## Compiler

```
dotnet build      # développement -> bin\Debug\net9.0-windows\Watcher.exe
publish.cmd       # exe autonome  -> dist\Watcher.exe
```

.NET 9 SDK requis pour compiler. Rien n'est requis pour exécuter l'exe publié.

**Publiez toujours via `publish.cmd`.** Le `RuntimeIdentifier` y est passé en ligne de
commande, jamais déclaré dans le `.csproj` : déclaré sans condition, il s'appliquerait
aussi à `dotnet build`, qui déplacerait sa sortie vers `bin\Debug\net9.0-windows\win-x64\`
en laissant un exécutable périmé à l'ancien emplacement — des builds « réussis » qui ne
mettent plus à jour le binaire qu'on lance.
