# StockWise MCP

AI-powered inventory chatbot for ISM 6225 (Fall 2026). ASP.NET Core MVC (.NET 10) app that answers
inventory questions by calling five read-only MCP tools over a SQLite database.

- **Guided Demo** mode works with no API key. **AI mode** is optional (OpenAI key).
- MCP tools at `/mcp`: `lookup_product`, `search_inventory`, `get_inventory_summary`,
  `rank_products_by_value`, `get_low_stock_products`. Cost data is never returned.
- Chat API: `GET /api/chat/token`, then `POST /api/chat` with header `X-CSRF-TOKEN` and body
  `{ "message": "...", "mode": "guided" | "ai" }` (message 2–500 characters).

## Run locally

```bash
dotnet run --project StockWiseMcp.csproj
```

The database (`App_Data/stockwise.db`) is created and seeded on first run. To turn on AI mode:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "<your key>"
```

## Tests

```bash
dotnet test Tests/StockWiseMcp.Tests
```

72 automated tests (xUnit) cover the five MCP tools against independent SQL, chat input validation,
CSRF, the `/mcp` access key, MCP tool discovery, adversarial input and AI-mode fallback. Each test
run uses its own temporary database. Evidence from the latest run is in `Evidence/`.

## Deploy (Render, free tier)

GitHub Pages only hosts static files, so it cannot run this app. Render runs it from the `Dockerfile`.

1. On render.com: **New + → Blueprint**, pick this repository (it reads `render.yaml`).
2. Optional environment variables, set in the Render dashboard only, never in Git:
   - `OpenAI__ApiKey`: enables AI mode.
   - `StockWise__McpAccessKey` (16+ characters): lets outside MCP clients call `/mcp`. If unset, a
     random key is generated at startup and only the built-in chatbot can use `/mcp`.
3. Open `https://<service>.onrender.com/`.

Free-tier notes: the service sleeps after inactivity (the first request takes about a minute), and the
free tier has no persistent disk, so the SQLite file is recreated from the seed data on every restart.
The app is read-only, so no user data is lost, but this is a known limitation.
