# SolidWorks natural language copilot

Open-source suite that turns natural language into parametric SolidWorks
geometry, plus (later) a screen+voice tutor. See [PLAN.md](PLAN.md) for the full
architecture rationale.

> Windows only. Requires SOLIDWORKS installed locally (developed against 2024).

## Layout

```
core/          Python SOLIDWORKS COM automation — standalone, no AI
mcp_server/    wraps core/ as MCP tools (the Generator)
PLAN.md        architecture plan
smoke_test.py  live end-to-end check of the core/ round trip
```

`adapters/`, `tutor_app/`, and `worker/` come in later milestones (see PLAN.md).

## Setup

```powershell
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## Validate against live SolidWorks

```powershell
python smoke_test.py
```

This boots (or attaches to) SOLIDWORKS and runs:
create part → sketch → rectangle → dimension → extrude → read state →
modify dimension → read state. The part is left open so you can inspect it.

## Run the MCP server

```powershell
python -m mcp_server.server
```

### Connect from Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "solidworks-generator": {
      "command": "python",
      "args": ["-m", "mcp_server.server"],
      "cwd": "C:/Users/olive/Desktop/Claude For SolidWorks"
    }
  }
}
```

## Recommended companion: swapi-pilot (SolidWorks API reference MCP)

[swapi-pilot](https://github.com/arthurle3210/swapi-pilot-solidworks-mcp) is a
separate, hosted MCP server that gives the model live access to the SolidWorks
API docs, code examples, and enum values (`search_solidworks_api`,
`get_api_detail`, `get_enum`, ...). It is purely a *reference* server — it does
not drive SolidWorks. It pairs well with this project:

- **swapi-pilot** = the reference manual (knows the API)
- **this project** = the hands (executes in SolidWorks)

With both connected, the model can look up the correct call/enum via swapi-pilot
and then run it through our tools. Add it alongside the generator:

```json
{
  "mcpServers": {
    "solidworks-generator": {
      "command": "python",
      "args": ["-m", "mcp_server.server"],
      "cwd": "C:/Users/olive/Desktop/Claude For SolidWorks"
    },
    "swapi-pilot": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://swapi-pilot.com/mcp"]
    }
  }
}
```

> Note: swapi-pilot is a third-party, externally hosted server with no license
> declared. We only *use* its public endpoint here — no code from it is vendored
> into this repo. See its repository for terms and current setup instructions.

## Tool surface (v1)

`create_part`, `create_sketch`, `add_line`, `add_rectangle`, `add_circle`,
`add_arc`, `add_dimension` (semantic name required), `extrude_boss`,
`extrude_cut`, `modify_dimension`, `get_model_state`.

Every modifying tool returns `{ success, errors[], rebuild_errors[], ... }` so
the model can see what happened and self-correct.

## Design notes

- **All COM lengths are meters.** `core/units.py` converts mm/in → meters on the
  way in and back on the way out. This is the #1 source of scale bugs.
- **Dimensions are force-named.** Every dimension is renamed to a semantic name
  on creation so `modify_dimension("width", 80)` stays reliable across rebuilds.
- **Rebuild + validate after every change.** Feature errors are collected and
  returned in `rebuild_errors`.

## License

MIT (recommended in PLAN.md; add a LICENSE file before publishing).
