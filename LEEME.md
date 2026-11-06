# MiCine

Tu propio cine en casa: las películas y series están en tu ordenador y las ves desde el navegador, la tele o el móvil, ordenadas como en Netflix (Películas, Series con sus temporadas y episodios, Anime). Solo entra quien tenga usuario y contraseña. La app de Android reproduce cualquier formato y permite descargar para verlas sin conexión.

Código y descargas: **https://github.com/Alvaropegr/MiCine** (las apps y `MiCine.exe` están en *Releases*).

**Manual paso a paso, con imágenes para cada aparato:** [`docs\Manual-de-MiCine.pdf`](docs/Manual-de-MiCine.pdf). Este LEEME es la versión resumida y con los detalles técnicos.

## Qué hay en esta carpeta

| | |
|---|---|
| **MiCine.exe** | La aplicación de Windows que hace de servidor. Es la forma recomendada de usar MiCine. |
| `Iniciar MiCine.bat` y `servidor.py` | Lo mismo, en versión Python (necesita Python instalado). Usan los mismos datos que MiCine.exe, así que puedes usar uno u otro, pero **no los dos a la vez**. |
| `apk\` | La app para Android: `MiCine-1.2.0.apk` (móviles), `MiCine-1.2.0-firetv.apk` (Fire TV y móviles antiguos) y la `universal` (vale para todo, pero pesa más). También están en GitHub. |
| `ffmpeg\` | FFmpeg, el conversor: con él, MiCine convierte al momento lo que la tele no sabe reproducir. Si borras la carpeta, MiCine funciona igual, pero sin convertir. No viene en GitHub (ver «FFmpeg»). |
| `peliculas\` | Aquí van las películas y series (se copian directamente; también puedes añadir otras carpetas o discos). |
| `datos\` | Usuarios, sesiones, por dónde va cada uno, las teles con permiso DLNA, lo identificado en TMDB (`tmdb.json`) y las carátulas sacadas del vídeo (`caratulas\`). **No lo compartas:** contiene las claves. |
| `config.json` | Ajustes (puerto, carpeta de películas…). |
| `tizen\` | La app para teles Samsung (de 2015 en adelante) y el script que la instala. |
| `docs\Manual-de-MiCine.pdf` | El manual de instalación y uso, con capturas para cada aparato. |
| `web\`, `windows\`, `android\` | Código fuente, por si hay que cambiar algo y recompilar. |

## Primeros pasos

1. **Abre `MiCine.exe`.** La primera vez Windows preguntará por el Firewall: marca **Redes privadas** y pulsa **Permitir**. Si no, el móvil y la tele no podrán conectarse.
2. **Crea la cuenta de administrador** con el botón **Crear administrador** de la ventana. También puedes hacerlo en `http://localhost:8080`, pero solo desde este ordenador.
3. **Añade películas y series.** Cópialas a la carpeta `peliculas` (o añade tus carpetas con **Añadir carpeta…**). Las subcarpetas también valen. Desde la web ya no se suben: se copian en el ordenador.
4. **Da acceso a tu familia** en **Usuarios**. Con «Solo ver» pueden ver y descargar. Con «Administrador» además pueden identificar lo que falte, quitar películas y gestionar usuarios.

Si cierras la ventana de MiCine, sigue funcionando junto al reloj de Windows. Para apagarlo del todo usa **Salir** en ese icono. Si marcas **Iniciar con Windows**, se abre solo al encender el ordenador, ya escondido junto al reloj.

Desde la ventana también puedes cambiar la carpeta de películas (**Cambiar…**, por ejemplo a otro disco), cambiar el puerto, ver la actividad (quién entra, qué se sube…) y copiar la dirección para el móvil.

### Varias carpetas de películas

Si tienes películas repartidas (otro disco, una carpeta de series…), pulsa **Añadir carpeta…** en la tarjeta **Películas** de la ventana. Sus películas salen junto a las demás, en un solo catálogo, agrupadas con el nombre de su carpeta (por ejemplo «Series» o «Series/Temporada 2»). Con **Quitar** dejan de salir; no se borra nada.

- Si un disco está desconectado, sus películas no salen y la ventana lo indica. Al conectarlo vuelven, y con ellas por dónde ibas.
- Una carpeta que está dentro de otra (o que la contiene) no se puede añadir: sus películas ya salen.
- La versión Python lee las mismas carpetas (`otras_carpetas` en `config.json`).

## Series, películas, anime y «MiCine»

MiCine ordena todo en secciones: **Películas**, **Series** (una tarjeta por serie; dentro, sus temporadas y episodios en orden), **Anime** y **MiCine** (lo que aún no ha podido identificar). «Seguir viendo» de una serie lleva al episodio a medias o al siguiente, y al acabar un episodio ofrece el siguiente.

- **Por el nombre del archivo** sabe si algo es un episodio y de qué serie: `Serie/Season 01/Serie - S01E02.mkv`, `Serie - 1x02`, `Serie [HDTV 1080p][Cap.102]` (temporada 1, episodio 2), `Temporada 1/02 - Título.mkv`… Los capítulos de la misma serie se juntan aunque estén en carpetas o discos distintos, y si tienes el mismo episodio en 4K y en 1080p salen como dos versiones del mismo.
- **Con TMDB** (themoviedb.org, gratis) además pone el nombre en español de cada película y serie, los títulos de los episodios y la sinopsis, y separa el anime. Crea una cuenta en themoviedb.org, pide una clave de API (en tu perfil: *Ajustes → API*) y pégala en la ventana de MiCine, en **Series, películas y anime (TMDB)** → **Guardar clave**. Solo se envían los nombres de los archivos. Lo identificado se guarda en `datos\tmdb.json`.
- **Sin clave** todo funciona igual, pero separando solo por el nombre del archivo (series y películas, sin anime).
- **Lo que TMDB no reconoce** va a la sección **MiCine**. Un administrador puede pulsar **Identificar…** en la web y elegirlo del buscador, o cambiar el nombre del archivo (mejor `Título (Año)`) y pulsar **Volver a identificar** (en la ventana, o en la web: *Usuarios → Mantenimiento*). Si algo se identificó mal, en la ficha de la serie está **Corregir…**.
- Se ignoran las carpetas `Plex Versions` (las copias que hace Plex): no salen dos veces.

## Ver las películas

- **En este ordenador:** `http://localhost:8080`
- **En el móvil, la tablet o la tele de casa:** la dirección `http://192.168.x.x:8080` que muestra la ventana de MiCine.
- **App de Android y Fire TV:** instala la app en el móvil o en la Fire TV (instrucciones más abajo). Al abrirla, escribe esa misma dirección, tu usuario y tu contraseña.
- **Tele Samsung:** con la app de MiCine para Tizen (instrucciones en «Tele Samsung»). Encuentra MiCine en la red sola y se maneja con el mando.
- **Tele sin app (Samsung de 2014 o antes, y casi cualquier tele con DLNA):** MiCine aparece en su lista de fuentes. No hay que instalar nada (ver «Teles sin app: DLNA»).

MiCine recuerda por dónde va cada usuario: si paras en el móvil, sigues en el ordenador desde el mismo minuto.

## Formatos

| Formato | Web | App Android y Fire TV | Tele Samsung |
|---|---|---|---|
| MP4, M4V, WebM | ✅ | ✅ | ✅ |
| MKV, MOV | ✅ | ✅ | ✅ |
| AVI, WMV, FLV, MPG, M2TS… | ✅ (lo convierte MiCine) | ✅ | ✅ (lo que la tele no lee, lo convierte MiCine) |
| H.265 / HEVC de 10 bits, VP9, MPEG-4 antiguo… | ✅ (si el navegador no puede, ídem) | ✅ | ✅ (ídem) |
| Audio DTS, AC3, TrueHD… | ✅ (MiCine convierte solo el sonido) | ✅ | ✅ (ídem) |
| Subtítulos dentro del MKV (SRT, ASS) | ✅ | ✅ | ✅ |

- **App de Android y Fire TV:** llevan dentro el motor de **VLC**, con todos los códecs, así que reproducen cualquier archivo por sí mismas.
- **Tele Samsung y web:** usan el reproductor de la tele o del navegador. Lo que no saben reproducir, MiCine lo convierte al momento en el ordenador con FFmpeg (ver abajo). Lo deciden solas: si el archivo vale tal cual, lo ponen tal cual; si no, piden la versión convertida. Si solo falla el sonido (lo típico con DTS o AC3), se convierte solo el sonido y la imagen pasa intacta, casi sin gastar ordenador. Mientras se convierte, el reproductor lo indica con «Sonido convertido por MiCine» o «Convertida por MiCine».
- **Audio y subtítulos:** en la tele y en la web puedes elegir cualquier pista de audio del archivo y cualquier subtítulo, tanto los `.srt` de al lado como los que van dentro del MKV. Si la película no está en español y hay subtítulos en español, se ponen solos.
- **VLC:** en la web, el botón **Abrir en VLC** copia un enlace que puedes pegar en VLC (*Medio → Abrir ubicación de red*).

### FFmpeg: para que la tele lo reproduzca todo

FFmpeg es el conversor gratuito que usan Plex, Jellyfin y casi todos los programas de vídeo. Va en la carpeta `ffmpeg\` (versión 9.0.2 «essentials» de [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), licencia GPL: ver `ffmpeg\LICENSE`). No se instala nada en Windows. Al arrancar, MiCine dice en su ventana **Conversión de vídeo disponible (con …)**.

Si has descargado MiCine de GitHub, esa carpeta no viene (pesa demasiado): descarga `ffmpeg-release-essentials.zip` de gyan.dev, descomprímelo y renombra la carpeta a `ffmpeg`, dentro de la de MiCine (debe quedar `MiCine\ffmpeg\bin\ffmpeg.exe`). Después reinicia MiCine.

Si el ordenador tiene tarjeta gráfica NVIDIA, Intel o AMD, MiCine la usa para convertir, mucho más rápido que con el procesador (en las pruebas con la NVIDIA de este ordenador, un vídeo 4K H.265 de 10 bits se convertía unas 4 veces más rápido de lo que dura, y uno de 720p, unas 30 veces). Puede convertir hasta 3 películas a la vez, y lo hace con prioridad baja para que el ordenador siga yendo fluido.

Al adelantar en una película a la que solo se le convierte el sonido, la imagen empieza en el fotograma completo anterior más cercano (como en cualquier reproductor), normalmente uno o dos segundos antes de donde pediste.

Para actualizar FFmpeg, descarga `ffmpeg-release-essentials.zip` de gyan.dev y cambia `ffmpeg.exe` y `ffprobe.exe` de `ffmpeg\bin\` por los nuevos. Después reinicia MiCine.

## Subtítulos y carátulas

Ponles el mismo nombre que a la película:

```
Alien (1979).mkv
Alien (1979).es.srt      ← subtítulos en español
Alien (1979).en.srt      ← subtítulos en inglés
Alien (1979).jpg         ← carátula
```

Valen `.srt` y `.vtt` para los subtítulos, y `.jpg`, `.png` y `.webp` para las carátulas. Si cada película va en su propia carpeta, también vale una imagen llamada `poster.jpg` o `folder.jpg`.

**Lo que no tiene carátula propia lleva un fotograma del vídeo** (necesita FFmpeg): MiCine lo saca una vez, en segundo plano y sin ralentizar el ordenador, y lo guarda en `datos\caratulas`. Las películas nuevas se hacen solas. **Actualizar carátulas** (en la tarjeta *Películas* de la ventana, o en la web en *Usuarios → Mantenimiento*) las rehace todas.

## Descargar para ver sin conexión (app Android)

En la app, toca el botón ⤓ de una película. La descarga sigue aunque cierres la app, aparece en las notificaciones y se reanuda sola si se corta. Las películas descargadas están en la pestaña **Descargas** y se ven sin internet, en el avión o en el coche, con sus subtítulos y su carátula.

Ocupan espacio en el móvil: bórralas desde **Descargas** cuando las hayas visto. Se guardan en la zona privada de la app, así que al desinstalarla se borran también.

## Instalar la app en Android

1. En el navegador del móvil, abre la dirección que muestra la ventana de MiCine en **Instalar las apps** (del tipo `192.168.1.20:8080/movil`; también está en la web, en **Apps**). Fuera de casa vale `https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine.apk`. También puedes pasar `apk\MiCine-1.2.0.apk` al móvil por cable, Google Drive, WhatsApp o correo.
2. Abre lo descargado. Android pedirá permiso para **instalar aplicaciones de origen desconocido**: concédelo.
3. Si Play Protect avisa de que no conoce la app (no viene de Google Play), elige **Instalar de todas formas**.

`MiCine-1.2.0.apk` vale para casi cualquier móvil de los últimos ocho años. En móviles muy antiguos (Android 5 a 7), usa `MiCine-1.2.0-firetv.apk`. Si alguno dice que no es compatible, usa el archivo `universal` (en GitHub, *Releases*), que vale para todos pero pesa más. Si ya tenías instalada una versión anterior, se actualiza sin perder las descargas.

En la app, el menú **⋮ → Instalar en la tele** enseña la dirección para la Fire TV.

## Fire TV Stick

Es la misma app de Android, preparada para manejarse con el mando:
- Las flechas mueven la selección y OK abre la película.
- ↑ sobre una carátula lleva a su botón de descarga.
- Durante la película, ← → avanzan o retroceden 10 segundos. Cuantas más pulsaciones seguidas, más largo es el salto, y se hace al dejar de pulsar.
- ⏪ ⏩ saltan 30 segundos y ⏯ pausa.
- ↑ o ↓ muestran los botones de audio, subtítulos y formato de imagen.

**Instalarla:**

> **Antes de nada: ¿qué Fire TV tienes?** Mira *Configuración → Mi Fire TV → Acerca de*. Si el software dice **Fire OS** (7, 8…), sigue estos pasos. Si dice **OS 1.x** (Vega OS: los modelos más nuevos, como el Fire TV Stick 4K Select), Amazon **no deja instalar apps de fuera de su tienda** (ni Downloader ni MiCine): en esa tele usa otro aparato o el navegador, si lo tiene.

1. **Activa las opciones de desarrollador.** En la Fire TV, ve a *Configuración → Mi Fire TV → Acerca de* y pulsa siete veces seguidas el nombre del dispositivo, hasta que salga el aviso de que ya eres desarrollador.
2. **Instala Downloader** desde la tienda de Amazon (es gratis; búscala escribiendo «Downloader» con el teclado de la pantalla, su icono es naranja) y permítele instalar apps: en *Mi Fire TV → Opciones para desarrolladores → Instalar apps desconocidas*, actívala. En modelos antiguos la opción se llama «Aplicaciones de origen desconocido». Si con Fire OS no te aparece Downloader, instala MiCine desde el ordenador con ADB (abajo).
3. **Abre Downloader y escribe la dirección** que muestra la ventana de MiCine en **Instalar las apps** (del tipo `192.168.1.20:8080/tele`; también está en la web, en **Apps**, y en la app del móvil, en **⋮ → Instalar en la tele**). Pulsa **Instalar** cuando termine la descarga. Fuera de casa vale `https://github.com/Alvaropegr/MiCine/releases/latest/download/MiCine-firetv.apk`.

Con la misma dirección se actualiza a la versión nueva cuando la haya. También se puede instalar desde el ordenador: con **Depuración ADB** activada en las opciones de desarrollador, ejecuta `adb connect IP-de-la-Fire-TV:5555`, acepta el aviso en la tele y ejecuta `adb install -r apk\MiCine-1.2.0-firetv.apk`.

MiCine aparecerá en **Tus apps y canales**. Para verla fuera de casa, usa la dirección `https://` de tu túnel (ver «Ver MiCine fuera de casa»).

## Tele Samsung (Tizen)

La app de la tele vale para las Samsung con Tizen, es decir, **de 2015 en adelante** (modelos con J, K, M, N, R, T, A, B, C… en el nombre). Las de 2014 o anteriores (por ejemplo, las «H», como la UE40H5570) no usan Tizen y no pueden instalarla: esas ven MiCine por DLNA (siguiente apartado).

**Cómo se maneja:** flechas para moverse, OK para elegir y VOLVER para retroceder. En la película:
- **← →** saltan 10 segundos. Si pulsas varias veces seguidas, el salto se acumula y acelera, y se hace al dejar de pulsar, como en Netflix.
- **⏪ ⏩** saltan 30 segundos.
- **OK** o **⏯** pausan.
- **↓** lleva a los botones: **Audio y subtítulos** (pistas de audio, subtítulos aparte y subtítulos de dentro del vídeo) y **Desde el principio**.

La app recuerda el idioma que elijas. Si la película no está en español y tiene subtítulos en español, los pone sola.

**Instalarla** (solo una vez, desde este ordenador):

1. **Instala Tizen Studio** desde la web de Samsung para desarrolladores, en `C:\tizen-studio`. En su *Package Manager*, pestaña *Extension SDK*, instala **TV Extensions** y **Samsung Certificate Extension**.
2. **Pon la tele en modo desarrollador.** En la tele, abre **Aplicaciones** y marca **1 2 3 4 5** con el mando. Activa *Developer mode*, escribe la IP de este ordenador (la que muestra la ventana de MiCine, sin `:8080`) y reinicia la tele: mantén pulsado el botón de encendido hasta que se apague y vuelva a encenderse.
3. **Conecta Tizen Studio con la tele:** *Tools → Device Manager → Remote Device Manager → +* y escribe la IP de la tele (en la tele: *Ajustes → General → Red → Estado de red → Ajustes IP*).
4. **Crea el certificado:** *Tools → Certificate Manager → +* → **Samsung** → **TV**. Ponle de nombre **MiCine**, crea el certificado de autor (te pedirá entrar con tu cuenta de Samsung) y después el de distribuidor, con privilegio *Public*. El identificador de la tele (DUID) aparece solo si está conectada desde el paso 3.
5. **Instala la app:**
   ```
   powershell -ExecutionPolicy Bypass -File tizen\empaquetar.ps1 -Tele 192.168.1.50
   ```
   Cambia la IP por la de tu tele. MiCine aparecerá al final de **Aplicaciones**.

La primera vez que la abras, la app buscará MiCine en tu red. Después solo tienes que escribir tu usuario y contraseña con el mando.

## Teles sin app: DLNA

Las teles que no pueden instalar la app (como la Samsung UE40H5570, de 2014) ven MiCine con su propio reproductor, por DLNA. No hay que instalar nada en la tele. Solo funciona con **MiCine.exe** (la versión Python no lo trae) y dentro de casa.

1. Con MiCine.exe en marcha, en la tele abre **Fuente** (o **Multimedia → Vídeos**) y elige **MiCine**. Verás **Películas**, **Series** (cada serie con sus temporadas y episodios), **Anime**, **MiCine**, **Añadidas recientemente** y **Todas las películas**.
2. La primera vez verás **«Dale permiso en la ventana de MiCine del ordenador»**. En el ordenador aparece un aviso junto al reloj, y en la ventana de MiCine, en **Teles con DLNA**, la tele con el botón **Permitir**. Púlsalo.
3. Vuelve a abrir MiCine en la tele. Verás **Todas las películas**, **Añadidas recientemente** y tus carpetas.

Cómo funciona:

- **Formatos:** lo que la tele sabe reproducir le llega tal cual. Lo que no (H.265, VP9, sonido DTS o FLAC…) se lo convierte MiCine al momento. Las dos cosas se pueden adelantar y atrasar.
- **Subtítulos:** si la película tiene un `.srt` al lado (mejor en español), la tele lo ofrece. En las convertidas, también los que van dentro del vídeo. Se activan desde las opciones del reproductor de la tele (en las Samsung, normalmente con el botón **TOOLS** del mando).
- **Por dónde ibas:** las Samsung recuerdan el minuto de cada película en esa tele. No se mezcla con los usuarios de MiCine, porque DLNA no tiene usuarios ni contraseñas.
- **Seguridad:** por eso mismo, cada aparato necesita tu permiso. MiCine lo reconoce por su dirección física (MAC), así que no hay que volver a darlo aunque el router le cambie la IP. Desde la ventana puedes quitar el permiso en cualquier momento. Fuera de casa no funciona nunca: ni desde internet ni por el túnel.
- **Desactivarlo:** quita **Activado** en **Teles con DLNA**, o pon `"dlna": false` en `config.json`.
- **Sin ventana:** si usas MiCine con `--sin-ventana`, no hay botón **Permitir**. En ese caso, cierra MiCine, cambia `"pendiente"` por `"permitido"` en `datos\dlna.json` y vuelve a abrirlo.

## Ver MiCine fuera de casa

Por defecto MiCine solo funciona dentro de tu red de casa. **No abras el puerto en el router:** MiCine no cifra la conexión por sí solo, así que las contraseñas viajarían sin protección por internet. Hay dos formas seguras:

- **Solo para tus móviles y ordenadores: [Tailscale](https://tailscale.com)** (gratis para uso personal). Instálalo en el ordenador y en el móvil con la misma cuenta, y en la app usa la dirección de Tailscale del ordenador (`100.x.x.x:8080`).
- **Para cualquier aparato (teles, Fire TV, el móvil de otra persona…): un túnel con HTTPS.** Te da una dirección fija con candado, sin tocar el router. Con Tailscale ya instalado en el ordenador, abre una consola y ejecuta:
  ```
  tailscale funnel --bg 8080
  ```
  La primera vez te dará un enlace para activar la función en tu cuenta. Después te mostrará la dirección, del tipo `https://tu-ordenador.tu-red.ts.net`. Escríbela en las apps de la tele, del móvil o de la Fire TV, o ábrela en cualquier navegador. Otra opción es Cloudflare Tunnel, si tienes un dominio propio. El túnel pasa por los servidores de Tailscale: si con películas muy pesadas notas cortes, en el móvil usa Tailscale directo, que no tiene ese intermediario.

MiCine distingue las visitas que llegan por el túnel. Para MiCine nunca son «este ordenador» aunque el túnel corra en él, así que el primer administrador sigue pudiéndose crear solo sentado delante del PC, y el bloqueo tras 5 contraseñas incorrectas se aplica a quien está fuera, no al túnel.

## Seguridad

- Las contraseñas se guardan cifradas (PBKDF2-SHA256, 600.000 iteraciones). Nadie puede leerlas, ni siquiera abriendo `datos\`.
- Tras 5 contraseñas incorrectas seguidas, ese dispositivo queda bloqueado 5 minutos.
- El primer administrador solo se puede crear desde el propio ordenador.
- Los enlaces de vídeo que usan la app y VLC están firmados: caducan a los 7 días y no sirven para otro usuario ni para otra película.
- Borrar una película no la elimina: va a la carpeta `.papelera` de su carpeta (`peliculas\.papelera` en la principal), de donde puedes recuperarla o borrarla del todo.

## Problemas frecuentes

**El móvil no conecta.** Comprueba que está en la misma Wi-Fi que el ordenador y que MiCine está abierto. Si en el primer arranque no permitiste el Firewall, ve a *Panel de control → Firewall de Windows Defender → Permitir una aplicación* y marca MiCine en «Privada». Si la dirección `192.168…` ha cambiado, es que el router le dio otra al ordenador. Para que no vuelva a pasar, reserva una IP fija para el ordenador en la configuración del router.

**He olvidado la contraseña.** Otro administrador puede cambiarla en **Usuarios**. Si eres el único administrador, pulsa **Recuperar administrador…** en la ventana de MiCine. Con la versión Python, cierra MiCine y ejecuta `Crear o recuperar administrador.bat`.

**La película se ve sin sonido en el navegador, o no arranca.** Normalmente MiCine se da cuenta y la convierte sola. Si no lo hace, comprueba que la ventana de MiCine dice **Conversión de vídeo disponible** al arrancar (si no, falta la carpeta `ffmpeg\`). Mientras tanto, usa la app de Android o «Abrir en VLC».

**La tele no ve MiCine en su lista de fuentes (DLNA).** Comprueba que la ventana de MiCine dice **Las teles de casa ya pueden encontrar MiCine por DLNA** y que la tele está en la misma red. Windows tiene que tener la red marcada como **privada** (*Configuración → Red e Internet → tu conexión → Red privada*) y MiCine permitido en el Firewall para redes privadas (como en «El móvil no conecta»). Si la tele ya veía MiCine y deja de verlo, apágala y enciéndela: algunas recuerdan la lista de servidores un rato.

**«No se pudo abrir el puerto».** Ya hay otro MiCine abierto (quizá la versión Python), o algún programa usa el puerto 8080. Cierra el otro o cambia `puerto` en `config.json`.

## Ajustes (`config.json`)

| Clave | Por defecto | Para qué |
|---|---|---|
| `puerto` | `8080` | Puerto de la web. |
| `direccion` | `0.0.0.0` | `0.0.0.0` = toda la red de casa; `127.0.0.1` = solo este ordenador. |
| `carpeta_peliculas` | `peliculas` | Puede ser otra carpeta u otro disco, por ejemplo `D:\\Películas`. En JSON la barra invertida va doble. |
| `otras_carpetas` | `[]` | Más carpetas de películas, por ejemplo `["E:\\Series", "F:\\Pelis"]`. Lo más cómodo es **Añadir carpeta…** en la ventana. |
| `dias_sesion` | `30` | Días sin usar MiCine antes de tener que volver a entrar. |
| `abrir_navegador` | `true` | Abrir la web al arrancar (no se abre si MiCine arranca con Windows). |
| `dlna` | `true` | Que las teles de casa vean MiCine en su lista de fuentes (solo MiCine.exe). También se cambia desde la ventana. |
| `tmdb` | `""` | Clave de themoviedb.org para identificar películas, series y anime. Lo más cómodo es pegarla en la ventana. |

La carpeta y el puerto se cambian más cómodamente desde la ventana. Para el resto, edita el archivo, cierra MiCine del todo (**Salir** en el icono del reloj) y vuelve a abrirlo.

## Recompilar

- **Windows:** `powershell -ExecutionPolicy Bypass -File windows\compilar.ps1`. Usa el compilador que ya trae Windows; no hay que instalar nada. La web va metida dentro de `MiCine.exe`: si cambias algo en `web\`, recompila. La versión Python, en cambio, lo lee de la carpeta directamente.
- **Tele Samsung:** `powershell -ExecutionPolicy Bypass -File tizen\empaquetar.ps1` (necesita Tizen Studio). Para probar la app sin tele, abre `tizen\app\index.html` desde un servidor web cualquiera: sin el reproductor de la tele usa el del navegador, y las teclas de flecha, Intro y Esc hacen de mando.
- **Android:** en `android\`, con el JDK de Android Studio:
  `JAVA_HOME="C:/Program Files/Android/Android Studio/jbr" gradle assembleRelease`
  **Guarda una copia de `android\micine-firma.jks` y `android\keystore.properties`.** Sin ellos no podrás actualizar la app ya instalada: habría que desinstalarla y se perderían las descargas.
