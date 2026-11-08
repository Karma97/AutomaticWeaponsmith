using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace AutomaticWeaponsmith
{
    // Ablauf beim Klick: Preise laden -> besten Build rechnen -> (Budget einhalten) -> Vorschau im Editor.
    // Kauft NICHTS.
    public static class OptimizerRunner
    {
        // Wie tief wir bei Konflikten verzweigen (2^N Rechenläufe im schlimmsten Fall).
        private const int MaxBranchDepth = 6;

        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(EditBuildScreen), "Item");
        private static readonly FieldInfo ManipulationField = AccessTools.Field(typeof(EditBuildScreen), "_useAllManipulation");
        private static readonly FieldInfo SessionField = AccessTools.Field(typeof(EditBuildScreen), "_session");

        public static bool Busy { get; private set; }

        // Alles, was ein Durchlauf braucht, an einem Ort.
        private class Run_
        {
            public Weapon Weapon;
            public BuildOptimizer Optimizer;
            public Stopwatch Watch;
            public long LimitMs;
            public bool TimedOut;
            public List<string> ConflictLog = new List<string>();
            public string LastUnresolved;
            public long PhaseStartMs;
            public Weights FinalWeights;
            public Dictionary<string, List<SlotChoice>> Cache = new Dictionary<string, List<SlotChoice>>();
        }

        public static async Task RunAsync(EditBuildScreen screen, OptimizeMode mode)
        {
            if (Busy)
            {
                return;
            }
            Busy = true;
            try
            {
                var weapon = ItemField?.GetValue(screen) as Weapon;
                var manipulation = ManipulationField?.GetValue(screen) as EditBuildManipulation;
                var session = SessionField?.GetValue(screen) as IEftSession;
                if (weapon == null || manipulation == null || session == null)
                {
                    Plugin.Log.LogWarning("Optimizer: no weapon, part list or session found.");
                    return;
                }

                // 1. Preise laden (Händler-Sortimente + Flohmarkt). Dauert beim ersten Mal kurz, dann 5 Min. gespeichert.
                var priceWatch = Stopwatch.StartNew();
                PriceData prices = null;
                try
                {
                    prices = await PriceService.Load(session, Plugin.AllowBarter.Value);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not load prices, calculating without prices: " + ex.Message);
                }
                long priceMs = priceWatch.ElapsedMilliseconds;

                // Der Spieler könnte inzwischen eine andere Waffe geöffnet haben.
                if (ItemField.GetValue(screen) != weapon)
                {
                    Plugin.Log.LogInfo("Weapon changed while loading, cancelled.");
                    return;
                }

                // 2. Vorbereitung im Spiel-Thread (liest Stash, Einstellungen, aktuelles Visier/Magazin).
                var watch = Stopwatch.StartNew();
                bool fleaAvailable = session.RagFair != null && session.RagFair.Available;
                var stash = BuildAnalyzer.CountStashParts(screen);
                var index = PartIndex.For(manipulation);

                var policy = Plugin.Sight.Value;
                var currentSight = CurrentBestSight(weapon);
                int sightRank = policy == SightPolicy.Any || currentSight == null ? 0 : BuildOptimizer.SightRank(currentSight);
                MongoID? exactSight = policy == SightPolicy.Keep ? currentSight?.TemplateId : null;
                bool alwaysOptic = Plugin.AlwaysOptic.Value;
                bool replacingIronSight = false;
                if (alwaysOptic)
                {
                    // Mindestens Rotpunkt/Holo. War vorher nur Kimme/Korn drauf, darf das nicht "behalten" werden.
                    replacingIronSight = currentSight != null && BuildOptimizer.SightRank(currentSight) < 2;
                    if (currentSight == null || replacingIronSight)
                    {
                        exactSight = null;
                    }
                    sightRank = Math.Max(sightRank, 2);
                }
                int minMagazine = CurrentMagazineSize(weapon) ?? Plugin.MinMagazineSize.Value;
                float? minErgo = mode == OptimizeMode.Recoil && Plugin.MinErgonomics.Value > 0f ? Plugin.MinErgonomics.Value : (float?)null;
                bool budgetEnabled = Plugin.BudgetEnabled.Value;
                int budget = Plugin.Budget.Value;
                string sightName = currentSight != null ? Name(currentSight) : null;

                // 3. Rechnen im Hintergrund (damit das Spiel nicht einfriert), danach echte Flohmarkt-Angebote prüfen.
                //    Gibt es ein Teil gerade nicht zu kaufen, wird es gesperrt und neu gerechnet (höchstens 3 Runden).
                List<string> notes = null;
                Run_ run = null;
                List<SlotChoice> result = null;
                Dictionary<SlotChoice, Purchase> purchases = null;
                var notOnFlea = new List<string>();

                for (int round = 0; round < 3; round++)
                {
                    var roundNotes = new List<string>();
                    await Task.Run(() =>
                    {
                        run = NewRun(weapon, manipulation, index, stash, fleaAvailable, sightRank, minMagazine, exactSight, prices, watch);
                        result = SolveForMode(run, mode, roundNotes);

                        // Passt das aktuelle Visier nicht auf den besten Build, dann eins derselben Art nehmen.
                        if (exactSight.HasValue && (result == null || !result.Any(run.Optimizer.ContainsSight)))
                        {
                            run = NewRun(weapon, manipulation, index, stash, fleaAvailable, sightRank, minMagazine, null, prices, watch);
                            roundNotes.Clear();
                            result = SolveForMode(run, mode, roundNotes);
                            roundNotes.Add(Lang.T("note.sight_replaced", ("sight", sightName)));
                        }

                        // Budget einhalten (nur wenn eingeschaltet). Im Rückstoß-Modus gilt dabei weiter die Mindest-Ergonomie.
                        if (result != null && budgetEnabled)
                        {
                            result = FitBudget(run, mode, result, stash, budget, minErgo, roundNotes);
                        }
                    });
                    notes = roundNotes;

                    if (result == null || ItemField.GetValue(screen) != weapon)
                    {
                        break;
                    }

                    purchases = AssignPurchases(result, stash, run.Optimizer);
                    if (prices == null || !fleaAvailable)
                    {
                        break;
                    }

                    var fleaParts = purchases.Where(p => p.Value.Source == PartSource.Flea).Select(p => p.Key.Part).ToList();
                    var blocked = await PriceService.CheckFleaOffers(session, prices, fleaParts.Select(p => p.TemplateId.ToString()),
                        Plugin.AllowBarter.Value);
                    if (blocked.Count == 0)
                    {
                        break;
                    }
                    notOnFlea.AddRange(fleaParts.Where(p => blocked.Contains(p.TemplateId.ToString())).Select(Name));
                    Plugin.Log.LogInfo($"Currently not on the flea market: {string.Join(", ", notOnFlea)}. Recalculating.");
                }
                watch.Stop();

                if (ItemField.GetValue(screen) != weapon)
                {
                    Plugin.Log.LogInfo("Weapon changed while calculating, cancelled.");
                    return;
                }

                if (result == null)
                {
                    Plugin.Log.LogWarning($"Optimizer: no valid build found. {watch.ElapsedMilliseconds} ms, " +
                                          $"{run?.Optimizer.SolveCount} solver runs. Unsolved conflict: {run?.LastUnresolved ?? "-"}");
                    return;
                }

                notes.InsertRange(0, StatNotes(weapon, mode, result, minErgo));
                if (alwaysOptic && !result.Any(run.Optimizer.ContainsSight))
                {
                    notes.Add(Lang.T("note.no_optic"));
                }
                else if (replacingIronSight)
                {
                    notes.Add(Lang.T("note.iron_replaced", ("sight", sightName)));
                }
                if (Plugin.RequireLight.Value && !result.Any(run.Optimizer.ContainsLight))
                {
                    notes.Add(Lang.T("note.no_flashlight"));
                }
                if (notOnFlea.Count > 0)
                {
                    notes.Add(Lang.T("note.not_on_flea", ("parts", string.Join(", ", notOnFlea.Distinct()))));
                }

                // Preise mit den echten Flohmarkt-Angeboten neu zuordnen (frischer Optimierer = keine alten Preise im Speicher).
                var priced = NewRun(weapon, manipulation, index, stash, fleaAvailable, sightRank, minMagazine, exactSight, prices, watch);
                purchases = AssignPurchases(result, stash, priced.Optimizer);
                Plugin.Log.LogInfo("\n" + Describe(weapon, mode, fleaAvailable, prices, result, purchases, run, notes, priceMs));

                // 4. Vorschau im Editor.
                PreviewApplier.Apply(screen, manipulation, mode, result, purchases, notes,
                    weapon.Template.Ergonomics + result.Sum(c => c.Ergonomics), result.Sum(c => c.Recoil));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Optimizer error: " + ex);
            }
            finally
            {
                Busy = false;
            }
        }

        private static List<SlotChoice> SolveForMode(Run_ run, OptimizeMode mode, List<string> notes)
        {
            switch (mode)
            {
                case OptimizeMode.Ergonomics:
                {
                    // Ergo über 100 bringt nichts. Erst schauen, wie hoch es geht; liegt das über 100,
                    // dann "mindestens 100 Ergo und dabei so wenig Rückstoß wie möglich".
                    var weights = new Weights(1f, 0.001f);
                    var maxErgo = Thorough(run, weights);
                    run.FinalWeights = weights;
                    if (maxErgo == null || ErgoTotal(run.Weapon, maxErgo) <= 100f)
                    {
                        return maxErgo;
                    }
                    var capped = SolveRecoilWithMinErgo(run, 100f, notes);
                    return capped ?? maxErgo;
                }

                case OptimizeMode.Balanced:
                {
                    var weights = new Weights(1f, Plugin.BalancedRecoilWeight.Value);
                    run.FinalWeights = weights;
                    return Thorough(run, weights);
                }

                default:
                    return SolveRecoilWithMinErgo(run, Plugin.MinErgonomics.Value, notes);
            }
        }

        // Rückstoß-Modus: so wenig Rückstoß wie möglich, aber die Ergonomie darf nicht unter das Minimum fallen.
        // Dafür geben wir der Ergonomie schrittweise mehr Gewicht, bis das Minimum erreicht ist (Binärsuche).
        private static List<SlotChoice> SolveRecoilWithMinErgo(Run_ run, float minErgo, List<string> notes)
        {
            var lowWeights = new Weights(0.001f, 1f);
            run.FinalWeights = lowWeights;
            var quick = Greedy(run, lowWeights, new HashSet<MongoID>());
            if (quick == null || minErgo <= 0f || ErgoTotal(run.Weapon, quick) >= minErgo)
            {
                return Thorough(run, lowWeights) ?? quick;
            }

            // Suchphase: nur schnelle Konfliktlösung, damit viele Gewichte probiert werden können.
            float low = 0.001f, high = 20f;
            var highWeights = new Weights(high, 1f);
            var highResult = Greedy(run, highWeights, new HashSet<MongoID>());
            if (highResult == null || ErgoTotal(run.Weapon, highResult) < minErgo)
            {
                run.FinalWeights = highWeights;
                return Thorough(run, highWeights) ?? highResult ?? quick;
            }

            var bestQuick = highResult;
            for (int i = 0; i < 12; i++)
            {
                float mid = (low + high) / 2f;
                var midResult = Greedy(run, new Weights(mid, 1f), new HashSet<MongoID>());
                if (midResult != null && ErgoTotal(run.Weapon, midResult) >= minErgo)
                {
                    bestQuick = midResult;
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }

            // Endphase: mit dem gefundenen Gewicht gründlich rechnen (Konflikte in beide Richtungen prüfen).
            var finalWeights = new Weights(high, 1f);
            run.FinalWeights = finalWeights;
            var final = Thorough(run, finalWeights);
            if (final == null || ErgoTotal(run.Weapon, final) < minErgo
                || final.Sum(c => c.Recoil) > bestQuick.Sum(c => c.Recoil))
            {
                final = bestQuick;
            }
            return final;
        }

        // Budget: Ist der Build zu teuer, bekommt der Preis schrittweise mehr Gewicht, bis die Kosten passen.
        // Gibt es eine Mindest-Ergonomie, bekommt notfalls auch die Ergonomie mehr Gewicht (bis zu 4 Runden).
        private static List<SlotChoice> FitBudget(Run_ run, OptimizeMode mode, List<SlotChoice> result, Dictionary<MongoID, int> stash,
            int budget, float? minErgo, List<string> notes)
        {
            float cost = TotalCost(result, stash, run.Optimizer);
            if (cost <= budget)
            {
                return result;
            }

            if (mode != OptimizeMode.Ergonomics)
            {
                return FitBudgetCore(run, run.FinalWeights, result, stash, budget, minErgo, notes, cost);
            }

            // Ergonomie-Modus: Passt "Ergo 100 mit möglichst wenig Rückstoß" ins Budget? Dann das.
            // Sonst so viel Ergo wie mit dem Budget geht.
            var capNotes = new List<string>();
            var capped = FitBudgetCore(run, run.FinalWeights, result, stash, budget, 100f, capNotes, cost);
            if (capped != null && ErgoTotal(run.Weapon, capped) >= 100f && TotalCost(capped, stash, run.Optimizer) <= budget)
            {
                notes.AddRange(capNotes);
                return capped;
            }
            return FitBudgetCore(run, new Weights(1f, 0.001f), result, stash, budget, null, notes, cost);
        }

        private static List<SlotChoice> FitBudgetCore(Run_ run, Weights baseWeights, List<SlotChoice> result,
            Dictionary<MongoID, int> stash, int budget, float? minErgo, List<string> notes, float cost)
        {
            List<SlotChoice> lastAffordable = null;
            Weights lastWeights = baseWeights;

            for (int round = 0; round < 4; round++)
            {
                float ergoWeight = Math.Min(20f, baseWeights.Ergo * (float)Math.Pow(4, round));
                var weights = new Weights(ergoWeight, baseWeights.Recoil);

                var affordable = CheapestWithinBudget(run, weights, stash, budget, out float costWeight);
                if (affordable == null)
                {
                    break; // Selbst "nur der Preis zählt" passt nicht ins Budget.
                }
                lastAffordable = affordable;
                lastWeights = weights.WithCost(costWeight);

                if (minErgo == null || ErgoTotal(run.Weapon, affordable) >= minErgo.Value)
                {
                    var final = Thorough(run, lastWeights);
                    if (final == null || TotalCost(final, stash, run.Optimizer) > budget
                        || (minErgo != null && ErgoTotal(run.Weapon, final) < minErgo.Value))
                    {
                        final = affordable;
                    }
                    notes.Add(Lang.T("note.budget_kept", ("budget", budget.ToString("N0")), ("cost", cost.ToString("N0"))));
                    return final;
                }
                if (ergoWeight >= 20f)
                {
                    break;
                }
            }

            if (lastAffordable != null)
            {
                notes.Add(Lang.T("note.budget_kept_low_ergo", ("budget", budget.ToString("N0")), ("cost", cost.ToString("N0"))));
                return lastAffordable;
            }

            var cheapest = Greedy(run, baseWeights.WithCost(10f), new HashSet<MongoID>());
            float cheapestCost = cheapest == null ? cost : TotalCost(cheapest, stash, run.Optimizer);
            notes.Add(Lang.T("note.budget_impossible", ("budget", budget.ToString("N0")), ("cost", cheapestCost.ToString("N0"))));
            return cheapest ?? result;
        }

        // Sucht das kleinste Kosten-Gewicht, bei dem der Build ins Budget passt (logarithmische Binärsuche).
        private static List<SlotChoice> CheapestWithinBudget(Run_ run, Weights weights, Dictionary<MongoID, int> stash, int budget,
            out float costWeight)
        {
            double lowLog = Math.Log(1e-7), highLog = Math.Log(10.0);
            costWeight = (float)Math.Exp(highLog);
            var cheapest = Greedy(run, weights.WithCost(costWeight), new HashSet<MongoID>());
            if (cheapest == null || TotalCost(cheapest, stash, run.Optimizer) > budget)
            {
                return null;
            }

            var best = cheapest;
            for (int i = 0; i < 10; i++)
            {
                double midLog = (lowLog + highLog) / 2.0;
                var midResult = Greedy(run, weights.WithCost((float)Math.Exp(midLog)), new HashSet<MongoID>());
                if (midResult != null && TotalCost(midResult, stash, run.Optimizer) <= budget)
                {
                    best = midResult;
                    costWeight = (float)Math.Exp(midLog);
                    highLog = midLog;
                }
                else
                {
                    lowLog = midLog;
                }
            }
            return best;
        }

        // Hinweise, die zum ENDERGEBNIS passen (nicht zu Zwischenschritten).
        private static IEnumerable<string> StatNotes(Weapon weapon, OptimizeMode mode, List<SlotChoice> result, float? minErgo)
        {
            float ergo = ErgoTotal(weapon, result);
            if (mode == OptimizeMode.Recoil && minErgo != null)
            {
                yield return ergo >= minErgo.Value
                    ? Lang.T("note.min_ergo_kept", ("min", minErgo.Value.ToString("0")))
                    : Lang.T("note.min_ergo_missed", ("min", minErgo.Value.ToString("0")), ("ergo", Clamp(ergo).ToString("0.#")));
            }
            if (mode == OptimizeMode.Ergonomics && ergo >= 100f)
            {
                yield return Lang.T("note.ergo_100");
            }
        }

        // Gründlich: Bei "A verträgt sich nicht mit B" wird einmal A und einmal B gesperrt,
        // und das bessere Ergebnis gewinnt. Bekommt ein eigenes Zeitbudget.
        private static List<SlotChoice> Thorough(Run_ run, Weights weights)
        {
            run.PhaseStartMs = run.Watch.ElapsedMilliseconds;
            run.TimedOut = false;
            return Branch(run, weights, new HashSet<MongoID>(), 0);
        }

        // Schnell: Immer das zweite Teil eines Konflikts sperren, bis keine Konflikte mehr da sind.
        private static List<SlotChoice> Greedy(Run_ run, Weights weights, HashSet<MongoID> banned)
        {
            var greedyBanned = new HashSet<MongoID>(banned);
            var result = SolveCached(run, weights, greedyBanned);
            var conflict = FindConflict(result);
            int i = 0;
            for (; i < 300 && conflict != null; i++)
            {
                greedyBanned.Add(conflict.Item2.TemplateId);
                result = SolveCached(run, weights, greedyBanned);
                conflict = FindConflict(result);
            }
            if (conflict != null)
            {
                run.LastUnresolved = $"{Name(conflict.Item1)} vs {Name(conflict.Item2)} after {i} bans";
                return null;
            }
            return result;
        }

        // Gleiche Gewichte + gleiche Sperrliste = gleiches Ergebnis, also nicht doppelt rechnen.
        private static List<SlotChoice> SolveCached(Run_ run, Weights weights, HashSet<MongoID> banned)
        {
            string key = weights + "|" + string.Join(",", banned.Select(b => b.ToString()).OrderBy(s => s));
            if (!run.Cache.TryGetValue(key, out var result))
            {
                result = run.Optimizer.Solve(run.Weapon, weights, banned);
                run.Cache[key] = result;
            }
            return result;
        }

        private static List<SlotChoice> Branch(Run_ run, Weights weights, HashSet<MongoID> banned, int depth)
        {
            var result = SolveCached(run, weights, banned);
            var conflict = FindConflict(result);
            if (conflict == null)
            {
                return result;
            }

            if (run.Watch.ElapsedMilliseconds - run.PhaseStartMs > run.LimitMs)
            {
                run.TimedOut = true;
            }

            if (depth >= MaxBranchDepth || run.TimedOut)
            {
                return Greedy(run, weights, banned);
            }

            var withoutA = Branch(run, weights, new HashSet<MongoID>(banned) { conflict.Item1.TemplateId }, depth + 1);
            var withoutB = Branch(run, weights, new HashSet<MongoID>(banned) { conflict.Item2.TemplateId }, depth + 1);

            var winner = Better(weights, withoutA, withoutB);
            if (depth == 0 || run.ConflictLog.Count < 10)
            {
                var dropped = winner == withoutA ? conflict.Item1 : conflict.Item2;
                var kept = winner == withoutA ? conflict.Item2 : conflict.Item1;
                string line = $"{Name(kept)} instead of {Name(dropped)} (they do not fit together)";
                if (!run.ConflictLog.Contains(line))
                {
                    run.ConflictLog.Add(line);
                }
            }
            return winner;
        }

        private static List<SlotChoice> Better(Weights weights, List<SlotChoice> a, List<SlotChoice> b)
        {
            if (a == null) return b;
            if (b == null) return a;
            float sa = weights.Score(a);
            float sb = weights.Score(b);
            if (Math.Abs(sa - sb) > 0.0001f)
            {
                return sa > sb ? a : b;
            }
            return a.Sum(c => c.Cost) <= b.Sum(c => c.Cost) ? a : b;
        }

        private static Run_ NewRun(Weapon weapon, EditBuildManipulation manipulation, PartIndex index, Dictionary<MongoID, int> stash,
            bool fleaAvailable, int sightRank, int minMagazine, MongoID? exactSight, PriceData prices, Stopwatch watch)
        {
            return new Run_
            {
                Weapon = weapon,
                Optimizer = new BuildOptimizer(manipulation, index, stash, fleaAvailable, Plugin.AllowLaunchers.Value,
                    sightRank, minMagazine, exactSight, prices, Plugin.RequireLight.Value, Plugin.AlwaysOptic.Value),
                Watch = watch,
                LimitMs = Plugin.TimeLimitMs.Value
            };
        }

        // Das "beste" Visier, das gerade in der Waffe steckt (Zielfernrohr vor Rotpunkt vor Kimme/Korn).
        private static Item CurrentBestSight(Weapon weapon)
        {
            Item best = null;
            foreach (var slot in weapon.AllSlots)
            {
                var item = slot.ContainedItem;
                if (item != null && BuildOptimizer.SightRank(item) > (best == null ? 0 : BuildOptimizer.SightRank(best)))
                {
                    best = item;
                }
            }
            return best;
        }

        private static int? CurrentMagazineSize(Weapon weapon)
        {
            foreach (var slot in weapon.Slots)
            {
                if (slot.ContainedItem is Magazine magazine)
                {
                    return magazine.MaxCount;
                }
            }
            return null;
        }

        private static float ErgoTotal(Weapon weapon, List<SlotChoice> result)
        {
            return weapon.Template.Ergonomics + result.Sum(c => c.Ergonomics);
        }

        // Sucht zwei gewählte Teile, die laut Spiel nicht zusammen verbaut werden dürfen.
        private static Tuple<Item, Item> FindConflict(List<SlotChoice> result)
        {
            var parts = result.SelectMany(c => c.Flatten()).Where(c => c.Part != null).Select(c => c.Part).ToList();
            for (int i = 0; i < parts.Count; i++)
            {
                var conflicts = parts[i].Template.ConflictingItems;
                if (conflicts == null || conflicts.Length == 0)
                {
                    continue;
                }
                for (int j = 0; j < parts.Count; j++)
                {
                    if (i != j && conflicts.Contains(parts[j].TemplateId.ToString()))
                    {
                        return Tuple.Create(parts[i], parts[j]);
                    }
                }
            }
            return null;
        }

        // Endgültige Quelle + Preis pro Teil: Stash nur so oft, wie wirklich vorhanden, danach günstigster Kauf.
        public static Dictionary<SlotChoice, Purchase> AssignPurchases(List<SlotChoice> result, Dictionary<MongoID, int> stash,
            BuildOptimizer optimizer)
        {
            var left = new Dictionary<MongoID, int>(stash);
            var purchases = new Dictionary<SlotChoice, Purchase>();
            foreach (var choice in result.SelectMany(c => c.Flatten()).Where(c => c.Part != null))
            {
                // Der Build-Editor ist nur ein Baukasten: Auch Teile, die dort schon "eingebaut" sind,
                // müssen im Stash liegen oder gekauft werden. Daher gilt für alle Teile dieselbe Regel.
                if (left.TryGetValue(choice.Part.TemplateId, out int n) && n > 0)
                {
                    left[choice.Part.TemplateId] = n - 1;
                    purchases[choice] = new Purchase { Source = PartSource.Stash, Price = 0f };
                    continue;
                }
                purchases[choice] = optimizer.Buy(choice.Part);
            }
            return purchases;
        }

        private static float TotalCost(List<SlotChoice> result, Dictionary<MongoID, int> stash, BuildOptimizer optimizer)
        {
            return AssignPurchases(result, stash, optimizer).Values.Sum(p => p.Price);
        }

        private static string Describe(Weapon weapon, OptimizeMode mode, bool fleaAvailable, PriceData prices, List<SlotChoice> result,
            Dictionary<SlotChoice, Purchase> purchases, Run_ run, List<string> notes, long priceMs)
        {
            var t = weapon.Template;
            float newErgo = result.Sum(c => c.Ergonomics);
            float newRecoil = result.Sum(c => c.Recoil);

            var sb = new StringBuilder();
            sb.AppendLine($"=== Auto Build: {Name(weapon)} | Mode: {ModeName(mode)} | Flea: {(fleaAvailable ? "yes" : "no")} " +
                          $"| Prices: {(prices?.TradersLoaded == true ? "yes" : "NO")} ===");
            sb.AppendLine($"Before: Ergo {Clamp(weapon.ErgonomicsTotal),5:0.#} | Recoil vertical {t.RecoilForceUp * (1f + weapon.RecoilDelta),4:0} | horizontal {t.RecoilForceBack * (1f + weapon.RecoilDelta),4:0}");
            sb.AppendLine($"After:  Ergo {Clamp(t.Ergonomics + newErgo),5:0.#} | Recoil vertical {t.RecoilForceUp * (1f + newRecoil / 100f),4:0} | horizontal {t.RecoilForceBack * (1f + newRecoil / 100f),4:0}");
            sb.AppendLine("Parts:");
            foreach (var choice in result)
            {
                AppendChoice(sb, choice, purchases, 1);
            }
            sb.AppendLine($"Total cost: {purchases.Values.Sum(p => p.Price):N0} ₽");

            foreach (var line in run.ConflictLog)
            {
                sb.AppendLine("Conflict: " + line);
            }
            foreach (var note in notes)
            {
                sb.AppendLine("Note: " + note);
            }
            if (run.TimedOut)
            {
                sb.AppendLine("WARNING: time limit reached, the result may not be the best one.");
            }
            sb.AppendLine($"=== Done: prices {priceMs} ms, calculation {run.Watch.ElapsedMilliseconds} ms, " +
                          $"{run.Optimizer.SolveCount} solver runs, {run.Optimizer.MemoHits}x reused ===");
            return sb.ToString();
        }

        private static void AppendChoice(StringBuilder sb, SlotChoice choice, Dictionary<SlotChoice, Purchase> purchases, int depth)
        {
            if (choice.Part == null)
            {
                return; // leere optionale Slots nicht auflisten
            }
            string indent = new string(' ', depth * 2);
            var mod = choice.Part as Mod;
            string stats = mod != null ? $"Ergo {mod.Template.Ergonomics:+0.#;-0.#;0}, Recoil {mod.Template.Recoil:+0.#;-0.#;0}%" : "";
            string changed = choice.Slot.ContainedItem?.TemplateId == choice.Part.TemplateId ? "" : "  <- NEW";
            var p = purchases[choice];
            sb.AppendLine($"{indent}{choice.Slot.ID,-18} {Name(choice.Part),-26} {stats,-26} [{PurchaseText(p)}]{changed}");
            foreach (var child in choice.Children)
            {
                AppendChoice(sb, child, purchases, depth + 1);
            }
        }

        public static string PurchaseText(Purchase p)
        {
            switch (p.Source)
            {
                case PartSource.Stash: return "Stash";
                case PartSource.Trader:
                    return p.Offer == null ? "Trader"
                        : p.Offer.IsBarter ? $"{p.Offer.TraderName}, barter: {p.Offer.BarterText} (~{p.Price:N0} ₽)"
                        : $"{p.Offer.TraderName}, {p.Price:N0} ₽";
                case PartSource.Flea: return p.Price > 0 ? $"Flea, ~{p.Price:N0} ₽" : "Flea";
                default: return "NOT AVAILABLE";
            }
        }

        public static string Name(Item item) => item.ShortName.Localized();

        private static float Clamp(float ergo) => Math.Max(0f, Math.Min(100f, ergo));

        public static string ModeName(OptimizeMode mode)
        {
            switch (mode)
            {
                case OptimizeMode.Recoil: return "Recoil";
                case OptimizeMode.Ergonomics: return "Ergonomics";
                default: return "Balanced";
            }
        }
    }
}
