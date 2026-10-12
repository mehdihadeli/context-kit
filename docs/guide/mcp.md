# MCP setup

ContextKit supports both standard MCP transports:

- `stdio` is the default and is best for one agent process per session.
- Streamable HTTP is available at `/mcp` for multiple clients or a container.

The installed CLI tool and repository launchers use `ck`.
The server also ships as its own tool shim,
`groundkit-mcp`, which accepts the same options.

## Run the published package with dnx

`ContextKit.Mcp` is a NuGet tool package. Like `npx`, `dnx` downloads and
caches the tool, then runs it without a global installation. Install the
.NET 10 SDK (which provides `dnx`) and the .NET 10 ASP.NET Core runtime;
the standard SDK installation includes both. Ensure `dnx` is on `PATH` for
your editor. The package defaults to stdio, so no `mcp` subcommand is needed.

Run the pinned preview:

```bash
dnx ContextKit.Mcp --version 1.0.0-preview-0015 --yes --source https://api.nuget.org/v3/index.json
```

The process waits for MCP messages on stdin. Its stdout is reserved for MCP
JSON-RPC; do not wrap it in a command that prints startup banners.
`--yes` suppresses download confirmation, which an MCP client cannot answer.
Use it only for trusted packages and sources. Pin the version so updates are
deliberate. The first run requires NuGet access and may take longer to start.

### VS Code and GitHub Copilot

Add this to `.vscode/mcp.json` in the workspace:

```json
{
  "servers": {
    "contextkit": {
      "type": "stdio",
      "command": "dnx",
      "args": [
        "ContextKit.Mcp",
        "--version", "1.0.0-preview-0015",
        "--yes",
        "--source", "https://api.nuget.org/v3/index.json"
      ]
    }
  }
}
```

For a portable configuration shared with Copilot CLI, use `.mcp.json` at the
workspace root and replace the top-level `servers` key with `mcpServers`.
Current VS Code documentation recommends this portable format for new servers;
the `.vscode/mcp.json` example above remains supported for existing setups.

Start the server from VS Code's MCP configuration. Review its configuration
before trusting the workspace, and approve a server trust prompt if shown.
Use `MCP: List Servers` to inspect status and logs. If `dnx` is not found,
restart VS Code after installing the SDK, or use `"command": "dotnet"` and
prepend `"dnx"` to `args`; `dotnet dnx` runs the same tool executor.

For clients that use `mcpServers` rather than VS Code's `servers`, put the
same command and arguments under that client's `mcpServers` object.
To restrict packages, append `"--", "--libs", "react,vite"` to `args`.
The separator forwards options to the server rather than to `dnx`.
The published package still uses `GROUNDKIT_HOME` for a custom package store;
set it in the server's `env` object when sharing a store with the CLI.

### Test the published package

The explicit integration test launches the pinned NuGet package via
`dotnet dnx` from an isolated temporary directory, not the source project.
It checks initialization and tool discovery, and fails on non-JSON stdout.
It requires NuGet access, so normal test runs do not execute it:

```bash
dotnet test --project tests/ContextKit.Mcp.Tests/ContextKit.Mcp.Tests.csproj --filter-method '*Should_Initialize_And_List_Tools_From_Published_Package_Via_Dnx' --explicit on
```

## Run the installed CLI or Docker image

Start the local server:

```bash
ck mcp
```

Start the Streamable HTTP server locally:

```bash
ck mcp --http
ck mcp --http 4000
ck mcp --http 4000 --host 0.0.0.0
```

The default HTTP endpoint is `http://127.0.0.1:4000/mcp`.
Use `--libs react,vite` with either transport to restrict the session.

For Docker, start the MCP service from the repository root:

```bash
docker compose up --build mcp
```

The container exposes `http://localhost:8081/mcp` and stores its package data
in the `mcp-data` volume. Install or import packages into the mounted
`GROUNDKIT_HOME` before querying them.

The Docker image defaults to Streamable HTTP, but stdio is also supported:

```bash
docker compose run --rm -i mcp-stdio
```

Use `mcp-stdio` when an MCP client launches Docker as its subprocess. The
container communicates over stdin/stdout and does not publish a network port.
Both Docker services use the same `mcp-data` package volume.

For an installed or published executable, configure an MCP client with:

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "groundkit-mcp"
    }
  }
}
```

For an HTTP-capable MCP host, configure the URL directly:

```json
{
  "mcpServers": {
    "groundkit": {
      "type": "streamable-http",
      "url": "http://localhost:8081/mcp"
    }
  }
}
```

To restrict the server to specific installed packages, pass `--libs` or `-l`:

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "groundkit-mcp",
      "args": ["-l", "react,vite"]
    }
  }
}
```

## Recommended agent flow

1. Call `resolve-source` with the library name. Use `library_catalog` when choosing from starter sources.
2. Confirm returned package ID and version match the project dependency.
3. If the package is missing, read the guidance `query-docs` returns: it lists installed packages, published versions, and the install command. Use `search_packages` only when you need a different registry or a version outside that list.
4. Install the selected version with `download_package`. This is the only flow step that needs registry access, together with the missing-package lookup above.
5. Call `query-docs` with the installed package ID and a short API name, symbol, error code, or topic.
6. Answer from returned sections. If no useful hits return, resolve the source or refine the query; do not silently fall back to invented documentation.

When the agent knows only the question, not the library, `ask-docs` takes
`question` instead of `packageId`. It detects the package from the question —
preferring an installed copy over a download — installs it when the registry
publishes it, and answers in the same call. It is the one MCP tool that
installs, and only the package it detected.

Two more tools cover the case where the question spans libraries or you want to
show a choice first. `find_libraries` resolves the question to candidate library
ids without querying or installing anything, so the agent can decide before it
commits. `search-docs` is the one-call version of that decision: it takes the
question plus up to four optional library hints, resolves the libraries, installs
a published one once, and returns hits from every library fused into a single
ranking, each tagged with the package it came from. See
[Question-based search](/guide/question-search).

`get_docs` is a compatibility alias for `query-docs`. Both return the same structured payload: package ID, version, token total, and focused hits with document and section titles, content, code presence, and relevance scores.

When the requested package is not installed, `query-docs` does not fail silently. It answers with the packages it can see locally, the versions a registry publishes, and the install command to run, for example:

```text
Package 'vue@3.5.0' is not installed locally.
Version '3.5.0' of npm/vue is not published.

Available in the registry:
  npm/vue@latest (5,353,472 bytes)

Install one, then query again:
  ck install npm/vue latest
  ck query 'vue@latest' '<topic>'
```

This saves the round trip of a failed lookup followed by a manual search. The tool still downloads nothing by itself: the agent chooses the registry, package, and version, and a registry that is unreachable only removes the version list.

The equivalent CLI command is:

```bash
ck query nextjs "middleware authentication"
ck query "nextjs@16.0" "middleware authentication"
```

A bare name selects the newest installed version. `name@version` selects that
exact installed version. CLI output uses the same JSON fields as `get_docs`.

The one deliberate difference is that the CLI closes the loop on its own: when
`query` runs for a package that is not installed but is published, it downloads
the package and answers in the same command. `query-docs` and `get_docs` never
do that, because answering a pinned package is not a reason to fetch anything.
`ask-docs` is the MCP tool that may install, and only the single package it
detected from the question. Pass `--no-install` to make `query` behave like the
pinned lookup tools and report instead of downloading:

```bash
ck query vue "composition api" --no-install
```

## Query shape

```json
{
  "packageId": "react",
  "topic": "useEffect cleanup",
  "maxTokens": 2000,
  "maxHits": 8,
  "relativeScoreCutoff": 0.5
}
```

Use a lower token or hit limit for a narrow lookup. Raise it only when the topic needs surrounding context. The score ranks matches; it is not a confidence or truth probability.

## Trust boundaries

The server queries installed packages locally. `query-docs` does not fetch documentation, but when a package is missing it does read the registry catalog to list installable versions, so that one case needs network access. Package provenance and freshness come from the package manifest and source metadata. Registry results and downloads are external inputs, so pin versions and inspect source metadata when reproducibility matters.

MCP makes retrieval available to an agent; it does not make the final answer automatically correct. The host agent should distinguish missing evidence from a negative fact, preserve conflicting source information, and verify high-impact changes.
