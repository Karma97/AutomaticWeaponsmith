using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

namespace AutomaticWeaponsmith
{
    public enum OptimizeMode
    {
        Recoil,
        Ergonomics,
        Balanced
    }

    // Woher ein Teil kommt. Reihenfolge = Vorliebe bei Gleichstand (Stash zuerst).
    public enum PartSource
    {
        Stash = 0,
        Trader = 1,
        Flea = 2,
        Unavailable = 3
    }

    // Ein Ergebnis für einen Slot: welches Teil (oder leer) und was in seinen Unter-Slots steckt.
    public class SlotChoice
    {
        public Slot Slot;
        public Item Part;                 // null = Slot bleibt leer
        public PartSource Source;
        public List<SlotChoice> Children = new List<SlotChoice>();
        public float Ergonomics;          // Summe für das ganze Teil inkl. Unter-Slots
        public float Recoil;              // Summe in % inkl. Unter-Slots
        public int Purchases;             // Wie viele Teile gekauft werden müssten
        public float Cost;                // Kosten in Rubel inkl. Unter-Slots (Stash = 0)
        public float PartPrice;           // Preis nur für dieses Teil
        public TraderOffer Offer;         // Händler-Angebot, falls beim Händler gekauft
        public int Mask;                  // Was dieses Teil inkl. Unter-Slots mitbringt: 1 = passendes Visier, 2 = Lampe

        public IEnumerable<SlotChoice> Flatten()
        {
            yield return this;
            foreach (var child in Children)
            {
                foreach (var c in child.Flatten())
                {
                    yield return c;
                }
            }
        }
    }

    // Pro Slot vier Bestwerte, je nachdem was mindestens drinstecken muss (Index = Bitmaske):
    // 0 = egal, 1 = mit Visier, 2 = mit Lampe, 3 = mit beidem. null = in diesem Slot nicht möglich.
    public class SlotResult
    {
        public readonly SlotChoice[] Best = new SlotChoice[4];

        public SlotChoice Any
        {
            get => Best[0];
            set => Best[0] = value;
        }
    }

    // Gewichtung: Punkte = Ergo * Ergo-Gewicht - Rückstoß% * Rückstoß-Gewicht - Rubel * Kosten-Gewicht. Höher = besser.
    // Das Kosten-Gewicht ist normalerweise 0 und wird nur fürs Budget-Limit hochgedreht.
    public struct Weights
    {
        public float Ergo;
        public float Recoil;
        public float Cost;

        public Weights(float ergo, float recoil, float cost = 0f)
        {
            Ergo = ergo;
            Recoil = recoil;
            Cost = cost;
        }

        public Weights WithCost(float cost) => new Weights(Ergo, Recoil, cost);

        public float Score(float ergonomics, float recoil, float cost) => ergonomics * Ergo - recoil * Recoil - cost * Cost;

        public float Score(SlotChoice c) => Score(c.Ergonomics, c.Recoil, c.Cost);

        public float Score(IEnumerable<SlotChoice> choices)
        {
            var self = this;
            return choices.Sum(c => self.Score(c));
        }

        public override string ToString() => Ergo.ToString("R") + "|" + Recoil.ToString("R") + "|" + Cost.ToString("R");
    }

    // Wo und für wie viel ein Teil zu bekommen ist.
    public struct Purchase
    {
        public PartSource Source;
        public float Price;
        public TraderOffer Offer;
    }

    // Sucht pro Slot das beste Teil, von unten nach oben (erst die Unter-Slots, dann das Teil selbst).
    // Weil Ergonomie und Rückstoß-% einfach addiert werden, ist das beste Teil eines Slots unabhängig
    // vom Rest der Waffe. Dadurch müssen wir NICHT alle Kombinationen durchprobieren.
    public class BuildOptimizer
    {
        private const int MaxDepth = 10;

        private readonly EditBuildManipulation _manipulation;
        private readonly PartIndex _index;
        private readonly Dictionary<MongoID, int> _stash;
        private readonly bool _fleaAvailable;
        private readonly bool _allowLaunchers;
        private readonly int _requiredSightRank;   // 0 = egal, 1 = Kimme/Korn, 2 = Rotpunkt/Holo, 3 = Zielfernrohr
        private readonly int _minMagazine;          // Mindest-Schusszahl fürs Magazin
        private readonly MongoID? _requiredSightTemplate; // Genau dieses Visier muss drin bleiben (wenn gesetzt)
        private readonly PriceData _prices;
        private readonly bool _requireLight;        // Es muss eine Lampe dran sein
        private readonly bool _noIronSights;        // Kimme/Korn nie einbauen (außer in Pflicht-Slots)
        private readonly Dictionary<MongoID, Purchase> _purchaseCache = new Dictionary<MongoID, Purchase>();

        // Zwischenergebnisse, die über mehrere Rechenläufe gültig bleiben (siehe Lookup).
        private readonly Dictionary<string, List<MemoEntry>> _memo = new Dictionary<string, List<MemoEntry>>();
        private string _memoWeights;

        private Weights _weights;
        private HashSet<MongoID> _banned = new HashSet<MongoID>();

        private class MemoEntry
        {
            public HashSet<MongoID> BannedAt;   // Sperrliste, mit der das Ergebnis berechnet wurde
            public HashSet<MongoID> Used;       // Alle Teile, die im Ergebnis stecken
            public SlotResult Result;
        }

        public BuildOptimizer(EditBuildManipulation manipulation, PartIndex index, Dictionary<MongoID, int> stash,
            bool fleaAvailable, bool allowLaunchers, int requiredSightRank, int minMagazine, MongoID? requiredSightTemplate,
            PriceData prices, bool requireLight, bool noIronSights)
        {
            _prices = prices;
            _requireLight = requireLight;
            _noIronSights = noIronSights;
            _requiredSightTemplate = requiredSightTemplate;
            _manipulation = manipulation;
            _index = index;
            _stash = stash;
            _fleaAvailable = fleaAvailable;
            _allowLaunchers = allowLaunchers;
            _requiredSightRank = requiredSightRank;
            _minMagazine = minMagazine;
        }

        public int SolveCount { get; private set; }
        public int MemoHits { get; private set; }
        public bool SightRequirementFailed { get; private set; }
        public bool LightRequirementFailed { get; private set; }

        private bool SightRuleActive => _requiredSightTemplate.HasValue || _requiredSightRank > 0;

        private const int SightBit = 1;
        private const int LightBit = 2;
        private const int FilledBit = 4; // nur intern: mindestens ein Unter-Slot belegt

        private int RequiredMask => (SightRuleActive ? SightBit : 0) | (_requireLight ? LightBit : 0);

        // Echte Taschenlampen (weißes Licht). Viele "Kombi-Geräte" sind nur Laser oder IR-Strahler (z. B. TBL, PEQ-15),
        // obwohl das Spiel sie in dieselbe Kategorie steckt. Darum: alle reinen Lampen + diese Kombi-Geräte mit Lampe.
        private static readonly HashSet<string> CombosWithWhiteLight = new HashSet<string>
        {
            "560d657b4bdc2da74d8b4572", // Zenit Klesch-2P
            "56def37dd2720bec348b456a", // SureFire X400 Ultra
            "5a7b483fe899ef0016170d15", // SureFire XC1
            "5a800961159bd4315e3a1657", // Glock GTL 21
            "5b07dd285acfc4001754240d", // Steiner LAS/TAC 2
            "5b3a337e5acfc4704b4a19a0", // Zenit Klesch-2U
            "5d2369418abbc306c62e0c80", // Steiner DBAL-PL
            "626becf9582c3e319310b837", // Insight WMX200
            "6272370ee4013c5d7e31f418", // Olight Baldr Pro
            "6272379924e29f06af4d5ecb", // Olight Baldr Pro (Tan)
        };

        public static bool IsLight(Item item) =>
            item is Flashlight || (item is TacticalCombo && CombosWithWhiteLight.Contains(item.TemplateId.ToString()));

        // Was ein einzelnes Teil selbst beisteuert (ohne Unter-Slots).
        private int Provides(Item part)
        {
            int mask = 0;
            if (SightRuleActive && IsRequiredSight(part))
            {
                mask |= SightBit;
            }
            if (_requireLight && IsLight(part))
            {
                mask |= LightBit;
            }
            return mask;
        }

        public List<SlotChoice> Solve(Weapon weapon, Weights weights, HashSet<MongoID> banned)
        {
            _weights = weights;
            _banned = banned;
            if (_memoWeights != weights.ToString())
            {
                _memo.Clear();
                _memoWeights = weights.ToString();
            }
            SolveCount++;

            var results = new List<SlotResult>();
            foreach (var slot in weapon.Slots)
            {
                var r = SolveSlot(slot, 0, isCurrentBuild: true);
                if (r?.Any != null)
                {
                    results.Add(r);
                }
            }

            // Beste Kombination, die alle Bedingungen erfüllt. Geht das nicht, erst auf die Lampe verzichten,
            // dann aufs Visier.
            var combos = Combine(results, 0);
            int need = RequiredMask;
            foreach (int want in new[] { need, need & SightBit, need & LightBit, 0 })
            {
                SlotChoice best = null;
                foreach (var kv in combos)
                {
                    if ((kv.Key & want) == want && (best == null || IsBetter(kv.Value, best)))
                    {
                        best = kv.Value;
                    }
                }
                if (best != null)
                {
                    SightRequirementFailed = (need & SightBit) != 0 && (best.Mask & SightBit) == 0;
                    LightRequirementFailed = (need & LightBit) != 0 && (best.Mask & LightBit) == 0;
                    return best.Children;
                }
            }
            return new List<SlotChoice>();
        }

        // Kombiniert die Unter-Slots so, dass es für jede Bitmaske (was steckt drin) die beste Variante gibt.
        // Ergebnis: Maske -> Summe (Children, Werte, Kosten). Teil/Slot sind darin nicht gesetzt.
        private Dictionary<int, SlotChoice> Combine(List<SlotResult> children, int startMask)
        {
            var states = new Dictionary<int, SlotChoice> { [startMask] = new SlotChoice { Mask = startMask } };
            foreach (var child in children)
            {
                var next = new Dictionary<int, SlotChoice>();
                foreach (var kv in states)
                {
                    var seen = new HashSet<SlotChoice>();
                    foreach (var variant in child.Best)
                    {
                        if (variant == null || !seen.Add(variant))
                        {
                            continue;
                        }
                        int mask = kv.Key | variant.Mask | (variant.Part != null ? FilledBit : 0);
                        var sum = kv.Value;
                        var candidate = new SlotChoice
                        {
                            Mask = mask,
                            Children = new List<SlotChoice>(sum.Children) { variant },
                            Ergonomics = sum.Ergonomics + variant.Ergonomics,
                            Recoil = sum.Recoil + variant.Recoil,
                            Purchases = sum.Purchases + variant.Purchases,
                            Cost = sum.Cost + variant.Cost
                        };
                        if (!next.TryGetValue(mask, out var existing) || IsBetter(candidate, existing))
                        {
                            next[mask] = candidate;
                        }
                    }
                }
                states = next;
            }
            return states;
        }

        public bool ContainsLight(SlotChoice choice) => choice.Flatten().Any(c => c.Part != null && IsLight(c.Part));

        public static int SightRank(Item item)
        {
            switch (item)
            {
                case OpticScope _:
                case AssaultScope _:
                case SpecialScope _:
                    return 3;
                case Collimator _:
                case CompactCollimator _:
                    return 2;
                case IronSight _:
                    return 1;
                default:
                    return 0;
            }
        }

        // Erfüllt dieses Teil die Visier-Regel? Entweder genau das gewünschte Visier oder mindestens die Visier-Art.
        private bool IsRequiredSight(Item part)
        {
            if (_requiredSightTemplate.HasValue)
            {
                return part.TemplateId == _requiredSightTemplate.Value;
            }
            return SightRank(part) >= _requiredSightRank;
        }

        public bool ContainsSight(SlotChoice choice) =>
            choice.Flatten().Any(c => c.Part != null && IsRequiredSight(c.Part));

        public Purchase SourceOf(Item item)
        {
            if (_stash.TryGetValue(item.TemplateId, out int n) && n > 0)
            {
                return new Purchase { Source = PartSource.Stash, Price = 0f };
            }
            return Buy(item);
        }

        // Günstigster Weg, das Teil zu kaufen: Händler oder Flohmarkt.
        public Purchase Buy(Item item)
        {
            if (_purchaseCache.TryGetValue(item.TemplateId, out var cached))
            {
                return cached;
            }

            var best = new Purchase { Source = PartSource.Unavailable, Price = float.MaxValue };

            if (_prices != null && _prices.TradersLoaded)
            {
                if (_prices.TraderOffers.TryGetValue(item.TemplateId, out var offer))
                {
                    best = new Purchase { Source = PartSource.Trader, Price = offer.PriceRub, Offer = offer };
                }
            }
            else if (_manipulation.GetItemAvailability(item) == EEditBuildItemAvailability.AvailableFromMerchants)
            {
                // Notlösung ohne Händler-Sortiment: Händler ja, Preis unbekannt.
                best = new Purchase { Source = PartSource.Trader, Price = 0f };
            }

            if (_fleaAvailable && item.Template.CanSellOnRagfair && !item.Template.QuestItem
                && (_prices == null || !_prices.IsFleaBlocked(item)))
            {
                float fleaPrice = 0f;
                bool known = _prices != null && _prices.TryFleaPrice(item, out fleaPrice);
                if (known && fleaPrice < best.Price)
                {
                    best = new Purchase { Source = PartSource.Flea, Price = fleaPrice };
                }
                else if (!known && best.Source == PartSource.Unavailable)
                {
                    best = new Purchase { Source = PartSource.Flea, Price = 0f };
                }
            }

            if (best.Source == PartSource.Unavailable)
            {
                best.Price = 0f;
            }
            _purchaseCache[item.TemplateId] = best;
            return best;
        }

        // Ein gespeichertes Ergebnis darf wiederverwendet werden, wenn
        // (1) jetzt mindestens alles gesperrt ist, was damals gesperrt war (weniger Auswahl kann nicht besser werden), und
        // (2) keins der damals gewählten Teile jetzt gesperrt ist (dann ist es immer noch möglich und damit immer noch das beste).
        private bool Lookup(string key, out SlotResult result)
        {
            result = null;
            if (!_memo.TryGetValue(key, out var entries))
            {
                return false;
            }
            foreach (var e in entries)
            {
                if (e.BannedAt.IsSubsetOf(_banned) && !e.Used.Overlaps(_banned))
                {
                    MemoHits++;
                    result = e.Result;
                    return true;
                }
            }
            return false;
        }

        private void Store(string key, SlotResult result)
        {
            var used = new HashSet<MongoID>();
            foreach (var choice in result?.Best ?? new SlotChoice[0])
            {
                if (choice == null)
                {
                    continue;
                }
                foreach (var c in choice.Flatten())
                {
                    if (c.Part != null)
                    {
                        used.Add(c.Part.TemplateId);
                    }
                }
            }
            if (!_memo.TryGetValue(key, out var entries))
            {
                entries = new List<MemoEntry>();
                _memo[key] = entries;
            }
            entries.Add(new MemoEntry { BannedAt = new HashSet<MongoID>(_banned), Used = used, Result = result });
        }

        // isCurrentBuild = dieser Slot gehört zu einem Teil, das gerade wirklich im Build steckt.
        private SlotResult SolveSlot(Slot slot, int depth, bool isCurrentBuild)
        {
            Item contained = isCurrentBuild ? slot.ContainedItem : null;

            // Kimme/Korn (auch Tragegriff) in einem freiwilligen Slot: nicht festhalten, darf raus.
            if (_noIronSights && contained is IronSight && !slot.Required)
            {
                contained = null;
            }

            // Slot muss belegt bleiben, wenn er Pflicht ist oder im aktuellen Build schon belegt ist.
            bool mustFill = slot.Required || contained != null;

            string key = slot.ParentItem.TemplateId + "|" + slot.ID + "|" + mustFill + "|" + (contained?.TemplateId.ToString() ?? "");
            if (Lookup(key, out var cached))
            {
                return cached;
            }

            var empty = new SlotChoice { Slot = slot, Part = null, Source = PartSource.Stash };
            var result = new SlotResult { Any = mustFill ? null : empty };

            // Granatwerfer nur, wenn erlaubt oder schon eingebaut.
            if (!_allowLaunchers && contained == null && !mustFill && slot.ID.StartsWith("mod_launcher"))
            {
                Store(key, result);
                return result;
            }

            var candidates = new List<Item>();
            if (contained != null)
            {
                candidates.Add(contained);
            }
            candidates.AddRange(_index.Candidates(slot));
            candidates = FilterMagazines(candidates, contained);

            SlotResult fallback = null;
            var seen = new HashSet<MongoID>();
            foreach (var part in candidates)
            {
                if (!seen.Add(part.TemplateId) || _banned.Contains(part.TemplateId))
                {
                    continue;
                }

                if (_noIronSights && part is IronSight && !slot.Required)
                {
                    continue;
                }

                bool isContained = part == contained;
                Purchase purchase = SourceOf(part);
                if (purchase.Source == PartSource.Unavailable && !isContained)
                {
                    continue;
                }

                var option = BuildOption(slot, part, purchase, depth, isContained);
                if (option == null)
                {
                    continue;
                }

                if (purchase.Source == PartSource.Unavailable)
                {
                    fallback = option; // Aktuelles Teil, aber nirgends zu bekommen: nur als Notlösung
                    continue;
                }

                for (int m = 0; m < 4; m++)
                {
                    var o = option.Best[m];
                    if (o != null && (result.Best[m] == null || IsBetter(o, result.Best[m])))
                    {
                        result.Best[m] = o;
                    }
                }
            }

            if (result.Any == null && fallback != null)
            {
                result = fallback;
            }
            // Nicht-Pflicht-Slot, für den nichts mehr erlaubt ist (z. B. weil das Teil wegen eines Konflikts
            // gesperrt wurde): dann lieber leer machen, statt das alte Teil stehen zu lassen.
            if (result.Any == null && !slot.Required)
            {
                result.Any = empty;
            }
            if (result.Any == null)
            {
                result = null; // Pflicht-Slot, der sich nicht füllen lässt
            }

            Store(key, result);
            return result;
        }

        // Magazin: mindestens so groß wie gefordert. Gibt es keins in der Größe, nehmen wir alle.
        private List<Item> FilterMagazines(List<Item> candidates, Item contained)
        {
            if (_minMagazine <= 0 || !candidates.Any(c => c is Magazine))
            {
                return candidates;
            }
            var bigEnough = candidates.Where(c => !(c is Magazine m) || m.MaxCount >= _minMagazine).ToList();
            return bigEnough.Any(c => c is Magazine) ? bigEnough : candidates;
        }

        private SlotResult BuildOption(Slot slot, Item part, Purchase purchase, int depth, bool isContained)
        {
            var childResults = new List<SlotResult>();
            if (part is CompoundItem compound && depth < MaxDepth)
            {
                foreach (var subSlot in compound.Slots)
                {
                    var child = SolveSlot(subSlot, depth + 1, isContained);
                    if (child?.Any == null)
                    {
                        if (subSlot.Required)
                        {
                            return null; // Pflicht-Unterslot nicht füllbar -> Teil unbrauchbar
                        }
                        continue;
                    }
                    childResults.Add(child);
                }
            }

            float ownErgo = 0f, ownRecoil = 0f;
            if (part is Mod mod)
            {
                ownErgo = mod.Template.Ergonomics;
                ownRecoil = mod.Template.Recoil;
            }
            int ownPurchases = purchase.Source == PartSource.Stash ? 0 : 1;

            // Leere Halterungen (z. B. Zielfernrohr-Montage ohne Optik) sind nutzlos: Eine Halterung muss
            // etwas tragen. Ausnahme: Sie steckt jetzt schon so im Build.
            bool mountMustCarry = part is Mount && !isContained;

            var option = new SlotResult();
            foreach (var kv in Combine(childResults, Provides(part)))
            {
                if (mountMustCarry && (kv.Key & FilledBit) == 0)
                {
                    continue;
                }
                var sum = kv.Value;
                var choice = new SlotChoice
                {
                    Slot = slot,
                    Part = part,
                    Source = purchase.Source,
                    PartPrice = purchase.Price,
                    Offer = purchase.Offer,
                    Mask = kv.Key & (SightBit | LightBit),
                    Children = sum.Children,
                    Ergonomics = ownErgo + sum.Ergonomics,
                    Recoil = ownRecoil + sum.Recoil,
                    Purchases = ownPurchases + sum.Purchases,
                    Cost = purchase.Price + sum.Cost
                };
                for (int m = 0; m < 4; m++)
                {
                    if ((choice.Mask & m) == m && (option.Best[m] == null || IsBetter(choice, option.Best[m])))
                    {
                        option.Best[m] = choice;
                    }
                }
            }
            return option.Any == null ? null : option;
        }

        private bool IsBetter(SlotChoice a, SlotChoice b)
        {
            float sa = _weights.Score(a);
            float sb = _weights.Score(b);
            if (Math.Abs(sa - sb) > 0.0001f)
            {
                return sa > sb;
            }
            // Gleichstand: billiger ist besser, dann weniger kaufen, dann Stash vor Händler vor Flohmarkt.
            if (Math.Abs(a.Cost - b.Cost) > 0.5f)
            {
                return a.Cost < b.Cost;
            }
            if (a.Purchases != b.Purchases)
            {
                return a.Purchases < b.Purchases;
            }
            return a.Source < b.Source;
        }
    }
}
