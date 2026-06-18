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
