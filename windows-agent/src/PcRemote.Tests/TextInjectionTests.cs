using PcRemote.Input;
using Xunit;

namespace PcRemote.Tests;

/// <summary>
/// The phone sends what the user typed. These tests pin down how that text
/// becomes keystrokes, without calling SendInput: a real call would type into
/// whatever window happened to be focused on the test machine.
/// </summary>
public class TextInjectionTests
{
    private const ushort Backspace = 0x08;

    [Fact]
    public void Plain_text_becomes_one_stroke_per_character()
    {
        var strokes = Injector.StrokesFor("hi");

        Assert.Equal(2, strokes.Count);
        Assert.Equal('h', strokes[0].Character);
        Assert.Equal('i', strokes[1].Character);
    }

    [Fact]
    public void Every_ordinary_character_is_typed_as_unicode()
    {
        // A virtual key would be interpreted by the current keyboard layout, so
        // an accented or non-Latin character would arrive as the wrong glyph.
        Assert.All(Injector.StrokesFor("aA1"), stroke => Assert.Null(stroke.VirtualKey));
    }

    [Fact]
    public void Backspace_is_a_key_press_not_a_character()
    {
        // The phone sends \b when the user deletes a character. Typing it as a
        // literal would insert a stray control character into the document.
        var stroke = Assert.Single(Injector.StrokesFor("\b"));

        // A Backspace is a named key press; no character is typed at all.
        Assert.Equal(Backspace, stroke.VirtualKey);
        Assert.Equal('\0', stroke.Character);
    }

    [Fact]
    public void A_delete_and_text_mix_splits_into_the_right_strokes()
    {
        var strokes = Injector.StrokesFor("ab\bc");

        Assert.Equal(4, strokes.Count);
        Assert.Equal('a', strokes[0].Character);
        Assert.Equal('b', strokes[1].Character);
        Assert.Equal(Backspace, strokes[2].VirtualKey);
        Assert.Equal('c', strokes[3].Character);
    }

    [Fact]
    public void Several_deletes_become_several_backspaces()
    {
        var strokes = Injector.StrokesFor("\b\b\b");

        Assert.Equal(3, strokes.Count);
        Assert.All(strokes, stroke => Assert.Equal(Backspace, stroke.VirtualKey));
    }

    [Fact]
    public void Empty_text_produces_nothing_to_type()
    {
        Assert.Empty(Injector.StrokesFor(""));
    }
}