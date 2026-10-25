namespace CanvasDesktop;

/// <summary>Transient launch feedback; ordinary cards retain their minimal layout.</summary>
internal readonly record struct PlaceholderLaunchPresentation(string Text, bool Busy)
{
    public static PlaceholderLaunchPresentation For(WorkspaceItemView item) => item.State switch
    {
        "Opening…" => new("Starting application…\nWaiting for its window", true),
        "Queued" => new("Waiting for another window of this app to finish starting…", true),
        _ => new(item.LaunchMessage ?? "", false)
    };
}
