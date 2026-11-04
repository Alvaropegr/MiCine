#!/usr/bin/env python3
"""
MiCine: tu propio servidor de películas, privado y con usuarios.

Solo usa la biblioteca estándar de Python 3.9+; no hace falta instalar nada.

    python servidor.py                 arranca MiCine (o doble clic en "Iniciar MiCine.bat")
    python servidor.py --admin         crea un administrador o le cambia la contraseña
    python servidor.py --puerto 9000   usa otro puerto (también se puede fijar en config.json)

Sirve la web (carpeta web/) y la API que usa la app de Android.
"""

from __future__ import annotations

import argparse
import getpass
import hashlib
import hmac
import ipaddress
import json
import math
import os
import re
import secrets
import shutil
import socket
import subprocess
import threading
import time
import traceback
import urllib.parse
import webbrowser
from http.cookies import CookieError, SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

BASE = Path(__file__).resolve().parent

# Las apps se publican en GitHub. Las direcciones cortas de MiCine (/tele, /movil) llevan a la última
# versión: en la Fire TV basta con escribir «192.168.1.20:8080/tele» en la app Downloader.
GITHUB = "https://github.com/Alvaropegr/MiCine"
DESCARGAS_APP = {
    "/tele": f"{GITHUB}/releases/latest/download/MiCine-firetv.apk",
    "/movil": f"{GITHUB}/releases/latest/download/MiCine.apk",
}
WEB = BASE / "web"
DATOS = BASE / "datos"
RUTA_CONFIG = BASE / "config.json"

CONFIG_POR_DEFECTO = {
    "puerto": 8080,
    "direccion": "0.0.0.0",
    "carpeta_peliculas": "peliculas",
    "dias_sesion": 30,
    "limite_subida_gb": 100,
    "abrir_navegador": True,
}

# Tipo MIME con el que se envía cada vídeo. Las .mkv se anuncian como WebM (un subconjunto
# de Matroska) y las .mov como MP4: así Chrome y Edge las reproducen si sus códecs lo permiten.
VIDEOS = {
    ".mp4": "video/mp4", ".m4v": "video/mp4", ".webm": "video/webm", ".ogv": "video/ogg",
    ".mkv": "video/webm", ".mov": "video/mp4",
    ".avi": "video/x-msvideo", ".wmv": "video/x-ms-wmv", ".flv": "video/x-flv",
    ".mpg": "video/mpeg", ".mpeg": "video/mpeg", ".m2ts": "video/mp2t", ".3gp": "video/3gpp",
}
COMPATIBLES = {".mp4", ".m4v", ".webm", ".ogv"}  # el navegador las reproduce casi siempre
DEPENDE = {".mkv", ".mov"}                       # depende de los códecs con que se grabaron
SUBTITULOS = {".srt", ".vtt"}
IMAGENES = {".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp"}
ACOMPANANTES = SUBTITULOS | set(IMAGENES)
POSTERS_CARPETA = ("poster", "cover", "folder")
IDIOMAS = {
    "es": "Español", "spa": "Español", "esp": "Español", "en": "Inglés", "eng": "Inglés",
    "fr": "Francés", "fre": "Francés", "fra": "Francés", "it": "Italiano", "ita": "Italiano",
    "de": "Alemán", "ger": "Alemán", "deu": "Alemán", "pt": "Portugués", "por": "Portugués",
    "ca": "Catalán", "cat": "Catalán", "forced": "forzados",
}
ESTATICOS = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
}
CSP = (
    "default-src 'self'; img-src 'self' data:; media-src 'self'; style-src 'self'; "
    "script-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
)
COOKIE = "micine_sesion"
TROZO = 1024 * 1024
ITERACIONES = 600_000
MARGEN_DISCO = 500 * 1024 * 1024
DIAS_ENLACE = 7
RE_USUARIO = re.compile(r"[\w.\-]{3,32}")
RESERVADOS_WINDOWS = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(10)), *(f"LPT{i}" for i in range(10))}


class ErrorPeticion(Exception):
    """Error que se devuelve tal cual al navegador o a la app."""

    def __init__(self, mensaje: str, codigo: int = 400):
        super().__init__(mensaje)
        self.codigo = codigo


def registrar(mensaje: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {mensaje}", flush=True)


def leer_json(ruta: Path, defecto):
    try:
        return json.loads(ruta.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return defecto


def escribir_json(ruta: Path, datos) -> None:
    temporal = ruta.with_name(ruta.name + ".tmp")
    temporal.write_text(json.dumps(datos, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporal, ruta)


def cargar_config() -> dict:
    config = dict(CONFIG_POR_DEFECTO)
    if RUTA_CONFIG.exists():
        config.update(leer_json(RUTA_CONFIG, {}))
    else:
        escribir_json(RUTA_CONFIG, CONFIG_POR_DEFECTO)
    return config


def hash_contrasena(contrasena: str, sal: bytes | None = None, iteraciones: int = ITERACIONES) -> dict:
    sal = sal or secrets.token_bytes(16)
    clave = hashlib.pbkdf2_hmac("sha256", contrasena.encode("utf-8"), sal, iteraciones)
    return {"sal": sal.hex(), "hash": clave.hex(), "iteraciones": iteraciones}


def comprobar_contrasena(contrasena: str, registro: dict) -> bool:
    calculado = hash_contrasena(contrasena, bytes.fromhex(registro["sal"]), registro["iteraciones"])
    return hmac.compare_digest(calculado["hash"], registro["hash"])


# Se compara contra este hash cuando el usuario no existe, para tardar lo mismo
# y no delatar qué nombres de usuario son válidos.
HASH_FALSO = hash_contrasena(secrets.token_hex(8))


def validar_nombre(nombre) -> str:
    if not isinstance(nombre, str) or not RE_USUARIO.fullmatch(nombre.strip()):
        raise ErrorPeticion("El usuario debe tener de 3 a 32 letras, números, puntos o guiones")
    return nombre.strip().lower()


def validar_contrasena(contrasena) -> str:
    if not isinstance(contrasena, str) or not 6 <= len(contrasena) <= 200:
        raise ErrorPeticion("La contraseña debe tener al menos 6 caracteres")
    return contrasena


def titulo_legible(nombre: str) -> str:
    """'The.Matrix.1999.1080p.BluRay' -> 'The Matrix (1999)'."""
    texto = nombre.replace("_", " ")
    if " " not in texto:
        texto = texto.replace(".", " ")
    texto = re.sub(r"\s+", " ", texto).strip()
    m = re.match(r"^(.+)\s[(\[]?((?:19|20)\d{2})[)\]]?(?:\s.*)?$", texto)
    if m and m.group(1).strip(" -(["):
        return f"{m.group(1).strip(' -([')} ({m.group(2)})"
    return texto


def etiqueta_subtitulo(medio: str) -> tuple[str, str]:
    """'es.forced' -> ('Español (forzados)', 'es'). Devuelve también el código de idioma."""
    partes = [p for p in medio.split(".") if p]
    if not partes:
        return "Subtítulos", ""
    nombres = [IDIOMAS.get(p.lower(), p) for p in partes]
    idioma = partes[0].lower() if partes[0].lower() in IDIOMAS and partes[0].lower() != "forced" else ""
    etiqueta = nombres[0] + (f" ({', '.join(nombres[1:])})" if len(nombres) > 1 else "")
    return etiqueta, idioma[:2]


def subtitulo_vtt(ruta: Path) -> bytes:
    """Los navegadores solo entienden WebVTT: las .srt se convierten al vuelo."""
    crudo = ruta.read_bytes()
    if crudo.startswith((b"\xff\xfe", b"\xfe\xff")):
        texto = crudo.decode("utf-16")
    else:
        for codificacion in ("utf-8-sig", "cp1252"):
            try:
                texto = crudo.decode(codificacion)
                break
            except UnicodeDecodeError:
                continue
        else:
            texto = crudo.decode("latin-1")
    texto = texto.replace("\r\n", "\n").replace("\r", "\n")
    if ruta.suffix.lower() == ".srt":
        texto = re.sub(r"(\d+):(\d{2}:\d{2}),(\d{3})", lambda m: f"{int(m[1]):02d}:{m[2]}.{m[3]}", texto)
        texto = "WEBVTT\n\n" + texto.lstrip()
    return texto.encode("utf-8")


class Almacen:
    """Usuarios, sesiones y progreso de visionado, guardados como JSON en la carpeta datos/."""

    def __init__(self, dias_sesion: int):
        DATOS.mkdir(exist_ok=True)
        self.lock = threading.RLock()
        self.duracion_sesion = dias_sesion * 86400
        self.usuarios: dict = leer_json(DATOS / "usuarios.json", {})
        self.sesiones: dict = leer_json(DATOS / "sesiones.json", {})
        self.progreso: dict = leer_json(DATOS / "progreso.json", {})
        ruta_secreto = DATOS / "secreto.key"
        if not ruta_secreto.exists():
            ruta_secreto.write_text(secrets.token_hex(32), encoding="utf-8")
        self.secreto = bytes.fromhex(ruta_secreto.read_text(encoding="utf-8").strip())

    def _guardar(self, nombre: str) -> None:
        escribir_json(DATOS / f"{nombre}.json", getattr(self, nombre))

    # --- usuarios ---

    def hay_usuarios(self) -> bool:
        return bool(self.usuarios)

    def rol(self, nombre: str) -> str | None:
        registro = self.usuarios.get(nombre)
        return registro["rol"] if registro else None

    def crear_usuario(self, nombre: str, contrasena: str, rol: str) -> None:
        with self.lock:
            if nombre in self.usuarios:
                raise ErrorPeticion("Ese usuario ya existe", 409)
            self.usuarios[nombre] = {"rol": rol, "creado": int(time.time()), **hash_contrasena(contrasena)}
            self._guardar("usuarios")

    def fijar_contrasena(self, nombre: str, contrasena: str, rol: str | None = None) -> None:
        with self.lock:
            if nombre not in self.usuarios:
                raise ErrorPeticion("Ese usuario no existe", 404)
            self.usuarios[nombre].update(hash_contrasena(contrasena))
            if rol:
                self.usuarios[nombre]["rol"] = rol
            self._guardar("usuarios")

    def borrar_usuario(self, nombre: str) -> None:
        with self.lock:
            if self.usuarios.pop(nombre, None) is None:
                raise ErrorPeticion("Ese usuario no existe", 404)
            self._guardar("usuarios")
            self.cerrar_sesiones_de(nombre)
            if self.progreso.pop(nombre, None) is not None:
                self._guardar("progreso")

    # --- sesiones ---

    @staticmethod
    def _clave(token: str) -> str:
        # En disco solo se guarda el hash del token: quien lea el archivo no puede usar las sesiones.
        return hashlib.sha256(token.encode()).hexdigest()

    def crear_sesion(self, nombre: str) -> str:
        token = secrets.token_urlsafe(32)
        with self.lock:
            ahora = time.time()
            self.sesiones = {k: s for k, s in self.sesiones.items() if s["expira"] > ahora}
            self.sesiones[self._clave(token)] = {"usuario": nombre, "expira": ahora + self.duracion_sesion}
            self._guardar("sesiones")
        return token

    def usuario_de(self, token: str | None) -> str | None:
        if not token:
            return None
        with self.lock:
            sesion = self.sesiones.get(self._clave(token))
            ahora = time.time()
            if not sesion or sesion["expira"] <= ahora or sesion["usuario"] not in self.usuarios:
                return None
            # Quien usa MiCine a menudo no tiene que volver a entrar: la sesión se renueva (como mucho una vez al día).
            if sesion["expira"] - ahora < self.duracion_sesion - 86400:
                sesion["expira"] = ahora + self.duracion_sesion
                self._guardar("sesiones")
            return sesion["usuario"]

    def cerrar_sesion(self, token: str | None) -> None:
        if not token:
            return
        with self.lock:
            if self.sesiones.pop(self._clave(token), None):
                self._guardar("sesiones")

    def cerrar_sesiones_de(self, nombre: str, salvo_token: str | None = None) -> None:
        salvo = self._clave(salvo_token) if salvo_token else None
        with self.lock:
            self.sesiones = {k: s for k, s in self.sesiones.items() if s["usuario"] != nombre or k == salvo}
            self._guardar("sesiones")

    # --- enlaces firmados ---
    # La app de Android (VLC, el gestor de descargas) no puede enviar cookies ni cabeceras,
    # así que cada archivo se pide con un enlace firmado que caduca a los pocos días.

    def _firma(self, usuario: str, ruta: str, expira: int) -> str:
        mensaje = f"{usuario}\n{ruta}\n{expira}".encode("utf-8")
        return hmac.new(self.secreto, mensaje, hashlib.sha256).hexdigest()[:40]

    def enlace(self, usuario: str, ruta: str) -> str:
        # La caducidad cambia una vez al día: así el enlace es estable y las carátulas se cachean.
        expira = (int(time.time()) // 86400 + DIAS_ENLACE + 1) * 86400
        consulta = urllib.parse.urlencode({"u": usuario, "e": expira, "f": self._firma(usuario, ruta, expira)})
        return f"/media/{urllib.parse.quote(ruta)}?{consulta}"

    def usuario_de_enlace(self, ruta: str, consulta: dict) -> str | None:
        try:
            usuario, expira, firma = consulta["u"][0], int(consulta["e"][0]), consulta["f"][0]
        except (KeyError, IndexError, ValueError):
            return None
        if expira < time.time() or usuario not in self.usuarios:
            return None
        return usuario if hmac.compare_digest(firma, self._firma(usuario, ruta, expira)) else None

    # --- progreso ---

    def progreso_de(self, nombre: str) -> dict:
        with self.lock:
            return dict(self.progreso.get(nombre, {}))

    def guardar_progreso(self, nombre: str, ruta: str, segundos: float, duracion: float) -> None:
        with self.lock:
            self.progreso.setdefault(nombre, {})[ruta] = {
                "t": round(segundos, 1), "d": round(duracion, 1), "fecha": int(time.time()),
            }
            self._guardar("progreso")


class Limitador:
    """Frena a quien intente adivinar contraseñas: 5 fallos seguidos bloquean esa IP 5 minutos."""

    def __init__(self, intentos: int = 5, bloqueo: int = 300):
        self.intentos, self.bloqueo = intentos, bloqueo
        self.fallos: dict[str, tuple[int, float]] = {}
        self.lock = threading.Lock()

    def espera(self, ip: str) -> int:
        with self.lock:
            numero, hasta = self.fallos.get(ip, (0, 0.0))
            return max(0, math.ceil(hasta - time.time())) if numero >= self.intentos else 0

    def fallo(self, ip: str) -> None:
        with self.lock:
            numero, hasta = self.fallos.get(ip, (0, 0.0))
            if numero >= self.intentos and hasta <= time.time():
                numero = 0
            numero += 1
            self.fallos[ip] = (numero, time.time() + self.bloqueo if numero >= self.intentos else 0.0)

    def exito(self, ip: str) -> None:
        with self.lock:
            self.fallos.pop(ip, None)


def ruta_de_config(texto: str) -> Path:
    """Una carpeta de config.json: admite ~ y %VARIABLES%; si no es absoluta, va dentro de la carpeta de MiCine."""
    carpeta = Path(os.path.expandvars(os.path.expanduser(texto)))
    return carpeta if carpeta.is_absolute() else BASE / carpeta


def id_carpeta(carpeta: Path) -> str:
    """Identificador fijo de una carpeta añadida (el mismo que calcula MiCine.exe): va delante de sus rutas."""
    normal = os.path.normcase(os.path.abspath(str(carpeta))).rstrip("\\/")
    return hashlib.sha256(normal.encode("utf-8")).hexdigest()[:8]


def nombre_carpeta(carpeta: Path) -> str:
    """Con qué nombre se agrupan sus películas: «Series», o «Disco E» si es un disco entero."""
    absoluta = Path(os.path.abspath(str(carpeta)))
    return absoluta.name or f"Disco {absoluta.drive.rstrip(':')}"


# Las películas de las carpetas añadidas van como «.1a2b3c4d/…»: un nombre que empieza por punto
# nunca es de la carpeta principal (se tratan como ocultos), así que no se pueden confundir.
RE_OTRA_CARPETA = re.compile(r"\.([0-9a-f]{8})/(.+)", re.S)


class Raiz:
    def __init__(self, ruta: Path, prefijo: str = "", nombre: str = ""):
        self.ruta, self.prefijo, self.nombre = ruta, prefijo, nombre


class Biblioteca:
    """Las carpetas de películas: la principal (donde se guardan las subidas) y las que se añadan, en
    otros discos por ejemplo. Juntas forman un solo catálogo."""

    def __init__(self, carpeta: Path, otras: list[Path] = ()):
        carpeta.mkdir(parents=True, exist_ok=True)
        self.carpeta = carpeta.resolve()
        self.lock = threading.Lock()
        self.raices = [Raiz(self.carpeta)]
        for otra in otras:
            try:
                real = otra.resolve()
            except (OSError, ValueError):
                real = Path(os.path.abspath(otra))
            if any(real == r.ruta or real.is_relative_to(r.ruta) or r.ruta.is_relative_to(real) for r in self.raices):
                registrar(f"Se ignora la carpeta «{otra}»: está dentro de otra carpeta de MiCine o la contiene")
                continue
            self.raices.append(Raiz(real, f".{id_carpeta(otra)}/", nombre_carpeta(otra)))

    def raiz_de(self, ruta: Path) -> Raiz:
        # Las carpetas no están unas dentro de otras: solo puede ser una.
        return next(r for r in self.raices if ruta == r.ruta or ruta.is_relative_to(r.ruta))

    def relativa(self, ruta: Path) -> str:
        raiz = self.raiz_de(ruta)
        return raiz.prefijo + ruta.relative_to(raiz.ruta).as_posix()

    def resolver(self, relativa: str) -> Path | None:
        """Convierte una ruta recibida de fuera en un archivo de la biblioteca, sin salirse de ella."""
        if not relativa or "\0" in relativa:
            return None
        raiz = self.raices[0]
        otra = RE_OTRA_CARPETA.fullmatch(relativa)
        if otra:
            raiz = next((r for r in self.raices[1:] if r.prefijo == f".{otra.group(1)}/"), None)
            if raiz is None:
                return None
            relativa = otra.group(2)
        try:
            ruta = (raiz.ruta / relativa).resolve()
        except (OSError, ValueError):
            return None
        if ruta == raiz.ruta or not ruta.is_relative_to(raiz.ruta):
            return None
        if any(parte.startswith(".") for parte in ruta.relative_to(raiz.ruta).parts):
            return None
        return ruta if ruta.is_file() else None

    def listar(self) -> list[dict]:
        peliculas = []
        for r in self.raices:
            if r.ruta.is_dir():  # un disco desconectado no se cuenta
                self._recorrer(r, peliculas)
        peliculas.sort(key=lambda p: p["titulo"].casefold())
        return peliculas

    def _recorrer(self, r: Raiz, peliculas: list[dict]) -> None:
        for raiz, carpetas, archivos in os.walk(r.ruta):
            carpetas[:] = sorted(c for c in carpetas if not c.startswith("."))
            raiz = Path(raiz)
            poster_carpeta = next(
                (a for a in sorted(archivos) if os.path.splitext(a)[0].lower() in POSTERS_CARPETA
                 and os.path.splitext(a)[1].lower() in IMAGENES), None)
            videos = [a for a in archivos if not a.startswith(".") and os.path.splitext(a)[1].lower() in VIDEOS]
            for nombre in videos:
                ruta = raiz / nombre
                try:
                    info = ruta.stat()
                except OSError:
                    continue
                ext = os.path.splitext(nombre)[1].lower()
                base = nombre[: -len(ext)]
                subtitulos, poster = [], None
                for otro in sorted(archivos):
                    otro_ext = os.path.splitext(otro)[1].lower()
                    if otro_ext not in ACOMPANANTES or not otro.lower().startswith(base.lower() + "."):
                        continue
                    medio = otro[len(base) + 1: len(otro) - len(otro_ext)]
                    if otro_ext in SUBTITULOS:
                        etiqueta, idioma = etiqueta_subtitulo(medio)
                        subtitulos.append({"ruta": self.relativa(raiz / otro), "etiqueta": etiqueta, "idioma": idioma})
                    elif poster is None:
                        poster = self.relativa(raiz / otro)
                if poster is None and poster_carpeta and len(videos) == 1:
                    poster = self.relativa(raiz / poster_carpeta)
                subcarpeta = raiz.relative_to(r.ruta).as_posix()
                peliculas.append({
                    "ruta": self.relativa(ruta),
                    "titulo": titulo_legible(base),
                    "carpeta": "/".join(x for x in (r.nombre, "" if subcarpeta == "." else subcarpeta) if x),
                    "tamano": info.st_size,
                    "fecha": int(info.st_mtime),
                    "compat": "si" if ext in COMPATIBLES else "quizas" if ext in DEPENDE else "no",
                    "subtitulos": subtitulos,
                    "poster": poster,
                })

    @staticmethod
    def nombre_seguro(nombre: str) -> str:
        nombre = nombre.replace("\\", "/").split("/")[-1]
        nombre = re.sub(r'[<>:"|?*\x00-\x1f]', "", nombre).strip().strip(".").strip()
        base, ext = os.path.splitext(nombre)
        if not base or ext.lower() not in VIDEOS.keys() | ACOMPANANTES:
            raise ErrorPeticion("Solo se pueden subir vídeos, subtítulos (.srt, .vtt) o carátulas (.jpg, .png, .webp)")
        if base.split(".")[0].upper() in RESERVADOS_WINDOWS:
            base = "_" + base
        return base[:180] + ext.lower()

    def destino_libre(self, nombre: str) -> Path:
        base, ext = os.path.splitext(nombre)
        numero = 1
        while True:
            candidato = self.carpeta / (nombre if numero == 1 else f"{base} ({numero}){ext}")
            if not candidato.exists() and not candidato.with_name(candidato.name + ".subiendo").exists():
                return candidato
            numero += 1

    def a_papelera(self, relativa: str) -> int:
        """Mueve la película (y sus subtítulos y carátula) a .papelera. No borra nada de verdad."""
        ruta = self.resolver(relativa)
        if not ruta or ruta.suffix.lower() not in VIDEOS:
            raise ErrorPeticion("Esa película ya no existe", 404)
        base = ruta.name[: -len(ruta.suffix)].lower() + "."
        archivos = [ruta] + [
            otro for otro in ruta.parent.iterdir()
            if otro.is_file() and otro.suffix.lower() in ACOMPANANTES and otro.name.lower().startswith(base)
        ]
        papelera = self.raiz_de(ruta).ruta / ".papelera"  # en su mismo disco: moverla es instantáneo
        papelera.mkdir(exist_ok=True)
        marca = time.strftime("%Y%m%d-%H%M%S")
        try:
            for archivo in archivos:
                os.replace(archivo, papelera / f"{marca} {archivo.name}")
        except PermissionError:
            raise ErrorPeticion("Alguien está viendo esa película ahora mismo. Inténtalo cuando termine.", 409)
        return len(archivos)


# --- Conversión al vuelo (opcional: solo si está FFmpeg) ---

# Subtítulos internos que se pueden pasar a texto; los de imagen (PGS, VobSub) no.
SUBTITULOS_TEXTO = {"subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer"}
CONVERSIONES_A_LA_VEZ = 3
CODIFICADORES = {
    "h264_nvenc": ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23",
                   "-b:v", "8M", "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high"],
    "h264_qsv": ["-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "23",
                 "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high"],
    "h264_amf": ["-c:v", "h264_amf", "-quality", "speed", "-rc", "vbr_peak",
                 "-b:v", "8M", "-maxrate", "12M", "-profile:v", "high"],
    "libx264": ["-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high", "-level", "4.1"],
}


def buscar_ffmpeg() -> tuple[str, str] | None:
    """FFmpeg en la carpeta ffmpeg/ de MiCine (tal como queda al descomprimir el zip) o en el PATH."""
    exe = ".exe" if os.name == "nt" else ""
    for carpeta in (BASE / "ffmpeg" / "bin", BASE / "ffmpeg"):
        ffmpeg, ffprobe = carpeta / f"ffmpeg{exe}", carpeta / f"ffprobe{exe}"
        if ffmpeg.is_file() and ffprobe.is_file():
            return str(ffmpeg), str(ffprobe)
    ffmpeg, ffprobe = shutil.which("ffmpeg"), shutil.which("ffprobe")
    return (ffmpeg, ffprobe) if ffmpeg and ffprobe else None


class Conversor:
    """Convierte al vuelo lo que la tele o el navegador no saben reproducir. Si la imagen se
    entiende y solo falla el audio (DTS, TrueHD…), la imagen se copia tal cual y casi no gasta."""

    def __init__(self, ffmpeg: str, ffprobe: str):
        self.ffmpeg, self.ffprobe = ffmpeg, ffprobe
        self.lock = threading.Lock()
        self.infos: dict = {}
        self.subtitulos: dict = {}
        self.procesos: set = set()
        self.codificador = "libx264"
        threading.Thread(target=self._elegir_codificador, daemon=True).start()

    @staticmethod
    def _opciones() -> dict:
        if os.name != "nt":
            return {}
        # Sin ventana negra y con prioridad baja: el ordenador sigue fluido mientras convierte.
        return {"creationflags": subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS}

    def _elegir_codificador(self) -> None:
        """La tarjeta gráfica convierte mucho más rápido que el procesador: se usa si la hay."""
        for codificador in ("h264_nvenc", "h264_qsv", "h264_amf"):
            try:
                r = subprocess.run([self.ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                                    "-i", "color=black:s=256x144:d=0.2", "-c:v", codificador, "-f", "null", "-"],
                                   capture_output=True, timeout=30, **self._opciones())
            except (OSError, subprocess.TimeoutExpired):
                continue
            if r.returncode == 0:
                self.codificador = codificador
                break
        registrar(f"Conversión de vídeo disponible (con {self.codificador})")

    def info(self, ruta: Path) -> dict:
        """Pistas de vídeo, audio y subtítulos que lleva el archivo (se pregunta una vez y se recuerda)."""
        estado = ruta.stat()
        clave = (str(ruta), estado.st_size, estado.st_mtime)
        with self.lock:
            if clave in self.infos:
                return self.infos[clave]
        try:
            r = subprocess.run([self.ffprobe, "-v", "error", "-print_format", "json", "-show_format",
                                "-show_streams", "file:" + str(ruta)], capture_output=True, timeout=60, **self._opciones())
            crudo = json.loads(r.stdout or b"{}")
        except (OSError, ValueError, subprocess.TimeoutExpired):
            crudo = {}
        info: dict = {"duracion": 0.0, "inicio": 0.0, "video": None, "audios": [], "subtitulos": []}
        for campo, clave in (("duracion", "duration"), ("inicio", "start_time")):
            try:
                info[campo] = round(float(crudo.get("format", {}).get(clave, 0)), 3)
            except (TypeError, ValueError):
                pass
        for s in crudo.get("streams", []):
            tipo, etiquetas, marcas = s.get("codec_type"), s.get("tags") or {}, s.get("disposition") or {}
            idioma, titulo, codec = str(etiquetas.get("language", "")).lower(), str(etiquetas.get("title", "")), s.get("codec_name", "")
            if tipo == "video" and info["video"] is None and not marcas.get("attached_pic"):
                info["video"] = {"codec": codec, "ancho": s.get("width", 0), "alto": s.get("height", 0),
                                 "bits": 10 if "10" in str(s.get("pix_fmt", "")) else 8}
            elif tipo == "audio":
                info["audios"].append({"n": len(info["audios"]), "codec": codec, "canales": s.get("channels", 0),
                                       "idioma": idioma, "titulo": titulo, "defecto": bool(marcas.get("default"))})
            elif tipo == "subtitle":
                info["subtitulos"].append({"n": len(info["subtitulos"]), "codec": codec, "idioma": idioma,
                                           "titulo": titulo, "texto": codec in SUBTITULOS_TEXTO})
        with self.lock:
            self.infos[clave] = info
        return info

    def clave(self, ruta: Path, segundo: float) -> float:
        """El fotograma completo (clave) en el que empieza la imagen si se salta a ese segundo sin
        convertirla: desde ahí arrancan imagen y sonido, y el reproductor debe contar desde ahí."""
        if segundo <= 0:
            return 0.0
        inicio = self.info(ruta)["inicio"]
        try:
            r = subprocess.run([self.ffprobe, "-v", "error", "-select_streams", "v:0", "-read_intervals",
                                f"{inicio + segundo + 0.001:.3f}%+#90", "-show_entries", "packet=pts_time,flags",
                                "-of", "csv=p=0", "file:" + str(ruta)], capture_output=True, timeout=30, **self._opciones())
            for linea in r.stdout.decode("ascii", "replace").splitlines():
                tiempo, _, marcas = linea.partition(",")
                if "K" in marcas and tiempo not in ("", "N/A"):
                    return round(min(max(float(tiempo) - inicio, 0.0), segundo), 3)
        except (OSError, ValueError, subprocess.TimeoutExpired):
            pass
        return segundo

    def convertir(self, ruta: Path, inicio: float, audio: int, copiar_video: bool, para_web: bool) -> subprocess.Popen:
        """Lanza FFmpeg y devuelve el proceso: el vídeo convertido sale por su salida estándar."""
        with self.lock:
            self.procesos = {p for p in self.procesos if p.poll() is None}
            if len(self.procesos) >= CONVERSIONES_A_LA_VEZ:
                raise ErrorPeticion("El ordenador ya está convirtiendo varias películas a la vez. Prueba en un momento.", 503)
        canales = next((a["canales"] for a in self.info(ruta)["audios"] if a["n"] == audio), 2)
        args = [self.ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error"]
        if inicio > 0 and copiar_video:
            # La imagen copiada solo puede empezar en un fotograma clave: el sonido arranca ahí
            # también para que no haya segundos mudos. (FFmpeg busca un poco antes de lo pedido.)
            args += ["-noaccurate_seek", "-ss", f"{self.clave(ruta, inicio) + 0.15:.3f}"]
        elif inicio > 0:
            args += ["-ss", f"{inicio:.2f}"]
        args += ["-i", "file:" + str(ruta), "-map", "0:v:0?", "-map", f"0:a:{audio}?", "-sn", "-dn",
                 "-map_metadata", "-1", "-map_chapters", "-1"]
        if copiar_video:
            args += ["-c:v", "copy"]
            if para_web and (self.info(ruta)["video"] or {}).get("codec") == "hevc":
                args += ["-tag:v", "hvc1"]  # la etiqueta que esperan los navegadores
        else:
            args += ["-vf", "scale='min(1920,iw)':-2,format=yuv420p", *CODIFICADORES[self.codificador], "-g", "48"]
        if para_web or canales <= 2:
            args += ["-c:a", "aac", "-ac", "2", "-b:a", "192k"]
        else:
            args += ["-c:a", "ac3", "-ac", str(min(canales, 6)), "-b:a", "640k"]
        if para_web:
            args += ["-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof"]
        else:
            args += ["-f", "mpegts"]
        args.append("pipe:1")
        proceso = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **self._opciones())
        proceso.parado = False
        with self.lock:
            self.procesos.add(proceso)
        threading.Thread(target=self._vigilar, args=(proceso, ruta.name), daemon=True).start()
        return proceso

    def _vigilar(self, proceso: subprocess.Popen, nombre: str) -> None:
        # Hay que leer lo que FFmpeg escribe en stderr: si nadie lo lee, se atasca.
        errores = proceso.stderr.read().decode("utf-8", "replace").strip()
        if proceso.wait() != 0 and not proceso.parado and errores:
            registrar(f"No se pudo convertir «{nombre}»: {errores.splitlines()[-1][:300]}")

    def parar(self, proceso: subprocess.Popen) -> None:
        proceso.parado = True
        if proceso.poll() is None:
            proceso.kill()
        with self.lock:
            self.procesos.discard(proceso)

    def subtitulo(self, ruta: Path, n: int) -> bytes:
        """Saca a WebVTT unos subtítulos que van dentro del vídeo (hay que leer el archivo entero)."""
        estado = ruta.stat()
        clave = (str(ruta), estado.st_size, estado.st_mtime, n)
        with self.lock:
            if clave in self.subtitulos:
                return self.subtitulos[clave]
        try:
            r = subprocess.run([self.ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", "file:" + str(ruta),
                                "-map", f"0:s:{n}", "-f", "webvtt", "pipe:1"],
                               capture_output=True, timeout=900, **self._opciones())
        except (OSError, subprocess.TimeoutExpired):
            raise ErrorPeticion("No se pudieron sacar los subtítulos del vídeo", 500)
        if r.returncode != 0 or not r.stdout:
            raise ErrorPeticion("Esos subtítulos son imágenes, no texto, y no se pueden mostrar", 422)
        with self.lock:
            if len(self.subtitulos) >= 50:
                self.subtitulos.clear()
            self.subtitulos[clave] = r.stdout
        return r.stdout


class ServidorMiCine(ThreadingHTTPServer):
    # En Windows, SO_REUSEADDR dejaría abrir dos MiCine en el mismo puerto sin avisar.
    allow_reuse_address = os.name != "nt"
    daemon_threads = True

    def __init__(self, direccion, config: dict, almacen: Almacen, biblioteca: Biblioteca,
                 conversor: Conversor | None = None):
        self.config, self.almacen, self.biblioteca, self.conversor = config, almacen, biblioteca, conversor
        self.limitador = Limitador()
        super().__init__(direccion, Manejador)


# (método HTTP, ruta) -> (método del manejador, solo administradores)
RUTAS = {
    ("GET", "/api/peliculas"): ("api_peliculas", False),
    ("GET", "/api/info"): ("api_info", False),
    ("POST", "/api/logout"): ("api_logout", False),
    ("POST", "/api/progreso"): ("api_progreso", False),
    ("POST", "/api/cuenta/contrasena"): ("api_mi_contrasena", False),
    ("POST", "/api/subir"): ("api_subir", True),
    ("POST", "/api/peliculas/borrar"): ("api_borrar_pelicula", True),
    ("GET", "/api/usuarios"): ("api_usuarios", True),
    ("POST", "/api/usuarios/crear"): ("api_crear_usuario", True),
    ("POST", "/api/usuarios/borrar"): ("api_borrar_usuario", True),
    ("POST", "/api/usuarios/contrasena"): ("api_contrasena_usuario", True),
}


# Lo que puede pedir una app que no se sirve desde MiCine (la de la tele): solo funciona con
# token, porque la galleta es SameSite=Strict y nunca se envía desde otro origen.
RUTAS_APPS = {"/api/estado", "/api/login", "/api/logout", "/api/peliculas", "/api/info", "/api/progreso"}


# Las añaden los túneles y proxys que corren en este mismo ordenador (Tailscale Funnel, Cloudflare
# Tunnel…): con ellas, la petición viene de fuera aunque llegue desde 127.0.0.1.
CABECERAS_PROXY = ("CF-Connecting-IP", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host",
                   "X-Real-IP", "Forwarded", "Tailscale-Funnel-Request")


def es_ruta_de_apps(ruta: str) -> bool:
    return ruta in RUTAS_APPS or ruta.startswith("/media/")


class Manejador(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "MiCine"
    sys_version = ""
    timeout = 300
    server: ServidorMiCine
    _cors = False

    # --- infraestructura ---

    def do_GET(self):
        self._atender()

    def do_POST(self):
        self._atender()

    def do_OPTIONS(self):
        """Consulta previa del navegador antes de que la app de la tele llame a la API."""
        self._cors = es_ruta_de_apps(urllib.parse.urlsplit(self.path).path)
        if not self._cors:
            return self.enviar_bytes(b"", "text/plain", 403)
        self.enviar_bytes(b"", "text/plain", 204, {
            "Access-Control-Allow-Methods": "GET, POST",
            "Access-Control-Allow-Headers": "Authorization, Content-Type, X-MiCine, Range",
            "Access-Control-Max-Age": "600",
        })

    def log_request(self, code="-", size="-"):
        pass  # cada salto en el vídeo es una petición: registrarlas todas sería ruido

    def log_error(self, formato, *args):
        if "timed out" not in formato:
            registrar(f"{self.client_address[0]}: {formato % args}")

    def _atender(self):
        self._cuerpo_leido = self.command != "POST"
        self._cors = False
        try:
            url = urllib.parse.urlsplit(self.path)
            ruta, self.consulta = url.path, urllib.parse.parse_qs(url.query)
            self._cors = es_ruta_de_apps(ruta)

            if self.command == "GET":
                if ruta in ("/", "/index.html"):
                    return self.enviar_estatico("index.html")
                if ruta.startswith("/static/"):
                    return self.enviar_estatico(ruta[len("/static/"):])
                if ruta == "/api/estado":
                    return self.api_estado()
                if ruta in DESCARGAS_APP:
                    return self.redirigir(DESCARGAS_APP[ruta])
                if ruta.startswith("/media/"):
                    relativa = urllib.parse.unquote(ruta[len("/media/"):])
                    if not (self.usuario_actual() or self.server.almacen.usuario_de_enlace(relativa, self.consulta)):
                        raise ErrorPeticion("Inicia sesión para continuar", 401)
                    return self.servir_media(relativa)
            else:
                # Cabecera propia obligatoria: otra web no puede añadirla sin permiso (protección CSRF).
                if self.headers.get("X-MiCine") != "1":
                    raise ErrorPeticion("Petición rechazada", 403)
                if ruta == "/api/login":
                    return self.api_login()
                if ruta == "/api/configurar":
                    return self.api_configurar()

            usuario = self.usuario_actual()
            if not usuario:
                raise ErrorPeticion("Inicia sesión para continuar", 401)
            accion = RUTAS.get((self.command, ruta))
            if accion is None:
                raise ErrorPeticion("No encontrado", 404)
            metodo, solo_admin = accion
            if solo_admin and self.server.almacen.rol(usuario) != "admin":
                raise ErrorPeticion("Solo un administrador puede hacer esto", 403)
            getattr(self, metodo)(usuario)
        except ErrorPeticion as error:
            if not self._cuerpo_leido:
                self.close_connection = True  # no se leyó el cuerpo: no se puede reutilizar la conexión
            self.enviar_json({"error": str(error)}, error.codigo)
        except (ConnectionError, TimeoutError):
            self.close_connection = True
        except Exception:
            traceback.print_exc()
            self.close_connection = True
            try:
                self.enviar_json({"error": "Error interno del servidor"}, 500)
            except OSError:
                pass

    def _desde_este_ordenador(self) -> bool:
        ip = ipaddress.ip_address(self.client_address[0])
        if ip.version == 6 and ip.ipv4_mapped:
            ip = ip.ipv4_mapped
        return ip.is_loopback

    def por_tunel(self) -> bool:
        return self._desde_este_ordenador() and any(self.headers.get(c) for c in CABECERAS_PROXY)

    def es_local(self) -> bool:
        return self._desde_este_ordenador() and not self.por_tunel()

    def ip_cliente(self) -> str:
        """La IP de quien pide; si llega por un túnel, la que anota el túnel."""
        if not self.por_tunel():
            return self.client_address[0]
        ip = (self.headers.get("CF-Connecting-IP") or "").strip()
        if not ip:
            # El último de la lista lo añade el propio túnel; los anteriores los puede inventar el cliente.
            ip = (self.headers.get("X-Forwarded-For") or "").split(",")[-1].strip()
        ip = (ip or (self.headers.get("X-Real-IP") or "").strip())[:64]
        return ip or "túnel"

    def token(self) -> str | None:
        autorizacion = self.headers.get("Authorization", "")
        if autorizacion.startswith("Bearer "):
            return autorizacion[len("Bearer "):].strip()
        galletas = SimpleCookie()
        try:
            galletas.load(self.headers.get("Cookie", ""))
        except CookieError:
            return None
        galleta = galletas.get(COOKIE)
        return galleta.value if galleta else None

    def usuario_actual(self) -> str | None:
        return self.server.almacen.usuario_de(self.token())

    def leer_cuerpo(self) -> dict:
        try:
            longitud = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            longitud = -1
        if not 0 <= longitud <= 64 * 1024:
            raise ErrorPeticion("Petición no válida")
        crudo = self.rfile.read(longitud)
        self._cuerpo_leido = True
        try:
            datos = json.loads(crudo or b"{}")
        except ValueError:
            raise ErrorPeticion("Petición no válida")
        if not isinstance(datos, dict):
            raise ErrorPeticion("Petición no válida")
        return datos

    def enviar_cabeceras(self, codigo: int, tipo: str, longitud: int, cabeceras: dict | None = None) -> None:
        self.send_response(codigo)
        todas = {"Content-Type": tipo, "Content-Length": str(longitud), "Cache-Control": "no-store",
                 "X-Content-Type-Options": "nosniff", "X-Frame-Options": "DENY", "Referrer-Policy": "same-origin"}
        if self._cors:
            todas["Access-Control-Allow-Origin"] = "*"
        todas.update(cabeceras or {})
        for nombre, valor in todas.items():
            self.send_header(nombre, valor)
        self.end_headers()

    def enviar_bytes(self, cuerpo: bytes, tipo: str, codigo: int = 200, cabeceras: dict | None = None) -> None:
        self.enviar_cabeceras(codigo, tipo, len(cuerpo), cabeceras)
        self.wfile.write(cuerpo)

    def enviar_json(self, datos, codigo: int = 200, cabeceras: dict | None = None) -> None:
        cuerpo = json.dumps(datos, ensure_ascii=False).encode("utf-8")
        self.enviar_bytes(cuerpo, "application/json; charset=utf-8", codigo, cabeceras)

    def redirigir(self, url: str) -> None:
        self.enviar_bytes(b"", "text/plain", 302, {"Location": url, "Cache-Control": "no-cache"})

    def enviar_estatico(self, nombre: str) -> None:
        ruta = (WEB / urllib.parse.unquote(nombre)).resolve()
        if not ruta.is_relative_to(WEB) or not ruta.is_file() or ruta.suffix not in ESTATICOS:
            raise ErrorPeticion("No encontrado", 404)
        cabeceras = {"Cache-Control": "no-cache"}
        if ruta.suffix == ".html":
            cabeceras["Content-Security-Policy"] = CSP
        self.enviar_bytes(ruta.read_bytes(), ESTATICOS[ruta.suffix], cabeceras=cabeceras)

    def enviar_archivo(self, ruta: Path, tipo: str, descarga: bool = False) -> None:
        """Envía un archivo respetando 'Range': así se puede saltar a cualquier minuto del vídeo."""
        tamano = ruta.stat().st_size
        inicio, fin, codigo = 0, tamano - 1, 200
        rango = self.headers.get("Range")
        if rango:
            m = re.fullmatch(r"bytes=(\d*)-(\d*)", rango.strip())
            if not m or m.group(1) == m.group(2) == "":
                raise ErrorPeticion("Rango no válido", 416)
            if m.group(1) == "":
                inicio = max(0, tamano - int(m.group(2)))
            else:
                inicio = int(m.group(1))
                if m.group(2):
                    fin = min(int(m.group(2)), tamano - 1)
            if inicio >= tamano or inicio > fin:
                self.enviar_cabeceras(416, "text/plain", 0, {"Content-Range": f"bytes */{tamano}"})
                return
            codigo = 206
        cabeceras = {"Accept-Ranges": "bytes", "Cache-Control": "private, max-age=86400"}
        if codigo == 206:
            cabeceras["Content-Range"] = f"bytes {inicio}-{fin}/{tamano}"
        if descarga:
            tipo = "application/octet-stream"
            cabeceras["Content-Disposition"] = f"attachment; filename*=UTF-8''{urllib.parse.quote(ruta.name)}"
        self.enviar_cabeceras(codigo, tipo, fin - inicio + 1, cabeceras)
        with open(ruta, "rb") as archivo:
            archivo.seek(inicio)
            restante = fin - inicio + 1
            while restante > 0:
                trozo = archivo.read(min(TROZO, restante))
                if not trozo:
                    break
                self.wfile.write(trozo)
                restante -= len(trozo)

    def numero(self, nombre: str, maximo: float) -> float:
        try:
            valor = float(self.consulta.get(nombre, ["0"])[0])
        except ValueError:
            raise ErrorPeticion("Petición no válida")
        if not 0 <= valor <= maximo:
            raise ErrorPeticion("Petición no válida")
        return valor

    def servir_conversion(self, ruta: Path) -> None:
        """Vídeo convertido al vuelo (o subtítulos internos pasados a texto) para quien no puede con el original."""
        conversor = self.server.conversor
        if conversor is None:
            raise ErrorPeticion("Para convertir vídeos, MiCine necesita FFmpeg (ver LEEME.md)", 501)
        if "subtitulo" in self.consulta:
            vtt = conversor.subtitulo(ruta, int(self.numero("subtitulo", 99)))
            return self.enviar_bytes(vtt, "text/vtt; charset=utf-8", cabeceras={"Cache-Control": "private, max-age=3600"})
        para_web = self.consulta.get("formato", [""])[0] == "web"
        proceso = conversor.convertir(ruta, self.numero("t", 1e6), int(self.numero("audio", 99)),
                                      self.consulta.get("video", [""])[0] == "copia", para_web)
        # No se sabe cuánto ocupará: la respuesta termina cuando se cierra la conexión.
        self.close_connection = True
        self.send_response(200)
        cabeceras = {"Content-Type": "video/mp4" if para_web else "video/mp2t", "Cache-Control": "no-store",
                     "X-Content-Type-Options": "nosniff", "Connection": "close"}
        if self._cors:
            cabeceras["Access-Control-Allow-Origin"] = "*"
        for nombre, valor in cabeceras.items():
            self.send_header(nombre, valor)
        self.end_headers()
        try:
            while True:
                trozo = proceso.stdout.read1(256 * 1024)
                if not trozo:
                    break
                self.wfile.write(trozo)
        except OSError:
            pass  # el reproductor ha cerrado: ha saltado a otro minuto, cambiado de pista o salido
        finally:
            conversor.parar(proceso)

    def servir_media(self, relativa: str) -> None:
        ruta = self.server.biblioteca.resolver(relativa)
        ext = ruta.suffix.lower() if ruta else ""
        if ext in VIDEOS and ("convertir" in self.consulta or "subtitulo" in self.consulta):
            return self.servir_conversion(ruta)
        if ext in VIDEOS:
            return self.enviar_archivo(ruta, VIDEOS[ext], descarga="descargar" in self.consulta)
        if ext in IMAGENES:
            return self.enviar_archivo(ruta, IMAGENES[ext])
        if ext in SUBTITULOS:
            if "original" in self.consulta:  # VLC entiende .srt directamente
                return self.enviar_archivo(ruta, "application/x-subrip" if ext == ".srt" else "text/vtt")
            return self.enviar_bytes(subtitulo_vtt(ruta), "text/vtt; charset=utf-8",
                                     cabeceras={"Cache-Control": "private, max-age=3600"})
        raise ErrorPeticion("No encontrado", 404)

    def galleta(self, token: str, segundos: int) -> dict:
        return {"Set-Cookie": f"{COOKIE}={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={segundos}"}

    def responder_sesion(self, nombre: str, para_app: bool) -> None:
        token = self.server.almacen.crear_sesion(nombre)
        datos = {"usuario": {"nombre": nombre, "rol": self.server.almacen.rol(nombre)}}
        if para_app:
            self.enviar_json({**datos, "token": token})
        else:
            self.enviar_json(datos, cabeceras=self.galleta(token, self.server.almacen.duracion_sesion))

    # --- API pública ---

    def api_estado(self) -> None:
        almacen = self.server.almacen
        usuario = self.usuario_actual()
        self.enviar_json({
            "configurado": almacen.hay_usuarios(),
            "local": self.es_local(),
            "usuario": {"nombre": usuario, "rol": almacen.rol(usuario)} if usuario else None,
        })

    def api_configurar(self) -> None:
        datos = self.leer_cuerpo()
        if not self.es_local():
            raise ErrorPeticion("El primer administrador solo se puede crear desde el propio ordenador", 403)
        nombre = validar_nombre(datos.get("usuario"))
        contrasena = validar_contrasena(datos.get("contrasena"))
        with self.server.almacen.lock:
            if self.server.almacen.hay_usuarios():
                raise ErrorPeticion("MiCine ya está configurado", 409)
            self.server.almacen.crear_usuario(nombre, contrasena, "admin")
        registrar(f"Administrador «{nombre}» creado")
        self.responder_sesion(nombre, para_app=False)

    def api_login(self) -> None:
        datos = self.leer_cuerpo()
        ip = self.ip_cliente()
        limitador = self.server.limitador
        espera = limitador.espera(ip)
        if espera:
            raise ErrorPeticion(f"Demasiados intentos fallidos. Espera {math.ceil(espera / 60)} min.", 429)
        nombre = str(datos.get("usuario", "")).strip().lower()
        contrasena = str(datos.get("contrasena", ""))[:200]
        registro = self.server.almacen.usuarios.get(nombre)
        if not comprobar_contrasena(contrasena, registro or HASH_FALSO) or not registro:
            limitador.fallo(ip)
            registrar(f"Acceso fallido para «{nombre}» desde {ip}")
            raise ErrorPeticion("Usuario o contraseña incorrectos", 401)
        limitador.exito(ip)
        registrar(f"«{nombre}» ha entrado desde {ip}{' (app)' if datos.get('app') else ''}")
        self.responder_sesion(nombre, para_app=bool(datos.get("app")))

    # --- API con sesión ---

    def api_logout(self, usuario: str) -> None:
        self.leer_cuerpo()
        self.server.almacen.cerrar_sesion(self.token())
        self.enviar_json({"ok": True}, cabeceras=self.galleta("", 0))

    def api_peliculas(self, usuario: str) -> None:
        almacen, biblioteca = self.server.almacen, self.server.biblioteca
        progreso = almacen.progreso_de(usuario)
        peliculas = biblioteca.listar()
        for p in peliculas:
            p["progreso"] = progreso.get(p["ruta"])
            p["url"] = almacen.enlace(usuario, p["ruta"])
            p["poster"] = almacen.enlace(usuario, p["poster"]) if p["poster"] else None
            for s in p["subtitulos"]:
                s["url"] = almacen.enlace(usuario, s["ruta"])
        # La dirección de este ordenador en la red de casa: la que hay que escribir en la tele.
        host, puerto = self.server.server_address[:2]
        ip = ip_en_red() if host in ("0.0.0.0", "") else None
        datos = {"peliculas": peliculas, "conversion": self.server.conversor is not None,
                 "red": f"http://{ip}:{puerto}" if ip else None}
        if almacen.rol(usuario) == "admin":
            datos.update(
                carpeta=str(biblioteca.carpeta),
                libre=shutil.disk_usage(biblioteca.carpeta).free,
                limite=int(self.server.config["limite_subida_gb"] * 1024 ** 3),
            )
        self.enviar_json(datos)

    def api_info(self, usuario: str) -> None:
        archivo = self.server.biblioteca.resolver(self.consulta.get("ruta", [""])[0])
        if not archivo or archivo.suffix.lower() not in VIDEOS:
            raise ErrorPeticion("Esa película ya no existe", 404)
        conversor = self.server.conversor
        if conversor and "clave" in self.consulta:
            return self.enviar_json({"clave": conversor.clave(archivo, self.numero("clave", 1e6))})
        self.enviar_json({**(conversor.info(archivo) if conversor else {}), "conversion": conversor is not None})

    def api_progreso(self, usuario: str) -> None:
        datos = self.leer_cuerpo()
        ruta, segundos, duracion = datos.get("ruta"), datos.get("t"), datos.get("d")
        numeros_validos = all(isinstance(x, (int, float)) and 0 <= x < 1e6 for x in (segundos, duracion))
        if not isinstance(ruta, str) or not numeros_validos or not self.server.biblioteca.resolver(ruta):
            raise ErrorPeticion("Datos de progreso no válidos")
        self.server.almacen.guardar_progreso(usuario, ruta, float(segundos), float(duracion))
        self.enviar_json({"ok": True})

    def api_mi_contrasena(self, usuario: str) -> None:
        datos = self.leer_cuerpo()
        almacen = self.server.almacen
        if not comprobar_contrasena(str(datos.get("actual", ""))[:200], almacen.usuarios[usuario]):
            raise ErrorPeticion("La contraseña actual no es correcta", 403)
        almacen.fijar_contrasena(usuario, validar_contrasena(datos.get("nueva")))
        almacen.cerrar_sesiones_de(usuario, salvo_token=self.token())
        self.enviar_json({"ok": True})

    def api_subir(self, usuario: str) -> None:
        """El navegador envía el archivo tal cual en el cuerpo; se escribe a disco por trozos."""
        biblioteca = self.server.biblioteca
        try:
            longitud = int(self.headers.get("Content-Length", ""))
        except ValueError:
            raise ErrorPeticion("Falta el tamaño del archivo", 411)
        limite = self.server.config["limite_subida_gb"]
        if longitud <= 0:
            raise ErrorPeticion("El archivo está vacío")
        if longitud > limite * 1024 ** 3:
            raise ErrorPeticion(f"El archivo supera el límite de {limite} GB (se cambia en config.json)", 413)
        nombre = biblioteca.nombre_seguro(urllib.parse.unquote(self.headers.get("X-Nombre-Archivo", "")))
        if longitud > shutil.disk_usage(biblioteca.carpeta).free - MARGEN_DISCO:
            raise ErrorPeticion("No queda espacio suficiente en el disco", 507)

        with biblioteca.lock:
            destino = biblioteca.destino_libre(nombre)
            temporal = destino.with_name(destino.name + ".subiendo")
            archivo = open(temporal, "xb")
        restante = longitud
        try:
            with archivo:
                while restante:
                    trozo = self.rfile.read(min(TROZO, restante))
                    if not trozo:
                        break
                    archivo.write(trozo)
                    restante -= len(trozo)
        finally:
            if restante:
                temporal.unlink(missing_ok=True)
        if restante:  # el navegador cortó la subida: ya no hay a quién responder
            self.close_connection = True
            registrar(f"Subida de «{nombre}» cancelada")
            return
        self._cuerpo_leido = True
        with biblioteca.lock:
            if destino.exists():
                destino = biblioteca.destino_libre(nombre)
            os.replace(temporal, destino)
        registrar(f"«{usuario}» ha subido «{destino.name}» ({longitud / 1024 ** 2:.0f} MB)")
        self.enviar_json({"ok": True, "nombre": destino.name}, 201)

    def api_borrar_pelicula(self, usuario: str) -> None:
        ruta = str(self.leer_cuerpo().get("ruta", ""))
        movidos = self.server.biblioteca.a_papelera(ruta)
        registrar(f"«{usuario}» ha movido «{ruta}» a la papelera ({movidos} archivo/s)")
        self.enviar_json({"ok": True})

    def api_usuarios(self, usuario: str) -> None:
        almacen = self.server.almacen
        with almacen.lock:
            lista = [{"nombre": n, "rol": u["rol"], "creado": u["creado"]} for n, u in sorted(almacen.usuarios.items())]
        self.enviar_json({"usuarios": lista})

    def api_crear_usuario(self, usuario: str) -> None:
        datos = self.leer_cuerpo()
        nombre = validar_nombre(datos.get("usuario"))
        contrasena = validar_contrasena(datos.get("contrasena"))
        rol = datos.get("rol")
        if rol not in ("admin", "usuario"):
            raise ErrorPeticion("Rol no válido")
        self.server.almacen.crear_usuario(nombre, contrasena, rol)
        registrar(f"«{usuario}» ha creado el usuario «{nombre}» ({rol})")
        self.enviar_json({"ok": True}, 201)

    def api_borrar_usuario(self, usuario: str) -> None:
        nombre = str(self.leer_cuerpo().get("usuario", ""))
        if nombre == usuario:
            raise ErrorPeticion("No puedes borrar tu propio usuario")
        self.server.almacen.borrar_usuario(nombre)
        registrar(f"«{usuario}» ha borrado el usuario «{nombre}»")
        self.enviar_json({"ok": True})

    def api_contrasena_usuario(self, usuario: str) -> None:
        datos = self.leer_cuerpo()
        nombre = str(datos.get("usuario", ""))
        almacen = self.server.almacen
        almacen.fijar_contrasena(nombre, validar_contrasena(datos.get("contrasena")))
        almacen.cerrar_sesiones_de(nombre, salvo_token=self.token() if nombre == usuario else None)
        registrar(f"«{usuario}» ha cambiado la contraseña de «{nombre}»")
        self.enviar_json({"ok": True})


def ip_en_red() -> str | None:
    """IP de este ordenador en la red de casa (no envía nada: solo pregunta la ruta)."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("10.255.255.255", 1))
            return s.getsockname()[0]
    except OSError:
        return None


def admin_por_consola(almacen: Almacen) -> int:
    print("Crear un administrador o cambiar su contraseña.")
    print("Hazlo con MiCine cerrado: si está abierto, ciérralo antes y vuelve a ejecutar esto.\n")
    try:
        nombre = validar_nombre(input("Usuario: "))
        contrasena = validar_contrasena(getpass.getpass("Contraseña (no se ve al escribir): "))
    except ErrorPeticion as error:
        print(error)
        return 1
    if getpass.getpass("Repite la contraseña: ") != contrasena:
        print("Las contraseñas no coinciden.")
        return 1
    if nombre in almacen.usuarios:
        almacen.fijar_contrasena(nombre, contrasena, rol="admin")
        almacen.cerrar_sesiones_de(nombre)
        print(f"Contraseña de «{nombre}» cambiada. Ahora es administrador.")
    else:
        almacen.crear_usuario(nombre, contrasena, "admin")
        print(f"Administrador «{nombre}» creado.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="MiCine: tus películas en el navegador y en el móvil.")
    parser.add_argument("--admin", action="store_true", help="crear un administrador o cambiar su contraseña")
    parser.add_argument("--puerto", type=int, help="puerto (por defecto, el de config.json)")
    parser.add_argument("--direccion", help="0.0.0.0 = toda la red de casa; 127.0.0.1 = solo este ordenador")
    parser.add_argument("--sin-navegador", action="store_true", help="no abrir el navegador al arrancar")
    args = parser.parse_args()

    config = cargar_config()
    almacen = Almacen(config["dias_sesion"])
    if args.admin:
        return admin_por_consola(almacen)

    otras = config.get("otras_carpetas") or []
    if not isinstance(otras, list) or not all(isinstance(x, str) for x in otras):
        print('«otras_carpetas» de config.json tiene que ser una lista de carpetas: ["D:\\\\Películas", …]')
        return 1
    biblioteca = Biblioteca(ruta_de_config(config["carpeta_peliculas"]), [ruta_de_config(x) for x in otras])
    puerto = args.puerto or config["puerto"]
    direccion = args.direccion or config["direccion"]
    ffmpeg = buscar_ffmpeg()
    try:
        servidor = ServidorMiCine((direccion, puerto), config, almacen, biblioteca, Conversor(*ffmpeg) if ffmpeg else None)
    except OSError as error:
        print(f"No se pudo abrir el puerto {puerto}: {error}")
        print("¿Ya hay otro MiCine abierto? Ciérralo o cambia el puerto en config.json.")
        return 1

    print("\n  MiCine está en marcha\n")
    print(f"  En este ordenador:      http://localhost:{puerto}")
    ip = ip_en_red()
    if ip and direccion in ("0.0.0.0", ""):
        print(f"  Móvil, tablet, tele…:   http://{ip}:{puerto}")
    print(f"  Carpeta de películas:   {biblioteca.carpeta}")
    for r in biblioteca.raices[1:]:
        print(f"  También:                {r.ruta}{'' if r.ruta.is_dir() else '  (no se encuentra: ¿disco desconectado?)'}")
    print(f"  Convertir formatos:     {'sí (FFmpeg)' if ffmpeg else 'no (falta FFmpeg; ver LEEME.md)'}")
    if not almacen.hay_usuarios():
        print(f"\n  Primera vez: abre http://localhost:{puerto} en ESTE ordenador para crear el administrador.")
    print("\n  Deja esta ventana abierta. Para apagar MiCine, ciérrala o pulsa Ctrl+C.\n")

    if config.get("abrir_navegador") and not args.sin_navegador:
        threading.Timer(1.0, webbrowser.open, [f"http://localhost:{puerto}"]).start()
    try:
        servidor.serve_forever()
    except KeyboardInterrupt:
        print("\nMiCine apagado.")
    finally:
        servidor.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
