using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiCine
{
    /// <summary>Error que se devuelve tal cual al navegador o a la app.</summary>
    public sealed class ErrorPeticion : Exception
    {
        public readonly int Codigo;
        public ErrorPeticion(string mensaje) : this(mensaje, 400) { }
        public ErrorPeticion(string mensaje, int codigo) : base(mensaje) { Codigo = codigo; }
    }

    /// <summary>
    /// Usuarios, sesiones y progreso de visionado, guardados como JSON en la
    /// carpeta datos/. Mismos archivos y mismo formato que servidor.py.
    /// </summary>
    public sealed class Almacen
    {
        public const int DIAS_ENLACE = 7;

        public readonly object Cerrojo = new object();
        public readonly string Datos;
        readonly object duracion;       // long o double, como dias_sesion * 86400 en Python
        public readonly double DuracionSesion;
        Objeto usuarios, sesiones, progreso;
        readonly byte[] secreto;

        public Almacen(string datos, object duracionSesion)
        {
            Datos = datos;
            Directory.CreateDirectory(datos);
            duracion = duracionSesion;
            DuracionSesion = Py.ANumero(duracionSesion);
            usuarios = Leer("usuarios");
            sesiones = Leer("sesiones");
            progreso = Leer("progreso");
            string rutaSecreto = Path.Combine(datos, "secreto.key");
            if (!File.Exists(rutaSecreto)) File.WriteAllText(rutaSecreto, Cripto.TokenHex(32), Py.Utf8);
            secreto = Cripto.DeHex(Py.Strip(File.ReadAllText(rutaSecreto, Encoding.UTF8)));
        }

        /// <summary>Max-Age de la cookie, escrito como lo escribe Python.</summary>
        public string DuracionTexto
        {
            get { return duracion is double ? Py.Repr((double)duracion) : Convert.ToString(duracion, Py.Inv); }
        }

        Objeto Leer(string nombre)
        {
            object v = Json.LeerArchivo(Path.Combine(Datos, nombre + ".json"), new Objeto());
            var o = v as Objeto;
            if (o == null) throw new ErrorJson(nombre + ".json tiene que ser un objeto JSON");
            return o;
        }

        void Guardar(string nombre)
        {
            Objeto datos = nombre == "usuarios" ? usuarios : nombre == "sesiones" ? sesiones : progreso;
            Json.EscribirArchivo(Path.Combine(Datos, nombre + ".json"), datos);
        }

        // ── usuarios ─────────────────────────────────────────────────────

        public bool HayUsuarios() { lock (Cerrojo) return usuarios.Count > 0; }

        public int CuantosUsuarios() { lock (Cerrojo) return usuarios.Count; }

        public int CuantosAdministradores()
        {
            lock (Cerrojo)
            {
                int n = 0;
                foreach (var kv in usuarios)
                {
                    var r = kv.Value as Objeto;
                    if (r != null && (r["rol"] as string) == "admin") n++;
                }
                return n;
            }
        }

        public bool Existe(string nombre) { lock (Cerrojo) return usuarios.Contiene(nombre); }

        /// <summary>El registro de un usuario (usuarios.get(nombre)).</summary>
        public Objeto Registro(string nombre) { lock (Cerrojo) return nombre == null ? null : usuarios.ObjetoDe(nombre); }

        public string Rol(string nombre)
        {
            lock (Cerrojo)
            {
                if (nombre == null) return null;
                var r = usuarios.ObjetoDe(nombre);
                return (r != null && r.Count > 0) ? r["rol"] as string : null;
            }
        }

        public void CrearUsuario(string nombre, string contrasena, string rol)
        {
            lock (Cerrojo)
            {
                if (usuarios.Contiene(nombre)) throw new ErrorPeticion("Ese usuario ya existe", 409);
                var registro = new Objeto { { "rol", rol }, { "creado", Py.AhoraEntero() } };
                foreach (var kv in Cripto.HashContrasena(contrasena)) registro[kv.Key] = kv.Value;
                usuarios[nombre] = registro;
                Guardar("usuarios");
            }
        }

        public void FijarContrasena(string nombre, string contrasena, string rol)
        {
            lock (Cerrojo)
            {
                var registro = usuarios.ObjetoDe(nombre);
                if (!usuarios.Contiene(nombre) || registro == null) throw new ErrorPeticion("Ese usuario no existe", 404);
                foreach (var kv in Cripto.HashContrasena(contrasena)) registro[kv.Key] = kv.Value;
                if (!string.IsNullOrEmpty(rol)) registro["rol"] = rol;
                Guardar("usuarios");
            }
        }

        public void BorrarUsuario(string nombre)
        {
            lock (Cerrojo)
            {
                if (!usuarios.Contiene(nombre) || usuarios[nombre] == null) throw new ErrorPeticion("Ese usuario no existe", 404);
                usuarios.Quitar(nombre);
                Guardar("usuarios");
                CerrarSesionesDe(nombre, null);
                if (progreso.Contiene(nombre))
                {
                    bool habia = progreso[nombre] != null;
                    progreso.Quitar(nombre);
                    if (habia) Guardar("progreso");
                }
            }
        }

        /// <summary>Lista para /api/usuarios: ordenada por nombre.</summary>
        public List<object> ListaUsuarios()
        {
            lock (Cerrojo)
            {
                var lista = new List<object>();
                foreach (string n in Py.Ordenados(usuarios.Claves))
                {
                    var u = usuarios.ObjetoDe(n);
                    lista.Add(new Objeto { { "nombre", n }, { "rol", u["rol"] }, { "creado", u["creado"] } });
                }
                return lista;
            }
        }

        // ── sesiones ─────────────────────────────────────────────────────

        /// <summary>En disco solo se guarda el hash del token: quien lea el archivo no puede usar las sesiones.</summary>
        static string Clave(string token) { return Cripto.Sha256Hex(token); }

        static double Expira(object sesion)
        {
            var s = sesion as Objeto;
            return Py.ANumero(s["expira"]);
        }

        public string CrearSesion(string nombre)
        {
            string token = Cripto.TokenUrlSafe(32);
            lock (Cerrojo)
            {
                double ahora = Py.Ahora();
                var vivas = new Objeto();
                foreach (var kv in sesiones) if (Expira(kv.Value) > ahora) vivas[kv.Key] = kv.Value;
                sesiones = vivas;
                sesiones[Clave(token)] = new Objeto { { "usuario", nombre }, { "expira", ahora + DuracionSesion } };
                Guardar("sesiones");
            }
            return token;
        }

        public string UsuarioDe(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (Cerrojo)
            {
                var sesion = sesiones.ObjetoDe(Clave(token));
                double ahora = Py.Ahora();
                if (sesion == null || sesion.Count == 0) return null;
                string usuario = sesion["usuario"] as string;
                if (Expira(sesion) <= ahora || usuario == null || !usuarios.Contiene(usuario)) return null;
                // Quien usa MiCine a menudo no tiene que volver a entrar: la sesión se renueva (como mucho una vez al día).
                if (Expira(sesion) - ahora < DuracionSesion - 86400)
                {
                    sesion["expira"] = ahora + DuracionSesion;
                    Guardar("sesiones");
                }
                return usuario;
            }
        }

        public void CerrarSesion(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (Cerrojo)
            {
                string clave = Clave(token);
                if (sesiones.Contiene(clave))
                {
                    bool valia = Py.Verdad(sesiones[clave]);
                    sesiones.Quitar(clave);
                    if (valia) Guardar("sesiones");
                }
            }
        }

        public void CerrarSesionesDe(string nombre, string salvoToken)
        {
            string salvo = string.IsNullOrEmpty(salvoToken) ? null : Clave(salvoToken);
            lock (Cerrojo)
            {
                var quedan = new Objeto();
                foreach (var kv in sesiones)
                {
                    var s = kv.Value as Objeto;
                    if ((s["usuario"] as string) != nombre || kv.Key == salvo) quedan[kv.Key] = kv.Value;
                }
                sesiones = quedan;
                Guardar("sesiones");
            }
        }

        // ── enlaces firmados ─────────────────────────────────────────────
        // La app de Android (VLC, el gestor de descargas) no puede enviar cookies ni cabeceras,
        // así que cada archivo se pide con un enlace firmado que caduca a los pocos días.

        string Firma(string usuario, string ruta, long expira)
        {
            return Cripto.HmacSha256Hex(secreto, usuario + "\n" + ruta + "\n" + expira.ToString(Py.Inv)).Substring(0, 40);
        }

        public string Enlace(string usuario, string ruta)
        {
            // La caducidad cambia una vez al día: así el enlace es estable y las carátulas se cachean.
            long expira = (Py.AhoraEntero() / 86400 + DIAS_ENLACE + 1) * 86400;
            string consulta = "u=" + Py.CitarMas(usuario) + "&e=" + expira.ToString(Py.Inv) + "&f=" + Py.CitarMas(Firma(usuario, ruta, expira));
            return "/media/" + Py.Citar(ruta) + "?" + consulta;
        }

        public string UsuarioDeEnlace(string ruta, Dictionary<string, List<string>> consulta)
        {
            List<string> u, e, f;
            if (!consulta.TryGetValue("u", out u) || !consulta.TryGetValue("e", out e) || !consulta.TryGetValue("f", out f)) return null;
            if (u.Count == 0 || e.Count == 0 || f.Count == 0) return null;
            long expira;
            if (!Py.Entero(e[0], out expira)) return null;
            string usuario = u[0], firma = f[0];
            if (expira < Py.Ahora()) return null;
            lock (Cerrojo) if (!usuarios.Contiene(usuario)) return null;
            if (expira == long.MaxValue || expira == long.MinValue) return null; // no cabe: nunca lo firmó este servidor
            return Cripto.Iguales(firma, Firma(usuario, ruta, expira)) ? usuario : null;
        }

        // ── progreso ─────────────────────────────────────────────────────

        public Objeto ProgresoDe(string nombre)
        {
            lock (Cerrojo)
            {
                var p = progreso.ObjetoDe(nombre);
                return p == null ? new Objeto() : p.Copia();
            }
        }

        public void GuardarProgreso(string nombre, string ruta, double segundos, double duracionVideo)
        {
            lock (Cerrojo)
            {
                var suyo = progreso.ObjetoDe(nombre);
                if (suyo == null) { suyo = new Objeto(); progreso[nombre] = suyo; }
                suyo[ruta] = new Objeto
                {
                    { "t", Py.Redondear1(segundos) },
                    { "d", Py.Redondear1(duracionVideo) },
                    { "fecha", Py.AhoraEntero() },
                };
                Guardar("progreso");
            }
        }
    }

    /// <summary>Frena a quien intente adivinar contraseñas: 5 fallos seguidos bloquean esa IP 5 minutos.</summary>
    public sealed class Limitador
    {
        sealed class Fallos { public int Numero; public double Hasta; }

        readonly int intentos, bloqueo;
        readonly Dictionary<string, Fallos> fallos = new Dictionary<string, Fallos>();

        public Limitador() : this(5, 300) { }
        public Limitador(int intentos, int bloqueo) { this.intentos = intentos; this.bloqueo = bloqueo; }

        public int Espera(string ip)
        {
            lock (fallos)
            {
                Fallos f;
                if (!fallos.TryGetValue(ip, out f) || f.Numero < intentos) return 0;
                return (int)Math.Max(0, Math.Ceiling(f.Hasta - Py.Ahora()));
            }
        }

        public void Fallo(string ip)
        {
            lock (fallos)
            {
                Fallos f;
                if (!fallos.TryGetValue(ip, out f)) f = new Fallos();
                int numero = f.Numero;
                if (numero >= intentos && f.Hasta <= Py.Ahora()) numero = 0;
                numero++;
                fallos[ip] = new Fallos { Numero = numero, Hasta = numero >= intentos ? Py.Ahora() + bloqueo : 0.0 };
            }
        }

        public void Exito(string ip)
        {
            lock (fallos) fallos.Remove(ip);
        }
    }
}
