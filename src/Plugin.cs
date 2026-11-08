using System.ComponentModel;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using AutomaticWeaponsmith.Patches;

namespace AutomaticWeaponsmith
{
    public enum SightPolicy
    {
        Keep,
        SameType,
        Any
    }

    // Einstiegspunkt: BepInEx findet diese Klasse automatisch und ruft Awake() beim Spielstart auf.
    [BepInPlugin("com.automaticweaponsmith", "AutomaticWeaponsmith", "1.0.2")]
    [BepInDependency("com.SPT.core", "4.1.5")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        // Einstellungen, im Spiel mit F12 änderbar.
        public static ConfigEntry<string> Language;
        public static ConfigEntry<float> BalancedRecoilWeight;
        public static ConfigEntry<int> TimeLimitMs;
        public static ConfigEntry<float> MinErgonomics;
        public static ConfigEntry<bool> AllowLaunchers;
        public static ConfigEntry<SightPolicy> Sight;
        public static ConfigEntry<int> MinMagazineSize;
        public static ConfigEntry<bool> AlwaysOptic;
        public static ConfigEntry<bool> RequireLight;
        public static ConfigEntry<bool> AllowBarter;
        public static ConfigEntry<bool> BudgetEnabled;
        public static ConfigEntry<int> Budget;

        private void Awake()
        {
            Log = Logger;

            // Sprache zuerst: Die Texte im Einstellungsmenü kommen schon aus der Sprachdatei.
            Lang.Init(Path.GetDirectoryName(Info.Location));
            OpticList.Load(Path.GetDirectoryName(Info.Location));
            Language = Config.Bind("General", "Language", Lang.Default,
                new ConfigDescription("Language of the mod. Files are in the lang folder of this mod. Restart the game to also change the names in this menu.",
                    new AcceptableValueList<string>(Lang.Available())));
            Lang.Load(Language.Value);
            Language.SettingChanged += (sender, args) => Lang.Load(Language.Value);

            // Die Speichernamen (Abschnitt + Name) bleiben englisch, damit Einstellungen beim Sprachwechsel erhalten bleiben.
            BalancedRecoilWeight = Bind("Optimizer", "Recoil weight (Balanced mode)", 1.0f, "recoil_weight", new AcceptableValueRange<float>(0.1f, 5f));
            TimeLimitMs = Bind("Optimizer", "Time limit (ms)", 1500, "time_limit", new AcceptableValueRange<int>(250, 20000));
            MinErgonomics = Bind("Optimizer", "Minimum ergonomics (Recoil mode)", 40f, "min_ergo", new AcceptableValueRange<float>(0f, 100f));
            AllowLaunchers = Bind("Optimizer", "Allow grenade launchers", false, "launchers");
            Sight = Bind("Optimizer", "Sight", SightPolicy.Keep, "sight");
            // Nicht 0-100, sonst zeigt der Config-Manager "%" an.
            MinMagazineSize = Bind("Optimizer", "Minimum magazine size", 30, "min_mag", new AcceptableValueRange<int>(0, 150));
            AlwaysOptic = Bind("Optimizer", "Always use an optic (no iron sights)", true, "always_optic");
            RequireLight = Bind("Optimizer", "Always add a flashlight", true, "flashlight");

            AllowBarter = Bind("Prices", "Allow barter trades", false, "barter");
            BudgetEnabled = Bind("Prices", "Use budget limit", false, "use_budget");
            Budget = Bind("Prices", "Budget (roubles)", 100000, "budget", new AcceptableValueRange<int>(0, 10000000));

            new EditBuildScreenButtonPatch().Enable();
            new EditBuildScreenButtonPositionPatch().Enable();
            Log.LogInfo("AutomaticWeaponsmith loaded.");
        }

        private ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string textId, AcceptableValueBase range = null)
        {
            return Config.Bind(section, key, defaultValue, new ConfigDescription(Lang.T($"setting.{textId}.desc"), range,
                new DisplayNameAttribute(Lang.T($"setting.{textId}.name")),
                new CategoryAttribute(Lang.T("section." + section.ToLowerInvariant()))));
        }
    }
}
