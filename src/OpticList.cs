using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AutomaticWeaponsmith
{
    // Rangliste der Optiken (optics.txt, eine Item-ID pro Zeile). Die Reihenfolge entscheidet, nicht die Werte.
    // Eine Standard-Liste steckt in der DLL, falls die Datei fehlt oder leer ist.
    public static class OpticList
    {
        public static IReadOnlyList<string> Ids { get; private set; } = new List<string>();

        public static void Load(string pluginFolder)
        {
            var ids = new List<string>();
            string path = Path.Combine(pluginFolder, "optics.txt");
            try
            {
                if (File.Exists(path))
                {
                    ids = Parse(File.ReadAllLines(path));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read optics.txt, using the built-in list: " + ex.Message);
            }

            if (ids.Count == 0)
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("optics.txt"))
                using (var reader = new StreamReader(stream))
                {
                    ids = Parse(reader.ReadToEnd().Split('\n'));
                }
            }
            Ids = ids;
            Plugin.Log.LogInfo($"Optic list loaded: {ids.Count} optics.");
        }

        private static List<string> Parse(IEnumerable<string> lines)
        {
            var result = new List<string>();
            foreach (var raw in lines)
            {
                int comment = raw.IndexOf('#');
                string id = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
                if (id.Length == 0)
                {
                    continue;
                }
                if (id.Length != 24 || !id.All(Uri.IsHexDigit))
                {
                    Plugin.Log.LogWarning($"optics.txt: '{id}' is not a valid item ID, skipped.");
                    continue;
                }
                if (!result.Contains(id))
                {
                    result.Add(id);
                }
            }
            return result;
        }
    }
}
