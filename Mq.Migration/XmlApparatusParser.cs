﻿using Cadmus.Core.Layers;
using Cadmus.Parts.Layers;
using Cadmus.Philology.Parts.Layers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mq.Migration
{
    /// <summary>
    /// XML apparatus document parser. This parses an <c>-app</c> TEI document,
    /// using the JSON dump files representing the base text as reference
    /// for calculating the location.
    /// </summary>
    public sealed class XmlApparatusParser : IHasLogger
    {
        private const char NOTE_SECT_SEP = '`';
        private const string TYPE_ANCIENT_NOTE = "ancient-note";
        private const string TYPE_MARGIN_NOTE = "margin-note";

        private readonly Regex _bracesRegex;
        private JsonTextIndex _textIndex;
        private string _userId;
        private int _groupNr;

        /// <summary>
        /// Gets or sets the user identifier to be assigned to data being
        /// imported. The default value is <c>zeus</c>.
        /// </summary>
        public string UserId
        {
            get { return _userId; }
            set
            {
                _userId = value
                    ?? throw new ArgumentNullException(nameof(value));
            }
        }

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        public ILogger Logger { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlApparatusParser"/>
        /// class.
        /// </summary>
        public XmlApparatusParser()
        {
            _userId = "zeus";
            _bracesRegex = new Regex(@"\{([^}]+)\}");
        }

        private Tuple<string, string> ParseFromTo(XElement appElem)
        {
            string from = appElem.Attribute("from").Value.Substring(1);
            string to = appElem.Attribute("to").Value.Substring(1);

            JsonTextIndexPayload a = _textIndex.Find(from);
            if (a == null)
            {
                Logger?.LogError("Word ID {WordId} not found", from);
                return null;
            }
            // range
            if (from != to)
            {
                JsonTextIndexPayload b = _textIndex.Find(to);
                if (b == null)
                {
                    Logger?.LogError("Word ID {WordId} not found", to);
                    return null;
                }
                if (b.ItemId != a.ItemId)
                {
                    Logger?.LogError("Fragment spans two items: {FromLoc} {ToLoc}",
                        a, b);
                    return null;
                }
                return Tuple.Create(a.ItemId, $"{a.Y}.{a.X}-{b.Y}.{b.X}");
            }
            // point
            return Tuple.Create(a.ItemId, $"{a.Y}.{a.X}");
        }

        private Tuple<string, string[]> ParseLoc(string loc)
        {
            List<string> locs = new List<string>();
            string itemId = null;

            foreach (string token in loc.Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                JsonTextIndexPayload a = _textIndex.Find(token);
                if (a == null)
                {
                    Logger?.LogError("Word ID {WordId} not found", token);
                    return null;
                }
                if (itemId == null)
                {
                    itemId = a.ItemId;
                }
                else if (a.ItemId != itemId)
                {
                    Logger?.LogError("Fragment spans two items: {Loc}", loc);
                    return null;
                }
                locs.Add($"{a.Y}.{a.X}");
            }
            return Tuple.Create(itemId, locs.ToArray());
        }

        private void ParseWit(string wit, ApparatusEntry entry)
        {
            if (string.IsNullOrEmpty(wit)) return;

            foreach (string token in wit.Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                entry.Witnesses.Add(new ApparatusAnnotatedValue
                {
                    Value = token.Substring(1)
                });
            }
        }

        private void ParseSource(string source, ApparatusEntry entry)
        {
            if (string.IsNullOrEmpty(source)) return;

            foreach (string token in source.Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                entry.Authors.Add(new ApparatusAnnotatedValue
                {
                    Value = token.Substring(1)
                });
            }
        }

        private XmlApparatusVarContent ParseVariantContent(XElement variant)
        {
            XmlApparatusVarContent content = new XmlApparatusVarContent
            {
                Logger = Logger
            };

            // scan children elements
            foreach (XElement child in variant.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "ident":
                        content.AddIdent(child);
                        break;
                    case "add":
                    case "note":
                        content.AddAnnotation(child);
                        break;
                    default:
                        Logger?.LogError("Unexpected element in variant content:" +
                            " {ElementName}", child.Name);
                        break;
                }
            }

            // get direct text
            StringBuilder sb = new StringBuilder();
            foreach (XText txt in variant.Nodes().OfType<XText>())
            {
                sb.Append(txt.Value);
            }
            if (sb.Length > 0) content.Value = sb.ToString();

            return content;
        }

        private void AddNoteToWitOrSource(IList<XmlApparatusNote> notes,
            ApparatusAnnotatedValue target)
        {
            int distCount = notes.Select(n => n.SectionId).Distinct().Count();
            if (distCount != notes.Count)
            {
                Logger?.LogError(
                    $"Duplicate section(s) in notes targeting {target.Value}");
            }

            StringBuilder sb = new StringBuilder();
            int sectionId = 1;
            foreach (XmlApparatusNote note in notes.OrderBy(n => n.SectionId))
            {
                while (sectionId < note.SectionId)
                {
                    sb.Append(NOTE_SECT_SEP);
                    sectionId++;
                }
                sb.Append(note.Value);
            }
            target.Note = ApplyMarkdown(sb.ToString());
        }

        private string ApplyMarkdown(string text)
        {
            Stack<string> stack = new Stack<string>();

            return _bracesRegex.Replace(text, (Match m) =>
            {
                switch (m.Groups[1].Value)
                {
                    case "/f":
                        return stack.Pop();
                    case "f=i":
                        stack.Push("_");
                        return "_";
                    case "f=b":
                        stack.Push("__");
                        return "__";
                    case "f=u":
                        stack.Push("</sup>");
                        return "<sup>";
                    case "f=d":
                        stack.Push("</sub>");
                        return "<sub>";
                    default:
                        return m.Value;
                }
            });
        }

        private void AddContentToEntry(XmlApparatusVarContent content,
            ApparatusEntry entry)
        {
            // value
            if (!string.IsNullOrWhiteSpace(content.Value))
                entry.Value = content.Value.Trim();

            // ident's
            if (content.Idents.Count > 0)
                entry.NormValue = string.Join(" ", content.Idents);

            // notes
            if (content.Notes.Count > 0)
            {
                // corner case: only note section 1
                if (content.Notes.Count == 1
                    && content.Notes[0].SectionId == 1
                    && content.Notes[0].Target == null)
                {
                    entry.Note = ApplyMarkdown(content.Notes[0].Value);
                    return;
                }

                // first process wit/source notes, grouped by target
                foreach (var group in from n in content.Notes
                         where n.Target != null
                         group n by n.Target into g
                         select g)
                {
                    ApparatusAnnotatedValue target =
                        entry.Witnesses.Find(w => w.Value == group.Key);
                    if (target != null)
                    {
                        AddNoteToWitOrSource(group.ToList(), target);
                        continue;
                    }
                    target = entry.Authors.Find(a => a.Value == group.Key);
                    if (target != null)
                    {
                        AddNoteToWitOrSource(group.ToList(), target);
                    }
                    else
                    {
                        Logger?.LogError($"Target {group.Key} not found");
                    }
                }

                // then process untargeted notes
                StringBuilder sb = new StringBuilder();
                int curSect = 1;
                HashSet<int> sections = new HashSet<int>();

                foreach (XmlApparatusNote note in content.Notes
                    .Where(n => n.Target == null)
                    .OrderBy(n => n.SectionId))
                {
                    if (sections.Contains(note.SectionId))
                    {
                        Logger?.LogError(
                            $"Note section {note.SectionId} overwritten by \"{note.Value}\"");
                    }
                    while (curSect < note.SectionId)
                    {
                        sb.Append(NOTE_SECT_SEP);
                        curSect++;
                    }
                    sb.Append(note.Value);
                    sections.Add(note.SectionId);
                }

                if (sb.Length > 0) entry.Note = ApplyMarkdown(sb.ToString());
            }
        }

        private string BuildGroupId(List<ApparatusEntry> entries)
        {
            string groupId = entries.Find(e => e.IsAccepted)?.Value
                ?? entries.Find(e => e.Value != null)?.Value ?? "g";

            // filter and append ordinal number to ensure each group is unique
            StringBuilder sb = new StringBuilder();
            foreach (char c in groupId)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            sb.Append('-').Append(++_groupNr);

            return sb.ToString();
        }

        private void AddFragmentToPart(ApparatusLayerFragment fr,
            TiledTextLayerPart<ApparatusLayerFragment> part,
            string originalLoc)
        {
            int count = part.Fragments.Count;

            // bypass part's add so that even overlapping fragments are added;
            // we will then move them all at once later, when splitting part
            TokenTextLocation loc = TokenTextLocation.Parse(fr.Location);
            if (part.Fragments.Any(f => TokenTextLocation.Parse(f.Location)
                .Overlaps(loc)))
            {
                Logger?.LogError("Overlap for new fragment at {Location}"
                    + $" (original {originalLoc}): "
                    + string.Join("; ", fr.Entries), fr.Location);
            }
            part.Fragments.Add(fr);
        }

        private TiledTextLayerPart<ApparatusLayerFragment> CreatePart(string docId)
        {
            return new TiledTextLayerPart<ApparatusLayerFragment>
            {
                ThesaurusScope = docId,
                CreatorId = _userId,
                UserId = _userId
            };
        }

        /// <summary>
        /// Splits the received apparatus layer part into 1 to 3 parts,
        /// distributing them as follows: 1) all the original entries except
        /// those under 2 and 3; 2) all the entries marked as ancient notes;
        /// 3) all the fragments marked as margin notes.
        /// </summary>
        /// <param name="part">The part.</param>
        /// <returns>The parts: just 1 if no splitting required.</returns>
        private IList<TiledTextLayerPart<ApparatusLayerFragment>> SplitPart(
            TiledTextLayerPart<ApparatusLayerFragment> part)
        {
            TiledTextLayerPart<ApparatusLayerFragment> ancPart =
                new TiledTextLayerPart<ApparatusLayerFragment>
                {
                    ThesaurusScope = part.ThesaurusScope,
                    CreatorId = part.CreatorId,
                    UserId = part.UserId,
                    RoleId = "ancient"
                };
            TiledTextLayerPart<ApparatusLayerFragment> margPart =
                new TiledTextLayerPart<ApparatusLayerFragment>
                {
                    ThesaurusScope = part.ThesaurusScope,
                    CreatorId = part.CreatorId,
                    UserId = part.UserId,
                    RoleId = "margin"
                };

            int ancEntryCount = 0, margEntryCount = 0;

            foreach (var fr in part.Fragments
                .Where(f => f.Tag.Contains(TYPE_MARGIN_NOTE)
                       || f.Entries.Any(e => e.Tag == TYPE_ANCIENT_NOTE))
                .ToList())
            {
                ApparatusLayerFragment targetFr = null;

                // app@type=margin -> tag with margin: move whole fragment
                if (fr.Tag.Contains(TYPE_MARGIN_NOTE))
                {
                    margPart.AddFragment(fr);
                    margEntryCount += fr.Entries.Count;
                    part.Fragments.Remove(fr);
                    continue;
                }

                // else examine parts, some might belong to ancient
                var ancEntries = fr.Entries
                    .Where(e => e.Tag == TYPE_ANCIENT_NOTE)
                    .ToList();
                ancEntryCount += ancEntries.Count;

                // if all the entries are to be moved, move the whole fragment
                if (ancEntries.Count == fr.Entries.Count)
                {
                    ancPart.AddFragment(fr);
                    part.Fragments.Remove(fr);
                    continue;
                }

                // else move only the ancient entries to a cloned fragment
                if (targetFr == null)
                {
                    targetFr = new ApparatusLayerFragment
                    {
                        Location = fr.Location,
                        Tag = fr.Tag
                    };
                }
                foreach (var entry in ancEntries)
                {
                    targetFr.Entries.Add(entry);
                    fr.Entries.Remove(entry);
                }
            }

            var parts = new List<TiledTextLayerPart<ApparatusLayerFragment>>
            {
                part
            };
            // check for overlaps after splitting
            if (PartHasOverlaps(part)) Logger?.LogError("Part has overlaps");

            if (ancPart.Fragments.Count > 0)
            {
                parts.Add(ancPart);
                Logger?.LogInformation($"Ancient part with {ancEntryCount} entries");
                if (PartHasOverlaps(ancPart)) Logger?.LogError("Ancient part has overlaps");
            }
            if (margPart.Fragments.Count > 0)
            {
                parts.Add(margPart);
                Logger?.LogInformation($"Margin part with {margEntryCount} entries");
                if (PartHasOverlaps(margPart)) Logger?.LogError("Margin part has overlaps");
            }

            return parts;
        }

        private static bool PartHasOverlaps(
            TiledTextLayerPart<ApparatusLayerFragment> part)
        {
            List<TokenTextLocation> locs = (from fr in part.Fragments
                select TokenTextLocation.Parse(fr.Location))
                .ToList();

            for (int i = 0; i < locs.Count; i++)
            {
                for (int j = i + 1; j < locs.Count; j++)
                {
                    if (locs[i].Overlaps(locs[j])) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Parses the specified document.
        /// </summary>
        /// <param name="doc">The document.</param>
        /// <param name="id">The document identifier.</param>
        /// <param name="textIndex">The index of the corresponding text.</param>
        /// <returns>Apparatus layer parts.</returns>
        /// <exception cref="ArgumentNullException">doc or id or textIndex
        /// </exception>
        public IEnumerable<TiledTextLayerPart<ApparatusLayerFragment>> Parse(
            XDocument doc, string id, JsonTextIndex textIndex)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (id == null) throw new ArgumentNullException(nameof(id));
            _textIndex = textIndex ??
                throw new ArgumentNullException(nameof(textIndex));

            XElement bodyElem = doc.Root
                .Element(XmlHelper.TEI + "text")
                .Element(XmlHelper.TEI + "body");
            var part = CreatePart(id);
            string divId;

            foreach (XElement divElem in bodyElem.Elements(XmlHelper.TEI + "div1"))
            {
                divId = divElem.Attribute(XmlHelper.XML + "id").Value;
                Logger?.LogInformation($"==Parsing div1 #{divId}" +
                    " at line " + ((IXmlLineInfo)divElem).LineNumber);
                int appNr = 0;

                foreach (XElement appElem in divElem.Elements(XmlHelper.TEI + "app"))
                {
                    Logger?.LogInformation($"--Parsing app #{++appNr} at line " +
                        ((IXmlLineInfo)appElem).LineNumber);

                    // app -> fragment
                    string type = appElem.Attribute("type")?.Value;
                    ApparatusLayerFragment fr = new ApparatusLayerFragment
                    {
                        // @type -> tag composed by divID + spc + type
                        Tag = divId + (type != null? $" {type}" : "")
                    };
                    _groupNr = 0;
                    string itemId = null;
                    string[] locs = null;

                    // @from/@to pair provides a single location
                    if (appElem.Attribute("from") != null)
                    {
                        var t = ParseFromTo(appElem);
                        itemId = t.Item1;
                        fr.Location = t.Item2;
                        if (fr.Location == null)
                        {
                            Logger?.LogError("Word IDs {WordId} not found",
                                appElem.Attribute("from").Value + "-" +
                                appElem.Attribute("to").Value);
                            continue;
                        }
                        Logger?.LogInformation("Fragment location: {Location}",
                            fr.Location);
                    }
                    // @loc provides multiple locations, each to be assigned
                    // to a clone of this fragment; thus, we keep the locations
                    // in locs for later use
                    else
                    {
                        string loc = appElem.Attribute("loc")?.Value;
                        if (loc == null)
                        {
                            Logger?.LogError("No location for app element");
                            continue;
                        }
                        var itemIdAndlocs = ParseLoc(loc);
                        if (itemIdAndlocs == null)
                        {
                            Logger?.LogError("Word IDs not found: " +
                                appElem.Attribute("loc").Value);
                            continue;
                        }
                        itemId = itemIdAndlocs.Item1;
                        locs = itemIdAndlocs.Item2;

                        Logger?.LogInformation("Fragment locations: {Location}",
                            string.Join(" ", locs));
                    }

                    // if the location refers to another item, change part
                    if (part.ItemId == null)
                    {
                        part.ItemId = itemId;
                        Logger?.LogInformation($"Item ID set to {itemId}");
                    }
                    else if (part.ItemId != itemId)
                    {
                        Logger?.LogInformation(
                            $"Item ID changed from {part.ItemId} to {itemId}");
                        if (part.Fragments.Count > 0)
                        {
                            foreach (var p in SplitPart(part)) yield return p;
                        }
                        part = CreatePart(id);
                        part.ItemId = itemId;
                    }

                    // app content: lem, rdg, note
                    foreach (XElement child in appElem.Elements())
                    {
                        // each child element (lem or rdg or note) is an entry
                        ApparatusEntry entry = new ApparatusEntry
                        {
                            // @type -> tag
                            Tag = child.Attribute("type")?.Value
                        };
                        fr.Entries.Add(entry);

                        // parse its content
                        XmlApparatusVarContent content;
                        switch (child.Name.LocalName)
                        {
                            case "lem":
                                entry.IsAccepted = true;
                                goto case "rdg";
                            case "rdg":
                                // @wit @source
                                ParseWit(child.Attribute("wit")?.Value, entry);
                                ParseSource(child.Attribute("source")?.Value, entry);
                                content = ParseVariantContent(child);
                                AddContentToEntry(content, entry);
                                break;
                            case "note":
                                entry.Type = ApparatusEntryType.Note;
                                content = ParseVariantContent(child);
                                AddContentToEntry(content, entry);
                                break;
                            default:
                                Logger?.LogError("Unexpected element {ElementName} in app",
                                    child.Name.LocalName);
                                break;
                        }
                    }

                    // duplicate fragment for @loc
                    if (locs != null)
                    {
                        // assign the same group ID to all the entries with a variant
                        string groupId = BuildGroupId(fr.Entries);
                        foreach (var entry in fr.Entries.Where(e => e.Value != null))
                            entry.GroupId = groupId;

                        foreach (string loc in locs)
                        {
                            string json = JsonConvert.SerializeObject(fr);
                            ApparatusLayerFragment clone =
                                JsonConvert.DeserializeObject<ApparatusLayerFragment>(json);
                            clone.Location = loc;
                            AddFragmentToPart(clone, part,
                                appElem.Attribute("loc").Value);
                            Logger?.LogInformation(
                                "Completed fragment at {Location} (entries: {EntryCount})",
                                clone.Location, clone.Entries.Count);
                        }
                    }
                    else
                    {
                        AddFragmentToPart(fr, part,
                            appElem.Attribute("from").Value + "-" +
                            appElem.Attribute("to").Value);
                        Logger?.LogInformation(
                            "Completed fragment at {Location} (entries: {EntryCount})",
                            fr.Location, fr.Entries.Count);
                    }
                } // app
            } //div

            if (part.Fragments.Count > 0)
                foreach (var p in SplitPart(part)) yield return p;

            _textIndex = null;
        }
    }
}
