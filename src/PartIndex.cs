using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

namespace AutomaticWeaponsmith
{
    // Schnelles Nachschlagen: "Welche Teile passen in diesen Slot?"
    // Statt bei jedem Slot alle ~2000 Teile mit allen Spielprüfungen durchzugehen, merken wir uns
    // einmal, welche Teile zu welcher Filter-ID gehören, und fragen nur noch das ab.
    public class PartIndex
    {
        private readonly List<Item> _allParts;
        private readonly Dictionary<MongoID, Item> _byTemplate;
        private readonly Dictionary<string, List<Item>> _byNode = new Dictionary<string, List<Item>>();
        private readonly Dictionary<string, List<Item>> _bySlot = new Dictionary<string, List<Item>>();

        // Der Index gilt für eine Teile-Liste des Spiels. Öffnet man den Screen neu, gibt es eine neue Liste.
        private static PartIndex _cached;
        private static object _cachedFor;

        public static PartIndex For(EditBuildManipulation manipulation)
        {
            if (_cached == null || !ReferenceEquals(_cachedFor, manipulation))
            {
                _cached = new PartIndex(manipulation);
                _cachedFor = manipulation;
            }
            return _cached;
        }

        private PartIndex(EditBuildManipulation manipulation)
        {
            _allParts = manipulation.Collections.GetAllItemsFromCollections()
                .Where(i => i is Mod)
                .GroupBy(i => i.TemplateId)
                .Select(g => g.First())
                .ToList();
            _byTemplate = _allParts.ToDictionary(i => i.TemplateId);
        }

        public int Count => _allParts.Count;

        public List<Item> Candidates(Slot slot)
        {
            string key = slot.ParentItem.TemplateId + "|" + slot.ID;
            if (_bySlot.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = new List<Item>();
            var seen = new HashSet<MongoID>();
            var filters = slot.Filters;
            if (filters != null)
            {
                foreach (var filter in filters)
                {
                    if (filter?.Filter == null)
                    {
                        continue;
                    }
                    foreach (var id in filter.Filter)
                    {
                        foreach (var item in ItemsFor(id))
                        {
                            if (filter.CheckItemExcludedFilter(item) && !item.IsSpecialSlotOnly && seen.Add(item.TemplateId))
                            {
                                result.Add(item);
                            }
                        }
                    }
                }
            }

            _bySlot[key] = result;
            return result;
        }

        // Eine Filter-ID ist entweder ein konkretes Teil oder eine ganze Kategorie (z. B. "alle Griffe").
        private IEnumerable<Item> ItemsFor(MongoID id)
        {
            if (_byTemplate.TryGetValue(id, out var single))
            {
                return new[] { single };
            }

            string node = id.ToString();
            if (_byNode.TryGetValue(node, out var list))
            {
                return list;
            }

            list = JsonTypes.TypeTable.TryGetValue(node, out Type type)
                ? _allParts.Where(type.IsInstanceOfType).ToList()
                : new List<Item>();
            _byNode[node] = list;
            return list;
        }
    }
}
