using System;
using System.Collections.Generic;

namespace MiCine
{
    /// <summary>
    /// Dónde se descargan las apps (las mismas direcciones que servidor.py). Se publican en GitHub;
    /// las cortas de MiCine (/tele, /movil) llevan a la última versión: en la Fire TV basta con escribir
    /// «192.168.1.20:8080/tele» en la app Downloader.
    /// </summary>
    public static class Apps
    {
        public const string GITHUB = "https://github.com/Alvaropegr/MiCine";
        public const string FIRE_TV = GITHUB + "/releases/latest/download/MiCine-firetv.apk";
        public const string MOVIL = GITHUB + "/releases/latest/download/MiCine.apk";

        public static readonly Dictionary<string, string> Descargas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "/tele", FIRE_TV },
            { "/movil", MOVIL },
        };
    }
}
