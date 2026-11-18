using CanvasDesktop.Dictation;

namespace CanvasDesktop.Dictation.Tests;

public class DictationTextSpanTests
{
    [Fact]
    public void ReplacesOnlyAnchoredSpanAcrossMultiplePartialsWithoutConcatenation()
    {
        var span = new DictationTextSpan();
        string text = "Hello world";
        span.CaptureAnchor(text, isEditorFocused: true, selectionStart: 5, selectionLength: 0);

        var u1 = span.Apply(text, "there", maxTextLength: 500);
        Assert.True(u1.Changed);
        Assert.Equal("Hello there world", u1.Text);
        Assert.Equal(11, u1.CaretIndex);

        var u2 = span.Apply(u1.Text, "there friend", maxTextLength: 500);
        Assert.True(u2.Changed);
        Assert.Equal("Hello there friend world", u2.Text);
        Assert.Equal(18, u2.CaretIndex);

        var u3 = span.Apply(u2.Text, "there dear friend", maxTextLength: 500);
        Assert.True(u3.Changed);
        Assert.Equal("Hello there dear friend world", u3.Text);
        Assert.Equal(23, u3.CaretIndex);
    }

    [Fact]
    public void UnfocusedEditorAppendsToEndWithSmartSpacing()
    {
        var span = new DictationTextSpan();
        string text = "Existing note";
        span.CaptureAnchor(text, isEditorFocused: false, selectionStart: 2, selectionLength: 4);

        var u1 = span.Apply(text, "appended words", maxTextLength: 500);
        Assert.True(u1.Changed);
        Assert.Equal("Existing note appended words", u1.Text);
        Assert.Equal(u1.Text.Length, u1.CaretIndex);
    }

    [Fact]
    public void DoesNotInsertLeadingSpaceBeforePunctuationOrWhitespace()
    {
        var span = new DictationTextSpan();
        span.CaptureAnchor("Hello", isEditorFocused: true, selectionStart: 5, selectionLength: 0);
        var punct = span.Apply("Hello", ", world!", maxTextLength: 500);
        Assert.Equal("Hello, world!", punct.Text);

        var span2 = new DictationTextSpan();
        span2.CaptureAnchor("Hello ", isEditorFocused: true, selectionStart: 6, selectionLength: 0);
        var afterSpace = span2.Apply("Hello ", "world", maxTextLength: 500);
        Assert.Equal("Hello world", afterSpace.Text);
    }

    [Fact]
    public void InsertsBetweenAdjacentWordsWithBothLeadingAndTrailingSpaces()
    {
        var span = new DictationTextSpan();
        span.CaptureAnchor("HelloWorld", isEditorFocused: true, selectionStart: 5, selectionLength: 0);

        var u1 = span.Apply("HelloWorld", "big", maxTextLength: 500);
        Assert.Equal("Hello big World", u1.Text);
        Assert.Equal(10, u1.CaretIndex);

        var u2 = span.Apply(u1.Text, "big blue", maxTextLength: 500);
        Assert.Equal("Hello big blue World", u2.Text);
        Assert.Equal(15, u2.CaretIndex);
    }

    [Fact]
    public void EmptyTranscriptPreservesActiveSelectionWhenNoPartialWasInserted()
    {
        var span = new DictationTextSpan();
        string text = "Keep this selected text";
        span.CaptureAnchor(text, isEditorFocused: true, selectionStart: 5, selectionLength: 4);

        var update = span.Apply(text, "   ", maxTextLength: 500);
        Assert.False(update.Changed);
        Assert.Equal(text, update.Text);
        Assert.Equal(9, update.CaretIndex);
    }

    [Fact]
    public void EmptyFinalTranscriptCleanlyRemovesPriorPartialSpan()
    {
        var span = new DictationTextSpan();
        string text = "Start end";
        span.CaptureAnchor(text, isEditorFocused: true, selectionStart: 5, selectionLength: 0);

        var partial = span.Apply(text, "noise", maxTextLength: 500);
        Assert.Equal("Start noise end", partial.Text);

        var cleared = span.Apply(partial.Text, "", maxTextLength: 500);
        Assert.True(cleared.Changed);
        Assert.Equal("Start end", cleared.Text);
        Assert.Equal(5, cleared.CaretIndex);
    }

    [Fact]
    public void ClampsToMaxLengthWithoutSplittingUtf16SurrogatePairs()
    {
        var span = new DictationTextSpan();
        span.CaptureAnchor("Hi", isEditorFocused: true, selectionStart: 2, selectionLength: 0);

        // "Hi" (2) + " " (1) + "😀" (2 UTF-16 code units) = 5.
        // With maxTextLength = 4, available = 2 -> " " + high surrogate would split, so must drop the surrogate pair!
        var tight = span.Apply("Hi", "\U0001F600", maxTextLength: 4);
        Assert.False(tight.Changed);
        Assert.Equal("Hi", tight.Text);

        // With maxTextLength = 6 -> "Hi" (2) + " " (1) + "a" (1) + "😀" (2) = 6 fits cleanly.
        var exact = span.Apply("Hi", "a\U0001F600\U0001F601", maxTextLength: 7);
        Assert.True(exact.Changed);
        Assert.Equal("Hi a\U0001F600", exact.Text);
        Assert.False(char.IsHighSurrogate(exact.Text[^1]));
    }

    [Fact]
    public void AdjustForExternalEditShiftsSpanCorrectly()
    {
        var span = new DictationTextSpan();
        string text = "Hello world";
        span.CaptureAnchor(text, isEditorFocused: true, selectionStart: 5, selectionLength: 0);
        var u1 = span.Apply(text, "nice", maxTextLength: 500);
        Assert.Equal("Hello nice world", u1.Text);
        Assert.Equal(5, span.InsertStart);
        Assert.Equal(10, span.InsertEnd);

        // User types "Oh " at index 0 -> "Oh Hello nice world"
        string afterPrefixEdit = "Oh " + u1.Text;
        span.AdjustForExternalEdit(changeOffset: 0, removedLength: 0, addedLength: 3, newTextLength: afterPrefixEdit.Length);
        Assert.Equal(8, span.InsertStart);
        Assert.Equal(13, span.InsertEnd);

        var u2 = span.Apply(afterPrefixEdit, "wonderful", maxTextLength: 500);
        Assert.Equal("Oh Hello wonderful world", u2.Text);
    }

    [Fact]
    public void ReplaceSelectionInsertsDictatedTextInPlace()
    {
        var span = new DictationTextSpan();
        string text = "Hello old world";
        span.CaptureAnchor(text, isEditorFocused: true, selectionStart: 6, selectionLength: 3);

        var update = span.Apply(text, "new", maxTextLength: 500);
        Assert.Equal("Hello new world", update.Text);
        Assert.Equal(9, update.CaretIndex);
    }
}
