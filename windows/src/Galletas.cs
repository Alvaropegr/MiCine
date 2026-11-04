using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MiCine
{
    /// <summary>
    /// http.cookies.SimpleCookie().load() de Python, para leer la cookie de
    /// sesión con las mismas reglas: si el navegador manda otra cookie con un
    /// nombre no válido, Python no reconoce ninguna, y aquí tampoco.
    /// </summary>
    public static class Galletas
    {
        const string Esp = @"[ \t\n\r\f\v]";
        const string Clave = @"[a-zA-Z0-9_!#%&'~`><@,:/$*+\-.^|)(?}{=]";
        const string Valor = @"[a-zA-Z0-9_!#%&'~`><@,:/$*+\-.^|)(?}{=\[\]]";

        static readonly Regex Patron = new Regex(
            @"\G" + Esp + @"*(?<key>" + Clave + @"+?)" +
            @"(" + Esp + @"*=" + Esp + @"*(?<val>" +
                @"""(?:[^\\""]|\\.)*""" +
                @"|(?:[a-zA-Z0-9_]{3,6}day|[a-zA-Z0-9_]{3})," + Esp + @"[a-zA-Z0-9_ \t\n\r\f\v-]{9,11}" + Esp + @"[0-9:]{8}" + Esp + @"GMT" +
                @"|" + Valor + @"*" +
            @"))?" + Esp + @"*(" + Esp + @"+|;|$)");

        static readonly Regex ClaveLegal = new Regex(@"^[a-zA-Z0-9!#$%&'*+\-.^_`|~:]+$");
        static readonly HashSet<string> Reservadas = new HashSet<string>(StringComparer.Ordinal)
        { "expires", "path", "comment", "domain", "max-age", "secure", "httponly", "version", "samesite" };
        static readonly HashSet<string> Banderas = new HashSet<string>(StringComparer.Ordinal) { "secure", "httponly" };
        static readonly Regex EscapeComillas = new Regex(@"\\(?:([0-3][0-7][0-7])|(.))");

        /// <summary>
        /// El valor de la cookie <paramref name="nombre"/>, o null si no está o
        /// si la cabecera no es válida (CookieError en Python).
        /// </summary>
        public static string ValorDe(string cabecera, string nombre)
        {
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0, n = cabecera.Length;
            bool vistaGalleta = false;
            // (tipo, clave, valor): 1 = atributo, 2 = clave=valor
            var leidos = new List<KeyValuePair<int, KeyValuePair<string, string>>>();
            while (i >= 0 && i < n)
            {
                Match m = Patron.Match(cabecera, i);
                if (!m.Success) break;
                string clave = m.Groups["key"].Value;
                Group g = m.Groups["val"];
                string valor = g.Success ? g.Value : null;
                i = m.Index + m.Length;
                if (clave[0] == '$')
                {
                    if (!vistaGalleta) continue;
                    leidos.Add(Par(1, clave.Substring(1), valor));
                }
                else if (Reservadas.Contains(clave.ToLowerInvariant()))
                {
                    if (!vistaGalleta) return null;
                    if (valor == null)
                    {
                        if (Banderas.Contains(clave.ToLowerInvariant())) leidos.Add(Par(1, clave, "True"));
                        else return null;
                    }
                    else leidos.Add(Par(1, clave, Desentrecomillar(valor)));
                }
                else if (valor != null)
                {
                    leidos.Add(Par(2, clave, Desentrecomillar(valor)));
                    vistaGalleta = true;
                }
                else return null;
            }
            foreach (var l in leidos)
            {
                string clave = l.Value.Key;
                if (l.Key == 1)
                {
                    // Morsel[clave] = valor: solo admite atributos conocidos.
                    if (!Reservadas.Contains(clave.ToLowerInvariant())) return null;
                }
                else
                {
                    if (Reservadas.Contains(clave.ToLowerInvariant()) || !ClaveLegal.IsMatch(clave)) return null;
                    cookies[clave] = l.Value.Value;
                }
            }
            string v;
            return cookies.TryGetValue(nombre, out v) ? v : null;
        }

        static KeyValuePair<int, KeyValuePair<string, string>> Par(int tipo, string clave, string valor)
        {
            return new KeyValuePair<int, KeyValuePair<string, string>>(tipo, new KeyValuePair<string, string>(clave, valor));
        }

        /// <summary>_unquote() de http.cookies.</summary>
        static string Desentrecomillar(string s)
        {
            if (s == null || s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') return s;
            s = s.Substring(1, s.Length - 2);
            return EscapeComillas.Replace(s, m => m.Groups[1].Success
                ? ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()
                : m.Groups[2].Value);
        }
    }
}
