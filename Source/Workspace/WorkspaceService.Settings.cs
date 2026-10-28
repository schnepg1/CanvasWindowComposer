using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

// Persisted per-place settings, including Chrome profile selection.
internal sealed partial class WorkspaceService
{
    private bool IsAutomaticMinimizationExcluded(AutomaticParkingToken token)
    {
        var live = _bindings.At(token.Identity.Handle);
        return live?.Identity == token.Identity && live.ItemId is { } id
            && Document.Items.Any(item => item.Id == id && item.ExcludeFromAutomaticMinimization);
    }

    public void SetAutomaticMinimizationExcluded(Guid id, bool value)
    {
        if (Find(id) is not { } item || item.ExcludeFromAutomaticMinimization == value) return;
        item.ExcludeFromAutomaticMinimization = value;
        Save();
        _wm.RefreshAutomaticParking();
        Changed?.Invoke();
    }

    public void SetAutoOpen(Guid id, bool value)
    {
        if (Find(id) is not { } item) return;
        if (value) item.Placement = WorkspacePlacement.Deliberate;
        if (value) foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea)) zone.ItemIds.Remove(id);
        item.OpenOnStartup = value; Save(); Changed?.Invoke();
    }

    public void SetName(Guid id, string name)
    {
        if (Find(id) is not { } item || name == null || name.Length > 200) return;
        item.Name = name.Trim(); EnsureCards(); Save(); Changed?.Invoke();
    }

    public bool SetChromeProfile(Guid id, string profilePath)
    {
        if (Find(id) is not { } item || item.WebResource != null || !ChromeProfiles.Supports(item.ExecutablePath) || item.IsFolder
            || !ChromeProfiles.ValidPath(profilePath) || _launching.ContainsKey(id) || _queued.Contains(id)) return false;
        if (profilePath.Length > 0) profilePath = Path.GetFullPath(profilePath);
        if (string.Equals(item.ChromeProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) && !item.CaptureChromeProfile) return Save();
        var live = BoundWindow(id);
        string? currentProfile = live == null ? null : _windowInspector.ReadChromeProfile(live.Identity.Handle, item.ExecutablePath, profilePath);
        string previousProfile = item.ChromeProfilePath;
        bool previousCapture = item.CaptureChromeProfile;
        item.ChromeProfilePath = profilePath;
        item.CaptureChromeProfile = false;
        if (!Save())
        {
            item.ChromeProfilePath = previousProfile; item.CaptureChromeProfile = previousCapture;
            Changed?.Invoke();
            return false;
        }
        // Changing the launch profile must not close or relabel an existing window
        // of another profile. Keep it on the canvas and make this slot reopenable.
        // Detach only after persistence succeeds, so cancelling a failed save
        // cannot leave a hidden in-memory edit or lose the current binding.
        bool sameProfile = string.Equals(currentProfile ?? previousProfile, profilePath, StringComparison.OrdinalIgnoreCase);
        if (profilePath.Length > 0 && live != null && !sameProfile)
        { live.Forgotten = true; _bindings.Unbind(live); }
        EnsureCards(); Changed?.Invoke();
        return true;
    }

    public void SetTarget(Guid id, string selected, bool folder)
    {
        if (Find(id) is not { } item || item.WebResource != null || string.IsNullOrWhiteSpace(selected)) return;
        item.Target = selected;
        item.AutoDetectedTarget = false;
        // A project folder for an editor is an argument to that editor; a folder
        // for Explorer is opened through Explorer's folder-launch path.
        item.IsFolder = folder && string.Equals(Path.GetFileName(item.ExecutablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        Save(); Changed?.Invoke();
    }
}
