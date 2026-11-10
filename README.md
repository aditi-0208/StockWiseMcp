# StockWise MCP

AI-powered inventory chatbot for ISM 6225 (Fall 2026). ASP.NET Core MVC (.NET 10) app that answers
inventory questions by calling five read-only MCP tools over a SQLite database.

- **Guided Demo** mode works with no API key. **AI mode** is optional (Google AI Studio or OpenAI key).
- MCP tools at `/mcp`: `lookup_product`, `search_inventory`, `get_inventory_summary`,
  `rank_products_by_value`, `get_low_stock_products`. Cost data is never returned.
- Chat API: `GET /api/chat/token`, then `POST /api/chat` with header `X-CSRF-TOKEN` and body
  `{ "message": "...", "mode": "guided" | "gemini" | "openai" | "ai" }` (message 2–500 characters).

## Pages

`/` overview, `/Home/Chat` chatbot, `/Home/Inventory` stock overview, `/Home/Architecture` how it works.

## Run locally

```bash
dotnet run --project StockWiseMcp.csproj
```

The database (`App_Data/stockwise.db`) is created and seeded on first run. To turn on AI mode:

```bash
dotnet user-secrets set "Gemini:ApiKey" "<your Google AI Studio key>"
```

An OpenAI key also works (`OpenAI:ApiKey`). Each configured key appears as its own choice in the chat page's Mode dropdown. `Gemini:Model` changes the default model, `gemini-3.6-flash`.

## Tests

```bash
dotnet test Tests/StockWiseMcp.Tests
```

80 automated tests (xUnit) cover the five MCP tools against independent SQL, chat input validation,
CSRF, the `/mcp` access key, MCP tool discovery, adversarial input, AI-mode fallback and the web pages. Each test
run uses its own temporary database. Evidence from the latest run is in `Evidence/`.

`Tests/scripts/evaluate_chat.py` runs the 10-question evaluation against a running app and regenerates the evidence files.

## Deploy (Render, free tier)

GitHub Pages only hosts static files, so it cannot run this app. Render runs it from the `Dockerfile`.

1. On render.com: **New + → Blueprint**, pick this repository (it reads `render.yaml`).
2. Optional environment variables, set in the Render dashboard only, never in Git:
   - `Gemini__ApiKey` or `OpenAI__ApiKey`: enables AI mode.
   - `StockWise__McpAccessKey` (16+ characters): lets outside MCP clients call `/mcp`. If unset, a
     random key is generated at startup and only the built-in chatbot can use `/mcp`.
3. Open `https://<service>.onrender.com/`.

Free-tier notes: the service sleeps after inactivity (the first request takes about a minute), and the
free tier has no persistent disk, so the SQLite file is recreated from the seed data on every restart.
The app is read-only, so no user data is lost, but this is a known limitation.
