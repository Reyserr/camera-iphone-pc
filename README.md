# Caméra iPhone → PC

Transforme ton iPhone en caméra pour ton PC Windows, gratuitement, comme DroidCam.
Version 1 : image en direct par **Wi-Fi**, affichée dans l'app PC, avec réglages à distance
(objectif, résolution, qualité, zoom, lampe).

À venir : webcam virtuelle (pour Discord, OBS, Zoom…) et connexion par câble USB.

| Dossier | Contenu |
| --- | --- |
| `ios/` | App iPhone (Swift). Compilée par GitHub, pas besoin de Mac. |
| `pc/` | App PC (C# / WPF, .NET 10). S'ouvre dans Visual Studio. |
| `.github/workflows/` | Scripts de compilation automatique sur GitHub. |
| `docs/protocole.md` | Format des messages entre les deux apps. |

---

## 1. Installer les outils (une seule fois)

| Outil | Lien | Remarque |
| --- | --- | --- |
| GitHub Desktop | https://desktop.github.com | Pour envoyer le code sur GitHub |
| Visual Studio 2026 Community | https://visualstudio.microsoft.com | Cocher « Développement .NET Desktop » |
| iTunes (version du site Apple) | https://www.apple.com/itunes/download/win64 | **Pas** la version Microsoft Store |
| iCloud (version du site Apple) | voir la FAQ de Sideloadly | **Pas** la version Microsoft Store |
| Sideloadly | https://sideloadly.io | Installe l'app sur l'iPhone |

Si iTunes ou iCloud viennent du Microsoft Store : désinstalle-les, installe les versions du site Apple, puis redémarre le PC.

## 2. Mettre le code sur GitHub

Le dépôt doit être **public** : les compilations sont alors gratuites et illimitées.

**Cas 1 — le dépôt n'existe pas encore sur GitHub**

1. Dans GitHub Desktop : **File › Add local repository…**, choisis ce dossier.
   Il propose « create a repository » : accepte.
2. Clique sur **Commit to main**.
3. Clique sur **Publish repository**, garde le nom `camera-iphone-pc` et **décoche « Keep this code private »**.

**Cas 2 — tu as déjà créé le dépôt vide `camera-iphone-pc` sur github.com**

1. Dans GitHub Desktop : **File › Clone repository…** › `camera-iphone-pc` › **Clone**.
2. Copie tout le contenu de ce dossier (y compris le dossier `.github`) dans le dossier cloné.
3. Clique sur **Commit to main**, puis **Push origin**.

À chaque envoi, GitHub compile automatiquement les deux apps (onglet **Actions** du dépôt, 3 à 5 minutes).

## 3. Récupérer l'app iPhone (.ipa)

1. Sur la page du dépôt, onglet **Actions** › **Build iOS** › clique sur la dernière exécution (coche verte).
2. En bas, section **Artifacts** : télécharge **CameraApp-ipa**.
3. Décompresse le zip : tu obtiens **CameraApp.ipa**.

Croix rouge à la place de la coche ? Ouvre l'exécution, copie le message d'erreur et envoie-le moi.

## 4. Installer l'app sur l'iPhone

1. Branche l'iPhone au PC, déverrouille-le, touche **« Se fier à cet ordinateur »**.
2. Ouvre Sideloadly, glisse **CameraApp.ipa** dedans, saisis ton identifiant Apple, clique sur **Start**.
3. Sur l'iPhone : **Réglages › Général › VPN et gestion de l'appareil** › ton identifiant › **Faire confiance**.
4. Sur l'iPhone : **Réglages › Confidentialité et sécurité › Mode développeur** › activer (l'iPhone redémarre).
5. Ouvre l'app **Caméra PC** et accepte les deux demandes : **caméra** et **réseau local**.

**Tous les 7 jours** (compte Apple gratuit), l'app ne s'ouvre plus : refais le point 2 ci-dessus avec le même `.ipa`.
Dans Sideloadly, l'option de rafraîchissement automatique peut le faire à ta place.

L'app demande iOS 17 ou plus récent.

## 5. Lancer l'app PC

Au choix :

- **Avec Visual Studio** : ouvre `pc/CameraPC.sln`, puis appuie sur **F5**.
- **Sans rien compiler** : onglet **Actions** › **Build PC** › dernière exécution › télécharge **CameraPC-windows**,
  décompresse, lance **CameraPC.exe**. Si Windows demande d'installer le « .NET Desktop Runtime 10 », accepte.

## 6. Première connexion

1. L'iPhone et le PC doivent être sur le **même Wi-Fi**.
2. Ouvre l'app sur l'iPhone : elle affiche une adresse, par exemple `192.168.1.42   port 5050`.
3. Dans l'app PC, saisis cette adresse dans **Adresse IP de l'iPhone**, laisse le port à 5050, clique sur **Se connecter**.
4. L'image apparaît. Les réglages à droite pilotent l'iPhone en direct.

Garde l'app ouverte sur l'iPhone pendant la diffusion : iOS coupe la caméra si l'app passe en arrière-plan
ou si l'écran se verrouille. L'écran reste allumé tout seul ; le bouton lune met l'écran en noir pour économiser la batterie.

---

## Dépannage

| Problème | Solution |
| --- | --- |
| « Pas de réponse » ou « Connection refused » | Vérifie le même Wi-Fi, l'app ouverte sur l'iPhone, et l'adresse saisie. |
| Toujours impossible de se connecter | iPhone : **Réglages › Confidentialité et sécurité › Réseau local** › active **Caméra PC**. |
| Ça ne marche pas sur un Wi-Fi d'école ou un réseau invité | Ces réseaux bloquent souvent les appareils entre eux. Essaie le partage de connexion de l'iPhone, ou attends le mode USB. |
| Sideloadly ne voit pas l'iPhone | Installe iTunes et iCloud du site Apple (pas Microsoft Store), redémarre, change de câble ou de port USB. |
| « Maximum App ID limit » dans Sideloadly | Limite Apple : 10 nouvelles apps par 7 jours. Ne change pas l'identifiant de l'app ; attends quelques jours. |
| L'app ne s'ouvre plus sur l'iPhone | Les 7 jours sont passés : réinstalle avec Sideloadly. |
| Image saccadée | Baisse la qualité JPEG ou passe en 1280 × 720 ; rapproche-toi de la box. |
