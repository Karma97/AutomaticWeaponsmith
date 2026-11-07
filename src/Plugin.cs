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
    [BepInPlugin("com.automaticweaponsmith", "AutomaticWeaponsmith", "1.0.0")]
    [BepInDependency("com.SPT.core", "4.1.6")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        // Einstellungen, im Spiel mit F12 änderbar.
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

            BalancedRecoilWeight = Config.Bind("Optimizer", "Recoil weight (Balanced mode)", 1.0f,
                new ConfigDescription("Balanced mode only: how many ergonomics points 1 % less recoil is worth.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            TimeLimitMs = Config.Bind("Optimizer", "Time limit (ms)", 1500,
                new ConfigDescription("After this time, part conflicts are solved quickly instead of perfectly.",
                    new AcceptableValueRange<int>(250, 20000)));
            MinErgonomics = Config.Bind("Optimizer", "Minimum ergonomics (Recoil mode)", 40f,
                new ConfigDescription("In Recoil mode, ergonomics will not go below this value. 0 = off.",
                    new AcceptableValueRange<float>(0f, 100f)));
            AllowLaunchers = Config.Bind("Optimizer", "Allow grenade launchers", false,
                "Allow underbarrel grenade launchers (much less recoil, but very heavy).");
            Sight = Config.Bind("Optimizer", "Sight", SightPolicy.Keep,
                "Keep = your current sight stays on the weapon (if it does not fit, one of the same type is used). " +
                "SameType = any sight of the same type (scope / red dot / iron sights). Any = no rule.");
            MinMagazineSize = Config.Bind("Optimizer", "Minimum magazine size", 30,
                new ConfigDescription("Only used if the build has no magazine. Otherwise the magazine is at least as big as the current one.",
                    new AcceptableValueRange<int>(0, 150))); // not 0-100, otherwise the config manager shows "%"
            AlwaysOptic = Config.Bind("Optimizer", "Always use an optic (no iron sights)", true,
                "On = the build always gets a red dot, holo or scope. Iron sights (and carry handles) are never used.");
            RequireLight = Config.Bind("Optimizer", "Always add a flashlight", true,
                "On = the build always gets a flashlight (a flashlight or a tactical device with a white light).");

            AllowBarter = Config.Bind("Prices", "Allow barter trades", false,
                "Also use trader offers that are paid with items instead of money.");
            BudgetEnabled = Config.Bind("Prices", "Use budget limit", false,
                "On = the build may not cost more than the budget (parts from your stash are free).");
            Budget = Config.Bind("Prices", "Budget (roubles)", 100000,
                new ConfigDescription("Maximum cost for all parts you need to buy.", new AcceptableValueRange<int>(0, 10000000)));

            new EditBuildScreenButtonPatch().Enable();
            new EditBuildScreenButtonPositionPatch().Enable();
            Log.LogInfo("AutomaticWeaponsmith loaded.");
        }
    }
}
