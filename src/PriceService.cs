using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;

namespace AutomaticWeaponsmith
{
    // Ein konkretes Händler-Angebot für ein Teil.
    public class TraderOffer
    {
        public string TraderName;
        public float PriceRub;          // Bei Tausch: geschätzter Wert der Tauschwaren (Flohmarktpreise)
        public bool IsBarter;
        public string BarterText;       // z. B. "2x Bolts, 1x Screws"
    }

    // Alle Preise, die der Optimierer braucht. Wird einmal geladen und ein paar Minuten wiederverwendet.
    public class PriceData
    {
        public readonly Dictionary<MongoID, TraderOffer> TraderOffers = new Dictionary<MongoID, TraderOffer>();
        public readonly Dictionary<string, double> FleaPrices = new Dictionary<string, double>();

        // Ergebnis der Live-Prüfung echter Flohmarkt-Angebote: günstigster kaufbarer Preis bzw. "gibt's gerade nicht".
        public readonly Dictionary<string, float> FleaLive = new Dictionary<string, float>();
        public readonly HashSet<string> FleaBlocked = new HashSet<string>();
        public DateTime LoadedAt;
        public bool IncludesBarter;
        public bool TradersLoaded;

        public bool IsFleaBlocked(Item item) => FleaBlocked.Contains(item.TemplateId.ToString());

        public bool TryFleaPrice(Item item, out float price)
        {
            price = 0f;
            string tpl = item.TemplateId.ToString();
            if (FleaLive.TryGetValue(tpl, out float live))
            {
                price = live;
                return true;
            }
            if (FleaPrices.TryGetValue(tpl, out double p) && p > 0)
            {
                price = (float)p;
                return true;
            }
            return false;
        }
    }

    public static class PriceService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
        private static PriceData _cached;

        public static async Task<PriceData> Load(IEftSession session, bool includeBarter)
        {
            if (_cached != null && _cached.IncludesBarter == includeBarter && DateTime.Now - _cached.LoadedAt < CacheTime)
            {
                return _cached;
            }

            var data = new PriceData { LoadedAt = DateTime.Now, IncludesBarter = includeBarter };
            var traders = session.DisplayableTraders.Where(t => t.Info.Available).ToList();

            // Sortimente aller Händler laden (passiert sonst erst, wenn man den Händler öffnet).
            foreach (var trader in traders)
            {
                try
                {
                    await trader.RefreshAssortment(createIfNotExists: true, ignoreTimeout: false);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Prices: could not load assortment of {trader.LocalizedName}: {ex.Message}");
                }
            }

            // Flohmarktpreise: Die Preistabelle kommt beim Laden der Händler mit (bei allen gleich).
            var supply = traders.Select(t => t._supplyData).FirstOrDefault(s => s?.MarketPrices != null);
            if (supply != null)
            {
                foreach (var kv in supply.MarketPrices)
                {
                    data.FleaPrices[kv.Key] = kv.Value;
                }
            }

            int offerCount = 0;
            foreach (var trader in traders)
            {
                var assortment = trader.CurrentAssortment;
                if (assortment == null)
                {
                    continue;
                }
                data.TradersLoaded = true;
                foreach (var offer in ReadOffers(trader, assortment, supply, data, includeBarter))
                {
                    offerCount++;
                    if (!data.TraderOffers.TryGetValue(offer.Key, out var existing) || IsCheaper(offer.Value, existing))
                    {
                        data.TraderOffers[offer.Key] = offer.Value;
                    }
                }
            }

            Plugin.Log.LogInfo($"Prices loaded: {traders.Count} traders, {offerCount} offers for weapon parts, " +
                               $"{data.FleaPrices.Count} flea prices, barter {(includeBarter ? "on" : "off")}.");
            _cached = data;
            return data;
        }

        // Bargeld schlägt Tausch bei gleichem Preis.
        private static bool IsCheaper(TraderOffer a, TraderOffer b)
        {
            if (Math.Abs(a.PriceRub - b.PriceRub) > 0.5f)
            {
                return a.PriceRub < b.PriceRub;
            }
            return !a.IsBarter && b.IsBarter;
        }

        private static IEnumerable<KeyValuePair<MongoID, TraderOffer>> ReadOffers(Trader trader, Assortment assortment,
            SupplyData supply, PriceData data, bool includeBarter)
        {
            if (!(trader.TraderController?.RootItem is CompoundItem root))
            {
                yield break;
            }

            int playerLevel = trader.Info.LoyaltyLevel;
            float restrictionMul = assortment._buyRestrictionMul;

            foreach (var grid in root.Grids ?? Array.Empty<Grid>())
            {
                foreach (var item in grid.Items)
                {
                    if (!(item is Mod))
                    {
                        continue;
                    }

                    // Loyalitätslevel, Vorrat und Kauflimit prüfen.
                    if (assortment.LoyalLevelItems != null && assortment.LoyalLevelItems.TryGetValue(item.Id, out int needed)
                        && needed > playerLevel)
                    {
                        continue;
                    }
                    if (!item.UnlimitedCount && item.StackObjectsCount <= 0)
                    {
                        continue;
                    }
                    if (item.BuyRestrictionMax > 0 && item.BuyRestrictionCurrent >= (int)(item.BuyRestrictionMax * restrictionMul))
                    {
                        continue;
                    }

                    var scheme = assortment.GetSchemeForItem(item);
                    if (scheme == null || scheme.Count == 0)
                    {
                        continue;
                    }

                    // Mehrere Bezahlvarianten möglich: die günstigste erlaubte nehmen.
                    TraderOffer best = null;
                    foreach (var variant in scheme)
                    {
                        var offer = ToOffer(trader, variant, supply, data);
                        if (offer == null || (offer.IsBarter && !includeBarter))
                        {
                            continue;
                        }
                        if (best == null || IsCheaper(offer, best))
                        {
                            best = offer;
                        }
                    }
                    if (best != null)
                    {
                        yield return new KeyValuePair<MongoID, TraderOffer>(item.TemplateId, best);
                    }
                }
            }
        }

        private static TraderOffer ToOffer(Trader trader, BarterVariant variant, SupplyData supply, PriceData data)
        {
            if (variant == null || variant.Count == 0)
            {
                return null;
            }

            // GP-Münzen sind zwar eine "Währung", lassen sich aber nicht kaufen: wie ein Tausch behandeln.
            bool isGp = variant.Count == 1 && new MongoID(variant[0]._tpl) == CurrencyUtil.GP_ID;

            // Bargeld = genau eine Position, und die ist eine Währung (Rubel, Dollar, Euro).
            if (variant.Count == 1 && !isGp && CurrencyUtil.IsCurrencyId(new MongoID(variant[0]._tpl)))
            {
                return new TraderOffer
                {
                    TraderName = trader.LocalizedName,
                    PriceRub = (float)(variant[0].count * CurrencyRate(variant[0]._tpl, supply))
                };
            }

            // Tausch: Wert der Tauschwaren über die Flohmarktpreise schätzen.
            float value = 0f;
            var parts = new List<string>();
            foreach (var req in variant)
            {
                data.FleaPrices.TryGetValue(req._tpl, out double each);
                value += (float)(each * req.count);
                parts.Add($"{req.IntCount}x {new MongoID(req._tpl).LocalizedShortName()}");
            }
            return new TraderOffer
            {
                TraderName = trader.LocalizedName,
                PriceRub = value,
                IsBarter = true,
                BarterText = string.Join(", ", parts)
            };
        }

        // Fragt für jedes Teil die echten Flohmarkt-Angebote ab (wie die Suche im Spiel) und merkt sich den
        // günstigsten kaufbaren Preis. Gibt zurück, welche Teile gerade NICHT zu bekommen sind.
        public static async Task<List<string>> CheckFleaOffers(IEftSession session, PriceData data, IEnumerable<string> templateIds,
            bool allowBarter)
        {
            var blocked = new List<string>();
            foreach (var tpl in templateIds.Distinct())
            {
                if (data.FleaLive.ContainsKey(tpl) || data.FleaBlocked.Contains(tpl))
                {
                    continue;
                }

                float? best = null;
                try
                {
                    var result = await session.GetOffers(0, 20, 5, false, 0, 0, 0, 0, 0, 0, 100, false, !allowBarter,
                        2, false, string.Empty, string.Empty, string.Empty, new Dictionary<string, int> { { tpl, 1 } }, 1, false);
                    if (result.Failed)
                    {
                        Plugin.Log.LogWarning($"Flea search for {tpl} failed: {result.Error}");
                        continue; // Im Zweifel nicht sperren
                    }
                    foreach (var offer in result.Value?.offers ?? Array.Empty<EFT.UI.Ragfair.Offer>())
                    {
                        if (offer == null || offer.Locked || offer.NotAvailable || offer.LimitsReached || offer.CurrentItemCount <= 0)
                        {
                            continue;
                        }
                        if (offer.Item == null || offer.Item.TemplateId.ToString() != tpl)
                        {
                            continue;
                        }
                        if (!allowBarter && !offer.OnlyMoney)
                        {
                            continue;
                        }
                        if (offer.SellInOnePiece && offer.Quantity > 1)
                        {
                            continue; // Nur im Paket zu kaufen
                        }
                        float price = offer.SummaryCost;
                        if (price > 0 && (best == null || price < best.Value))
                        {
                            best = price;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Flea search for {tpl} failed: {ex.Message}");
                    continue;
                }

                if (best.HasValue)
                {
                    data.FleaLive[tpl] = best.Value;
                }
                else
                {
                    data.FleaBlocked.Add(tpl);
                    blocked.Add(tpl);
                }
            }
            return blocked;
        }

        private static double CurrencyRate(string currencyTpl, SupplyData supply)
        {
            if (new MongoID(currencyTpl) == CurrencyUtil.ROUBLE_ID)
            {
                return 1.0;
            }
            if (supply?.CurrencyCourses != null && supply.CurrencyCourses.TryGetValue(currencyTpl, out double rate) && rate > 0)
            {
                return rate;
            }
            return 1.0;
        }
    }
}
