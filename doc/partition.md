﻿# Partitioning

## Rationale

As we are dealing with text, we must define a partitioning criterion. We should examine the documents typical structure(s) and metrics to define the best strategy.

Of course, in the context of such editing system, we could not think of storing the whole text in a single unit, like in a big text file; this would not be scalable, hamper the highly networked nature of the parts, and make impossible to concurrently edit different parts of it. We must then partition our documents.

This partitioning must be driven by the intrinsic structure of the text, so that each resulting text part represents a meaningful, self-contained unit. For instance, in a corpus of inscriptions the item would be an inscription; in a corpus of epigrams, it would be an epigram; in a prose corpus of Platonic dialogs, it would be a paragraph. As for their typographical nature, paragraphs are modern units determined by the traditional text layout; but of course they obey the text meaning, by grouping one or more complete sentences into a relatively self-contained unit.

Of course this is an arbitrary choice; but, in a sense, this type of text partitioning is no different from the divisions applied to the structure of a TEI document. For instance, imagine a Plato's dialog where each part is a paragraph, and a corresponding TEI text where each paragraph is marked by p: as for archiving, the substantial difference would be that each paragraph gets separately stored in the database, while it is usually contained in a single file in XML. Joining text parts to rebuild the unique flow of base text would of course be trivial, whether we are exporting data to generate TEI, or just displaying a continuous text.

Naturally, such decomposition, functional to the software requirements and to the high density of metatextual data with their multiple connections, would be an overkill if applied outside the scenarios which practically defined the birth of the system itself.

The partition function targets MQDQ XML document comments. Its primary output will be a copy of the original document, with some `pb` elements inserted.

This element will have a `@n` attribute with the full citation (see below) of the partition ended by the `pb` element itself. This way, users will be able to check the process before we apply it.

This element is chosen because `pb` is never used in the original documents, and we need an empty element to avoid breaking the existing XML structure.

An importer will then just have to look at these `pb` elements, and store an item for each partition, with a text part and its citation. The citation will be provided in the item's description, and the item's sort key will be generated by joining the file name with the line ID.

Note that further processing will be required for `l`'s text, as it may contain non-textual data in the form of text, like e.g. the annotations used to aid metrics (see below).

- if there is `div2`, partition = `div2`.
- else: look at `div1@type` value:
  - `section`, `fragment`, `fragments`: e.g. a book in the Aeneid, or a poem in Catullus, or a fragment: partition = `div1`. For value section, the `div1` might be too long (e.g. a book in the Aeneid). In this case, refer to the algorithm below ('too long' here means >M).
  - work: a full work without further divisions: partition = `div1`. If >M, apply partitioning.

For partitioning case `div1@type=section` when this is too long (e.g. >50 lines), we must use an algorithmic approach, following these principles:

- min lines count treshold = N;
- max lines count treshold = M;
- break after the first `l` whose content matches the regex `[\u037e.?!][^\p{L}]*$` (=stop/exclamation/question mark at line end);
- if no match, prefer the largest partition (if any) below N, or just force a break at M;
- in the corner case where the starting `l` of the next partition is the last child of `div1`, this will be joined to the previous partition.

Also, the partitioning process requires us to calculate a **citation** for each partition. In general, the citation tells us exactly the portion of the source document which was cut, e.g. 3,12-36 for lines 12-36 in book 3 of Vergilius' Aeneid. In our case, we need to be able to rebuild the XML from the database, so we must keep all the metadata linked to the text division the partition belongs to. This won't be a nice citation, but it will serve its legacy-compliant purpose.

The citation is built by concatenating these components, separated by space:

1. the file name without extension (e.g. `LVCR-rena`).
2. the line number, always found at `l@n`.
3. `l@xml:id` must be preserved, too; we append it to the line number after a `#` character.
4. `div1` attributes, each with the form name=value separated by U+2016 (double vertical pipe, e.g. `xml:id=d001‖type=section‖decls=#md‖met=H`). This is a character which never occurs in the current MQDQ files.
5. `div2` attributes, when there is a `div2`.

Thus, a citation (no `div2`) would be like this: `LVCR-rena 20#d001l20 xml:id=d001‖type=section‖decls=#md‖met=H`.
