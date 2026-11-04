using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace MiCine
{
    /// <summary>Un aparato de la red que ha pedido ver MiCine por DLNA (casi siempre, una tele).</summary>
    public sealed class Aparato
    {
        public string Clave, Nombre, Ip, Estado;
        public long Visto;
    }

    /// <summary>
    /// MiCine como servidor DLNA (UPnP MediaServer). Las teles que no pueden instalar la app de MiCine
    /// (las Samsung de antes de 2015, por ejemplo) lo ven en su lista de fuentes y ponen las películas con
    /// su propio reproductor; lo que no saben reproducir se convierte al momento con FFmpeg.
    /// DLNA no tiene usuarios ni contraseñas: cada aparato necesita permiso en la ventana de MiCine, y
    /// solo se atiende a la red de casa (nunca a lo que llega por un túnel).
    /// </summary>
    public sealed class Dlna
    {
        public const string PENDIENTE = "pendiente", PERMITIDO = "permitido", BLOQUEADO = "bloqueado";
        public const string NOMBRE = "MiCine";

        const string GRUPO = "239.255.255.250";
        const int PUERTO_SSDP = 1900;
        const int VIGENCIA = 1800;           // segundos que los aparatos recuerdan el anuncio
        const int CADA_CUANTO_ANUNCIAR = 600;
        internal const string DISPOSITIVO = "urn:schemas-upnp-org:device:MediaServer:1";
        internal const string SERVICIO_CD = "urn:schemas-upnp-org:service:ContentDirectory:1";
        internal const string SERVICIO_CM = "urn:schemas-upnp-org:service:ConnectionManager:1";
        internal static readonly string Firma = "Windows/" + Environment.OSVersion.Version.Major + "." +
            Environment.OSVersion.Version.Minor + " UPnP/1.0 MiCine/1.0";

        /// <summary>Avisos para la ventana (los lanza el DLNA que esté en marcha).</summary>
        public static event Action Cambio;
        public static event Action<Aparato> NuevoAparato;

        public readonly Servidor Servidor;
        public readonly string Udn;
        readonly string rutaDatos;
        readonly object cerrojo = new object();
        readonly Objeto datos;
        readonly Random azar = new Random();

        public Dlna(string carpetaDatos, Servidor servidor)
        {
            Servidor = servidor;
            rutaDatos = Path.Combine(carpetaDatos, "dlna.json");
            datos = Json.LeerArchivo(rutaDatos, new Objeto()) as Objeto ?? new Objeto();
            if (!(datos["udn"] is string)) datos["udn"] = Guid.NewGuid().ToString();
            if (datos.ObjetoDe("aparatos") == null) datos["aparatos"] = new Objeto();
            Udn = (string)datos["udn"];
            Guardar();
        }

        void Guardar()
        {
            lock (cerrojo)
            {
                try { Json.EscribirArchivo(rutaDatos, datos); }
                catch (Exception e) { Registro.Error("guardar dlna.json", e); }
            }
        }

        static void Avisar()
        {
            var a = Cambio;
            if (a != null) { try { a(); } catch (Exception) { } }
        }

        // ── la red de casa ───────────────────────────────────────────────

        /// <summary>Direcciones privadas (192.168…, 10…, 172.16-31…), de enlace local o de este mismo ordenador.</summary>
        public static bool EsDeCasa(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destino, uint origen, byte[] mac, ref uint largo);

        readonly Dictionary<string, KeyValuePair<string, DateTime>> macs = new Dictionary<string, KeyValuePair<string, DateTime>>();

        /// <summary>La dirección física (MAC) del aparato: no cambia aunque el router le dé otra IP.</summary>
        string Mac(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return null;
            string clave = ip.ToString();
            lock (macs)
            {
                KeyValuePair<string, DateTime> guardada;
                if (macs.TryGetValue(clave, out guardada) && (DateTime.UtcNow - guardada.Value).TotalMinutes < 10) return guardada.Key;
            }
            string mac = null;
            try
            {
                var bytes = new byte[8];
                uint largo = (uint)bytes.Length;
                if (SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, bytes, ref largo) == 0 && largo >= 6)
                    mac = string.Join("-", bytes.Take(6).Select(x => x.ToString("x2", Py.Inv)));
            }
            catch (Exception) { }
            lock (macs) macs[clave] = new KeyValuePair<string, DateTime>(mac, DateTime.UtcNow);
            return mac;
        }

        string ClaveDe(IPAddress ip)
        {
            string mac = Mac(ip);
            return mac != null ? "mac:" + mac : "ip:" + ip;
        }

        // ── aparatos y permisos ──────────────────────────────────────────

        static readonly Regex ModeloSamsung = new Regex("mn=\"([^\"]{1,60})\"");

        /// <summary>Un nombre reconocible: «Tele Samsung UE40H5570», «VLC»…</summary>
        static string NombreDe(List<KeyValuePair<string, string>> cabeceras)
        {
            string info = Conexion.Cabecera(cabeceras, "X-AV-Client-Info") ?? "";
            Match m = ModeloSamsung.Match(info);
            if (m.Success) return "Tele Samsung " + m.Groups[1].Value.Trim();
            string agente = (Conexion.Cabecera(cabeceras, "User-Agent") ?? "").Trim();
            int sec = agente.IndexOf("SEC_HHP_", StringComparison.Ordinal);
            if (sec >= 0)
            {
                string resto = agente.Substring(sec + "SEC_HHP_".Length);
                int barra = resto.IndexOf('/');
                if (barra > 0) resto = resto.Substring(0, barra);
                resto = resto.Replace("[TV]", "").Trim();
                return resto == "" ? "Tele Samsung" : "Tele " + (resto.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase) ? resto : "Samsung " + resto);
            }
            if (agente == "") return "Aparato sin nombre";
            foreach (string palabra in agente.Split(' '))
            {
                if (palabra.StartsWith("DLNADOC", StringComparison.OrdinalIgnoreCase) || palabra.StartsWith("UPnP", StringComparison.OrdinalIgnoreCase)) continue;
                int barra = palabra.IndexOf('/');
                string nombre = barra > 0 ? palabra.Substring(0, barra) : palabra;
                if (nombre.Length > 1) return Py.Recortar(nombre, 40);
            }
            return Py.Recortar(agente, 40);
        }

        /// <summary>
        /// Si el aparato puede ver MiCine. Si es la primera vez que pide la lista de películas,
        /// se apunta como pendiente y se avisa en la ventana.
        /// </summary>
        public string EstadoDe(IPAddress ip, List<KeyValuePair<string, string>> cabeceras, bool apuntar)
        {
            string clave = ClaveDe(ip);
            Aparato nuevo = null;
            string estado;
            bool cambiado = false;
            lock (cerrojo)
            {
                var aparatos = datos.ObjetoDe("aparatos");
                var a = aparatos.ObjetoDe(clave);
                if (a == null)
                {
                    if (!apuntar) return PENDIENTE;
                    a = new Objeto { { "nombre", NombreDe(cabeceras) }, { "ip", ip.ToString() }, { "estado", PENDIENTE }, { "visto", Py.AhoraEntero() } };
                    aparatos[clave] = a;
                    nuevo = new Aparato { Clave = clave, Nombre = (string)a["nombre"], Ip = ip.ToString(), Estado = PENDIENTE };
                    cambiado = true;
                }
                else if (Py.Str(a.Obtener("ip", "")) != ip.ToString())
                {
                    a["ip"] = ip.ToString();
                    cambiado = true;
                }
                a["visto"] = Py.AhoraEntero();
                estado = Py.Str(a.Obtener("estado", PENDIENTE));
            }
            if (cambiado) { Guardar(); Avisar(); }
            if (nuevo != null)
            {
                Registro.Anotar("«" + nuevo.Nombre + "» (" + nuevo.Ip + ") quiere ver MiCine por DLNA: dale permiso en la ventana de MiCine");
                var n = NuevoAparato;
                if (n != null) { try { n(nuevo); } catch (Exception) { } }
            }
            return estado;
        }

        public List<Aparato> ListaAparatos()
        {
            var lista = new List<Aparato>();
            lock (cerrojo)
            {
                foreach (var kv in datos.ObjetoDe("aparatos"))
                {
                    var a = kv.Value as Objeto;
                    if (a == null) continue;
                    lista.Add(new Aparato
                    {
                        Clave = kv.Key, Nombre = Py.Str(a.Obtener("nombre", kv.Key)), Ip = Py.Str(a.Obtener("ip", "")),
                        Estado = Py.Str(a.Obtener("estado", PENDIENTE)), Visto = Py.EsNumero(a.Obtener("visto", null)) ? (long)Py.ANumero(a["visto"]) : 0,
                    });
                }
            }
            // Primero los que esperan permiso; luego, los vistos hace menos.
            return lista.OrderBy(a => a.Estado == PENDIENTE ? 0 : 1).ThenByDescending(a => a.Visto).ToList();
        }

        public void CambiarEstado(string clave, string estado)
        {
            string nombre;
            lock (cerrojo)
            {
                var a = datos.ObjetoDe("aparatos").ObjetoDe(clave);
                if (a == null) return;
                a["estado"] = estado;
                nombre = Py.Str(a.Obtener("nombre", clave));
            }
            Guardar();
            Registro.Anotar(estado == PERMITIDO ? "«" + nombre + "» ya puede ver MiCine por DLNA" : "«" + nombre + "» ya no puede ver MiCine por DLNA");
            Avisar();
        }

        public void Olvidar(string clave)
        {
            lock (cerrojo) datos.ObjetoDe("aparatos").Quitar(clave);
            Guardar();
            Avisar();
        }

        /// <summary>Por dónde va cada película en ese aparato (X_SetBookmark de las Samsung).</summary>
        internal void GuardarMarca(IPAddress ip, string ruta, long segundos)
        {
            string clave = ClaveDe(ip);
            lock (cerrojo)
            {
                var a = datos.ObjetoDe("aparatos").ObjetoDe(clave);
                if (a == null) return;
                var marcas = a.ObjetoDe("marcas");
                if (marcas == null) a["marcas"] = marcas = new Objeto();
                if (segundos <= 0) marcas.Quitar(ruta);
                else marcas[ruta] = segundos;
                while (marcas.Count > 300) marcas.Quitar(marcas.Claves[0]);
            }
            Guardar();
        }

        internal long Marca(string claveAparato, string ruta)
        {
            lock (cerrojo)
            {
                var a = datos.ObjetoDe("aparatos").ObjetoDe(claveAparato);
                var marcas = a == null ? null : a.ObjetoDe("marcas");
                object v = marcas == null ? null : marcas.Obtener(ruta, null);
                return Py.EsNumero(v) ? (long)Py.ANumero(v) : 0;
            }
        }

        internal string ClaveAparato(IPAddress ip) { return ClaveDe(ip); }

        internal string NombreAparato(IPAddress ip)
        {
            string clave = ClaveDe(ip);
            lock (cerrojo)
            {
                var a = datos.ObjetoDe("aparatos").ObjetoDe(clave);
                return a == null ? ip.ToString() : Py.Str(a.Obtener("nombre", ip.ToString()));
            }
        }

        // ── catálogo ─────────────────────────────────────────────────────

        internal sealed class Catalogo
        {
            public List<Objeto> Peliculas = new List<Objeto>();
            public List<Objeto> Recientes = new List<Objeto>();
            public Dictionary<string, Objeto> PorId = new Dictionary<string, Objeto>(StringComparer.Ordinal);
            public List<KeyValuePair<string, string>> Carpetas = new List<KeyValuePair<string, string>>(); // id → nombre
            public Dictionary<string, List<Objeto>> EnCarpeta = new Dictionary<string, List<Objeto>>(StringComparer.Ordinal);
        }

        readonly object cerrojoCatalogo = new object();
        Catalogo catalogo;
        DateTime catalogoHora;
        string firma;
        long actualizacion = Py.AhoraEntero();

        /// <summary>SystemUpdateID: cambia cuando cambian las películas, para que la tele no use una lista vieja.</summary>
        internal long Actualizacion { get { return Interlocked.Read(ref actualizacion); } }

        internal static string IdDe(string prefijo, string texto) { return prefijo + Cripto.Sha256Hex(texto).Substring(0, 16); }

        internal Catalogo Actual()
        {
            lock (cerrojoCatalogo)
            {
                if (catalogo != null && (DateTime.UtcNow - catalogoHora).TotalSeconds < 15) return catalogo;
                var c = new Catalogo();
                c.Peliculas = Servidor.Biblioteca.Listar();
                var carpetas = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in c.Peliculas)
                {
                    c.PorId[IdDe("i", (string)p["ruta"])] = p;
                    string carpeta = Py.Str(p.Obtener("carpeta", ""));
                    if (carpeta == "") continue;
                    string id;
                    if (!carpetas.TryGetValue(carpeta, out id))
                    {
                        id = IdDe("c", carpeta);
                        carpetas[carpeta] = id;
                        c.EnCarpeta[id] = new List<Objeto>();
                    }
                    c.EnCarpeta[id].Add(p);
                }
                foreach (string nombre in Py.Ordenados(carpetas.Keys)) c.Carpetas.Add(new KeyValuePair<string, string>(carpetas[nombre], nombre));
                c.Recientes = c.Peliculas.OrderByDescending(p => Py.ANumero(p["fecha"])).Take(20).ToList();
                string nueva = c.Peliculas.Count + "|" + c.Peliculas.Sum(p => Py.ANumero(p["tamano"])) + "|" +
                    string.Join("|", c.Peliculas.Select(p => (string)p["ruta"]));
                nueva = Cripto.Sha256Hex(nueva);
                if (firma != null && firma != nueva) Interlocked.Exchange(ref actualizacion, Math.Max(Py.AhoraEntero(), Actualizacion + 1));
                firma = nueva;
                catalogo = c;
                catalogoHora = DateTime.UtcNow;
                Sondear(c.Peliculas);
                return c;
            }
        }

        // Para decidir qué hay que convertir hace falta saber qué lleva cada archivo: se mira en segundo
        // plano, de uno en uno, para que la tele no espere al pedir la lista.
        readonly Queue<string> porSondear = new Queue<string>();
        readonly HashSet<string> enCola = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sondeando;

        void Sondear(List<Objeto> peliculas)
        {
            var conversor = Servidor.Conversor;
            if (conversor == null) return;
            lock (porSondear)
            {
                foreach (var p in peliculas)
                {
                    string ruta = Servidor.Biblioteca.Resolver((string)p["ruta"]);
                    if (ruta == null || enCola.Contains(ruta) || conversor.InfoGuardada(ruta) != null) continue;
                    enCola.Add(ruta);
                    porSondear.Enqueue(ruta);
                }
                if (sondeando || porSondear.Count == 0) return;
                sondeando = true;
            }
            var hilo = new Thread(delegate ()
            {
                while (activo)
                {
                    string ruta;
                    lock (porSondear)
                    {
                        if (porSondear.Count == 0)
                        {
                            sondeando = false;
                            // Ya se sabe qué hay que convertir: la tele debe volver a pedir la lista.
                            Interlocked.Exchange(ref actualizacion, Math.Max(Py.AhoraEntero(), Actualizacion + 1));
                            return;
                        }
                        ruta = porSondear.Dequeue();
                        enCola.Remove(ruta);
                    }
                    try { conversor.Info(ruta); }
                    catch (Exception) { }
                }
                lock (porSondear) sondeando = false;
            }) { IsBackground = true, Name = "MiCine: DLNA mira los vídeos", Priority = ThreadPriority.BelowNormal };
            hilo.Start();
        }

        // ── qué sabe reproducir la tele ──────────────────────────────────

        // Lo que leen las teles Samsung de 2012-2014 (y casi cualquier tele DLNA) sin ayuda. H.265, VP9,
        // AV1 o el H.264 de 10 bits no: se convierten. El DTS, por si acaso, también.
        static readonly HashSet<string> VideoTele = new HashSet<string>(StringComparer.Ordinal)
        {
            "h264", "mpeg4", "msmpeg4v1", "msmpeg4v2", "msmpeg4v3", "mpeg2video", "mpeg1video",
            "vc1", "wmv1", "wmv2", "wmv3", "h263", "flv1", "vp8", "mjpeg",
        };
        static readonly HashSet<string> AudioTele = new HashSet<string>(StringComparer.Ordinal)
        {
            "aac", "ac3", "eac3", "mp3", "mp2", "mp1", "wmav1", "wmav2", "wmapro",
            "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_u8", "adpcm_ms", "adpcm_ima_wav",
        };
        static readonly HashSet<string> CajasTele = new HashSet<string>(StringComparer.Ordinal)
        {
            ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".m2ts", ".3gp", ".flv", ".webm",
        };
        // Imagen que se puede copiar tal cual al MPEG-TS de la conversión.
        static readonly HashSet<string> CopiableTs = new HashSet<string>(StringComparer.Ordinal) { "h264", "mpeg2video" };

        internal static Objeto AudioPorDefecto(Objeto info)
        {
            var audios = info == null ? null : info["audios"] as List<object>;
            if (audios == null || audios.Count == 0) return null;
            foreach (Objeto a in audios) if (Py.Verdad(a["defecto"])) return a;
            return (Objeto)audios[0];
        }

        /// <summary>directo (el archivo tal cual), audio (se convierte el sonido) o video (se convierte todo).</summary>
        internal string Modo(string ruta, Objeto info)
        {
            if (Servidor.Conversor == null || info == null) return "directo";
            var video = info["video"] as Objeto;
            var audio = AudioPorDefecto(info);
            string codec = video == null ? "" : Py.Str(video["codec"]);
            bool imagenVale = video == null || (VideoTele.Contains(codec) && !(codec == "h264" && Py.ANumero(video["bits"]) > 8)
                && Py.ANumero(video["ancho"]) <= 1920 && Py.ANumero(video["alto"]) <= 1088);
            bool sonidoVale = audio == null || AudioTele.Contains(Py.Str(audio["codec"]));
            bool cajaVale = CajasTele.Contains(Py.Lower(Py.Sufijo(Path.GetFileName(ruta))));
            if (imagenVale && sonidoVale && cajaVale) return "directo";
            if (video == null || (imagenVale && CopiableTs.Contains(codec))) return "audio";
            return "video";
        }

        internal static string Mime(string ext, bool samsung)
        {
            switch (ext)
            {
                case ".mkv": return samsung ? "video/x-mkv" : "video/x-matroska";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".avi": return "video/x-msvideo";
                case ".wmv": return "video/x-ms-wmv";
                case ".mpg": case ".mpeg": return "video/mpeg";
                case ".m2ts": return "video/vnd.dlna.mpeg-tts";
                case ".flv": return "video/x-flv";
                case ".3gp": return "video/3gpp";
                case ".webm": return "video/webm";
                case ".ogv": return "video/ogg";
            }
            return "application/octet-stream";
        }

        // ── SSDP: el anuncio en la red ───────────────────────────────────

        Socket ssdp;
        IPAddress enlace;
        volatile bool activo;
        readonly ManualResetEvent parar = new ManualResetEvent(false);
        readonly object envio = new object();
        readonly HashSet<string> unidos = new HashSet<string>();

        /// <summary>Empieza a anunciarse. Devuelve null si va bien o el motivo si no.</summary>
        public string Arrancar()
        {
            string dir = Servidor.Direccion;
            enlace = string.IsNullOrEmpty(dir) || dir == "0.0.0.0" ? IPAddress.Any : null;
            if (enlace == null)
            {
                IPAddress ip;
                if (!IPAddress.TryParse(dir, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || !EsDeCasa(ip))
                    return "DLNA desactivado: solo funciona si MiCine escucha en la red de casa (\"direccion\": \"0.0.0.0\" en config.json)";
                enlace = ip;
            }
            try
            {
                ssdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Windows ya usa el 1900 para lo suyo: se comparte.
                ssdp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                ssdp.Bind(new IPEndPoint(enlace, PUERTO_SSDP));
                ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                UnirseAlGrupo();
            }
            catch (Exception e)
            {
                try { if (ssdp != null) ssdp.Close(); } catch (Exception) { }
                ssdp = null;
                return "No se pudo poner en marcha DLNA (puerto 1900): " + e.Message;
            }
            activo = true;
            new Thread(Escuchar) { IsBackground = true, Name = "MiCine: DLNA escucha" }.Start();
            new Thread(Anunciar) { IsBackground = true, Name = "MiCine: DLNA anuncia" }.Start();
            // Se empieza ya a mirar qué lleva cada vídeo, para tenerlo cuando la tele pida la lista.
            ThreadPool.QueueUserWorkItem(delegate { try { Actual(); } catch (Exception) { } });
            return null;
        }

        public void Parar()
        {
            if (!activo) return;
            activo = false;
            parar.Set();
            try { EnviarAnuncios("ssdp:byebye"); } catch (Exception) { }
            try { ssdp.Close(); } catch (Exception) { }
        }

        /// <summary>Las IP de este ordenador en la red de casa, por las que se anuncia MiCine.</summary>
        List<IPAddress> Interfaces()
        {
            if (!enlace.Equals(IPAddress.Any)) return new List<IPAddress> { enlace };
            var lista = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && EsDeCasa(u.Address) && !IPAddress.IsLoopback(u.Address))
                            lista.Add(u.Address);
                }
            }
            catch (Exception) { }
            return lista;
        }

        void UnirseAlGrupo()
        {
            foreach (var ip in Interfaces())
            {
                if (unidos.Contains(ip.ToString())) continue;
                try
                {
                    ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse(GRUPO), ip));
                    unidos.Add(ip.ToString());
                }
                catch (SocketException) { } // esa red no admite multidifusión
            }
        }

        string Ubicacion(IPAddress ip) { return "http://" + ip + ":" + Servidor.Puerto + "/dlna/descripcion.xml"; }

        IEnumerable<KeyValuePair<string, string>> Anunciables()
        {
            // (NT o ST, USN)
            yield return new KeyValuePair<string, string>("upnp:rootdevice", "uuid:" + Udn + "::upnp:rootdevice");
            yield return new KeyValuePair<string, string>("uuid:" + Udn, "uuid:" + Udn);
            yield return new KeyValuePair<string, string>(DISPOSITIVO, "uuid:" + Udn + "::" + DISPOSITIVO);
            yield return new KeyValuePair<string, string>(SERVICIO_CD, "uuid:" + Udn + "::" + SERVICIO_CD);
            yield return new KeyValuePair<string, string>(SERVICIO_CM, "uuid:" + Udn + "::" + SERVICIO_CM);
        }

        void Anunciar()
        {
            int primeros = 3;
            while (activo)
            {
                try
                {
                    lock (envio) UnirseAlGrupo();
                    EnviarAnuncios("ssdp:alive");
                }
                catch (Exception) { }
                if (parar.WaitOne(primeros-- > 0 ? 2000 : CADA_CUANTO_ANUNCIAR * 1000)) return;
            }
        }

        void EnviarAnuncios(string nts)
        {
            var destino = new IPEndPoint(IPAddress.Parse(GRUPO), PUERTO_SSDP);
            foreach (var ip in Interfaces())
            {
                foreach (var t in Anunciables())
                {
                    string m = "NOTIFY * HTTP/1.1\r\nHOST: " + GRUPO + ":" + PUERTO_SSDP + "\r\n" +
                        (nts == "ssdp:alive" ? "CACHE-CONTROL: max-age=" + VIGENCIA + "\r\nLOCATION: " + Ubicacion(ip) + "\r\nSERVER: " + Firma + "\r\n" : "") +
                        "NT: " + t.Key + "\r\nNTS: " + nts + "\r\nUSN: " + t.Value + "\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(m);
                    lock (envio)
                    {
                        try
                        {
                            ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, BitConverter.ToInt32(ip.GetAddressBytes(), 0));
                            ssdp.SendTo(bytes, destino);
                        }
                        catch (SocketException) { }
                    }
                }
            }
        }

        void Escuchar()
        {
            var buffer = new byte[4096];
            while (activo)
            {
                EndPoint remoto = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = ssdp.ReceiveFrom(buffer, ref remoto); }
                catch (SocketException) { if (!activo) return; continue; }
                catch (ObjectDisposedException) { return; }
                var quien = (IPEndPoint)remoto;
                if (n <= 0 || !EsDeCasa(quien.Address)) continue;
                string texto = Encoding.ASCII.GetString(buffer, 0, n);
                if (!texto.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
                Responder(texto, quien);
            }
        }

        static string CabeceraSsdp(string texto, string nombre)
        {
            foreach (string linea in texto.Split('\n'))
            {
                int dp = linea.IndexOf(':');
                if (dp > 0 && string.Equals(linea.Substring(0, dp).Trim(), nombre, StringComparison.OrdinalIgnoreCase))
                    return linea.Substring(dp + 1).Trim();
            }
            return null;
        }

        /// <summary>Contesta a quien busca servidores (la tele, al abrir la lista de fuentes).</summary>
        void Responder(string busqueda, IPEndPoint quien)
        {
            if (!string.Equals((CabeceraSsdp(busqueda, "MAN") ?? "").Trim('"'), "ssdp:discover", StringComparison.OrdinalIgnoreCase)) return;
            string st = CabeceraSsdp(busqueda, "ST") ?? "";
            var respuestas = Anunciables().Where(t => st == "ssdp:all" || string.Equals(t.Key, st, StringComparison.OrdinalIgnoreCase)).ToList();
            if (respuestas.Count == 0) return;
            IPAddress local = enlace.Equals(IPAddress.Any) ? IpHacia(quien.Address) : enlace;
            if (local == null || !EsDeCasa(local)) return;
            int mx;
            if (!int.TryParse(CabeceraSsdp(busqueda, "MX") ?? "1", out mx)) mx = 1;
            int espera;
            lock (azar) espera = azar.Next(0, Math.Max(1, Math.Min(mx, 2)) * 400);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(espera);
                foreach (var t in respuestas)
                {
                    string m = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=" + VIGENCIA + "\r\nDATE: " + DateTime.UtcNow.ToString("r", Py.Inv) +
                        "\r\nEXT:\r\nLOCATION: " + Ubicacion(local) + "\r\nSERVER: " + Firma + "\r\nST: " + t.Key + "\r\nUSN: " + t.Value +
                        "\r\nContent-Length: 0\r\n\r\n";
                    lock (envio)
                    {
                        try { if (activo) ssdp.SendTo(Encoding.ASCII.GetBytes(m), quien); }
                        catch (Exception) { }
                    }
                }
            });
        }

        /// <summary>La IP de este ordenador por la que se llega a ese aparato.</summary>
        static IPAddress IpHacia(IPAddress remoto)
        {
            if (IPAddress.IsLoopback(remoto)) return IPAddress.Loopback;
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(remoto, 9);
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch (Exception) { return null; }
        }

        // ── el icono que enseña la tele ──────────────────────────────────

        static byte[] icono;

        internal static byte[] Icono()
        {
            if (icono != null) return icono;
            using (var ico = Tema.Icono(128))
            using (var original = ico.ToBitmap())
            using (var bmp = new Bitmap(120, 120, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var m = new MemoryStream())
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                g.DrawImage(original, 0, 0, 120, 120);
                bmp.Save(m, ImageFormat.Png);
                icono = m.ToArray();
            }
            return icono;
        }
    }

    /// <summary>
    /// Una petición DLNA: la descripción del servidor, la lista de películas (SOAP) y los vídeos.
    /// Va aparte del Manejador de la web porque las teles no tienen sesión ni cabecera X-MiCine:
    /// lo que manda aquí es el permiso de cada aparato.
    /// </summary>
    public sealed class PeticionDlna
    {
        public static readonly string[] Metodos = { "GET", "HEAD", "POST", "SUBSCRIBE", "UNSUBSCRIBE" };
        const string DIDL = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\" xmlns:sec=\"http://www.sec.co.kr/\">";
        const string RASGOS_ARCHIVO = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        const string RASGOS_CONVERTIDO = "DLNA.ORG_OP=10;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        const int TROZO = 1024 * 1024;

        readonly Conexion con;
        readonly Dlna dlna;
        readonly string metodo, rutaPeticion;
        readonly List<KeyValuePair<string, string>> cabeceras;
        public bool CuerpoLeido;

        public PeticionDlna(Conexion con, Dlna dlna, string metodo, string ruta, List<KeyValuePair<string, string>> cabeceras)
        {
            this.con = con;
            this.dlna = dlna;
            this.metodo = metodo;
            rutaPeticion = ruta;
            this.cabeceras = cabeceras;
            CuerpoLeido = metodo != "POST";
        }

        string Cabecera(string nombre) { return Conexion.Cabecera(cabeceras, nombre); }

        bool Samsung
        {
            get
            {
                string agente = (Cabecera("User-Agent") ?? "") + " " + (Cabecera("X-AV-Client-Info") ?? "");
                return agente.IndexOf("SEC_HHP", StringComparison.OrdinalIgnoreCase) >= 0 || agente.IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        string Base { get { return "http://" + (con.IpLocal.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + con.IpLocal + "]" : con.IpLocal.ToString()) + ":" + con.Servidor.Puerto; } }

        static string X(string texto)
        {
            var sb = new StringBuilder(texto.Length + 16);
            foreach (char c in texto)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        static List<KeyValuePair<string, string>> Lista(params string[] pares)
        {
            var l = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pares.Length; i += 2) l.Add(new KeyValuePair<string, string>(pares[i], pares[i + 1]));
            return l;
        }

        void Enviar(int codigo, string tipo, byte[] cuerpo, List<KeyValuePair<string, string>> extra)
        {
            var h = Lista("Content-Type", tipo, "Content-Length", cuerpo.Length.ToString(Py.Inv));
            if (extra != null) h.AddRange(extra);
            con.Enviar(codigo, h, cuerpo);
        }

        void EnviarXml(int codigo, string xml, List<KeyValuePair<string, string>> extra)
        {
            Enviar(codigo, "text/xml; charset=\"utf-8\"", Py.Utf8.GetBytes(xml), extra);
        }

        void Texto(int codigo, string texto)
        {
            Enviar(codigo, "text/plain; charset=utf-8", Py.Utf8.GetBytes(texto), null);
        }

        public void Atender()
        {
            try
            {
                string ruta = rutaPeticion;
                int q = ruta.IndexOf('?');
                if (q >= 0) ruta = ruta.Substring(0, q);
                // Solo la red de casa: nada que llegue de internet ni por un túnel.
                if (!Dlna.EsDeCasa(con.IpRemota) || Manejador.PorTunel(con, cabeceras)) { con.Cerrar = true; Texto(403, "Solo para la red de casa"); return; }
                if (dlna == null) { con.Cerrar = true; Texto(404, "DLNA desactivado"); return; }

                if (metodo == "SUBSCRIBE" || metodo == "UNSUBSCRIBE") { Suscripcion(ruta); return; }
                if (metodo == "POST")
                {
                    if (ruta == "/dlna/control/cd") { Control(Dlna.SERVICIO_CD); return; }
                    if (ruta == "/dlna/control/cm") { Control(Dlna.SERVICIO_CM); return; }
                    con.Cerrar = true;
                    Texto(404, "No encontrado");
                    return;
                }
                switch (ruta)
                {
                    case "/dlna/descripcion.xml": EnviarXml(200, Descripcion(), null); return;
                    case "/dlna/cd.xml": EnviarXml(200, ScpdContenido, null); return;
                    case "/dlna/cm.xml": EnviarXml(200, ScpdConexiones, null); return;
                    case "/dlna/icono.png": Enviar(200, "image/png", Dlna.Icono(), Lista("Cache-Control", "max-age=86400")); return;
                }
                if (ruta.StartsWith("/dlna/v/", StringComparison.Ordinal)) { Video(Id(ruta, "/dlna/v/")); return; }
                if (ruta.StartsWith("/dlna/s/", StringComparison.Ordinal)) { Subtitulo(Id(ruta, "/dlna/s/")); return; }
                if (ruta.StartsWith("/dlna/p/", StringComparison.Ordinal)) { Caratula(Id(ruta, "/dlna/p/")); return; }
                Texto(404, "No encontrado");
            }
            catch (Desconectado) { con.Cerrar = true; }
            catch (ErrorPeticion e)
            {
                con.Cerrar = true;
                try { Texto(e.Codigo, e.Message); } catch (Exception) { }
            }
            catch (Exception e)
            {
                Registro.Error("DLNA " + metodo + " " + rutaPeticion, e);
                con.Cerrar = true;
                if (!con.CabecerasEnviadas) { try { Texto(500, "Error interno del servidor"); } catch (Exception) { } }
            }
        }

        /// <summary>«/dlna/v/i0123abcd.mkv» → «i0123abcd».</summary>
        static string Id(string ruta, string prefijo)
        {
            string id = ruta.Substring(prefijo.Length);
            int punto = id.IndexOf('.');
            return punto >= 0 ? id.Substring(0, punto) : id;
        }

        // ── descripción del servidor ─────────────────────────────────────

        string Descripcion()
        {
            string servicio = "<service><serviceType>{0}</serviceType><serviceId>urn:upnp-org:serviceId:{1}</serviceId>" +
                "<controlURL>/dlna/control/{2}</controlURL><eventSubURL>/dlna/eventos/{2}</eventSubURL><SCPDURL>/dlna/{2}.xml</SCPDURL></service>";
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<root xmlns=\"urn:schemas-upnp-org:device-1-0\" xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\" xmlns:sec=\"http://www.sec.co.kr/dlna\">" +
                "<specVersion><major>1</major><minor>0</minor></specVersion><device>" +
                "<deviceType>" + Dlna.DISPOSITIVO + "</deviceType>" +
                "<friendlyName>" + X(Dlna.NOMBRE) + "</friendlyName>" +
                "<manufacturer>MiCine</manufacturer>" +
                "<modelDescription>Servidor de películas MiCine</modelDescription>" +
                "<modelName>Windows Media Connect compatible (MiCine)</modelName>" +
                "<modelNumber>1</modelNumber>" +
                "<serialNumber>" + X(dlna.Udn.Substring(0, 8)) + "</serialNumber>" +
                "<UDN>uuid:" + X(dlna.Udn) + "</UDN>" +
                "<dlna:X_DLNADOC>DMS-1.50</dlna:X_DLNADOC>" +
                "<sec:ProductCap>smi,DCM10,getMediaInfo.sec,getCaptionInfo.sec</sec:ProductCap>" +
                "<sec:X_ProductCap>smi,DCM10,getMediaInfo.sec,getCaptionInfo.sec</sec:X_ProductCap>" +
                "<presentationURL>" + X(Base) + "/</presentationURL>" +
                "<iconList><icon><mimetype>image/png</mimetype><width>120</width><height>120</height><depth>24</depth><url>/dlna/icono.png</url></icon></iconList>" +
                "<serviceList>" +
                string.Format(Py.Inv, servicio, Dlna.SERVICIO_CD, "ContentDirectory", "cd") +
                string.Format(Py.Inv, servicio, Dlna.SERVICIO_CM, "ConnectionManager", "cm") +
                "</serviceList></device></root>";
        }

        static string Accion(string nombre, params string[] argumentos)
        {
            var sb = new StringBuilder("<action><name>" + nombre + "</name><argumentList>");
            foreach (string a in argumentos)
            {
                string[] p = a.Split(':'); // dirección:nombre:variable
                sb.Append("<argument><name>").Append(p[1]).Append("</name><direction>").Append(p[0]).Append("</direction><relatedStateVariable>")
                  .Append(p[2]).Append("</relatedStateVariable></argument>");
            }
            return sb.Append("</argumentList></action>").ToString();
        }

        static string Variable(string nombre, string tipo, bool eventos, params string[] valores)
        {
            var sb = new StringBuilder("<stateVariable sendEvents=\"" + (eventos ? "yes" : "no") + "\"><name>" + nombre + "</name><dataType>" + tipo + "</dataType>");
            if (valores.Length > 0)
            {
                sb.Append("<allowedValueList>");
                foreach (string v in valores) sb.Append("<allowedValue>").Append(v).Append("</allowedValue>");
                sb.Append("</allowedValueList>");
            }
            return sb.Append("</stateVariable>").ToString();
        }

        const string SCPD = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>";

        static readonly string ScpdContenido = SCPD + "<actionList>" +
            Accion("GetSearchCapabilities", "out:SearchCaps:SearchCapabilities") +
            Accion("GetSortCapabilities", "out:SortCaps:SortCapabilities") +
            Accion("GetSystemUpdateID", "out:Id:SystemUpdateID") +
            Accion("Browse", "in:ObjectID:A_ARG_TYPE_ObjectID", "in:BrowseFlag:A_ARG_TYPE_BrowseFlag", "in:Filter:A_ARG_TYPE_Filter",
                "in:StartingIndex:A_ARG_TYPE_Index", "in:RequestedCount:A_ARG_TYPE_Count", "in:SortCriteria:A_ARG_TYPE_SortCriteria",
                "out:Result:A_ARG_TYPE_Result", "out:NumberReturned:A_ARG_TYPE_Count", "out:TotalMatches:A_ARG_TYPE_Count", "out:UpdateID:A_ARG_TYPE_UpdateID") +
            Accion("X_GetFeatureList", "out:FeatureList:A_ARG_TYPE_Featurelist") +
            Accion("X_SetBookmark", "in:CategoryType:A_ARG_TYPE_CategoryType", "in:RID:A_ARG_TYPE_RID", "in:ObjectID:A_ARG_TYPE_ObjectID", "in:PosSecond:A_ARG_TYPE_PosSec") +
            "</actionList><serviceStateTable>" +
            Variable("A_ARG_TYPE_BrowseFlag", "string", false, "BrowseMetadata", "BrowseDirectChildren") +
            Variable("SystemUpdateID", "ui4", true) + Variable("ContainerUpdateIDs", "string", true) +
            Variable("A_ARG_TYPE_Count", "ui4", false) + Variable("A_ARG_TYPE_SortCriteria", "string", false) +
            Variable("SortCapabilities", "string", false) + Variable("A_ARG_TYPE_Index", "ui4", false) +
            Variable("A_ARG_TYPE_ObjectID", "string", false) + Variable("A_ARG_TYPE_UpdateID", "ui4", false) +
            Variable("A_ARG_TYPE_Result", "string", false) + Variable("SearchCapabilities", "string", false) +
            Variable("A_ARG_TYPE_Filter", "string", false) + Variable("A_ARG_TYPE_Featurelist", "string", false) +
            Variable("A_ARG_TYPE_CategoryType", "ui4", false) + Variable("A_ARG_TYPE_RID", "ui4", false) + Variable("A_ARG_TYPE_PosSec", "ui4", false) +
            "</serviceStateTable></scpd>";

        static readonly string ScpdConexiones = SCPD + "<actionList>" +
            Accion("GetProtocolInfo", "out:Source:SourceProtocolInfo", "out:Sink:SinkProtocolInfo") +
            Accion("GetCurrentConnectionIDs", "out:ConnectionIDs:CurrentConnectionIDs") +
            Accion("GetCurrentConnectionInfo", "in:ConnectionID:A_ARG_TYPE_ConnectionID", "out:RcsID:A_ARG_TYPE_RcsID",
                "out:AVTransportID:A_ARG_TYPE_AVTransportID", "out:ProtocolInfo:A_ARG_TYPE_ProtocolInfo",
                "out:PeerConnectionManager:A_ARG_TYPE_ConnectionManager", "out:PeerConnectionID:A_ARG_TYPE_ConnectionID",
                "out:Direction:A_ARG_TYPE_Direction", "out:Status:A_ARG_TYPE_ConnectionStatus") +
            "</actionList><serviceStateTable>" +
            Variable("SourceProtocolInfo", "string", true) + Variable("SinkProtocolInfo", "string", true) + Variable("CurrentConnectionIDs", "string", true) +
            Variable("A_ARG_TYPE_ConnectionStatus", "string", false, "OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown") +
            Variable("A_ARG_TYPE_ConnectionManager", "string", false) + Variable("A_ARG_TYPE_Direction", "string", false, "Input", "Output") +
            Variable("A_ARG_TYPE_ProtocolInfo", "string", false) + Variable("A_ARG_TYPE_ConnectionID", "i4", false) +
            Variable("A_ARG_TYPE_AVTransportID", "i4", false) + Variable("A_ARG_TYPE_RcsID", "i4", false) +
            "</serviceStateTable></scpd>";

        static readonly string Protocolos = string.Join(",", new[]
        {
            "video/x-matroska", "video/x-mkv", "video/mp4", "video/quicktime", "video/x-msvideo", "video/x-ms-wmv", "video/mpeg",
            "video/vnd.dlna.mpeg-tts", "video/x-flv", "video/3gpp", "video/webm", "text/srt", "image/jpeg", "image/png",
        }.Select(m => "http-get:*:" + m + ":*"));

        // ── SOAP ─────────────────────────────────────────────────────────

        static string Sobre(string contenido)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" + contenido + "</s:Body></s:Envelope>";
        }

        void Respuesta(string servicio, string accion, params string[] pares)
        {
            var sb = new StringBuilder("<u:" + accion + "Response xmlns:u=\"" + servicio + "\">");
            for (int i = 0; i + 1 < pares.Length; i += 2)
                sb.Append('<').Append(pares[i]).Append('>').Append(X(pares[i + 1])).Append("</").Append(pares[i]).Append('>');
            sb.Append("</u:").Append(accion).Append("Response>");
            EnviarXml(200, Sobre(sb.ToString()), Lista("EXT", ""));
        }

        void Fallo(int codigo, string descripcion)
        {
            EnviarXml(500, Sobre("<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + codigo + "</errorCode><errorDescription>" +
                X(descripcion) + "</errorDescription></UPnPError></detail></s:Fault>"), Lista("EXT", ""));
        }

        void Control(string servicio)
        {
            long largo;
            if (!Py.Entero(Cabecera("Content-Length") ?? "", out largo) || largo < 0 || largo > 64 * 1024)
            {
                con.Cerrar = true;
                Texto(411, "Falta Content-Length");
                return;
            }
            byte[] cuerpo = con.LeerExacto((int)largo);
            CuerpoLeido = true;

            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                var ajustes = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1000000 };
                using (var lector = XmlReader.Create(new MemoryStream(cuerpo), ajustes)) doc.Load(lector);
            }
            catch (Exception) { Fallo(401, "Invalid Action"); return; }
            XmlElement accion = null;
            foreach (XmlNode n in doc.GetElementsByTagName("Body", "http://schemas.xmlsoap.org/soap/envelope/"))
                foreach (XmlNode hijo in n.ChildNodes)
                    if (hijo is XmlElement) { accion = (XmlElement)hijo; break; }
            if (accion == null) { Fallo(401, "Invalid Action"); return; }
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode a in accion.ChildNodes) if (a is XmlElement) args[a.LocalName] = a.InnerText;
            string nombre = accion.LocalName;

            if (servicio == Dlna.SERVICIO_CM)
            {
                switch (nombre)
                {
                    case "GetProtocolInfo": Respuesta(servicio, nombre, "Source", Protocolos, "Sink", ""); return;
                    case "GetCurrentConnectionIDs": Respuesta(servicio, nombre, "ConnectionIDs", "0"); return;
                    case "GetCurrentConnectionInfo":
                        Respuesta(servicio, nombre, "RcsID", "-1", "AVTransportID", "-1", "ProtocolInfo", "", "PeerConnectionManager", "",
                            "PeerConnectionID", "-1", "Direction", "Output", "Status", "OK");
                        return;
                }
                Fallo(401, "Invalid Action");
                return;
            }
            switch (nombre)
            {
                case "GetSearchCapabilities": Respuesta(servicio, nombre, "SearchCaps", ""); return;
                case "GetSortCapabilities": Respuesta(servicio, nombre, "SortCaps", "dc:title"); return;
                case "GetSystemUpdateID": Respuesta(servicio, nombre, "Id", dlna.Actualizacion.ToString(Py.Inv)); return;
                case "X_GetFeatureList":
                    // Las Samsung abren «Vídeos» en el contenedor que se diga aquí.
                    Respuesta(servicio, nombre, "FeatureList", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Features xmlns=\"urn:schemas-upnp-org:av:avs\" " +
                        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:schemas-upnp-org:av:avs http://www.upnp.org/schemas/av/avs.xsd\">" +
                        "<Feature name=\"samsung.com_BASICVIEW\" version=\"1\"><container id=\"v\" type=\"object.item.videoItem\"/></Feature></Features>");
                    return;
                case "X_SetBookmark":
                    Marcar(args);
                    Respuesta(servicio, nombre);
                    return;
                case "Browse": Explorar(args); return;
            }
            Fallo(401, "Invalid Action");
        }

        void Marcar(Dictionary<string, string> args)
        {
            string id, pos;
            long segundos;
            if (!args.TryGetValue("ObjectID", out id) || !args.TryGetValue("PosSecond", out pos) || !Py.Entero(pos, out segundos)) return;
            if (dlna.EstadoDe(con.IpRemota, cabeceras, false) != Dlna.PERMITIDO) return;
            Objeto p;
            if (dlna.Actual().PorId.TryGetValue(id, out p)) dlna.GuardarMarca(con.IpRemota, (string)p["ruta"], Math.Max(0, segundos));
        }

        // ── la lista de películas ────────────────────────────────────────

        static string Arg(Dictionary<string, string> args, string nombre)
        {
            string v;
            return args.TryGetValue(nombre, out v) ? v.Trim() : "";
        }

        static string Contenedor(string id, string padre, string titulo, int hijos)
        {
            return "<container id=\"" + X(id) + "\" parentID=\"" + X(padre) + "\" restricted=\"1\" searchable=\"0\" childCount=\"" + hijos +
                "\"><dc:title>" + X(titulo) + "</dc:title><upnp:class>object.container.storageFolder</upnp:class></container>";
        }

        void Explorar(Dictionary<string, string> args)
        {
            string id = Arg(args, "ObjectID"), bandera = Arg(args, "BrowseFlag");
            long desde, cuantos;
            if (!Py.Entero(Arg(args, "StartingIndex") == "" ? "0" : Arg(args, "StartingIndex"), out desde) || desde < 0) desde = 0;
            if (!Py.Entero(Arg(args, "RequestedCount") == "" ? "0" : Arg(args, "RequestedCount"), out cuantos) || cuantos < 0) cuantos = 0;
            if (bandera != "BrowseMetadata" && bandera != "BrowseDirectChildren") { Fallo(402, "Invalid Args"); return; }
            if (id == "") id = "0";

            string estado = dlna.EstadoDe(con.IpRemota, cabeceras, true);
            var elementos = new List<string>();
            int total;
            if (estado != Dlna.PERMITIDO)
            {
                // Sin permiso, la tele solo ve un aviso de lo que hay que hacer.
                string aviso = estado == Dlna.BLOQUEADO ? "Este aparato no tiene permiso para ver MiCine" : "Dale permiso en la ventana de MiCine del ordenador";
                if (bandera == "BrowseMetadata") elementos.Add(id == "0" ? Contenedor("0", "-1", Dlna.NOMBRE, 1) : Contenedor("aviso", "0", aviso, 0));
                else if (id == "0") elementos.Add(Contenedor("aviso", "0", aviso, 0));
                total = elementos.Count;
                Resultado(elementos, total);
                return;
            }

            var cat = dlna.Actual();
            var contexto = new Contexto { Base = Base, Samsung = Samsung, Aparato = dlna.ClaveAparato(con.IpRemota) };
            if (bandera == "BrowseMetadata")
            {
                Objeto p;
                if (id == "0") elementos.Add(Contenedor("0", "-1", Dlna.NOMBRE, Raiz(cat).Count));
                else if (id == "v") elementos.Add(Contenedor("v", "0", "Todas las películas", cat.Peliculas.Count));
                else if (id == "r") elementos.Add(Contenedor("r", "0", "Añadidas recientemente", cat.Recientes.Count));
                else if (cat.EnCarpeta.ContainsKey(id)) elementos.Add(Contenedor(id, "0", cat.Carpetas.First(c => c.Key == id).Value, cat.EnCarpeta[id].Count));
                else if (cat.PorId.TryGetValue(id, out p)) elementos.Add(Item(p, "v", contexto));
                else { Fallo(701, "No such object"); return; }
                Resultado(elementos, 1);
                return;
            }

            List<string> hijos;
            if (id == "0") hijos = Raiz(cat);
            else
            {
                List<Objeto> peliculas;
                if (id == "v") peliculas = cat.Peliculas;
                else if (id == "r") peliculas = cat.Recientes;
                else if (!cat.EnCarpeta.TryGetValue(id, out peliculas))
                {
                    if (cat.PorId.ContainsKey(id)) { Resultado(elementos, 0); return; } // una película no tiene hijos
                    Fallo(701, "No such object");
                    return;
                }
                total = peliculas.Count;
                var trozo = peliculas.Skip((int)Math.Min(desde, int.MaxValue)).Take(cuantos == 0 ? int.MaxValue : (int)Math.Min(cuantos, int.MaxValue));
                foreach (var p in trozo) elementos.Add(Item(p, id, contexto));
                Resultado(elementos, total);
                return;
            }
            total = hijos.Count;
            elementos.AddRange(hijos.Skip((int)Math.Min(desde, int.MaxValue)).Take(cuantos == 0 ? int.MaxValue : (int)Math.Min(cuantos, int.MaxValue)));
            Resultado(elementos, total);
        }

        static List<string> Raiz(Dlna.Catalogo cat)
        {
            var l = new List<string> { Contenedor("v", "0", "Todas las películas", cat.Peliculas.Count) };
            if (cat.Recientes.Count > 0) l.Add(Contenedor("r", "0", "Añadidas recientemente", cat.Recientes.Count));
            foreach (var c in cat.Carpetas) l.Add(Contenedor(c.Key, "0", c.Value, cat.EnCarpeta[c.Key].Count));
            return l;
        }

        void Resultado(List<string> elementos, int total)
        {
            string didl = DIDL + string.Join("", elementos) + "</DIDL-Lite>";
            Respuesta(Dlna.SERVICIO_CD, "Browse", "Result", didl, "NumberReturned", elementos.Count.ToString(Py.Inv),
                "TotalMatches", total.ToString(Py.Inv), "UpdateID", dlna.Actualizacion.ToString(Py.Inv));
        }

        sealed class Contexto { public string Base, Aparato; public bool Samsung; }

        static string Duracion(double segundos)
        {
            long ms = (long)Math.Round(segundos * 1000);
            return string.Format(Py.Inv, "{0}:{1:00}:{2:00}.{3:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);
        }

        /// <summary>Los subtítulos que se le dan a la tele: un .srt de al lado (mejor en español) o, si se convierte, los de dentro.</summary>
        sealed class Sub { public string Externo; public int Interno = -1; }

        Sub ElegirSubtitulo(Objeto p, Objeto info, string modo)
        {
            var externos = p["subtitulos"] as List<object> ?? new List<object>();
            Objeto elegido = null;
            foreach (Objeto s in externos)
            {
                string idioma = Py.Str(s.Obtener("idioma", ""));
                if (idioma == "es" || idioma == "spa") { elegido = s; break; }
                if (elegido == null) elegido = s;
            }
            if (elegido != null)
            {
                string ruta = dlna.Servidor.Biblioteca.Resolver((string)elegido["ruta"]);
                if (ruta != null) return new Sub { Externo = ruta };
            }
            // Al reproducir el archivo tal cual, la tele ya lee los subtítulos que van dentro.
            if (modo == "directo" || info == null || dlna.Servidor.Conversor == null) return null;
            int primero = -1;
            foreach (Objeto s in info["subtitulos"] as List<object> ?? new List<object>())
            {
                if (!Py.Verdad(s["texto"])) continue;
                string idioma = Py.Str(s["idioma"]);
                int n = (int)Py.ANumero(s["n"]);
                if (idioma == "spa" || idioma == "es") return new Sub { Interno = n };
                if (primero < 0) primero = n;
            }
            return primero >= 0 ? new Sub { Interno = primero } : null;
        }

        string Item(Objeto p, string padre, Contexto ctx)
        {
            string relativa = (string)p["ruta"];
            string id = Dlna.IdDe("i", relativa);
            string ruta = dlna.Servidor.Biblioteca.Resolver(relativa);
            var conversor = dlna.Servidor.Conversor;
            Objeto info = ruta != null && conversor != null ? conversor.InfoGuardada(ruta) : null;
            string modo = ruta == null ? "directo" : dlna.Modo(ruta, info);
            string ext = Py.Lower(Py.Sufijo(Path.GetFileName(relativa)));
            var sb = new StringBuilder();
            sb.Append("<item id=\"").Append(id).Append("\" parentID=\"").Append(X(padre)).Append("\" restricted=\"1\">");
            sb.Append("<dc:title>").Append(X(Py.Str(p["titulo"]))).Append("</dc:title>");
            sb.Append("<upnp:class>object.item.videoItem</upnp:class>");
            if (Py.EsNumero(p["fecha"]))
                sb.Append("<dc:date>").Append(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Py.ANumero(p["fecha"])).ToString("yyyy-MM-dd'T'HH:mm:ss", Py.Inv)).Append("</dc:date>");
            long marca = dlna.Marca(ctx.Aparato, relativa);
            if (marca > 0) sb.Append("<sec:dcmInfo>CREATIONDATE=0,FOLDER=MiCine,BM=").Append(marca).Append("</sec:dcmInfo>");
            string poster = p["poster"] as string;
            if (poster != null)
            {
                string px = Py.Lower(Py.Sufijo(poster));
                if (px == ".jpg" || px == ".jpeg" || px == ".png")
                    sb.Append("<upnp:albumArtURI dlna:profileID=\"").Append(px == ".png" ? "PNG_TN" : "JPEG_TN").Append("\">")
                      .Append(X(ctx.Base + "/dlna/p/" + id + px)).Append("</upnp:albumArtURI>");
            }
            Sub sub = ruta == null ? null : ElegirSubtitulo(p, info, modo);
            string urlSub = sub == null ? null : ctx.Base + "/dlna/s/" + id + ".srt";
            if (urlSub != null) sb.Append("<sec:CaptionInfoEx sec:type=\"srt\">").Append(X(urlSub)).Append("</sec:CaptionInfoEx>");

            bool directo = modo == "directo";
            sb.Append("<res protocolInfo=\"http-get:*:").Append(directo ? Dlna.Mime(ext, ctx.Samsung) : "video/mpeg").Append(':')
              .Append(directo ? RASGOS_ARCHIVO : RASGOS_CONVERTIDO).Append('"');
            if (directo) sb.Append(" size=\"").Append(((long)Py.ANumero(p["tamano"])).ToString(Py.Inv)).Append('"');
            double duracion = info != null ? Py.ANumero(info["duracion"]) : 0;
            if (duracion > 0) sb.Append(" duration=\"").Append(Duracion(duracion)).Append('"');
            var video = info == null ? null : info["video"] as Objeto;
            if (video != null && directo) sb.Append(" resolution=\"").Append((long)Py.ANumero(video["ancho"])).Append('x').Append((long)Py.ANumero(video["alto"])).Append('"');
            sb.Append('>').Append(X(ctx.Base + "/dlna/v/" + id + (directo ? ext : ".ts"))).Append("</res>");
            if (urlSub != null) sb.Append("<res protocolInfo=\"http-get:*:text/srt:*\">").Append(X(urlSub)).Append("</res>");
            sb.Append("</item>");
            return sb.ToString();
        }

        // ── vídeos, subtítulos y carátulas ───────────────────────────────

        /// <summary>La película pedida, si el aparato tiene permiso.</summary>
        Objeto Pelicula(string id, out string ruta)
        {
            if (dlna.EstadoDe(con.IpRemota, cabeceras, false) != Dlna.PERMITIDO) throw new ErrorPeticion("Este aparato no tiene permiso para ver MiCine", 403);
            Objeto p;
            ruta = null;
            if (!dlna.Actual().PorId.TryGetValue(id, out p) || (ruta = dlna.Servidor.Biblioteca.Resolver((string)p["ruta"])) == null)
                throw new ErrorPeticion("No encontrado", 404);
            return p;
        }

        static readonly Dictionary<string, DateTime> avisados = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        void Apuntar(Objeto p, bool convertida)
        {
            string clave = con.Ip + "|" + p["ruta"];
            lock (avisados)
            {
                DateTime antes;
                if (avisados.TryGetValue(clave, out antes) && (DateTime.UtcNow - antes).TotalMinutes < 30) return;
                avisados[clave] = DateTime.UtcNow;
                if (avisados.Count > 500) avisados.Clear();
            }
            Registro.Anotar("«" + dlna.NombreAparato(con.IpRemota) + "» está viendo «" + p["titulo"] + "»" + (convertida ? " (convertida en el ordenador)" : ""));
        }

        void Video(string id)
        {
            string ruta;
            Objeto p = Pelicula(id, out ruta);
            var conversor = dlna.Servidor.Conversor;
            Objeto info = conversor != null ? conversor.Info(ruta) : null;
            string modo = dlna.Modo(ruta, info);
            double duracion = info != null ? Py.ANumero(info["duracion"]) : 0;
            var extra = Lista("transferMode.dlna.org", "Streaming",
                "contentFeatures.dlna.org", modo == "directo" ? RASGOS_ARCHIVO : RASGOS_CONVERTIDO);
            // Las Samsung piden los subtítulos y la duración con estas cabeceras.
            if (Cabecera("getCaptionInfo.sec") != null && ElegirSubtitulo(p, info, modo) != null)
                extra.Add(new KeyValuePair<string, string>("CaptionInfo.sec", Base + "/dlna/s/" + id + ".srt"));
            if (Cabecera("getMediaInfo.sec") != null && duracion > 0)
                extra.Add(new KeyValuePair<string, string>("MediaInfo.sec", "SEC_Duration=" + ((long)(duracion * 1000)).ToString(Py.Inv) + ";"));
            if (modo == "directo") EnviarArchivo(p, ruta, Dlna.Mime(Py.Lower(Py.Sufijo(ruta)), Samsung), extra);
            else Convertir(p, ruta, info, modo, duracion, extra);
        }

        static readonly Regex Rango = new Regex(@"^bytes=(\d*)-(\d*)\z");

        void EnviarArchivo(Objeto p, string ruta, string tipo, List<KeyValuePair<string, string>> extra)
        {
            using (var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
            {
                long tamano = archivo.Length, inicio = 0, fin = tamano - 1;
                int codigo = 200;
                string rango = Cabecera("Range");
                if (!string.IsNullOrEmpty(rango))
                {
                    Match m = Rango.Match(Py.Strip(rango));
                    if (!m.Success || (m.Groups[1].Value == "" && m.Groups[2].Value == "")) throw new ErrorPeticion("Rango no válido", 416);
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
                        if (m.Groups[2].Value != "") { Py.Entero(m.Groups[2].Value, out b); fin = Math.Min(b, tamano - 1); }
                    }
                    if (inicio >= tamano || inicio > fin)
                    {
                        con.Enviar(416, Lista("Content-Type", "text/plain", "Content-Length", "0", "Content-Range", "bytes */" + tamano.ToString(Py.Inv)), null);
                        return;
                    }
                    codigo = 206;
                }
                var h = Lista("Content-Type", tipo, "Content-Length", (fin - inicio + 1).ToString(Py.Inv), "Accept-Ranges", "bytes");
                if (codigo == 206) h.Add(new KeyValuePair<string, string>("Content-Range", "bytes " + inicio.ToString(Py.Inv) + "-" + fin.ToString(Py.Inv) + "/" + tamano.ToString(Py.Inv)));
                h.AddRange(extra);
                con.Enviar(codigo, h, null);
                if (metodo == "HEAD") return;
                if (inicio == 0) Apuntar(p, false);
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

        /// <summary>«123.4», «1:02:03.5» o «02:03» (npt de DLNA) en segundos.</summary>
        static double Npt(string texto)
        {
            texto = texto.Trim();
            if (texto == "" || texto == "now") return 0;
            double total = 0;
            foreach (string parte in texto.Split(':'))
            {
                double v;
                if (!double.TryParse(parte, NumberStyles.Float, Py.Inv, out v) || double.IsNaN(v) || v < 0) throw new ErrorPeticion("Tiempo no válido", 400);
                total = total * 60 + v;
            }
            return total;
        }

        void Convertir(Objeto p, string ruta, Objeto info, string modo, double duracion, List<KeyValuePair<string, string>> extra)
        {
            // Lo convertido no tiene tamaño: la tele salta por tiempo (TimeSeekRange), no por bytes.
            double inicio = 0;
            string busca = Cabecera("TimeSeekRange.dlna.org");
            if (busca != null)
            {
                string valor = busca.Trim();
                if (valor.StartsWith("npt=", StringComparison.OrdinalIgnoreCase)) valor = valor.Substring(4);
                int guion = valor.IndexOf('-');
                inicio = Npt(guion >= 0 ? valor.Substring(0, guion) : valor);
                if (duracion > 0 && inicio >= duracion) throw new ErrorPeticion("Fuera de la película", 416);
            }
            var h = Lista("Content-Type", "video/mpeg", "Cache-Control", "no-store", "Connection", "close");
            if (busca != null)
            {
                string fin = duracion > 0 ? duracion.ToString("0.000", Py.Inv) : "";
                h.Add(new KeyValuePair<string, string>("TimeSeekRange.dlna.org", "npt=" + inicio.ToString("0.000", Py.Inv) + "-" + fin + (fin != "" ? "/" + fin : "/*")));
            }
            h.AddRange(extra);
            con.Cerrar = true;
            if (metodo == "HEAD") { con.Enviar(200, h, null); return; }
            var audio = Dlna.AudioPorDefecto(info);
            var conversor = dlna.Servidor.Conversor;
            Process proceso = conversor.Convertir(ruta, inicio, audio == null ? 0 : (int)Py.ANumero(audio["n"]), modo == "audio", false);
            try
            {
                con.Enviar(200, h, null);
                if (inicio < 1) Apuntar(p, true);
                Stream salida = proceso.StandardOutput.BaseStream;
                var trozo = new byte[256 * 1024];
                int n;
                while ((n = salida.Read(trozo, 0, trozo.Length)) > 0) con.Escribir(trozo, 0, n);
            }
            catch (IOException) { }
            finally { conversor.Parar(proceso); }
        }

        void Subtitulo(string id)
        {
            string ruta;
            Objeto p = Pelicula(id, out ruta);
            var conversor = dlna.Servidor.Conversor;
            Objeto info = conversor != null ? conversor.Info(ruta) : null;
            Sub sub = ElegirSubtitulo(p, info, dlna.Modo(ruta, info));
            if (sub == null) throw new ErrorPeticion("Sin subtítulos", 404);
            byte[] vtt = sub.Externo != null ? Biblioteca.SubtituloVtt(sub.Externo) : conversor.Subtitulo(ruta, sub.Interno);
            byte[] srt = VttASrt(Py.Utf8.GetString(vtt));
            Enviar(200, "text/srt; charset=utf-8", srt, Lista("Cache-Control", "max-age=3600"));
        }

        static readonly Regex TiempoVtt = new Regex(@"^\s*((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})");
        static readonly Regex Etiquetas = new Regex("<[^>]*>");

        static string TiempoSrt(string t)
        {
            t = t.Replace('.', ',');
            if (t.Split(':').Length == 2) t = "00:" + t;
            int coma = t.IndexOf(',');
            string ms = t.Substring(coma + 1).PadRight(3, '0');
            return t.Substring(0, coma) + "," + ms;
        }

        /// <summary>WebVTT → SRT, que es lo que entienden las teles (en UTF-8 con BOM para que lean bien las tildes).</summary>
        internal static byte[] VttASrt(string vtt)
        {
            var sb = new StringBuilder();
            int n = 0;
            string[] lineas = vtt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lineas.Length; i++)
            {
                Match m = TiempoVtt.Match(lineas[i]);
                if (!m.Success) continue;
                var texto = new List<string>();
                while (i + 1 < lineas.Length && lineas[i + 1].Trim() != "")
                {
                    i++;
                    string limpio = Etiquetas.Replace(lineas[i], "").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ").Trim();
                    if (limpio != "") texto.Add(limpio);
                }
                if (texto.Count == 0) continue;
                sb.Append(++n).Append("\r\n").Append(TiempoSrt(m.Groups[1].Value)).Append(" --> ").Append(TiempoSrt(m.Groups[2].Value)).Append("\r\n")
                  .Append(string.Join("\r\n", texto)).Append("\r\n\r\n");
            }
            byte[] cuerpo = Py.Utf8.GetBytes(sb.ToString());
            var conBom = new byte[cuerpo.Length + 3];
            conBom[0] = 0xEF; conBom[1] = 0xBB; conBom[2] = 0xBF;
            Buffer.BlockCopy(cuerpo, 0, conBom, 3, cuerpo.Length);
            return conBom;
        }

        void Caratula(string id)
        {
            string ruta;
            Objeto p = Pelicula(id, out ruta);
            string poster = p["poster"] as string;
            string real = poster == null ? null : dlna.Servidor.Biblioteca.Resolver(poster);
            string tipo;
            if (real == null || !Tipos.Imagenes.TryGetValue(Py.Lower(Py.Sufijo(real)), out tipo)) throw new ErrorPeticion("Sin carátula", 404);
            byte[] imagen = File.ReadAllBytes(real);
            Enviar(200, tipo, imagen, Lista("Cache-Control", "max-age=86400"));
        }

        // ── avisos de cambios (GENA) ─────────────────────────────────────

        /// <summary>
        /// Algunas teles se suscriben a los avisos de cambios: se acepta y se manda el primer aviso
        /// (solo a quien lo pide, nunca a otra dirección).
        /// </summary>
        void Suscripcion(string ruta)
        {
            string servicio = ruta == "/dlna/eventos/cd" ? Dlna.SERVICIO_CD : ruta == "/dlna/eventos/cm" ? Dlna.SERVICIO_CM : null;
            if (servicio == null) { Texto(404, "No encontrado"); return; }
            if (metodo == "UNSUBSCRIBE") { con.Enviar(200, Lista("Content-Length", "0"), null); return; }
            string sid = Cabecera("SID");
            bool renovar = !string.IsNullOrEmpty(sid);
            if (!renovar) sid = "uuid:" + Guid.NewGuid();
            con.Enviar(200, Lista("SID", sid, "TIMEOUT", "Second-1800", "Content-Length", "0"), null);
            if (renovar) return;
            string llamada = Cabecera("CALLBACK") ?? "";
            int a = llamada.IndexOf('<'), b = llamada.IndexOf('>');
            if (a < 0 || b <= a) return;
            Uri destino;
            if (!Uri.TryCreate(llamada.Substring(a + 1, b - a - 1), UriKind.Absolute, out destino) || destino.Scheme != "http") return;
            IPAddress ipDestino;
            if (!IPAddress.TryParse(destino.Host.Trim('[', ']'), out ipDestino) || !ipDestino.Equals(con.IpRemota)) return;
            string cuerpo = servicio == Dlna.SERVICIO_CD
                ? "<e:property><SystemUpdateID>" + dlna.Actualizacion + "</SystemUpdateID></e:property><e:property><ContainerUpdateIDs></ContainerUpdateIDs></e:property>"
                : "<e:property><SourceProtocolInfo>" + X(Protocolos) + "</SourceProtocolInfo></e:property><e:property><SinkProtocolInfo></SinkProtocolInfo></e:property>" +
                  "<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>";
            byte[] xml = Py.Utf8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">" + cuerpo + "</e:propertyset>");
            string idSuscripcion = sid;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(200);
                    using (var c = new TcpClient())
                    {
                        var intento = c.BeginConnect(ipDestino, destino.Port, null, null);
                        if (!intento.AsyncWaitHandle.WaitOne(3000)) return;
                        c.EndConnect(intento);
                        c.SendTimeout = c.ReceiveTimeout = 3000;
                        byte[] h = Encoding.ASCII.GetBytes("NOTIFY " + destino.PathAndQuery + " HTTP/1.1\r\nHOST: " + destino.Authority +
                            "\r\nCONTENT-TYPE: text/xml; charset=\"utf-8\"\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nSID: " + idSuscripcion +
                            "\r\nSEQ: 0\r\nCONTENT-LENGTH: " + xml.Length + "\r\nConnection: close\r\n\r\n");
                        var s = c.GetStream();
                        s.Write(h, 0, h.Length);
                        s.Write(xml, 0, xml.Length);
                        s.Read(new byte[512], 0, 512);
                    }
                }
                catch (Exception) { } // la tele no escuchaba: da igual
            });
        }
    }
}
