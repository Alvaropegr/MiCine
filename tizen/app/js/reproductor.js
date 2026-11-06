/* Reproductor a pantalla completa con los controles de Netflix:
   ← → saltan 10 s (si se pulsan seguidas, cada vez más), ⏪ ⏩ saltan 30 s y el salto se hace
   al dejar de pulsar; OK pausa; ↓ lleva a los botones (audio y subtítulos, desde el principio…).
   El progreso se guarda en MiCine cada 15 s, al pausar y al salir.

   Si el ordenador tiene FFmpeg, lo que la tele no sepa reproducir se pide convertido:
     «audio»: la imagen viaja tal cual y solo se convierte el sonido (DTS, TrueHD…); casi no gasta.
     «video»: se convierte todo a H.264. Para saltar o cambiar de audio se vuelve a pedir desde ese punto. */

var PASO = 10;
var PASO_RAPIDO = 30;
var ESPERA_SALTO = 800;
var ESPERA_OCULTAR = 5000;
var AVISO_SIGUIENTE = 25;    // segundos antes del final en que se ofrece el siguiente episodio
var CUENTA_SIGUIENTE = 10;   // segundos hasta que empieza solo

// Lo que reproducen sin ayuda las Samsung con Tizen y los navegadores (nombres de FFmpeg).
var VIDEO_TELE = ['h264', 'hevc', 'mpeg4', 'mpeg2video', 'mpeg1video', 'vc1', 'wmv3', 'vp8', 'vp9', 'av1', 'msmpeg4v3', 'h263'];
var AUDIO_TELE = ['aac', 'ac3', 'eac3', 'mp3', 'mp2', 'pcm_s16le', 'pcm_s24le'];
var VIDEO_NAVEGADOR = ['h264', 'vp8', 'vp9', 'av1'];
var AUDIO_NAVEGADOR = ['aac', 'mp3', 'opus', 'vorbis', 'flac'];

MC.reproducir = function (peli, reanudar, volverA) {
  if (!peli) return;
  MC.mostrar(new PantallaReproductor(peli, reanudar && peli.progreso ? Math.max(0, peli.progreso.t - 3) : 0, volverA));
};

function PantallaReproductor(peli, inicio, volverA) {
  var yo = this;
  this.peli = peli;
  this.inicio = inicio;
  this.posicion = inicio;
  this.volverA = volverA;
  this.listo = false;
  this.salto = null;
  this.foco = 'barra';
  this.boton = 1;
  this.subActual = null;
  this.subExterno = null;
  this.ultimoGuardado = -100;
  this.modo = 'directo';
  this.info = null;
  this.desfase = 0;
  this.audioN = null;
  // Con secciones, si es un episodio y hay otro detrás, se ofrece al acabar.
  this.siguiente = MC.siguienteEpisodio ? MC.siguienteEpisodio(peli) : null;
  this.avisoSiguiente = null;
  this.siguienteDescartado = false;
  MC.ultimoVideo = peli;

  this.capa = MC.h('div', { class: 'capa-video' });
  this.subs = MC.h('div', { class: 'subtitulos' });
  this.cargandoTexto = MC.h('div', { class: 'cargando-texto' });
  this.cargando = MC.h('div', { class: 'cargando visible' }, MC.h('div', { class: 'rueda' }), this.cargandoTexto);
  this.centro = MC.h('div', { class: 'aviso-centro' });

  this.relleno = MC.h('div', { class: 'relleno' });
  this.marca = MC.h('div', { class: 'marca' });
  this.globo = MC.h('div', { class: 'globo' });
  this.barra = MC.h('div', { class: 'barra-tiempo' }, MC.h('div', { class: 'carril' }, this.relleno), this.marca, this.globo);
  this.tActual = MC.h('span', { class: 'actual' });
  this.tResto = MC.h('span', { class: 'resto' });
  this.etiquetaModo = MC.h('div', { class: 'modo-video' });

  this.botonPlay = MC.h('div', { class: 'boton-video redondo' }, MC.icono('pausa'));
  this.botones = [
    { el: MC.h('div', { class: 'boton-video redondo' }, MC.icono('atras10')), accion: function () { yo.saltar(-PASO, true); } },
    { el: this.botonPlay, accion: function () { yo.alternar(); } },
    { el: MC.h('div', { class: 'boton-video redondo' }, MC.icono('adelante10')), accion: function () { yo.saltar(PASO, true); } },
    { el: MC.h('div', { class: 'boton-video' }, MC.icono('subtitulos'), 'Audio y subtítulos'), accion: function () { yo.abrirPanel(); } },
    { el: MC.h('div', { class: 'boton-video' }, MC.icono('reinicio'), 'Desde el principio'), accion: function () { yo.irA(0); } }
  ];
  var filaBotones = MC.h('div', { class: 'botones-video' });
  for (var i = 0; i < this.botones.length; i++) {
    filaBotones.appendChild(this.botones[i].el);
    (function (indice) {
      yo.botones[indice].el.addEventListener('click', function () {
        yo.foco = 'botones';
        yo.boton = indice;
        yo.tecla('ok');
      });
    })(i);
  }
  this.barra.addEventListener('click', function (e) {
    var caja = yo.barra.getBoundingClientRect();
    yo.irA((e.clientX - caja.left) / caja.width * yo.duracion());
  });

  this.controles = MC.h('div', { class: 'controles' },
    MC.h('div', { class: 'controles-arriba' }, MC.h('div', { class: 'titulo-video' }, peli.titulo), this.etiquetaModo),
    MC.h('div', { class: 'controles-abajo' },
      this.barra,
      MC.h('div', { class: 'tiempos' }, this.tActual, this.tResto),
      filaBotones));

  this.el = MC.h('div', { class: 'pantalla reproductor' },
    this.capa, this.subs, this.cargando, this.centro, this.controles);
  this.motor = MC.crearMotor(this.capa);
  this.enTele = this.motor instanceof MotorAVPlay;
}

PantallaReproductor.prototype.entrarPantalla = function () {
  var yo = this;
  MC.clase(document.documentElement, 'reproduciendo', true);
  this.reloj = setInterval(function () { yo.tic(); }, 250);
  this.alCambiarVisibilidad = function () {
    if (document.hidden) {
      // La tele pasa a otra app o al menú: se libera el reproductor y se sigue luego desde aquí.
      yo.guardar(true);
      yo.reanudarEn = yo.tiempo();
      yo.listo = false;
      yo.motor.cerrar();
    } else if (yo.reanudarEn !== undefined) {
      yo.abrir(yo.reanudarEn);
      yo.reanudarEn = undefined;
    }
  };
  document.addEventListener('visibilitychange', this.alCambiarVisibilidad);

  if (!MC.conversion) return this.abrir(this.inicio);
  // Primero se pregunta a MiCine qué lleva el archivo, para saber si hay que convertirlo.
  MC.api.pedir('GET', '/api/info?ruta=' + encodeURIComponent(this.peli.ruta), null, function (info) {
    if (MC.pantalla !== yo) return;
    yo.info = info.conversion ? info : null;
    yo.modo = yo.elegirModo();
    yo.abrir(yo.inicio);
  }, function () {
    if (MC.pantalla === yo) yo.abrir(yo.inicio);
  }, 30000);
};

PantallaReproductor.prototype.salirPantalla = function () {
  clearInterval(this.reloj);
  clearTimeout(this.tOcultar);
  clearTimeout(this.tSalto);
  clearTimeout(this.tSub);
  clearTimeout(this.tCentro);
  clearInterval(this.tCuenta);
  document.removeEventListener('visibilitychange', this.alCambiarVisibilidad);
  this.motor.cerrar();
  MC.clase(document.documentElement, 'reproduciendo', false);
};

// ── Directo o convertido ───────────────────────────────────────────────

PantallaReproductor.prototype.audioPorDefecto = function () {
  var audios = this.info ? this.info.audios : [];
  for (var i = 0; i < audios.length; i++) if (audios[i].defecto) return audios[i];
  return audios[0] || null;
};

PantallaReproductor.prototype.videoCompatible = function () {
  var v = this.info && this.info.video;
  if (!v) return true;
  if (!this.enTele && !/\.(mp4|m4v|webm|mkv|mov)$/i.test(this.peli.ruta)) return false;
  return (this.enTele ? VIDEO_TELE : VIDEO_NAVEGADOR).indexOf(v.codec) >= 0;
};

PantallaReproductor.prototype.audioCompatible = function (audio) {
  return !audio || (this.enTele ? AUDIO_TELE : AUDIO_NAVEGADOR).indexOf(audio.codec) >= 0;
};

PantallaReproductor.prototype.elegirModo = function () {
  if (!this.info) return 'directo';
  if (!this.videoCompatible()) return 'video';
  if (!this.audioCompatible(this.audioPorDefecto())) return 'audio';
  return 'directo';
};

PantallaReproductor.prototype.abrir = function (inicio) {
  var yo = this, apertura = this.aperturas = (this.aperturas || 0) + 1;
  this.listo = false;
  this.posicion = inicio;
  this.subtituloInternoEnCurso = false;
  if (this.modo !== 'audio' || inicio < 1) return this.abrirEn(inicio);
  // Si la imagen no se convierte, solo puede empezar en un fotograma clave (a veces unos
  // segundos antes): MiCine dice cuál es, para que reloj y subtítulos cuadren.
  MC.clase(this.cargando, 'visible', true);
  MC.api.pedir('GET', '/api/info?ruta=' + encodeURIComponent(this.peli.ruta) + '&clave=' + Math.round(inicio * 1000) / 1000, null, function (r) {
    if (apertura === yo.aperturas && MC.pantalla === yo) yo.abrirEn(typeof r.clave === 'number' ? r.clave : inicio);
  }, function () {
    if (apertura === yo.aperturas && MC.pantalla === yo) yo.abrirEn(inicio);
  }, 15000);
};

PantallaReproductor.prototype.abrirEn = function (inicio) {
  var yo = this, url = MC.api.url(this.peli.url);
  this.posicion = inicio;
  if (this.modo === 'directo') {
    this.desfase = 0;
  } else {
    this.desfase = inicio;
    var defecto = this.audioPorDefecto();
    if (this.audioN === null) this.audioN = defecto ? defecto.n : 0;
    url += '&convertir=1&t=' + Math.round(inicio * 1000) / 1000 + '&audio=' + this.audioN +
      (this.modo === 'audio' ? '&video=copia' : '') + (this.enTele ? '' : '&formato=web');
    inicio = 0;
  }
  this.etiquetaModo.textContent = this.modo === 'video' ? 'Convertido en el ordenador para esta pantalla'
    : this.modo === 'audio' ? 'Audio convertido en el ordenador' : '';
  this.motor.abrir(url, inicio, {
    listo: function () {
      yo.listo = true;
      if (!yo.preferenciasAplicadas) {
        yo.preferenciasAplicadas = true;
        yo.aplicarPreferencias();
      } else if (yo.modo === 'directo') {
        yo.reaplicarPistas();
      }
      yo.ponerIconoPlay();
      yo.mostrarControles();
    },
    tiempo: function () { yo.pintarTiempo(); },
    cargando: function (si, pct) {
      MC.clase(yo.cargando, 'visible', si);
      yo.cargandoTexto.textContent = si && pct ? pct + ' %' : '';
    },
    fin: function () { yo.terminar(); },
    error: function (texto) { yo.errorMotor(texto); },
    subtitulo: function (texto, ms) { yo.subtituloInterno(texto, ms); }
  });
};

/** Si la tele no puede con el archivo, se prueba convirtiendo: primero solo el audio, luego todo. */
PantallaReproductor.prototype.errorMotor = function (texto) {
  if (this.info && this.modo !== 'video') {
    var antes = this.modo;
    this.modo = this.modo === 'directo' && this.videoCompatible() ? 'audio' : 'video';
    if (antes === 'directo') MC.aviso('Esta pantalla no puede con el archivo original: MiCine lo está convirtiendo en el ordenador.');
    var t = this.listo ? this.tiempo() : this.posicion;
    this.motor.cerrar();
    return this.abrir(t);
  }
  this.fallo(texto);
};

PantallaReproductor.prototype.tiempo = function () {
  var t = this.desfase + this.motor.tiempo();
  if (this.listo) this.posicion = t;
  return t;
};

PantallaReproductor.prototype.duracion = function () {
  if (this.modo !== 'directo' && this.info && this.info.duracion) return this.info.duracion;
  return this.motor.duracion() || (this.info && this.info.duracion) || 0;
};

PantallaReproductor.prototype.tic = function () {
  if (!this.listo) return;
  if (this.controlesVisibles()) this.pintarTiempo();
  this.pintarSubExterno();
  if (!this.motor.enPausa() && Math.abs(this.tiempo() - this.ultimoGuardado) >= 15) this.guardar(true);
  if (this.siguiente && !this.avisoSiguiente && !this.siguienteDescartado && !this.salto && !this.panel) {
    var d = this.duracion();
    if (d > 2 * AVISO_SIGUIENTE && !this.motor.enPausa() && d - this.tiempo() <= AVISO_SIGUIENTE) this.ofrecerSiguiente(false);
  }
};

// ── Controles ──────────────────────────────────────────────────────────

PantallaReproductor.prototype.mostrarControles = function () {
  MC.clase(this.controles, 'visible', true);
  MC.clase(this.subs, 'alto', true);
  this.pintarFoco();
  this.pintarTiempo();
  this.programarOcultar();
};

PantallaReproductor.prototype.programarOcultar = function () {
  var yo = this;
  clearTimeout(this.tOcultar);
  if (this.motor.enPausa() || this.panel || this.salto) return;
  this.tOcultar = setTimeout(function () { yo.ocultarControles(); }, ESPERA_OCULTAR);
};

PantallaReproductor.prototype.ocultarControles = function () {
  clearTimeout(this.tOcultar);
  MC.clase(this.controles, 'visible', false);
  MC.clase(this.subs, 'alto', false);
  this.foco = 'barra';
};

PantallaReproductor.prototype.controlesVisibles = function () {
  return this.controles.className.indexOf('visible') >= 0;
};

PantallaReproductor.prototype.pintarFoco = function () {
  MC.clase(this.barra, 'foco', this.foco === 'barra');
  for (var i = 0; i < this.botones.length; i++) MC.clase(this.botones[i].el, 'foco', this.foco === 'botones' && i === this.boton);
};

PantallaReproductor.prototype.pintarTiempo = function () {
  var d = this.duracion(), t = this.listo ? this.tiempo() : this.posicion;
  if (this.salto) t = this.salto.pos;
  else if (this.destino !== undefined) {
    // Mientras la tele termina de saltar, se muestra ya el punto de destino.
    if ((this.listo && Math.abs(t - this.destino) < 2) || new Date().getTime() - this.destinoHora > 4000) this.destino = undefined;
    else t = this.destino;
  }
  var pct = d ? Math.max(0, Math.min(100, t / d * 100)) : 0;
  this.relleno.style.width = pct + '%';
  this.marca.style.left = pct + '%';
  this.globo.style.left = pct + '%';
  this.globo.textContent = MC.tiempo(t);
  MC.clase(this.globo, 'visible', !!this.salto);
  this.tActual.textContent = MC.tiempo(t);
  this.tResto.textContent = d ? '-' + MC.tiempo(d - t) : '';
};

PantallaReproductor.prototype.ponerIconoPlay = function () {
  MC.vaciar(this.botonPlay);
  this.botonPlay.appendChild(MC.icono(this.motor.enPausa() ? 'play' : 'pausa'));
};

PantallaReproductor.prototype.aviso = function (icono) {
  var yo = this;
  MC.vaciar(this.centro);
  this.centro.appendChild(MC.icono(icono));
  MC.clase(this.centro, 'visible', true);
  clearTimeout(this.tCentro);
  this.tCentro = setTimeout(function () { MC.clase(yo.centro, 'visible', false); }, 700);
};

PantallaReproductor.prototype.alternar = function () {
  if (!this.listo) return;
  if (this.motor.enPausa()) {
    this.motor.play();
    this.aviso('play');
  } else {
    this.motor.pausa();
    this.aviso('pausa');
    this.guardar(true);
  }
  var yo = this;
  // AVPlay tarda un instante en cambiar de estado.
  setTimeout(function () { yo.ponerIconoPlay(); yo.programarOcultar(); }, 150);
  this.mostrarControles();
};

// ── Saltos ─────────────────────────────────────────────────────────────

PantallaReproductor.prototype.saltar = function (segundos, ya) {
  if (!this.listo) return;
  var yo = this, ahora = new Date().getTime(), d = this.duracion();
  if (!this.salto) this.salto = { pos: this.tiempo(), n: 0, ultima: 0 };
  var s = this.salto;
  s.n = ahora - s.ultima < 600 ? s.n + 1 : 1;
  s.ultima = ahora;
  var multiplicador = s.n > 12 ? 6 : s.n > 5 ? 3 : 1;
  s.pos = Math.max(0, Math.min(d ? d - 1 : 0, s.pos + segundos * multiplicador));
  this.mostrarControles();
  clearTimeout(this.tSalto);
  if (ya) this.confirmarSalto();
  else this.tSalto = setTimeout(function () { yo.confirmarSalto(); }, ESPERA_SALTO);
};

PantallaReproductor.prototype.confirmarSalto = function () {
  clearTimeout(this.tSalto);
  if (!this.salto) return;
  var destino = this.salto.pos;
  this.salto = null;
  this.irA(destino);
};

PantallaReproductor.prototype.cancelarSalto = function () {
  clearTimeout(this.tSalto);
  this.salto = null;
  this.pintarTiempo();
  this.programarOcultar();
};

PantallaReproductor.prototype.irA = function (segundos) {
  if (!this.listo) return;
  this.destino = segundos;
  this.destinoHora = new Date().getTime();
  if (this.subExterno) this.subExterno.i = 0;
  this.ponerTexto('');
  if (this.modo === 'directo') {
    this.motor.ir(segundos);
  } else {
    this.motor.cerrar();
    this.abrir(segundos);
  }
  this.mostrarControles();
};

// ── Teclas ─────────────────────────────────────────────────────────────

PantallaReproductor.prototype.tecla = function (t) {
  if (this.error) {
    if (t === 'ok' || t === 'atras' || t === 'stop') this.salir();
    return;
  }
  if (this.avisoSiguiente) return this.teclaSiguiente(t);
  if (this.panel) return this.teclaPanel(t);
  var visibles = this.controlesVisibles();

  if (t === 'atras') {
    if (this.salto) return this.cancelarSalto();
    if (visibles && this.foco === 'botones') return this.ocultarControles();
    return this.salir();
  }
  if (t === 'stop') return this.salir();
  if (t === 'play') { this.motor.play(); this.aviso('play'); }
  else if (t === 'pausa') { this.motor.pausa(); this.aviso('pausa'); this.guardar(true); }
  else if (t === 'playpausa') return this.alternar();
  else if (t === 'avance') return this.saltar(PASO_RAPIDO);
  else if (t === 'retroceso') return this.saltar(-PASO_RAPIDO);
  else if (t === 'izq' || t === 'der') {
    if (visibles && this.foco === 'botones') {
      this.boton = Math.max(0, Math.min(this.botones.length - 1, this.boton + (t === 'izq' ? -1 : 1)));
    } else {
      return this.saltar(t === 'izq' ? -PASO : PASO);
    }
  } else if (t === 'ok') {
    if (this.salto) return this.confirmarSalto();
    if (visibles && this.foco === 'botones') return this.botones[this.boton].accion();
    return this.alternar();
  } else if (t === 'arr') {
    this.foco = 'barra';
  } else if (t === 'aba') {
    if (visibles) {
      if (this.foco !== 'botones') this.boton = 1;
      this.foco = 'botones';
    }
  } else if (t !== 'info') return;
  var yo = this;
  setTimeout(function () { yo.ponerIconoPlay(); }, 150);
  this.mostrarControles();
};

// ── Audio y subtítulos ─────────────────────────────────────────────────

function nombreCanalesInfo(n) {
  return n === 1 ? 'Mono' : n === 2 ? 'Estéreo' : n === 6 ? '5.1' : n === 8 ? '7.1' : n ? n + ' canales' : '';
}

/** Pistas de audio. Con la información de MiCine se ven todas, también las que hay que convertir. */
PantallaReproductor.prototype.opcionesAudio = function () {
  if (!this.info || !this.info.audios.length) return this.motor.audios();
  var lista = [], actual = this.audioN, i;
  if (this.modo === 'directo') {
    var delMotor = this.motor.audios();
    for (i = 0; i < delMotor.length; i++) if (delMotor[i].activa) actual = i;
    if (actual === null) actual = (this.audioPorDefecto() || { n: 0 }).n;
  }
  for (i = 0; i < this.info.audios.length; i++) {
    var a = this.info.audios[i];
    var detalle = [nombreCanalesInfo(a.canales), String(a.codec).toUpperCase()];
    if (a.titulo) detalle.unshift(a.titulo);
    lista.push({
      id: a.n,
      idioma: MC.claveIdioma(a.idioma),
      nombre: MC.nombreIdioma(a.idioma) || 'Pista ' + (i + 1),
      detalle: detalle.join(' · ').replace(/^ · | · $/g, ''),
      activa: a.n === actual,
      info: a
    });
  }
  return lista;
};

PantallaReproductor.prototype.elegirAudio = function (opcion) {
  if (!opcion.info) return this.motor.elegirAudio(opcion.id);
  if (this.modo === 'directo' && this.audioCompatible(opcion.info)) {
    // La n-ésima pista de audio del archivo es la n-ésima que ve la tele.
    var delMotor = this.motor.audios();
    if (delMotor[opcion.id]) this.motor.elegirAudio(delMotor[opcion.id].id);
    this.audioN = opcion.id;
    return;
  }
  var t = this.tiempo();
  this.audioN = opcion.id;
  if (this.modo === 'directo') this.modo = this.videoCompatible() ? 'audio' : 'video';
  this.motor.cerrar();
  this.abrir(t);
};

/** Tras volver de otra app, la tele olvida las pistas elegidas: se vuelven a poner. */
PantallaReproductor.prototype.reaplicarPistas = function () {
  var opciones = this.opcionesAudio();
  if (this.audioN !== null && opciones[this.audioN] && !opciones[this.audioN].activa) this.elegirAudio(opciones[this.audioN]);
  if (this.subActual && this.subActual.tipo === 'interno') this.motor.elegirTexto(this.subActual.id);
};

PantallaReproductor.prototype.opcionesSubtitulos = function () {
  var opciones = [{ nombre: 'Desactivados', valor: null }], i;
  for (i = 0; i < this.peli.subtitulos.length; i++) {
    var s = this.peli.subtitulos[i];
    opciones.push({ nombre: s.etiqueta, valor: { tipo: 'externo', url: s.url, clave: s.ruta, idioma: MC.claveIdioma(s.idioma) } });
  }
  if (this.modo === 'directo' && this.enTele) {
    // Los que van dentro del vídeo los lee la propia tele.
    var internos = this.motor.textos();
    for (i = 0; i < internos.length; i++) {
      opciones.push({ nombre: internos[i].nombre, valor: { tipo: 'interno', id: internos[i].id, idioma: internos[i].idioma } });
    }
  } else if (this.info) {
    // Si no, MiCine los saca a texto (tarda un poco la primera vez: hay que leer el archivo entero).
    for (i = 0; i < this.info.subtitulos.length; i++) {
      var x = this.info.subtitulos[i];
      if (!x.texto) continue;
      opciones.push({
        nombre: (MC.nombreIdioma(x.idioma) || x.titulo || 'Pista ' + (i + 1)) + ' (dentro del vídeo)',
        valor: { tipo: 'externo', url: this.peli.url + '&subtitulo=' + x.n, clave: 'interno:' + x.n, idioma: MC.claveIdioma(x.idioma) }
      });
    }
  }
  return opciones;
};

PantallaReproductor.prototype.esSubActual = function (valor) {
  var a = this.subActual;
  if (!a || !valor) return a === valor;
  return a.tipo === valor.tipo && (a.tipo === 'interno' ? a.id === valor.id : a.clave === valor.clave);
};

/** Recuerda el último idioma elegido; si el audio no es español y hay subtítulos en español, los pone. */
PantallaReproductor.prototype.aplicarPreferencias = function () {
  var pref = MC.leer('preferencias', null) || {};
  var audios = this.opcionesAudio(), activo = null, i;
  for (i = 0; i < audios.length; i++) if (audios[i].activa) activo = audios[i];
  if (pref.audio) {
    for (i = 0; i < audios.length; i++) {
      if (audios[i].idioma === pref.audio) {
        if (!audios[i].activa) this.elegirAudio(audios[i]);
        activo = audios[i];
        break;
      }
    }
  }
  var quiero = pref.subs;
  if (quiero === undefined) quiero = activo && activo.idioma && activo.idioma !== 'es' ? 'es' : 'no';
  if (quiero === 'no') return;
  var opciones = this.opcionesSubtitulos();
  for (i = 1; i < opciones.length; i++) {
    if (opciones[i].valor.idioma === quiero) return this.elegirSubtitulo(opciones[i].valor);
  }
};

PantallaReproductor.prototype.elegirSubtitulo = function (valor) {
  var yo = this;
  this.motor.elegirTexto(valor && valor.tipo === 'interno' ? valor.id : null);
  this.subActual = valor;
  this.subExterno = null;
  this.ponerTexto('');
  if (!valor || valor.tipo !== 'externo') return;
  var x = new XMLHttpRequest();
  if (valor.clave.indexOf('interno:') === 0) MC.aviso('Preparando los subtítulos…');
  x.open('GET', MC.api.url(valor.url), true);
  x.onreadystatechange = function () {
    if (x.readyState !== 4) return;
    if (x.status === 200 && yo.subActual === valor) yo.subExterno = { cues: leerVTT(x.responseText), i: 0 };
    else if (x.status !== 200) MC.aviso('No se pudieron cargar los subtítulos.');
  };
  x.send(null);
};

function leerVTT(texto) {
  var cues = [], bloques = String(texto).replace(/\r/g, '').split(/\n\s*\n/);
  var segundos = function (h, m, s, ms) { return (+h || 0) * 3600 + (+m) * 60 + (+s) + (+ms) / Math.pow(10, ms.length); };
  for (var i = 0; i < bloques.length; i++) {
    var lineas = bloques[i].split('\n');
    for (var j = 0; j < lineas.length; j++) {
      var m = /(?:(\d+):)?(\d+):(\d+)[.,](\d+)\s*-->\s*(?:(\d+):)?(\d+):(\d+)[.,](\d+)/.exec(lineas[j]);
      if (!m) continue;
      cues.push({
        a: segundos(m[1], m[2], m[3], m[4]),
        b: segundos(m[5], m[6], m[7], m[8]),
        texto: limpiarSubtitulo(lineas.slice(j + 1).join('\n'))
      });
      break;
    }
  }
  return cues.sort(function (x, y) { return x.a - y.a; });
}

function limpiarSubtitulo(texto) {
  return String(texto || '').replace(/<[^>]*>/g, '').replace(/\{\\[^}]*\}/g, '')
    .replace(/&nbsp;/g, ' ').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&')
    .replace(/^\s+|\s+$/g, '');
}

PantallaReproductor.prototype.pintarSubExterno = function () {
  var s = this.subExterno;
  if (!s) return;
  var t = this.tiempo(), cues = s.cues;
  if (s.i >= cues.length || (s.i > 0 && cues[s.i - 1].a > t)) s.i = 0;
  while (s.i < cues.length && cues[s.i].b <= t) s.i++;
  var actual = s.i < cues.length && cues[s.i].a <= t ? cues[s.i].texto : '';
  // Puede haber dos frases a la vez (diálogos solapados).
  if (actual && s.i + 1 < cues.length && cues[s.i + 1].a <= t && cues[s.i + 1].b > t) actual += '\n' + cues[s.i + 1].texto;
  this.ponerTexto(actual);
};

PantallaReproductor.prototype.subtituloInterno = function (texto, ms) {
  var yo = this;
  if (!this.subActual || this.subActual.tipo !== 'interno') return;
  this.ponerTexto(limpiarSubtitulo(texto));
  clearTimeout(this.tSub);
  this.tSub = setTimeout(function () { yo.ponerTexto(''); }, ms);
};

PantallaReproductor.prototype.ponerTexto = function (texto) {
  if (texto === this.textoSub) return;
  this.textoSub = texto;
  MC.vaciar(this.subs);
  if (!texto) return;
  var lineas = texto.split('\n');
  for (var i = 0; i < lineas.length; i++) this.subs.appendChild(MC.h('span', null, lineas[i]));
};

PantallaReproductor.prototype.abrirPanel = function () {
  var audios = this.opcionesAudio(), subs = this.opcionesSubtitulos(), i;
  var columnas = [
    { titulo: 'Audio', items: [], i: 0 },
    { titulo: 'Subtítulos', items: [], i: 0 }
  ];
  for (i = 0; i < audios.length; i++) {
    columnas[0].items.push({ nombre: audios[i].nombre, detalle: audios[i].detalle, valor: audios[i], activo: audios[i].activa });
    if (audios[i].activa) columnas[0].i = i;
  }
  for (i = 0; i < subs.length; i++) {
    var activo = this.esSubActual(subs[i].valor);
    columnas[1].items.push({ nombre: subs[i].nombre, detalle: '', valor: subs[i].valor, activo: activo });
    if (activo) columnas[1].i = i;
  }
  this.panel = { columnas: columnas, col: audios.length > 1 ? 0 : 1 };
  this.panel.el = MC.h('div', { class: 'panel-pistas' });
  this.el.appendChild(this.panel.el);
  this.ocultarControles();
  this.pintarPanel();
};

PantallaReproductor.prototype.pintarPanel = function () {
  var p = this.panel;
  MC.vaciar(p.el);
  var caja = MC.h('div', { class: 'panel-columnas' });
  for (var c = 0; c < p.columnas.length; c++) {
    var col = p.columnas[c], lista = MC.h('div', { class: 'panel-lista' });
    for (var i = 0; i < col.items.length; i++) {
      var item = col.items[i];
      var el = MC.h('div', { class: 'panel-item' + (item.activo ? ' activo' : '') + (c === p.col && i === col.i ? ' foco' : '') },
        MC.h('span', { class: 'panel-marca' }, item.activo ? MC.icono('marca') : null),
        MC.h('span', null, item.nombre),
        item.detalle ? MC.h('span', { class: 'panel-detalle' }, item.detalle) : null);
      this.clicEnPanel(el, c, i);
      lista.appendChild(el);
    }
    caja.appendChild(MC.h('div', { class: 'panel-columna' }, MC.h('h2', null, col.titulo), lista));
  }
  p.el.appendChild(caja);
  p.el.appendChild(MC.h('div', { class: 'panel-ayuda' }, 'Pulsa VOLVER para seguir viendo'));
};

PantallaReproductor.prototype.clicEnPanel = function (el, c, i) {
  var yo = this;
  el.addEventListener('click', function () {
    yo.panel.col = c;
    yo.panel.columnas[c].i = i;
    yo.teclaPanel('ok');
  });
};

PantallaReproductor.prototype.teclaPanel = function (t) {
  var p = this.panel, col = p.columnas[p.col];
  if (t === 'arr' && col.i > 0) col.i--;
  else if (t === 'aba' && col.i < col.items.length - 1) col.i++;
  else if (t === 'izq' && p.col > 0) p.col--;
  else if (t === 'der' && p.col < p.columnas.length - 1) p.col++;
  else if (t === 'ok') this.elegirEnPanel(p.col, col.items[col.i]);
  else if (t === 'atras' || t === 'info' || t === 'stop') {
    this.el.removeChild(p.el);
    this.panel = null;
    this.mostrarControles();
    return;
  } else if (t === 'playpausa') return this.alternar();
  else return;
  this.pintarPanel();
};

PantallaReproductor.prototype.elegirEnPanel = function (c, item) {
  var pref = MC.leer('preferencias', null) || {}, lista = this.panel.columnas[c].items;
  for (var i = 0; i < lista.length; i++) lista[i].activo = lista[i] === item;
  if (c === 0) {
    this.elegirAudio(item.valor);
    if (item.valor.idioma) pref.audio = item.valor.idioma;
  } else {
    this.elegirSubtitulo(item.valor);
    pref.subs = item.valor ? item.valor.idioma || 'no' : 'no';
  }
  MC.guardar('preferencias', pref);
};

// ── Guardar, terminar, errores ─────────────────────────────────────────

PantallaReproductor.prototype.guardar = function (forzar) {
  if (!this.listo) return;
  var t = this.tiempo(), d = this.duracion();
  if (!d || t < 1 || (!forzar && Math.abs(t - this.ultimoGuardado) < 5)) return;
  this.ultimoGuardado = t;
  this.enviarProgreso(t, d);
};

PantallaReproductor.prototype.enviarProgreso = function (t, d) {
  this.peli.progreso = { t: t, d: d, fecha: new Date().getTime() / 1000 };
  MC.progresoCambiado = true;
  MC.api.pedir('POST', '/api/progreso', { ruta: this.peli.ruta, t: Math.round(t * 10) / 10, d: Math.round(d * 10) / 10 },
    null, function () { /* sin conexión: se guardará en el siguiente intento */ });
};

PantallaReproductor.prototype.terminar = function () {
  var d = this.duracion();
  if (d) this.enviarProgreso(d, d);
  this.listo = false;
  if (this.siguiente) return this.ofrecerSiguiente(true);
  this.salir();
};

// ── Siguiente episodio ─────────────────────────────────────────────────

/** Cerca del final (o al acabar) se ofrece el siguiente episodio, con el foco en él;
    si no se pulsa nada, empieza solo al terminar la cuenta atrás. */
PantallaReproductor.prototype.ofrecerSiguiente = function (alFinal) {
  var yo = this, sig = this.siguiente, aviso = this.avisoSiguiente;
  if (aviso) {
    // Ya estaba a la vista: al acabar el vídeo, «Ver créditos» pasa a ser «Volver».
    if (alFinal && !aviso.alFinal) {
      aviso.alFinal = true;
      aviso.otro.lastChild.textContent = 'Volver';
      MC.clase(aviso.el, 'al-final', true);
    }
    return;
  }
  if (this.panel) {
    this.el.removeChild(this.panel.el);
    this.panel = null;
  }
  this.ocultarControles();
  MC.clase(this.cargando, 'visible', false);
  var img = MC.h('img', { class: 'siguiente-imagen', alt: '' });
  var poster = sig.video.poster || sig.ep.obra.poster;
  if (poster) {
    img.onload = function () { MC.clase(img, 'lista', true); };
    img.src = MC.api.url(poster);
  }
  aviso = this.avisoSiguiente = { i: 0, cuenta: CUENTA_SIGUIENTE, alFinal: alFinal };
  aviso.cuentaEl = MC.h('div', { class: 'siguiente-cuenta' });
  aviso.ir = MC.h('div', { class: 'boton' }, MC.icono('siguiente'), 'Siguiente episodio');
  aviso.otro = MC.h('div', { class: 'boton secundario' }, MC.h('span', null, alFinal ? 'Volver' : 'Ver créditos'));
  aviso.ir.addEventListener('click', function () { yo.irSiguiente(); });
  aviso.otro.addEventListener('click', function () { yo.descartarSiguiente(); });
  aviso.el = MC.h('div', { class: 'siguiente' + (alFinal ? ' al-final' : '') },
    MC.h('div', { class: 'siguiente-caja' },
      img,
      MC.h('div', { class: 'siguiente-texto' },
        MC.h('div', { class: 'siguiente-etiqueta' }, 'Siguiente episodio'),
        MC.h('div', { class: 'siguiente-titulo' }, MC.etiquetaEpisodio(sig.ep) + ' · ' +
          (sig.ep.nombre || (sig.ep.episodio !== null ? 'Episodio ' + sig.ep.episodio : sig.video.titulo))),
        aviso.cuentaEl),
      MC.h('div', { class: 'siguiente-botones' }, aviso.ir, aviso.otro)));
  this.el.appendChild(aviso.el);
  this.pintarSiguiente();
  this.tCuenta = setInterval(function () {
    if (document.hidden) return;   // con la app en segundo plano la cuenta espera
    aviso.cuenta--;
    if (aviso.cuenta <= 0) yo.irSiguiente();
    else yo.pintarSiguiente();
  }, 1000);
};

PantallaReproductor.prototype.pintarSiguiente = function () {
  var a = this.avisoSiguiente;
  a.cuentaEl.textContent = 'Empieza en ' + a.cuenta + ' s';
  MC.clase(a.ir, 'foco', a.i === 0);
  MC.clase(a.otro, 'foco', a.i === 1);
};

PantallaReproductor.prototype.teclaSiguiente = function (t) {
  var a = this.avisoSiguiente;
  if (t === 'izq' || t === 'der') a.i = t === 'izq' ? 0 : 1;
  else if (t === 'ok') return a.i === 0 ? this.irSiguiente() : this.descartarSiguiente();
  else if (t === 'atras') return this.descartarSiguiente();
  else if (t === 'stop') return this.salir();
  else if (t === 'playpausa' && !a.alFinal) return this.alternar();
  else return;
  this.pintarSiguiente();
};

/** El episodio actual cuenta como visto y empieza el siguiente (a medias, desde donde se dejó). */
PantallaReproductor.prototype.irSiguiente = function () {
  clearInterval(this.tCuenta);
  var d = this.duracion();
  if (this.listo && d) this.enviarProgreso(d, d);
  this.listo = false;
  var v = this.siguiente.video;
  MC.mostrar(new PantallaReproductor(v, enCurso(v) ? Math.max(0, v.progreso.t - 3) : 0, this.volverA));
};

/** «Ver créditos» (o VOLVER): se sigue viendo sin cuenta atrás; si el vídeo ya ha acabado, se sale. */
PantallaReproductor.prototype.descartarSiguiente = function () {
  var a = this.avisoSiguiente;
  clearInterval(this.tCuenta);
  this.avisoSiguiente = null;
  this.siguienteDescartado = true;
  if (a.el.parentNode) a.el.parentNode.removeChild(a.el);
  if (a.alFinal) return this.salir();
  this.mostrarControles();
};

PantallaReproductor.prototype.salir = function () {
  this.guardar(true);
  MC.mostrar(this.volverA && this.volverA.rehacer ? this.volverA.rehacer() : this.volverA);
};

PantallaReproductor.prototype.fallo = function (texto) {
  if (this.error) return;
  this.error = true;
  this.listo = false;
  this.ocultarControles();
  MC.clase(this.cargando, 'visible', false);
  var volver = MC.h('div', { class: 'boton foco' }, 'Volver');
  var yo = this;
  volver.addEventListener('click', function () { yo.salir(); });
  this.el.appendChild(MC.h('div', { class: 'error-video' },
    MC.h('h2', null, 'No se puede reproducir «' + this.peli.titulo + '»'),
    MC.h('p', null, texto),
    MC.h('p', { class: 'pista' }, this.info
      ? 'Ni convirtiéndolo en el ordenador ha sido posible: puede que el archivo esté dañado.'
      : MC.conversion ? 'Si es un formato poco común, prueba con la app de Android.'
        : 'Si instalas FFmpeg en el ordenador (ver LEEME.md), MiCine convertirá lo que esta pantalla no sepa reproducir.'),
    volver));
};
