using System;
using System.Collections.Generic;
using Xunit;

namespace CanvasDesktop.Tests;

public class CanvasLayerCoordinatorTests
{
    [Fact]
    public void Register_And_RaiseAll_ExecutesInAscendingTierSequence()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.ModalDialogs, "ModalDialog", () => executionLog.Add("ModalDialog"));
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "Dock", () => executionLog.Add("Dock"));
        coordinator.Register(CanvasLayerTier.CanvasSurface, "Surface", () => executionLog.Add("Surface"));
        coordinator.Register(CanvasLayerTier.PeekPreview, "Peek", () => executionLog.Add("Peek"));
        coordinator.Register(CanvasLayerTier.Zones, "Zones", () => executionLog.Add("Zones"));
        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Backpack", () => executionLog.Add("Backpack"));
        coordinator.Register(CanvasLayerTier.ContextPopups, "Popups", () => executionLog.Add("Popups"));

        coordinator.RaiseAll();

        var expectedOrder = new[]
        {
            "Surface",
            "Zones",
            "Backpack",
            "Dock",
            "Peek",
            "Popups",
            "ModalDialog"
        };

        Assert.Equal(expectedOrder, executionLog);
    }

    [Fact]
    public void RaiseAll_PreservesInsertionOrderForSameTier()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Editor", () => executionLog.Add("Editor"));
        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Backpack", () => executionLog.Add("Backpack"));
        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Drawer", () => executionLog.Add("Drawer"));

        coordinator.RaiseAll();

        Assert.Equal(new[] { "Editor", "Backpack", "Drawer" }, executionLog);
    }

    [Fact]
    public void Register_ReplacesExistingLayer()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "Panel", () => executionLog.Add("Old"));
        Assert.True(coordinator.IsRegistered("Panel"));
        Assert.Equal(1, coordinator.Count);

        coordinator.Register(CanvasLayerTier.PeekPreview, "Panel", () => executionLog.Add("New"));
        Assert.Equal(1, coordinator.Count);

        coordinator.RaiseAll();

        Assert.Single(executionLog);
        Assert.Equal("New", executionLog[0]);
    }

    [Fact]
    public void Unregister_RemovesLayer()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "PanelA", () => executionLog.Add("A"));
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "PanelB", () => executionLog.Add("B"));

        coordinator.Unregister("PanelA");
        Assert.False(coordinator.IsRegistered("PanelA"));
        Assert.True(coordinator.IsRegistered("PanelB"));
        Assert.Equal(1, coordinator.Count);

        coordinator.RaiseAll();

        Assert.Single(executionLog);
        Assert.Equal("B", executionLog[0]);
    }

    [Fact]
    public void RaiseAbove_OnlyInvokesHigherTiers()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "Zones", () => executionLog.Add("Zones"));
        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Backpack", () => executionLog.Add("Backpack"));
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "Dock", () => executionLog.Add("Dock"));
        coordinator.Register(CanvasLayerTier.PeekPreview, "Peek", () => executionLog.Add("Peek"));
        coordinator.Register(CanvasLayerTier.ContextPopups, "Popup", () => executionLog.Add("Popup"));

        coordinator.RaiseAbove(CanvasLayerTier.DockAndToolbars);

        // Only tiers strictly above DockAndToolbars (30) should be raised, in ascending order: Peek (40), Popup (50)
        Assert.Equal(new[] { "Peek", "Popup" }, executionLog);
    }

    [Fact]
    public void RaiseAbove_WithHighestTier_InvokesNothing()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.ModalDialogs, "Modal", () => executionLog.Add("Modal"));

        coordinator.RaiseAbove(CanvasLayerTier.ModalDialogs);

        Assert.Empty(executionLog);
    }

    [Fact]
    public void IsAlive_False_SkipsInvocationWithoutThrowing()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "ActiveA", () => executionLog.Add("ActiveA"), isAlive: null);
        coordinator.Register(CanvasLayerTier.BackpackAndDrawers, "DeadB", () => executionLog.Add("DeadB"), isAlive: () => false);
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "ActiveC", () => executionLog.Add("ActiveC"), isAlive: () => true);

        coordinator.RaiseAll();

        Assert.Equal(new[] { "ActiveA", "ActiveC" }, executionLog);

        executionLog.Clear();
        coordinator.RaiseAbove(CanvasLayerTier.Zones);

        Assert.Single(executionLog);
        Assert.Equal("ActiveC", executionLog[0]);
    }

    [Fact]
    public void IsAlive_ThrowingObjectDisposedException_TreatedAsDeadWithoutThrowing()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "DisposedPanel",
            () => executionLog.Add("Disposed"),
            isAlive: () => throw new ObjectDisposedException("TestWindow"));
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "HealthyPanel",
            () => executionLog.Add("Healthy"),
            isAlive: () => true);

        coordinator.RaiseAll();

        Assert.Single(executionLog);
        Assert.Equal("Healthy", executionLog[0]);
    }

    [Fact]
    public void RaiseWithoutActivation_ThrowingObjectDisposedException_DoesNotAbortRemainingLayers()
    {
        var coordinator = new CanvasLayerCoordinator();
        var executionLog = new List<string>();

        coordinator.Register(CanvasLayerTier.Zones, "DisposedAction",
            () => throw new ObjectDisposedException("TestWindow"));
        coordinator.Register(CanvasLayerTier.DockAndToolbars, "NextPanel",
            () => executionLog.Add("Next"));

        coordinator.RaiseAll();

        Assert.Single(executionLog);
        Assert.Equal("Next", executionLog[0]);
    }

    [Fact]
    public void ArgumentValidation_ThrowsOnInvalidArguments()
    {
        var coordinator = new CanvasLayerCoordinator();

        Assert.Throws<ArgumentException>(() => coordinator.Register(CanvasLayerTier.Zones, "", () => { }));
        Assert.Throws<ArgumentException>(() => coordinator.Register(CanvasLayerTier.Zones, "   ", () => { }));
        Assert.Throws<ArgumentNullException>(() => coordinator.Register(CanvasLayerTier.Zones, "Valid", null!));

        // Unregister with invalid names should not throw
        coordinator.Unregister("");
        coordinator.Unregister("NonExistent");
    }
}
