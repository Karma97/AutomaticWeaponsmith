using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using TMPro;

namespace AutomaticWeaponsmith
{
    // Schritt 4: Setzt den Vorschlag direkt in die Waffe im Build-Editor (nur Baukasten, nichts wird gekauft
    // oder gespeichert) und fragt dann: behalten oder rückgängig?
    public static class PreviewApplier
    {
        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(EditBuildScreen), "Item");
        private static readonly FieldInfo UiContextField = AccessTools.Field(typeof(EditBuildScreen), "_itemUiContext");

        public static void Apply(EditBuildScreen screen, EditBuildManipulation manipulation, OptimizeMode mode,
            List<SlotChoice> result, Dictionary<SlotChoice, Purchase> purchases, List<string> notes,
            float predictedErgo, float predictedRecoil)
        {
            var weapon = ItemField.GetValue(screen) as Weapon;
            var uiContext = UiContextField.GetValue(screen) as ItemUiContext;
            if (weapon == null || uiContext == null)
            {
                Plugin.Log.LogWarning("Preview: weapon or UI not found.");
                return;
            }

            // Werte und Kopie VOR dem Umbau merken.
            var t = weapon.Template;
            float ergoBefore = Clamp(weapon.ErgonomicsTotal);
            float vertBefore = t.RecoilForceUp * (1f + weapon.RecoilDelta);
            float horBefore = t.RecoilForceBack * (1f + weapon.RecoilDelta);
            Item original = weapon.CloneItem();

            // 1. Alles rausnehmen, was ersetzt oder entfernt wird. 2. Neue Teile einsetzen (zweimal, falls die
            // Reihenfolge eine Rolle spielt). Fehler sammeln statt abzubrechen.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var errors = new List<string>();
            Strip(manipulation, weapon, result);
            AddParts(manipulation, weapon, result, null);
            AddParts(manipulation, weapon, result, errors);
            long buildMs = watch.ElapsedMilliseconds;

            screen.WeaponUpdate();
            Plugin.Log.LogInfo($"Time: placing parts {buildMs} ms, updating view {watch.ElapsedMilliseconds - buildMs} ms");

            float ergoAfter = Clamp(weapon.ErgonomicsTotal);
            float vertAfter = t.RecoilForceUp * (1f + weapon.RecoilDelta);
            float horAfter = t.RecoilForceBack * (1f + weapon.RecoilDelta);
            float vertPredicted = t.RecoilForceUp * (1f + predictedRecoil / 100f);

            Plugin.Log.LogInfo($"Preview applied. Predicted: ergo {Clamp(predictedErgo):0.#}, vertical {vertPredicted:0} | " +
                               $"Game says: ergo {ergoAfter:0.#}, vertical {vertAfter:0}, horizontal {horAfter:0} | Errors: {errors.Count}");
            foreach (var e in errors)
            {
                Plugin.Log.LogWarning("Preview: " + e);
            }

            string message = BuildMessage(mode, ergoBefore, vertBefore, horBefore, ergoAfter, vertAfter, horAfter, purchases, notes, errors);

            uiContext.ShowMessageWindow(message,
                acceptAction: () =>
                {
                    Plugin.Log.LogInfo("Build kept.");
                },
                cancelAction: () =>
                {
                    screen.UpdateItem(original);
                    Plugin.Log.LogInfo("Build undone.");
                },
                caption: "AUTO BUILD",
                alignment: TextAlignmentOptions.Left);
        }

        private static void Strip(EditBuildManipulation manipulation, CompoundItem parent, List<SlotChoice> desired)
        {
            foreach (var choice in desired)
            {
                var slot = FindSlot(parent, choice);
                var current = slot?.ContainedItem;
                if (current == null)
                {
                    continue;
                }
                if (choice.Part == null || current.TemplateId != choice.Part.TemplateId)
                {
                    manipulation.Select(null, slot, false, out _);
                }
                else if (current is CompoundItem compound)
                {
                    Strip(manipulation, compound, choice.Children);
                }
            }

            // Was in einem Slot steckt, den der Plan gar nicht erwähnt, kommt auch raus
            // (sonst bleiben alte Teile hängen und können mit neuen kollidieren).
            var planned = new HashSet<string>(desired.Select(c => c.Slot.ID));
            foreach (var slot in parent.Slots)
            {
                if (slot.ContainedItem != null && !planned.Contains(slot.ID) && !slot.Required)
                {
                    manipulation.Select(null, slot, false, out _);
                }
            }
        }

        private static void AddParts(EditBuildManipulation manipulation, CompoundItem parent, List<SlotChoice> desired, List<string> errors)
        {
            foreach (var choice in desired)
            {
                if (choice.Part == null)
                {
                    continue;
                }
                var slot = FindSlot(parent, choice);
                if (slot == null)
                {
                    errors?.Add($"Slot {choice.Slot.ID} on {Name(parent)} not found");
                    continue;
                }
                if (slot.ContainedItem == null && !manipulation.Select(choice.Part, slot, false, out var error))
                {
                    errors?.Add($"{Name(choice.Part)} does not fit into {slot.ID} ({error})");
                    continue;
                }
                if (slot.ContainedItem is CompoundItem compound)
                {
                    AddParts(manipulation, compound, choice.Children, errors);
                }
            }
        }

        private static Slot FindSlot(CompoundItem parent, SlotChoice choice)
        {
            return parent.Slots.FirstOrDefault(s => s.ID == choice.Slot.ID);
        }

        private static string BuildMessage(OptimizeMode mode, float ergoBefore, float vertBefore, float horBefore,
            float ergoAfter, float vertAfter, float horAfter, Dictionary<SlotChoice, Purchase> purchases, List<string> notes,
            List<string> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Mode: {ModeName(mode)}");
            sb.AppendLine();
            sb.AppendLine($"Ergonomics:        {ergoBefore:0.#}  ->  {ergoAfter:0.#}");
            sb.AppendLine($"Vertical recoil:   {vertBefore:0}  ->  {vertAfter:0}");
            sb.AppendLine($"Horizontal recoil: {horBefore:0}  ->  {horAfter:0}");
            sb.AppendLine();

            int fromStash = purchases.Count(p => p.Value.Source == PartSource.Stash);
            sb.AppendLine($"From your stash: {fromStash} {(fromStash == 1 ? "part" : "parts")}");

            // Händler (Bargeld), Flohmarkt, Tausch jeweils als eigene Zeile mit Summe.
            var cash = purchases.Where(p => p.Value.Source == PartSource.Trader && p.Value.Offer?.IsBarter != true).ToList();
            var barter = purchases.Where(p => p.Value.Source == PartSource.Trader && p.Value.Offer?.IsBarter == true).ToList();
            var flea = purchases.Where(p => p.Value.Source == PartSource.Flea).ToList();
            var missing = purchases.Where(p => p.Value.Source == PartSource.Unavailable).ToList();
            AppendGroup(sb, "Traders", cash);
            AppendGroup(sb, "Flea market", flea);
            foreach (var b in barter)
            {
                sb.AppendLine($"Barter at {b.Value.Offer.TraderName}: {Name(b.Key.Part)} for {b.Value.Offer.BarterText}");
            }
            if (missing.Count > 0)
            {
                sb.AppendLine($"Not available: {string.Join(", ", missing.Select(m => Name(m.Key.Part)))}");
            }

            sb.AppendLine();
            sb.AppendLine($"Total cost: about {purchases.Values.Sum(p => p.Price):N0} ₽" + (barter.Count > 0 ? " (barter estimated)" : ""));

            foreach (var note in notes)
            {
                sb.AppendLine(note);
            }

            if (errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{errors.Count} part(s) could not be attached, see the log for details.");
            }

            sb.AppendLine();
            sb.AppendLine("YES = keep this build (buy and assemble with ASSEMBLE or FIND PARTS)");
            sb.AppendLine("NO = undo");
            return sb.ToString();
        }

        private static string Name(Item item) => item.ShortName.Localized();

        private static float Clamp(float ergo) => Math.Max(0f, Math.Min(100f, ergo));

        private static string ModeName(OptimizeMode mode)
        {
            switch (mode)
            {
                case OptimizeMode.Recoil: return "Recoil";
                case OptimizeMode.Ergonomics: return "Ergonomics";
                default: return "Balanced";
            }
        }

        private static void AppendGroup(StringBuilder sb, string title, List<KeyValuePair<SlotChoice, Purchase>> group)
        {
            if (group.Count == 0)
            {
                return;
            }
            var names = group.Select(p => Name(p.Key.Part)).GroupBy(n => n)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            sb.AppendLine($"{title} ({group.Count}, {group.Sum(p => p.Value.Price):N0} ₽): {string.Join(", ", names)}");
        }
    }
}
