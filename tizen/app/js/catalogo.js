/* Catálogo por secciones (MiCine 1.3 o posterior).
   El servidor manda cada vídeo con su «obra» (película o serie), temporada, episodio y versión,
   y aparte la ficha de cada obra y la lista de secciones (Películas, Series, Anime, MiCine).
   Aquí se agrupa todo para las pantallas: una tarjeta por película o serie, temporadas y
   episodios en orden, la mejor versión de cada uno y a qué episodio lleva «Seguir viendo».
   Con un servidor antiguo (sin «obras» ni «secciones») no se usa nada de esto: la app enseña
   la lista plana de siempre. */

MC.catalogo = null;

var CALIDADES = { '4K': 3, '1080p': 2, '720p': 1 };
var TITULOS_SECCION = { peliculas: 'Películas', series: 'Series', anime: 'Anime', micine: 'MiCine' };

/** Sin calidad en el nombre cuenta como 1080p (así suelen llamarse las series bien ordenadas). */
function calidad(v) {
  return v.version ? CALIDADES[v.version] || 0 : 2;
}

/** De mejor a peor: 4K, 1080p (o sin indicar) y 720p; a igual calidad, el archivo más grande. */
function ordenarVersiones(lista) {
  return lista.sort(function (a, b) { return calidad(b) - calidad(a) || (b.tamano || 0) - (a.tamano || 0); });
}

/** La versión vista más recientemente, o null si no se ha empezado ninguna. */
function ultimaVista(versiones) {
  var u = null;
  for (var i = 0; i < versiones.length; i++) {
    var p = versiones[i].progreso;
    if (p && (!u || (p.fecha || 0) > (u.progreso.fecha || 0))) u = versiones[i];
  }
  return u;
}

/** Lo que se pone al pulsar OK: la versión que está a medias o, si no, la de más calidad
    (o la de la misma calidad que se estaba viendo, si se indica y existe). */
function versionPorDefecto(versiones, preferida) {
  var u = ultimaVista(versiones);
  if (u && enCurso(u)) return u;
  if (preferida) {
    for (var i = 0; i < versiones.length; i++) if (versiones[i].version === preferida) return versiones[i];
  }
  return versiones[0];
}

function porcentaje(v) {
  return Math.round(v.progreso.t / v.progreso.d * 100);
}

function porTitulo(a, b) {
  var x = MC.sinAcentos(a.titulo), y = MC.sinAcentos(b.titulo);
  return x < y ? -1 : x > y ? 1 : (a.anio || 0) - (b.anio || 0);
}

// ── Textos ─────────────────────────────────────────────────────────────

/** «4K · MKV · 12,3 GB»: para distinguir las versiones de una misma película o episodio. */
MC.nombreVersion = function (v) {
  return [v.version, MC.extension(v.ruta), MC.tamano(v.tamano)].filter(function (x) { return x; }).join(' · ');
};

/** «T1·E3» (o lo que se sepa). */
MC.etiquetaEpisodio = function (ep) {
  var t = ep.temporada !== null ? 'T' + ep.temporada : '';
  if (ep.episodio === null) return t;
  return (t ? t + '·' : '') + 'E' + ep.episodio;
};

/** «3. El gato está en la bolsa»; si no se sabe el nombre, «3. Episodio 3». */
MC.tituloEpisodio = function (ep) {
  if (ep.episodio === null) return ep.nombre || ep.versiones[0].titulo;
  return ep.episodio + '. ' + (ep.nombre || 'Episodio ' + ep.episodio);
};

MC.nombreTemporada = function (n) {
  return n === null ? 'Otros' : n === 0 ? 'Especiales' : 'Temporada ' + n;
};

/** «2 temporadas» o «8 episodios». */
MC.textoTemporadas = function (o) {
  var n = o.temporadas.length, e = o.episodios.length;
  return n > 1 ? n + ' temporadas' : e + (e === 1 ? ' episodio' : ' episodios');
};

// ── Construcción ───────────────────────────────────────────────────────

/** Agrupa la respuesta de /api/peliculas. Devuelve null si el servidor es antiguo. */
MC.crearCatalogo = function (r) {
  if (!r || !r.obras || !r.secciones) return null;
  var cat = { obras: [], porId: {}, porRuta: {}, secciones: [] };
  var pelis = r.peliculas || [], i, k, o;
  for (i = 0; i < pelis.length; i++) {
    var v = pelis[i], id = v.obra || 'ruta:' + v.ruta;
    o = cat.porId[id];
    if (!o) {
      var f = r.obras[id] || {};
      var serie = f.tipo ? f.tipo === 'serie' : typeof v.episodio === 'number';
      o = cat.porId[id] = {
        id: id,
        serie: serie,
        titulo: f.titulo || (serie ? String(v.titulo).split(' · ')[0] : v.titulo),
        anio: f.anio || null,
        sinopsis: f.sinopsis || null,
        poster: f.poster || null,
        generos: f.generos && f.generos.length ? f.generos : [],   // de TMDB (MiCine 1.4 o posterior)
        seccion: f.seccion || v.seccion || (serie ? 'series' : 'peliculas'),
        identificada: f.identificada !== false,
        videos: [],
        fecha: 0
      };
      cat.obras.push(o);
    }
    o.videos.push(v);
    o.fecha = Math.max(o.fecha, v.fecha || 0);
  }

  for (i = 0; i < cat.obras.length; i++) {
    o = cat.obras[i];
    var conImagen = [];
    if (o.serie) {
      agruparEpisodios(o, cat);
      for (k = 0; k < o.episodios.length; k++) conImagen = conImagen.concat(o.episodios[k].versiones);
    } else {
      o.versiones = ordenarVersiones(o.videos.slice());
      for (k = 0; k < o.versiones.length; k++) cat.porRuta[o.versiones[k].ruta] = { obra: o, ep: null };
      conImagen = o.versiones;
    }
    // Normalmente la carátula de la obra ya la elige el servidor; por si acaso, la del primer vídeo que tenga.
    for (k = 0; !o.poster && k < conImagen.length; k++) o.poster = conImagen[k].poster || null;
  }

  // Secciones: las que manda el servidor, en su orden y solo si tienen algo.
  var hay = {}, puestas = {};
  for (i = 0; i < cat.obras.length; i++) hay[cat.obras[i].seccion] = true;
  for (i = 0; i < r.secciones.length; i++) {
    var s = r.secciones[i];
    if (!s || !hay[s.id] || puestas[s.id]) continue;
    puestas[s.id] = true;
    cat.secciones.push({ id: s.id, titulo: s.titulo || TITULOS_SECCION[s.id] || s.id });
  }
  for (i = 0; i < cat.obras.length; i++) {
    var sid = cat.obras[i].seccion;
    if (puestas[sid]) continue;
    puestas[sid] = true;
    cat.secciones.push({ id: sid, titulo: TITULOS_SECCION[sid] || sid.charAt(0).toUpperCase() + sid.slice(1) });
  }
  return cat;
};

function numeroOrden(n) {
  // Los especiales (temporada 0) y lo que no tiene número, al final.
  return n === null ? 100001 : n === 0 ? 100000 : n;
}

/** Temporadas y episodios de una serie, en orden; las versiones de un mismo episodio van juntas. */
function agruparEpisodios(o, cat) {
  var porClave = {}, eps = [], ep, i, k;
  for (i = 0; i < o.videos.length; i++) {
    var v = o.videos[i];
    var t = typeof v.temporada === 'number' ? v.temporada : null;
    var e = typeof v.episodio === 'number' ? v.episodio : null;
    var clave = e === null ? 'ruta|' + v.ruta : t + '|' + e;
    ep = porClave[clave];
    if (!ep) {
      ep = porClave[clave] = { obra: o, temporada: t, episodio: e, nombre: null, versiones: [] };
      eps.push(ep);
    }
    ep.versiones.push(v);
    if (!ep.nombre && v.nombre_episodio) ep.nombre = v.nombre_episodio;
  }
  eps.sort(function (a, b) {
    return numeroOrden(a.temporada) - numeroOrden(b.temporada) || numeroOrden(a.episodio) - numeroOrden(b.episodio) ||
      (a.versiones[0].ruta < b.versiones[0].ruta ? -1 : 1);
  });
  o.episodios = eps;
  o.temporadas = [];
  for (i = 0; i < eps.length; i++) {
    ep = eps[i];
    ep.i = i;
    ordenarVersiones(ep.versiones);
    var ultima = o.temporadas[o.temporadas.length - 1];
    if (!ultima || ultima.n !== ep.temporada) {
      ultima = { n: ep.temporada, episodios: [] };
      o.temporadas.push(ultima);
    }
    ep.t = o.temporadas.length - 1;   // su temporada (posición en o.temporadas)
    ep.j = ultima.episodios.length;   // su posición dentro de la temporada
    ultima.episodios.push(ep);
    for (k = 0; k < ep.versiones.length; k++) cat.porRuta[ep.versiones[k].ruta] = { obra: o, ep: ep };
  }
}

// ── Progreso ───────────────────────────────────────────────────────────

/** Cómo va una película o un episodio, según la versión vista más recientemente. */
MC.estado = function (versiones) {
  var u = ultimaVista(versiones);
  return { video: u, enCurso: !!u && enCurso(u), vista: !!u && vista(u), fecha: u ? u.progreso.fecha || 0 : 0 };
};

/** A qué episodio lleva una serie: el que está a medias, el siguiente al último visto o el primero.
    modo: «seguir», «siguiente», «empezar» o «repetir» (ya se ha visto entera). */
MC.objetivoSerie = function (o) {
  var ultimo = null, estado = null, i;
  for (i = 0; i < o.episodios.length; i++) {
    var e = MC.estado(o.episodios[i].versiones);
    if ((e.enCurso || e.vista) && (!estado || e.fecha > estado.fecha)) {
      ultimo = o.episodios[i];
      estado = e;
    }
  }
  var primero = o.episodios[0];
  if (!ultimo) return { ep: primero, modo: 'empezar', fecha: 0, video: versionPorDefecto(primero.versiones) };
  if (estado.enCurso) return { ep: ultimo, modo: 'seguir', fecha: estado.fecha, video: estado.video };
  var sig = o.episodios[ultimo.i + 1];
  if (sig) return { ep: sig, modo: 'siguiente', fecha: estado.fecha, video: versionPorDefecto(sig.versiones, estado.video.version) };
  return { ep: primero, modo: 'repetir', fecha: estado.fecha, video: versionPorDefecto(primero.versiones) };
};

/** El episodio que va después de este vídeo (en la misma calidad, si la hay), o null. */
MC.siguienteEpisodio = function (video) {
  var x = MC.catalogo && MC.catalogo.porRuta[video.ruta];
  if (!x || !x.ep) return null;
  var sig = x.obra.episodios[x.ep.i + 1];
  return sig ? { ep: sig, video: versionPorDefecto(sig.versiones, video.version) } : null;
};

// ── Tarjetas ───────────────────────────────────────────────────────────
// Un «elemento» es lo que hay detrás de cada tarjeta: {clave, obra, d (lo que se dibuja), video?, objetivo?}.

/** Tarjeta de una película o una serie. */
MC.elementoObra = function (o) {
  var d = { titulo: o.titulo, poster: o.poster };
  if (!o.serie) {
    var e = MC.estado(o.versiones);
    if (e.enCurso) d.pct = porcentaje(e.video);
    else if (e.vista) d.vista = true;
  }
  return { clave: 'obra:' + o.id, obra: o, d: d };
};

/** «Seguir viendo»: películas a medias y, de cada serie, el episodio a medias o el que toca. */
MC.seguirViendo = function (obras) {
  var lista = [];
  for (var i = 0; i < obras.length; i++) {
    var o = obras[i];
    if (o.serie) {
      var obj = MC.objetivoSerie(o);
      if (obj.modo !== 'seguir' && obj.modo !== 'siguiente') continue;
      lista.push({
        clave: 'seguir:' + o.id, obra: o, objetivo: obj, fecha: obj.fecha,
        d: { titulo: o.titulo, poster: o.poster, etiqueta: MC.etiquetaEpisodio(obj.ep), pct: obj.modo === 'seguir' ? porcentaje(obj.video) : null }
      });
    } else {
      var e = MC.estado(o.versiones);
      if (!e.enCurso) continue;
      lista.push({
        clave: 'seguir:' + o.id, obra: o, video: e.video, fecha: e.fecha,
        d: { titulo: o.titulo, poster: o.poster, pct: porcentaje(e.video) }
      });
    }
  }
  return lista.sort(function (a, b) { return b.fecha - a.fecha; });
};

/** OK sobre una tarjeta: la ficha de la película o de la serie. */
MC.abrirElemento = function (el, volver) {
  var o = el.obra;
  if (o.serie) return MC.mostrar(new PantallaSerie(o, volver));
  MC.mostrar(new PantallaDetalle(el.video || versionPorDefecto(o.versiones), volver, o));
};

/** PLAY sobre una tarjeta: directamente al vídeo (si estaba a medias, se sigue). */
MC.reproducirElemento = function (el, volver) {
  var o = el.obra;
  if (o.serie) {
    var obj = el.objetivo || MC.objetivoSerie(o);
    return MC.reproducir(obj.video, obj.modo === 'seguir', volver);
  }
  var v = el.video || versionPorDefecto(o.versiones);
  MC.reproducir(v, enCurso(v), volver);
};
