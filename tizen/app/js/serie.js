/* Ficha de una serie (servidor con secciones). A la izquierda, sus datos y el botón principal
   («Seguir T1·E3», «Reproducir T1·E4»…); a la derecha, las temporadas y sus episodios.
   Mando: → pasa de los botones a los episodios; ↑ desde el primer episodio lleva a las temporadas
   (← → las cambian); si un episodio tiene varias versiones (4K, 1080p…), → las recorre y OK pone
   la elegida. OK sobre el episodio pone la versión a medias o la mejor. VOLVER, a la pantalla anterior. */

var PASO_EPISODIO = 188;    // 172 de fila + 16 de separación
var ALTO_EPISODIOS = 894;   // lo que se ve de la lista

/** estado (opcional, al volver del reproductor): {zona, ep} para dejar el foco donde estaba. */
function PantallaSerie(obra, volver, estado) {
  var yo = this, i;
  estado = estado || {};
  this.obra = obra;
  this.volver = volver;
  this.objetivo = MC.objetivoSerie(obra);
  this.zona = estado.zona || 'botones';   // 'botones', 'temporadas' o 'episodios'
  this.boton = 0;
  this.chip = -1;                          // -1 = el episodio; 0, 1… = una de sus versiones
  this.posiciones = {};                    // episodio enfocado en cada temporada
  var ep = estado.ep || this.objetivo.ep;
  this.t = ep.t;
  this.posiciones[ep.t] = ep.j;

  // Izquierda: datos y botones.
  var obj = this.objetivo, et = MC.etiquetaEpisodio(obj.ep);
  this.botones = [];
  if (obj.modo === 'seguir') {
    this.botones.push({ el: MC.h('div', { class: 'boton' }, MC.icono('play'), 'Seguir ' + et),
      accion: function () { MC.reproducir(obj.video, true, yo); } });
    this.botones.push({ el: MC.h('div', { class: 'boton secundario' }, MC.icono('reinicio'), 'Desde el principio'),
      accion: function () { MC.reproducir(obj.video, false, yo); } });
  } else {
    this.botones.push({ el: MC.h('div', { class: 'boton' }, MC.icono('play'), (obj.modo === 'repetir' ? 'Volver a ver ' : 'Reproducir ') + et),
      accion: function () { MC.reproducir(obj.video, false, yo); } });
  }
  var filaBotones = MC.h('div', { class: 'botones' });
  for (i = 0; i < this.botones.length; i++) {
    filaBotones.appendChild(this.botones[i].el);
    (function (indice) {
      yo.botones[indice].el.addEventListener('click', function () { yo.zona = 'botones'; yo.boton = indice; yo.tecla('ok'); });
    })(i);
  }

  var nombre = et + (obj.ep.nombre ? ' · ' + obj.ep.nombre : '');
  var progreso = null;
  if (obj.modo === 'seguir') progreso = MC.h('div', { class: 'progreso' }, barraProgreso(obj.video, nombre));
  else if (obj.modo === 'siguiente') progreso = MC.h('div', { class: 'progreso' }, MC.h('span', null, 'Siguiente: ' + nombre));
  else if (obj.modo === 'repetir') progreso = MC.h('div', { class: 'progreso' }, MC.h('span', { class: 'vista' }, MC.icono('marca'), 'Vista entera'));

  var meta = [obra.anio, MC.textoTemporadas(obra)];
  if (!obra.identificada && obra.videos[0].carpeta) meta.unshift(obra.videos[0].carpeta.replace(/\//g, ' › '));

  // Derecha: temporadas (pestañas) y episodios.
  this.pestanas = [];
  this.filaTemporadas = MC.h('div', { class: 'temporadas' });
  if (obra.temporadas.length > 1) {
    for (i = 0; i < obra.temporadas.length; i++) {
      this.pestanas.push(MC.h('div', { class: 'temporada' }, MC.nombreTemporada(obra.temporadas[i].n)));
      this.filaTemporadas.appendChild(this.pestanas[i]);
      (function (indice) {
        yo.pestanas[indice].addEventListener('click', function () { yo.zona = 'temporadas'; yo.cambiarTemporada(indice); yo.pintar(); });
      })(i);
    }
  } else {
    this.filaTemporadas.appendChild(MC.h('div', { class: 'temporada sola' }, MC.nombreTemporada(obra.temporadas[0].n)));
  }
  this.listaEpisodios = MC.h('div', { class: 'episodios' });

  var fondo = MC.h('img', { class: 'fondo-difuso', alt: '' });
  if (obra.poster) {
    fondo.onload = function () { MC.clase(fondo, 'lista', true); };
    fondo.src = MC.api.url(obra.poster);
  }
  this.el = MC.h('div', { class: 'pantalla serie' },
    fondo,
    MC.h('div', { class: 'velo' }),
    MC.h('div', { class: 'serie-datos' },
      MC.h('h1', null, obra.titulo),
      MC.h('div', { class: 'meta' }, meta.filter(function (x) { return x; }).join('  ·  ')),
      obra.sinopsis ? MC.h('div', { class: 'info sinopsis' }, obra.sinopsis) : null,
      progreso,
      filaBotones),
    MC.h('div', { class: 'temporadas-marco' }, this.filaTemporadas),
    MC.h('div', { class: 'episodios-marco' }, this.listaEpisodios));
  this.pintarEpisodios();
}

/** Una fila por episodio de la temporada elegida (las imágenes se piden al acercarse). */
PantallaSerie.prototype.pintarEpisodios = function () {
  var yo = this, temporada = this.obra.temporadas[this.t];
  MC.vaciar(this.listaEpisodios);
  this.filas = [];
  for (var j = 0; j < temporada.episodios.length; j++) {
    var ep = temporada.episodios[j], v = versionPorDefecto(ep.versiones), e = MC.estado(ep.versiones);
    var chips = [], k;
    if (ep.versiones.length > 1) {
      for (k = 0; k < ep.versiones.length; k++) {
        var otra = ep.versiones[k];
        chips.push(MC.h('span', { class: 'chip' + (otra === v ? ' activa' : '') },
          (otra.version || MC.extension(otra.ruta)) + ' · ' + MC.tamano(otra.tamano)));
      }
    }
    var estado = e.enCurso ? 'Quedan ' + MC.duracion(e.video.progreso.d - e.video.progreso.t) : e.vista ? 'Visto' : '';
    var meta = chips.length ? [estado] : [MC.nombreVersion(v), estado];
    var poster = null;
    for (k = 0; !poster && k < ep.versiones.length; k++) poster = ep.versiones[k].poster;
    var fila = {
      ep: ep,
      chips: chips,
      poster: poster || this.obra.poster,
      img: MC.h('img', { alt: '' })
    };
    fila.el = MC.h('div', { class: 'episodio' },
      MC.h('div', { class: 'episodio-imagen' }, fila.img,
        e.vista ? MC.h('div', { class: 'tarjeta-vista' }, MC.icono('marca')) : null,
        e.enCurso ? MC.h('div', { class: 'tarjeta-barra' }, MC.h('div', { style: 'width:' + porcentaje(e.video) + '%' })) : null),
      MC.h('div', { class: 'episodio-texto' },
        MC.h('div', { class: 'episodio-titulo' }, MC.tituloEpisodio(ep)),
        MC.h('div', { class: 'episodio-meta' }, meta.filter(function (x) { return x; }).join('  ·  ')),
        chips.length ? MC.h('div', { class: 'episodio-versiones' }, chips) : null));
    this.clicEnEpisodio(fila, j);
    this.filas.push(fila);
    this.listaEpisodios.appendChild(fila.el);
  }
};

PantallaSerie.prototype.clicEnEpisodio = function (fila, j) {
  var yo = this;
  fila.el.addEventListener('click', function () {
    yo.zona = 'episodios';
    yo.posiciones[yo.t] = j;
    yo.chip = -1;
    yo.pintar();
    yo.tecla('ok');
  });
  for (var k = 0; k < fila.chips.length; k++) {
    (function (indice) {
      fila.chips[indice].addEventListener('click', function (ev) {
        ev.stopPropagation();
        yo.zona = 'episodios';
        yo.posiciones[yo.t] = j;
        yo.chip = indice;
        yo.pintar();
        yo.tecla('ok');
      });
    })(k);
  }
};

PantallaSerie.prototype.cambiarTemporada = function (t) {
  if (t === this.t) return;
  this.t = t;
  this.chip = -1;
  if (this.posiciones[t] === undefined) this.posiciones[t] = 0;
  this.pintarEpisodios();
};

PantallaSerie.prototype.pos = function () {
  return this.posiciones[this.t] || 0;
};

PantallaSerie.prototype.entrarPantalla = function () { this.pintar(); };

PantallaSerie.prototype.pintar = function () {
  var i, pos = this.pos(), n = this.filas.length;
  for (i = 0; i < this.botones.length; i++) MC.clase(this.botones[i].el, 'foco', this.zona === 'botones' && i === this.boton);
  for (i = 0; i < this.pestanas.length; i++) {
    MC.clase(this.pestanas[i], 'activa', i === this.t);
    MC.clase(this.pestanas[i], 'foco', this.zona === 'temporadas' && i === this.t);
  }
  for (i = 0; i < n; i++) {
    var f = this.filas[i], aqui = this.zona === 'episodios' && i === pos;
    MC.clase(f.el, 'foco', aqui && this.chip === -1);
    MC.clase(f.el, 'dentro', aqui && this.chip >= 0);
    for (var k = 0; k < f.chips.length; k++) MC.clase(f.chips[k], 'foco', aqui && k === this.chip);
    if (i >= pos - 2 && i <= pos + 6 && !f.cargada && f.poster) this.cargarImagen(f);
  }
  // La lista se desplaza para que el episodio enfocado se vea con el anterior encima.
  var y = Math.max(0, Math.min((pos - 1) * PASO_EPISODIO, n * PASO_EPISODIO + 24 - ALTO_EPISODIOS));
  MC.mover(this.listaEpisodios, 0, -y);
  // Muchas temporadas: las pestañas se desplazan para que la elegida se vea, con la anterior entera a su izquierda.
  var anterior = this.pestanas[this.t - 1], x = 0;
  if (anterior) x = Math.max(0, Math.min(anterior.offsetLeft, this.filaTemporadas.scrollWidth - 924));
  MC.mover(this.filaTemporadas, -x, 0);
};

PantallaSerie.prototype.cargarImagen = function (f) {
  f.cargada = true;
  var img = f.img;
  img.onload = function () { MC.clase(img, 'lista', true); };
  img.src = MC.api.url(f.poster);
};

PantallaSerie.prototype.tecla = function (t) {
  if (t === 'atras') return MC.mostrar(this.volver);
  if (t === 'play' || t === 'playpausa') {
    if (this.zona === 'episodios') return this.reproducirEpisodio();
    return this.botones[0].accion();
  }
  var pos = this.pos(), eps = this.filas, varias = this.pestanas.length > 1;
  if (this.zona === 'botones') {
    if (t === 'arr' && this.boton > 0) this.boton--;
    else if (t === 'aba' && this.boton < this.botones.length - 1) this.boton++;
    else if (t === 'der') this.zona = 'episodios';
    else if (t === 'ok') return this.botones[this.boton].accion();
    else return;
  } else if (this.zona === 'temporadas') {
    if (t === 'izq') {
      if (this.t > 0) this.cambiarTemporada(this.t - 1);
      else this.zona = 'botones';
    } else if (t === 'der' && this.t < this.pestanas.length - 1) this.cambiarTemporada(this.t + 1);
    else if (t === 'aba' || t === 'ok') this.zona = 'episodios';
    else return;
  } else {
    var fila = eps[pos];
    if (t === 'arr') {
      this.chip = -1;
      if (pos > 0) pos--;
      else if (varias) this.zona = 'temporadas';
    } else if (t === 'aba' && pos < eps.length - 1) {
      this.chip = -1;
      pos++;
    } else if (t === 'izq') {
      if (this.chip >= 0) this.chip--;
      else this.zona = 'botones';
    } else if (t === 'der' && this.chip < fila.chips.length - 1) this.chip++;
    else if (t === 'ok') return this.reproducirEpisodio();
    else return;
    this.posiciones[this.t] = pos;
  }
  this.pintar();
};

/** OK sobre un episodio: la versión elegida en sus etiquetas o, si no, la que está a medias o la mejor. */
PantallaSerie.prototype.reproducirEpisodio = function () {
  var ep = this.filas[this.pos()].ep;
  var v = this.chip >= 0 ? ep.versiones[this.chip] : versionPorDefecto(ep.versiones);
  MC.reproducir(v, enCurso(v), this);
};

/** Al volver del reproductor se rehace con el progreso nuevo; si se estaba en la lista, el foco
    va al último episodio visto (puede ser otro, si se pasó al siguiente). */
PantallaSerie.prototype.rehacer = function () {
  var zona = this.zona === 'temporadas' ? 'episodios' : this.zona, ep = null;
  if (zona === 'episodios') {
    var ultimo = MC.ultimoVideo && MC.catalogo && MC.catalogo.porRuta[MC.ultimoVideo.ruta];
    ep = ultimo && ultimo.obra === this.obra ? ultimo.ep : this.filas[this.pos()].ep;
  }
  return new PantallaSerie(this.obra, this.volver, { zona: zona, ep: ep });
};
