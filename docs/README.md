# DB Cowboy documentation

DB Cowboy is a Windows desktop app for PostgreSQL.

## What the app does

| Page | What it is for |
|---|---|
| Home | Where you are connected, facts about that database (largest tables, indexes never scanned, installed extensions) and the way into each page. |
| Connections | Saved profiles. Passwords are protected with Windows data protection for your account, or kept for the app session only. |
| SQL | Tabbed queries with completion, formatting, EXPLAIN plans, charts, snippets and history. Read-only is on by default. |
| Tables | Rows, columns, indexes with usage counts, foreign keys and a relationship diagram. Row changes are staged, then applied in one transaction after review. |
| Extensions | What the server offers and what is installed; install, update and remove with the exact statement shown first. |
| In this database | A navigation group that follows the connected database: one page per installed extension that has a dedicated view. Vector lab (pgvector) is one of them. |
| Activity | A snapshot of sessions in the connected database, and the queries run from this computer. |

Source layout: `src/DbCowboy` holds the app. `Db*.cs` is the data layer, `MainWindow.*.cs` the pages, `MainWindow.Design.cs` and `BrandUi.cs` the shared brand helpers, `PgExtensions.cs` the extension guide and the list of extension pages.

## Build

Windows 10 version 2004 or newer, the .NET 10 SDK and the Windows SDK.

```powershell
dotnet build src/DbCowboy/DbCowboy.csproj -c Debug -p:Platform=x64
```

Start the app against a disposable database with `DbCowboy.exe --integration-ui`; it connects a read-only fixture profile to `127.0.0.1:55439 / cowboy_test`. Set `DBCOWBOY_DATA_HOME` to keep that run out of your real profile.

## Test

```powershell
docker run -d --name cowboy-test -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=cowboy_test -p 127.0.0.1:55439:5432 pgvector/pgvector:pg17
dotnet run --project tests/DbCowboy.Checks        # data layer against that container (PostgreSQL 17 or 18); creates a cowboy_demo schema
dotnet run --project tests/DbCowboy.ChartChecks   # chart model and rendering, no database
./tests/ReleasePayloadChecks.ps1                  # release payload guard
```

`DbCowboy.VectorChecks` and `DbCowboy.ConnectionChecks` are read-only checks written against one specific local dataset (a `word_embeddings` table reached through a saved profile). They stop at once when that profile is absent. Setting `DBCOWBOY_CHECK_CONNECTION` to an Npgsql connection string for a local fixture (localhost only) runs them there instead; see `tests/CheckTarget.cs`.

## Publish

```powershell
./scripts/Publish.ps1 -Version <version>
```

This writes a self-contained payload and a zip to `artifacts/DbCowboy-<version>-win-x64` and refuses to overwrite an existing release. Add `-Installer -InnoCompiler <path-to-ISCC.exe>` to build the installer from `packaging/DbCowboy.iss`. `scripts/Assert-ReleasePayload.ps1` runs as part of it and rejects profile data in the payload. `python -I scripts/Verify-Release.py <version>` then checks the finished zip: it is intact, the app files are present, nothing excluded is inside, and any `.sha256` files beside the zip and installer match.

## Other documents

- `restricted-connection-discovery.md`: why discovery only lists schemas the role may use.
