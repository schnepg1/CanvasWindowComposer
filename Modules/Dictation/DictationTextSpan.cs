namespace CanvasDesktop.Dictation;

/// <summary>Result of applying a dictation transcript update to the active text span.</summary>
public readonly record struct DictationTextUpdate(bool Changed, string Text, int CaretIndex);

/// <summary>
/// Pure text-span engine that tracks an anchored [InsertStart, InsertEnd] region and replaces
/// only that region on each streaming partial/final update without concatenating updates.
/// </summary>
public sealed class DictationTextSpan
{
    public int InsertStart { get; private set; }
    public int InsertEnd { get; private set; }
    public bool HasAppliedTranscript { get; private set; }
    public bool IsActive { get; private set; }

    public void CaptureAnchor(string currentText, bool isEditorFocused, int selectionStart, int selectionLength)
    {
        currentText ??= string.Empty;
        if (isEditorFocused)
        {
            int clampedStart = Math.Clamp(selectionStart, 0, currentText.Length);
            int clampedLength = Math.Clamp(selectionLength, 0, currentText.Length - clampedStart);
            InsertStart = clampedStart;
            InsertEnd = clampedStart + clampedLength;
        }
        else
        {
            InsertStart = currentText.Length;
            InsertEnd = currentText.Length;
        }

        HasAppliedTranscript = false;
        IsActive = true;
    }

    public void Reset()
    {
        InsertStart = 0;
        InsertEnd = 0;
        HasAppliedTranscript = false;
        IsActive = false;
    }

    public DictationTextUpdate Apply(string currentText, string? rawTranscript, int maxTextLength)
    {
        currentText ??= string.Empty;
        int safeMaxLength = Math.Max(0, maxTextLength);

        int start = Math.Clamp(InsertStart, 0, currentText.Length);
        int end = Math.Clamp(InsertEnd, start, currentText.Length);
        InsertStart = start;
        InsertEnd = end;

        string trimmed = rawTranscript?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return ApplyEmpty(currentText, start, end);

        string prefix = currentText[..start];
        string suffix = currentText[end..];

        bool needsLeadingSpace = prefix.Length > 0
            && !char.IsWhiteSpace(prefix[^1])
            && !IsAttachedLeadingPunctuation(trimmed[0]);

        bool needsTrailingSpace = suffix.Length > 0
            && !char.IsWhiteSpace(suffix[0])
            && !IsAttachedTrailingPunctuation(suffix[0]);

        string candidate = (needsLeadingSpace ? " " : string.Empty)
            + trimmed
            + (needsTrailingSpace ? " " : string.Empty);

        int available = Math.Max(0, safeMaxLength - prefix.Length - suffix.Length);
        string inserted = TruncatePreservingSurrogates(candidate, available);

        // Avoid leaving behind only a separating space when the length budget cannot fit any word character.
        if (string.IsNullOrWhiteSpace(inserted))
            return ApplyEmpty(currentText, start, end);

        string updatedText = string.Concat(prefix, inserted, suffix);
        InsertEnd = start + inserted.Length;
        HasAppliedTranscript = true;

        bool changed = !string.Equals(updatedText, currentText, StringComparison.Ordinal);
        return new DictationTextUpdate(changed, updatedText, InsertEnd);
    }

    public void AdjustForExternalEdit(int changeOffset, int removedLength, int addedLength, int newTextLength)
    {
        if (!IsActive)
            return;

        int editStart = Math.Max(0, changeOffset);
        int removed = Math.Max(0, removedLength);
        int added = Math.Max(0, addedLength);
        int editOldEnd = editStart + removed;
        int delta = added - removed;

        if (editOldEnd <= InsertStart)
        {
            InsertStart += delta;
            InsertEnd += delta;
        }
        else if (editStart >= InsertEnd)
        {
            // Edit occurred entirely after the active dictation span.
        }
        else if (editStart >= InsertStart && editOldEnd <= InsertEnd)
        {
            InsertEnd += delta;
        }
        else if (editStart < InsertStart && editOldEnd > InsertEnd)
        {
            // Edit replaced the entire active span.
            InsertStart = editStart + added;
            InsertEnd = InsertStart;
            HasAppliedTranscript = false;
        }
        else if (editStart < InsertStart)
        {
            InsertStart = editStart + added;
            InsertEnd = Math.Max(InsertStart, InsertEnd + delta);
        }
        else
        {
            InsertEnd = Math.Max(InsertStart, editStart);
        }

        int maxLength = Math.Max(0, newTextLength);
        InsertStart = Math.Clamp(InsertStart, 0, maxLength);
        InsertEnd = Math.Clamp(InsertEnd, InsertStart, maxLength);
    }

    private DictationTextUpdate ApplyEmpty(string currentText, int start, int end)
    {
        if (!HasAppliedTranscript)
        {
            // Preserve existing text and any active selection without inserting stray spaces.
            return new DictationTextUpdate(Changed: false, Text: currentText, CaretIndex: end);
        }

        string updatedText = string.Concat(currentText[..start], currentText[end..]);
        InsertEnd = start;
        HasAppliedTranscript = false;
        bool changed = !string.Equals(updatedText, currentText, StringComparison.Ordinal);
        return new DictationTextUpdate(changed, updatedText, start);
    }

    private static string TruncatePreservingSurrogates(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length == 0)
            return string.Empty;

        if (value.Length <= maxLength)
            return value;

        int length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;

        return length <= 0 ? string.Empty : value[..length];
    }

    private static bool IsAttachedLeadingPunctuation(char c)
        => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…';

    private static bool IsAttachedTrailingPunctuation(char c)
        => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…';
}
