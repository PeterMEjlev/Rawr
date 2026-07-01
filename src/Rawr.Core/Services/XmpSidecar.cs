using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Lightroom-compatible XMP sidecar I/O. One ".xmp" file is written next to
/// each photo carrying the metadata Lightroom honours on import: rating,
/// color label, and keywords. Pick/reject — which Lightroom keeps in its
/// catalog and never reads from XMP — ride along as the keywords
/// "RAWR:Pick" / "RAWR:Reject"; a reject also coerces xmp:Rating to -1, the
/// Adobe-standard signal Lightroom interprets as the Reject flag.
/// </summary>
public static class XmpSidecar
{
    public const string PickKeyword   = "RAWR:Pick";
    public const string RejectKeyword = "RAWR:Reject";

    private static readonly XNamespace X   = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Dc  = "http://purl.org/dc/elements/1.1/";

    public static string SidecarPathFor(string photoPath) =>
        Path.ChangeExtension(photoPath, ".xmp");

    public static XmpData Snapshot(PhotoItem photo, IReadOnlyDictionary<int, string> tagNames)
    {
        var keywords = new List<string>();
        foreach (var id in photo.TagIds)
            if (tagNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                keywords.Add(name);
        if (photo.Flag == CullFlag.Pick)   keywords.Add(PickKeyword);
        if (photo.Flag == CullFlag.Reject) keywords.Add(RejectKeyword);

        // Adobe convention: -1 means "rejected". A reject overrides any star
        // rating in XMP because Lightroom won't honour the (catalog-only) flag
        // on import — only the rating field. Star rating is preserved on the
        // RAWR side in SQLite, so this isn't lossy locally.
        int? rating = photo.Flag == CullFlag.Reject ? -1
                    : photo.Rating > 0              ? photo.Rating
                    :                                 (int?)null;

        string? label = photo.ColorLabel switch
        {
            ColorLabel.Red    => "Red",
            ColorLabel.Yellow => "Yellow",
            ColorLabel.Green  => "Green",
            ColorLabel.Blue   => "Blue",
            ColorLabel.Purple => "Purple",
            _                 => null,
        };

        return new XmpData(rating, label, keywords);
    }

    public static void Write(string photoPath, XmpData data)
    {
        var sidecarPath = SidecarPathFor(photoPath);
        var description = new XElement(Rdf + "Description",
            new XAttribute(Rdf + "about", ""),
            new XAttribute(XNamespace.Xmlns + "xmp", Xmp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc",  Dc.NamespaceName));

        if (data.Rating.HasValue)
            description.Add(new XAttribute(Xmp + "Rating",
                data.Rating.Value.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(data.Label))
            description.Add(new XAttribute(Xmp + "Label", data.Label));
        description.Add(new XAttribute(Xmp + "MetadataDate",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

        if (data.Keywords.Count > 0)
        {
            var bag = new XElement(Rdf + "Bag");
            foreach (var k in data.Keywords)
                bag.Add(new XElement(Rdf + "li", k));
            description.Add(new XElement(Dc + "subject", bag));
        }

        var root = new XElement(X + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute(X + "xmptk", "RAWR"),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                description));

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
        };

        var tmp = sidecarPath + ".tmp";
        using (var w = XmlWriter.Create(tmp, settings))
        {
            w.WriteProcessingInstruction("xpacket", "begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"");
            root.WriteTo(w);
            w.WriteProcessingInstruction("xpacket", "end=\"w\"");
        }
        try { File.Move(tmp, sidecarPath, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static XmpData? TryRead(string photoPath)
    {
        var sidecarPath = SidecarPathFor(photoPath);
        if (!File.Exists(sidecarPath)) return null;
        try
        {
            var doc = XDocument.Load(sidecarPath);
            var description = doc.Descendants(Rdf + "Description").FirstOrDefault();
            if (description == null) return null;

            // Both attribute and element forms are valid RDF; Lightroom emits a
            // mix. Read both for either.
            int? rating = null;
            var ratingText = description.Attribute(Xmp + "Rating")?.Value
                          ?? description.Element(Xmp + "Rating")?.Value;
            if (ratingText != null && int.TryParse(ratingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                rating = r;

            var label = description.Attribute(Xmp + "Label")?.Value
                     ?? description.Element(Xmp + "Label")?.Value;
            if (string.IsNullOrWhiteSpace(label)) label = null;

            var keywords = new List<string>();
            var subject = description.Element(Dc + "subject");
            if (subject != null)
            {
                foreach (var li in subject.Descendants(Rdf + "li"))
                {
                    var v = li.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) keywords.Add(v);
                }
            }
            return new XmpData(rating, label, keywords);
        }
        catch { return null; }
    }
}

public sealed record XmpData(int? Rating, string? Label, List<string> Keywords)
{
    /// <summary>
    /// True when the photo carries no cull metadata worth persisting — no rating,
    /// no color label, and no keywords (which also covers pick/reject, since those
    /// ride along as keywords). Used to avoid littering a folder with empty
    /// sidecars for untouched photos.
    /// </summary>
    public bool IsEmpty => !Rating.HasValue && string.IsNullOrEmpty(Label) && Keywords.Count == 0;
}
