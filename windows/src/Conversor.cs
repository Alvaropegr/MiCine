using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MiCine
{
    /// <summary>
    /// Conversión al vuelo con FFmpeg (la clase Conversor de servidor.py): lo que la tele o el
    /// navegador no saben reproducir se convierte mientras se ve. Si la imagen se entiende y solo
    /// falla el audio (DTS, TrueHD…), la imagen se copia tal cual y casi no gasta. Es opcional:
    /// sin ffmpeg\bin\ffmpeg.exe (ni FFmpeg en el PATH) MiCine funciona igual, sin convertir.
    /// </summary>
    public sealed class Conversor
    {
        const int A_LA_VEZ = 3;

        // Subtítulos internos que se pueden pasar a texto; los de imagen (PGS, VobSub) no.
        static readonly HashSet<string> SubtitulosTexto = new HashSet<string>(StringComparer.Ordinal)
        {
            "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer",
        };

        static readonly Dictionary<string, string[]> Codificadores = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "h264_nvenc", new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23",
                                    "-b:v", "8M", "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high" } },
            { "h264_qsv", new[] { "-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "23",
                                  "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high" } },
            { "h264_amf", new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "vbr_peak",
                                  "-b:v", "8M", "-maxrate", "12M", "-profile:v", "high" } },
            { "libx264", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                                 "-maxrate", "12M", "-bufsize", "16M", "-profile:v", "high", "-level", "4.1" } },
        };

        public readonly string Ffmpeg, Ffprobe;
        volatile string codificador = "libx264";
        readonly object cerrojo = new object();
        readonly Dictionary<string, Objeto> infos = new Dictionary<string, Objeto>(StringComparer.Ordinal);
        readonly Dictionary<string, byte[]> subtitulos = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        readonly HashSet<Process> procesos = new HashSet<Process>();
        readonly HashSet<Process> parados = new HashSet<Process>();

        Conversor(string ffmpeg, string ffprobe)
        {
            Ffmpeg = ffmpeg;
            Ffprobe = ffprobe;
            new Thread(ElegirCodificador) { IsBackground = true, Name = "MiCine: codificador" }.Start();
        }

        public string Codificador { get { return codificador; } }

        /// <summary>FFmpeg en la carpeta ffmpeg\ de MiCine (tal como queda al descomprimir el zip) o en el PATH.</summary>
        public static Conversor Buscar(string baseDir)
        {
            var carpetas = new List<string> { Path.Combine(baseDir, "ffmpeg", "bin"), Path.Combine(baseDir, "ffmpeg") };
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string limpia = d.Trim().Trim('"');
                if (limpia != "") carpetas.Add(limpia);
            }
            foreach (string carpeta in carpetas)
            {
                try
                {
                    string ffmpeg = Path.Combine(carpeta, "ffmpeg.exe"), ffprobe = Path.Combine(carpeta, "ffprobe.exe");
                    if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new Conversor(ffmpeg, ffprobe);
                }
                catch (ArgumentException) { } // una entrada del PATH con caracteres no válidos
            }
            return null;
        }

        // ── procesos ─────────────────────────────────────────────────────

        /// <summary>Argumentos de línea de órdenes con las comillas que espera un programa de Windows.</summary>
        static string Citar(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int barras = 0;
            foreach (char c in a)
            {
                if (c == '\\') { barras++; continue; }
                if (c == '"') sb.Append('\\', barras * 2 + 1);
                else sb.Append('\\', barras);
                sb.Append(c);
                barras = 0;
            }
            sb.Append('\\', barras * 2).Append('"');
            return sb.ToString();
        }

        /// <summary>Sin ventana negra y con prioridad baja: el ordenador sigue fluido mientras convierte.</summary>
        static Process Lanzar(string exe, List<string> args, StringBuilder errores)
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.ConvertAll(Citar)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = Process.Start(psi);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception) { } // ya terminó
            // Hay que leer lo que escribe en stderr: si nadie lo lee, se atasca.
            p.ErrorDataReceived += delegate (object o, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (errores)
                {
                    if (errores.Length < 8000) errores.AppendLine(e.Data);
                }
            };
            p.BeginErrorReadLine();
            return p;
        }

        /// <summary>Ejecuta y espera: devuelve lo que sale por la salida estándar.</summary>
        static byte[] Ejecutar(string exe, List<string> args, int segundos, out int codigo)
        {
            var errores = new StringBuilder();
            using (var p = Lanzar(exe, args, errores))
            {
                var salida = new MemoryStream();
                var lector = new Thread(delegate ()
                {
                    try { p.StandardOutput.BaseStream.CopyTo(salida); }
                    catch (IOException) { }
                }) { IsBackground = true };
                lector.Start();
                if (p.WaitForExit(segundos * 1000))
                {
                    p.WaitForExit();
                    codigo = p.ExitCode;
                }
                else
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    codigo = -1;
                }
                lector.Join(5000);
                return salida.ToArray();
            }
        }

        /// <summary>La tarjeta gráfica convierte mucho más rápido que el procesador: se usa si la hay.</summary>
        void ElegirCodificador()
        {
            foreach (string candidato in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                int codigo;
                try
                {
                    Ejecutar(Ffmpeg, new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                        "-i", "color=black:s=256x144:d=0.2", "-c:v", candidato, "-f", "null", "-" }, 30, out codigo);
                }
                catch (Exception) { continue; }
                if (codigo == 0)
                {
                    codificador = candidato;
                    break;
                }
            }
            Registro.Anotar("Conversión de vídeo disponible (con " + codificador + ")");
        }

        static string Clave(string ruta)
        {
            var info = new FileInfo(ruta);
            return ruta + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }

        // ── información del archivo ──────────────────────────────────────

        static string Texto(Objeto o, string clave) { return o == null ? "" : Py.Str(o.Obtener(clave, "")); }

        static long Entero(Objeto o, string clave)
        {
            object v = o == null ? null : o.Obtener(clave, null);
            return Py.EsNumero(v) ? (long)Py.ANumero(v) : 0;
        }

        /// <summary>ffprobe da los segundos como texto ("60.000000").</summary>
        static double Decimal(Objeto o, string clave)
        {
            double valor;
            return double.TryParse(Texto(o, clave), System.Globalization.NumberStyles.Float, Py.Inv, out valor)
                && !double.IsNaN(valor) && !double.IsInfinity(valor) ? Math.Round(valor, 3) : 0.0;
        }

        /// <summary>Lo que ya se sabe del archivo, sin llamar a ffprobe (null si aún no se ha mirado).</summary>
        public Objeto InfoGuardada(string ruta)
        {
            string clave;
            try { clave = Clave(ruta); }
            catch (Exception) { return null; }
            lock (cerrojo)
            {
                Objeto guardada;
                return infos.TryGetValue(clave, out guardada) ? guardada : null;
            }
        }

        /// <summary>Pistas de vídeo, audio y subtítulos que lleva el archivo (se pregunta una vez y se recuerda).</summary>
        public Objeto Info(string ruta)
        {
            string clave = Clave(ruta);
            lock (cerrojo)
            {
                Objeto guardada;
                if (infos.TryGetValue(clave, out guardada)) return guardada;
            }
            Objeto crudo = null;
            try
            {
                int codigo;
                byte[] salida = Ejecutar(Ffprobe, new List<string> { "-v", "error", "-print_format", "json", "-show_format",
                    "-show_streams", "file:" + ruta }, 60, out codigo);
                crudo = salida.Length > 0 ? Json.LeerBytes(salida) as Objeto : null;
            }
            catch (Exception) { } // ffprobe no ha podido con el archivo: se trata como vacío
            if (crudo == null) crudo = new Objeto();

            Objeto formato = crudo.ObjetoDe("format");
            var info = new Objeto
            {
                { "duracion", Decimal(formato, "duration") }, { "inicio", Decimal(formato, "start_time") }, { "video", null },
                { "audios", new List<object>() }, { "subtitulos", new List<object>() },
            };
            var audios = (List<object>)info["audios"];
            var subs = (List<object>)info["subtitulos"];
            var flujos = crudo.Obtener("streams", null) as List<object> ?? new List<object>();
            foreach (object x in flujos)
            {
                var s = x as Objeto;
                if (s == null) continue;
                string tipo = Texto(s, "codec_type"), codec = Texto(s, "codec_name");
                Objeto etiquetas = s.ObjetoDe("tags"), marcas = s.ObjetoDe("disposition");
                string idioma = Py.Lower(Texto(etiquetas, "language")), titulo = Texto(etiquetas, "title");
                if (tipo == "video" && info["video"] == null && Entero(marcas, "attached_pic") == 0)
                {
                    info["video"] = new Objeto
                    {
                        { "codec", codec }, { "ancho", Entero(s, "width") }, { "alto", Entero(s, "height") },
                        { "bits", Texto(s, "pix_fmt").Contains("10") ? 10L : 8L },
                    };
                }
                else if (tipo == "audio")
                {
                    audios.Add(new Objeto
                    {
                        { "n", (long)audios.Count }, { "codec", codec }, { "canales", Entero(s, "channels") },
                        { "idioma", idioma }, { "titulo", titulo }, { "defecto", Entero(marcas, "default") != 0 },
                    });
                }
                else if (tipo == "subtitle")
                {
                    subs.Add(new Objeto
                    {
                        { "n", (long)subs.Count }, { "codec", codec }, { "idioma", idioma },
                        { "titulo", titulo }, { "texto", SubtitulosTexto.Contains(codec) },
                    });
                }
            }
            lock (cerrojo) infos[clave] = info;
            return info;
        }

        /// <summary>
        /// El fotograma completo (clave) en el que empieza la imagen si se salta a ese segundo sin
        /// convertirla: desde ahí arrancan imagen y sonido, y el reproductor debe contar desde ahí.
        /// </summary>
        public double FotogramaClave(string ruta, double segundo)
        {
            if (segundo <= 0) return 0.0;
            double inicio = Py.ANumero(Info(ruta)["inicio"]);
            try
            {
                int codigo;
                byte[] salida = Ejecutar(Ffprobe, new List<string> { "-v", "error", "-select_streams", "v:0", "-read_intervals",
                    (inicio + segundo + 0.001).ToString("0.000", Py.Inv) + "%+#90", "-show_entries", "packet=pts_time,flags",
                    "-of", "csv=p=0", "file:" + ruta }, 30, out codigo);
                foreach (string linea in Encoding.ASCII.GetString(salida).Split('\n'))
                {
                    string[] partes = linea.Trim().Split(',');
                    double tiempo;
                    if (partes.Length >= 2 && partes[1].Contains("K")
                        && double.TryParse(partes[0], System.Globalization.NumberStyles.Float, Py.Inv, out tiempo))
                        return Math.Round(Math.Min(Math.Max(tiempo - inicio, 0.0), segundo), 3);
                }
            }
            catch (Exception) { } // sin respuesta de ffprobe: se empieza donde se pidió
            return segundo;
        }

        // ── conversión ───────────────────────────────────────────────────

        /// <summary>Lanza FFmpeg: el vídeo convertido sale por su salida estándar.</summary>
        public Process Convertir(string ruta, double inicio, int audio, bool copiarVideo, bool paraWeb)
        {
            lock (cerrojo)
            {
                procesos.RemoveWhere(delegate (Process p) { return p.HasExited; });
                if (procesos.Count >= A_LA_VEZ)
                    throw new ErrorPeticion("El ordenador ya está convirtiendo varias películas a la vez. Prueba en un momento.", 503);
            }
            long canales = 2;
            foreach (Objeto a in (List<object>)Info(ruta)["audios"])
                if (Entero(a, "n") == audio) canales = Entero(a, "canales");

            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
            if (inicio > 0 && copiarVideo)
            {
                // La imagen copiada solo puede empezar en un fotograma clave: el sonido arranca ahí
                // también para que no haya segundos mudos. (FFmpeg busca un poco antes de lo pedido.)
                args.AddRange(new[] { "-noaccurate_seek", "-ss", (FotogramaClave(ruta, inicio) + 0.15).ToString("0.000", Py.Inv) });
            }
            else if (inicio > 0) args.AddRange(new[] { "-ss", inicio.ToString("0.00", Py.Inv) });
            args.AddRange(new[] { "-i", "file:" + ruta, "-map", "0:v:0?", "-map", "0:a:" + audio + "?", "-sn", "-dn",
                "-map_metadata", "-1", "-map_chapters", "-1" });
            if (copiarVideo)
            {
                args.AddRange(new[] { "-c:v", "copy" });
                var video = Info(ruta)["video"] as Objeto;
                if (paraWeb && video != null && Texto(video, "codec") == "hevc")
                    args.AddRange(new[] { "-tag:v", "hvc1" }); // la etiqueta que esperan los navegadores
            }
            else
            {
                args.AddRange(new[] { "-vf", "scale='min(1920,iw)':-2,format=yuv420p" });
                args.AddRange(Codificadores[codificador]);
                args.AddRange(new[] { "-g", "48" });
            }
            if (paraWeb || canales <= 2) args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "192k" });
            else args.AddRange(new[] { "-c:a", "ac3", "-ac", Math.Min(canales, 6).ToString(Py.Inv), "-b:a", "640k" });
            if (paraWeb) args.AddRange(new[] { "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof" });
            else args.AddRange(new[] { "-f", "mpegts" });
            args.Add("pipe:1");

            var errores = new StringBuilder();
            Process proceso = Lanzar(Ffmpeg, args, errores);
            string nombre = Path.GetFileName(ruta);
            proceso.EnableRaisingEvents = true;
            proceso.Exited += delegate
            {
                bool aProposito;
                lock (cerrojo)
                {
                    aProposito = parados.Remove(proceso);
                    procesos.Remove(proceso);
                }
                string texto;
                lock (errores) texto = errores.ToString().Trim();
                int codigo = -1;
                try { codigo = proceso.ExitCode; }
                catch (Exception) { }
                if (codigo != 0 && !aProposito && texto != "")
                {
                    string[] lineas = texto.Split('\n');
                    Registro.Anotar("No se pudo convertir «" + nombre + "»: " + Py.Recortar(lineas[lineas.Length - 1].Trim(), 300));
                }
            };
            lock (cerrojo)
            {
                if (!proceso.HasExited) procesos.Add(proceso);
            }
            return proceso;
        }

        public void Parar(Process proceso)
        {
            lock (cerrojo)
            {
                parados.Add(proceso);
                procesos.Remove(proceso);
            }
            try
            {
                if (!proceso.HasExited) proceso.Kill();
            }
            catch (Exception) { } // terminó justo ahora
        }

        /// <summary>Al detener MiCine, que no se quede ningún FFmpeg trabajando.</summary>
        public void PararTodos()
        {
            List<Process> lista;
            lock (cerrojo) lista = new List<Process>(procesos);
            foreach (Process p in lista) Parar(p);
        }

        /// <summary>Saca a WebVTT unos subtítulos que van dentro del vídeo (hay que leer el archivo entero).</summary>
        public byte[] Subtitulo(string ruta, int n)
        {
            string clave = Clave(ruta) + "|" + n;
            lock (cerrojo)
            {
                byte[] guardado;
                if (subtitulos.TryGetValue(clave, out guardado)) return guardado;
            }
            int codigo;
            byte[] vtt;
            try
            {
                vtt = Ejecutar(Ffmpeg, new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", "file:" + ruta,
                    "-map", "0:s:" + n, "-f", "webvtt", "pipe:1" }, 900, out codigo);
            }
            catch (Exception)
            {
                throw new ErrorPeticion("No se pudieron sacar los subtítulos del vídeo", 500);
            }
            if (codigo != 0 || vtt.Length == 0)
                throw new ErrorPeticion("Esos subtítulos son imágenes, no texto, y no se pueden mostrar", 422);
            lock (cerrojo)
            {
                if (subtitulos.Count >= 50) subtitulos.Clear();
                subtitulos[clave] = vtt;
            }
            return vtt;
        }
    }
}
