/* MiCine para teles Samsung (Tizen).
   Todo está escrito en JavaScript «antiguo» (ES5, sin librerías) a propósito: así funciona
   también en los navegadores de las teles de 2015-2017, que no entienden el moderno. */

var MC = {};

// ── DOM ────────────────────────────────────────────────────────────────

MC.h = function (etiqueta, atributos) {
  var el = document.createElement(etiqueta);
  for (var k in atributos || {}) {
    var v = atributos[k];
    if (!atributos.hasOwnProperty(k) || v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.indexOf('on') === 0) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v);
  }
  for (var i = 2; i < arguments.length; i++) MC.anadir(el, arguments[i]);
  return el;
};

MC.anadir = function (el, hijo) {
  if (hijo === null || hijo === undefined || hijo === false) return;
  if (hijo instanceof Array) {
    for (var i = 0; i < hijo.length; i++) MC.anadir(el, hijo[i]);
  } else {
    el.appendChild(typeof hijo === 'object' ? hijo : document.createTextNode(String(hijo)));
  }
};

MC.vaciar = function (el) {
  while (el.firstChild) el.removeChild(el.firstChild);
};

MC.clase = function (el, nombre, activa) {
  var clases = (' ' + el.className + ' ').replace(' ' + nombre + ' ', ' ');
  el.className = (activa ? clases + nombre : clases).replace(/^\s+|\s+$/g, '').replace(/\s+/g, ' ');
};

MC.mover = function (el, x, y) {
  var t = 'translate(' + (x || 0) + 'px,' + (y || 0) + 'px)';
  el.style.webkitTransform = t;
  el.style.transform = t;
};

var ICONOS = {
  play: [['path', { d: 'M14 7 L41 24 L14 41 Z' }]],
  pausa: [['path', { d: 'M11 7h9v34h-9z M28 7h9v34h-9z' }]],
  atras10: [
    ['path', { d: 'M24 10 A15 15 0 1 1 9 25', fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M24 3 L15 10 L24 17 Z' }],
    ['text', { x: 24, y: 30, 'font-size': 13, 'font-weight': 'bold', 'text-anchor': 'middle', 'font-family': 'Arial' }, '10']
  ],
  adelante10: [
    ['path', { d: 'M24 10 A15 15 0 1 0 39 25', fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M24 3 L33 10 L24 17 Z' }],
    ['text', { x: 24, y: 30, 'font-size': 13, 'font-weight': 'bold', 'text-anchor': 'middle', 'font-family': 'Arial' }, '10']
  ],
  subtitulos: [
    ['rect', { x: 4, y: 9, width: 40, height: 30, rx: 5, fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M11 19h17v4H11z M31 19h6v4h-6z M11 27h9v4h-9z M23 27h14v4H23z' }]
  ],
  reinicio: [['path', { d: 'M9 8h5v32H9z M40 8 L17 24 L40 40 Z' }]],
  lupa: [
    ['circle', { cx: 20, cy: 20, r: 12, fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M29 29 L41 41', stroke: 'currentColor', 'stroke-width': 5, 'stroke-linecap': 'round' }]
  ],
  recargar: [
    ['path', { d: 'M38 24 A14 14 0 1 1 33 13', fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M27 5 L39 9 L35 21 Z' }]
  ],
  salir: [
    ['path', { d: 'M20 8H9v32h11', fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }],
    ['path', { d: 'M19 24h20 M31 15l9 9-9 9', fill: 'none', stroke: 'currentColor', 'stroke-width': 4 }]
  ],
  marca: [['path', { d: 'M8 25 L19 36 L40 13', fill: 'none', stroke: 'currentColor', 'stroke-width': 5 }]]
};

MC.icono = function (nombre) {
  var NS = 'http://www.w3.org/2000/svg';
  var svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 48 48');
  svg.setAttribute('class', 'icono');
  svg.setAttribute('fill', 'currentColor');
  var partes = ICONOS[nombre];
  for (var i = 0; i < partes.length; i++) {
    var p = document.createElementNS(NS, partes[i][0]);
    for (var a in partes[i][1]) p.setAttribute(a, partes[i][1][a]);
    if (partes[i][2]) p.appendChild(document.createTextNode(partes[i][2]));
    svg.appendChild(p);
  }
  return svg;
};

// ── Textos ─────────────────────────────────────────────────────────────

MC.tiempo = function (segundos) {
  segundos = Math.max(0, Math.floor(segundos || 0));
  var h = Math.floor(segundos / 3600), m = Math.floor(segundos / 60) % 60, s = segundos % 60;
  var dos = function (n) { return (n < 10 ? '0' : '') + n; };
  return h ? h + ':' + dos(m) + ':' + dos(s) : m + ':' + dos(s);
};

MC.duracion = function (segundos) {
  var min = Math.max(1, Math.round(segundos / 60));
  return min >= 60 ? Math.floor(min / 60) + ' h ' + (min % 60) + ' min' : min + ' min';
};

MC.tamano = function (bytes) {
  if (bytes >= 1e9) return (bytes / 1e9).toFixed(1).replace('.', ',') + ' GB';
  return Math.max(1, Math.round(bytes / 1e6)) + ' MB';
};

MC.extension = function (ruta) {
  var m = /\.([a-z0-9]+)$/i.exec(ruta);
  return m ? m[1].toUpperCase() : '';
};

MC.sinAcentos = function (texto) {
  texto = String(texto).toLowerCase();
  if (texto.normalize) return texto.normalize('NFD').replace(/[̀-ͯ]/g, '');
  return texto.replace(/[áàä]/g, 'a').replace(/[éèë]/g, 'e').replace(/[íìï]/g, 'i')
    .replace(/[óòö]/g, 'o').replace(/[úùü]/g, 'u').replace(/ñ/g, 'n');
};

// Códigos de idioma de las pistas (ISO 639-1 y 639-2) → clave común de dos letras.
var IDIOMAS = {
  es: ['Español', 'spa', 'esp'], en: ['Inglés', 'eng'], fr: ['Francés', 'fre', 'fra'],
  de: ['Alemán', 'ger', 'deu'], it: ['Italiano', 'ita'], pt: ['Portugués', 'por'],
  ca: ['Catalán', 'cat'], eu: ['Euskera', 'baq', 'eus'], gl: ['Gallego', 'glg'],
  ja: ['Japonés', 'jpn'], ko: ['Coreano', 'kor'], zh: ['Chino', 'chi', 'zho'],
  ru: ['Ruso', 'rus'], nl: ['Neerlandés', 'dut', 'nld'], pl: ['Polaco', 'pol']
};

MC.claveIdioma = function (codigo) {
  codigo = String(codigo || '').toLowerCase();
  for (var clave in IDIOMAS) {
    if (clave === codigo || IDIOMAS[clave].indexOf(codigo) > 0) return clave;
  }
  return codigo;
};

MC.nombreIdioma = function (codigo) {
  var clave = MC.claveIdioma(codigo);
  return IDIOMAS[clave] ? IDIOMAS[clave][0] : '';
};

// ── Lo que se recuerda en la tele ──────────────────────────────────────

MC.leer = function (clave, defecto) {
  try {
    var v = localStorage.getItem('micine.' + clave);
    return v === null ? defecto : JSON.parse(v);
  } catch (e) {
    return defecto;
  }
};

MC.guardar = function (clave, valor) {
  try {
    if (valor === null) localStorage.removeItem('micine.' + clave);
    else localStorage.setItem('micine.' + clave, JSON.stringify(valor));
  } catch (e) { /* sin almacenamiento: se pedirá entrar otra vez */ }
};

// ── Servidor ───────────────────────────────────────────────────────────

/** Igual que la app de Android: «192.168.1.20» → «http://192.168.1.20:8080». */
MC.normalizarServidor = function (texto) {
  var s = String(texto || '').replace(/^\s+|\s+$/g, '').replace(/\/+$/, '');
  if (!s) return '';
  if (!/^https?:\/\//i.test(s)) s = 'http://' + s;
  var m = /^(https?):\/\/([^\/:]+|\[[^\]]+\])(:\d+)?(\/.*)?$/i.exec(s);
  if (m && !m[3] && !m[4] && m[1].toLowerCase() === 'http') s += ':8080';
  return s;
};

MC.api = {
  servidor: '',
  token: '',

  /** Petición a MiCine. fallo recibe {codigo, mensaje}; codigo 0 = no hay conexión. */
  pedir: function (metodo, ruta, datos, ok, fallo, espera) {
    var x = new XMLHttpRequest(), servidor = this.servidor, terminado = false;
    var acabar = function (codigo, cuerpo) {
      if (terminado) return;
      terminado = true;
      var json = null;
      try { json = cuerpo ? JSON.parse(cuerpo) : null; } catch (e) { /* no era JSON */ }
      if (codigo >= 200 && codigo < 300) {
        if (ok) ok(json || {});
      } else if (fallo) {
        fallo({
          codigo: codigo,
          mensaje: codigo === 0
            ? 'No encuentro MiCine en ' + servidor.replace(/^https?:\/\//, '') + '. ¿Está encendido el ordenador?'
            : (json && json.error) || 'Error ' + codigo
        });
      }
    };
    try {
      x.open(metodo, servidor + ruta, true);
      x.timeout = espera || 20000;
      if (this.token) x.setRequestHeader('Authorization', 'Bearer ' + this.token);
      if (metodo === 'POST') {
        x.setRequestHeader('X-MiCine', '1');
        x.setRequestHeader('Content-Type', 'application/json');
      }
      x.onreadystatechange = function () {
        if (x.readyState === 4) acabar(x.status, x.responseText);
      };
      x.onerror = x.ontimeout = x.onabort = function () { acabar(0, null); };
      x.send(datos ? JSON.stringify(datos) : null);
    } catch (e) {
      acabar(0, null);
    }
  },

  /** Las rutas de vídeo, carátula y subtítulos llegan firmadas desde el servidor. */
  url: function (ruta) {
    return ruta ? this.servidor + ruta : '';
  }
};

/** Busca MiCine en la red de casa probando el puerto 8080 de cada dirección. */
MC.buscarEnRed = function (progreso, encontrado, fin) {
  var ip = null;
  try { ip = webapis.network.getIp(); } catch (e) { /* no es una tele */ }
  if (!ip || !/^\d+\.\d+\.\d+\.\d+$/.test(ip)) {
    fin(false);
    return { parar: function () {} };
  }
  var base = ip.split('.').slice(0, 3).join('.') + '.';
  var cola = [], hechos = 0, activos = 0, parado = false;
  for (var i = 1; i < 255; i++) if (base + i !== ip) cola.push(base + i);
  var total = cola.length;

  var probar = function (host) {
    var x = new XMLHttpRequest(), url = 'http://' + host + ':8080', listo = false;
    var terminar = function (esMiCine) {
      if (listo) return;
      listo = true;
      activos--;
      hechos++;
      if (parado) return;
      if (esMiCine) {
        parado = true;
        encontrado(url);
        return;
      }
      progreso(hechos / total);
      siguiente();
    };
    activos++;
    try {
      x.open('GET', url + '/api/estado', true);
      x.timeout = 1500;
      x.onreadystatechange = function () {
        if (x.readyState !== 4 || x.status === 0) return;
        var j = null;
        try { j = JSON.parse(x.responseText); } catch (e) { /* otro servicio */ }
        terminar(x.status === 200 && j !== null && typeof j.configurado === 'boolean');
      };
      x.onerror = x.ontimeout = x.onabort = function () { terminar(false); };
      x.send(null);
    } catch (e) {
      terminar(false);
    }
  };

  var siguiente = function () {
    while (!parado && activos < 24 && cola.length) probar(cola.shift());
    if (!parado && activos === 0 && !cola.length) {
      parado = true;
      fin(true);
    }
  };

  siguiente();
  return { parar: function () { parado = true; } };
};

// ── Mando a distancia ──────────────────────────────────────────────────

var TECLAS = {
  37: 'izq', 38: 'arr', 39: 'der', 40: 'aba', 13: 'ok',
  10009: 'atras', 27: 'atras',
  415: 'play', 19: 'pausa', 10252: 'playpausa', 413: 'stop',
  417: 'avance', 412: 'retroceso', 34: 'avance', 33: 'retroceso',
  457: 'info', 65376: 'hecho', 65385: 'cancelar'
};

MC.tecla = function (e) {
  var t = TECLAS[e.keyCode];
  var escribiendo = MC.escribiendo();
  if (!t && e.keyCode === 32 && !escribiendo) t = 'playpausa';
  if (!t && e.keyCode === 8 && !escribiendo) t = 'atras';
  return t || null;
};

MC.escribiendo = function () {
  var a = document.activeElement;
  return !!a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA');
};

MC.registrarTeclas = function () {
  var nombres = ['MediaPlayPause', 'MediaPlay', 'MediaPause', 'MediaStop', 'MediaFastForward', 'MediaRewind', 'Info'];
  for (var i = 0; i < nombres.length; i++) {
    try { tizen.tvinputdevice.registerKey(nombres[i]); } catch (e) { /* esta tele no tiene esa tecla */ }
  }
};

MC.cerrarApp = function () {
  try {
    tizen.application.getCurrentApplication().exit();
  } catch (e) {
    window.close();
  }
};
