# SolidWorks natural language copilot — architecture plan

Two-part open-source suite for SolidWorks. Built for native Windows throughout — no macOS code paths anywhere in this plan.

1. **Generator** — an MCP server that turns natural language into parametric single-part geometry (sketches, dimensions, extrusions).  
2. **Tutor** — a Windows companion app, modeled on [farzaa/clicky](https://github.com/farzaa/clicky) (MIT), that watches the screen, listens, and teaches SolidWorks by pointing at UI elements, grounded in live SolidWorks state pulled over COM.

Target: an open tool for the CAD community, not a personal script. Keep the pieces modular so contributors can touch one layer without understanding the others.

---

## Part 1: Generator (NL → parametric CAD)

### Why MCP, not a native SolidWorks add-in

A native add-in means code signing, an installer, and SolidWorks-version certification for very little payoff — anyone with Claude Desktop, Claude Code, or Cursor can already use an MCP server with zero install friction. It's also a much lower barrier for community contributors who don't want to deal with COM/.NET interop just to add a feature.

A landscape check turned up roughly half a dozen existing SolidWorks MCP servers (lobehub's, Sam-Of-The-Arth's, alisamsam's, the Apify-hosted one, SolidPilot). All of them do basic part-level *generation* only — sketch, extrude, maybe fillet/pattern. None handle assembly-level operations, and none appear to do real context-aware *editing* of existing geometry. SolidPilot's layering (Python core \+ C\# adapter \+ COM bridge, with version-specific adapter DLLs) is the most architecturally mature precedent and worth mirroring. The gap — reliable editing, not just generation — is where this project can actually differentiate instead of being clone \#7.

### Scope (current)

Single part only. No assembly automation (mates, multi-component, configurations across an assembly) — explicitly descoped for now. Can be revisited as a separate extension later.

### Layers

- **CAD automation layer** — Python wrapper around the SolidWorks COM API for everything that works cleanly that way. Optional thin C\# adapter (version-specific, per SolidWorks release) for operations that are awkward over raw COM-via-Python.  
- **MCP server (Python)** — exposes the tool schema below, owns context serialization and the validation/safety logic.  
- **Context/state inspector** — serializes the current part's feature tree and dimension list into something the LLM can reason about. Needed even at single-part scale; without it, editing existing geometry is just guessing.  
- **Validation/safety** — rebuild and check for errors after every modifying call; semantic dimension naming enforced by the tools themselves, not left to convention.

### Tool schema (v1)

Every tool returns an envelope of `{ success, errors[], rebuild_errors[] }` alongside its specific output, so the calling model can see what actually happened and self-correct rather than assuming success.

create\_part(template?: string, units?: "mm"|"in") 

  \-\> { part\_id }

create\_sketch(part\_id: string, plane: "Front"|"Top"|"Right"|{face\_ref: string})

  \-\> { sketch\_id }

add\_line(sketch\_id: string, start: \[x,y\], end: \[x,y\])

  \-\> { entity\_id }

add\_rectangle(sketch\_id: string, corner1: \[x,y\], corner2: \[x,y\])

  \-\> { entity\_ids: string\[\] }

add\_circle(sketch\_id: string, center: \[x,y\], radius: number)

  \-\> { entity\_id }

add\_arc(sketch\_id: string, center: \[x,y\], radius: number, start\_angle: number, end\_angle: number)

  \-\> { entity\_id }

add\_dimension(entity\_id: string, dimension\_type: "length"|"radius"|"diameter"|"angle"|"distance",

              value: number, name: string)  // name is REQUIRED — no auto-generated D1@... names allowed

  \-\> { dimension\_id, current\_value }

extrude\_boss(sketch\_id: string, depth: number, direction: "blind"|"through\_all"|"up\_to\_surface",

             profile\_selection?: string)  // disambiguates which closed region if the sketch has multiple

  \-\> { feature\_id }

extrude\_cut(sketch\_id: string, depth: number, direction: "blind"|"through\_all"|"up\_to\_surface",

            profile\_selection?: string)

  \-\> { feature\_id }

modify\_dimension(dimension\_name: string, new\_value: number)

  \-\> { old\_value, new\_value }   // this is what makes "make the case 10mm taller" work for free

get\_model\_state(part\_id: string)

  \-\> {

       sketches: \[{ sketch\_id, plane, fully\_defined: bool, entity\_count }\],

       features: \[{ feature\_id, type, name, suppressed: bool }\],

       dimensions: \[{ name, value, owner\_feature }\]

     }

Phase 2 additions (still single-part scope): `fillet`, `chamfer`, `linear_pattern`, `circular_pattern`, `mirror` — same shape as the extrude tools, taking edge/feature references plus parameters.

### Key technical risks

- **Stable geometry references.** Referencing a face/edge by index breaks silently after any upstream edit. Use SolidWorks's persistent reference IDs from day one, not raw indices.  
- **Forced dimension naming.** Every dimension created by a tool gets renamed to something semantic immediately — this is what makes `modify_dimension` reliable later.  
- **Sketch closure.** Sketches need to end up fully defined, and a sketch with multiple closed regions needs `profile_selection` to disambiguate which one to extrude.  
- **Feature order dependency.** You can't dimension what isn't sketched yet — the agent's plan has to respect strict ordering even though the user's description won't.

### Repo structure

core/          Python SolidWorks automation library — useful standalone, no AI involved

mcp\_server/    wraps core/ as MCP tools; owns context serialization \+ safety logic

adapters/      optional, version-specific C\# pieces, isolated from the Python layer

### Roadmap

1. Sketch primitives \+ extrude/cut \+ enforced semantic naming (MVP).  
2. Editing reliability — persistent references, rebuild-and-validate with rollback.  
3. (Optional, later) fillet/chamfer/patterns, still single-part.

---

## Part 2: Tutor (screen \+ voice CAD teacher)

### Inspiration and platform reality

Forked conceptually from [farzaa/clicky](https://github.com/farzaa/clicky) (MIT) — push-to-talk → AssemblyAI transcription → screenshot \+ transcript to Claude via streaming SSE → Claude embeds `[POINT:x,y:label:screenN]` tags → cursor overlay animates to that point → ElevenLabs TTS reads the response. Clicky itself is macOS-only (ScreenCaptureKit, NSPanel); since SolidWorks is Windows-only and this user runs native Windows, this is a full reimplementation of the *pattern*, not a port of the code.

### What carries over unmodified

The Cloudflare Worker backend (TypeScript routes for `/chat`, `/tts`, `/transcribe-token`) is platform-agnostic — deploy it once, point a Windows client at it.

### Stack

C\#/.NET (WPF or WinUI3) — chosen partly because it overlaps with the C\# adapter layer from Part 1, so the same skillset covers both halves of the suite.

- Screenshot: plain GDI `Graphics.CopyFromScreen` (point-in-time snapshot is enough; no need for a continuous capture API).  
- Overlay: transparent, click-through, always-on-top WPF window via P/Invoke (`WS_EX_LAYERED` / `WS_EX_TRANSPARENT`), standing in for NSPanel.  
- Global hotkey: `RegisterHotKey` or the NHotkey wrapper — pick a combo SolidWorks doesn't already use (it leans heavily on Ctrl+Alt chords for view manipulation).  
- Audio: NAudio for mic capture (push-to-talk) and TTS playback.

### The actual differentiator

Attach to the running SolidWorks instance via `Marshal.GetActiveObject("SldWorks.Application")` — no need to register as a SolidWorks Add-in just for this. Pull live state (active command, feature tree errors, whether the current sketch is closed/dimensioned) and inject it into the same Claude call alongside the screenshot. Vanilla Clicky is guessing from pixels; this version knows what's actually wrong.

### Process architecture

Standalone tray app, not a SolidWorks Add-in or taskpane — the overlay needs to float over the entire screen (toolbars, menus, dialogs), which a docked taskpane can't do. It just also happens to reach into SolidWorks over COM when it's running.

### Repo structure

tutor\_app/     C\#/.NET WPF or WinUI3 client (the Windows reimplementation of Clicky's app)

worker/        forked/reused TypeScript Cloudflare Worker, unmodified

References `core/` from Part 1 for read-only state queries — same COM layer, used in inspect-only mode here instead of execute mode.

---

## Open questions

- Project name / branding (current working name is whatever you call the repo).  
- License — MIT recommended, matches Clicky's and keeps community contribution friction low.  
- Whether/when to revisit assembly-level automation as a separate extension.

## Note for Claude Code

This file is the full context from the planning conversation that produced it. Suggested first milestone: scaffold `core/` \+ `mcp_server/` from Part 1 and get a single sketch → dimension → extrude round trip working end to end through Claude Desktop before starting on the Tutor app — it's the primary deliverable, and the Tutor's COM read-only queries depend on the same automation layer existing first.  
