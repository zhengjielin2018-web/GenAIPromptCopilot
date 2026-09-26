using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Endpoints;

/// <summary>Adopt（2026-09-25）：採用推薦的一套組合；有它時 Text 忽略（設計 §6.1）。
/// Safety：on（預設）／off，off 是測試用的審查開關，後端 Safety:AllowDisable 開著才收。</summary>
public sealed record MessageRequest(string? Text, AdoptRequest? Adopt = null, string? Safety = null);
public sealed record SaveRequest(string Intent);
public sealed record CreateSessionRequest(string? Retrieval);
public sealed record SessionCreated(string SessionId, string Retrieval);
public sealed record SavedToShared(Guid Id);
public sealed record ErrorBody(string Error);
public sealed record FinalDto(string Positive, string Negative, string Tips, string IntentSummary,
    IReadOnlyList<TagSource> PositiveSources, IReadOnlyList<TagSource> NegativeSources);
public sealed record SessionSnapshotDto(string SessionId, string Status, string? Profile, int TurnIndex, int AskCount, int AskLimit,
    IReadOnlyDictionary<string, string> FacetStates, FinalDto? LastFinal, string Retrieval, IReadOnlyDictionary<string, string> FacetTags);

public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("Sessions");

        // body 可省略：minimal API 的 nullable body 參數在沒有 body 或 body 為空時是 null。
        g.MapPost("/", (CreateSessionRequest? req, SessionStore store) =>
        {
            bool? enabled = req?.Retrieval?.Trim().ToLowerInvariant() switch { null or "" or "on" => true, "off" => false, _ => null };
            if (enabled is null) return Results.BadRequest(new ErrorBody("retrieval 只能是 on 或 off"));
            var s = store.Create(enabled.Value);
            return Results.Created($"/api/sessions/{s.Id}", new SessionCreated(s.Id, s.RetrievalMode));
        })
        .WithSummary("開一段新對話")
        .WithDescription("""
            body 可省略：`{"retrieval": "on" | "off"}`，預設 `on`。`off` 的對話不查知識庫（模型拿不到 `SearchPresets` 與 `SearchSimilarPrompts`），是量測用的對照組；建立後不能改。其他值回 `400`。

            回 `201` 與 `{"sessionId": "...", "retrieval": "on" | "off"}`，之後的呼叫都帶這個 id。

            session 只存在記憶體：API 重啟就消失；閒置超過 `Orchestrator:SessionSlidingExpirationMinutes`（預設 120 分鐘）也會過期，之後再用這個 id 會得到 404。
            """)
        .Produces<SessionCreated>(StatusCodes.Status201Created)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        g.MapGet("/{id}", (string id, SessionStore store, OrchestratorOptions options) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            // 不拿 session 鎖：重載時連線已隨頁面斷掉、那一輪已回滾；兩個分頁共用同一個 id 時讀到半途狀態是可接受的最壞情況。
            var final = s.LastFinal is { } f
                ? new FinalDto(f.Positive, f.Negative, f.Tips, f.IntentSummary, f.PositiveSources ?? Array.Empty<TagSource>(), f.NegativeSources ?? Array.Empty<TagSource>())
                : null;
            return Results.Ok(new SessionSnapshotDto(s.Id, s.Status.ToString(), s.Profile, s.TurnIndex, s.AskCount, options.MaxAskCount,
                s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)), final, s.RetrievalMode, new Dictionary<string, string>(s.FacetTags)));
        })
        .WithSummary("讀 session 目前的狀態")
        .WithDescription("""
            給前端整頁重載後重建畫面用：狀態（`Collecting`／`Finalized`）、題材、每個 facet 的狀態、追問已用幾次（`askCount`／`askLimit`）、最後一次定稿（未定稿為 `null`；欄位同 `final` 事件的 `finalized`，含 tag 來源）、這段對話是否使用知識庫（`retrieval`）、模型給每個已涵蓋 facet 的英文 tag（`facetTags`）。

            不含對話紀錄：對話流由前端自己保存。唯讀，除了重置閒置過期的計時之外不改任何狀態。

            - `404`：session 不存在或已過期
            """)
        .Produces<SessionSnapshotDto>(StatusCodes.Status200OK)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound);

        g.MapPost("/{id}/messages", async (string id, MessageRequest req, SessionStore store, IPromptOrchestrator orchestrator,
            PresetRepository presets, FacetCatalog catalog, IOptions<SafetyOptions> safety, HttpContext http) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            if (req.Adopt is null && string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new ErrorBody("text 不可為空"));
            bool? safetyOn = req.Safety?.Trim().ToLowerInvariant() switch { null or "" or "on" => true, "off" => false, _ => null };
            if (safetyOn is null) return Results.BadRequest(new ErrorBody("safety 只能是 on 或 off"));
            if (safetyOn is false && !safety.Value.AllowDisable)
                return Results.Json(new ErrorBody("後端沒開放關閉審查：.env 設 SAFETY_ALLOW_DISABLE=true 後重建 api（本機開發設 Safety:AllowDisable）"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            try
            {
                TurnInput input;
                if (req.Adopt is { } adopt)
                {
                    // 拿著鎖再讀 session：沒鎖時讀到的可能是上一輪回滾中的半途狀態
                    if (!s.RetrievalEnabled) return Results.Conflict(new ErrorBody("這段對話沒有知識庫，沒有組合可以採用"));
                    if (s.Profile is null) return Results.Conflict(new ErrorBody("尚未判定題材，還不能採用組合"));
                    var preset = await presets.GetAsync(adopt.PresetId, http.RequestAborted);
                    if (preset is null) return Results.BadRequest(new ErrorBody($"找不到 preset #{adopt.PresetId}"));
                    try
                    {
                        var c = AdoptionComposer.Compose(adopt, preset, s, catalog, s.TurnIndex + 1);
                        input = new TurnInput(c.Text, c.Adoption, c.Preset, safetyOn.Value);
                    }
                    catch (AdoptValidationException e) { return Results.BadRequest(new ErrorBody(e.Message)); }
                }
                else input = new TurnInput(req.Text!.Trim(), SafetyOn: safetyOn.Value);

                await SseWriter.WriteAsync(http.Response, orchestrator.RunTurnAsync(s, input, http.RequestAborted), http.RequestAborted);
                return Results.Empty;
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.Empty; }
            // headers 已經送出去就改不了 status code，框架的 500 到不了客戶端，只會把連線砍掉、
            // 留下一段被截斷的串流。補發一個 error frame，客戶端才知道這一輪結束了、結束在哪。
            // 還沒開始寫就不接（走框架的 500 才是對的）。
            catch (Exception e) when (http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
            {
                // 例外訊息可能帶連線字串、路徑、上游原文：進 log，不進串流
                app.Logger.LogError(e, "turn failed mid-stream for session {SessionId}", id);
                await SseWriter.WriteOneAsync(http.Response,
                    new ErrorEvent("turn_failed", "這一輪失敗，已還原到送出前的狀態。可以直接再送一次。"),
                    http.RequestAborted);
                return Results.Empty;
            }
            finally { s.Lock.Release(); }
        })
        .WithSummary("送一句話，跑一輪對話（SSE 串流）")
        .WithDescription("""
            body：`{"text": "一個銀髮少女站在雨夜的霓虹街頭"}`；或採用推薦的一套組合 `{"adopt": {"presetId": 41720, "dimension": "clothing", "take": ["clothing.upper", "clothing.head"]}}`（`take` 是「照它的」facet，其餘該維度的 facet 保留使用者原本的；有 `adopt` 時 `text` 忽略，伺服器會組一句「採用〈標題〉（知識庫 #id）：…照它的（tags）；…保留我的。」當使用者訊息，`session` 事件的 `text` 帶回這句）。回應是 `text/event-stream`，每一筆是 `event: <名稱>` 加一行 `data: <JSON>`。

            | 事件 | 內容 |
            | :--- | :--- |
            | `session` | 一定是第一筆：第幾輪（`turnIndex`）、這輪開始時的狀態（`Collecting` 還在收集／`Finalized` 已定稿）；`text?` 採用輪才有，是伺服器組的採用句（2026-09-25），用它換掉使用者泡泡 |
            | `tool_call`／`tool_result` | 模型呼叫的工具與結果，一輪可能好幾次。`tool_result.presets` 是檢索到的 preset（`{id, title, imageUrl, sourceRef}`；`sourceRef` 是資料來源識別，如 `civitai:12345:0`，圖片屬於原作者，顯示時要標出處），可拿 id 去 `GET /api/presets/{id}` |
            | `dimensions` | `{ profile, facetStates: {facetId: state}, facetTags?: {facetId: "sandals"} }`：題材與每個 facet 的狀態，`covered`／`missing`／`waived`（使用者說不指定）／`notApplicable`；`facetTags`（2026-09-25）是模型給已涵蓋 facet 的英文 tag（只有 covered 的有）。輪中有變動就送，成功的一輪最後會再送一次完整的 |
            | `recommendations` | `{ turnIndex, dimensions: [{ dimension, label, anchored, anchorTags, sets: [{ presetId, title, imageUrl?, sourceRef?, dist, facets: [{ facetId, label, state, tags }] }] }] }`：整套組合推薦（2026-09-25）：追問時只有被問的維度、定稿時全部維度，跟在 `final`＋`dimensions` 之後；掛在該輪的追問卡／定稿卡下方。`retrieval: off` 的對話沒有。見 `2026-09-25-set-recommendations-design.md` §5 |
            | `final` | 這一輪的結果，看 `kind`：`ask` 追問（`preamble`、`asks`）、`message` 討論或回答問題（`message`、`options`）、`finalized` 定稿（`positive`、`negative`、`tips`、`intentSummary`：一句繁中需求描述，可拿來預填 `save-to-shared` 的 `intent`；`positiveSources`／`negativeSources`：逐 tag 的來源 `{tag, origin, presetIds, presetTitle, sourceRef}`，`origin` 是 `rag` 知識庫片段／`adopted` 採用的組合帶進來的／`llm` 模型生成／`base` 基礎詞，由伺服器比對 ledger 與採用紀錄標註）、`save_consent_requested` 使用者要求儲存（見 `save-to-shared`） |
            | `blocked` | 被攔下，`reason`：`Blocked_NSFW`、`Blocked_Celebrity`（輸入端，不會呼叫模型）、`Blocked_Output`（模型輸出被攔）、`Blocked_Upstream`（Gemini 拒絕生成）。session 狀態不變 |
            | `error` | 這一輪失敗，`code`：`timeout`、`protocol_violation`、`turn_failed`。session 已還原到送出前，可以直接重送同一句 |

            成功的一輪以 `final` + `dimensions`（+ `recommendations`，有的話）收尾；失敗或被攔的一輪以 `blocked` 或 `error` 收尾，不會有 `final`。
            一輪最多 `Orchestrator:MaxToolCallsPerTurn` 次工具呼叫、`Orchestrator:TurnTimeoutSeconds` 秒。

            Swagger UI 會等整輪跑完才一次顯示所有事件（通常數秒到數十秒）。要逐筆看，用 `curl -N` 或 repo 的 `manual-tests/chat.py`。

            測試用的審查開關：body 可加 `"safety": "off"`（預設 `on`），這一輪不做程式端審查——denylist 不比對、輸入分類器照跑但只用來判斷「你看著辦」、輸出不檢。Gemini 自己的攔截（`Blocked_Upstream`）不受影響。後端要設 `Safety:AllowDisable=true` 才收；`GET /api/config/safety` 回報目前能不能關。

            - `404`：session 不存在或已過期
            - `400`：`text` 是空白且沒有 `adopt`；`safety` 不是 `on`／`off`；`adopt` 的 preset 不存在、尚未拆分 facet、`take` 為空或含不屬於該維度／這套沒有 tag 的 facet
            - `403`：`safety: off` 但後端沒開放
            - `409`：同一個 session 上一輪還沒跑完；`adopt` 但這段對話 `retrieval: off` 或尚未判定題材
            """)
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces<ErrorBody>(StatusCodes.Status403Forbidden)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict);

        g.MapPost("/{id}/save-to-shared", async (string id, SaveRequest req, SessionStore store, IEmbeddingClient embed,
            HistoryRepository histories, IAuditSink audit, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound();
            // 這裡會讀 FacetStates 與 LastFinal。那一輪還在跑（而且隨時可能被回滾）時讀到的是半途的狀態。
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            try
            {
                if (s.Status != SessionStatus.Finalized || s.LastFinal is null || s.Profile is null) return Results.Conflict(new ErrorBody("尚未定稿"));
                // 審查關著產生的定稿沒經過輸出側檢查；共享庫會被別的對話檢索到，不能從這裡進去
                if (!s.LastFinal.Reviewed) return Results.Conflict(new ErrorBody("這份定稿是在關閉程式端審查時產生的，不能存進共享知識庫；打開審查後重新定稿再存"));
                if (string.IsNullOrWhiteSpace(req.Intent)) return Results.BadRequest(new ErrorBody("intent 不可為空"));
                var vec = (await embed.EmbedAsync(new[] { req.Intent }, GeminiEmbeddingClient.RetrievalDocument, ct))[0];
                var scores = JsonSerializer.Serialize(s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
                var newId = await histories.InsertAsync(new HistoryInsert(req.Intent.Trim(), s.LastFinal.Positive, s.LastFinal.Negative, s.Profile, scores, vec), ct);
                // 資料已經寫進去了：稽核再失敗也不能把回應變成 500，否則使用者一重試就多一筆重複的資料
                try { await audit.WriteAsync(new AuditEntry(s.Id, s.TurnIndex, "Saved_To_Shared", PayloadJson: JsonSerializer.Serialize(new { id = newId })), ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    app.Logger.LogWarning(e, "Saved_To_Shared audit write failed for session {SessionId}, row {RowId}", s.Id, newId);
                }
                return Results.Ok(new SavedToShared(newId));
            }
            finally { s.Lock.Release(); }
        })
        .WithSummary("把這個 session 的定稿存進共享庫")
        .WithDescription("""
            body：`{"intent": "一句話描述這張圖"}`。把這個 session 最後一次的定稿（positive、negative、題材、各 facet 狀態）連同 `intent` 的向量寫進 `shared_prompt_histories`，回新資料列的 `{"id": "..."}`。之後的對話呼叫 `SearchSimilarPrompts` 時會撈到它當參考。

            模型不會自己存：使用者說要存時，`messages` 只會回 `final.kind = save_consent_requested`，要由客戶端在使用者同意後呼叫這支。**這是真的寫入資料庫。**

            - `404`：session 不存在或已過期
            - `409`：還沒定稿、這個 session 還有一輪在跑，或最後一次定稿是在關閉程式端審查（`safety: off`）時產生的
            - `400`：`intent` 是空白
            """)
        .Produces<SavedToShared>(StatusCodes.Status200OK)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict);
    }
}
