# Sir Correctifs Pulsar

Mod client Pulsar de Sirius Immersion.

Greffon facultatif, installé par les joueurs volontaires (d'abord Guesn et RougeSable) depuis le catalogue Pulsar « Sirius ». Il relève **chez le joueur** ce que le serveur ne peut pas voir :

- quand ses **marqueurs de minerai** apparaissent et disparaissent, et pourquoi ils tardent ;
- ce que ses **écrans** affichent vraiment (« Online », « Offline » ou leur vrai contenu), au moment où l'image est montrée, et pourquoi ils attendent ;
- comment se chargent les modèles de l'**hologramme** du bloc en main et des **blocs en chantier** tout proches, qui restent parfois invisibles (cas de Guesn).

Ces relevés partent vers le serveur (PU ou PTU), où **Sir Correctifs Torch** les écrit, un fichier par jour, dans `Instance/sir-correctifs-torch/minerai/`, `Instance/sir-correctifs-torch/ecrans/` et `Instance/sir-correctifs-torch/blocs/` (ticket #1452). On a ainsi la réponse en regardant les joueurs jouer.

Le greffon **ne change rien au jeu** : il observe seulement ce que le jeu du joueur calcule déjà, sans jamais relire le terrain, recalculer un écran ni recharger un modèle. Ce qu'il voit hors du fil du jeu (fils de travail, fil de l'affichage) passe par une file sans verrou, vidée par le fil du jeu ; rien n'est écrit depuis le fil de l'affichage. Un joueur sans le greffon joue normalement.

## S'en servir

1. Dans Pulsar, catalogue « Sirius », cocher **Sir Correctifs Pulsar**, puis relancer le jeu.
2. Se connecter à PU ou à PTU et jouer comme d'habitude. Il n'y a rien à régler.

Pour l'arrêter, le décocher dans Pulsar. Hors de PU et de PTU (solo, autre serveur), rien n'est relevé ni envoyé.

Au chargement, le journal du jeu porte la ligne « Sir Correctifs Pulsar chargé », puis le nombre d'accroches posées. Cinq minutes après la connexion, une ligne « contrôle des accroches » dit, pour chaque suivi, les accroches que le jeu n'a encore jamais appelées.

## Ce qui est relevé

Chaque ligne est écrite au moment même de l'événement, chez le joueur :

```
2026-11-19T13:56:02.123Z image=81234 joueur=76561198000000001 pseudo="Guesn" evenement="marqueur apparu" minerai=Magnésium distance=412.5 detecteur=bloc bloc="Détecteur 3500" grille="Base de Guesn" grille_id=1234 portee=3500 antenne=oui
```

- l'heure en UTC à la milliseconde et `image` (le compteur d'images de la partie) : deux joueurs se comparent à la milliseconde ;
- le joueur, par son identifiant Steam et son pseudo ;
- l'événement, puis ses champs `clé=valeur` (entre guillemets quand la valeur a une espace).

On ne note que les **changements** : un état qui se répète ne fait jamais de ligne.

### Minerai (`minerai/`)

| Événement | Quand | Champs principaux |
|---|---|---|
| `partie prête` | à chaque connexion, quand le joueur a la main (aussi dans `ecrans/` et `blocs/`) | `chargement_ms` |
| `accès du détecteur` | un détecteur devient (ou cesse d'être) calculé chez le joueur | bloc, grille, portée, `antenne`, `acces_local`, `en_marche`, `groupe_logique`, `acces_au_groupe`, `joueur_sur_la_grille`, `personnage`, `calcule_chez_le_joueur` (la condition même du jeu : en marche, accès, personnage, puis accès au groupe logique par antenne, ou joueur sur la même grille sans antenne) |
| `début de passage` | un détecteur (ou la foreuse) lance un passage complet | détecteur, portée, `cellules`, `marqueurs`, `appels_occupe`, `appels_sautes` |
| `fin de passage` | le passage est complet, juste avant que les marqueurs changent | détecteur, portée, `duree_ms`, `requetes` (les quatre, la dernière comprise), `attente_file_max_ms`, `travail_ms`, `processeur_ms`, `gisements`, `apparus`, `disparus`, `marqueurs_du_hud` |
| `marqueur apparu` / `marqueur disparu` | un marqueur de minerai apparaît ou disparaît | `minerai`, `distance` (au joueur), `detecteur` (`bloc` ou `foreuse portative`), `bloc`, `grille`, `portee`, `antenne`, `cause` |
| `nettoyage des marqueurs` | le jeu retire d'un coup les marqueurs d'un détecteur | `cause` (perte d'accès, alimentation, bloc éteint, fermeture du bloc, voxel sorti de portée, foreuse rangée...), `requetes_orphelines`, `marqueurs_retires` |
| `requête jetée` | une requête finit sur un détecteur déjà nettoyé | `requetes_restantes` |
| `passage en attente` | la session n'est pas prête pour un passage | `raison` |
| `foreuse en main` / `foreuse` / `foreuse rangée` | le joueur prend sa foreuse portative, son alimentation change, il la range | `alimentee`, `courant`, `iem`, `portee` |
| `file très basse` | au plus une fois par seconde, quand elle change | `file_tres_basse` (tâches en attente, la même file que les textures et les modèles), `fils_tres_bas`, `fils_occupes`, `requetes_en_vol`, `ecrans_en_attente_de_rendu`, `rendus_d_ecran_en_vol`, `composition` (sortes de tâches en attente, au plus toutes les 10 s) |
| `affichage des marqueurs` | l'affichage du HUD change | `marqueurs_visibles`, `hud_visible`, `hud_minimal`, `iem` |

La foreuse portative passe dans la même file que les détecteurs : son passage (quelques cellules) mesure directement l'attente dans la file. Ses marqueurs sont reconnus à son composant de détection (`detecteur="foreuse portative"`).

### Écrans (`ecrans/`)

Chaque écran à portée du joueur (panneaux, écrans des cockpits, des blocs programmables et des autres blocs) est relevé jusqu'à la **portée de son bloc plus 10 m** : 130 m pour la plupart, 70 m pour les LCD d'angle, inclinés, encastrés, holographiques et de console, 410 m pour les grands LCD sci-fi. Au-delà, rien.

| Événement | Quand |
|---|---|
| `écran relevé` | l'écran entre dans la zone relevée |
| `affichage demandé` | le jeu demande d'afficher autre chose : `demande` vaut `Online`, `Offline`, `contenu` (image dessinée), `image`, `vide` ou `aucun` (texture du modèle, cockpits et blocs programmables) |
| `affichage` | ce que l'écran montre **vraiment** change : le fil de l'affichage vient d'échanger sa texture (pour un contenu dessiné, après le rendu hors écran ; pour une image, après son chargement). L'heure de la ligne est celle de l'échange |
| `attente` | la raison de l'attente change |
| `contenu changé` | la sorte de contenu ou le script change |
| `entrée dans les 32 m` / `sortie des 32 m` | le joueur passe la limite des morceaux d'image |
| `premier morceau dans les 32 m` | le premier morceau d'image arrive du serveur dans ce rayon |
| `réglages graphiques` | à la connexion et à chaque changement : `textures`, `textures_des_voxels`, `modeles`, `voxels`, `filtrage_anisotrope`, `ombres`, `multiplicateur_de_portee_des_ecrans` |

Chaque ligne d'écran porte : `bloc`, `type`, `ecran`, `construction` et `construction_id`, `contenu` (texte, image, texte et image, script, programme d'écran d'un mod, image du serveur), `script`, `distance`, `portee`, `portee_effective` (portée × qualité des textures), `affichage`, `raison`, et selon le cas :

- `affichage_precedent` et `duree_precedent_ms` : ce que l'écran montrait avant, et combien de temps ;
- `demande_a` et `ecart_demande_ms` : quand le jeu a demandé ce qui est maintenant montré, et l'écart entre la demande et l'échange réel ;
- `rendu_en_file_ms`, `rendu_lance_ms`, `texture_prete_ms` (comptés depuis la demande), `file_hors_ecran` et `rendus_en_vol` : où le rendu hors écran a attendu (5 rendus en vol au plus, dans la file « très basse ») ;
- `attente_a_portee_ms` : à l'échange du vrai contenu, depuis combien de temps l'écran était à portée ; la `distance` de la ligne dit où il s'est allumé à l'approche ;
- `raison` : `hors portée`, `pas encore admis au dessin` (les 6 passages de la réserve), `objet de rendu pas prêt`, `bloc vu sans courant` (avec `alimentation` : allumé, fonctionnel, alimenté, demande, état du réseau, grilles du groupe), `réserve de dessin pleine`, `bloc endommagé`, `raison illisible` (le jeu n'a pas pu être lu : le greffon envoie alors un rapport d'erreur) ;
- `dernier_calcul` et `duree_calcul_ms` : le dernier calcul d'un écran à script chez le joueur ;
- `dernier_morceau` et `morceaux_32m` : pour une image venue du serveur, le dernier morceau reçu et le nombre de morceaux depuis l'entrée dans les 32 m ; `image_peut_etre_perimee=oui` quand un morceau est arrivé alors que le joueur était à plus de 32 m.

### Blocs invisibles (`blocs/`)

Seulement pour l'hologramme du bloc en main et les blocs à moins de 15 m dont la construction n'est pas finie (au plus 64 objets à la fois). Chaque ligne porte `objet` (l'identifiant de rendu), `sorte` (`hologramme` ou `bloc`), `modele`, et pour un bloc `bloc` et `bloc_id`.

| Événement | Quand | Champs principaux |
|---|---|---|
| `objet de rendu créé` | le jeu crée l'objet d'affichage de l'hologramme ou du bloc | `nom`, `transparence`, `fondu`, `avancement`, `distance` |
| `aspects préchargés` | après l'ajout d'un bloc à la scène, le jeu demande le préchargement de ses aspects de chantier | `aspects_precharges` |
| `aspect du bloc` | une soudure change le modèle ou l'objet de rendu du bloc | `modele`, `modele_precedent`, `objet`, `objet_precedent`, `avancement`, `transparence` |
| `modèle demandé` | l'affichage cherche le modèle : déjà chargé, ou modèle d'attente (`Models\Debug\LoadingQuad.mwm`) | `modele_d_attente` |
| `modèle chargé` / `chargement du modèle en échec` | fin du chargement du modèle, sur un fil de travail | `duree_ms`, `etat_de_chargement`, `valide`, `exception`, `message` |
| `bascule des objets vers le modèle chargé` | l'affichage va remplacer le modèle d'attente | `chemin_complet`, `fondu` |
| `bascule vers le modèle chargé` / `bascule vers le modèle chargé ratée` | pour chaque objet suivi au rechargement | `meme_chemin`, `casse_differente` (preuve du piège de la casse), `attente_avant`, `attente_apres` |
| `modèle posé` / `bascule ratée` | le modèle est posé (ou refusé) sur l'objet | `modele_pose`, `modele_d_attente`, `reussi` |
| `passage à l'ancien chemin d'affichage` | l'objet est converti à l'ancien chemin, qui peut ne rien dessiner | |
| `objets d'affichage reconstruits` | l'ancien chemin reconstruit ses niveaux de détail | `niveaux_de_detail`, `rien_a_dessiner` |
| `état d'affichage` | toutes les 45 images d'affichage, quand l'état de l'objet change | `visible`, `boite` (une boîte de la taille du modèle d'attente le trahit), `chemin` (`nouveau` ou `ancien`), `modele_d_attente`, `modele_de_l_objet`, `modele_valide`, `etat_de_chargement`, `detail`, `detail_en_transition`, `etat_de_detail`, `tramage`, `dessine`, `ecarte`, et les réglages `detail_min`, `decalage_de_detail`, `multiplicateur_de_distance`, `detail_min_au_chargement` |
| `objet de rendu retiré` | l'objet n'existe plus côté affichage | |

## Contrôle du greffon

Les méthodes du jeu d'une seule ligne ne sont jamais accrochées : le jeu peut les intégrer à leurs appelants, et l'accroche ne partirait jamais. Chaque accroche compte ses appels ; cinq minutes après la connexion, la ligne `contrôle des accroches` de chaque dossier dit celles qui ne l'ont jamais été (`jamais_appelees`) et celles qui n'ont pas pu être posées (`non_posees`). Toute erreur rattrapée par le greffon (un champ du jeu illisible, par exemple) donne une ligne au journal du jeu la première fois, puis un rapport d'erreur avec son nombre de répétitions.

## Ce que le greffon ne peut pas savoir

- Ce que la carte graphique dessine vraiment (pixels, tramage, occlusion) : le greffon voit l'échange des textures et le chargement des modèles côté affichage, pas l'image finale. Il faudrait une capture RenderDoc.
- Les envois de morceaux d'écran manqués : ni numéro de séquence ni accusé de réception.
- Le pourquoi côté serveur d'un accès ou d'une alimentation qui change.
- Le code interne des scripts d'écran des mods.
- Les détecteurs hors distance de réplication : ils n'existent pas chez le joueur.

## Accord avec Sir Correctifs Torch

- Canal réseau des relevés : **47815**, du joueur vers le serveur (`SendMessageToServer`, envoi fiable). Aucun autre produit du studio ne l'utilise.
- Un paquet est un texte UTF-8 sans marque d'ordre des octets, de 8 000 octets au plus. Sa première ligne est `sir-correctifs-pulsar releves 1` ; chaque ligne suivante commence par le dossier (`minerai`, `ecrans` ou `blocs`), une espace, puis la ligne du relevé telle qu'elle doit s'écrire.
- Les paquets partent quatre fois par seconde. Si le joueur est coupé du serveur, au plus 20 000 lignes attendent ; au-delà, une ligne `lignes perdues` dit combien ont été jetées.

## Rapports d'erreurs

Quand le joueur est sur PU ou PTU, le greffon envoie ses propres erreurs et les plantages du jeu au studio, sur le canal commun des greffons côté joueur (5465479001, soit 38745 pour le jeu) : un JSON `{ greffon, version, genre, message, pile, premiere, derniere, nombre, session, lignes }`. La même erreur ne part qu'une fois par session, avec son nombre. Un plantage se reconnaît au démarrage suivant (marque `session.marque` dans `%AppData%\SpaceEngineers\Storage\sir-correctifs-pulsar`) : le rapport reprend le bloc CRASH INFO du jeu, l'exception et sa pile, les 20 dernières lignes d'erreur du jeu et les 20 dernières du greffon. Ces rapports ne portent **aucune** donnée sur le joueur ; hors de nos serveurs, ils attendent dans `Rapports\` la prochaine connexion.

## Construire

```
dotnet build sir-correctifs-pulsar.csproj          # le greffon (dossier Bin64 du jeu : voir Directory.Build.props.example)
dotnet test tests/tests.csproj                     # les tests de la logique, sans le jeu
```

Pulsar compile lui-même le dossier `Source` (fiche `PluginHub/sir-correctifs-pulsar.xml`).

Il évolue par tickets dans le studio sir-studio3 : la branche `main` porte le travail accepté en qualification, la branche `qualification` celui qui attend la sienne. Chaque ticket qui change ce que fait le produit tient ce README à jour.
