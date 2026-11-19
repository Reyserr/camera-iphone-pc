# Protocole entre l'app iPhone et l'app PC

Connexion TCP. L'iPhone est le serveur (port **5050**), le PC est le client.
En USB, le PC passe par le service Apple Mobile Device (usbmux) pour joindre ce même port.

## Format d'un message

| Octets | Contenu |
| --- | --- |
| 0 à 3 | Taille des données, entier non signé 32 bits, gros-boutiste |
| 4 | Type du message |
| 5 et + | Données |

## Types

| Type | Code | Sens | Données |
| --- | --- | --- | --- |
| HELLO | 0x01 | iPhone → PC | JSON : `name`, `system`, `protocol`, `app` |
| FRAME_JPEG | 0x02 | iPhone → PC | Horodatage (8 octets, ms, gros-boutiste) + image JPEG |
| FRAME_H264 | 0x03 | iPhone → PC | Réservé (H.264, plus tard) |
| COMMAND | 0x10 | PC → iPhone | JSON `{"cmd": "...", "value": ...}` |
| STATUS | 0x11 | iPhone → PC | JSON : `camera`, `torch`, `zoom`, `resolution`, `quality`, `fps`, `width`, `height`, `battery`, `charging` |
| LOG | 0x12 | iPhone → PC | Texte UTF-8 |
| PING | 0x20 | PC → iPhone | 8 octets (horodatage du PC) |
| PONG | 0x21 | iPhone → PC | Les mêmes 8 octets, renvoyés tels quels |

## Commandes

| `cmd` | `value` |
| --- | --- |
| `camera` | `"back"`, `"ultrawide"` ou `"front"` |
| `torch` | `true` / `false` |
| `zoom` | nombre, 1.0 à 10.0 |
| `resolution` | `"720p"` ou `"1080p"` |
| `quality` | nombre, 0.1 à 1.0 (qualité JPEG) |

L'iPhone n'envoie une nouvelle image que lorsque la précédente est partie : si le réseau ralentit,
des images sont sautées au lieu de prendre du retard.
