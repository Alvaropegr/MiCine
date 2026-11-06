using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MiCine
{
    /// <summary>
    /// Carátulas sacadas de un fotograma del vídeo para lo que no tiene imagen propia. Se hacen una vez,
    /// en segundo plano y de una en una (FFmpeg con prioridad baja), y se guardan en datos\caratulas;
    /// las películas nuevas se hacen solas y «Actualizar carátulas» las rehace todas.
    /// </summary>
    public sealed class Caratulas
    {
        public const int ANCHO = 400, ALTO = 600;
        const int CADA_CUANTO = 10 * 60 * 1000;
        static readonly double[] Momentos = { 0.20, 0.35, 0.50, 0.10, 0.65 };

        sealed class Tarea { public string Ruta, Clave; }
        static readonly System.Text.RegularExpressions.Regex Valida = new System.Text.RegularExpressions.Regex(@"\A[0-9a-f]{20}\.(?:jpg|fallo)\z");
        static int contador;

        readonly string carpeta;
        readonly Conversor conversor;
        readonly Func<List<Objeto>> listar;
        readonly Func<string, string> resolver;
        readonly object cerrojo = new object();
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        readonly HashSet<string> hechas = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> fallidas = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> encoladas = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<Tarea> cola = new Queue<Tarea>();
        volatile bool activo;

        public Caratulas(string carpetaDatos, Conversor conversor, Func<List<Objeto>> listar, Func<string, string> resolver)
        {
            carpeta = Path.Combine(carpetaDatos, "caratulas");
            this.conversor = conversor;
            this.listar = listar;
            this.resolver = resolver;
            try
            {
                if (Directory.Exists(carpeta))
                    foreach (string f in Directory.GetFiles(carpeta))
                    {
                        string n = Path.GetFileName(f);
                        if (n.EndsWith(".tmp.jpg", StringComparison.Ordinal)) { try { File.Delete(f); } catch (Exception) { } continue; }
                        if (!Valida.IsMatch(n)) continue;
                        if (n.EndsWith(".jpg", StringComparison.Ordinal)) hechas.Add(n.Substring(0, 20));
                        else fallidas.Add(n.Substring(0, 20));
                    }
            }
            catch (Exception) { }
        }

        public bool Disponible { get { return conversor != null; } }
        public int Pendientes { get { lock (cerrojo) return cola.Count; } }

        /// <summary>sha1(ruta|tamaño): cambia si se sustituye el archivo por otro.</summary>
        public static string Clave(string ruta, long tamano)
        {
            return Nombres.Sha1Hex(ruta + "|" + tamano.ToString(Py.Inv)).Substring(0, 20);
        }

        public static string Clave(Objeto p) { return Clave((string)p["ruta"], (long)Py.ANumero(p["tamano"])); }

        public string Archivo(string clave) { return Path.Combine(carpeta, clave + ".jpg"); }

        /// <summary>Segundos de la última vez que se hizo (cambia la dirección y así nadie usa la vieja), o 0 si no hay.</summary>
        public long Version(string clave)
        {
            lock (cerrojo) if (!hechas.Contains(clave)) return 0;
            try { return Py.Segundos(File.GetLastWriteTimeUtc(Archivo(clave))); }
            catch (Exception) { return 0; }
        }

        public void Arrancar()
        {
            if (conversor == null) return;
            activo = true;
            new Thread(Trabajar) { IsBackground = true, Name = "MiCine: carátulas" }.Start();
        }

        public void Parar()
        {
            activo = false;
            despertar.Set();
        }

        /// <summary>Encola lo que no tiene imagen ni carátula hecha (barato: todo en memoria).</summary>
        public void Revisar(List<Objeto> peliculas)
        {
            if (conversor == null) return;
            bool nuevas = false;
            lock (cerrojo)
            {
                foreach (var p in peliculas)
                {
                    if (p["poster"] != null) continue;
                    string clave = Clave(p);
                    if (hechas.Contains(clave) || fallidas.Contains(clave) || encoladas.Contains(clave)) continue;
                    encoladas.Add(clave);
                    cola.Enqueue(new Tarea { Ruta = (string)p["ruta"], Clave = clave });
                    nuevas = true;
                }
            }
            if (nuevas) despertar.Set();
        }

        /// <summary>«Actualizar carátulas»: se borran todas (también los fallos) y se vuelven a hacer.</summary>
        public int Rehacer()
        {
            if (conversor == null) throw new ErrorPeticion("Para sacar carátulas del vídeo hace falta FFmpeg (ver LEEME.md)");
            lock (cerrojo)
            {
                cola.Clear();
                encoladas.Clear();
                hechas.Clear();
                fallidas.Clear();
                try
                {
                    if (Directory.Exists(carpeta))
                        foreach (string f in Directory.GetFiles(carpeta))
                            try { File.Delete(f); } catch (Exception) { }
                }
                catch (Exception) { }
            }
            Revisar(listar());
            return Pendientes;
        }

        void Trabajar()
        {
            DateTime ultimoListado = DateTime.MinValue;
            while (activo)
            {
                try
                {
                    if ((DateTime.UtcNow - ultimoListado).TotalMilliseconds >= CADA_CUANTO)
                    {
                        ultimoListado = DateTime.UtcNow;
                        try { Revisar(listar()); } catch (Exception) { }
                    }
                    Tarea t = null;
                    lock (cerrojo) if (cola.Count > 0) t = cola.Peek();
                    if (t == null)
                    {
                        despertar.WaitOne(CADA_CUANTO);
                        continue;
                    }
                    bool bien = false;
                    try { bien = Hacer(t); }
                    catch (Exception e) { Registro.Error("Carátula de " + t.Ruta, e); }
                    lock (cerrojo)
                    {
                        if (cola.Count > 0 && cola.Peek() == t) cola.Dequeue();
                        encoladas.Remove(t.Clave);
                        if (bien) hechas.Add(t.Clave);
                        else
                        {
                            fallidas.Add(t.Clave);
                            try { Directory.CreateDirectory(carpeta); File.WriteAllBytes(Path.Combine(carpeta, t.Clave + ".fallo"), new byte[0]); }
                            catch (Exception) { }
                        }
                        if (cola.Count == 0) Registro.Anotar("Carátulas del vídeo al día (" + hechas.Count + ")");
                    }
                }
                catch (Exception e)
                {
                    Registro.Error("Carátulas", e);
                    despertar.WaitOne(60000);
                }
            }
        }

        const string FILTRO =
            "[0:v:0]yadif=deint=interlaced,scale=trunc(iw*sar/2)*2:ih,setsar=1,thumbnail=40,split=3[a][b][c];" +
            "[a]scale=400:600:force_original_aspect_ratio=increase,crop=400:600,boxblur=18:3,eq=brightness=-0.12[fondo];" +
            "[b]scale=400:-2[frente];[fondo][frente]overlay=0:(H-h)/2,format=yuvj420p[img];[c]scale=16:16,format=gray[g]";

        /// <summary>
        /// Un fotograma al 20 % (si sale casi negro o plano, al 35 %, 50 %, 10 % o 65 %): el fotograma a lo
        /// ancho sobre él mismo ampliado y desenfocado, para que quede una carátula vertical.
        /// </summary>
        bool Hacer(Tarea t)
        {
            string ruta = resolver(t.Ruta);
            if (ruta == null || !File.Exists(ruta)) return false;
            double duracion = 0;
            try { duracion = Py.ANumero(conversor.Info(ruta)["duracion"]); } catch (Exception) { }
            Directory.CreateDirectory(carpeta);
            string temporal = Path.Combine(carpeta, t.Clave + "." + Interlocked.Increment(ref contador) + ".tmp.jpg");
            bool hecha = false;
            // Sin duración conocida: el minuto 1 y, si el vídeo es más corto, el principio.
            IEnumerable<double> segundos = duracion > 0 ? Momentos.Select(f => duracion * f) : new double[] { 60, 0 };
            foreach (double segundo in segundos)
            {
                int codigo;
                byte[] muestra = Conversor.Ejecutar(conversor.Ffmpeg, new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", segundo.ToString("0.000", Py.Inv),
                    "-i", "file:" + ruta, "-filter_complex", FILTRO,
                    "-map", "[img]", "-frames:v", "1", "-q:v", "3", "-update", "1", "-y", temporal,
                    "-map", "[g]", "-frames:v", "1", "-f", "rawvideo", "pipe:1",
                }, 120, out codigo);
                if (codigo != 0 || !File.Exists(temporal) || new FileInfo(temporal).Length == 0) continue;
                hecha = true;
                if (!Plana(muestra) || duracion <= 0) break;
            }
            if (!hecha) { try { File.Delete(temporal); } catch (Exception) { } return false; }
            Nativo.ReemplazarConReintentos(temporal, Archivo(t.Clave));
            return true;
        }

        /// <summary>Casi negro (créditos, fundidos) o de un solo color: mejor otro momento.</summary>
        static bool Plana(byte[] grises)
        {
            if (grises == null || grises.Length < 16) return true;
            double media = grises.Average(b => (double)b);
            double varianza = grises.Average(b => (b - media) * (b - media));
            return media < 20 || Math.Sqrt(varianza) < 8;
        }
    }
}
