/* Motores de vídeo. En la tele se usa AVPlay, el reproductor propio de Samsung: lee MKV, AVI,
   H.265, AC3… y las pistas de audio y subtítulos que llevan dentro. En un navegador (para
   probar la app en el ordenador) se usa <video>, que solo entiende lo básico.

   Los dos ofrecen lo mismo:
     abrir(url, inicio, ev)  ev = {listo, tiempo(s), cargando(si, %), fin, error(texto), subtitulo(texto, ms)}
     play() pausa() enPausa() ir(s) tiempo() duracion()
     audios() / elegirAudio(id)    textos() / elegirTexto(id o null)    cerrar()
   Los tiempos van en segundos. */

MC.crearMotor = function (contenedor) {
  var hayAVPlay = false;
  try { hayAVPlay = !!(window.webapis && webapis.avplay); } catch (e) { /* no es una tele */ }
  return hayAVPlay ? new MotorAVPlay(contenedor) : new MotorHTML5(contenedor);
};

function leerExtra(pista) {
  try { return JSON.parse(pista.extra_info) || {}; } catch (e) { return {}; }
}

function nombreCanales(n) {
  n = parseInt(n, 10);
  return n === 1 ? 'Mono' : n === 2 ? 'Estéreo' : n === 6 ? '5.1' : n === 8 ? '7.1' : n ? n + ' canales' : '';
}

function textoError(e) {
  var tipo = String((e && (e.name || e.message)) || e || '');
  if (/NOT_SUPPORTED|UNSUPPORTED|NotSupported|CODEC|FORMAT/i.test(tipo)) {
    return 'Esta tele no puede reproducir este archivo: su formato o su audio no es compatible con este modelo.';
  }
  if (/CONNECTION|NETWORK|TIMEOUT|INVALID_URI|NOT_FOUND|404|401/i.test(tipo)) {
    return 'Se ha perdido la conexión con MiCine. Comprueba que el ordenador sigue encendido.';
  }
  return 'No se pudo reproducir la película (' + (tipo || 'error desconocido') + ').';
}

// ── AVPlay (tele Samsung) ──────────────────────────────────────────────

function MotorAVPlay(contenedor) {
  this.av = webapis.avplay;
  this.duracionMs = 0;
  this.textoActivo = null;
  // AVPlay dibuja el vídeo en una capa por debajo de la página; este objeto marca dónde.
  this.objeto = MC.h('object', { type: 'application/avplayer', class: 'avplayer' });
  contenedor.appendChild(this.objeto);
}

MotorAVPlay.prototype.abrir = function (url, inicio, ev) {
  var av = this.av, yo = this;
  this.ev = ev;
  this.textoActivo = null;
  this.cerrar();
  try {
    av.open(url);
    av.setDisplayRect(0, 0, 1920, 1080);
    av.setDisplayMethod('PLAYER_DISPLAY_MODE_LETTER_BOX');
    av.setListener({
      onbufferingstart: function () { ev.cargando(true, 0); },
      onbufferingprogress: function (pct) { ev.cargando(true, pct); },
      onbufferingcomplete: function () { ev.cargando(false); },
      oncurrentplaytime: function (ms) { ev.tiempo(ms / 1000); },
      onstreamcompleted: function () { ev.fin(); },
      onerror: function (tipo) { ev.error(textoError(tipo)); },
      onevent: function () {},
      onsubtitlechange: function (duracion, texto) {
        if (yo.textoActivo !== null) ev.subtitulo(texto, parseInt(duracion, 10) || 3000);
      },
      ondrmevent: function () {}
    });
  } catch (e) {
    ev.error(textoError(e));
    return;
  }
  ev.cargando(true, 0);
  av.prepareAsync(function () {
    yo.duracionMs = av.getDuration() || 0;
    var arrancar = function () {
      try {
        av.play();
      } catch (e) {
        ev.error(textoError(e));
        return;
      }
      ev.cargando(false);
      ev.listo();
    };
    if (inicio > 1) av.seekTo(Math.floor(inicio * 1000), arrancar, arrancar);
    else arrancar();
  }, function (e) {
    ev.error(textoError(e));
  });
};

MotorAVPlay.prototype.estado = function () {
  try { return this.av.getState(); } catch (e) { return 'NONE'; }
};

MotorAVPlay.prototype.play = function () {
  if (this.estado() === 'PAUSED' || this.estado() === 'READY') this.av.play();
};

MotorAVPlay.prototype.pausa = function () {
  if (this.estado() === 'PLAYING') this.av.pause();
};

MotorAVPlay.prototype.enPausa = function () {
  return this.estado() !== 'PLAYING';
};

MotorAVPlay.prototype.ir = function (segundos) {
  var ms = Math.floor(Math.max(0, Math.min(segundos, this.duracion() - 1)) * 1000);
  var ev = this.ev;
  try {
    this.av.seekTo(ms, function () {}, function () { if (ev) ev.cargando(false); });
  } catch (e) { /* aún no está listo */ }
};

MotorAVPlay.prototype.tiempo = function () {
  try { return this.av.getCurrentTime() / 1000; } catch (e) { return 0; }
};

MotorAVPlay.prototype.duracion = function () {
  if (!this.duracionMs) {
    try { this.duracionMs = this.av.getDuration() || 0; } catch (e) { /* aún no se sabe */ }
  }
  return this.duracionMs / 1000;
};

MotorAVPlay.prototype.pistas = function (tipo) {
  var todas = [];
  try { todas = this.av.getTotalTrackInfo() || []; } catch (e) { /* aún no está listo */ }
  var lista = [];
  for (var i = 0; i < todas.length; i++) if (todas[i].type === tipo) lista.push(todas[i]);
  return lista;
};

MotorAVPlay.prototype.pistaActual = function (tipo) {
  try {
    var actuales = this.av.getCurrentStreamInfo() || [];
    for (var i = 0; i < actuales.length; i++) if (actuales[i].type === tipo) return actuales[i].index;
  } catch (e) { /* no lo sabe */ }
  return null;
};

MotorAVPlay.prototype.audios = function () {
  var pistas = this.pistas('AUDIO'), actual = this.pistaActual('AUDIO'), lista = [];
  for (var i = 0; i < pistas.length; i++) {
    var x = leerExtra(pistas[i]);
    var detalle = [nombreCanales(x.channels), String(x.fourCC || '').toUpperCase()];
    lista.push({
      id: pistas[i].index,
      idioma: MC.claveIdioma(x.language),
      nombre: MC.nombreIdioma(x.language) || 'Pista ' + (i + 1),
      detalle: detalle.join(' · ').replace(/^ · | · $/g, ''),
      activa: actual === null ? i === 0 : pistas[i].index === actual
    });
  }
  return lista;
};

MotorAVPlay.prototype.elegirAudio = function (id) {
  try { this.av.setSelectTrack('AUDIO', id); } catch (e) { /* la tele no lo permite con este archivo */ }
};

MotorAVPlay.prototype.textos = function () {
  var pistas = this.pistas('TEXT'), lista = [];
  for (var i = 0; i < pistas.length; i++) {
    var x = leerExtra(pistas[i]);
    var codigo = x.track_lang || x.language;
    lista.push({
      id: pistas[i].index,
      idioma: MC.claveIdioma(codigo),
      nombre: (MC.nombreIdioma(codigo) || 'Pista ' + (i + 1)) + ' (dentro del vídeo)'
    });
  }
  return lista;
};

MotorAVPlay.prototype.elegirTexto = function (id) {
  this.textoActivo = id;
  try {
    if (id === null) {
      this.av.setSilentSubtitle(true);
    } else {
      this.av.setSilentSubtitle(false);
      this.av.setSelectTrack('TEXT', id);
    }
  } catch (e) { /* la tele no lo permite con este archivo */ }
};

MotorAVPlay.prototype.cerrar = function () {
  var estado = this.estado();
  if (estado === 'READY' || estado === 'PLAYING' || estado === 'PAUSED') {
    try { this.av.stop(); } catch (e) { /* ya estaba parado */ }
  }
  if (estado !== 'NONE') {
    try { this.av.close(); } catch (e) { /* ya estaba cerrado */ }
  }
  this.duracionMs = 0;
};

// ── <video> (navegador del ordenador, para probar) ─────────────────────

function MotorHTML5(contenedor) {
  this.contenedor = contenedor;
  this.v = null;
}

MotorHTML5.prototype.abrir = function (url, inicio, ev) {
  this.cerrar();
  var v = this.v = MC.h('video', { class: 'video-html5', preload: 'auto' });
  var avisado = false;
  this.contenedor.insertBefore(v, this.contenedor.firstChild);
  v.addEventListener('loadedmetadata', function () {
    if (inicio > 1) v.currentTime = inicio;
    var promesa = v.play();
    if (promesa && promesa.then) promesa.then(null, function () { /* el navegador pide un clic */ });
    if (!avisado) {
      avisado = true;
      ev.cargando(false);
      ev.listo();
    }
  });
  v.addEventListener('timeupdate', function () { ev.tiempo(v.currentTime); });
  v.addEventListener('waiting', function () { ev.cargando(true, 0); });
  v.addEventListener('playing', function () { ev.cargando(false); });
  v.addEventListener('seeked', function () { ev.cargando(false); });
  v.addEventListener('ended', function () { ev.fin(); });
  v.addEventListener('error', function () {
    ev.error(textoError(v.error && v.error.code === 4 ? 'NOT_SUPPORTED' : 'NETWORK'));
  });
  ev.cargando(true, 0);
  v.src = url;
};

MotorHTML5.prototype.play = function () {
  if (!this.v) return;
  var promesa = this.v.play();
  if (promesa && promesa.then) promesa.then(null, function () {});
};

MotorHTML5.prototype.pausa = function () { if (this.v) this.v.pause(); };
MotorHTML5.prototype.enPausa = function () { return !this.v || this.v.paused; };

MotorHTML5.prototype.ir = function (segundos) {
  if (this.v) this.v.currentTime = Math.max(0, Math.min(segundos, this.duracion() - 1));
};

MotorHTML5.prototype.tiempo = function () { return this.v ? this.v.currentTime : 0; };
MotorHTML5.prototype.duracion = function () { return this.v && isFinite(this.v.duration) ? this.v.duration : 0; };

MotorHTML5.prototype.audios = function () {
  var pistas = this.v && this.v.audioTracks, lista = [];
  if (!pistas || pistas.length < 2) return [{ id: 0, idioma: '', nombre: 'Predeterminado', detalle: '', activa: true }];
  for (var i = 0; i < pistas.length; i++) {
    lista.push({
      id: i,
      idioma: MC.claveIdioma(pistas[i].language),
      nombre: MC.nombreIdioma(pistas[i].language) || pistas[i].label || 'Pista ' + (i + 1),
      detalle: '',
      activa: pistas[i].enabled
    });
  }
  return lista;
};

MotorHTML5.prototype.elegirAudio = function (id) {
  var pistas = this.v && this.v.audioTracks;
  if (!pistas || pistas.length < 2) return;
  for (var i = 0; i < pistas.length; i++) pistas[i].enabled = i === id;
};

MotorHTML5.prototype.textos = function () { return []; };
MotorHTML5.prototype.elegirTexto = function () {};

MotorHTML5.prototype.cerrar = function () {
  if (!this.v) return;
  this.v.pause();
  this.v.removeAttribute('src');
  try { this.v.load(); } catch (e) { /* ya estaba vacío */ }
  if (this.v.parentNode) this.v.parentNode.removeChild(this.v);
  this.v = null;
};
