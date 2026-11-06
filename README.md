# MiCine

Tu propio cine en casa: las películas y series están en tu ordenador y las ves desde el navegador, la tele o el móvil, ordenadas como en Netflix (Películas, Series con temporadas y episodios, Anime), con usuarios y contraseñas.

- **Servidor para Windows** (`MiCine.exe`) o en Python (`servidor.py`): web para ver películas y series, identificación con TMDB (lo que no reconoce va a la sección «MiCine»), carátulas sacadas del vídeo, usuarios, progreso compartido entre aparatos, varias carpetas y conversión al momento con FFmpeg de lo que el aparato no sepa reproducir.
- **App para Android y Fire TV**: reproduce cualquier formato (motor de VLC), se maneja con el mando y descarga películas para verlas sin conexión.
- **App para teles Samsung con Tizen** (2015 en adelante) y **DLNA** para las anteriores, sin instalar nada.

## Descargas

En [Releases](https://github.com/Alvaropegr/MiCine/releases/latest):

| | |
|---|---|
| [`MiCine.exe`](https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine.exe) | El servidor para Windows. Funciona solo: ponlo en una carpeta y ábrelo. Para convertir formatos, añade FFmpeg (ver el LEEME). |
| [`MiCine.apk`](https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine.apk) | App para móviles Android. |
| [`MiCine-firetv.apk`](https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine-firetv.apk) | App para Fire TV y móviles antiguos. En la Fire TV, escríbela en la app **Downloader**. |
| [`MiCine-universal.apk`](https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine-universal.apk) | Vale para cualquier Android, pero pesa más. |

## Manual

**[Manual de MiCine (PDF)](docs/Manual-de-MiCine.pdf)**: instalación y uso paso a paso, con capturas para cada aparato. Cubre Windows, el navegador, el móvil Android, la Fire TV, las teles Samsung con Tizen y las teles con DLNA. También está en [Releases](https://github.com/Alvaropegr/MiCine/releases/latest).

Los detalles técnicos (formatos, ajustes de `config.json`, recompilar) están en el **[LEEME](LEEME.md)**.
