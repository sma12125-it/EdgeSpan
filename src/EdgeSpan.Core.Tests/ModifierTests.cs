using EdgeSpan.Input;

namespace EdgeSpan.Tests;

public class ModifierTests
{
    [Fact]
    public void A_swallowed_down_swallows_its_up_and_a_host_down_does_not()
    {
        var ledger = new ModifierLedger();
        var down = ledger.Observe(0xA4, true, remoteHasControl: true, hotkey: false);
        Assert.True(down.Swallow);
        Assert.True(down.Forward);
        var up = ledger.Observe(0xA4, false, remoteHasControl: false, hotkey: false);
        Assert.True(up.Swallow);
        Assert.True(up.Forward);

        var hostDown = ledger.Observe(0xA4, true, remoteHasControl: false, hotkey: false);
        Assert.False(hostDown.Swallow);
        Assert.False(hostDown.Forward);
        var hostUp = ledger.Observe(0xA4, false, remoteHasControl: true, hotkey: false);
        Assert.False(hostUp.Swallow);
        Assert.False(hostUp.Forward);
    }

    [Fact]
    public void The_shortcut_trigger_is_swallowed_and_the_modifiers_are_not()
    {
        var ledger = new ModifierLedger();
        Assert.False(ledger.Observe(0x11, true, false, false).Swallow);
        Assert.False(ledger.Observe(0x12, true, false, false).Swallow);
        var space = ledger.Observe(0x20, true, false, true);
        Assert.True(space.Swallow);
        Assert.False(space.Forward);
        Assert.False(ledger.Observe(0x12, false, false, false).Swallow);
        Assert.False(ledger.Observe(0x11, false, false, false).Swallow);
        var spaceUp = ledger.Observe(0x20, false, false, false);
        Assert.True(spaceUp.Swallow);
        Assert.False(spaceUp.Forward);
    }

    [Fact]
    public void Leaving_the_device_emits_modifier_ups_and_keeps_the_physical_up_local()
    {
        var ledger = new ModifierLedger();
        ledger.Observe(0xA4, true, true, false);
        ledger.Observe(0x41, true, true, false);
        var ups = ledger.TakeRemoteUps(includeHeldKeys: true);
        Assert.Contains(0xA4, ups);
        Assert.Contains(0x11, ups);
        Assert.Contains(0x5B, ups);
        Assert.Contains(0x41, ups);
        var physical = ledger.Observe(0xA4, false, false, false);
        Assert.True(physical.Swallow);
        Assert.False(physical.Forward);
        var letter = ledger.Observe(0x41, false, false, false);
        Assert.True(letter.Swallow);
        Assert.False(letter.Forward);
    }

    [Fact]
    public void A_lone_alt_release_is_followed_by_a_dummy_key()
    {
        var alt = ModifierReleasePlan.HostKeyUps(new[] { 0xA4, 0xA2 });
        Assert.Contains(new KeyStroke(0xA4, false), alt);
        Assert.Contains(new KeyStroke(0xA2, false), alt);
        Assert.Contains(new KeyStroke(ModifierLedger.DummyVk, true), alt);
        Assert.Contains(new KeyStroke(ModifierLedger.DummyVk, false), alt);
        var ctrl = ModifierReleasePlan.HostKeyUps(new[] { 0xA2, 0x11 });
        Assert.DoesNotContain(ctrl, stroke => stroke.VirtualKey == ModifierLedger.DummyVk);
        Assert.Equal(2, ctrl.Length);
    }

    [Fact]
    public void A_synthesized_host_release_swallows_the_later_physical_up()
    {
        var ledger = new ModifierLedger();
        ledger.Observe(0x12, true, false, false);
        ledger.NoteHostReleased(new[] { 0x12, 0xA4 });
        var up = ledger.Observe(0x12, false, false, false);
        Assert.True(up.Swallow);
        Assert.False(up.Forward);
        var other = ledger.Observe(0xA5, false, false, false);
        Assert.False(other.Swallow);
    }

    [Fact]
    public void Secure_desktop_recovery_waits_until_the_interactive_desktop_returns()
    {
        Assert.True(SecureDesktop.IsAway(null));
        Assert.True(SecureDesktop.IsAway("Winlogon"));
        Assert.False(SecureDesktop.IsAway("Default"));
        Assert.False(SecureDesktop.Returned(wasAway: false, nowAway: false));
        Assert.False(SecureDesktop.Returned(wasAway: true, nowAway: true));
        Assert.True(SecureDesktop.Returned(wasAway: true, nowAway: false));
        Assert.True(SecureDesktop.MarkAway(0x7));
        Assert.False(SecureDesktop.Recover(0x7));
        Assert.True(SecureDesktop.Recover(0x8));
        Assert.True(SecureDesktop.Recover(0x1));
        Assert.True(SecureDesktop.Recover(0x3));
    }
}
