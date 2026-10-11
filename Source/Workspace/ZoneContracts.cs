using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed record ZoneView(Guid Id, string Name, string Color, WorldRect Bounds, int MemberCount = 0);
internal sealed record ZoneMemberView(Guid Id, string Title, bool Assigned, WorldRect Bounds, string Details = "");

internal interface IZoneCommands
{
    IReadOnlyList<ZoneView> Items { get; }
    event Action? Changed;
    bool Update(Guid id, string name, string color);
    bool Remove(Guid id);
    IReadOnlyList<ZoneMemberView> Members(Guid id);
    bool SetMembership(Guid id, IReadOnlyCollection<Guid> itemIds);
}
