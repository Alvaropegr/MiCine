"use strict";

// MiCine: interfaz web. Sin dependencias; todo se construye con el DOM (nunca con innerHTML,
// así un nombre de archivo raro no puede inyectar código).

const raiz = document.getElementById("app");
const EXT_VIDEO = ["mp4", "m4v", "webm", "ogv", "mkv", "mov", "avi", "wmv", "flv", "mpg", "mpeg", "m2ts", "3gp"];
const EXT_PERMITIDAS = [...EXT_VIDEO, "srt", "vtt", "jpg", "jpeg", "png", "webp"];

const estado = { usuario: null, peliculas: null, info: {}, busqueda: "", orden: "titulo" };
const subidas = []; // la cola sigue viva aunque cambies de pantalla
let subiendo = false;
let alSalirDeVista = null;
let generacion = 0; // descarta respuestas de pantallas que ya se han abandonado
let refrescarSubidas = () => {};

// ---------- utilidades ----------

function h(etiqueta, atributos, ...hijos) {
  const el = document.createElement(etiqueta);
  for (const [clave, valor] of Object.entries(atributos || {})) {
    if (valor == null || valor === false) continue;
    if (clave === "class") el.className = valor;
    else if (clave === "style") Object.assign(el.style, valor);
    else if (clave.startsWith("on")) el.addEventListener(clave.slice(2), valor);
    else el.setAttribute(clave, valor === true ? "" : valor);
  }
  for (const hijo of hijos.flat(Infinity)) {
    if (hijo == null || hijo === false) continue;
    el.append(hijo instanceof Node ? hijo : String(hijo));
  }
  return el;
}

function pintar(nodo) {
  raiz.replaceChildren(nodo);
}

function formatoTamano(bytes) {
  const unidades = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  while (bytes >= 1024 && i < unidades.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i >= 3 ? 1 : 0).replace(".", ",")} ${unidades[i]}`;
}

function formatoTiempo(segundos) {
  segundos = Math.max(0, Math.floor(segundos));
  const horas = Math.floor(segundos / 3600);
  const minutos = Math.floor((segundos % 3600) / 60);
  const resto = String(segundos % 60).padStart(2, "0");
  return horas ? `${horas}:${String(minutos).padStart(2, "0")}:${resto}` : `${minutos}:${resto}`;
}

function extension(nombre) {
  const punto = nombre.lastIndexOf(".");
  return punto < 0 ? "" : nombre.slice(punto + 1).toLowerCase();
}

function normalizar(texto) {
  return texto.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
}

function esAdmin() {
  return estado.usuario && estado.usuario.rol === "admin";
}

function enCurso(p) {
  return p.progreso && p.progreso.t > 15 && p.progreso.t < p.progreso.d * 0.95;
}

function aviso(texto, tipo = "exito") {
  let caja = document.getElementById("avisos");
  if (!caja) {
    caja = h("div", { id: "avisos", "aria-live": "polite" });
    document.body.append(caja);
  }
  const el = h("div", { class: `aviso ${tipo}` }, texto);
  caja.append(el);
  setTimeout(() => el.classList.add("saliendo"), 3600);
  setTimeout(() => el.remove(), 4100);
}

async function api(ruta, datos) {
  const opciones = { credentials: "same-origin" };
  if (datos !== undefined) {
    Object.assign(opciones, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-MiCine": "1" },
      body: JSON.stringify(datos),
    });
  }
  const respuesta = await fetch(ruta, opciones);
  const cuerpo = await respuesta.json().catch(() => ({}));
  if (respuesta.status === 401 && ruta !== "/api/login") {
    estado.usuario = null;
    vistaLogin("Tu sesión ha terminado. Vuelve a entrar.");
    const error = new Error("Sesión caducada");
    error.silencioso = true;
    throw error;
  }
  if (!respuesta.ok) throw new Error(cuerpo.error || `Error ${respuesta.status}`);
  return cuerpo;
}

// Último progreso guardado desde este navegador: el servidor puede recibirlo después
// de que el catálogo se recargue al salir del reproductor.
const progresoLocal = {};

async function cargarPeliculas() {
  const datos = await api("/api/peliculas");
  for (const p of datos.peliculas) {
    const local = progresoLocal[p.ruta];
    if (local && (!p.progreso || local.fecha > p.progreso.fecha)) p.progreso = local;
  }
  estado.peliculas = datos.peliculas;
  estado.info = datos;
  return datos;
}

function campo(etiqueta, control) {
  return h("label", { class: "campo" }, h("span", {}, etiqueta), control);
}

function logo() {
  return h("a", { class: "logo", href: "#/" }, h("span", { class: "logo-icono", "aria-hidden": "true" }, "▶"), "MiCine");
}

// Colores estables por título para las películas sin carátula.
function degradado(texto) {
  let hash = 0;
  for (const c of texto) hash = (hash * 31 + c.codePointAt(0)) | 0;
  const tono = Math.abs(hash) % 360;
  return `linear-gradient(160deg, hsl(${tono} 45% 32%), hsl(${(tono + 40) % 360} 55% 14%))`;
}

function pedirContrasena(titulo) {
  return new Promise((resolver) => {
    const entrada = h("input", { type: "password", minlength: "6", required: true, autocomplete: "new-password" });
    const dialogo = h("dialog", { class: "dialogo" },
      h("form", { method: "dialog" },
        h("h2", {}, titulo),
        campo("Nueva contraseña (mínimo 6 caracteres)", entrada),
        h("div", { class: "acciones-dialogo" },
          h("button", { type: "button", class: "boton secundario", onclick: () => dialogo.close("") }, "Cancelar"),
          h("button", { class: "boton", value: "ok" }, "Guardar"))));
    dialogo.addEventListener("close", () => {
      resolver(dialogo.returnValue === "ok" ? entrada.value : null);
      dialogo.remove();
    });
    document.body.append(dialogo);
    dialogo.showModal();
  });
}

// ---------- marco común ----------

function enlaceNav(destino, texto, activo, extra) {
  return h("a", { href: destino, class: activo ? "activo" : null }, texto, extra);
}

function insigniaSubidas() {
  const pendientes = subidas.filter((s) => s.estado === "cola" || s.estado === "subiendo").length;
  return h("span", { class: "insignia", id: "insignia-subidas", hidden: pendientes === 0 }, pendientes);
}

function actualizarInsignia() {
  const vieja = document.getElementById("insignia-subidas");
  if (vieja) vieja.replaceWith(insigniaSubidas());
}

function marco(activo, ...contenido) {
  return h("div", {},
    h("header", { class: "barra" },
      logo(),
      h("nav", { class: "nav" },
        enlaceNav("#/", "Películas", activo === "catalogo"),
        esAdmin() && enlaceNav("#/subir", "Subir", activo === "subir", insigniaSubidas()),
        esAdmin() && enlaceNav("#/usuarios", "Usuarios", activo === "usuarios"),
        enlaceNav("#/apps", "Apps", activo === "apps")),
      h("div", { class: "cuenta" },
        h("a", { href: "#/cuenta", title: "Mi cuenta" }, "👤 ", estado.usuario.nombre),
        h("button", { class: "boton-texto", onclick: salir }, "Salir"))),
    h("main", { class: "contenido" }, ...contenido));
}

async function salir() {
  if (subidas.some((s) => s.estado === "subiendo") && !confirm("Hay subidas en curso y se cancelarán. ¿Salir igualmente?")) return;
  subidas.forEach((s) => s.xhr && s.xhr.abort());
  try { await api("/api/logout", {}); } catch { /* da igual: se sale de todos modos */ }
  estado.usuario = null;
  estado.peliculas = null;
  history.replaceState(null, "", "#/");
  vistaLogin();
}

// ---------- acceso ----------

function vistaLogin(mensaje) {
  limpiarVista();
  const usuario = h("input", { name: "usuario", autocomplete: "username", required: true, autocapitalize: "none" });
  const contrasena = h("input", { name: "contrasena", type: "password", autocomplete: "current-password", required: true });
  const error = h("p", { class: "error-form", role: "alert" }, mensaje || "");
  const boton = h("button", { class: "boton ancho", type: "submit" }, "Entrar");
  const formulario = h("form", {
    class: "tarjeta-acceso",
    onsubmit: async (e) => {
      e.preventDefault();
      boton.disabled = true;
      error.textContent = "";
      try {
        const r = await api("/api/login", { usuario: usuario.value, contrasena: contrasena.value });
        estado.usuario = r.usuario;
        enrutar();
      } catch (err) {
        error.textContent = err.message;
        contrasena.select();
      } finally {
        boton.disabled = false;
      }
    },
  }, logo(), h("h1", {}, "Entrar"), campo("Usuario", usuario), campo("Contraseña", contrasena), error, boton);
  pintar(h("div", { class: "pantalla-acceso" }, formulario));
  usuario.focus();
}

function vistaConfigurar() {
  const usuario = h("input", { autocomplete: "username", required: true, minlength: "3", maxlength: "32", autocapitalize: "none" });
  const contrasena = h("input", { type: "password", autocomplete: "new-password", required: true, minlength: "6" });
  const repetir = h("input", { type: "password", autocomplete: "new-password", required: true, minlength: "6" });
  const error = h("p", { class: "error-form", role: "alert" });
  const boton = h("button", { class: "boton ancho", type: "submit" }, "Crear administrador");
  const formulario = h("form", {
    class: "tarjeta-acceso",
    onsubmit: async (e) => {
      e.preventDefault();
      if (contrasena.value !== repetir.value) { error.textContent = "Las contraseñas no coinciden"; return; }
      boton.disabled = true;
      try {
        const r = await api("/api/configurar", { usuario: usuario.value, contrasena: contrasena.value });
        estado.usuario = r.usuario;
        aviso("¡Listo! Ya puedes subir películas.");
        location.hash = "#/subir";
        enrutar();
      } catch (err) {
        error.textContent = err.message;
      } finally {
        boton.disabled = false;
      }
    },
  },
  logo(),
  h("h1", {}, "Bienvenido"),
  h("p", { class: "tenue" }, "Crea la cuenta de administrador. Con ella podrás subir películas y dar acceso a otras personas."),
  campo("Usuario", usuario), campo("Contraseña", contrasena), campo("Repite la contraseña", repetir), error, boton);
  pintar(h("div", { class: "pantalla-acceso" }, formulario));
  usuario.focus();
}

function vistaSoloLocal() {
  pintar(h("div", { class: "pantalla-acceso" },
    h("div", { class: "tarjeta-acceso" }, logo(), h("h1", {}, "Falta configurar MiCine"),
      h("p", { class: "tenue" }, "Por seguridad, el primer administrador solo se puede crear desde el ordenador donde está instalado MiCine."),
      h("p", {}, "Abre ", h("code", {}, `http://localhost:${location.port || 80}`), " en ese ordenador."))));
}

// ---------- catálogo ----------

async function vistaCatalogo() {
  const yo = ++generacion;
  if (!estado.peliculas) pintar(marco("catalogo", h("p", { class: "cargando" }, "Cargando películas…")));
  await cargarPeliculas();
  if (yo !== generacion) return;

  const rejilla = h("div", { class: "rejilla" });
  const seccionSeguir = h("section", {});
  const contador = h("p", { class: "tenue" });

  const buscador = h("input", {
    type: "search", placeholder: "Buscar película…", value: estado.busqueda, "aria-label": "Buscar",
    oninput: (e) => { estado.busqueda = e.target.value; actualizar(); },
  });
  const orden = h("select", {
    "aria-label": "Ordenar",
    onchange: (e) => { estado.orden = e.target.value; actualizar(); },
  },
  h("option", { value: "titulo", selected: estado.orden === "titulo" }, "A–Z"),
  h("option", { value: "recientes", selected: estado.orden === "recientes" }, "Añadidas recientemente"));

  function actualizar() {
    const consulta = normalizar(estado.busqueda.trim());
    let lista = estado.peliculas.filter((p) => normalizar(`${p.titulo} ${p.ruta}`).includes(consulta));
    if (estado.orden === "recientes") lista = [...lista].sort((a, b) => b.fecha - a.fecha);
    contador.textContent = `${estado.peliculas.length} película${estado.peliculas.length === 1 ? "" : "s"}`;

    const seguir = estado.peliculas.filter(enCurso).sort((a, b) => b.progreso.fecha - a.progreso.fecha);
    seccionSeguir.replaceChildren(...(seguir.length && !consulta
      ? [h("h2", {}, "Seguir viendo"), h("div", { class: "fila-horizontal" }, seguir.map(tarjeta))]
      : []));

    rejilla.replaceChildren(...lista.map(tarjeta));
    rejilla.hidden = !lista.length;
    vacio.replaceChildren(...mensajeVacio(consulta, lista.length));
    vacio.hidden = lista.length > 0;
  }

  const vacio = h("div", { class: "vacio" });
  pintar(marco("catalogo",
    h("div", { class: "cabecera-catalogo" },
      h("div", {}, h("h1", {}, "Películas"), contador),
      h("div", { class: "filtros" }, buscador, orden)),
    seccionSeguir,
    h("h2", { hidden: !estado.peliculas.length }, "Todas"),
    rejilla,
    vacio));
  actualizar();
}

function mensajeVacio(consulta, cuantas) {
  if (cuantas) return [];
  if (consulta) return [h("div", { class: "grande" }, "🔍"), h("p", {}, "Ninguna película coincide con la búsqueda.")];
  if (esAdmin()) {
    return [
      h("div", { class: "grande" }, "🎬"),
      h("p", {}, "Todavía no hay películas."),
      h("p", {}, h("a", { class: "boton", href: "#/subir" }, "Subir películas")),
      h("p", { class: "tenue" }, "También puedes copiarlas directamente en ", h("code", {}, estado.info.carpeta || "la carpeta de películas")),
    ];
  }
  return [h("div", { class: "grande" }, "🎬"), h("p", {}, "Todavía no hay películas. Pide al administrador que suba alguna.")];
}

function tarjeta(p) {
  const pct = p.progreso && p.progreso.d ? Math.min(100, (p.progreso.t / p.progreso.d) * 100) : 0;
  const portada = p.poster
    ? h("img", { src: p.poster, alt: "", loading: "lazy" })
    : h("div", { class: "portada-vacia", style: { background: degradado(p.titulo) } },
      h("span", { class: "inicial" }, p.titulo.trim().charAt(0).toUpperCase()),
      h("span", { class: "nombre" }, p.titulo));
  const etiquetaFormato = { quizas: "Puede fallar en el navegador", no: "Formato no compatible con el navegador: ábrelo con VLC" }[p.compat];
  return h("div", { class: "tarjeta" },
    h("a", { class: "tarjeta-enlace", href: `#/ver/${encodeURIComponent(p.ruta)}`, title: p.ruta },
      h("div", { class: "portada" },
        portada,
        h("div", { class: "reproducir", "aria-hidden": "true" }, "▶"),
        p.compat !== "si" && !estado.info.conversion && h("span", { class: `chapa ${p.compat}`, title: etiquetaFormato }, extension(p.ruta)),
        pct >= 95 && h("span", { class: "chapa vista" }, "✓ Vista"),
        pct > 0 && pct < 95 && h("div", { class: "barra-progreso" }, h("div", { style: { width: `${pct}%` } }))),
      h("div", { class: "tarjeta-info" },
        h("div", { class: "tarjeta-titulo" }, p.titulo),
        h("div", { class: "tarjeta-meta" }, [p.carpeta, formatoTamano(p.tamano)].filter(Boolean).join(" · ")))),
    esAdmin() && h("button", {
      class: "tarjeta-borrar", title: "Quitar película", "aria-label": `Quitar ${p.titulo}`,
      onclick: () => borrarPelicula(p),
    }, "🗑"));
}

async function borrarPelicula(p) {
  const pregunta = `¿Quitar «${p.titulo}» de MiCine?\n\n` +
    "El archivo (con sus subtítulos y carátula) se moverá a la carpeta .papelera, dentro de tu carpeta de películas. Desde ahí puedes recuperarlo o borrarlo del todo.";
  if (!confirm(pregunta)) return;
  try {
    await api("/api/peliculas/borrar", { ruta: p.ruta });
    aviso("Película movida a la papelera");
    vistaCatalogo();
  } catch (e) {
    aviso(e.message, "error");
  }
}

// ---------- reproductor ----------

// Lo que el navegador reproduce por sí mismo. Lo demás lo convierte MiCine al momento (si el
// ordenador tiene FFmpeg): solo el sonido cuando la imagen vale (lo típico con DTS o AC3), o todo.
const AUDIO_NAVEGADOR = ["aac", "mp3", "opus", "vorbis", "flac"];
const VIDEO_NAVEGADOR = {
  h264: 'video/mp4; codecs="avc1.640028"',
  hevc: 'video/mp4; codecs="hvc1.1.6.L120.90"',
  vp9: 'video/webm; codecs="vp9"',
  vp8: 'video/webm; codecs="vp8"',
  av1: 'video/mp4; codecs="av01.0.08M.08"',
  theora: 'video/ogg; codecs="theora"',
};
const VIDEO_COPIABLE = ["h264", "hevc", "vp9", "av1"]; // los que caben tal cual en el MP4 convertido
const NIVELES = ["directo", "audio", "video"];

const ICONOS = {
  play: "M8 5v14l11-7z",
  pausa: "M6 19h4V5H6v14zm8-14v14h4V5h-4z",
  atras: "M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z",
  adelante: "M12 5V1l5 5-5 5V7c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6h2c0 4.42-3.58 8-8 8s-8-3.58-8-8 3.58-8 8-8z",
  sonido: "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05A4.47 4.47 0 0 0 16.5 12zM14 3.23v2.06a7 7 0 0 1 0 13.42v2.06A9 9 0 0 0 14 3.23z",
  silencio: "M16.5 12A4.5 4.5 0 0 0 14 7.97v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51A8.8 8.8 0 0 0 21 12a9 9 0 0 0-7-8.77v2.06A7 7 0 0 1 19 12zM4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06a8.99 8.99 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4 9.91 6.09 12 8.18V4z",
  completa: "M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z",
  salirCompleta: "M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z",
};

function icono(nombre) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  const trazo = document.createElementNS(svg.namespaceURI, "path");
  trazo.setAttribute("d", ICONOS[nombre]);
  trazo.setAttribute("fill", "currentColor");
  svg.append(trazo);
  return svg;
}

const nombresDeIdioma = (() => {
  try { return new Intl.DisplayNames(["es"], { type: "language" }); } catch { return null; }
})();

/** «spa», «eng», «fre»… (como vienen en los MKV) en español. */
function nombreIdioma(codigo) {
  if (!codigo || codigo === "und") return "";
  try {
    const nombre = nombresDeIdioma && nombresDeIdioma.of(codigo);
    if (nombre && nombre.toLowerCase() !== codigo.toLowerCase()) return nombre.charAt(0).toUpperCase() + nombre.slice(1);
  } catch { /* código que el navegador no conoce */ }
  return codigo.toUpperCase();
}

function esEspanol(codigo) {
  return ["es", "spa", "esp"].includes(String(codigo || "").toLowerCase().split("-")[0]);
}

function nombreCanales(n) {
  return { 1: "Mono", 2: "Estéreo", 6: "5.1", 8: "7.1" }[n] || (n ? `${n} canales` : "");
}

/** Frases de un WebVTT con sus tiempos en segundos. */
function leerVTT(texto) {
  const segundos = (s) => s.trim().split(":").map(Number).reduce((total, x) => total * 60 + x, 0);
  const entidades = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&nbsp;": " ", "&lrm;": "", "&rlm;": "" };
  const frases = [];
  for (const bloque of texto.replace(/\r/g, "").split(/\n{2,}/)) {
    const lineas = bloque.split("\n");
    const i = lineas.findIndex((l) => l.includes("-->"));
    if (i < 0) continue;
    const [ini, resto] = lineas[i].replace(/,/g, ".").split("-->");
    const frase = lineas.slice(i + 1).join("\n").replace(/<[^>]*>/g, "").replace(/&[a-z]+;/g, (e) => entidades[e] ?? e).trim();
    if (frase) frases.push({ ini: segundos(ini), fin: segundos(resto.trim().split(/\s+/)[0]), texto: frase });
  }
  return frases;
}

async function vistaReproductor(ruta) {
  const yo = ++generacion;
  if (!estado.peliculas) await cargarPeliculas();
  if (yo !== generacion) return;
  const p = estado.peliculas.find((x) => x.ruta === ruta);
  if (!p) {
    aviso("Esa película ya no está disponible", "error");
    location.hash = "#/";
    return;
  }

  let info = null;          // pistas del archivo, si MiCine puede convertir
  let modo = "directo";     // directo, audio (se convierte el sonido) o video (se convierte todo)
  let forzado = 0;          // nivel mínimo tras un fallo: no se vuelve a probar lo que no funcionó
  let audioN = 0, desfase = 0, posicion = 0, saltoInicial = 0, aperturas = 0, reintentos = 0;
  let abriendo = true, sonidoComprobado = false, arrastrando = false, vivo = true;
  let objetivo = null, tSalto = 0, tOcultar = 0, tocadoOculto = false;
  let frases = [], fraseActual = null, cargasSub = 0, cargandoSub = false;
  let imagenEntendida = true, imagenCopiable = false, audioPorDefecto = null, fuentesSub = [];

  const video = h("video", { autoplay: true, playsinline: true, preload: "auto" });
  const textoSub = h("span", {});
  const capaSub = h("div", { class: "subtitulos-video", hidden: true }, textoSub);
  const rueda = h("div", { class: "rueda-video" });
  const linea = h("input", { type: "range", class: "linea-tiempo", min: 0, max: 0, step: "any", value: 0, "aria-label": "Posición" });
  const reloj = h("span", { class: "reloj-video" }, "0:00");
  const etiquetaModo = h("span", { class: "modo-video" });
  const botonPlay = h("button", { class: "control", title: "Reproducir o pausar (espacio)", "aria-label": "Reproducir o pausar", onclick: () => alternar() }, icono("pausa"));
  const botonSonido = h("button", { class: "control", title: "Silenciar (M)", "aria-label": "Silenciar", onclick: () => { video.muted = !video.muted; } }, icono("sonido"));
  const volumen = h("input", {
    type: "range", class: "volumen", min: 0, max: 1, step: 0.05, value: 1, "aria-label": "Volumen",
    oninput: (e) => { video.volume = Number(e.target.value); video.muted = false; },
  });
  const selectorAudio = h("select", { class: "selector-video", "aria-label": "Audio", title: "Audio", hidden: true, onchange: (e) => elegirAudio(Number(e.target.value)) });
  const selectorSubs = h("select", {
    class: "selector-video", "aria-label": "Subtítulos", title: "Subtítulos", hidden: true,
    onchange: (e) => elegirSubtitulo(e.target.value === "" ? null : Number(e.target.value)),
  });
  const botonCompleta = h("button", { class: "control", title: "Pantalla completa (F)", "aria-label": "Pantalla completa", onclick: () => pantallaCompleta() }, icono("completa"));
  const controles = h("div", { class: "controles-video" },
    linea,
    h("div", { class: "fila-controles" },
      botonPlay,
      h("button", { class: "control con-numero", title: "Atrás 10 s (←)", "aria-label": "Atrás 10 segundos", onclick: () => saltar(-10) }, icono("atras"), h("span", {}, "10")),
      h("button", { class: "control con-numero", title: "Adelante 10 s (→)", "aria-label": "Adelante 10 segundos", onclick: () => saltar(10) }, icono("adelante"), h("span", {}, "10")),
      botonSonido, volumen, reloj, etiquetaModo, h("span", { class: "hueco" }),
      selectorAudio, selectorSubs, botonCompleta));
  const marcoVideo = h("div", { class: "marco-video" }, video, capaSub, rueda, controles);
  const avisos = h("div", {});

  const absoluta = new URL(p.url, location.href).href;
  const copiarEnlace = async () => {
    try {
      await navigator.clipboard.writeText(absoluta);
      aviso("Enlace copiado. En VLC: Medio → Abrir ubicación de red → pegar.");
    } catch {
      prompt("Copia este enlace y ábrelo en VLC (Medio → Abrir ubicación de red):", absoluta);
    }
  };

  pintar(h("div", {},
    marco("catalogo").firstChild, // solo la barra superior
    h("div", { class: "reproductor" },
      h("div", { class: "reproductor-cabecera" },
        h("button", { class: "boton secundario pequeno", onclick: () => history.length > 1 ? history.back() : (location.hash = "#/") }, "← Volver"),
        h("h1", {}, p.titulo),
        h("button", { class: "boton secundario pequeno", onclick: copiarEnlace, title: "Para verla con VLC u otro reproductor" }, "Abrir en VLC"),
        h("a", { class: "boton secundario pequeno", href: `${p.url}&descargar=1` }, "Descargar")),
      marcoVideo,
      avisos,
      h("p", { class: "atajos" },
        h("kbd", {}, "Espacio"), " pausa · ", h("kbd", {}, "←"), " ", h("kbd", {}, "→"), " 10 s · ",
        h("kbd", {}, "F"), " pantalla completa · ", h("kbd", {}, "M"), " silencio"))));

  if (p.compat !== "si" && !estado.info.conversion) {
    avisos.append(h("div", { class: "franja alerta" },
      h("p", {}, p.compat === "no"
        ? `El navegador no suele reproducir archivos .${extension(p.ruta)}. Si no arranca, ábrela con VLC o usa la app de Android, que reproduce cualquier formato.`
        : `Los .${extension(p.ruta)} a veces se ven sin sonido o no arrancan en el navegador, según cómo se grabaron. Si pasa, ábrela con VLC o con la app de Android.`)));
  }

  // ---- tiempo: al convertir, el vídeo empieza en el segundo pedido (desfase), no en el 0

  const tiempo = () => (modo === "directo" ? video.currentTime : desfase + video.currentTime);
  const ahora = () => (abriendo ? posicion : tiempo());
  const duracion = () => {
    if (modo !== "directo" && info && info.duracion) return info.duracion;
    return isFinite(video.duration) && video.duration > 0 ? video.duration : (info && info.duracion) || 0;
  };
  const redondear = (x) => Math.round(x * 1000) / 1000;

  function pintarTiempo() {
    const d = duracion();
    const t = objetivo ?? (arrastrando ? Number(linea.value) : ahora());
    if (!arrastrando) {
      linea.max = d;
      linea.value = Math.min(t, d);
    }
    reloj.textContent = d ? `${formatoTiempo(t)} / ${formatoTiempo(d)}` : formatoTiempo(t);
  }

  // ---- directo o convertido

  function elegirModo() {
    if (!info) return "directo";
    const audio = info.audios.find((a) => a.n === audioN);
    // El navegador solo pone la pista de audio principal: otra hay que pedírsela a MiCine.
    const sonidoEntendido = !audio || (AUDIO_NAVEGADOR.includes(audio.codec) && audio === audioPorDefecto);
    let nivel = p.compat !== "no" && imagenEntendida && sonidoEntendido ? 0 : imagenCopiable ? 1 : 2;
    nivel = Math.max(nivel, forzado);
    return NIVELES[nivel === 1 && !imagenCopiable ? 2 : nivel];
  }

  function pasarAConvertir(motivo) {
    const antes = modo;
    forzado = antes === "directo" && imagenCopiable && motivo !== "imagen" ? 1 : 2;
    modo = NIVELES[forzado];
    if (antes === "directo") {
      aviso(motivo === "sonido"
        ? "Tu navegador no entiende el sonido de este archivo: MiCine lo está convirtiendo."
        : "Tu navegador no puede con el archivo original: MiCine lo está convirtiendo.");
    }
    abrir(posicion);
  }

  async function abrir(inicio) {
    const n = ++aperturas;
    abriendo = true;
    posicion = inicio;
    sonidoComprobado = false;
    rueda.hidden = false;
    if (modo === "audio" && inicio >= 1) {
      // La imagen sin convertir solo puede empezar en un fotograma completo (clave), a veces unos
      // segundos antes: MiCine dice cuál, para que el reloj y los subtítulos cuadren.
      try {
        const r = await api(`/api/info?ruta=${encodeURIComponent(p.ruta)}&clave=${redondear(inicio)}`);
        if (typeof r.clave === "number") inicio = posicion = r.clave;
      } catch (e) {
        if (e.silencioso) return;
      }
      if (n !== aperturas || !vivo) return;
    }
    if (modo === "directo") {
      desfase = 0;
      saltoInicial = inicio;
      video.src = p.url;
    } else {
      desfase = inicio;
      saltoInicial = 0;
      video.src = `${p.url}&convertir=1&t=${redondear(inicio)}&audio=${audioN}${modo === "audio" ? "&video=copia" : ""}&formato=web`;
    }
    etiquetaModo.textContent = { directo: "", audio: "Sonido convertido por MiCine", video: "Convertida por MiCine" }[modo];
    video.play().catch(() => {});
    pintarTiempo();
  }

  function irA(segundos) {
    const d = duracion();
    segundos = Math.max(0, d ? Math.min(segundos, Math.max(0, d - 1)) : segundos);
    posicion = segundos;
    if (modo === "directo") {
      saltoInicial = segundos; // por si aún no ha cargado: que no se reanude donde se dejó
      video.currentTime = segundos;
    } else {
      abrir(segundos);
    }
    pintarTiempo();
    pintarSubtitulo();
  }

  /** Flechas y botones de 10 s: al convertir, se espera a que dejes de pulsar para no abrir el vídeo una vez por pulsación. */
  function saltar(delta) {
    const d = duracion();
    objetivo = Math.max(0, Math.min((objetivo ?? ahora()) + delta, d || Infinity));
    clearTimeout(tSalto);
    mostrarControles();
    if (modo === "directo") {
      const t = objetivo;
      objetivo = null;
      return irA(t);
    }
    pintarTiempo();
    tSalto = setTimeout(() => {
      const t = objetivo;
      objetivo = null;
      irA(t);
    }, 700);
  }

  function alternar() {
    if (video.ended) return irA(0);
    if (video.paused) video.play().catch(() => {});
    else video.pause();
  }

  function pantallaCompleta() {
    if (document.fullscreenElement) return document.exitFullscreen().catch(() => {});
    if (marcoVideo.requestFullscreen) marcoVideo.requestFullscreen().catch(() => {});
    else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen(); // iPhone
  }
  const alCambiarCompleta = () => botonCompleta.replaceChildren(icono(document.fullscreenElement ? "salirCompleta" : "completa"));
  document.addEventListener("fullscreenchange", alCambiarCompleta);

  function mostrarControles() {
    marcoVideo.classList.remove("quieto");
    clearTimeout(tOcultar);
    tOcultar = setTimeout(() => {
      if (vivo && !video.paused && !arrastrando) marcoVideo.classList.add("quieto");
    }, 3000);
  }

  function fallo() {
    rueda.hidden = true;
    avisos.replaceChildren(h("div", { class: "franja alerta" },
      h("p", {}, info
        ? "No se ha podido reproducir ni convirtiéndola: puede que el archivo esté dañado. Prueba a abrirlo con VLC."
        : "Tu navegador no puede reproducir este archivo. Ábrelo con VLC o con la app de Android de MiCine, que reproducen cualquier formato."),
      h("button", { class: "boton pequeno", onclick: copiarEnlace }, "Copiar enlace para VLC")));
  }

  // ---- pistas de audio

  function pintarAudios() {
    const audios = info ? info.audios : [];
    selectorAudio.replaceChildren(...audios.map((a) => h("option", { value: a.n, selected: a.n === audioN },
      [nombreIdioma(a.idioma) || `Audio ${a.n + 1}`, a.titulo, nombreCanales(a.canales), String(a.codec).toUpperCase()].filter(Boolean).join(" · "))));
    selectorAudio.hidden = audios.length < 2;
  }

  function elegirAudio(n) {
    const t = ahora();
    audioN = n;
    modo = elegirModo();
    abrir(t);
  }

  // ---- subtítulos: los .srt/.vtt de al lado y los de texto que van dentro del vídeo. Se pintan
  // aquí (no con <track>) porque, al convertir, el vídeo no empieza en el segundo 0.

  function pintarListaSubtitulos() {
    fuentesSub = [
      ...p.subtitulos.map((s) => ({ nombre: s.etiqueta, idioma: s.idioma, url: s.url })),
      ...(info ? info.subtitulos : []).map((s) => ({
        nombre: `${[nombreIdioma(s.idioma) || `Subtítulos ${s.n + 1}`, s.titulo].filter(Boolean).join(" · ")}${s.texto ? "" : " (imagen: no disponible)"}`,
        idioma: s.idioma,
        url: s.texto ? `${p.url}&subtitulo=${s.n}` : null,
      })),
    ];
    selectorSubs.replaceChildren(h("option", { value: "" }, "Sin subtítulos"),
      ...fuentesSub.map((s, i) => h("option", { value: i, disabled: !s.url }, s.nombre)));
    selectorSubs.hidden = !fuentesSub.length;
  }

  async function elegirSubtitulo(i) {
    const n = ++cargasSub;
    frases = [];
    cargandoSub = i !== null;
    selectorSubs.value = i === null ? "" : String(i);
    pintarSubtitulo();
    if (i === null) return;
    try {
      const r = await fetch(fuentesSub[i].url, { credentials: "same-origin" });
      if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || `Error ${r.status}`);
      const leidas = leerVTT(await r.text());
      if (n !== cargasSub || !vivo) return;
      frases = leidas;
    } catch (e) {
      if (n !== cargasSub || !vivo) return;
      aviso(`No se pudieron cargar los subtítulos: ${e.message}`, "error");
      selectorSubs.value = "";
    }
    cargandoSub = false;
    pintarSubtitulo();
  }

  function pintarSubtitulo() {
    const t = ahora();
    const texto = cargandoSub ? "Cargando subtítulos…"
      : frases.filter((f) => f.ini <= t && t < f.fin).map((f) => f.texto).join("\n");
    if (texto === fraseActual) return;
    fraseActual = texto;
    textoSub.textContent = texto;
    capaSub.hidden = !texto;
  }

  // ---- eventos del vídeo

  video.addEventListener("loadedmetadata", () => {
    if (modo === "directo" && saltoInicial > 0) video.currentTime = saltoInicial;
    // Hay navegadores que, sin entender la imagen, ponen solo el sonido sin dar error.
    if (modo === "directo" && info && info.video && !video.videoWidth) return pasarAConvertir("imagen");
    pintarTiempo();
  });
  video.addEventListener("canplay", () => {
    if (modo !== "directo" || Math.abs(video.currentTime - saltoInicial) < 3) {
      abriendo = false;
      rueda.hidden = true;
    }
  });
  video.addEventListener("playing", () => {
    abriendo = false;
    reintentos = 0;
    rueda.hidden = true;
  });
  video.addEventListener("waiting", () => { rueda.hidden = false; });
  video.addEventListener("play", () => botonPlay.replaceChildren(icono("pausa")));
  video.addEventListener("pause", () => {
    botonPlay.replaceChildren(icono("play"));
    mostrarControles();
    guardar();
  });
  video.addEventListener("volumechange", () => {
    botonSonido.replaceChildren(icono(video.muted || !video.volume ? "silencio" : "sonido"));
    volumen.value = video.muted ? 0 : video.volume;
  });
  video.addEventListener("error", () => {
    if (!vivo || !video.getAttribute("src")) return;
    if (info && modo === "directo") return pasarAConvertir("error");
    if (info && reintentos < 1) {
      // Quizá el ordenador estaba ocupado con otras conversiones: se prueba otra vez.
      reintentos++;
      return setTimeout(() => { if (vivo) abrir(posicion); }, 1500);
    }
    if (info && modo === "audio") {
      reintentos = 0;
      forzado = 2;
      modo = "video";
      return abrir(posicion);
    }
    fallo();
  });
  video.addEventListener("click", () => { if (!tocadoOculto) alternar(); });
  video.addEventListener("dblclick", pantallaCompleta);
  marcoVideo.addEventListener("pointermove", mostrarControles);
  marcoVideo.addEventListener("pointerdown", (e) => {
    tocadoOculto = e.pointerType === "touch" && marcoVideo.classList.contains("quieto");
    mostrarControles();
  });
  linea.addEventListener("input", () => {
    arrastrando = true;
    pintarTiempo();
    mostrarControles();
  });
  linea.addEventListener("change", () => {
    arrastrando = false;
    linea.blur();
    irA(Number(linea.value));
  });

  // Guardar el progreso cada 10 s, al pausar y al salir
  let ultimoGuardado = Date.now();
  const guardar = () => {
    const d = duracion();
    const t = video.ended ? d : ahora();
    if (!d || t < 1) return;
    p.progreso = progresoLocal[p.ruta] = { t, d, fecha: Date.now() / 1000 };
    fetch("/api/progreso", {
      method: "POST", keepalive: true, credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-MiCine": "1" },
      body: JSON.stringify({ ruta: p.ruta, t, d }),
    }).catch(() => {});
  };
  video.addEventListener("timeupdate", () => {
    if (!abriendo) posicion = tiempo();
    pintarTiempo();
    pintarSubtitulo();
    // Algunos navegadores ponen la imagen sin sonido (AC3 dentro de un MKV, por ejemplo) sin avisar.
    if (!sonidoComprobado && !abriendo && modo === "directo" && info && info.audios.length && video.currentTime - saltoInicial > 3) {
      sonidoComprobado = true;
      if (video.webkitAudioDecodedByteCount === 0 || video.mozHasAudio === false) return pasarAConvertir("sonido");
    }
    if (Date.now() - ultimoGuardado > 10000) { ultimoGuardado = Date.now(); guardar(); }
  });
  video.addEventListener("seeked", () => { pintarTiempo(); pintarSubtitulo(); });
  video.addEventListener("ended", () => {
    posicion = duracion() || posicion;
    guardar();
    mostrarControles();
  });

  const teclas = (e) => {
    if (e.target.closest("select, textarea, input:not([type=range])") || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target.closest("button") && (e.key === " " || e.key === "Enter")) return; // el botón ya hace lo suyo
    switch (e.key) {
      case " ": case "k": alternar(); break;
      case "ArrowRight": saltar(10); break;
      case "ArrowLeft": saltar(-10); break;
      case "f": case "F": pantallaCompleta(); break;
      case "m": case "M": video.muted = !video.muted; break;
      default: return;
    }
    e.preventDefault();
    mostrarControles();
  };
  document.addEventListener("keydown", teclas);
  window.addEventListener("pagehide", guardar);

  alSalirDeVista = () => {
    guardar();
    vivo = false;
    clearTimeout(tSalto);
    clearTimeout(tOcultar);
    document.removeEventListener("keydown", teclas);
    document.removeEventListener("fullscreenchange", alCambiarCompleta);
    window.removeEventListener("pagehide", guardar);
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    video.pause();
    video.removeAttribute("src");
    video.load(); // corta la descarga del vídeo (y la conversión en el ordenador)
  };

  // ---- arranque: con FFmpeg en el ordenador, primero se pregunta qué lleva el archivo

  if (estado.info.conversion) {
    try {
      info = await api(`/api/info?ruta=${encodeURIComponent(p.ruta)}`);
    } catch (e) {
      if (e.silencioso) return;
    }
    if (!vivo) return;
    if (info && !info.conversion) info = null;
  }
  if (info) {
    const v = info.video;
    if (v) {
      const tipo = v.codec === "hevc" && v.bits > 8 ? 'video/mp4; codecs="hvc1.2.4.L120.90"' : VIDEO_NAVEGADOR[v.codec];
      imagenEntendida = !!tipo && !(v.codec === "h264" && v.bits > 8) && video.canPlayType(tipo) !== "";
      imagenCopiable = imagenEntendida && VIDEO_COPIABLE.includes(v.codec);
    }
    audioPorDefecto = info.audios.find((a) => a.defecto) || info.audios[0] || null;
    audioN = audioPorDefecto ? audioPorDefecto.n : 0;
    modo = elegirModo();
  }
  pintarAudios();
  pintarListaSubtitulos();

  // Si la película no está en español, se ponen los subtítulos en español (si los hay).
  const audio = info && info.audios.find((a) => a.n === audioN);
  if (audio && audio.idioma && audio.idioma !== "und" && !esEspanol(audio.idioma)) {
    const i = fuentesSub.findIndex((s) => s.url && esEspanol(s.idioma));
    if (i >= 0) elegirSubtitulo(i);
  }

  // Reanudar donde se dejó
  const previo = p.progreso;
  let inicio = 0;
  if (previo && previo.t > 15 && previo.t < previo.d * 0.95) {
    inicio = previo.t;
    const franja = h("div", { class: "franja" },
      h("p", {}, `Continuando desde ${formatoTiempo(previo.t)}.`),
      h("button", { class: "boton secundario pequeno", onclick: () => { irA(0); franja.remove(); } }, "Empezar desde el principio"));
    avisos.append(franja);
    setTimeout(() => franja.remove(), 12000);
  }
  abrir(inicio);
  mostrarControles();
}

// ---------- subida ----------

async function vistaSubir() {
  const yo = ++generacion;
  if (!estado.info.carpeta) await cargarPeliculas();
  if (yo !== generacion) return;

  const entrada = h("input", {
    type: "file", multiple: true, hidden: true,
    accept: EXT_PERMITIDAS.map((e) => `.${e}`).join(","),
    onchange: (e) => { encolar([...e.target.files]); e.target.value = ""; },
  });
  const zona = h("div", {
    class: "zona-subida",
    ondragover: (e) => { e.preventDefault(); zona.classList.add("encima"); },
    ondragleave: () => zona.classList.remove("encima"),
    ondrop: (e) => { e.preventDefault(); zona.classList.remove("encima"); encolar([...e.dataTransfer.files]); },
  },
  h("div", { class: "zona-icono" }, "⬆"),
  h("p", {}, h("strong", {}, "Arrastra aquí tus películas")),
  h("p", { class: "tenue" }, "o"),
  h("button", { class: "boton", onclick: () => entrada.click() }, "Elegir archivos"),
  entrada);

  const lista = h("div", { class: "lista-subidas" });
  const limpiar = h("button", {
    class: "boton secundario pequeno",
    onclick: () => {
      for (let i = subidas.length - 1; i >= 0; i--) if (["hecho", "error", "cancelado"].includes(subidas[i].estado)) subidas.splice(i, 1);
      refrescarSubidas();
    },
  }, "Quitar terminadas");

  pintar(marco("subir",
    h("h1", {}, "Subir películas"),
    h("p", { class: "tenue" }, "Se guardan en ", h("code", {}, estado.info.carpeta), ` · Libre: ${formatoTamano(estado.info.libre)}`),
    zona,
    h("div", { class: "cabecera-catalogo" }, h("div", {}, h("h2", { id: "titulo-cola" }, "Cola de subida")), limpiar),
    lista,
    h("div", { class: "consejos" },
      h("h2", {}, "Consejos"),
      h("ul", {},
        estado.info.conversion
          ? h("li", {}, "Se puede subir cualquier formato: lo que el navegador no entienda (.avi, .wmv, audio DTS…), MiCine lo convierte al verlo.")
          : h("li", {}, "Las ", h("strong", {}, ".mp4"), " se ven en cualquier navegador. Las .mkv suelen ir en Chrome y Edge. Para .avi, .wmv y otros formatos usa la app de Android o VLC."),
        h("li", {}, "Subtítulos y carátula: súbelos con el mismo nombre que la película, por ejemplo ", h("code", {}, "Alien (1979).mp4"), ", ", h("code", {}, "Alien (1979).es.srt"), " y ", h("code", {}, "Alien (1979).jpg"), "."),
        h("li", {}, "Puedes cambiar de pantalla mientras sube; no cierres la pestaña hasta que termine."),
        h("li", {}, "Si las películas ya están en este ordenador, es más rápido copiarlas directamente a la carpeta.")))));

  refrescarSubidas = () => {
    lista.replaceChildren(...subidas.map(filaSubida));
    const hay = subidas.length > 0;
    document.getElementById("titulo-cola").parentElement.parentElement.hidden = !hay;
    limpiar.hidden = !subidas.some((s) => ["hecho", "error", "cancelado"].includes(s.estado));
    actualizarInsignia();
  };
  refrescarSubidas();
  alSalirDeVista = () => { refrescarSubidas = actualizarInsignia; };
}

function encolar(archivos) {
  for (const archivo of archivos) {
    const item = { archivo, nombre: archivo.name, tamano: archivo.size, enviado: 0, estado: "cola", error: "", xhr: null, fila: null };
    if (!EXT_PERMITIDAS.includes(extension(archivo.name))) {
      Object.assign(item, { estado: "error", error: "Tipo de archivo no permitido" });
    } else if (estado.info.limite && archivo.size > estado.info.limite) {
      Object.assign(item, { estado: "error", error: `Supera el límite de ${formatoTamano(estado.info.limite)} (se cambia en config.json)` });
    } else if (estado.info.libre && archivo.size > estado.info.libre - 500 * 1024 ** 2) {
      Object.assign(item, { estado: "error", error: "No queda espacio suficiente en el disco del ordenador" });
    }
    subidas.push(item);
  }
  refrescarSubidas();
  procesarCola();
}

function procesarCola() {
  if (subiendo) return;
  const item = subidas.find((s) => s.estado === "cola");
  if (!item) return;
  subiendo = true;
  Object.assign(item, { estado: "subiendo", inicio: performance.now() });

  const xhr = new XMLHttpRequest();
  item.xhr = xhr;
  xhr.open("POST", "/api/subir");
  xhr.setRequestHeader("X-MiCine", "1");
  xhr.setRequestHeader("X-Nombre-Archivo", encodeURIComponent(item.nombre));
  xhr.setRequestHeader("Content-Type", "application/octet-stream");
  xhr.upload.onprogress = (e) => { item.enviado = e.loaded; actualizarFila(item); };
  xhr.onload = () => {
    let respuesta = {};
    try { respuesta = JSON.parse(xhr.responseText); } catch { /* respuesta vacía */ }
    if (xhr.status === 201) {
      Object.assign(item, { estado: "hecho", nombreFinal: respuesta.nombre });
      estado.peliculas = null; // el catálogo se recargará
    } else {
      Object.assign(item, { estado: "error", error: respuesta.error || `Error ${xhr.status}` });
    }
    terminar();
  };
  xhr.onerror = () => {
    Object.assign(item, { estado: "error", error: "Se cortó la conexión con el ordenador (¿se apagó MiCine o falta espacio?)" });
    terminar();
  };
  xhr.onabort = () => { item.estado = "cancelado"; terminar(); };
  xhr.send(item.archivo);
  actualizarFila(item);

  function terminar() {
    subiendo = false;
    item.xhr = null;
    actualizarFila(item);
    actualizarInsignia();
    refrescarSubidas();
    procesarCola();
  }
}

function filaSubida(item) {
  if (!item.fila) {
    item.fila = {
      raiz: h("div", { class: "fila-subida" }),
      detalle: h("div", { class: "detalle" }),
      barra: h("div", {}),
      boton: h("button", { class: "boton secundario pequeno", onclick: () => cancelarSubida(item) }, "Cancelar"),
    };
    item.fila.raiz.append(
      h("div", { class: "nombre", title: item.nombre }, item.nombre),
      item.fila.boton,
      h("div", { class: "barra-subida" }, item.fila.barra),
      item.fila.detalle);
  }
  actualizarFila(item);
  return item.fila.raiz;
}

function actualizarFila(item) {
  const fila = item.fila;
  if (!fila) return;
  const pct = item.tamano ? (item.enviado / item.tamano) * 100 : 0;
  let texto = "";
  let clase = "";
  switch (item.estado) {
    case "cola": texto = `En cola · ${formatoTamano(item.tamano)}`; break;
    case "subiendo": {
      const segundos = (performance.now() - item.inicio) / 1000;
      const velocidad = segundos > 0.5 ? item.enviado / segundos : 0;
      const quedan = velocidad ? (item.tamano - item.enviado) / velocidad : 0;
      texto = `${Math.floor(pct)} % · ${formatoTamano(item.enviado)} de ${formatoTamano(item.tamano)}` +
        (velocidad ? ` · ${formatoTamano(velocidad)}/s · quedan ${formatoTiempo(quedan)}` : "");
      if (item.enviado >= item.tamano) texto = "Guardando en el disco…";
      break;
    }
    case "hecho": texto = `Subida ✓ ${item.nombreFinal && item.nombreFinal !== item.nombre ? `(guardada como ${item.nombreFinal})` : ""}`; clase = "hecho"; break;
    case "error": texto = item.error; clase = "error"; break;
    case "cancelado": texto = "Cancelada"; clase = "error"; break;
  }
  fila.raiz.className = `fila-subida ${item.estado}`;
  fila.detalle.className = `detalle ${clase}`;
  fila.detalle.textContent = texto;
  fila.barra.style.width = `${item.estado === "hecho" ? 100 : item.estado === "subiendo" ? pct : 0}%`;
  fila.boton.hidden = !["cola", "subiendo"].includes(item.estado);
}

function cancelarSubida(item) {
  if (item.estado === "subiendo") item.xhr.abort();
  else if (item.estado === "cola") { item.estado = "cancelado"; actualizarFila(item); actualizarInsignia(); refrescarSubidas(); }
}

window.addEventListener("beforeunload", (e) => {
  if (subidas.some((s) => s.estado === "subiendo" || s.estado === "cola")) e.preventDefault();
});

// ---------- usuarios ----------

async function vistaUsuarios() {
  const yo = ++generacion;
  const { usuarios } = await api("/api/usuarios");
  if (yo !== generacion) return;

  const nombre = h("input", { required: true, minlength: "3", maxlength: "32", autocomplete: "off", autocapitalize: "none" });
  const contrasena = h("input", { type: "password", required: true, minlength: "6", autocomplete: "new-password" });
  const rol = h("select", {}, h("option", { value: "usuario" }, "Solo ver"), h("option", { value: "admin" }, "Administrador"));
  const error = h("p", { class: "error-form", role: "alert" });
  const formulario = h("form", {
    class: "panel",
    onsubmit: async (e) => {
      e.preventDefault();
      error.textContent = "";
      try {
        await api("/api/usuarios/crear", { usuario: nombre.value, contrasena: contrasena.value, rol: rol.value });
        aviso(`Usuario «${nombre.value.trim().toLowerCase()}» creado`);
        vistaUsuarios();
      } catch (err) {
        error.textContent = err.message;
      }
    },
  },
  h("div", { class: "formulario-linea" },
    campo("Usuario", nombre), campo("Contraseña", contrasena), campo("Permisos", rol),
    h("button", { class: "boton", type: "submit" }, "Crear usuario")),
  error);

  const filas = usuarios.map((u) => h("tr", {},
    h("td", {}, h("strong", {}, u.nombre), u.nombre === estado.usuario.nombre && h("span", { class: "tenue" }, " (tú)")),
    h("td", {}, h("span", { class: `rol ${u.rol}` }, u.rol === "admin" ? "Administrador" : "Solo ver")),
    h("td", { class: "tenue" }, new Date(u.creado * 1000).toLocaleDateString("es-ES")),
    h("td", { class: "acciones" },
      h("button", {
        class: "boton secundario pequeno",
        onclick: async () => {
          const nueva = await pedirContrasena(`Contraseña de ${u.nombre}`);
          if (!nueva) return;
          try {
            await api("/api/usuarios/contrasena", { usuario: u.nombre, contrasena: nueva });
            aviso("Contraseña cambiada. Se ha cerrado su sesión en los demás dispositivos.");
          } catch (err) { aviso(err.message, "error"); }
        },
      }, "Cambiar contraseña"),
      u.nombre !== estado.usuario.nombre && h("button", {
        class: "boton peligro pequeno",
        onclick: async () => {
          if (!confirm(`¿Borrar el usuario «${u.nombre}»? Ya no podrá entrar en MiCine.`)) return;
          try {
            await api("/api/usuarios/borrar", { usuario: u.nombre });
            aviso("Usuario borrado");
            vistaUsuarios();
          } catch (err) { aviso(err.message, "error"); }
        },
      }, "Borrar"))));

  pintar(marco("usuarios",
    h("h1", {}, "Usuarios"),
    h("p", { class: "tenue" }, "Solo quien tenga usuario y contraseña puede entrar. «Solo ver» puede ver y descargar películas; «Administrador» además sube, borra y gestiona usuarios."),
    h("h2", {}, "Nuevo usuario"),
    formulario,
    h("h2", {}, "Con acceso"),
    h("div", { class: "panel" },
      h("table", { class: "tabla" },
        h("thead", {}, h("tr", {}, h("th", {}, "Usuario"), h("th", {}, "Permisos"), h("th", {}, "Desde"), h("th", {}))),
        h("tbody", {}, filas)))));
}

// ---------- mi cuenta ----------

function vistaCuenta() {
  ++generacion;
  const actual = h("input", { type: "password", required: true, autocomplete: "current-password" });
  const nueva = h("input", { type: "password", required: true, minlength: "6", autocomplete: "new-password" });
  const repetir = h("input", { type: "password", required: true, minlength: "6", autocomplete: "new-password" });
  const error = h("p", { class: "error-form", role: "alert" });
  const formulario = h("form", {
    class: "panel estrecho",
    onsubmit: async (e) => {
      e.preventDefault();
      error.textContent = "";
      if (nueva.value !== repetir.value) { error.textContent = "Las contraseñas nuevas no coinciden"; return; }
      try {
        await api("/api/cuenta/contrasena", { actual: actual.value, nueva: nueva.value });
        aviso("Contraseña cambiada");
        formulario.reset();
      } catch (err) {
        error.textContent = err.message;
      }
    },
  },
  campo("Contraseña actual", actual), campo("Nueva contraseña", nueva), campo("Repite la nueva", repetir), error,
  h("button", { class: "boton", type: "submit" }, "Cambiar contraseña"));

  pintar(marco("cuenta",
    h("h1", {}, "Mi cuenta"),
    h("p", { class: "tenue" }, "Has entrado como ", h("strong", {}, estado.usuario.nombre), estado.usuario.rol === "admin" ? " (administrador)." : "."),
    h("h2", {}, "Cambiar contraseña"),
    formulario));
}

// ---------- apps ----------

const GITHUB = "https://github.com/Alvaropegr/MiCine";

async function vistaApps() {
  const yo = ++generacion;
  if (!estado.peliculas) await cargarPeliculas();
  if (yo !== generacion) return;
  // Desde este mismo ordenador (localhost) hay que dar la dirección de la red de casa; si no, sirve la que se usa ahora.
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(location.hostname);
  const base = local ? estado.info.red : location.origin;

  const copiable = (url) => h("div", { class: "fila-url" }, h("code", {}, url),
    h("button", {
      class: "boton secundario pequeno",
      onclick: async () => {
        try {
          await navigator.clipboard.writeText(url);
          aviso("Dirección copiada");
        } catch {
          prompt("Copia esta dirección:", url);
        }
      },
    }, "Copiar"));

  pintar(marco("apps",
    h("h1", {}, "Instalar las apps"),
    h("p", { class: "tenue" }, "Con la app, la tele o el móvil reproducen cualquier formato, recuerdan por dónde vas y (en el móvil) descargan películas para verlas sin conexión."),

    h("section", { class: "panel app" },
      h("h2", {}, "Fire TV Stick"),
      h("ol", {},
        h("li", {}, "En la Fire TV, busca e instala ", h("strong", {}, "Downloader"), " (gratis, en la tienda de Amazon)."),
        h("li", {}, "Ábrela y escribe esta dirección:",
          base ? copiable(`${base}/tele`) : h("p", { class: "tenue" }, "MiCine solo escucha en este ordenador: usa la dirección de GitHub de abajo.")),
        h("li", {}, "Si te lo pide, permite que Downloader instale apps (", h("em", {}, "Configuración → Mi Fire TV → Opciones para desarrolladores"), ")."),
        h("li", {}, "Abre MiCine y escribe la dirección del ordenador, tu usuario y tu contraseña.")),
      h("p", { class: "tenue" }, "Fuera de casa (o si la corta no funciona), esta otra lleva al mismo sitio:"),
      copiable(`${GITHUB}/releases/latest/download/MiCine-firetv.apk`)),

    h("section", { class: "panel app" },
      h("h2", {}, "Móvil o tablet Android"),
      h("p", {}, "Abre esta dirección en el navegador del móvil, descarga la app e instálala (el móvil pedirá permiso para instalar apps de fuera de la tienda)."),
      copiable(base ? `${base}/movil` : `${GITHUB}/releases/latest/download/MiCine.apk`),
      h("p", { class: "tenue" }, "Para móviles muy antiguos hay una versión universal en ", h("a", { href: `${GITHUB}/releases/latest`, target: "_blank", rel: "noopener" }, "GitHub"), ".")),

    h("section", { class: "panel app" },
      h("h2", {}, "Tele Samsung"),
      h("p", {}, h("strong", {}, "De 2015 en adelante (Tizen): "), "la app se instala una vez desde el ordenador, con Tizen Studio. ",
        h("a", { href: `${GITHUB}/blob/main/LEEME.md#tele-samsung-tizen`, target: "_blank", rel: "noopener" }, "Instrucciones")),
      h("p", {}, h("strong", {}, "De 2014 o antes: "), "no hace falta instalar nada. En la tele, abre ", h("em", {}, "Fuente"), " (o ", h("em", {}, "Multimedia → Vídeos"),
        ") y elige «MiCine». La primera vez, dale permiso en la ventana de MiCine del ordenador.")),

    h("p", { class: "tenue" }, "Todas las versiones y el código de MiCine: ", h("a", { href: GITHUB, target: "_blank", rel: "noopener" }, GITHUB.replace("https://", "")))));
}

// ---------- arranque y rutas ----------

function limpiarVista() {
  if (alSalirDeVista) {
    const limpieza = alSalirDeVista;
    alSalirDeVista = null;
    limpieza();
  }
}

async function enrutar() {
  limpiarVista();
  if (!estado.usuario) return vistaLogin();
  const [vista = "", ...resto] = location.hash.replace(/^#\/?/, "").split("/");
  try {
    if (vista === "ver") return await vistaReproductor(decodeURIComponent(resto.join("/")));
    if (vista === "subir" && esAdmin()) return await vistaSubir();
    if (vista === "usuarios" && esAdmin()) return await vistaUsuarios();
    if (vista === "cuenta") return vistaCuenta();
    if (vista === "apps") return await vistaApps();
    return await vistaCatalogo();
  } catch (e) {
    if (!e.silencioso) aviso(e.message, "error");
  }
}

async function arrancar() {
  try {
    const r = await api("/api/estado");
    if (!r.configurado) return r.local ? vistaConfigurar() : vistaSoloLocal();
    estado.usuario = r.usuario;
    enrutar();
  } catch {
    pintar(h("p", { class: "cargando" }, "No se puede conectar con MiCine. ¿Está encendido el ordenador y abierta la ventana de MiCine?"));
  }
}

window.addEventListener("hashchange", enrutar);
arrancar();
