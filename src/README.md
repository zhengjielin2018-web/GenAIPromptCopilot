# PromptCopilot.Api

## 跑起來

```bash
docker compose up -d db                      # 專案根目錄；schema 由 db/init 自動建
cd src/PromptCopilot.Api
dotnet user-secrets set "Llm:ApiKey" "<GEMINI_API_KEY>"
dotnet run                                   # Swagger: http://localhost:5000/swagger
```

DB 密碼不是預設的 `postgres` 時，連線字串用環境變數蓋掉，不要改 `appsettings.json`：
`Database__ConnectionString="Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=<POSTGRES_PASSWORD>"`（或 `dotnet user-secrets set "Database:ConnectionString" "..."`）。

`launchSettings.json` 會挑別的埠、也會開瀏覽器。要固定埠又不要瀏覽器就繞過它，
但 user-secrets 只在 Development 載入，所以環境變數得自己補（2026-09-23 驗收就是這樣跑的）：

```bash
cd src
ASPNETCORE_ENVIRONMENT=Development dotnet run --project PromptCopilot.Api --no-launch-profile --urls http://localhost:5000
```

## 打一輪

```bash
SID=$(curl -s -X POST localhost:5000/api/sessions | jq -r .sessionId)
curl -N -X POST localhost:5000/api/sessions/$SID/messages -H 'content-type: application/json' \
  -d '{"text":"一個銀髮少女站在雨夜的霓虹街頭"}'
```

`-N` 讓 curl 不緩衝，能看到 SSE 逐筆吐出。事件形狀見主規格 §10.2。

Windows 沒有 `jq`。驗收那次是在 Git Bash 裡用 `curl.exe` 跑的，session id 用 `sed` 挖，
中文 body 先寫成 UTF-8 檔再 `--data-binary '@body.json'` 送——
直接把中文塞進 `-d` 會被主控台的本機編碼（cp950）轉掉，伺服器收到亂碼。

## 測試

```bash
cd src && dotnet test                        # 單元；整合測試預設 Skipped
PC_INTEGRATION=1 dotnet test                 # 需要 db 在跑
PC_INTEGRATION=1 PC_TEST_DB="Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=<POSTGRES_PASSWORD>" dotnet test --filter RepositoryIntegrationTests
GEMINI_API_KEY=<key> PC_INTEGRATION=1 dotnet test --filter GeminiContractTests   # 真打 Gemini，只斷言形狀
```

`PC_TEST_DB` 沒設時預設 `Password=postgres`；契約測試沒有 `GEMINI_API_KEY` 會直接失敗，不吞。

## 組態

所有數字在 `appsettings.json`（`Orchestrator:*`、`Llm:*Retries`），本機覆寫用 `appsettings.Development.json` 或 user-secrets。

## Eval

改完 `Prompts/system.md` 照 [`docs/eval-cases.md`](../docs/eval-cases.md) 手跑一遍，把 `prompt_version` 與結果填回去。
