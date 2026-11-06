using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MiCine
{
    /// <summary>
    /// Una petición: la clase Manejador de servidor.py, método a método. Las
    /// rutas, los códigos de estado, los mensajes y las cabeceras son los
    /// mismos para que la web y la app de Android no noten la diferencia.
    /// </summary>
    public sealed class Manejador
    {
        const string COOKIE = "micine_sesion";
        const int TROZO = 1024 * 1024;
        const string CSP = "default-src 'self'; img-src 'self' data: https://image.tmdb.org; media-src 'self'; style-src 'self'; " +
            "script-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

        static readonly Dictionary<string, string> Estaticos = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".js", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".svg", "image/svg+xml" },
        };

        delegate void Accion(string usuario);
        sealed class Ruta { public Accion Metodo; public bool SoloAdmin; }

        readonly Conexion con;
        readonly Servidor servidor;
        readonly string metodo, rutaPeticion;
        readonly List<KeyValuePair<string, string>> cabeceras;
        Dictionary<string, List<string>> consulta = new Dictionary<string, List<string>>();
        public bool CuerpoLeido;

        public Manejador(Conexion con, string metodo, string ruta, List<KeyValuePair<string, string>> cabeceras)
        {
            this.con = con;
            servidor = con.Servidor;
            this.metodo = metodo;
            rutaPeticion = ruta;
            this.cabeceras = cabeceras;
        }

        Almacen Almacen { get { return servidor.Almacen; } }
        Biblioteca Biblioteca { get { return servidor.Biblioteca; } }

        // (método HTTP, ruta) -> (método del manejador, solo administradores)
        Ruta Buscar(string m, string ruta)
        {
            switch (m + " " + ruta)
            {
                case "GET /api/peliculas": return new Ruta { Metodo = ApiPeliculas, SoloAdmin = false };
                case "GET /api/info": return new Ruta { Metodo = ApiInfo, SoloAdmin = false };
                case "POST /api/logout": return new Ruta { Metodo = ApiLogout, SoloAdmin = false };
                case "POST /api/progreso": return new Ruta { Metodo = ApiProgreso, SoloAdmin = false };
                case "POST /api/cuenta/contrasena": return new Ruta { Metodo = ApiMiContrasena, SoloAdmin = false };
                case "POST /api/peliculas/borrar": return new Ruta { Metodo = ApiBorrarPelicula, SoloAdmin = true };
                case "GET /api/identificar/buscar": return new Ruta { Metodo = ApiIdentificarBuscar, SoloAdmin = true };
                case "POST /api/identificar": return new Ruta { Metodo = ApiIdentificar, SoloAdmin = true };
                case "POST /api/identificar/actualizar": return new Ruta { Metodo = ApiIdentificarActualizar, SoloAdmin = true };
                case "POST /api/caratulas/actualizar": return new Ruta { Metodo = ApiCaratulasActualizar, SoloAdmin = true };
                case "GET /api/usuarios": return new Ruta { Metodo = ApiUsuarios, SoloAdmin = true };
                case "POST /api/usuarios/crear": return new Ruta { Metodo = ApiCrearUsuario, SoloAdmin = true };
                case "POST /api/usuarios/borrar": return new Ruta { Metodo = ApiBorrarUsuario, SoloAdmin = true };
                case "POST /api/usuarios/contrasena": return new Ruta { Metodo = ApiContrasenaUsuario, SoloAdmin = true };
            }
            return null;
        }

        // Lo que puede pedir una app que no se sirve desde MiCine (la de la tele): solo funciona con
        // token, porque la galleta es SameSite=Strict y nunca se envía desde otro origen.
        static bool EsRutaDeApps(string ruta)
        {
            switch (ruta)
            {
                case "/api/estado": case "/api/login": case "/api/logout": case "/api/peliculas": case "/api/info": case "/api/progreso":
                    return true;
            }
            return ruta.StartsWith("/media/", StringComparison.Ordinal) || ruta.StartsWith("/caratula/", StringComparison.Ordinal);
        }

        bool cors;

        // ── infraestructura ──────────────────────────────────────────────

        public void Atender()
        {
            CuerpoLeido = metodo != "POST";
            try
            {
                string ruta, q;
                PartirUrl(rutaPeticion, out ruta, out q);
                consulta = Py.ParseQs(q);
                cors = EsRutaDeApps(ruta);

                if (metodo == "OPTIONS")
                {
                    // Consulta previa del navegador antes de que la app de la tele llame a la API.
                    if (!cors) { EnviarBytes(new byte[0], "text/plain", 403, null); return; }
                    EnviarBytes(new byte[0], "text/plain", 204, Lista(
                        "Access-Control-Allow-Methods", "GET, POST",
                        "Access-Control-Allow-Headers", "Authorization, Content-Type, X-MiCine, Range",
                        "Access-Control-Max-Age", "600"));
                    return;
                }
                if (metodo == "GET")
                {
                    if (ruta == "/" || ruta == "/index.html") { EnviarEstatico("index.html"); return; }
                    if (ruta.StartsWith("/static/", StringComparison.Ordinal)) { EnviarEstatico(ruta.Substring("/static/".Length)); return; }
                    if (ruta == "/api/estado") { ApiEstado(); return; }
                    string descarga;
                    if (Apps.Descargas.TryGetValue(ruta, out descarga))
                    {
                        EnviarBytes(new byte[0], "text/plain", 302, Lista("Location", descarga, "Cache-Control", "no-cache"));
                        return;
                    }
                    if (ruta.StartsWith("/media/", StringComparison.Ordinal))
                    {
                        string relativa = Py.Desescapar(ruta.Substring("/media/".Length));
                        if (UsuarioActual() == null && Almacen.UsuarioDeEnlace(relativa, consulta) == null)
                            throw new ErrorPeticion("Inicia sesión para continuar", 401);
                        ServirMedia(relativa);
                        return;
                    }
                    if (ruta.StartsWith("/caratula/", StringComparison.Ordinal))
                    {
                        ServirCaratula(ruta.Substring("/caratula/".Length));
                        return;
                    }
                }
                else
                {
                    // Cabecera propia obligatoria: otra web no puede añadirla sin permiso (protección CSRF).
                    if (Cabecera("X-MiCine") != "1") throw new ErrorPeticion("Petición rechazada", 403);
                    if (ruta == "/api/login") { ApiLogin(); return; }
                    if (ruta == "/api/configurar") { ApiConfigurar(); return; }
                }

                string usuario = UsuarioActual();
                if (usuario == null) throw new ErrorPeticion("Inicia sesión para continuar", 401);
                Ruta accion = Buscar(metodo, ruta);
                if (accion == null) throw new ErrorPeticion("No encontrado", 404);
                if (accion.SoloAdmin && Almacen.Rol(usuario) != "admin")
                    throw new ErrorPeticion("Solo un administrador puede hacer esto", 403);
                accion.Metodo(usuario);
            }
            catch (ErrorPeticion error)
            {
                if (!CuerpoLeido) con.Cerrar = true; // no se leyó el cuerpo: no se puede reutilizar la conexión
                try { EnviarJson(new Objeto { { "error", error.Message } }, error.Codigo, null); }
                catch (Desconectado) { con.Cerrar = true; }
            }
            catch (Desconectado)
            {
                con.Cerrar = true;
            }
            catch (Exception e)
            {
                Registro.Error(metodo + " " + rutaPeticion, e);
                con.Cerrar = true;
                if (!con.CabecerasEnviadas)
                {
                    try { EnviarJson(new Objeto { { "error", "Error interno del servidor" } }, 500, null); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>urllib.parse.urlsplit(): ruta y consulta.</summary>
        static void PartirUrl(string url, out string ruta, out string consulta)
        {
            int k = 0;
            while (k < url.Length && url[k] <= ' ') k++;
            url = url.Substring(k).Replace("\t", "").Replace("\r", "").Replace("\n", "");
            int dp = url.IndexOf(':');
            if (dp > 0 && url[0] < 128 && char.IsLetter(url[0]))
            {
                bool esquema = true;
                for (int i = 0; i < dp; i++)
                {
                    char c = url[i];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.')) { esquema = false; break; }
                }
                if (esquema) url = url.Substring(dp + 1);
            }
            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                int fin = url.Length;
                foreach (char c in "/?#") { int p = url.IndexOf(c, 2); if (p >= 0 && p < fin) fin = p; }
                url = url.Substring(fin);
            }
            int h = url.IndexOf('#');
            if (h >= 0) url = url.Substring(0, h);
            int q = url.IndexOf('?');
            if (q >= 0) { consulta = url.Substring(q + 1); ruta = url.Substring(0, q); }
            else { consulta = ""; ruta = url; }
        }

        string Cabecera(string nombre) { return Conexion.Cabecera(cabeceras, nombre); }

        // Las añaden los túneles y proxys que corren en este mismo ordenador (Tailscale Funnel, Cloudflare
        // Tunnel…): con ellas, la petición viene de fuera aunque llegue desde 127.0.0.1.
        static readonly string[] CabecerasProxy = { "CF-Connecting-IP", "X-Forwarded-For", "X-Forwarded-Proto",
            "X-Forwarded-Host", "X-Real-IP", "Forwarded", "Tailscale-Funnel-Request" };

        public static bool PorTunel(Conexion con, List<KeyValuePair<string, string>> cabeceras)
        {
            if (!System.Net.IPAddress.IsLoopback(con.IpRemota)) return false;
            foreach (string c in CabecerasProxy) if (!string.IsNullOrEmpty(Conexion.Cabecera(cabeceras, c))) return true;
            return false;
        }

        bool PorTunel() { return PorTunel(con, cabeceras); }

        bool EsLocal() { return System.Net.IPAddress.IsLoopback(con.IpRemota) && !PorTunel(); }

        /// <summary>La IP de quien pide; si llega por un túnel, la que anota el túnel.</summary>
        string IpCliente()
        {
            if (!PorTunel()) return con.Ip;
            string ip = (Cabecera("CF-Connecting-IP") ?? "").Trim();
            if (ip == "")
            {
                // El último de la lista lo añade el propio túnel; los anteriores los puede inventar el cliente.
                string[] lista = (Cabecera("X-Forwarded-For") ?? "").Split(',');
                ip = lista[lista.Length - 1].Trim();
            }
            if (ip == "") ip = (Cabecera("X-Real-IP") ?? "").Trim();
            if (ip.Length > 64) ip = ip.Substring(0, 64);
            return ip == "" ? "túnel" : ip;
        }

        string Token()
        {
            string autorizacion = Cabecera("Authorization") ?? "";
            if (autorizacion.StartsWith("Bearer ", StringComparison.Ordinal)) return Py.Strip(autorizacion.Substring("Bearer ".Length));
            return Galletas.ValorDe(Cabecera("Cookie") ?? "", COOKIE);
        }

        string UsuarioActual() { return Almacen.UsuarioDe(Token()); }

        Objeto LeerCuerpo()
        {
            long longitud;
            if (!Py.Entero(Cabecera("Content-Length") ?? "0", out longitud)) longitud = -1;
            if (longitud < 0 || longitud > 64 * 1024) throw new ErrorPeticion("Petición no válida");
            byte[] crudo = con.LeerExacto((int)longitud);
            CuerpoLeido = true;
            object datos;
            try { datos = crudo.Length == 0 ? new Objeto() : Json.LeerBytes(crudo); }
            catch (ErrorJson) { throw new ErrorPeticion("Petición no válida"); }
            var objeto = datos as Objeto;
            if (objeto == null) throw new ErrorPeticion("Petición no válida");
            return objeto;
        }

        static List<KeyValuePair<string, string>> Lista(params string[] pares)
        {
            var l = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pares.Length; i += 2) l.Add(new KeyValuePair<string, string>(pares[i], pares[i + 1]));
            return l;
        }

        /// <summary>enviar_cabeceras(): las de seguridad de siempre, y luego las propias (que pueden sustituirlas).</summary>
        List<KeyValuePair<string, string>> Todas(string tipo, long longitud, List<KeyValuePair<string, string>> extra)
        {
            var todas = Lista(
                "Content-Type", tipo,
                "Content-Length", longitud.ToString(Py.Inv),
                "Cache-Control", "no-store",
                "X-Content-Type-Options", "nosniff",
                "X-Frame-Options", "DENY",
                "Referrer-Policy", "same-origin");
            if (cors) todas.Add(new KeyValuePair<string, string>("Access-Control-Allow-Origin", "*"));
            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    int i = todas.FindIndex(x => x.Key == kv.Key);
                    if (i >= 0) todas[i] = kv; else todas.Add(kv);
                }
            }
            return todas;
        }

        void EnviarBytes(byte[] cuerpo, string tipo, int codigo, List<KeyValuePair<string, string>> extra)
        {
            con.Enviar(codigo, Todas(tipo, cuerpo.Length, extra), cuerpo);
        }

        void EnviarJson(object datos, int codigo, List<KeyValuePair<string, string>> extra)
        {
            EnviarBytes(Py.Utf8.GetBytes(Json.Escribir(datos)), "application/json; charset=utf-8", codigo, extra);
        }

        void EnviarJson(object datos) { EnviarJson(datos, 200, null); }

        void EnviarEstatico(string nombre)
        {
            string real;
            byte[] contenido;
            string sufijo = null;
            if (Web.Buscar(servidor.Base, Py.Desescapar(nombre), out real, out contenido))
                sufijo = Py.Sufijo(real.Substring(real.LastIndexOf('/') + 1));
            if (sufijo == null || !Estaticos.ContainsKey(sufijo)) throw new ErrorPeticion("No encontrado", 404);
            var extra = Lista("Cache-Control", "no-cache");
            if (sufijo == ".html") extra.Add(new KeyValuePair<string, string>("Content-Security-Policy", CSP));
            EnviarBytes(contenido, Estaticos[sufijo], 200, extra);
        }

        static readonly Regex Rango = new Regex(@"^bytes=(\d*)-(\d*)\z");

        /// <summary>Envía un archivo respetando 'Range': así se puede saltar a cualquier minuto del vídeo.</summary>
        void EnviarArchivo(string ruta, string tipo, bool descarga)
        {
            // Se abre sin permitir borrarlo: igual que en Python, mover a la papelera
            // una película que alguien está viendo da «archivo en uso».
            using (var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
            {
                long tamano = archivo.Length;
                long inicio = 0, fin = tamano - 1;
                int codigo = 200;
                string rango = Cabecera("Range");
                if (!string.IsNullOrEmpty(rango))
                {
                    Match m = Rango.Match(Py.Strip(rango));
                    if (!m.Success || (m.Groups[1].Value == "" && m.Groups[2].Value == ""))
                        throw new ErrorPeticion("Rango no válido", 416);
                    long a, b;
                    if (m.Groups[1].Value == "")
                    {
                        Py.Entero(m.Groups[2].Value, out b);
                        inicio = Math.Max(0, b >= tamano ? 0 : tamano - b);
                    }
                    else
                    {
                        Py.Entero(m.Groups[1].Value, out a);
                        inicio = a;
                        if (m.Groups[2].Value != "")
                        {
                            Py.Entero(m.Groups[2].Value, out b);
                            fin = Math.Min(b, tamano - 1);
                        }
                    }
                    if (inicio >= tamano || inicio > fin)
                    {
                        con.Enviar(416, Todas("text/plain", 0, Lista("Content-Range", "bytes */" + tamano.ToString(Py.Inv))), null);
                        return;
                    }
                    codigo = 206;
                }
                var extra = Lista("Accept-Ranges", "bytes", "Cache-Control", "private, max-age=86400");
                if (codigo == 206)
                    extra.Add(new KeyValuePair<string, string>("Content-Range", "bytes " + inicio.ToString(Py.Inv) + "-" + fin.ToString(Py.Inv) + "/" + tamano.ToString(Py.Inv)));
                if (descarga)
                {
                    tipo = "application/octet-stream";
                    extra.Add(new KeyValuePair<string, string>("Content-Disposition", "attachment; filename*=UTF-8''" + Py.Citar(Path.GetFileName(ruta))));
                }
                con.Enviar(codigo, Todas(tipo, fin - inicio + 1, extra), null);
                archivo.Seek(inicio, SeekOrigin.Begin);
                long restante = fin - inicio + 1;
                var trozo = new byte[(int)Math.Min(TROZO, Math.Max(1, restante))];
                while (restante > 0)
                {
                    int n = archivo.Read(trozo, 0, (int)Math.Min(trozo.Length, restante));
                    if (n <= 0) break;
                    con.Escribir(trozo, 0, n);
                    restante -= n;
                }
            }
        }

        string Primero(string nombre)
        {
            List<string> valores;
            return consulta.TryGetValue(nombre, out valores) && valores.Count > 0 ? valores[0] : "";
        }

        double Numero(string nombre, double maximo)
        {
            string texto = Primero(nombre);
            double valor = 0;
            if (texto != "" && (!double.TryParse(Py.Strip(texto), NumberStyles.Float, Py.Inv, out valor) || double.IsNaN(valor)))
                throw new ErrorPeticion("Petición no válida");
            if (valor < 0 || valor > maximo) throw new ErrorPeticion("Petición no válida");
            return valor;
        }

        /// <summary>Vídeo convertido al vuelo (o subtítulos internos pasados a texto) para quien no puede con el original.</summary>
        void ServirConversion(string ruta)
        {
            var conversor = servidor.Conversor;
            if (conversor == null) throw new ErrorPeticion("Para convertir vídeos, MiCine necesita FFmpeg (ver LEEME.md)", 501);
            if (consulta.ContainsKey("subtitulo"))
            {
                byte[] vtt = conversor.Subtitulo(ruta, (int)Numero("subtitulo", 99));
                EnviarBytes(vtt, "text/vtt; charset=utf-8", 200, Lista("Cache-Control", "private, max-age=3600"));
                return;
            }
            bool paraWeb = Primero("formato") == "web";
            var proceso = conversor.Convertir(ruta, Numero("t", 1e6), (int)Numero("audio", 99), Primero("video") == "copia", paraWeb);
            try
            {
                // No se sabe cuánto ocupará: la respuesta termina cuando se cierra la conexión.
                con.Cerrar = true;
                var cabeceras = Lista("Content-Type", paraWeb ? "video/mp4" : "video/mp2t", "Cache-Control", "no-store",
                    "X-Content-Type-Options", "nosniff", "Connection", "close");
                if (cors) cabeceras.Add(new KeyValuePair<string, string>("Access-Control-Allow-Origin", "*"));
                con.Enviar(200, cabeceras, null);
                Stream salida = proceso.StandardOutput.BaseStream;
                var trozo = new byte[256 * 1024];
                int n;
                while ((n = salida.Read(trozo, 0, trozo.Length)) > 0) con.Escribir(trozo, 0, n);
            }
            catch (IOException) { } // FFmpeg se ha cerrado
            finally
            {
                // El reproductor ha cerrado (ha saltado a otro minuto, cambiado de pista o salido) o ha terminado.
                conversor.Parar(proceso);
            }
        }

        void ServirMedia(string relativa)
        {
            string ruta = Biblioteca.Resolver(relativa);
            string ext = ruta != null ? Py.Lower(Py.Sufijo(Path.GetFileName(ruta))) : "";
            string tipo;
            if (Tipos.Videos.ContainsKey(ext) && (consulta.ContainsKey("convertir") || consulta.ContainsKey("subtitulo")))
            {
                ServirConversion(ruta);
                return;
            }
            if (Tipos.Videos.TryGetValue(ext, out tipo)) { EnviarArchivo(ruta, tipo, consulta.ContainsKey("descargar")); return; }
            if (Tipos.Imagenes.TryGetValue(ext, out tipo)) { EnviarArchivo(ruta, tipo, false); return; }
            if (Tipos.Subtitulos.Contains(ext))
            {
                if (consulta.ContainsKey("original")) // VLC entiende .srt directamente
                {
                    EnviarArchivo(ruta, ext == ".srt" ? "application/x-subrip" : "text/vtt", false);
                    return;
                }
                EnviarBytes(Biblioteca.SubtituloVtt(ruta), "text/vtt; charset=utf-8", 200, Lista("Cache-Control", "private, max-age=3600"));
                return;
            }
            throw new ErrorPeticion("No encontrado", 404);
        }

        List<KeyValuePair<string, string>> Galleta(string token, string segundos)
        {
            return Lista("Set-Cookie", COOKIE + "=" + token + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=" + segundos);
        }

        void ResponderSesion(string nombre, bool paraApp)
        {
            string token = Almacen.CrearSesion(nombre);
            var datos = new Objeto { { "usuario", new Objeto { { "nombre", nombre }, { "rol", Almacen.Rol(nombre) } } } };
            if (paraApp)
            {
                datos["token"] = token;
                EnviarJson(datos);
            }
            else EnviarJson(datos, 200, Galleta(token, Almacen.DuracionTexto));
        }

        // ── validaciones ─────────────────────────────────────────────────

        /// <summary>[\w.\-]{3,32} de Python: letras y números Unicode, guion bajo, punto y guion.</summary>
        public static bool NombreValido(string nombre)
        {
            int cuenta = 0;
            for (int i = 0; i < nombre.Length; i++)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(nombre, i);
                if (char.IsHighSurrogate(nombre[i]) && i + 1 < nombre.Length && char.IsLowSurrogate(nombre[i + 1])) i++;
                char c = nombre[i];
                bool ok = c == '_' || c == '.' || c == '-'
                    || cat == UnicodeCategory.UppercaseLetter || cat == UnicodeCategory.LowercaseLetter
                    || cat == UnicodeCategory.TitlecaseLetter || cat == UnicodeCategory.ModifierLetter
                    || cat == UnicodeCategory.OtherLetter || cat == UnicodeCategory.DecimalDigitNumber
                    || cat == UnicodeCategory.LetterNumber || cat == UnicodeCategory.OtherNumber;
                if (!ok) return false;
                cuenta++;
            }
            return cuenta >= 3 && cuenta <= 32;
        }

        public static string ValidarNombre(object valor)
        {
            var nombre = valor as string;
            if (nombre == null || !NombreValido(Py.Strip(nombre)))
                throw new ErrorPeticion("El usuario debe tener de 3 a 32 letras, números, puntos o guiones");
            return Py.Lower(Py.Strip(nombre));
        }

        public static string ValidarContrasena(object valor)
        {
            var contrasena = valor as string;
            if (contrasena == null || Py.Longitud(contrasena) < 6 || Py.Longitud(contrasena) > 200)
                throw new ErrorPeticion("La contraseña debe tener al menos 6 caracteres");
            return contrasena;
        }

        // ── API pública ──────────────────────────────────────────────────

        void ApiEstado()
        {
            string usuario = UsuarioActual();
            EnviarJson(new Objeto
            {
                { "configurado", Almacen.HayUsuarios() },
                { "local", EsLocal() },
                { "usuario", usuario != null ? new Objeto { { "nombre", usuario }, { "rol", Almacen.Rol(usuario) } } : null },
            });
        }

        void ApiConfigurar()
        {
            var datos = LeerCuerpo();
            if (!EsLocal()) throw new ErrorPeticion("El primer administrador solo se puede crear desde el propio ordenador", 403);
            string nombre = ValidarNombre(datos["usuario"]);
            string contrasena = ValidarContrasena(datos["contrasena"]);
            lock (Almacen.Cerrojo)
            {
                if (Almacen.HayUsuarios()) throw new ErrorPeticion("MiCine ya está configurado", 409);
                Almacen.CrearUsuario(nombre, contrasena, "admin");
            }
            Registro.Anotar("Administrador «" + nombre + "» creado");
            ResponderSesion(nombre, false);
        }

        void ApiLogin()
        {
            var datos = LeerCuerpo();
            string ip = IpCliente();
            var limitador = servidor.Limitador;
            int espera = limitador.Espera(ip);
            if (espera > 0)
                throw new ErrorPeticion("Demasiados intentos fallidos. Espera " + (int)Math.Ceiling(espera / 60.0) + " min.", 429);
            string nombre = Py.Lower(Py.Strip(Py.Str(datos.Obtener("usuario", ""))));
            string contrasena = Py.Recortar(Py.Str(datos.Obtener("contrasena", "")), 200);
            Objeto registro = Almacen.Registro(nombre);
            bool hayRegistro = registro != null && registro.Count > 0;
            if (!Cripto.ComprobarContrasena(contrasena, hayRegistro ? registro : HashFalso.Obtener()) || !hayRegistro)
            {
                limitador.Fallo(ip);
                Registro.Anotar("Acceso fallido para «" + nombre + "» desde " + ip);
                throw new ErrorPeticion("Usuario o contraseña incorrectos", 401);
            }
            limitador.Exito(ip);
            bool app = Py.Verdad(datos["app"]);
            Registro.Anotar("«" + nombre + "» ha entrado desde " + ip + (app ? " (app)" : ""));
            ResponderSesion(nombre, app);
        }

        // ── API con sesión ───────────────────────────────────────────────

        void ApiLogout(string usuario)
        {
            LeerCuerpo();
            Almacen.CerrarSesion(Token());
            EnviarJson(new Objeto { { "ok", true } }, 200, Galleta("", "0"));
        }

        void ApiPeliculas(string usuario)
        {
            var progreso = Almacen.ProgresoDe(usuario);
            var peliculas = Biblioteca.Listar();
            List<object> secciones;
            Objeto obras = servidor.Identificacion.Clasificar(peliculas, out secciones);
            Identificacion.Ordenar(peliculas, obras);
            servidor.Caratulas.Revisar(peliculas); // antes de enlazar: aquí poster es aún la imagen propia (o null)
            foreach (var p in peliculas)
            {
                string ruta = (string)p["ruta"];
                p["progreso"] = progreso.Obtener(ruta, null);
                p["url"] = Almacen.Enlace(usuario, ruta);
                if (p["poster"] != null) p["poster"] = Almacen.Enlace(usuario, (string)p["poster"]);
                else
                {
                    // Sin imagen propia: el fotograma del vídeo, si ya está hecho
                    string clave = Caratulas.Clave(p);
                    long version = servidor.Caratulas.Version(clave);
                    p["poster"] = version > 0 ? Almacen.EnlaceCaratula(usuario, clave, version) : null;
                }
                foreach (Objeto s in (List<object>)p["subtitulos"]) s["url"] = Almacen.Enlace(usuario, (string)s["ruta"]);
            }
            // Carátula de cada obra: la de su primera película o la del episodio más bajo que tenga
            var elegido = new Dictionary<string, Objeto>(StringComparer.Ordinal);
            foreach (var p in peliculas)
            {
                string obra = p["obra"] as string;
                if (obra == null || p["poster"] == null) continue;
                if (!elegido.ContainsKey(obra)) elegido[obra] = p;
            }
            foreach (var kv in elegido) { var o = obras.ObjetoDe(kv.Key); if (o != null) o["poster"] = kv.Value["poster"]; }

            var ident = servidor.Identificacion;
            // La dirección de este ordenador en la red de casa: la que hay que escribir en la tele.
            string ip = servidor.Direccion == "0.0.0.0" || servidor.Direccion == "" ? Servidor.IpEnRed() : null;
            var datos = new Objeto
            {
                { "peliculas", peliculas }, { "obras", obras }, { "secciones", secciones },
                { "identificacion", new Objeto { { "tmdb", ident.ConClave }, { "pendientes", (long)ident.Pendientes }, { "estado", ident.EstadoParaWeb } } },
                { "caratulas", new Objeto { { "pendientes", (long)servidor.Caratulas.Pendientes } } },
                { "conversion", servidor.Conversor != null },
                { "red", ip != null ? "http://" + ip + ":" + servidor.Puerto : null },
            };
            if (Almacen.Rol(usuario) == "admin") datos["carpeta"] = Biblioteca.Carpeta;
            EnviarJson(datos);
        }

        /// <summary>El fotograma de un vídeo sin carátula propia (enlace firmado, como los de /media/).</summary>
        void ServirCaratula(string nombre)
        {
            if (!Regex.IsMatch(nombre, @"\A[0-9a-f]{20}\.jpg\z")) throw new ErrorPeticion("No encontrado", 404);
            string clave = nombre.Substring(0, 20);
            if (UsuarioActual() == null && Almacen.UsuarioDeEnlace("caratula:" + clave, consulta) == null)
                throw new ErrorPeticion("Inicia sesión para continuar", 401);
            string archivo = servidor.Caratulas.Archivo(clave);
            if (!File.Exists(archivo)) throw new ErrorPeticion("No encontrado", 404);
            EnviarBytes(File.ReadAllBytes(archivo), "image/jpeg", 200, Lista("Cache-Control", "private, max-age=86400"));
        }

        void ApiIdentificarBuscar(string usuario)
        {
            string texto = Py.Strip(Primero("q") ?? "");
            if (texto == "") throw new ErrorPeticion("Escribe qué quieres buscar");
            bool serie = Primero("tipo") == "serie";
            EnviarJson(new Objeto { { "resultados", servidor.Identificacion.Buscar(texto, serie) } });
        }

        void ApiIdentificar(string usuario)
        {
            var datos = LeerCuerpo();
            string grupo = datos["grupo"] as string, obra = datos["obra"] as string;
            servidor.Identificacion.Asignar(grupo, obra);
            Registro.Anotar("«" + usuario + "» ha identificado a mano «" + grupo + "»" + (obra != null ? " (" + obra + ")" : " (sin identificar)"));
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiIdentificarActualizar(string usuario)
        {
            LeerCuerpo();
            servidor.Identificacion.Reintentar();
            Registro.Anotar("«" + usuario + "» ha pedido volver a identificar lo que falta");
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiCaratulasActualizar(string usuario)
        {
            LeerCuerpo();
            if (!servidor.Caratulas.Disponible) throw new ErrorPeticion("Para sacar carátulas del vídeo hace falta FFmpeg (ver LEEME.md)", 501);
            int pendientes = servidor.Caratulas.Rehacer();
            Registro.Anotar("«" + usuario + "» ha pedido rehacer las carátulas del vídeo (" + pendientes + ")");
            EnviarJson(new Objeto { { "ok", true }, { "pendientes", (long)pendientes } });
        }

        void ApiInfo(string usuario)
        {
            string ruta = Biblioteca.Resolver(Primero("ruta"));
            string ext = ruta != null ? Py.Lower(Py.Sufijo(Path.GetFileName(ruta))) : "";
            if (ruta == null || !Tipos.Videos.ContainsKey(ext)) throw new ErrorPeticion("Esa película ya no existe", 404);
            var conversor = servidor.Conversor;
            if (conversor != null && consulta.ContainsKey("clave"))
            {
                EnviarJson(new Objeto { { "clave", conversor.FotogramaClave(ruta, Numero("clave", 1e6)) } });
                return;
            }
            var datos = conversor != null ? conversor.Info(ruta).Copia() : new Objeto();
            datos["conversion"] = conversor != null;
            EnviarJson(datos);
        }

        static bool NumeroValido(object x)
        {
            if (!Py.EsNumero(x)) return false;
            double d = Py.ANumero(x);
            return d >= 0 && d < 1e6;
        }

        void ApiProgreso(string usuario)
        {
            var datos = LeerCuerpo();
            object ruta = datos["ruta"], segundos = datos["t"], duracion = datos["d"];
            bool numerosValidos = NumeroValido(segundos) && NumeroValido(duracion);
            if (!(ruta is string) || !numerosValidos || Biblioteca.Resolver((string)ruta) == null)
                throw new ErrorPeticion("Datos de progreso no válidos");
            Almacen.GuardarProgreso(usuario, (string)ruta, Py.ANumero(segundos), Py.ANumero(duracion));
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiMiContrasena(string usuario)
        {
            var datos = LeerCuerpo();
            string actual = Py.Recortar(Py.Str(datos.Obtener("actual", "")), 200);
            if (!Cripto.ComprobarContrasena(actual, Almacen.Registro(usuario)))
                throw new ErrorPeticion("La contraseña actual no es correcta", 403);
            Almacen.FijarContrasena(usuario, ValidarContrasena(datos["nueva"]), null);
            Almacen.CerrarSesionesDe(usuario, Token());
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiBorrarPelicula(string usuario)
        {
            string ruta = Py.Str(LeerCuerpo().Obtener("ruta", ""));
            int movidos = Biblioteca.APapelera(ruta);
            Registro.Anotar("«" + usuario + "» ha movido «" + ruta + "» a la papelera (" + movidos + " archivo/s)");
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiUsuarios(string usuario)
        {
            EnviarJson(new Objeto { { "usuarios", Almacen.ListaUsuarios() } });
        }

        void ApiCrearUsuario(string usuario)
        {
            var datos = LeerCuerpo();
            string nombre = ValidarNombre(datos["usuario"]);
            string contrasena = ValidarContrasena(datos["contrasena"]);
            string rol = datos["rol"] as string;
            if (rol != "admin" && rol != "usuario") throw new ErrorPeticion("Rol no válido");
            Almacen.CrearUsuario(nombre, contrasena, rol);
            Registro.Anotar("«" + usuario + "» ha creado el usuario «" + nombre + "» (" + rol + ")");
            EnviarJson(new Objeto { { "ok", true } }, 201, null);
        }

        void ApiBorrarUsuario(string usuario)
        {
            string nombre = Py.Str(LeerCuerpo().Obtener("usuario", ""));
            if (nombre == usuario) throw new ErrorPeticion("No puedes borrar tu propio usuario");
            Almacen.BorrarUsuario(nombre);
            Registro.Anotar("«" + usuario + "» ha borrado el usuario «" + nombre + "»");
            EnviarJson(new Objeto { { "ok", true } });
        }

        void ApiContrasenaUsuario(string usuario)
        {
            var datos = LeerCuerpo();
            string nombre = Py.Str(datos.Obtener("usuario", ""));
            Almacen.FijarContrasena(nombre, ValidarContrasena(datos["contrasena"]), null);
            Almacen.CerrarSesionesDe(nombre, nombre == usuario ? Token() : null);
            Registro.Anotar("«" + usuario + "» ha cambiado la contraseña de «" + nombre + "»");
            EnviarJson(new Objeto { { "ok", true } });
        }
    }
}
