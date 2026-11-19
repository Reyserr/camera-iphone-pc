# Protocole LynxCam (version 2)

Connexion TCP. L'iPhone est le serveur (port **5050**), le PC est le client.
En USB, le PC passe par le service Apple Mobile Device (usbmux, `127.0.0.1:27015`) pour joindre ce même port.

## Format d'un message

| Octets | Contenu |
| --- | --- |
| 0 à 3 | Taille des données, entier non signé 32 bits, gros-boutiste |
| 4 | Type du message |
| 5 et + | Données |

## Types

| Type | Code | Sens | Données |
| --- | --- | --- | --- |
| HELLO | 0x01 | iPhone → PC | JSON : `name`, `system`, `protocol` (2), `app`, `version` |
| FRAME_JPEG | 0x02 | iPhone → PC | Horodatage (8 octets, ms) + image JPEG (effets déjà appliqués) |
| FRAME_H264 | 0x03 | iPhone → PC | Réservé |
| AUDIO_PCM | 0x04 | iPhone → PC | Fréquence (4 octets) + voies (1) + bits (1) + options (1, bit 0 = float) + 0 (1) + échantillons PCM |
| COMMAND | 0x10 | PC → iPhone | JSON `{"cmd": "...", "value": ...}` |
| STATUS | 0x11 | iPhone → PC | JSON : `camera`, `torch`, `zoom`, `resolution`, `fps`, `fpsTarget`, `fpsApplied`, `quality`, `mic`, `recording`, `recordSeconds`, `background`, `battery`, `charging` |
| LOG | 0x12 | iPhone → PC | Texte UTF-8 |
| BACKGROUND_IMAGE | 0x13 | PC → iPhone | Photo d'arrière-plan (JPEG) |
| PHOTO | 0x14 | iPhone → PC | Photo prise (JPEG), aussi enregistrée dans la galerie de l'iPhone |
| PING | 0x20 | PC → iPhone | 8 octets (horodatage du PC) |
| PONG | 0x21 | iPhone → PC | Les mêmes 8 octets |

## Commandes

| `cmd` | `value` |
| --- | --- |
| `camera` | `"back"`, `"ultrawide"`, `"front"` |
| `resolution` | `"720p"`, `"1080p"` |
| `fps` | 15, 24, 30 ou 60 |
| `quality` | 0.1 à 1.0 (qualité JPEG) |
| `zoom` | 1.0 à 10.0 |
| `torch` | `true` / `false` |
| `background` | `"none"`, `"blur"`, `"theme"`, `"image"` |
| `blur` | 0 à 100 |
| `theme` | `"nuit"`, `"coucher"`, `"ocean"`, `"neon"`, `"studio"`, `"vert"` |
| `exposure` | -3.0 à +3.0 (EV) |
| `focus` | `"auto"` ou 0.0 (près) à 1.0 (loin) |
| `whitebalance` | `"auto"` ou température en kelvins (2500 à 9000) |
| `brightness`, `contrast`, `saturation` | -100 à +100 |
| `mirror` | `true` / `false` |
| `mic` | `true` / `false` |
| `record` | `true` (démarrer) / `false` (arrêter) |
| `photo` | `true` |

L'iPhone n'envoie une nouvelle image que lorsque la précédente est partie : si le réseau ralentit,
des images sont sautées au lieu de prendre du retard.
