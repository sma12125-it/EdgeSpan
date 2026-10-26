namespace EdgeSpan.Input;

public readonly record struct Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, int VirtualKey)
{
    public bool Matches(int virtualKey, bool ctrl, bool alt, bool shift, bool win) =>
        VirtualKey == virtualKey && Ctrl == ctrl && Alt == alt && Shift == shift && Win == win;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyMap.NameOf(VirtualKey));
        return string.Join("+", parts);
    }
}

public static class HotkeyPolicy
{
    public static bool TryNumberChord(int virtualKey, bool ctrl, bool alt, bool shift, bool win, out int number)
    {
        number = 0;
        if (!ctrl || !alt || shift || win)
            return false;
        if (virtualKey is >= '1' and <= '9')
        {
            number = virtualKey - '0';
            return true;
        }
        if (virtualKey is >= 0x61 and <= 0x69)
        {
            number = virtualKey - 0x60;
            return true;
        }
        return false;
    }

    public static bool Conflicts(string? switchChord, string? lockChord, bool numberHotkeys)
    {
        var left = HotkeyParser.Parse(switchChord);
        var right = HotkeyParser.Parse(lockChord);
        if (left.VirtualKey != 0 && left == right)
            return true;
        return numberHotkeys && TryNumberChord(left.VirtualKey, left.Ctrl, left.Alt, left.Shift, left.Win, out _);
    }
}

public static class HotkeyParser
{
    public static Hotkey Parse(string? text)
    {
        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        var vk = 0;
        foreach (var raw in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                case "meta":
                    win = true;
                    break;
                default:
                    vk = KeyMap.FromName(token);
                    break;
            }
        }
        return new Hotkey(ctrl, alt, shift, win, vk);
    }
}

public readonly record struct KeyMapping(int Vk, int Hid, int Android, uint Plain, uint Shifted);

public static class KeyMap
{
    public static IReadOnlyList<KeyMapping> All { get; } = Build();

    public static bool TryHid(int vk, out int hid)
    {
        foreach (var map in All)
        {
            if (map.Vk == vk && map.Hid != 0)
            {
                hid = map.Hid;
                return true;
            }
        }
        hid = 0;
        return false;
    }

    public static bool TryAndroid(int vk, out int android)
    {
        foreach (var map in All)
        {
            if (map.Vk == vk && map.Android != 0)
            {
                android = map.Android;
                return true;
            }
        }
        android = 0;
        return false;
    }

    public static uint Codepoint(int vk, bool shift)
    {
        foreach (var map in All)
        {
            if (map.Vk == vk)
                return shift ? map.Shifted : map.Plain;
        }
        return 0;
    }

    public static byte ModifierBits(bool ctrl, bool alt, bool shift, bool win)
    {
        byte mods = 0;
        if (ctrl) mods |= 0x01;
        if (shift) mods |= 0x02;
        if (alt) mods |= 0x04;
        if (win) mods |= 0x08;
        return mods;
    }

    public static string NameOf(int vk) => vk switch
    {
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        0x0D => "Enter",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x1B => "Escape",
        0x20 => "Space",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1),
        _ => "VK" + vk.ToString("X")
    };

    public static int FromName(string token)
    {
        if (token.Length == 1 && token[0] is >= 'a' and <= 'z')
            return char.ToUpperInvariant(token[0]);
        if (token.Length == 1 && token[0] is >= '0' and <= '9')
            return token[0];
        if (token.Length is >= 2 and <= 3 && token[0] == 'f' && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 12)
            return 0x70 + fn - 1;
        return token switch
        {
            "enter" or "return" => 0x0D,
            "backspace" or "back" => 0x08,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "space" => 0x20,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "delete" or "del" => 0x2E,
            _ => 0
        };
    }

    private static List<KeyMapping> Build()
    {
        var list = new List<KeyMapping>();
        for (var i = 0; i < 26; i++)
            list.Add(new KeyMapping(0x41 + i, 0x04 + i, 29 + i, (uint)('a' + i), (uint)('A' + i)));
        for (var d = 1; d <= 9; d++)
            list.Add(new KeyMapping(0x30 + d, 0x1E + d - 1, 8 + d - 1, (uint)('0' + d), (uint)('0' + d)));
        list.Add(new KeyMapping(0x30, 0x27, 7, '0', '0'));
        list.Add(new KeyMapping(0x0D, 0x28, 66, 0, 0));
        list.Add(new KeyMapping(0x08, 0x2A, 67, 0, 0));
        list.Add(new KeyMapping(0x09, 0x2B, 61, 0, 0));
        list.Add(new KeyMapping(0x1B, 0x29, 111, 0, 0));
        list.Add(new KeyMapping(0x20, 0x2C, 62, ' ', ' '));
        list.Add(new KeyMapping(0x25, 0x50, 21, 0, 0));
        list.Add(new KeyMapping(0x26, 0x52, 19, 0, 0));
        list.Add(new KeyMapping(0x27, 0x4F, 22, 0, 0));
        list.Add(new KeyMapping(0x28, 0x51, 20, 0, 0));
        list.Add(new KeyMapping(0x2E, 0x4C, 112, 0, 0));
        list.Add(new KeyMapping(0x24, 0x4A, 122, 0, 0));
        list.Add(new KeyMapping(0x23, 0x4D, 123, 0, 0));
        list.Add(new KeyMapping(0x21, 0x4B, 92, 0, 0));
        list.Add(new KeyMapping(0x22, 0x4E, 93, 0, 0));
        for (var f = 0; f < 12; f++)
            list.Add(new KeyMapping(0x70 + f, 0x3A + f, 131 + f, 0, 0));
        list.Add(new KeyMapping(0xBD, 0x2D, 69, (uint)'-', (uint)'_'));
        list.Add(new KeyMapping(0xBB, 0x2E, 70, (uint)'=', (uint)'+'));
        list.Add(new KeyMapping(0xBC, 0x36, 55, (uint)',', (uint)'<'));
        list.Add(new KeyMapping(0xBE, 0x37, 56, (uint)'.', (uint)'>'));
        list.Add(new KeyMapping(0xBF, 0x38, 76, (uint)'/', (uint)'?'));
        list.Add(new KeyMapping(0x10, 0, 59, 0, 0));
        list.Add(new KeyMapping(0x11, 0, 113, 0, 0));
        list.Add(new KeyMapping(0x12, 0, 57, 0, 0));
        list.Add(new KeyMapping(0x5B, 0, 117, 0, 0));
        list.Add(new KeyMapping(0xA0, 0, 59, 0, 0));
        list.Add(new KeyMapping(0xA1, 0, 60, 0, 0));
        list.Add(new KeyMapping(0xA2, 0, 113, 0, 0));
        list.Add(new KeyMapping(0xA3, 0, 114, 0, 0));
        list.Add(new KeyMapping(0xA4, 0, 57, 0, 0));
        list.Add(new KeyMapping(0xA5, 0, 58, 0, 0));
        return list;
    }
}
