# Studio UX direction

Status: agreed direction. The decisions are recorded at the end.

Studio is a power tool for shard developers who know their data by id: "0x1F03", "body 400", "hue 1153". It should feel like one tool, not ten panels. Every screen follows the same few patterns, so learning one teaches all of them.

## Principles

1. **Find first.** Most time goes into finding the thing. Browsing is the large area of every screen, and searching works the same everywhere.
2. **One pattern per kind of screen.** Browsers share one layout, inspectors share one shape and documents (the map) share one frame. A new panel picks a pattern; it does not invent one.
3. **Everything is linked.** A tile links to its art, a texmap lists its land tiles, a multi lists its components and a hue shows on any asset. Any id shown in Studio can be clicked.
4. **Keyboard first, mouse friendly.** Every action has a menu entry that shows its shortcut, so the keys can be learned from the menus.
5. **Never lose work, never touch a published pack.** Edits collect into a change set; saving writes a new pack.

Studio stays one main window with docked panels; every pattern below assumes that.

## The browser pattern

Every asset browser (art, gumps, texmaps, hues, bodies, multis, tiledata, sounds, cliloc and the map tile palettes later) has the same layout:

```
+--------------------------------------------------------------+
| [search........] [filters v] [grid|list] [size ---o--] 39705  |  toolbar
+-------------------------------------------+------------------+
|                                           |  Inspector       |
|   collection: grid of thumbnails          |  header          |
|   (or table rows for data-only kinds)     |  preview         |
|                                           |  properties      |
|                                           |  relations       |
|                                           |  actions         |
+-------------------------------------------+------------------+
```

- **Collection** (left, wide): a virtualised grid of thumbnails, or a table for data without pictures (tiledata, sounds, cliloc). Grid and list are both available where a picture exists. Only visible items decode.
- **Inspector** (right, narrow, resizable): everything about the selected item. Below a minimum panel width it moves under the collection, so a browser still works as a narrow docked palette next to the map.
- **Toolbar**, always in this order: search, filters, view mode, thumbnail size, result count.
- **Search** takes an id (`0x1F03`, `7939`, `1F03h`) or text. An exact id jumps to the item.
- **Keys**: arrows move, Page Up/Down jump, Home/End, Enter opens the item's main action, Ctrl+F focuses search, Ctrl+C copies the id.
- **Context menu** on every item: copy id, the item's links, export.

Animations and multis follow it as well: the body grid and the multi grid are the collection, and the player and the composite view are the inspector's preview. The splitter gives the preview more room when needed.

## The inspector shape

Every inspector has the same sections, in this order, and leaves out the ones that do not apply:

1. **Header**: kind, id in hex and decimal (right-click copies), name.
2. **Preview**: checkerboard background, zoom, and the panel's hue (below). Animated kinds have playback here.
3. **Properties**: a two-column key/value table, read-only now and editable later in the same place.
4. **Relations**: "used by" and "uses" lists, each entry a link (land tiles using a texmap, multis containing a static, the gump of an equippable item, the animation of a static).
5. **Actions**: export, open in another panel, and later revert or edit.

## Navigation

- **One selection model.** Every link is an asset reference (`kind:id`). Opening one focuses that kind's browser and selects the item.
- **History.** Alt+Left and Alt+Right (and mouse buttons 4/5) move back and forward through opened references, as in a web browser.
- **Go to anything** (Ctrl+P): one search box over every kind. Typing `0x1F03` lists the static, the tiledata entry, the gump and the multi with that id; typing `robe` lists names across art, tiledata and cliloc.
- **Commands** (Ctrl+Shift+P): every menu action, searchable.
- **Pins.** Any item can be pinned; the Pins panel keeps a working set across sessions.

## Hue and ids

- **Hue per panel.** Each browser that shows pictures (art, gumps, animations, multis) has its own hue control in the inspector's preview, with the same widget everywhere: a hue id field, a swatch strip that opens a hue picker, and a partial hue toggle that defaults to the tile's PartialHue flag. The hue applies to the preview and, as an option, to the collection's thumbnails. "Show hue" links to the Hues browser.
- **Id format.** Hex first with decimal next to it (`0x1F03 (7939)`); a setting switches to decimal first.

## Workspaces and documents

- A **workspace** is a saved docking layout for a job: Browse (browsers as tabs, the inspector wide), Map (the map in the centre, the tile, static and hue palettes as narrow browsers around it, the change list at the bottom), and later Edit assets.
- The **map is a document**: it owns the centre, with its own toolbar (tools, z and layer filters, grid, overlays). Browsers dock around it as palettes; picking in a palette sets the active brush.
- Workspaces are switched from a tab strip on the main toolbar and each one keeps its own layout.

## Editing model (later milestones)

- Every edit is an operation in the **change set**: a map edit, a tiledata field, an imported image. The Changes panel lists them by kind, and each one links to its item.
- Global undo and redo (Ctrl+Z, Ctrl+Y) over the change set, across panels.
- Changed items carry a marker in every collection and inspector.
- **Save** writes a new pack (published packs are immutable) and shows a diff of the two packs first. Unsaved changes survive a crash in an autosaved change set.

## Feedback

- **Status bar**: open pack, selected reference, background decodes in flight, frame rate.
- **Toasts** for completed actions (export written, pack saved) with a link that opens the folder; failures also go to the Log panel.
- Empty and loading states say what to do ("No pack open: File > Open Pack"); a section that failed to load says so in its browser and in Pack Info.

## Visual language

Studio uses the ModernUO brand from modernuo.com, as a modern pro tool rather than stock ImGui. Every colour, font and size is a token in `Gui/Theme.cs`; panels never pick their own.

- **Colour.** Warm near-black neutrals (`#141414` base, `#1a1a1a` panel, `#212121` surface, `#2a2a2a` raised, hairline borders). **muo-gold `#d5bf74` is the one accent**: selection, focus, primary buttons, the active tab's overline. muo-blue `#748ad5` is for links and information, green `#77d574` for success, red for errors, amber for warnings, purple for special states. Colour marks state, never decoration.
- **Type.** Inter (regular and semibold) for text, JetBrains Mono for ids, coordinates, hashes and the log. Section labels are small, semibold, uppercase and muted; inspector titles are the item's name in semibold, with the id in gold monospace under it.
- **Icons.** Lucide (ISC licence), merged into every UI font, on tabs, menus and toolbar buttons; every icon button has a tooltip and a menu equivalent.
- **Widgets.** Studio's own (`Gui/Ui.cs`), drawn on ImGui's draw lists: segmented controls instead of radio buttons, switches instead of checkboxes, pill search fields with a magnifier and a clear button, icon buttons with soft hover backgrounds, gold primary buttons, chips, cards with 8 px rounding, and short hover and selection fades. Stock ImGui widgets appear only where Studio has no counterpart yet (combos, sliders, tables), restyled by the theme.
- **Chrome.** A top bar with the ModernUO mark, the menus, the workspace switch in the middle and the open pack on the right; a status bar with Studio's state, the selection and the frame rate. Dock tabs carry an icon, have no window menu or dock close button, and show a close button only on the hovered selected tab.
- **Layout.** An 8 px spacing grid; collections sit on a sunken card, inspectors on a raised one; empty states centre an icon, a title and what to do.
- Fonts, icons and their licences are embedded from `Fonts/`, so Studio looks the same on every machine; one UI scale setting covers high-DPI screens.

## Decisions

| Question | Decision |
|---|---|
| Window model | One main window with docked panels. ImGui multi-viewports (tear-off OS windows) may come later; they need platform-window support in the SDL3 backend. |
| Hue scope | Per panel, with one shared hue widget. |
| Icon font | Lucide (ISC licence). |
| Look | Modern pro tool in the ModernUO brand: muo-gold accent, muo-blue links, Inter and JetBrains Mono (from modernuo.com, replacing the blue accent first proposed). |
| Id display | Hex first, decimal next to it; a setting for decimal first. |
| Grid or list | Grid by default where a picture exists; the choice is remembered per browser. |

## First steps

1. A browser framework that replaces `SpriteGridBrowser`: collection (grid or table, custom cell drawing), inspector sections, toolbar, keys and context menu in one place. Move every browser onto it, Animations and Multis included.
2. The shared hue widget, used by every picture browser.
3. Asset references, history and the cross-panel links.
4. Status bar and toasts.
5. Go to anything and the command palette.
6. Lucide and monospace fonts; workspaces (Browse now, Map once the map viewer exists).
