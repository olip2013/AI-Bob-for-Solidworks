"""mcp_server — exposes core/ as MCP tools.

Owns the live SolidWorks session and the part registry that lets stateless tool
calls refer to the same open document across a conversation. All geometry logic
lives in core/; this layer is schema + glue + safety envelope only.
"""
