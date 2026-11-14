using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasDesktop.CanvasView;

public sealed class AnnotationClipboardPackage
{
    public int Version { get; set; } = 1;
    public CanvasAnnotation[] Items { get; set; } = [];
}

public static class AnnotationClipboard
{
    public const string DataFormat = "CanvasDesktop.Annotations.v1";
    public const double DefaultPasteOffset = 32.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(IEnumerable<CanvasAnnotation> items)
    {
        var list = items.Where(a => a is not null && string.IsNullOrEmpty(a.AgentTaskId)).ToArray();
        var package = new AnnotationClipboardPackage
        {
            Version = 1,
            Items = list
        };
        return JsonSerializer.Serialize(package, JsonOptions);
    }

    public static bool TryDeserialize(string? json, out IReadOnlyList<CanvasAnnotation> items)
    {
        items = [];
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var package = JsonSerializer.Deserialize<AnnotationClipboardPackage>(json, JsonOptions);
            if (package is null || package.Items is null || package.Items.Length == 0) return false;
            var list = package.Items.Where(a => a is not null && string.IsNullOrEmpty(a.AgentTaskId)).ToArray();
            if (list.Length == 0) return false;
            items = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<CanvasAnnotation> PrepareForPaste(
        IReadOnlyList<CanvasAnnotation> sourceItems,
        Guid targetDesktop,
        double offsetX,
        double offsetY)
    {
        var validSources = sourceItems.Where(a => a is not null && string.IsNullOrEmpty(a.AgentTaskId)).ToList();
        if (validSources.Count == 0) return [];

        var idMap = new Dictionary<Guid, Guid>();
        foreach (var item in validSources)
        {
            idMap[item.Id] = Guid.NewGuid();
        }

        var result = new List<CanvasAnnotation>(validSources.Count);
        foreach (var item in validSources)
        {
            double newAx = Math.Clamp(item.A.X + offsetX, -10000000.0, 10000000.0);
            double newAy = Math.Clamp(item.A.Y + offsetY, -10000000.0, 10000000.0);
            double newBx = Math.Clamp(item.B.X + offsetX, -10000000.0, 10000000.0);
            double newBy = Math.Clamp(item.B.Y + offsetY, -10000000.0, 10000000.0);

            // Re-anchor connectors if both endpoints exist in the copied group; otherwise detach
            Guid? sourceId = null;
            ConnectionPort sourcePort = ConnectionPort.Auto;
            if (item.SourceId is { } sid && idMap.TryGetValue(sid, out var newSid))
            {
                sourceId = newSid;
                sourcePort = item.SourcePort;
            }

            Guid? targetId = null;
            ConnectionPort targetPort = ConnectionPort.Auto;
            if (item.TargetId is { } tid && idMap.TryGetValue(tid, out var newTid))
            {
                targetId = newTid;
                targetPort = item.TargetPort;
            }

            var newItem = item with
            {
                Id = idMap[item.Id],
                DesktopId = targetDesktop,
                A = new CanvasPoint(newAx, newAy),
                B = new CanvasPoint(newBx, newBy),
                SourceId = sourceId,
                SourcePort = sourcePort,
                TargetId = targetId,
                TargetPort = targetPort
            };

            result.Add(newItem);
        }

        return result;
    }

    public static CanvasAnnotation CreateFromPlainText(
        string text,
        Guid desktopId,
        CanvasPoint position,
        string color = "#FBBF24",
        double stroke = 3,
        double fontSize = AnnotationDocument.DefaultFontSize,
        AnnotationTextAlign textAlign = AnnotationTextAlign.Left,
        AnnotationTextEmphasis emphasis = AnnotationTextEmphasis.Normal,
        AnnotationCardFill cardFill = AnnotationCardFill.Solid)
    {
        if (text is null) text = "";
        if (text.Length > AnnotationDocument.MaximumTextLength)
            text = text[..AnnotationDocument.MaximumTextLength];
        if (string.IsNullOrWhiteSpace(text))
            text = "Note";

        double ax = Math.Clamp(position.X, -10000000.0, 10000000.0);
        double ay = Math.Clamp(position.Y, -10000000.0, 10000000.0);
        double bx = Math.Clamp(ax + AnnotationCanvasTool.DefaultTextCardWidth, -10000000.0, 10000000.0);
        double by = Math.Clamp(ay + AnnotationCanvasTool.DefaultTextCardHeight, -10000000.0, 10000000.0);

        return new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: desktopId,
            Kind: AnnotationKind.Text,
            A: new CanvasPoint(ax, ay),
            B: new CanvasPoint(bx, by),
            Color: color,
            Stroke: stroke,
            Text: text,
            FontSize: fontSize,
            TextAlign: textAlign,
            Emphasis: emphasis,
            CardFill: cardFill);
    }
}
