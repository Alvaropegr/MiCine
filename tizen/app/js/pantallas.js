/* Pantallas que se manejan con el mando: conectar, inicio (filas como Netflix), ficha y buscar.
   Cada pantalla tiene un elemento «el», y «tecla(t)» recibe las teclas ya traducidas
   («izq», «ok», «atras»…). «entrar()» se llama cada vez que se muestra.
   Con un servidor con secciones (MiCine 1.3) el inicio tiene pestañas (Inicio, Películas, Series…)
   y las series se abren en su propia ficha (serie.js); con uno antiguo, todo sigue como antes. */

var ANCHO_TARJETA = 228;   // 200 de carátula + 28 de separación
var ALTO_FILA = 460;           // título de la fila, carátula (300) y su título debajo
var ALTO_FILA_REJILLA = 400;   // filas de la rejilla «de la A a la Z», sin título
var MAX_FILA = 30;             // tarjetas por sección (o género); el resto, en «Ver todo»
var MIN_GENERO = 2;            // un género con menos títulos no tiene fila propia
var LETRAS = '#ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');

/** La letra del abecedario de un título: A-Z (sin acentos; la Ñ va con la N) o «#» (números y signos). */
function letraDe(titulo) {
  var c = MC.sinAcentos(titulo || '').replace(/^[\s¡¿"'«(\[.]+/, '').charAt(0).toUpperCase();
  return c >= 'A' && c <= 'Z' ? c : '#';
}

function enCurso(p) {
  return !!p.progreso && p.progreso.t > 15 && p.progreso.t < p.progreso.d * 0.95;
}

function vista(p) {
  return !!p.progreso && p.progreso.d > 0 && p.progreso.t >= p.progreso.d * 0.95;
}

/** Lo que se dibuja en la tarjeta de un vídeo suelto (servidor antiguo). */
function datosVideo(p) {
  return { titulo: p.titulo, poster: p.poster, vista: vista(p), pct: enCurso(p) ? Math.round(p.progreso.t / p.progreso.d * 100) : null };
}

/** Carátula vertical con el título debajo; la imagen se pide solo cuando hace falta y se recorta
    para llenar el hueco (los fotogramas del vídeo son apaisados).
    d = {titulo, poster, pct (barra de progreso), vista, etiqueta («T1·E3»), clase}.
    Las tarjetas «Ver todo» (clase «todo») llevan el texto dentro y nada debajo. */
function Tarjeta(peli, d) {
  d = d || datosVideo(peli);
  this.peli = peli;
  this.poster = d.poster;
  this.img = MC.h('img', { alt: '' });
  var todo = d.clase === 'todo';
  this.el = MC.h('div', { class: 'tarjeta' + (d.clase ? ' ' + d.clase : '') },
    MC.h('div', { class: 'tarjeta-caja' },
      todo ? MC.h('div', { class: 'tarjeta-titulo' }, d.titulo)
        : !d.poster ? MC.h('div', { class: 'tarjeta-sin-imagen' }, MC.icono('play')) : null,
      this.img,
      d.vista ? MC.h('div', { class: 'tarjeta-vista' }, MC.icono('marca')) : null,
      d.etiqueta ? MC.h('div', { class: 'tarjeta-etiqueta' }, d.etiqueta) : null,
      d.pct !== null && d.pct !== undefined ? MC.h('div', { class: 'tarjeta-barra' },
        MC.h('div', { style: 'width:' + d.pct + '%' })) : null),
    todo ? null : MC.h('div', { class: 'tarjeta-pie' }, d.titulo));
  this.cargada = false;
}

Tarjeta.prototype.cargar = function () {
  if (this.cargada || !this.poster) return;
  this.cargada = true;
  var img = this.img;
  img.onload = function () { MC.clase(img, 'lista', true); };
  img.src = MC.api.url(this.poster);
};

/** Barra de progreso y «Quedan 20 min» (en el inicio, las fichas…). */
function barraProgreso(v, delante) {
  return [
    MC.h('div', { class: 'barra' }, MC.h('div', { style: 'width:' + Math.round(v.progreso.t / v.progreso.d * 100) + '%' })),
    MC.h('span', null, (delante ? delante + '  ·  ' : '') + 'Quedan ' + MC.duracion(v.progreso.d - v.progreso.t))
  ];
}

/** Lista vertical de botones y campos de texto. */
function Vertical(items) {
  this.items = items;
  this.i = 0;
  var yo = this;
  for (var k = 0; k < items.length; k++) {
    (function (indice) {
      items[indice].el.addEventListener('click', function () {
        yo.i = indice;
        yo.pintar();
        yo.tecla('ok');
      });
    })(k);
  }
}

Vertical.prototype.pintar = function () {
  for (var k = 0; k < this.items.length; k++) MC.clase(this.items[k].el, 'foco', k === this.i);
};

Vertical.prototype.ir = function (i) {
  this.i = Math.max(0, Math.min(this.items.length - 1, i));
  this.pintar();
};

Vertical.prototype.tecla = function (t) {
  if (t === 'arr' && this.i > 0) this.ir(this.i - 1);
  else if (t === 'aba' && this.i < this.items.length - 1) this.ir(this.i + 1);
  else if (t === 'ok') {
    var item = this.items[this.i];
    if (item.campo) item.campo.focus();
    else if (item.accion) item.accion();
  } else return false;
  return true;
};

/** Teclas mientras se escribe en un campo (con el teclado de la tele abierto o no). */
Vertical.prototype.teclaCampo = function (t) {
  var campo = document.activeElement;
  if (campo && campo.blur) campo.blur();
  if (t === 'ok' || t === 'hecho' || t === 'aba') this.ir(this.i + 1);
  else if (t === 'arr') this.ir(this.i - 1);
};

function logo() {
  return MC.h('div', { class: 'logo' }, MC.h('span', { class: 'logo-icono' }, MC.icono('play')), 'MiCine');
}

// ── Conectar ───────────────────────────────────────────────────────────

function PantallaConectar(mensaje) {
  var sesion = MC.leer('sesion', null) || {};
  var yo = this;
  this.servidor = MC.h('input', {
    type: 'text', placeholder: '192.168.1.20:8080', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off'
  });
  this.servidor.value = (sesion.servidor || '').replace(/^http:\/\//, '');
  this.usuario = MC.h('input', { type: 'text', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off' });
  this.usuario.value = sesion.usuario || '';
  this.clave = MC.h('input', { type: 'password' });
  this.mensaje = MC.h('div', { class: 'mensaje' + (mensaje ? ' error' : '') }, mensaje || '');
  this.botonBuscar = MC.h('div', { class: 'boton secundario' }, MC.icono('lupa'), MC.h('span', null, 'Buscar MiCine en mi red'));
  var campo = function (texto, input) {
    return MC.h('div', { class: 'campo' }, MC.h('label', null, texto), input);
  };
  var items = [
    { el: campo('Dirección de MiCine', this.servidor), campo: this.servidor },
    { el: this.botonBuscar, accion: function () { yo.buscar(); } },
    { el: campo('Usuario', this.usuario), campo: this.usuario },
    { el: campo('Contraseña', this.clave), campo: this.clave },
    { el: MC.h('div', { class: 'boton' }, 'Entrar'), accion: function () { yo.entrar(); } }
  ];
  this.lista = new Vertical(items);
  var formulario = MC.h('div', { class: 'formulario' });
  for (var i = 0; i < items.length; i++) formulario.appendChild(items[i].el);
  this.el = MC.h('div', { class: 'pantalla conectar' },
    MC.h('div', { class: 'panel' },
      logo(),
      MC.h('h1', null, 'Conecta con tu MiCine'),
      MC.h('p', { class: 'ayuda' }, 'La dirección aparece en la ventana de MiCine del ordenador. ' +
        'Fuera de casa, escribe la dirección https:// de tu túnel.'),
      formulario,
      this.mensaje));
  this.lista.ir(!this.servidor.value ? 0 : !this.usuario.value ? 2 : 3);
}

PantallaConectar.prototype.entrar = function () {
  if (this.ocupado) return;
  var yo = this;
  var servidor = MC.normalizarServidor(this.servidor.value);
  var usuario = this.usuario.value.replace(/^\s+|\s+$/g, '');
  if (!servidor) return this.avisar('Escribe la dirección de MiCine.', 0);
  if (!usuario) return this.avisar('Escribe tu usuario.', 2);
  if (!this.clave.value) return this.avisar('Escribe tu contraseña.', 3);
  this.ocupado = true;
  this.avisar('Conectando…');
  MC.api.servidor = servidor;
  MC.api.token = '';
  MC.api.pedir('POST', '/api/login', { usuario: usuario, contrasena: this.clave.value, app: true }, function (r) {
    yo.ocupado = false;
    MC.api.token = r.token;
    MC.guardar('sesion', { servidor: servidor, usuario: r.usuario.nombre, token: r.token });
    MC.irInicio();
  }, function (e) {
    yo.ocupado = false;
    yo.clave.value = '';
    yo.avisar(e.codigo === 401 ? 'Usuario o contraseña incorrectos.' : e.mensaje, e.codigo === 0 ? 0 : 3);
  });
};

PantallaConectar.prototype.avisar = function (texto, foco) {
  MC.vaciar(this.mensaje);
  this.mensaje.appendChild(document.createTextNode(texto));
  MC.clase(this.mensaje, 'error', foco !== undefined);
  if (foco !== undefined) this.lista.ir(foco);
};

PantallaConectar.prototype.buscar = function () {
  var yo = this;
  if (this.busqueda) return;
  this.avisar('Buscando MiCine en tu red…');
  this.busqueda = MC.buscarEnRed(function (parte) {
    yo.avisar('Buscando MiCine en tu red… ' + Math.round(parte * 100) + ' %');
  }, function (url) {
    yo.busqueda = null;
    yo.servidor.value = url.replace(/^http:\/\//, '');
    yo.avisar('MiCine encontrado. Ahora escribe tu usuario y contraseña.');
    yo.lista.ir(yo.usuario.value ? 3 : 2);
  }, function (sePudo) {
    yo.busqueda = null;
    yo.avisar(sePudo ? 'No he encontrado MiCine. Comprueba que está abierto en el ordenador y escribe su dirección.'
      : 'Esta tele no permite buscar. Escribe la dirección que muestra MiCine en el ordenador.', 0);
  });
};

PantallaConectar.prototype.entrarPantalla = function () {
  this.lista.pintar();
  if (!this.servidor.value) this.buscar();
};

PantallaConectar.prototype.salirPantalla = function () {
  if (this.busqueda) this.busqueda.parar();
  this.busqueda = null;
};

PantallaConectar.prototype.tecla = function (t) {
  if (this.lista.tecla(t)) return;
  if (t === 'atras') MC.preguntarSalir();
};

PantallaConectar.prototype.teclaCampo = function (t) {
  var enClave = this.lista.i === 3;
  this.lista.teclaCampo(t);
  if (enClave && (t === 'ok' || t === 'hecho')) this.entrar();
};

// ── Inicio ─────────────────────────────────────────────────────────────

function PantallaInicio() {
  this.pelis = [];
  this.catalogo = null;   // solo con un servidor con secciones
  this.seccion = null;    // pestaña elegida (null = Inicio)
  this.genero = null;     // dentro de una pestaña, «Ver todo» de un género (null = la pestaña entera)
  this.filas = [];
  this.fila = 0;
  this.menuI = 0;
  this.letras = null;     // letra → {fila, pos} de su primer título en la rejilla (null = sin abecedario)
  this.enAbc = false;     // el foco está en el abecedario
  this.abcI = 0;          // letra enfocada (posición en LETRAS)

  this.fondo = MC.h('img', { class: 'fondo-difuso', alt: '' });
  this.arte = MC.h('img', { class: 'heroe-arte', alt: '' });
  this.heroeTitulo = MC.h('h1');
  this.heroeMeta = MC.h('div', { class: 'meta' });
  this.heroeInfo = MC.h('div', { class: 'info' });
  this.heroeBarra = MC.h('div', { class: 'progreso' });
  this.pista = MC.h('div', { class: 'filas-pista' });
  this.menuSecciones = MC.h('div', { class: 'menu-secciones' });
  this.menuAcciones = MC.h('div', { class: 'menu-items' });
  this.construirMenu();

  // Abecedario a la izquierda, en las pestañas: lleva a la primera película o serie de cada letra.
  this.abc = MC.h('div', { class: 'abecedario' });
  this.abcLetras = [];
  var yo = this;
  for (var i = 0; i < LETRAS.length; i++) {
    this.abcLetras.push(MC.h('div', { class: 'abc-letra' }, LETRAS[i]));
    this.abc.appendChild(this.abcLetras[i]);
    (function (indice) {
      yo.abcLetras[indice].addEventListener('click', function () {
        if (!yo.letras || !yo.letras[LETRAS[indice]]) return;
        yo.enAbc = true;
        yo.irLetra(indice);
        yo.tecla('ok');
      });
    })(i);
  }

  this.el = MC.h('div', { class: 'pantalla inicio' },
    this.fondo,
    MC.h('div', { class: 'velo' }),
    MC.h('div', { class: 'menu' }, logo(), this.menuSecciones, this.menuAcciones),
    this.arte,
    MC.h('div', { class: 'heroe' }, this.heroeTitulo, this.heroeMeta, this.heroeInfo, this.heroeBarra),
    MC.h('div', { class: 'filas' }, this.pista),
    this.abc);
}

/** Menú de arriba. Con secciones: pestañas a la izquierda y Buscar, Actualizar y Cerrar sesión
    como botones redondos a la derecha (su nombre sale debajo al enfocarlos). Sin secciones, como siempre. */
PantallaInicio.prototype.construirMenu = function () {
  var yo = this, usuario = (MC.leer('sesion', null) || {}).usuario || '', nuevo = !!this.catalogo, i;
  this.menu = [];
  MC.vaciar(this.menuSecciones);
  MC.vaciar(this.menuAcciones);
  if (nuevo) {
    var pestanas = [{ id: null, titulo: 'Inicio' }].concat(this.catalogo.secciones);
    for (i = 0; i < pestanas.length; i++) {
      this.menu.push({
        el: MC.h('div', { class: 'menu-item menu-seccion' }, pestanas[i].titulo),
        seccion: pestanas[i].id,
        pestana: true,
        accion: (function (id) { return function () { yo.irSeccion(id); }; })(pestanas[i].id)
      });
      this.menuSecciones.appendChild(this.menu[i].el);
    }
  }
  var acciones = [
    ['lupa', 'Buscar', function () { yo.buscar(); }],
    ['recargar', 'Actualizar', function () { yo.cargar(); }],
    ['salir', 'Cerrar sesión (' + usuario + ')', function () { MC.cerrarSesion(); }]
  ];
  for (i = 0; i < acciones.length; i++) {
    var a = acciones[i];
    var el = nuevo
      ? MC.h('div', { class: 'menu-item redondo' }, MC.icono(a[0]), MC.h('span', { class: 'menu-etiqueta' }, a[1]))
      : MC.h('div', { class: 'menu-item' }, MC.icono(a[0]), a[1]);
    if (a[0] === 'recargar') this.iActualizar = this.menu.length;
    this.menu.push({ el: el, accion: a[2] });
    this.menuAcciones.appendChild(el);
  }
  for (i = 0; i < this.menu.length; i++) {
    (function (indice) {
      yo.menu[indice].el.addEventListener('click', function () {
        yo.fila = -1;
        yo.menuI = indice;
        yo.pintar();
        yo.tecla('ok');
      });
    })(i);
  }
  if (this.menuI >= this.menu.length) this.menuI = 0;
};

/** Posición en el menú de la pestaña que se está viendo. */
PantallaInicio.prototype.indicePestana = function () {
  for (var i = 0; i < this.menu.length; i++) if (this.menu[i].pestana && this.menu[i].seccion === this.seccion) return i;
  return 0;
};

PantallaInicio.prototype.entrarPantalla = function () {
  if (!this.cargado) this.cargar();
  else if (MC.progresoCambiado) this.construir();
  else this.pintar();
  MC.progresoCambiado = false;
};

PantallaInicio.prototype.cargar = function () {
  var yo = this;
  this.mostrarTexto('Cargando películas…', '');
  MC.api.pedir('GET', '/api/peliculas', null, function (r) {
    yo.cargado = true;
    yo.pelis = r.peliculas || [];
    MC.conversion = !!r.conversion;
    yo.catalogo = MC.catalogo = MC.crearCatalogo(r);
    if (!yo.catalogo || !yo.existeSeccion(yo.seccion)) yo.seccion = null;
    yo.construirMenu();
    yo.construir();
  }, function (e) {
    if (e.codigo === 401) return MC.cerrarSesion('Tu sesión ha caducado. Vuelve a entrar.');
    yo.mostrarTexto('No se pudo cargar MiCine', e.mensaje + ' Pulsa OK en «Actualizar» para reintentar.');
    yo.fila = -1;
    yo.menuI = yo.iActualizar;
    yo.pintar();
  });
};

PantallaInicio.prototype.existeSeccion = function (id) {
  if (id === null) return true;
  for (var i = 0; i < this.catalogo.secciones.length; i++) if (this.catalogo.secciones[i].id === id) return true;
  return false;
};

PantallaInicio.prototype.mostrarTexto = function (titulo, texto) {
  this.heroeTitulo.textContent = titulo;
  this.heroeMeta.textContent = '';
  this.heroeInfo.textContent = texto;
  MC.vaciar(this.heroeBarra);
  MC.clase(this.arte, 'lista', false);
  MC.clase(this.fondo, 'lista', false);
};

/** Filas como antes (servidor antiguo): seguir viendo, añadidas recientemente y una por carpeta. */
PantallaInicio.prototype.filasPlanas = function () {
  var pelis = this.pelis, filas = [], porCarpeta = {}, carpetas = [], i;
  var seguir = pelis.filter(enCurso).sort(function (a, b) { return b.progreso.fecha - a.progreso.fecha; });
  if (seguir.length) filas.push({ titulo: 'Seguir viendo', pelis: seguir });
  if (pelis.length > 10) {
    filas.push({
      titulo: 'Añadidas recientemente',
      pelis: pelis.slice().sort(function (a, b) { return b.fecha - a.fecha; }).slice(0, 20)
    });
  }
  for (i = 0; i < pelis.length; i++) {
    var c = pelis[i].carpeta || '';
    if (!porCarpeta[c]) {
      porCarpeta[c] = [];
      carpetas.push(c);
    }
    porCarpeta[c].push(pelis[i]);
  }
  carpetas.sort(function (a, b) { return a === '' ? -1 : b === '' ? 1 : a.localeCompare(b); });
  for (i = 0; i < carpetas.length; i++) {
    filas.push({ titulo: carpetas[i] === '' ? 'Películas' : carpetas[i].replace(/\//g, ' › '), pelis: porCarpeta[carpetas[i]] });
  }
  return filas;
};

/** Filas con secciones. Inicio: seguir viendo, una por sección (lo último primero) y añadidas
    recientemente. Una pestaña: lo suyo a medias, lo último, una fila por género (según TMDB) y todo
    de la A a la Z en rejilla. «Ver todo» de un género: solo la rejilla de ese género. */
PantallaInicio.prototype.filasCatalogo = function () {
  var cat = this.catalogo, sec = this.seccion, genero = this.genero, filas = [], i;
  var obras = sec === null ? cat.obras : cat.obras.filter(function (o) { return o.seccion === sec; });
  if (sec !== null && genero !== null) {
    var delGenero = obras.filter(function (o) { return o.generos.indexOf(genero) >= 0; });
    var azGenero = delGenero.sort(porTitulo).map(MC.elementoObra);
    for (i = 0; i < azGenero.length; i += COLUMNAS) {
      filas.push({ titulo: i === 0 ? genero + ' · de la A a la Z' : null, pelis: azGenero.slice(i, i + COLUMNAS), rejilla: true });
    }
    return filas;
  }
  var recientes = obras.slice().sort(function (a, b) { return b.fecha - a.fecha; });

  var seguir = MC.seguirViendo(obras);
  if (seguir.length) filas.push({ titulo: 'Seguir viendo', pelis: seguir });

  if (sec === null) {
    for (i = 0; i < cat.secciones.length; i++) {
      var s = cat.secciones[i];
      var suyas = recientes.filter(function (o) { return o.seccion === s.id; });
      var fila = { titulo: s.titulo, pelis: suyas.slice(0, MAX_FILA).map(MC.elementoObra) };
      if (suyas.length > MAX_FILA) {
        fila.pelis.push({ clave: 'todo:' + s.id, todo: s, n: suyas.length,
          d: { titulo: 'Ver todo (' + suyas.length + ')', clase: 'todo' } });
      }
      filas.push(fila);
    }
    if (cat.secciones.length > 1 || obras.length > 10) {
      filas.push({ titulo: 'Añadidas recientemente', pelis: recientes.slice(0, 20).map(MC.elementoObra) });
    }
  } else {
    if (obras.length > 14) filas.push({ titulo: 'Añadidas recientemente', pelis: recientes.slice(0, 20).map(MC.elementoObra) });
    filas = filas.concat(this.filasGeneros(recientes));
    var az = obras.slice().sort(porTitulo).map(MC.elementoObra);
    for (i = 0; i < az.length; i += COLUMNAS) {
      filas.push({ titulo: i === 0 ? 'De la A a la Z' : null, pelis: az.slice(i, i + COLUMNAS), rejilla: true });
    }
  }
  return filas;
};

/** Una fila por género, de los que más títulos tienen a los que menos (lo último primero en cada una).
    No salen los géneros con muy pocos títulos ni los que tienen todo (en «Anime», la animación). */
PantallaInicio.prototype.filasGeneros = function (recientes) {
  var porGenero = {}, nombres = [], filas = [], i, k;
  for (i = 0; i < recientes.length; i++) {
    var gs = recientes[i].generos;
    for (k = 0; k < gs.length; k++) {
      if (!porGenero[gs[k]]) {
        porGenero[gs[k]] = [];
        nombres.push(gs[k]);
      }
      porGenero[gs[k]].push(recientes[i]);
    }
  }
  nombres = nombres.filter(function (g) { return porGenero[g].length >= MIN_GENERO && porGenero[g].length < recientes.length; });
  nombres.sort(function (a, b) { return porGenero[b].length - porGenero[a].length || (a < b ? -1 : a > b ? 1 : 0); });
  for (i = 0; i < nombres.length; i++) {
    var suyas = porGenero[nombres[i]];
    var fila = { titulo: nombres[i], pelis: suyas.slice(0, MAX_FILA).map(MC.elementoObra) };
    if (suyas.length > MAX_FILA) {
      fila.pelis.push({ clave: 'genero:' + nombres[i], genero: nombres[i], n: suyas.length,
        d: { titulo: 'Ver todo (' + suyas.length + ')', clase: 'todo' } });
    }
    filas.push(fila);
  }
  return filas;
};

/** Rehace las filas. Tras ver algo se vuelve a la misma tarjeta (o a la de su obra). */
PantallaInicio.prototype.construir = function (sinRecordar) {
  var anterior = sinRecordar ? null : this.actual();
  var filaAnterior = this.filas[this.fila] ? this.filas[this.fila].titulo : null;
  var filas = this.catalogo ? this.filasCatalogo() : this.filasPlanas();
  var y = 0, i;

  MC.vaciar(this.pista);
  for (i = 0; i < filas.length; i++) {
    var f = filas[i];
    f.pos = 0;
    f.y = y;
    f.tarjetas = [];
    f.carril = MC.h('div', { class: 'fila-tarjetas' });
    for (var k = 0; k < f.pelis.length; k++) {
      var t = new Tarjeta(f.pelis[k], f.pelis[k].d);
      this.clicEnTarjeta(t, i, k);
      f.tarjetas.push(t);
      f.carril.appendChild(t.el);
    }
    this.pista.appendChild(MC.h('div', { class: 'fila' + (f.titulo === null ? ' sin-titulo' : '') },
      f.titulo !== null ? MC.h('div', { class: 'fila-titulo' }, f.titulo) : null, f.carril));
    y += f.titulo === null ? ALTO_FILA_REJILLA : ALTO_FILA;
  }
  this.filas = filas;

  // Abecedario: el primer título de cada letra en la rejilla «de la A a la Z» (solo en las pestañas).
  this.letras = null;
  this.enAbc = false;
  if (this.catalogo && this.seccion !== null) {
    for (i = 0; i < filas.length; i++) {
      if (!filas[i].rejilla) continue;
      for (var p = 0; p < filas[i].pelis.length; p++) {
        var letra = letraDe(filas[i].pelis[p].obra.titulo);
        if (!this.letras) this.letras = {};
        if (!this.letras[letra]) this.letras[letra] = { fila: i, pos: p };
      }
    }
  }

  if (!filas.length) {
    this.mostrarTexto('Aún no hay películas', this.catalogo
      ? 'Añádelas desde el ordenador: cópialas en las carpetas de MiCine y pulsa «Actualizar».'
      : 'Añádelas desde el ordenador: cópialas en la carpeta «peliculas» de MiCine o súbelas desde la web.');
    this.fila = -1;
    this.menuI = this.iActualizar;
  } else {
    this.fila = 0;
    // Tras ver una película se vuelve a ella (si sigue a medias, estará la primera de «Seguir viendo»).
    if (anterior) {
      if (this.catalogo) this.enfocarElemento(anterior, filaAnterior);
      else this.enfocarPeli(anterior.ruta);
    }
  }
  this.pintar();
};

PantallaInicio.prototype.clicEnTarjeta = function (tarjeta, fila, pos) {
  var yo = this;
  tarjeta.el.addEventListener('click', function () {
    yo.fila = fila;
    yo.filas[fila].pos = pos;
    yo.pintar();
    yo.tecla('ok');
  });
};

PantallaInicio.prototype.enfocarPeli = function (ruta) {
  for (var i = 0; i < this.filas.length; i++) {
    for (var k = 0; k < this.filas[i].pelis.length; k++) {
      if (this.filas[i].pelis[k].ruta === ruta) {
        this.fila = i;
        this.filas[i].pos = k;
        return;
      }
    }
  }
};

/** Busca la misma tarjeta: primero en la fila donde estaba, luego en cualquiera, y si no, la de su obra. */
PantallaInicio.prototype.enfocarElemento = function (anterior, tituloFila) {
  var claves = [anterior.clave];
  if (anterior.obra) claves.push('obra:' + anterior.obra.id, 'seguir:' + anterior.obra.id);
  for (var c = 0; c < claves.length; c++) {
    for (var pasada = 0; pasada < 2; pasada++) {
      for (var i = 0; i < this.filas.length; i++) {
        if (pasada === 0 && this.filas[i].titulo !== tituloFila) continue;
        for (var k = 0; k < this.filas[i].pelis.length; k++) {
          if (this.filas[i].pelis[k].clave === claves[c]) {
            this.fila = i;
            this.filas[i].pos = k;
            return;
          }
        }
      }
    }
  }
};

PantallaInicio.prototype.actual = function () {
  var f = this.filas[this.fila];
  return f ? f.pelis[f.pos] : null;
};

PantallaInicio.prototype.pintar = function () {
  for (var m = 0; m < this.menu.length; m++) {
    MC.clase(this.menu[m].el, 'foco', this.fila === -1 && m === this.menuI);
    if (this.menu[m].pestana) MC.clase(this.menu[m].el, 'activa', this.menu[m].seccion === this.seccion);
  }
  for (var i = 0; i < this.filas.length; i++) {
    var f = this.filas[i];
    for (var k = 0; k < f.tarjetas.length; k++) MC.clase(f.tarjetas[k].el, 'foco', !this.enAbc && i === this.fila && k === f.pos);
    MC.mover(f.carril, f.rejilla ? 0 : -f.pos * ANCHO_TARJETA, 0);
  }
  this.pintarAbc();
  // Las filas sin título (rejilla) dejan un margen arriba para que la tarjeta enfocada, que crece, se vea
  // entera (18 px: justo el hueco bajo los títulos de la fila de antes, que así no asoman).
  var arriba = this.filas[Math.max(0, this.fila)];
  MC.mover(this.pista, 0, arriba ? -(arriba.y - (arriba.titulo === null ? 18 : 0)) : 0);
  this.cargarCaratulas();
  var yo = this;
  clearTimeout(this.esperaHeroe);
  this.esperaHeroe = setTimeout(function () { yo.pintarHeroe(); }, 120);
};

/** El abecedario: las letras con títulos se ven más claras; la del título enfocado, en blanco. */
PantallaInicio.prototype.pintarAbc = function () {
  MC.clase(this.abc, 'visible', !!this.letras);
  MC.clase(this.abc, 'activo', this.enAbc);
  if (!this.letras) return;
  var f = this.filas[this.fila], el = this.actual();
  var actual = this.enAbc ? null : f && f.rejilla && el && el.obra ? letraDe(el.obra.titulo) : null;
  for (var k = 0; k < LETRAS.length; k++) {
    var l = this.abcLetras[k];
    MC.clase(l, 'hay', !!this.letras[LETRAS[k]]);
    MC.clase(l, 'actual', LETRAS[k] === actual);
    MC.clase(l, 'foco', this.enAbc && k === this.abcI);
  }
};

/** Del borde izquierdo de una fila al abecedario, en la letra del título enfocado (o la primera). */
PantallaInicio.prototype.entrarAbc = function () {
  var f = this.filas[this.fila], el = this.actual(), j = -1;
  if (f && f.rejilla && el && el.obra) j = LETRAS.indexOf(letraDe(el.obra.titulo));
  for (var k = 0; j < 0 && k < LETRAS.length; k++) if (this.letras[LETRAS[k]]) j = k;
  if (j < 0) return;
  this.abcVolver = { fila: this.fila, pos: f ? f.pos : 0 };
  this.enAbc = true;
  this.irLetra(j);
};

/** Mientras se recorre el abecedario, la rejilla ya va mostrando el primer título de cada letra. */
PantallaInicio.prototype.irLetra = function (j) {
  var destino = this.letras && this.letras[LETRAS[j]];
  if (!destino) return;
  this.abcI = j;
  this.fila = destino.fila;
  this.filas[destino.fila].pos = destino.pos;
};

PantallaInicio.prototype.teclaAbc = function (t) {
  if (t === 'arr' || t === 'aba') {
    var paso = t === 'arr' ? -1 : 1;
    for (var j = this.abcI + paso; j >= 0 && j < LETRAS.length; j += paso) {
      if (this.letras[LETRAS[j]]) { this.irLetra(j); break; }
    }
  } else if (t === 'der' || t === 'ok') {
    this.enAbc = false;                // al título de esa letra
  } else if (t === 'atras') {
    this.enAbc = false;                // vuelve a donde estaba
    var v = this.abcVolver;
    if (v && this.filas[v.fila]) {
      this.fila = v.fila;
      this.filas[v.fila].pos = Math.min(v.pos, this.filas[v.fila].pelis.length - 1);
    }
  } else return;
  this.pintar();
};

PantallaInicio.prototype.cargarCaratulas = function () {
  var desde = Math.max(0, this.fila - 1), hasta = Math.min(this.filas.length - 1, Math.max(0, this.fila) + 2);
  for (var i = desde; i <= hasta; i++) {
    var f = this.filas[i];
    for (var k = Math.max(0, f.pos - 2); k < Math.min(f.tarjetas.length, f.pos + 10); k++) f.tarjetas[k].cargar();
  }
};

PantallaInicio.prototype.pintarHeroe = function () {
  var p = this.actual();
  if (!p) return;
  if (this.catalogo) return this.pintarHeroeObra(p);
  this.heroeTitulo.textContent = p.titulo;
  var meta = [];
  if (p.carpeta) meta.push(p.carpeta.replace(/\//g, ' › '));
  meta.push(MC.extension(p.ruta));
  meta.push(MC.tamano(p.tamano));
  this.heroeMeta.textContent = meta.join('  ·  ');
  var subs = [];
  for (var i = 0; i < p.subtitulos.length; i++) subs.push(p.subtitulos[i].etiqueta);
  this.heroeInfo.textContent = subs.length ? 'Subtítulos: ' + subs.join(', ') : '';
  MC.vaciar(this.heroeBarra);
  if (enCurso(p)) {
    MC.anadir(this.heroeBarra, barraProgreso(p));
  } else if (vista(p)) {
    this.heroeBarra.appendChild(MC.h('span', { class: 'vista' }, MC.icono('marca'), 'Vista'));
  }
  this.ponerArte(p.poster);
};

PantallaInicio.prototype.ponerArte = function (poster) {
  var arte = this.arte, fondo = this.fondo;
  MC.clase(arte, 'lista', false);
  MC.clase(fondo, 'lista', false);
  if (poster) {
    arte.onload = function () { MC.clase(arte, 'lista', true); };
    fondo.onload = function () { MC.clase(fondo, 'lista', true); };
    arte.src = fondo.src = MC.api.url(poster);
  }
};

/** Cabecera con secciones: título, año, sinopsis y cómo va (a medias, siguiente episodio…). */
PantallaInicio.prototype.pintarHeroeObra = function (el) {
  MC.vaciar(this.heroeBarra);
  MC.clase(this.heroeInfo, 'sinopsis', true);
  if (el.todo || el.genero) {
    this.heroeTitulo.textContent = el.todo ? el.todo.titulo : el.genero;
    this.heroeMeta.textContent = el.n + ' títulos';
    this.heroeInfo.textContent = 'Pulsa OK para verlos todos.';
    return this.ponerArte(null);
  }
  var o = el.obra, meta = [], info = o.sinopsis || '';
  if (!o.identificada && o.videos[0].carpeta) meta.push(o.videos[0].carpeta.replace(/\//g, ' › '));
  if (o.anio) meta.push(o.anio);
  if (o.serie) {
    meta.push(MC.textoTemporadas(o));
    var obj = el.objetivo || MC.objetivoSerie(o), et = MC.etiquetaEpisodio(obj.ep) + (obj.ep.nombre ? ' · ' + obj.ep.nombre : '');
    if (obj.modo === 'seguir') MC.anadir(this.heroeBarra, barraProgreso(obj.video, et));
    else if (obj.modo === 'siguiente') this.heroeBarra.appendChild(MC.h('span', null, 'Siguiente: ' + et));
    else if (obj.modo === 'repetir') this.heroeBarra.appendChild(MC.h('span', { class: 'vista' }, MC.icono('marca'), 'Vista entera'));
  } else {
    var v = el.video || versionPorDefecto(o.versiones), e = MC.estado(o.versiones);
    meta.push(MC.nombreVersion(v));
    if (o.versiones.length > 1) meta.push(o.versiones.length + ' versiones');
    if (!info) {
      var subs = [];
      for (var i = 0; i < v.subtitulos.length; i++) subs.push(v.subtitulos[i].etiqueta);
      info = subs.length ? 'Subtítulos: ' + subs.join(', ') : '';
    }
    if (e.enCurso) MC.anadir(this.heroeBarra, barraProgreso(e.video));
    else if (e.vista) this.heroeBarra.appendChild(MC.h('span', { class: 'vista' }, MC.icono('marca'), 'Vista'));
  }
  this.heroeTitulo.textContent = o.titulo;
  this.heroeMeta.textContent = meta.join('  ·  ');
  this.heroeInfo.textContent = info;
  this.ponerArte(o.poster);
};

PantallaInicio.prototype.tecla = function (t) {
  var f = this.filas[this.fila], nuevo = !!this.catalogo;
  if (this.enAbc) return this.teclaAbc(t);
  if (this.fila === -1) {
    if (t === 'izq' && this.menuI > 0) this.menuI--;
    else if (t === 'der' && this.menuI < this.menu.length - 1) this.menuI++;
    else if ((t === 'aba' || t === 'atras') && this.filas.length) this.fila = 0;
    else if (t === 'ok') return this.menu[this.menuI].accion();
    else if (t === 'atras') return MC.preguntarSalir();
    else return;
  } else if (t === 'izq' && f.pos > 0) f.pos--;
  else if (t === 'izq' && this.letras) this.entrarAbc();
  else if (t === 'der' && f.pos < f.pelis.length - 1) f.pos++;
  else if (t === 'arr') {
    // Al subir al menú, el foco va a la pestaña que se está viendo.
    if (this.fila === 0 && nuevo) this.menuI = this.indicePestana();
    this.cambiarFila(-1);
  } else if (t === 'aba' && this.fila < this.filas.length - 1) this.cambiarFila(1);
  else if (t === 'ok') return nuevo ? this.abrir(this.actual()) : MC.mostrar(new PantallaDetalle(this.actual(), this));
  else if (t === 'play' || t === 'playpausa') {
    if (!nuevo) return MC.reproducir(this.actual(), enCurso(this.actual()), this);
    if (!this.actual().todo && !this.actual().genero) return MC.reproducirElemento(this.actual(), this);
    return;
  } else if (t === 'atras') {
    // Viendo un género entero, VOLVER lleva a la pestaña, a la fila de ese género.
    if (nuevo && this.genero !== null) return this.salirGenero();
    if (this.fila === 0 && f.pos === 0) {
      // En una pestaña, VOLVER lleva a Inicio; en Inicio, pregunta si se sale.
      if (nuevo && this.seccion !== null) return this.irSeccion(null);
      return MC.preguntarSalir();
    }
    this.fila = 0;
    this.filas[0].pos = 0;
  } else return;
  this.pintar();
};

/** Entre filas de la rejilla se mantiene la columna. */
PantallaInicio.prototype.cambiarFila = function (paso) {
  var antes = this.filas[this.fila];
  this.fila += paso;
  var ahora = this.filas[this.fila];
  if (antes && ahora && antes.rejilla && ahora.rejilla) ahora.pos = Math.min(antes.pos, ahora.pelis.length - 1);
};

PantallaInicio.prototype.abrir = function (el) {
  if (!el) return;
  if (el.todo) return this.irSeccion(el.todo.id);
  if (el.genero) return this.irGenero(el.genero);
  MC.abrirElemento(el, this);
};

/** Cambia de pestaña (null = Inicio) y lleva el foco a la primera fila. */
PantallaInicio.prototype.irSeccion = function (id) {
  this.seccion = id;
  this.genero = null;
  this.construir(true);
  if (this.filas.length) this.fila = 0;
  this.pintar();
};

/** «Ver todo» de un género: su rejilla de la A a la Z (con abecedario). */
PantallaInicio.prototype.irGenero = function (genero) {
  this.genero = genero;
  this.construir(true);
  if (this.filas.length) this.fila = 0;
  this.pintar();
};

/** De vuelta a la pestaña, en la tarjeta «Ver todo» de la fila del género. */
PantallaInicio.prototype.salirGenero = function () {
  var genero = this.genero;
  this.genero = null;
  this.construir(true);
  for (var i = 0; i < this.filas.length; i++) {
    var f = this.filas[i];
    if (!f.rejilla && f.titulo === genero) {
      this.fila = i;
      f.pos = f.pelis.length - 1;
      break;
    }
  }
  this.pintar();
};

PantallaInicio.prototype.buscar = function () {
  if (!this.catalogo) return MC.mostrar(new PantallaBuscar(this.pelis, this));
  MC.mostrar(new PantallaBuscar(this.catalogo.obras.slice().sort(porTitulo).map(MC.elementoObra), this, true));
};

// ── Ficha de una película ──────────────────────────────────────────────

/** obra (opcional, con secciones): da el título, el año, la sinopsis y las otras versiones. */
function PantallaDetalle(peli, volver, obra) {
  var yo = this;
  this.peli = peli;
  this.volver = volver;
  this.obra = obra || null;
  var items = [];
  if (enCurso(peli)) {
    items.push({ el: MC.h('div', { class: 'boton' }, MC.icono('play'), 'Reanudar desde ' + MC.tiempo(peli.progreso.t)),
      accion: function () { MC.reproducir(peli, true, yo); } });
    items.push({ el: MC.h('div', { class: 'boton secundario' }, MC.icono('reinicio'), 'Ver desde el principio'),
      accion: function () { MC.reproducir(peli, false, yo); } });
  } else {
    items.push({ el: MC.h('div', { class: 'boton' }, MC.icono('play'), vista(peli) ? 'Volver a verla' : 'Reproducir'),
      accion: function () { MC.reproducir(peli, false, yo); } });
  }
  if (obra && obra.versiones && obra.versiones.length > 1) {
    items.push({ el: MC.h('div', { class: 'boton secundario' }, MC.icono('capas'), 'Otras versiones (' + obra.versiones.length + ')'),
      accion: function () { yo.elegirVersion(); } });
  }
  items.push({ el: MC.h('div', { class: 'boton secundario' }, 'Volver'), accion: function () { MC.mostrar(volver); } });
  this.lista = new Vertical(items);

  var subs = [];
  for (var i = 0; i < peli.subtitulos.length; i++) subs.push(peli.subtitulos[i].etiqueta);
  var botones = MC.h('div', { class: 'botones' });
  for (i = 0; i < items.length; i++) botones.appendChild(items[i].el);
  var poster = obra ? obra.poster || peli.poster : peli.poster;
  var cartel = MC.h('img', { class: 'detalle-arte', alt: '' });
  var fondo = MC.h('img', { class: 'fondo-difuso', alt: '' });
  if (poster) {
    cartel.onload = function () { MC.clase(cartel, 'lista', true); };
    fondo.onload = function () { MC.clase(fondo, 'lista', true); };
    cartel.src = fondo.src = MC.api.url(poster);
  }
  var meta = obra
    ? [!obra.identificada && peli.carpeta ? peli.carpeta.replace(/\//g, ' › ') : '', obra.anio, MC.nombreVersion(peli)]
    : [peli.carpeta.replace(/\//g, ' › '), MC.extension(peli.ruta), MC.tamano(peli.tamano)];
  this.el = MC.h('div', { class: 'pantalla detalle' + (obra ? ' con-obra' : '') },
    fondo,
    MC.h('div', { class: 'velo' }),
    cartel,
    MC.h('div', { class: 'detalle-texto' },
      MC.h('h1', null, obra ? obra.titulo : peli.titulo),
      MC.h('div', { class: 'meta' }, meta.filter(function (x) { return x; }).join('  ·  ')),
      obra && obra.sinopsis ? MC.h('div', { class: 'info sinopsis' }, obra.sinopsis) : null,
      MC.h('div', { class: 'info' }, subs.length ? 'Subtítulos: ' + subs.join(', ') :
        'Sin subtítulos aparte (los que lleve el vídeo dentro se eligen al verla).'),
      enCurso(peli) ? MC.h('div', { class: 'progreso' }, barraProgreso(peli)) : null,
      botones));
}

PantallaDetalle.prototype.entrarPantalla = function () { this.lista.pintar(); };

PantallaDetalle.prototype.tecla = function (t) {
  if (this.lista.tecla(t)) return;
  if (t === 'atras') MC.mostrar(this.volver);
  else if (t === 'play' || t === 'playpausa') MC.reproducir(this.peli, enCurso(this.peli), this);
};

/** Al volver del reproductor, la ficha se rehace con el progreso nuevo. */
PantallaDetalle.prototype.rehacer = function () {
  return new PantallaDetalle(this.peli, this.volver, this.obra);
};

/** «Otras versiones»: la ficha pasa a la versión elegida (con su propio progreso). */
PantallaDetalle.prototype.elegirVersion = function () {
  var yo = this, versiones = this.obra.versiones, opciones = [], actual = 0;
  for (var i = 0; i < versiones.length; i++) {
    var v = versiones[i];
    if (v === this.peli) actual = i;
    opciones.push(MC.nombreVersion(v) + (enCurso(v) ? '  ·  a medias' : vista(v) ? '  ·  vista' : ''));
  }
  MC.elegir('¿Qué versión quieres ver?', opciones, actual, function (n) {
    MC.mostrar(new PantallaDetalle(versiones[n], yo.volver, yo.obra));
  });
};

// ── Buscar ─────────────────────────────────────────────────────────────

var COLUMNAS = 7;
var ALTO_REJILLA_BUSCAR = 412;   // 20 de margen + 300 de carátula + 76 de título + 16

/** catalogo: true si «pelis» son tarjetas de obras (servidor con secciones). */
function PantallaBuscar(pelis, volver, catalogo) {
  var yo = this;
  this.pelis = pelis;
  this.volver = volver;
  this.catalogo = !!catalogo;
  this.i = -1;          // -1 = el campo de texto
  this.resultados = [];
  this.texto = MC.h('input', { type: 'text', placeholder: catalogo ? 'Título de la película o serie' : 'Título de la película',
    autocomplete: 'off', spellcheck: 'false' });
  this.texto.addEventListener('input', function () { yo.filtrar(); });
  this.campo = MC.h('div', { class: 'campo buscador' }, MC.icono('lupa'), this.texto);
  this.campo.addEventListener('click', function () { yo.i = -1; yo.pintar(); yo.texto.focus(); });
  this.rejilla = MC.h('div', { class: 'rejilla' });
  this.vacio = MC.h('div', { class: 'vacio' });
  this.el = MC.h('div', { class: 'pantalla buscar' },
    MC.h('div', { class: 'menu' }, logo()),
    this.campo,
    MC.h('div', { class: 'rejilla-marco' }, this.rejilla),
    this.vacio);
  this.filtrar();
}

PantallaBuscar.prototype.filtrar = function () {
  var q = MC.sinAcentos(this.texto.value).replace(/^\s+|\s+$/g, '');
  var yo = this;
  this.resultados = [];
  MC.vaciar(this.rejilla);
  for (var i = 0; i < this.pelis.length; i++) {
    var p = this.pelis[i];
    var titulo = this.catalogo ? p.obra.titulo + (p.obra.anio ? ' ' + p.obra.anio : '') : p.titulo;
    if (q && MC.sinAcentos(titulo).indexOf(q) < 0) continue;
    var t = new Tarjeta(p, p.d);
    (function (indice, tarjeta) {
      tarjeta.el.addEventListener('click', function () { yo.i = indice; yo.pintar(); yo.tecla('ok'); });
    })(this.resultados.length, t);
    this.resultados.push(t);
    this.rejilla.appendChild(t.el);
  }
  this.vacio.textContent = this.resultados.length ? '' :
    this.catalogo ? 'No hay ninguna película ni serie con ese título.' : 'No hay ninguna película con ese título.';
  if (this.i >= this.resultados.length) this.i = this.resultados.length - 1;
  this.pintar();
};

PantallaBuscar.prototype.pintar = function () {
  MC.clase(this.campo, 'foco', this.i === -1);
  var filaFoco = Math.max(0, Math.floor(this.i / COLUMNAS));
  for (var k = 0; k < this.resultados.length; k++) {
    MC.clase(this.resultados[k].el, 'foco', k === this.i);
    if (Math.abs(Math.floor(k / COLUMNAS) - filaFoco) <= 2) this.resultados[k].cargar();
  }
  MC.mover(this.rejilla, 0, -Math.max(0, filaFoco - 1) * ALTO_REJILLA_BUSCAR);
};

PantallaBuscar.prototype.entrarPantalla = function () {
  this.pintar();
  if (this.i === -1) this.texto.focus();
};

PantallaBuscar.prototype.tecla = function (t) {
  var n = this.resultados.length;
  if (this.i === -1) {
    if (t === 'ok') return this.texto.focus();
    if (t === 'aba' && n) this.i = 0;
    else if (t === 'atras') return MC.mostrar(this.volver);
    else return;
  } else if (t === 'izq' && this.i % COLUMNAS > 0) this.i--;
  else if (t === 'der' && this.i % COLUMNAS < COLUMNAS - 1 && this.i < n - 1) this.i++;
  else if (t === 'arr') this.i = this.i < COLUMNAS ? -1 : this.i - COLUMNAS;
  else if (t === 'aba' && Math.floor(this.i / COLUMNAS) < Math.floor((n - 1) / COLUMNAS)) this.i = Math.min(n - 1, this.i + COLUMNAS);
  else if (t === 'ok') {
    if (this.catalogo) return MC.abrirElemento(this.resultados[this.i].peli, this);
    return MC.mostrar(new PantallaDetalle(this.resultados[this.i].peli, this));
  } else if (t === 'atras') this.i = -1;
  else return;
  this.pintar();
};

PantallaBuscar.prototype.teclaCampo = function (t) {
  this.texto.blur();
  if ((t === 'ok' || t === 'hecho' || t === 'aba') && this.resultados.length) this.i = 0;
  this.pintar();
};
