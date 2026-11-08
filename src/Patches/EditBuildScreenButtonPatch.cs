using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutomaticWeaponsmith.Patches
{
    // Teil 1: Hängt sich hinter EditBuildScreen.Awake() (läuft einmal, wenn der Screen erstellt wird)
    // und erzeugt den AUTO-BUILD-Button plus drei Modus-Buttons (Kopien von "FIND PARTS").
    public class EditBuildScreenButtonPatch : ModulePatch
    {
        internal const string ButtonName = "AutomaticWeaponsmithButton";
        internal const string MainLabelKey = "button.main";
        internal const float SubIndent = 14f;

        // Akzentfarbe wie die hellen Tarkov-Texte (beige).
        private static readonly Color Accent = new Color(0.77f, 0.76f, 0.70f, 1f);

        // Modus-Buttons, die beim Klick auf AUTO BUILD unter dem Button aufklappen.
        internal static readonly (string Name, string LabelKey, OptimizeMode Mode)[] ModeButtons =
        {
            ("AutomaticWeaponsmithModeRecoil", "button.recoil", OptimizeMode.Recoil),
            ("AutomaticWeaponsmithModeErgo", "button.ergonomics", OptimizeMode.Ergonomics),
            ("AutomaticWeaponsmithModeBalanced", "button.balanced", OptimizeMode.Balanced)
        };

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>();

        protected override MethodBase GetTargetMethod()
        {
            return typeof(EditBuildScreen).GetMethod(nameof(EditBuildScreen.Awake),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }

        [PatchPostfix]
        private static void Postfix(EditBuildScreen __instance)
        {
            Transform screen = __instance.transform;
            if (screen.Find(ButtonName) != null)
            {
                return;
            }

            var modeButtons = new List<GameObject>();
            var main = CreateButton(__instance, ButtonName, MainLabelKey, keepIcon: true);
            UseAssembleIcon(__instance, main);

            main.GetComponent<Button>().onClick.AddListener(() =>
            {
                if (OptimizerRunner.Busy)
                {
                    return;
                }
                SetOpen(main, modeButtons, !modeButtons[0].activeSelf);
            });

            foreach (var (name, labelKey, mode) in ModeButtons)
            {
                var go = CreateButton(__instance, name, labelKey, keepIcon: false);
                AddAccentBar(go);
                go.GetComponent<Button>().onClick.AddListener(async () =>
                {
                    SetOpen(main, modeButtons, false);
                    Plugin.Log.LogInfo($"Auto build started, mode {mode}.");
                    ApplyLabel(main.transform, Lang.T("button.calculating"));
                    main.GetComponent<Button>().interactable = false;
                    try
                    {
                        await OptimizerRunner.RunAsync(__instance, mode);
                    }
                    finally
                    {
                        if (main != null)
                        {
                            main.GetComponent<Button>().interactable = true;
                            ApplyLabel(main.transform, Lang.T(MainLabelKey));
                        }
                    }
                });
                go.SetActive(false);
                modeButtons.Add(go);
            }
        }

        // Auf-/Zuklappen. Offen: Haupt-Button bekommt einen Pfeil, damit man sieht, dass das Menü offen ist.
        private static void SetOpen(GameObject main, List<GameObject> modeButtons, bool open)
        {
            foreach (var b in modeButtons)
            {
                b.SetActive(open);
                if (open)
                {
                    ApplyLabel(b.transform, Lang.T(Labels[b.name]));
                }
            }
            ApplyLabel(main.transform, open ? Lang.T(MainLabelKey) + "  <" : Lang.T(MainLabelKey));
        }

        // Kopie des FIND-PARTS-Buttons, damit er wie die Liste links oben aussieht.
        // Klick-Funktionen von FIND PARTS werden dabei NICHT mitkopiert.
        private static GameObject CreateButton(EditBuildScreen screen, string name, string labelKey, bool keepIcon)
        {
            GameObject go = Object.Instantiate(screen._findPartsButton.gameObject, screen.transform, false);
            go.name = name;
            Labels[name] = labelKey;

            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;

            // Übersetzungs-Komponenten entfernen, sonst überschreiben sie unseren Text mit "FIND PARTS".
            foreach (var localized in go.GetComponentsInChildren<LocalizedText>(true))
            {
                Object.DestroyImmediate(localized);
            }

            var icon = go.transform.Find("IconPlaceholder");
            if (icon != null)
            {
                icon.gameObject.SetActive(keepIcon);
            }

            ApplyLabel(go.transform, Lang.T(labelKey));
            return go;
        }

        // Icon vom ASSEMBLE-Button (Puzzleteil) übernehmen, passt besser als die Lupe von FIND PARTS.
        private static void UseAssembleIcon(EditBuildScreen screen, GameObject main)
        {
            var target = main.transform.Find("IconPlaceholder/Icon")?.GetComponent<Image>();
            var assemble = screen._assembleButton;
            if (target == null || assemble == null)
            {
                return;
            }
            var background = assemble._button != null ? assemble._button.targetGraphic : null;
            var source = assemble.GetComponentsInChildren<Image>(true)
                .FirstOrDefault(i => i.sprite != null && i != background && i.name.ToLower().Contains("icon"))
                ?? assemble.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.sprite != null && i != background);
            if (source != null)
            {
                target.sprite = source.sprite;
                target.preserveAspect = true;
            }
            else
            {
                Plugin.Log.LogInfo("No icon found on the ASSEMBLE button, using the magnifier icon.");
            }
        }

        // Schmaler heller Streifen links: zeigt, dass die Modus-Buttons zu AUTO BUILD gehören.
        private static void AddAccentBar(GameObject button)
        {
            var bar = new GameObject("AccentBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bar.transform.SetParent(button.transform, false);
            var rect = (RectTransform)bar.transform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(2f, -4f);
            rect.anchoredPosition = Vector2.zero;
            var image = bar.GetComponent<Image>();
            image.color = Accent;
            image.raycastTarget = false;
        }

        // Der Text von FIND PARTS ist im Original ausgeschaltet, bis das Spiel ihn einschaltet.
        // Darum schalten wir ihn selbst ein und setzen unseren Text.
        internal static void ApplyLabel(Transform button, string text)
        {
            foreach (var label in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                label.gameObject.SetActive(true);
                label.enabled = true;
                label.alpha = 1f;
                label.text = text;
                if (label.isActiveAndEnabled)
                {
                    label.ForceMeshUpdate();
                }
            }
        }
    }

    // Teil 2: Nach Show() ist das Layout fertig. Dann setzen wir den Button
    // linksbündig unter die beiden Icon-Buttons (Speichern / Zusammenbauen) und die Modus-Buttons eingerückt darunter.
    public class EditBuildScreenButtonPositionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(EditBuildScreen).GetMethod(nameof(EditBuildScreen.Show),
                new[] { typeof(Item), typeof(Item), typeof(InventoryController), typeof(IEftSession) });
        }

        [PatchPostfix]
        private static void Postfix(EditBuildScreen __instance)
        {
            var rect = __instance.transform.Find(EditBuildScreenButtonPatch.ButtonName) as RectTransform;
            if (rect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();

            var corners = new Vector3[4]; // 0 = unten links, 1 = oben links, 2 = oben rechts, 3 = unten rechts
            var findParts = (RectTransform)__instance._findPartsButton.transform;
            findParts.GetWorldCorners(corners);
            float left = corners[0].x;
            float width = findParts.rect.width;

            float bottom = float.MaxValue;
            foreach (var iconButton in new Component[] { __instance._saveButton, __instance._assembleButton })
            {
                ((RectTransform)iconButton.transform).GetWorldCorners(corners);
                bottom = Mathf.Min(bottom, corners[0].y);
            }

            // 40 px Abstand, damit die Beschriftung/Hovertexte der Icon-Buttons frei bleiben.
            float scale = rect.lossyScale.x;
            Place(rect, left, bottom - 40f * rect.lossyScale.y, width);
            EditBuildScreenButtonPatch.ApplyLabel(rect, Lang.T(EditBuildScreenButtonPatch.MainLabelKey));

            // Modus-Buttons eingerückt untereinander, eingeklappt.
            float step = (rect.rect.height + 3f) * rect.lossyScale.y;
            float indent = EditBuildScreenButtonPatch.SubIndent;
            int i = 1;
            foreach (var (name, _, _) in EditBuildScreenButtonPatch.ModeButtons)
            {
                if (__instance.transform.Find(name) is RectTransform modeRect)
                {
                    Place(modeRect, left + indent * scale, rect.position.y - step * i, width - indent);
                    modeRect.gameObject.SetActive(false);
                    i++;
                }
            }
        }

        private static void Place(RectTransform rect, float x, float y, float width)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rect.position = new Vector3(x, y, rect.position.z);
        }
    }
}
