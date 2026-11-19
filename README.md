# LynxCam

Ton iPhone devient une webcam pour ton PC Windows, gratuitement, comme DroidCam.

- Connexion en **Wi-Fi** ou par **câble USB**
- **Webcam virtuelle « LynxCam »** visible dans OBS, Discord, Zoom, Teams, les navigateurs…
- **Arrière-plan** flou, thème (dont un fond vert pour OBS) ou ta propre photo
- **15, 24, 30 ou 60 images/s**, 720p ou 1080p, caméra arrière, ultra grand-angle ou avant, zoom, lampe
- **Réglages d'image** : exposition, mise au point, balance des blancs, luminosité, contraste, saturation, miroir
- **Micro de l'iPhone** (activable) envoyé au PC
- **Photo** et **vidéo** enregistrées dans la galerie de l'iPhone, déclenchées depuis le PC ou l'iPhone

| Dossier | Contenu |
| --- | --- |
| `ios/` | App iPhone (Swift). Compilée par GitHub, pas besoin de Mac. |
| `pc/` | App PC (C# / WPF, .NET 10). S'ouvre dans Visual Studio. |
| `.github/workflows/` | Compilations automatiques sur GitHub (iPhone et PC). |
| `docs/protocole.md` | Format des messages entre les deux apps. |

---

## 1. Installer les outils (une seule fois)

| Outil | Lien | Remarque |
| --- | --- | --- |
| iTunes (version du site Apple) | https://www.apple.com/itunes/download/win64 | **Pas** la version Microsoft Store. Sert à Sideloadly et au mode USB. |
| iCloud (version du site Apple) | voir la FAQ de Sideloadly | **Pas** la version Microsoft Store |
| Sideloadly | https://sideloadly.io | Installe l'app sur l'iPhone |
| Visual Studio 2026 Community (facultatif) | https://visualstudio.microsoft.com | Seulement pour modifier l'app PC (« Développement .NET Desktop ») |

## 2. Récupérer les deux apps

À chaque envoi de code, GitHub compile tout seul (onglet **Actions** du dépôt, 3 à 10 minutes). Connecté à GitHub, sur le **PC** :

1. **Actions › Build iOS** › dernière exécution (coche verte) › en bas, **Artifacts** › télécharge **LynxCam-ipa**, puis décompresse-le pour avoir **LynxCam.ipa**.
2. **Actions › Build PC** › dernière exécution › télécharge **LynxCam-windows**, puis décompresse-le dans un dossier fixe (par exemple `Documents\LynxCam`).

## 3. Installer l'app sur l'iPhone

1. Branche l'iPhone au PC, déverrouille-le, touche **« Se fier à cet ordinateur »**.
2. Ouvre Sideloadly, clique sur l'icône **IPA**, choisis **LynxCam.ipa**, saisis ton identifiant Apple, clique sur **Start**.
3. La première fois, sur l'iPhone : **Réglages › Général › VPN et gestion de l'appareil** › ton identifiant › **Faire confiance**,
   puis **Réglages › Confidentialité et sécurité › Mode développeur** › activer.
4. Ouvre **LynxCam** et accepte les demandes : caméra, réseau local, et plus tard micro et galerie.

**Tous les 7 jours** (compte Apple gratuit), refais le point 2. Une nouvelle version s'installe de la même façon et remplace l'ancienne.

## 4. Lancer l'app PC et se connecter

1. Lance **LynxCam.exe**. Si Windows demande le « .NET Desktop Runtime 10 », accepte.
2. En haut à droite, choisis **Wi-Fi** (tape l'adresse affichée sur l'iPhone) ou **Câble USB** (iPhone branché et déverrouillé).
3. Ouvre LynxCam sur l'iPhone, puis clique sur **Se connecter**.

Garde l'app ouverte sur l'iPhone : iOS coupe la caméra si elle passe en arrière-plan.
Le bouton lune met l'écran de l'iPhone en noir pour économiser la batterie.

## 5. Webcam virtuelle (OBS, Discord, Zoom…)

1. Dans l'app PC, section **Webcam virtuelle**, clique sur **Installer** (une seule fois ; Windows demande l'autorisation administrateur).
2. Clique sur **Activer la webcam**.
3. **OBS** : Sources › **+** › **Périphérique de capture vidéo** › choisis **LynxCam**.
   **Discord** : Paramètres › Voix et vidéo › Caméra › **LynxCam**.

Avec le thème **Fond vert**, ajoute dans OBS le filtre **Incrustation chromatique** sur la source LynxCam pour un fond transparent.

## 6. Son de l'iPhone

Active **Micro** dans l'app PC (ou sur l'iPhone). Le son sort sur la sortie choisie dans la section **Son**.

- **OBS** : ajoute une source **Capture audio d'application** et choisis **LynxCam**.
- **Discord** : installe **VB-Audio Virtual Cable** (gratuit), choisis **CABLE Input** comme sortie dans LynxCam,
  puis **CABLE Output** comme micro dans Discord.

## 7. Photos et vidéos

- **Photo** : enregistrée dans la galerie de l'iPhone **et** dans `Images\LynxCam` sur le PC.
- **Enregistrer** : vidéo (avec les effets et le son si le micro est actif) enregistrée dans la galerie de l'iPhone.
- **Capture PC** : enregistre l'image affichée dans `Images\LynxCam`.

---

## Dépannage

| Problème | Solution |
| --- | --- |
| Wi-Fi : « pas de réponse » | Même Wi-Fi sur les deux, app ouverte sur l'iPhone, bonne adresse. iPhone : **Réglages › Confidentialité et sécurité › Réseau local** › active LynxCam. |
| Wi-Fi d'école ou réseau invité | Ces réseaux bloquent les appareils entre eux : utilise le **câble USB**. |
| USB : « service Apple introuvable » | Installe iTunes depuis le site d'Apple, puis redémarre le PC. |
| USB : « aucun iPhone branché » | Déverrouille l'iPhone, touche « Se fier », vérifie qu'iTunes voit l'iPhone. |
| USB : « l'iPhone refuse la connexion » | Ouvre l'app LynxCam sur l'iPhone. |
| La webcam n'apparaît pas dans une app | Clique sur **Activer la webcam**, puis ferme et rouvre l'app (OBS, Discord…). |
| 60 img/s pas atteints | Certains objectifs sont limités ; en Wi-Fi, préfère 720p, ou passe en câble USB. |
| Image saccadée | Baisse la qualité ou la résolution, ou passe en câble USB. |
| L'app ne s'ouvre plus sur l'iPhone | Les 7 jours sont passés : réinstalle avec Sideloadly. |

## Licences

La webcam virtuelle utilise [softcam](https://github.com/tshino/softcam) (licence MIT, fichier `VirtualCam/LICENSE-softcam.txt`).
Le son utilise [NAudio](https://github.com/naudio/NAudio) (licence MIT).
