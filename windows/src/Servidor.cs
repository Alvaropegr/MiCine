using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiCine
{
    /// <summary>El cliente se ha ido (o no contesta): se cierra sin ruido.</summary>
    public sealed class Desconectado : Exception
    {
        public Desconectado(Exception causa) : base("Conexión cerrada por el cliente", causa) { }
    }

    /// <summary>
    /// Servidor HTTP/1.1 mínimo sobre <c>TcpListener</c>, no
    /// <c>HttpListener</c>: este último necesita que un administrador reserve
    /// la URL con <c>netsh http add urlacl</c> para escuchar en la red de casa,
    /// y MiCine tiene que funcionar con un usuario normal.
    ///
    /// Imita a ThreadingHTTPServer + BaseHTTPRequestHandler de Python: un hilo
    /// por conexión, conexiones persistentes (keep-alive), 300 s de espera,
    /// «100 Continue», los mismos límites de cabeceras y los mismos errores.
    /// </summary>
    public sealed class Servidor
    {
        public readonly string Base;
        public readonly Configuracion Config;
        public readonly Almacen Almacen;
        public readonly Biblioteca Biblioteca;
        public readonly Limitador Limitador = new Limitador();
        /// <summary>null si no hay FFmpeg: entonces no se convierte nada.</summary>
        public readonly Conversor Conversor;
        /// <summary>El servidor DLNA para las teles (null si está desactivado).</summary>
        public volatile Dlna Dlna;

        public int Puerto { get; private set; }
        public string Direccion { get; private set; }
        public bool Activo { get { return activo; } }

        TcpListener escucha;
        volatile bool activo;
        readonly HashSet<TcpClient> clientes = new HashSet<TcpClient>();

        public Servidor(string baseDir, Configuracion config, Almacen almacen, Biblioteca biblioteca, Conversor conversor)
        {
            Base = baseDir;
            Config = config;
            Almacen = almacen;
            Biblioteca = biblioteca;
            Conversor = conversor;
        }

        /// <summary>Empieza a escuchar. Devuelve null si va bien o el motivo si no.</summary>
        public string Arrancar(string direccion, int puerto)
        {
            Direccion = direccion;
            Puerto = puerto;
            IPAddress ip;
            try { ip = ResolverDireccion(direccion); }
            catch (Exception e) { return "La dirección «" + direccion + "» no es válida: " + e.Message; }
            try
            {
                escucha = new TcpListener(ip, puerto);
                escucha.Start(64);
            }
            catch (SocketException e)
            {
                escucha = null;
                if (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    return "El puerto " + puerto + " ya está en uso. ¿Ya hay otro MiCine abierto? Ciérralo o cambia el puerto.";
                if (e.SocketErrorCode == SocketError.AccessDenied)
                    return "Windows no deja usar el puerto " + puerto + " (lo tiene reservado u otro programa lo usa en exclusiva). Prueba con otro puerto.";
                if (e.SocketErrorCode == SocketError.AddressNotAvailable)
                    return "La dirección " + direccion + " no pertenece a este ordenador (revisa «direccion» en config.json).";
                return "No se pudo abrir el puerto " + puerto + ": " + e.Message;
            }
            activo = true;
            HashFalso.Preparar();
            var hilo = new Thread(Aceptar) { IsBackground = true, Name = "MiCine: escucha" };
            hilo.Start();
            return null;
        }

        static IPAddress ResolverDireccion(string direccion)
        {
            if (string.IsNullOrEmpty(direccion) || direccion == "0.0.0.0") return IPAddress.Any;
            IPAddress ip;
            if (IPAddress.TryParse(direccion, out ip)) return ip;
            foreach (var a in Dns.GetHostAddresses(direccion))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            throw new ArgumentException("no se encuentra");
        }

        /// <summary>Deja de escuchar y corta las conexiones abiertas (vídeos en curso incluidos).</summary>
        public void Parar()
        {
            activo = false;
            try { if (escucha != null) escucha.Stop(); } catch (Exception) { }
            TcpClient[] abiertos;
            lock (clientes) { abiertos = new TcpClient[clientes.Count]; clientes.CopyTo(abiertos); }
            foreach (var c in abiertos) { try { c.Close(); } catch (Exception) { } }
            if (Conversor != null) Conversor.PararTodos();
        }

        void Aceptar()
        {
            var mia = escucha;
            while (activo)
            {
                TcpClient c;
                try { c = mia.AcceptTcpClient(); }
                catch (Exception) { if (!activo) return; Thread.Sleep(50); continue; }
                lock (clientes) clientes.Add(c);
                var hilo = new Thread(delegate()
                {
                    try { new Conexion(this, c).Atender(); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                    catch (Exception e) { Registro.Error("conexión", e); }
                    finally
                    {
                        lock (clientes) clientes.Remove(c);
                        try { c.Close(); } catch (Exception) { }
                    }
                });
                hilo.IsBackground = true;
                hilo.Name = "MiCine: conexión";
                try { hilo.Start(); }
                catch (Exception) { lock (clientes) clientes.Remove(c); c.Close(); }
            }
        }

        /// <summary>IP de este ordenador en la red de casa (no envía nada: solo pregunta la ruta).</summary>
        public static string IpEnRed()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("10.255.255.255", 1);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Se compara contra este hash cuando el usuario no existe, para tardar lo
    /// mismo y no delatar qué nombres de usuario son válidos (HASH_FALSO).
    /// Se calcula una vez, en segundo plano, al arrancar.
    /// </summary>
    public static class HashFalso
    {
        static Objeto valor;
        static readonly object cerrojo = new object();

        public static void Preparar()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { Obtener(); } catch (Exception) { } });
        }

        public static Objeto Obtener()
        {
            lock (cerrojo)
            {
                if (valor == null) valor = Cripto.HashContrasena(Cripto.TokenHex(8));
                return valor;
            }
        }
    }

    /// <summary>Una conexión TCP: lee peticiones una tras otra mientras el cliente la mantenga.</summary>
    public sealed class Conexion
    {
        public const int LINEA_MAXIMA = 65536;
        public const int CABECERAS_MAXIMAS = 100;

        public readonly Servidor Servidor;
        readonly TcpClient cliente;
        readonly NetworkStream red;
        public readonly IPAddress IpRemota;
        public readonly string Ip;
        /// <summary>La IP de este ordenador por la que ha llegado la conexión (la que hay que darle a una tele).</summary>
        public readonly IPAddress IpLocal;

        readonly byte[] buffer = new byte[65536];
        int ini, fin;

        /// <summary>close_connection de Python.</summary>
        public bool Cerrar = true;
        public bool CabecerasEnviadas;
        public string Metodo = "";
        public bool Version09;
        bool pendienteSinLeer;

        public Conexion(Servidor servidor, TcpClient cliente)
        {
            Servidor = servidor;
            this.cliente = cliente;
            cliente.NoDelay = true;
            cliente.ReceiveTimeout = 300000;
            cliente.SendTimeout = 300000;
            red = cliente.GetStream();
            var punto = (IPEndPoint)cliente.Client.RemoteEndPoint;
            IpRemota = punto.Address.IsIPv4MappedToIPv6 ? punto.Address.MapToIPv4() : punto.Address;
            Ip = IpRemota.ToString();
            var local = ((IPEndPoint)cliente.Client.LocalEndPoint).Address;
            IpLocal = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
        }

        // ── lectura ──────────────────────────────────────────────────────

        bool Llenar()
        {
            int n;
            try { n = red.Read(buffer, 0, buffer.Length); }
            catch (IOException e) { throw new Desconectado(e); }
            catch (ObjectDisposedException e) { throw new Desconectado(e); }
            ini = 0;
            fin = n;
            return n > 0;
        }

        /// <summary>rfile.readline(limite): hasta '\n' incluido, como mucho <paramref name="limite"/> bytes; vacío al final.</summary>
        public byte[] LeerLinea(int limite)
        {
            var m = new MemoryStream();
            while (m.Length < limite)
            {
                if (ini >= fin && !Llenar()) break;
                int max = (int)Math.Min(fin - ini, limite - m.Length);
                int i = Array.IndexOf(buffer, (byte)'\n', ini, max);
                if (i >= 0)
                {
                    m.Write(buffer, ini, i - ini + 1);
                    ini = i + 1;
                    break;
                }
                m.Write(buffer, ini, max);
                ini += max;
            }
            return m.ToArray();
        }

        /// <summary>Lee lo que haya (como mucho n bytes); 0 si el cliente cerró.</summary>
        public int Leer(byte[] destino, int desde, int n)
        {
            if (n <= 0) return 0;
            if (ini >= fin)
            {
                // Los trozos grandes se leen directamente, sin pasar por el búfer.
                if (n >= buffer.Length)
                {
                    try { return red.Read(destino, desde, n); }
                    catch (IOException e) { throw new Desconectado(e); }
                    catch (ObjectDisposedException e) { throw new Desconectado(e); }
                }
                if (!Llenar()) return 0;
            }
            int k = Math.Min(n, fin - ini);
            Buffer.BlockCopy(buffer, ini, destino, desde, k);
            ini += k;
            return k;
        }

        /// <summary>rfile.read(n): n bytes, o menos si el cliente cierra antes.</summary>
        public byte[] LeerExacto(int n)
        {
            var datos = new byte[n];
            int leido = 0;
            while (leido < n)
            {
                int r = Leer(datos, leido, n - leido);
                if (r <= 0) break;
                leido += r;
            }
            if (leido == n) return datos;
            var corto = new byte[leido];
            Buffer.BlockCopy(datos, 0, corto, 0, leido);
            return corto;
        }

        // ── escritura ────────────────────────────────────────────────────

        public void Escribir(byte[] datos, int desde, int n)
        {
            try { red.Write(datos, desde, n); }
            catch (IOException e) { throw new Desconectado(e); }
            catch (ObjectDisposedException e) { throw new Desconectado(e); }
        }

        static readonly Dictionary<int, string> Frases = new Dictionary<int, string>
        {
            { 100, "Continue" }, { 200, "OK" }, { 201, "Created" }, { 206, "Partial Content" }, { 302, "Found" },
            { 400, "Bad Request" }, { 401, "Unauthorized" }, { 403, "Forbidden" }, { 404, "Not Found" },
            { 409, "Conflict" }, { 411, "Length Required" }, { 413, "Content Too Large" }, { 414, "URI Too Long" },
            { 416, "Range Not Satisfiable" }, { 429, "Too Many Requests" }, { 431, "Request Header Fields Too Large" },
            { 500, "Internal Server Error" }, { 501, "Not Implemented" }, { 505, "HTTP Version Not Supported" },
            { 507, "Insufficient Storage" },
        };

        static readonly Dictionary<int, string> Explicaciones = new Dictionary<int, string>
        {
            { 400, "Bad request syntax or unsupported method" },
            { 414, "URI is too long" },
            { 431, "The server refused this request because the request header fields are too large" },
            { 501, "Server does not support this operation" },
            { 505, "Cannot fulfill request" },
        };

        /// <summary>Cabeceras de respuesta en el orden de servidor.py (send_response + enviar_cabeceras).</summary>
        public byte[] Cabeceras(int codigo, string frase, List<KeyValuePair<string, string>> lista)
        {
            if (Version09) return new byte[0];
            var h = new StringBuilder();
            string f;
            if (frase == null) frase = Frases.TryGetValue(codigo, out f) ? f : "";
            h.Append("HTTP/1.1 ").Append(codigo.ToString(Py.Inv)).Append(' ').Append(frase).Append("\r\n");
            h.Append("Server: MiCine\r\n");
            h.Append("Date: ").Append(DateTime.UtcNow.ToString("r", Py.Inv)).Append("\r\n");
            bool conConnection = false;
            foreach (var kv in lista)
            {
                h.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                if (string.Equals(kv.Key, "Connection", StringComparison.OrdinalIgnoreCase)) conConnection = true;
            }
            // Python cierra sin avisar; decirlo ayuda al cliente a no reutilizar la conexión.
            if (Cerrar && !conConnection) h.Append("Connection: close\r\n");
            h.Append("\r\n");
            return Py.Latin1.GetBytes(h.ToString());
        }

        /// <summary>Envía cabeceras y cuerpo (si lo hay) en una sola escritura.</summary>
        public void Enviar(int codigo, List<KeyValuePair<string, string>> cabeceras, byte[] cuerpo)
        {
            byte[] h = Cabeceras(codigo, null, cabeceras);
            CabecerasEnviadas = true;
            bool conCuerpo = cuerpo != null && cuerpo.Length > 0 && Metodo != "HEAD";
            var todo = new byte[h.Length + (conCuerpo ? cuerpo.Length : 0)];
            Buffer.BlockCopy(h, 0, todo, 0, h.Length);
            if (conCuerpo) Buffer.BlockCopy(cuerpo, 0, todo, h.Length, cuerpo.Length);
            Escribir(todo, 0, todo.Length);
        }

        /// <summary>send_error() de BaseHTTPRequestHandler: página HTML y cierre de la conexión.</summary>
        void EnviarError(int codigo, string mensaje)
        {
            EnviarError(codigo, mensaje, null);
        }

        void EnviarError(int codigo, string mensaje, string explicacion)
        {
            string corto, largo;
            if (!Frases.TryGetValue(codigo, out corto)) corto = "???";
            if (!Explicaciones.TryGetValue(codigo, out largo)) largo = corto;
            if (explicacion != null) largo = explicacion;
            if (mensaje == null) mensaje = corto;
            Registro.Anotar(Ip + ": code " + codigo + ", message " + mensaje);
            Cerrar = true;
            string html = "<!DOCTYPE HTML>\n<html lang=\"en\">\n    <head>\n        <meta charset=\"utf-8\">\n" +
                "        <title>Error response</title>\n    </head>\n    <body>\n        <h1>Error response</h1>\n" +
                "        <p>Error code: " + codigo + "</p>\n        <p>Message: " + EscaparHtml(mensaje) + ".</p>\n" +
                "        <p>Error code explanation: " + codigo + " - " + EscaparHtml(largo) + ".</p>\n    </body>\n</html>\n";
            byte[] cuerpo = Py.Utf8.GetBytes(html);
            var lista = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Connection", "close"),
                new KeyValuePair<string, string>("Content-Type", "text/html;charset=utf-8"),
                new KeyValuePair<string, string>("Content-Length", cuerpo.Length.ToString(Py.Inv)),
            };
            byte[] h = Cabeceras(codigo, mensaje, lista);
            CabecerasEnviadas = true;
            Escribir(h, 0, h.Length);
            if (Metodo != "HEAD") Escribir(cuerpo, 0, cuerpo.Length);
        }

        static string EscaparHtml(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ── peticiones ───────────────────────────────────────────────────

        public void Atender()
        {
            try
            {
                Cerrar = true;
                while (UnaPeticion() && !Cerrar) { }
            }
            catch (Desconectado) { }
            finally { CierreSuave(); }
        }

        /// <summary>
        /// Al cerrar sin haber leído el cuerpo (una subida rechazada), se deja de
        /// enviar y se descarta lo que llegue un momento: así el cliente llega a
        /// leer la respuesta en vez de recibir un «connection reset».
        /// </summary>
        void CierreSuave()
        {
            try
            {
                cliente.Client.Shutdown(SocketShutdown.Send);
                if (pendienteSinLeer)
                {
                    cliente.ReceiveTimeout = 2000;
                    var tiempo = System.Diagnostics.Stopwatch.StartNew();
                    var tirar = new byte[65536];
                    while (tiempo.ElapsedMilliseconds < 2000)
                    {
                        if (red.Read(tirar, 0, tirar.Length) <= 0) break;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>handle_one_request(): false si hay que cerrar ya.</summary>
        bool UnaPeticion()
        {
            Cerrar = true;
            CabecerasEnviadas = false;
            Version09 = false;
            Metodo = "";
            byte[] linea = LeerLinea(LINEA_MAXIMA + 1);
            if (linea.Length > LINEA_MAXIMA)
            {
                EnviarError(414, null);
                return false;
            }
            if (linea.Length == 0) return false;

            string peticion = Py.Latin1.GetString(linea).TrimEnd('\r', '\n');
            var palabras = Py.Split(peticion);
            if (palabras.Count == 0) return false;

            int mayor = 0, menor = 9;
            if (palabras.Count >= 3)
            {
                string version = palabras[palabras.Count - 1];
                if (!VersionValida(version, out mayor, out menor))
                {
                    EnviarError(400, "Bad request version (" + Py.ReprTexto(version) + ")");
                    return false;
                }
                if (mayor > 1 || (mayor == 1 && menor >= 1)) Cerrar = false;
                if (mayor >= 2)
                {
                    EnviarError(505, "Invalid HTTP version (" + version.Substring(5) + ")");
                    return false;
                }
            }
            if (palabras.Count < 2 || palabras.Count > 3)
            {
                EnviarError(400, "Bad request syntax (" + Py.ReprTexto(peticion) + ")");
                return false;
            }
            string metodo = palabras[0], ruta = palabras[1];
            if (palabras.Count == 2)
            {
                Cerrar = true;
                if (metodo != "GET")
                {
                    EnviarError(400, "Bad HTTP/0.9 request type (" + Py.ReprTexto(metodo) + ")");
                    return false;
                }
                Version09 = true;
            }
            Metodo = metodo;
            // gh-87389: '//ruta' se reduce a '/ruta'.
            if (ruta.StartsWith("//", StringComparison.Ordinal)) ruta = "/" + ruta.TrimStart('/');

            List<KeyValuePair<string, string>> cabeceras;
            string error = LeerCabeceras(out cabeceras);
            if (error != null)
            {
                EnviarError(431, error, error == "Line too long"
                    ? "got more than " + LINEA_MAXIMA + " bytes when reading header line"
                    : "got more than " + CABECERAS_MAXIMAS + " headers");
                return false;
            }
            string conexion = Cabecera(cabeceras, "Connection") ?? "";
            if (conexion.ToLowerInvariant() == "close") Cerrar = true;
            else if (conexion.ToLowerInvariant() == "keep-alive") Cerrar = false;
            string espera = Cabecera(cabeceras, "Expect") ?? "";
            if (espera.ToLowerInvariant() == "100-continue" && (mayor > 1 || (mayor == 1 && menor >= 1)) && !Version09)
            {
                byte[] sigue = Py.Latin1.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                Escribir(sigue, 0, sigue.Length);
            }

            // Las teles DLNA hablan su propio idioma (HEAD, SOAP, SUBSCRIBE…): van aparte.
            if (ruta.StartsWith("/dlna/", StringComparison.Ordinal) && Array.IndexOf(PeticionDlna.Metodos, metodo) >= 0)
            {
                var d = new PeticionDlna(this, Servidor.Dlna, metodo, ruta, cabeceras);
                d.Atender();
                if (!d.CuerpoLeido) pendienteSinLeer = true;
                return true;
            }
            if (metodo != "GET" && metodo != "POST" && metodo != "OPTIONS")
            {
                EnviarError(501, "Unsupported method (" + Py.ReprTexto(metodo) + ")");
                return false;
            }
            var m = new Manejador(this, metodo, ruta, cabeceras);
            m.Atender();
            if (!m.CuerpoLeido) pendienteSinLeer = true;
            return true;
        }

        static bool VersionValida(string version, out int mayor, out int menor)
        {
            mayor = menor = 0;
            if (!version.StartsWith("HTTP/", StringComparison.Ordinal)) return false;
            string[] partes = version.Substring(5).Split('.');
            if (partes.Length != 2) return false;
            foreach (string p in partes)
            {
                if (p.Length == 0 || p.Length > 10) return false;
                foreach (char c in p) if (!char.IsDigit(c)) return false;
            }
            long a, b;
            if (!Py.Entero(partes[0], out a) || !Py.Entero(partes[1], out b)) return false;
            mayor = (int)Math.Min(a, int.MaxValue);
            menor = (int)Math.Min(b, int.MaxValue);
            return true;
        }

        /// <summary>http.client.parse_headers + el analizador de email (compat32).</summary>
        string LeerCabeceras(out List<KeyValuePair<string, string>> cabeceras)
        {
            cabeceras = new List<KeyValuePair<string, string>>();
            var lineas = new List<string>();
            while (true)
            {
                byte[] l = LeerLinea(LINEA_MAXIMA + 1);
                if (l.Length > LINEA_MAXIMA) return "Line too long";
                if (l.Length == 0 || (l.Length == 1 && l[0] == '\n') || (l.Length == 2 && l[0] == '\r' && l[1] == '\n')) break;
                lineas.Add(Py.Latin1.GetString(l));
                if (lineas.Count > CABECERAS_MAXIMAS) return "Too many headers";
            }
            string nombre = null;
            var valor = new StringBuilder();
            foreach (string l in lineas)
            {
                if (l[0] == ' ' || l[0] == '\t')
                {
                    if (nombre != null) valor.Append(l);
                    continue;
                }
                int dp = l.IndexOf(':');
                bool valida = dp >= 0;
                for (int i = 0; valida && i < dp; i++) if (l[i] < 0x21 || l[i] > 0x7e) valida = false;
                if (!valida) break; // como Python: lo que sigue ya no se trata como cabeceras
                if (nombre != null) cabeceras.Add(new KeyValuePair<string, string>(nombre, valor.ToString().TrimEnd('\r', '\n')));
                nombre = l.Substring(0, dp);
                valor.Clear();
                valor.Append(l.Substring(dp + 1).TrimStart(' ', '\t'));
            }
            if (nombre != null) cabeceras.Add(new KeyValuePair<string, string>(nombre, valor.ToString().TrimEnd('\r', '\n')));
            return null;
        }

        public static string Cabecera(List<KeyValuePair<string, string>> cabeceras, string nombre)
        {
            foreach (var kv in cabeceras)
                if (string.Equals(kv.Key, nombre, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }
}
