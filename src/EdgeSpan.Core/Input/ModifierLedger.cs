namespace EdgeSpan.Input;

/// <summary>
/// Pairs each key-down with its key-up. A down that Windows did not see has its
/// up swallowed too. A down that was not forwarded is not completed on the other
/// device. Modifiers are tracked individually, left and right.
/// </summary>
public sealed class ModifierLedger
{
    public const int DummyVk = 0xFF;
    public static readonly int[] All =
    {
        0x10, 0x11, 0x12,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
        0x5B, 0x5C
    };

    private readonly object _gate = new();
    private readonly HashSet<int> _remoteDown = new();
    private readonly HashSet<int> _swallowUp = new();

    public readonly record struct Decision(bool Swallow, bool Forward, bool Hotkey);

    public static bool IsModifier(int virtualKey)
    {
        foreach (var vk in All)
        {
            if (vk == virtualKey)
                return true;
        }
        return false;
    }

    public static bool IsAlt(int virtualKey) => virtualKey is 0x12 or 0xA4 or 0xA5;

    public static bool IsExtended(int virtualKey) => virtualKey is 0xA3 or 0xA5 or 0x5C;

    public Decision Observe(int virtualKey, bool down, bool remoteHasControl, bool hotkey)
    {
        lock (_gate)
        {
            if (hotkey)
            {
                if (down)
                    _swallowUp.Add(virtualKey);
                else
                    _swallowUp.Remove(virtualKey);
                return new Decision(true, false, true);
            }
            if (down)
            {
                if (remoteHasControl)
                {
                    _swallowUp.Add(virtualKey);
                    _remoteDown.Add(virtualKey);
                    return new Decision(true, true, false);
                }
                _swallowUp.Remove(virtualKey);
                return new Decision(false, false, false);
            }
            var swallowed = _swallowUp.Remove(virtualKey);
            var forward = _remoteDown.Remove(virtualKey);
            return new Decision(swallowed, forward, false);
        }
    }

    /// <summary>
    /// Key-ups to send to the device. Modifiers are always included.
    /// Held non-modifiers are included when leaving the device, so a forwarded
    /// down is not left without its up. Swallowed physical ups stay swallowed.
    /// </summary>
    public int[] TakeRemoteUps(bool includeHeldKeys)
    {
        lock (_gate)
        {
            var ups = new List<int>(All);
            if (includeHeldKeys)
            {
                foreach (var vk in _remoteDown)
                {
                    if (!IsModifier(vk))
                        ups.Add(vk);
                }
                _remoteDown.Clear();
            }
            else
            {
                foreach (var vk in All)
                    _remoteDown.Remove(vk);
            }
            return ups.ToArray();
        }
    }

    public void NoteHostReleased(IReadOnlyList<int> virtualKeys)
    {
        lock (_gate)
        {
            foreach (var vk in virtualKeys)
            {
                if (IsModifier(vk))
                    _swallowUp.Add(vk);
            }
        }
    }
}

public readonly record struct KeyStroke(int VirtualKey, bool Down);

/// <summary>
/// Host-side releases for modifiers GetAsyncKeyState still reports down.
/// A lone Alt key-up opens the menu bar, so an Alt release is followed by a dummy key.
/// </summary>
public static class ModifierReleasePlan
{
    public static KeyStroke[] HostKeyUps(IEnumerable<int> downVirtualKeys)
    {
        var strokes = new List<KeyStroke>();
        var alt = false;
        foreach (var vk in downVirtualKeys.Distinct())
        {
            if (!ModifierLedger.IsModifier(vk))
                continue;
            strokes.Add(new KeyStroke(vk, false));
            if (ModifierLedger.IsAlt(vk))
                alt = true;
        }
        if (alt)
        {
            strokes.Add(new KeyStroke(ModifierLedger.DummyVk, true));
            strokes.Add(new KeyStroke(ModifierLedger.DummyVk, false));
        }
        return strokes.ToArray();
    }
}

/// <summary>
/// Win+L, UAC, and the secure desktop hide key-ups from the low-level hook.
/// Coming back to the interactive desktop is the moment to drop stuck modifiers.
/// </summary>
public static class SecureDesktop
{
    public static bool IsAway(string? desktopName) =>
        !string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase);

    public static bool Returned(bool wasAway, bool nowAway) => wasAway && !nowAway;

    public static bool MarkAway(int sessionReason) => sessionReason is 0x2 or 0x4 or 0x6 or 0x7;

    public static bool Recover(int sessionReason) => sessionReason is 0x1 or 0x3 or 0x5 or 0x8;
}
