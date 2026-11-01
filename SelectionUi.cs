using System;
using System.Collections.Generic;
using HarmonyLib;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChainedBeasts.Hybrids;

/// <summary>
/// The existing three native buttons keep native indices 0..2. Alt changes their
/// presentation and the mod-owned choice, never the index sent to native code.
/// </summary>
internal static class SelectionUi
{
    private static readonly Dictionary<IntPtr, PageView> Pages = new();
    private static readonly Dictionary<IntPtr, VariantView> Variants = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static readonly string[] HybridNumerals = { "IV", "V", "VI" };
    [ThreadStatic] private static int _selectDepth;
    [ThreadStatic] private static int _variantRpcDepth;

    internal static void Install(Harmony harmony)
    {
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobbyCharacterVariant),
            nameof(UIGameServerLobbyCharacterVariant.HandleSelectedInternal),
            prefix: nameof(VariantSelected), args: new[] { typeof(ClientIdentifier) });
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobbyCharacterVariant),
            nameof(UIGameServerLobbyCharacterVariant.Refresh), postfix: nameof(VariantRefreshed),
            args: new[] { typeof(GameServerLobby.UIState), typeof(bool) });
        // This concrete controller method is also used by native navigation.
        // Patch neither the shared UIClientNavigationButton base nor its vtable.
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobby),
            nameof(UIGameServerLobby.HandleCharacterVariantSelected), prefix: nameof(ControllerSelected),
            args: new[] { typeof(ClientIdentifier), typeof(int), typeof(int) });
        NativeHooks.Patch(typeof(SelectionUi), typeof(GameServerLobby),
            nameof(GameServerLobby.HandleSubmitRpc), prefix: nameof(Submitted),
            args: new[] { typeof(ClientIdentifier) });
        NativeHooks.Patch(typeof(SelectionUi), typeof(GameServerLobby),
            nameof(GameServerLobby.HandleCharacterVariantSelectedRpc), prefix: nameof(VariantRpcBefore),
            finalizer: nameof(VariantRpcFinally),
            args: new[] { typeof(ClientIdentifier), typeof(int), typeof(int) });
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobbyPage),
            nameof(UIGameServerLobbyPage.Refresh), prefix: nameof(PageRefreshing), postfix: nameof(PageRefreshed),
            args: new[] { typeof(GameServerLobby.UIState), typeof(int) });
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobbyPage),
            nameof(UIGameServerLobbyPage.Update), postfix: nameof(PageUpdated), args: Type.EmptyTypes);
        NativeHooks.Patch(typeof(SelectionUi), typeof(UIGameServerLobbyPage),
            nameof(UIGameServerLobbyPage.OnDestroy), prefix: nameof(PageDestroyed), args: Type.EmptyTypes);
    }

    private static bool VariantSelected(UIGameServerLobbyCharacterVariant __instance, ClientIdentifier clientID)
    {
        if (!HybridRuntime.EnabledForSelection) return true;
        try
        {
            var client = LocalClient(clientID, __instance.playerIndex);
            if (client == null) return true;
            // beastType is already the actual enum, not the visual sorted row.
            Select(client, (int)__instance.beastType, __instance.beastVariantIndex);
            return false;
        }
        catch (Exception exception)
        {
            Report("select", exception);
            // A failed Alt action must not quietly select the ordinary variant.
            return !HybridRuntime.AltHeld;
        }
    }

    private static bool ControllerSelected(ClientIdentifier clientID, int characterIndex, int variantIndex)
    {
        if (!HybridRuntime.EnabledForSelection) return true;
        try
        {
            var client = LocalClient(clientID, -1);
            if (client == null) return true;
            // Native HandleCharacterVariantSelected forwards this integer to
            // the same RPC as the concrete button's actual BeastType field.
            Select(client, characterIndex, variantIndex);
            return false;
        }
        catch (Exception exception)
        {
            Report("navigation select", exception);
            return !HybridRuntime.AltHeld;
        }
    }

    private static void Select(ClientIdentifier client, int beast, int variant)
    {
        if (beast < 0 || beast > 7 || variant < 0 || variant > 2) return;
        var hybrid = HybridRuntime.AltHeld;
        if (hybrid && !HybridRuntime.CanSelect(client, beast, variant, out _)) return;
        // Runtime latches the choice before sending the ordinary native RPC.
        // Releasing Alt afterwards never calls this method or changes the choice.
        _selectDepth++;
        try { HybridRuntime.SelectLocal(client, beast, variant, hybrid); }
        finally { _selectDepth--; }
    }

    private static bool Submitted(GameServerLobby __instance, ClientIdentifier clientID)
    {
        // Keyboard/controller submit reaches this RPC directly through
        // IClientNavigation, without invoking the concrete button callback.
        if (_selectDepth > 0 || _variantRpcDepth > 0 || !HybridRuntime.EnabledForSelection) return true;
        try
        {
            var client = LocalClient(clientID, -1);
            var state = __instance.uiState;
            if (client == null || state == null || state.gameStarting || state.isRestoringCheckpoint) return true;
            var player = checked((int)client.PlayerIndex);
            var current = state.GetState(player);
            if (current == GameServerLobby.eState.CHARACTER_CONFIRM)
                return !HybridTransport.PendingLocal;
            if (current != GameServerLobby.eState.CHARACTER_NAVIGATE) return true;
            var beasts = state.navBeastIndex;
            var variants = state.navBeastVariantIndex;
            if (beasts == null || variants == null || player < 0 || player >= beasts.Length || player >= variants.Length)
                return false;
            // SelectLocal invokes SelectedRpc, whose own Submit must run once.
            // The depth guard admits that nested native submission and prevents
            // recursively publishing another mod selection.
            Select(client, beasts[player], variants[player]);
            return false;
        }
        catch (Exception exception)
        {
            Report("navigation submit", exception);
            return !HybridRuntime.AltHeld;
        }
    }

    private static void VariantRpcBefore(out bool __state)
    {
        // Sending and receiving can be separate invocations on the host. Scope
        // every actual wrapper invocation so a deferred receive's nested Submit
        // cannot interpret the now-released Alt key as a fresh ordinary choice.
        _variantRpcDepth++;
        __state = true;
    }

    private static Exception VariantRpcFinally(Exception __exception, bool __state)
    {
        if (__state) _variantRpcDepth--;
        return __exception;
    }

    private static void VariantRefreshed(UIGameServerLobbyCharacterVariant __instance,
        GameServerLobby.UIState uiState, bool isLocal)
    {
        try { RefreshVariant(__instance, isLocal); }
        catch (Exception exception) { Report("variant display", exception); }
    }

    private static void RefreshVariant(UIGameServerLobbyCharacterVariant button, bool isLocal)
    {
        if (button == null || button.beastVariantIndex < 0 || button.beastVariantIndex > 2) return;
        var displayHybrid = isLocal && HybridRuntime.EnabledForSelection && HybridRuntime.AltHeld;
        if (!Variants.TryGetValue(button.Pointer, out var view))
        {
            if (!displayHybrid) return;
            var page = FindPage(button);
            if (page == null || page.beastLevelText == null || button.icon == null) return;
            view = CreateNumeral(button, page);
            Variants.Add(button.Pointer, view);
        }

        if (!displayHybrid)
        {
            view.Hide();
            // Native Refresh already restored the ordinary lock/icon game objects.
            return;
        }

        var client = LocalClient(null, button.playerIndex);
        var allowed = client != null && HybridRuntime.CanSelect(client,
            (int)button.beastType, button.beastVariantIndex, out _);
        view.HideIcon();
        if (button.lockVisual != null) button.lockVisual.SetActive(!allowed);
        if (view.Label != null)
        {
            view.Label.text = HybridNumerals[button.beastVariantIndex];
            view.Label.color = allowed ? button.icon.color : button.baseColor;
            SetNumeralLayout(button, view.Label, allowed);
            // All new tiers begin locked. Keep their numeral visible in the
            // corner beside the unchanged native lock, rather than hiding the
            // only visible difference between the ordinary and Alt grids.
            view.Label.gameObject.SetActive(true);
        }
    }

    private static void SetNumeralLayout(UIGameServerLobbyCharacterVariant button, TMP_Text label, bool unlocked)
    {
        var source = button.icon.rectTransform;
        var rect = label.rectTransform;
        rect.localScale = source.localScale;
        rect.localRotation = source.localRotation;
        if (unlocked)
        {
            rect.anchorMin = source.anchorMin;
            rect.anchorMax = source.anchorMax;
            rect.pivot = source.pivot;
            rect.sizeDelta = source.sizeDelta;
            rect.anchoredPosition3D = source.anchoredPosition3D;
            label.alignment = TextAlignmentOptions.Center;
            label.fontSizeMax = 26f;
            label.fontSizeMin = 10f;
        }
        else
        {
            // Audited native backing: 64 x 65; lock: 55 x 48, centered.
            // Use the upper-left corner without moving or rescaling the lock.
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition3D = new Vector3(-29f, 31f, 0f);
            rect.sizeDelta = new Vector2(28f, 20f);
            label.alignment = TextAlignmentOptions.TopLeft;
            label.fontSizeMax = 16f;
            label.fontSizeMin = 12f;
        }
    }

    private static VariantView CreateNumeral(UIGameServerLobbyCharacterVariant button,
        UIGameServerLobbyPage page)
    {
        // Borrow the game's live TMP font/material by cloning its own label.
        // No image/font asset is created or bundled, and the source is unchanged.
        var label = Object.Instantiate(page.beastLevelText, button.icon.transform.parent, false);
        label.gameObject.name = "ChainedBeasts.Hybrids.VariantNumeral";
        label.gameObject.hideFlags = HideFlags.DontSave;
        foreach (var localize in label.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
        foreach (var graphic in label.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
        foreach (var fitter in label.GetComponentsInChildren<ContentSizeFitter>(true)) fitter.enabled = false;
        foreach (var group in label.GetComponentsInChildren<CanvasGroup>(true))
        {
            group.blocksRaycasts = false;
            group.interactable = false;
        }
        var layout = label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>();
        layout.ignoreLayout = true;
        var source = button.icon.rectTransform;
        var rect = label.rectTransform;
        rect.anchorMin = source.anchorMin;
        rect.anchorMax = source.anchorMax;
        rect.pivot = source.pivot;
        rect.sizeDelta = source.sizeDelta;
        rect.anchoredPosition3D = source.anchoredPosition3D;
        rect.localScale = source.localScale;
        rect.localRotation = source.localRotation;
        rect.SetAsLastSibling();
        label.raycastTarget = false;
        label.alignment = TextAlignmentOptions.Center;
        label.margin = Vector4.zero;
        label.richText = false;
        label.enableAutoSizing = true;
        label.fontSizeMax = Math.Max(16f, Math.Min(26f, page.beastLevelText.fontSize));
        label.fontSizeMin = 10f;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.enabled = true;
        label.gameObject.SetActive(false);
        return new VariantView(button, label, page.Pointer);
    }

    private static void PageRefreshed(UIGameServerLobbyPage __instance,
        GameServerLobby.UIState uiState, int playerIndex)
    {
        try
        {
            var view = GetPage(__instance);
            view.LastAlt = HybridRuntime.AltHeld;
            view.LastEnabled = HybridRuntime.EnabledForSelection;
            view.LastUnlockMask = HybridRuntime.LocalMask;
            UpdateDescription(view);
        }
        catch (Exception exception) { Report("character description", exception); }
    }

    private static void PageRefreshing(UIGameServerLobbyPage __instance)
    {
        try
        {
            // Restore our previous level text before native code computes the
            // next base preview. The postfix can then capture the fresh native
            // value, rather than accidentally saving our own LVL IV/V/VI text.
            if (Pages.TryGetValue(__instance.Pointer, out var view)) view.RestoreLevel();
        }
        catch (Exception exception) { Report("level text refresh", exception); }
    }

    private static void PageUpdated(UIGameServerLobbyPage __instance)
    {
        try
        {
            var view = GetPage(__instance);
            var alt = HybridRuntime.AltHeld;
            var enabled = HybridRuntime.EnabledForSelection;
            var mask = HybridRuntime.LocalMask;
            if ((view.LastAlt != alt || view.LastEnabled != enabled || view.LastUnlockMask != mask) && __instance.uiState != null)
            {
                view.LastAlt = alt;
                view.LastEnabled = enabled;
                view.LastUnlockMask = mask;
                // A modifier can change while the cursor/focus stays still.
                // Native refresh restores base visuals before our postfixes.
                __instance.Refresh(__instance.uiState, __instance.playerIndex);
            }
            else UpdateDescription(view);
        }
        catch (Exception exception) { Report("modifier refresh", exception); }
    }

    private static PageView GetPage(UIGameServerLobbyPage page)
    {
        if (!Pages.TryGetValue(page.Pointer, out var view))
        {
            view = new PageView(page);
            Pages.Add(page.Pointer, view);
        }
        return view;
    }

    private static void UpdateDescription(PageView view)
    {
        var page = view.Page;
        var state = page.uiState;
        var player = page.playerIndex;
        if (!HybridRuntime.EnabledForSelection || state == null || state.isRestoringCheckpoint || player < 0)
        {
            view.RestoreText();
            return;
        }
        var client = LocalClient(null, player);
        if (client == null) { view.RestoreText(); return; }
        var navigating = state.GetState(player) == GameServerLobby.eState.CHARACTER_NAVIGATE;
        var hybrid = false;
        var beast = -1;
        var variant = -1;
        var reason = "";
        if (navigating && HybridRuntime.AltHeld)
        {
            // The native details card follows navigation, not the committed pick.
            var beasts = state.navBeastIndex;
            var variants = state.navBeastVariantIndex;
            if (beasts != null && variants != null && player < beasts.Length && player < variants.Length)
            {
                beast = beasts[player];
                variant = variants[player];
                hybrid = beast >= 0 && beast <= 7 && variant >= 0 && variant <= 2;
                if (hybrid && !HybridRuntime.CanSelect(client, beast, variant, out reason) && string.IsNullOrEmpty(reason))
                    reason = HybridRuntime.LockReason(client, beast, variant);
            }
        }
        else if (!navigating && HybridRuntime.TryGetSelection(player, out beast, out variant, out var selectedHybrid))
        {
            hybrid = selectedHybrid && beast >= 0 && beast <= 7 && variant >= 0 && variant <= 2;
            var nativeBeasts = state.beastIndex;
            var nativeVariants = state.beastVariantIndex;
            hybrid = hybrid && nativeBeasts != null && nativeVariants != null &&
                player < nativeBeasts.Length && player < nativeVariants.Length &&
                nativeBeasts[player] == beast && nativeVariants[player] == variant;
        }
        if (!hybrid) { view.RestoreText(); return; }
        var description = HybridRuntime.BuildDescription(beast, variant);
        if (!string.IsNullOrEmpty(reason)) description += "\n\nLocked: " + reason;
        view.ShowText(HybridNumerals[variant] + " - " + HybridRuntime.BuildName(beast, variant), description, variant);
    }

    private static UIGameServerLobbyPage FindPage(UIGameServerLobbyCharacterVariant button)
    {
        var parent = button.GetComponentInParent<UIGameServerLobbyPage>();
        if (parent != null) return parent;
        var pages = button.controller?.pages;
        if (pages == null) return null;
        foreach (var page in pages)
        {
            if (page == null || page.characterOptions == null) continue;
            foreach (var character in page.characterOptions)
            {
                if (character == null || character.variants == null) continue;
                foreach (var variant in character.variants)
                    if (variant != null && variant.Pointer == button.Pointer) return page;
            }
        }
        return null;
    }

    private static ClientIdentifier LocalClient(ClientIdentifier candidate, int player)
    {
        if (!NetworkSession.InstanceExists) return null;
        var session = NetworkSession.Instance;
        if (candidate != null) return session.IsLocalClient(candidate) ? candidate : null;
        if (player >= 0)
        {
            var client = session.GetClientByPlayerIndex((ulong)player);
            return client != null && session.IsLocalClient(client) ? client : null;
        }
        var locals = session.LocalClients;
        return locals != null && locals.Cast<Il2CppSystem.Collections.Generic.ICollection<ClientIdentifier>>().Count == 1
            ? locals[0] : null;
    }

    private static void PageDestroyed(UIGameServerLobbyPage __instance)
    {
        try
        {
            var pointer = __instance.Pointer;
            foreach (var key in new List<IntPtr>(Variants.Keys))
            {
                var view = Variants[key];
                if (view.PagePointer != pointer) continue;
                view.Destroy();
                Variants.Remove(key);
            }
            Pages.Remove(pointer);
        }
        catch (Exception exception) { Report("page cleanup", exception); }
    }

    internal static void Reset()
    {
        foreach (var view in Variants.Values) view.Destroy();
        foreach (var page in Pages.Values)
        {
            try { page.RestoreText(); }
            catch (Exception exception) { Report("cleanup", exception); }
        }
        Variants.Clear();
        Pages.Clear();
    }

    private static void Report(string operation, Exception exception)
    {
        if (Reported.Add(operation + ":" + exception.GetType().Name))
            HybridRuntime.Log?.LogWarning("[Hybrids] " + operation + ": " + exception.Message);
    }

    private sealed class VariantView
    {
        internal readonly UIGameServerLobbyCharacterVariant Button;
        internal readonly TMP_Text Label;
        internal readonly IntPtr PagePointer;
        private bool _iconHidden;
        private bool _iconEnabled;

        internal VariantView(UIGameServerLobbyCharacterVariant button, TMP_Text label, IntPtr pagePointer)
        { Button = button; Label = label; PagePointer = pagePointer; }

        internal void HideIcon()
        {
            if (Button == null || Button.icon == null) return;
            if (!_iconHidden) { _iconEnabled = Button.icon.enabled; _iconHidden = true; }
            Button.icon.enabled = false;
        }
        internal void Hide()
        {
            if (Label != null) Label.gameObject.SetActive(false);
            if (_iconHidden && Button != null && Button.icon != null) Button.icon.enabled = _iconEnabled;
            _iconHidden = false;
        }
        internal void Destroy()
        {
            Hide();
            if (Label != null) Object.Destroy(Label.gameObject);
        }
    }

    private sealed class PageView
    {
        internal readonly UIGameServerLobbyPage Page;
        internal bool LastAlt;
        internal bool LastEnabled;
        internal int LastUnlockMask = -1;
        private bool _overridden;
        private TextStyle _name;
        private TextStyle _description;
        private bool _levelOverridden;
        private string _baseLevelText;

        internal PageView(UIGameServerLobbyPage page) { Page = page; }

        internal void ShowText(string name, string description, int variant)
        {
            if (Page == null || Page.characterName == null || Page.characterDescription == null) return;
            if (!_overridden)
            {
                _name = new TextStyle(Page.characterName.GetComponent<TMP_Text>());
                _description = new TextStyle(Page.characterDescription.GetComponent<TMP_Text>());
                _overridden = true;
            }
            _name.Show(name);
            _description.Show(description);
            if (Page.beastLevelText != null)
            {
                if (!_levelOverridden)
                {
                    _baseLevelText = Page.beastLevelText.text;
                    _levelOverridden = true;
                }
                var level = "LVL " + HybridNumerals[variant];
                if (Page.beastLevelText.text != level) Page.beastLevelText.text = level;
            }
        }
        internal void RestoreLevel()
        {
            if (!_levelOverridden) return;
            _levelOverridden = false;
            if (Page != null && Page.beastLevelText != null) Page.beastLevelText.text = _baseLevelText;
        }
        internal void RestoreText()
        {
            RestoreLevel();
            if (!_overridden) return;
            _overridden = false;
            _name?.Restore();
            _description?.Restore();
            if (Page == null) return;
            // SetTerm may skip work when the native term did not change. Force
            // just these native components to regenerate their current text.
            if (Page.characterName != null) Page.characterName.OnLocalize(true);
            if (Page.characterDescription != null) Page.characterDescription.OnLocalize(true);
        }
    }

    private sealed class TextStyle
    {
        private readonly TMP_Text _text;
        private readonly bool _autoSize;
        private readonly float _size;
        private readonly float _minimum;
        private readonly float _maximum;
        private readonly TextWrappingModes _wrapping;
        private readonly TextOverflowModes _overflow;
        internal TextStyle(TMP_Text text)
        {
            _text = text;
            if (text == null) return;
            _autoSize = text.enableAutoSizing;
            _size = text.fontSize;
            _minimum = text.fontSizeMin;
            _maximum = text.fontSizeMax;
            _wrapping = text.textWrappingMode;
            _overflow = text.overflowMode;
        }
        internal void Show(string value)
        {
            if (_text == null) return;
            _text.enableAutoSizing = true;
            _text.fontSizeMax = Math.Max(16f, _size);
            _text.fontSizeMin = 10f;
            _text.textWrappingMode = TextWrappingModes.Normal;
            _text.overflowMode = TextOverflowModes.Truncate;
            if (_text.text != value) _text.text = value;
        }
        internal void Restore()
        {
            if (_text == null) return;
            _text.enableAutoSizing = _autoSize;
            _text.fontSizeMin = _minimum;
            _text.fontSizeMax = _maximum;
            _text.fontSize = _size;
            _text.textWrappingMode = _wrapping;
            _text.overflowMode = _overflow;
        }
    }
}
