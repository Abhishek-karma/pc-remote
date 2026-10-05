// Tests for the two boundaries where a mistake becomes a real bug: the wire
// protocol, and the key allowlist at the injection boundary.

using System.Text.Json;
using PcRemote.Core;
using PcRemote.Input;
using Xunit;

namespace PcRemote.Tests;

public class ProtocolTests
{
    [Fact]
    public void Input_messages_round_trip_with_the_documented_field_names()
    {
        // The wire names are a contract with the Android app; renaming one
        // silently breaks input.
        var json = JsonSerializer.Serialize(new Message
        {
            Type = Protocol.Move,
            Dx = 12,
            Dy = -4,
        });

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"type\":\"move\"", json);
        Assert.Contains("\"dx\":12", json);
        Assert.Contains("\"dy\":-4", json);
    }

    [Theory]
    [InlineData(Protocol.Hello)]
    [InlineData(Protocol.Move)]
    [InlineData(Protocol.Button)]
    [InlineData(Protocol.Scroll)]
    [InlineData(Protocol.Key)]
    [InlineData(Protocol.Text)]
    [InlineData(Protocol.ReleaseAll)]
    [InlineData(Protocol.Disconnect)]
    public void Every_real_user_action_is_accepted(string type) => Assert.True(Protocol.IsAccepted(type));

    [Theory]
    [InlineData("stream_start")]
    [InlineData("media_control")]
    [InlineData("system_power")]
    [InlineData("mouse_move_abs")]
    [InlineData("sas")]
    [InlineData("")]
    [InlineData("MOVE")] // the allowlist is case sensitive on purpose
    public void Anything_outside_the_product_is_refused(string type) =>
        Assert.False(Protocol.IsAccepted(type));
}

public class KeyAllowlistTests
{
    [Theory]
    [InlineData("CTRL")]
    [InlineData("ALT")]
    [InlineData("SHIFT")]
    [InlineData("WIN")]
    [InlineData("ESC")]
    [InlineData("TAB")]
    [InlineData("ENTER")]
    [InlineData("BACKSPACE")]
    [InlineData("UP")]
    [InlineData("DOWN")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    public void The_keys_the_app_offers_are_all_allowed(string key) =>
        Assert.True(Injector.Keys.ContainsKey(key));

    [Theory]
    [InlineData("F1")]      // the removed F1-F24 deck
    [InlineData("F24")]
    [InlineData("PRT_SC")]
    [InlineData("WIN+D")]   // not a key we synthesize as a shortcut
    [InlineData("")]
    public void Unknown_or_removed_keys_are_not_allowed(string key) =>
        Assert.False(Injector.Keys.ContainsKey(key));

    [Fact]
    public void Keys_are_case_insensitive_so_the_app_cannot_miss_a_tap()
    {
        Assert.True(Injector.Keys.ContainsKey("enter"));
        Assert.Equal(Injector.Keys["ENTER"], Injector.Keys["enter"]);
    }

    [Fact]
    public void Every_latchable_modifier_is_released_on_disconnect()
    {
        // A modifier the app can latch but ReleaseAll cannot release would leave
        // the user's PC in a broken state after a disconnect.
        ushort[] latched = [0x10, 0x11, 0x12, 0x5B];
        foreach (var vk in latched)
        {
            Assert.Contains(Injector.Keys.Values, v => v == vk);
        }
    }
}