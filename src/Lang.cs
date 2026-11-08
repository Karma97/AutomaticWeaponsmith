using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AutomaticWeaponsmith
{
    // Alle Texte, die Spieler sehen, kommen aus Sprachdateien (lang\<sprache>.txt, Format: name = text).
    // Englisch steckt zusätzlich fest in der DLL: Fehlt eine Zeile oder die ganze Datei, wird Englisch angezeigt.
    public static class Lang
    {
        public const string Default = "en";

        private static Dictionary<string, string> _english = new Dictionary<string, string>();
        private static Dictionary<string, string> _texts = new Dictionary<string, string>();
        private static string _folder;

        public static void Init(string pluginFolder)
        {
            _folder = Path.Combine(pluginFolder, "lang");
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("en.txt"))
                using (var reader = new StreamReader(stream))
                {
                    _english = Parse(reader.ReadToEnd().Split('\n'));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not read the built-in English texts: " + ex.Message);
            }
            _texts = _english;
        }

        // Alle Sprachdateien im lang-Ordner, Englisch immer zuerst.
        public static string[] Available()
        {
            var files = Enumerable.Empty<string>();
            try
            {
                if (Directory.Exists(_folder))
                {
                    files = Directory.GetFiles(_folder, "*.txt").Select(Path.GetFileNameWithoutExtension).ToList();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read the language folder: " + ex.Message);
            }
            return new[] { Default }
                .Concat(files.Where(f => !f.Equals(Default, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        public static void Load(string language)
        {
            var merged = new Dictionary<string, string>(_english);
            string path = Path.Combine(_folder, language + ".txt");
            if (File.Exists(path))
            {
                try
                {
                    var file = Parse(File.ReadAllLines(path));
                    foreach (var kv in file)
                    {
                        merged[kv.Key] = kv.Value;
                    }
                    int missing = _english.Keys.Count(k => !file.ContainsKey(k));
                    Plugin.Log.LogInfo($"Language '{language}' loaded, {missing} line(s) missing (shown in English).");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Could not read language file '{language}', using English: {ex.Message}");
                }
            }
            else if (language != Default)
            {
                Plugin.Log.LogWarning($"Language file '{language}' not found, using English.");
            }
            _texts = merged; // Wird im Ganzen getauscht, der Rechen-Thread sieht nie eine halb geladene Sprache.
        }

        // Text holen und {platzhalter} ersetzen, z. B. Lang.T("note.iron_replaced", ("sight", name)).
        public static string T(string key, params (string Name, object Value)[] values)
        {
            if (!_texts.TryGetValue(key, out string text))
            {
                return key;
            }
            foreach (var (name, value) in values)
            {
                text = text.Replace("{" + name + "}", value?.ToString() ?? "");
            }
            return text;
        }

        private static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, string>();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                int separator = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || separator <= 0)
                {
                    continue;
                }
                result[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim().Replace("\\n", "\n");
            }
            return result;
        }
    }
}
