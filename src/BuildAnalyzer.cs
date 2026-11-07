using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace AutomaticWeaponsmith
{
    // Liest aus, welche Anbauteile im Stash frei verfügbar sind.
    public static class BuildAnalyzer
    {
        // Dieses Feld ist im Spiel "private", darum holen wir es per Reflection.
        private static readonly FieldInfo PlayerItemsField = AccessTools.Field(typeof(EditBuildScreen), "_playerItems");

        // Wie viele Stück pro Teil-Typ (TemplateId) im Stash frei verfügbar sind.
        // Gleiche Regeln wie das Spiel: keine angepinnten/gesperrten Teile, nichts, was an einer anderen Waffe hängt.
        public static Dictionary<MongoID, int> CountStashParts(EditBuildScreen screen)
        {
            var counts = new Dictionary<MongoID, int>();
            if (!(PlayerItemsField?.GetValue(screen) is List<Item> playerItems))
            {
                return counts;
            }
            foreach (var item in playerItems.Where(screen.IsAcceptableItem).OfType<Mod>())
            {
                counts.TryGetValue(item.TemplateId, out int n);
                counts[item.TemplateId] = n + 1;
            }
            return counts;
        }
    }
}
