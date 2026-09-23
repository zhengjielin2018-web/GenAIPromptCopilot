# 多輪對話 — 設計規格

日期：2026-09-22
狀態：已併入主規格（2026-09-22）；本文件保留為推導紀錄，以主規格為準
範圍：子專案 2 的 SK agent 編排。改寫主規格 [§4.2–§4.7](2026-09-21-genai-prompt-copilot-design.md#42-plugins-與-tools)、§5.4、§6.2、§10.2、§11.1、§12、§14（清單見 §9）。不動 Python 管線與 `scripts/demo.py`。

---

## 1. 問題

主規格的 agent loop 要求**每一輪都以終止型 tool 收尾**，而終止型只有 `AskUser`、`FinalizePrompt`、`RequestSaveConsent`。LLM 回純文字會被重試、再被包成 `AskUser` 或發 `error`（主規格 §4.6）。

結果是「跟 LLM 討論建議」這件事在設計上沒有出口。使用者問「寫實跟動漫差在哪？」，LLM 只有兩個合法動作：假裝在追問（燒掉 `AskCount`、前端跳追問氣泡與 chip），或提早定稿。

逐條對照主規格，多輪討論會撞到七處：

| # | 卡點 | 出處 |
| :--- | :--- | :--- |
| 1 | 每輪必須推進狀態機，沒有「純講話」的合法出口 | §4.5 `TerminalToolFilter`、§4.6 純文字補救 |
| 2 | `AskUser` 的語意是「系統缺資訊而索取」，參數是 `missingFacetIds` + `suggestedOptions`，前端渲染成追問氣泡。拿它裝解說會污染 UI 與儀表板語意 | §4.2、§10.2 |
| 3 | `AskCount < 2` 是防系統死循環的閘門，但綁在 `AskUser` 這個工具上，使用者主動要求多聊也在燒同一份額度。§4.4 註解「防死循環針對的是系統，不是限制使用者」，機制跟意圖對不上 | §4.3、§4.4 |
| 4 | `Finalized` 後 `AskUser` 永久不可用。「把背景改成黃昏」可以重新定稿，但使用者每問一句（「negative 裡的 `blurry` 是幹嘛的？」）都收到一張新的完整定稿卡 | §4.4 |
| 5 | SSE `final` 只有 `ask` / `finalized` / `save_consent_requested` 三種 kind，協定層也沒有「這輪就是講話」的收尾 | §10.2 |
| 6 | history 修剪把 `SearchPresets` 結果壓成 `[{id, title}]`，使用者說「你剛剛第二個建議具體會加哪些 tag」時，snippet 已經不在了 | §4.7 |
| 7 | `AutoFill` 一旦 true 不可逆，使用者委託後想回頭細談沒有路 | §5.4 |

根因一句話：**設計把「回合結束」和「狀態機推進」綁成同一件事**。對話本身不是一個合法狀態，每講一句話都要付一次流程推進的代價。

另外在推導過程中發現一個既有缺陷，跟多輪無關但必須一起修：`AskUser` 的簽名是一個 `question` 字串配一組扁平 `suggestedOptions`，**一次只能問一件事**；但使用者少講時缺的是好幾個維度（「一個少女」六缺五）。2 次 × 1 個問題到不了。Python 端的單輪 demo 早就是「每個缺的維度各一則、各 2 個對比方向」的形狀（[單輪流程說明 §8](../../單輪流程說明.md)），C# 端的 `AskUser` 卻不是。

## 2. 目標與非目標

**目標**

- 使用者發起的討論／提問，LLM 能直接回答，不消耗追問額度、不偽造追問 UI。
- LLM 主動打斷使用者的次數維持硬上限 2，不重設、不豁免。
- `Collecting` 期間「一直不交東西」有硬上限；上限到了強制交一版，不是拒絕回答。
- `Finalized` 之後可以無限討論，只有真的動到 prompt 才出新定稿卡。
- `AskUser` 一次能問多個維度，2 次額度足以覆蓋六維度全缺的最壞情況。
- 使用者回頭引用先前的選項時，LLM 拿得到 snippet 內容，不用重撈或編造。
- 新增的輸出面（討論內容、選項）走跟定稿同一道輸出側安全過濾。
- 所有硬限制由程式碼保證，不靠 prompt 約束，也不靠輸入分類器的判斷（沿用主規格 §4.1）。

**非目標**

- 不改 `AskCount` 的規則。
- 不讓 `AutoFill` 可逆（§10 說明為什麼不需要）。
- 不加 session 總輪次上限（純成本護欄，既有設計就沒有）。
- 不動 Python 管線、`demo.py`、資料模型。
- 前端只寫預設行為，細節屬子專案 3。

## 3. 狀態機與護欄

### 3.1 設計原則

兩個目標各由一個獨立機制保證，中間沒有耦合：

| 目標 | 機制 |
| :--- | :--- |
| LLM 不無限追問 | `AskCount` 上限 2，一字不改 |
| 使用者能繼續對話 | 新的終止型 tool `Discuss`，不碰 `AskCount` |

曾考慮過「使用者發起提問時重設或豁免 `AskCount`」，否決，理由見 §10。

### 3.2 Session 欄位

```text
Session {
  Id, CreatedAt
  Status:         Collecting | Finalized
  Profile:        portrait | landscape | object | vehicle | null
  AskCount:       int        // 語意收窄：LLM 未受邀請的主動打斷次數。上限 2，永不重設
  DiscussStreak:  int        // 新增：未定稿的 Discuss 連續次數。只由 FinalizePrompt 歸零
  AutoFill:       bool
  FacetStates:    Dictionary<facetId, FacetState>
  ChatHistory:    SK ChatHistory
  PresetLedger:   Dictionary<presetId, LedgerEntry>   // 新增，見 §6.1
  LastFinal:      { Positive, Negative, Tips }?
  Lock:           SemaphoreSlim(1)
}
```

### 3.3 `ToolSetBuilder.Build(session, userMessage, guardResult)`

```text
永遠註冊：  SearchSimilarPrompts, SearchPresets, SetProfile,
           SetFacetStates, FinalizePrompt

AskUser：            Status == Collecting
                 且  AskCount < MaxAskCount (2)
                 且  !guardResult.wantsAutoComplete          ← 沿用現行

Discuss：            !guardResult.wantsAutoComplete
                 且 (Status == Finalized
                     或 DiscussStreak < MaxDiscussStreak (8))

RequestSaveConsent： Status == Finalized
```

`wantsAutoComplete` 命中的那一輪同時移除 `AskUser` 與 `Discuss`：「你決定」的語意就是「直接給我」，工具清單只剩 `FinalizePrompt` 與檢索／設定類，LLM 只能立即定稿。否則 LLM 會回一句「好的，我來幫你決定」就結束回合，使用者得再送一句才拿得到東西。

`Discuss` 不需要 `Profile`。主規格 §4.6 對 `AskUser` / `FinalizePrompt` 在 `Profile == null` 時擋回要求先 `SetProfile`；`Discuss` 不受此限，使用者第一句就問「這個怎麼用？」時 LLM 要能直接回答。

### 3.4 狀態轉移

| 事件 | `AskCount` | `DiscussStreak` | `Status` |
| :--- | :--- | :--- | :--- |
| `AskUser` 成功 | +1 | 不變 | Collecting |
| `Discuss` 成功（Collecting） | 不變 | +1 | Collecting |
| `Discuss` 成功（Finalized） | 不變 | 不變 | Finalized |
| `FinalizePrompt` 成功 | 不變 | **歸零** | Finalized |
| `SetProfile` 切換 | 不變 | 不變 | 不變 |
| 任何 tool 被 `OutputSafetyFilter` 攔截 | 不變 | 不變 | 不變 |

`DiscussStreak` 只由 `FinalizePrompt` 歸零，`AskUser` 不歸零。這讓 `Collecting` 期間的未定稿回合有一個好講的上限：**最多 8 次 `Discuss` + 2 次 `AskUser` = 10 輪**，之後工具清單只剩 `FinalizePrompt`，強制交出一版。跟主規格 §4.6「tool 預算耗盡 → 強制定稿」同一個機制。

`SetProfile` 不重置 `DiscussStreak`，跟 `AskCount` 同理——防死循環針對的是系統。

### 3.5 護欄的語意

`Finalized` 之後 `Discuss` 不受 streak 限制。護欄的目的是「確保使用者拿得到東西」；東西交出去了就沒有要保護的對象，使用者想對著定稿聊多久是他的事。

護欄擋的是「一直不交東西」，不是「一直講話」。踩到上限時 LLM 不是拒絕回答，是被迫先交一版；交出去的那一刻 `Discuss` 立刻解除限制。

## 4. Tool 契約

### 4.1 `DialogPlugin.Discuss`（新增，終止型）

```text
Discuss(
  message:     string,                                   // 繁中回覆內容
  options:     [{ label: string, tags: string, presetId: int? }]?,   // 0–4 個參考方向
  facetStates: Dictionary<string, FacetState>            // 必填
)
```

**語意：回應。** 這是我對你問題的回答；你可以無視它繼續講別的。

`facetStates` 必填，跟 `AskUser` / `FinalizePrompt` 一致（主規格 §4.2）。這跟「不宣告 facet」不衝突，兩件事要分開看：

| | `missingFacetIds` | `facetStates` |
| :--- | :--- | :--- |
| 語意 | **宣告需求**：我需要這幾項才能繼續 | **同步事實**：目前每一項是什麼狀態 |
| `AskUser` | 有 | 有 |
| `Discuss` | **沒有** | **有** |
| 前端反應 | 該維度高亮 | chip 填色更新 |

`Discuss` 沒有前者。後者必須留，否則使用者在討論裡說「那就寫實」時，`Discuss` 是終止型、這一輪就結束了，儀表板會停在舊狀態。

`options[].presetId` 可為 null：LLM 可以提出知識庫裡沒有的方向，由 §5.2 的輸出過濾兜住。

### 4.2 `DialogPlugin.AskUser`（改版）

```text
AskUser(
  preamble:    string,                                   // 一句開場
  asks:        [{                                        // 1–3 則
    dimension:       string,                             // style | scene | camera | appearance | pose | outfit
    question:        string,
    missingFacetIds: string[],
    options:         [{ label: string, tags: string, presetId: int? }]   // 2–4 個，必須不同方向
  }],
  facetStates: Dictionary<string, FacetState>
)
```

**語意：索取。** 我需要你回答才能繼續。

| 旋鈕 | 值 | 理由 |
| :--- | :--- | :--- |
| `asks` 每次上限 | 3 個維度 | 3 × 2 次 = 6，剛好覆蓋六維度全缺的最壞情況 |
| `options` 每維度 | 2–4 個 | 沿用 demo 規則；且必須不同方向（寫實／動漫），不是同方向的兩種說法 |
| `MaxAskCount` | 維持 2 | 形狀修好之後 2 次就夠 |

LLM 該先問哪三個維度由 system prompt 引導（風格是最大的槓桿，穿著通常最不影響畫面），不由程式碼決定。

`options` 的形狀與 `Discuss.options` 共用。

### 4.3 後端清洗（不信 LLM 自述）

沿用單輪 demo `normalize_queries` / `validate_suggestions` 的精神：程式只修剪與過濾，不改語意，被拒的理由寫 audit。

**`AskUser.asks`**

| 情況 | 處理 |
| :--- | :--- |
| `asks` 超過 3 則 | 只留前 3 |
| 某則 `options` 超過 4 個 | 只留前 4 |
| 某則 `options` 少於 2 個 | 該則移除 |
| `missingFacetIds` 含 session 現值非 `missing` 的 facet | 過濾掉 |
| `missingFacetIds` 含不屬於 `dimension` 的 facet | 過濾掉 |
| 過濾後某則 `missingFacetIds` 為空 | 該則移除 |
| 全部移除、`asks` 為空 | 不終止，回結構化錯誤要 LLM 重呼叫；計入 tool 預算 |

**`AskUser.asks[].options` 與 `Discuss.options`**

| 情況 | 處理 |
| :--- | :--- |
| `presetId` 非 null 但不在 `PresetLedger` | 降級為 null，記 audit |
| `Discuss.options` 超過 4 個 | 只留前 4 |

「不在 ledger 就降級」跟 demo 的 `validate_suggestions`（來源不在檢索結果 → 移除整個選項）不同：這裡降級不移除，因為 `presetId` 本來就可為 null。

**`Discuss.facetStates` 在 `Profile == null` 時**：忽略，不更新 session、不發 `dimensions` 事件，記 audit。

### 4.4 `Finalized` 之下 `Discuss` 不得變更 facet 狀態

定稿後使用者說「風格改成動漫」，LLM 可能 `Discuss` 回「好的」並帶著改過的 `facetStates`，但沒有 `FinalizePrompt`——儀表板變了、定稿卡沒變。任何 facet 變動都意味著 prompt 該重組。

程式碼保證：`Status == Finalized` 且 `Discuss.facetStates` 與 session 現值不同 → `TerminalToolFilter` 不終止，回結構化錯誤「facet 狀態有變更，請改用 `FinalizePrompt`」，計入 tool 預算。跟主規格 §4.6 的 `Profile == null` 處理同一個模式。

## 5. 協定與 Filters

### 5.1 SSE `final` 事件（主規格 §10.2 改寫）

```text
{ kind: "ask",       preamble, asks: [{ dimension, question, missingFacetIds, options }] }
{ kind: "message",   message, options? }                                     ← 新增
{ kind: "finalized", positive, negative, tips }
{ kind: "save_consent_requested" }
```

`options` 的每筆是 `{ label, tags, presetId? }`。`dimensions` 事件不變，`Discuss` 一樣會發（帶 `facetStates`）。

`Discuss.message` 不會有打字機效果：它是 tool call 的參數，一次到位。這跟 `AskUser` / `FinalizePrompt` 現況一致，不是新問題；前端不要對 `message` 期待 `token` 事件。

### 5.2 Filters（主規格 §4.5、§6.2 改寫）

| Filter | 改動 |
| :--- | :--- |
| `TerminalToolFilter` | 加入 `Discuss`；§4.4 的 `Finalized` 下 facet 變更檢查放在這裡 |
| `ToolBudgetFilter` | 不變 |
| `AuditFilter` | 不變 |
| `OutputSafetyFilter` | **擴大範圍**：除了 `FinalizePrompt.positivePrompt`，也檢 `Discuss.message`、`Discuss.options[].label/tags`、`AskUser.asks[].question`、`AskUser.asks[].options[].label/tags` |

擴大的理由是主規格 §6.2 原文：「防止無害輸入配上 preset 組出不當內容」。`options` 直接來自 `prompt_knowledge_presets`，走的是跟定稿一模一樣的來源；輸入側擋不到（輸入無害），輸出側原本也沒擋。這是多輪設計暴露出一個新的輸出面，不是既有防線的問題；§6.3 管線層的決議不動。

代價是每個討論回合多一次 Gemini Flash 分類呼叫。討論回合本來就不跑六次 `SearchPresets`，延遲在這種輪次裡幾乎看不出來。

**命中時**：發 `blocked` 事件、Terminate、任何計數器都不動（不算 streak、不算 ask）、寫 audit。跟輸入側「不計 `AskCount`」對稱。

### 5.3 純文字違規補救（主規格 §4.6 第一列改寫）

現行：仍為純文字 → 若 `AskUser` 可用就包成 `AskUser`（`AskCount++`），否則 `error`。

改為：

> LLM 回純文字、未呼叫終止 tool → 補一則系統提示重試一次。仍為純文字：若 `Discuss` 本輪可用，包成 `Discuss`（`message` = 原文，`options` 留空，`facetStates` 用 session 現值原樣填回，`DiscussStreak++`）；否則發 `error` 事件。

LLM 吐散文時想做的九成是講話，不是追問；包成追問會憑空生出追問氣泡與 chip，還燒掉一次 `AskCount`。

順帶：`Finalized` 之後 `Discuss` 永遠可用，所以定稿後這條路徑永遠不會掉到 `error` 分支。

### 5.4 前端預設行為（子專案 3 可改）

- `kind: "ask"` 渲染成一張追問卡，每個維度一區、各自一排 chip，該維度在儀表板高亮。
- `kind: "message"` 渲染成一般對話氣泡；`options` 若有，渲染成比追問 chip 更輕的「參考方向」列表，儀表板不高亮。
- chip 點選是**填入輸入框可累積**，不是點了就送；否則三個維度要送三次。
- chip 送出時帶維度前綴：`[風格] 寫實攝影`，讓 LLM 能對回 `asks` 的哪一則。
- `options[].presetId` 非 null 的選項可點開 preset 抽屜（主規格 §11.1 已有）。
- 任何 `error` / `blocked`：失敗的訊息保留顯示並標記原因，附「重試」按鈕；按下把原文填回輸入框，使用者可改可直接送（§5.6）。

### 5.5 上游內容攔截與重試（新增）

Gemini 有可能對某些輸入**完全不回應**：HTTP 200，但 `candidates` 是 `null`、
`promptFeedback.blockReason` 有值、`safetyRatings` 與 `blockReasonMessage` 都是 `null`。
這跟 `OutputSafetyFilter` 是兩回事 —— 那是我們檢查 LLM 的輸出，這是 LLM 根本拒絕產出。

實測（2026-09-22，用子專案 1 的 `demo.py`，語料庫 19,354 筆 presets，每格 3 次）：

| 查詢 | 被擋 |
| :--- | ---: |
| 少女＋熱褲 | 3/6 |
| 成年女性＋熱褲 | 2/6 |
| 少女＋泳裝 | 2/3 |
| 成年女性＋泳裝 | 0/3 |
| 少女／男性／風景／載具，一般服裝 | 0/18 |

三個結論：

1. 觸發因子是**暴露性服裝**，不是年齡用詞。加「成年」有幫助但不消除。
2. 攔截發生在**組裝**階段——送進去的 prompt 含檢索到的候選片段。分析階段從沒被擋過。
3. 是**機率性**的，同一份輸入重送有時會過。

#### 重試規則（C# client 層必須實作）

| 情況 | 判定 | 處置 |
| :--- | :--- | :--- |
| `blockReason` 或 `finishReason` 屬 `PROHIBITED_CONTENT`／`SAFETY`／`BLOCKLIST`／`JAILBREAK`／`IMAGE_SAFETY`／`MODEL_ARMOR` | 內容攔截 | **重試 1 次**，仍被擋才交給使用者 |
| `finishReason = MAX_TOKENS`；或空 `candidates` 且無 `blockReason` | 與內容無關 | 重試，預設 3 次 |
| 429／5xx／逾時 | 傳輸 | 既有的指數退避重試 |

**內容攔截重試一次，不多。** 攔截是機率性的：實測同一份 SFW 內容也會被誤擋，重送一次常會過。
送到 Gemini 的內容已經先過了 §6.1 輸入側 `SafetyGuard`——是我們自己判定可接受的東西，
上游攔截是第二個分類器的第二次意見，容忍它一次誤判是合理的。

但只能一次。對同一份輸入重送到通過為止，等於利用分類器的不確定性規避安全判定，
而實測顯示這裡的判定牽涉未成年與暴露服裝的組合——這條界線兩次仍被擋就交給使用者，
由人決定要不要改寫。`safetySettings` 也不是出口：`PROHIBITED_CONTENT` 不在可調的 `HarmCategory` 之列。

#### 攔截時的對話行為

比照 §5.2 `OutputSafetyFilter` 命中：發 `blocked` 事件、Terminate、**session 回滾至本輪開始前**（§5.6）。
回滾涵蓋「任何計數器都不動」（`DiscussStreak`、`AskCount`、tool 預算），還多了一件事：被擋的那句話不留在
history——否則下一輪 LLM 會看到它，可能再觸發一次。使用者不該因為上游攔截損失輪次。

訊息要說清楚三件事：這是上游模型的判定**不是程式錯誤**、**不是知識庫的問題**、
**下一步在使用者手上**（由人決定要不要改寫自己的需求）。

#### Audit

`event_type = 'Blocked_Upstream'`，`payload` 記 `blockReason` 與發生階段。必須與既有的
`Blocked_NSFW`（我們自己擋的）**分開**——兩者混在一起會讓「合規」指標失真：一個是我們的
防線生效，一個是我們把上游不接受的東西送出去了。

#### 參考實作

`scripts/pipeline/gemini_client.py` 的 `UnusableResponse.is_content_block` 與
`generate_structured` 的重試條件（`CONTENT_BLOCK_ATTEMPTS = 1`）；使用者訊息見 `scripts/demo.py::unusable_message`。

### 5.6 錯誤復原：回滾、內部重試、手動重試（新增）

Gemini 會出錯：傳輸層（429／5xx／逾時）、空回應（§5.5 的非攔截情形）、內容攔截（§5.5）。
主規格 §4.6 對逾時說「session 狀態回滾到本輪開始前」，但只針對逾時；其他失敗要嘛回結構化錯誤給 LLM，
要嘛沒講。多輪對話下一個 session 可能累積十幾輪的狀態，**任何一種失敗都不能把它毀掉**。

#### 一輪是一個交易

```text
RunTurnAsync(session, text, ct):
  snapshot = session.Snapshot()     // AskCount, DiscussStreak, Status, Profile, AutoFill,
                                    // FacetStates, PresetLedger, ChatHistory.Count
  try:
    agent loop …
    終止型 tool 成功 → 交易成立，snapshot 丟棄
  catch (任何失敗，含 ct 取消):
    session.Restore(snapshot)
    發 error 或 blocked 事件
```

回滾之後 session 跟這一輪沒發生過一樣：使用者訊息不在 history、計數器沒動、ledger 沒多東西。
這把主規格 §4.6 的逾時回滾與 §5.5 的「計數器不動」一般化成同一個機制——不用逐項講哪個不動，整個 session 都回去了。

`PresetLedger` 也回滾。重試會重撈，代價是一次 embed + 幾條 SQL，換來「一輪 = 原子」這個好講的性質。
`ChatHistory` 的 snapshot 是訊息數、restore 是截回該長度；其餘欄位淺複製，都很便宜。

#### 內部重試：三層，對應 Python 端的三層

| 層 | 觸發 | 次數 | 對應 `gemini_client.py` |
| :--- | :--- | :--- | :--- |
| 傳輸 | 429／5xx／逾時 | **3 次**，退避 1s／2s／4s | `_call` 的 6 次。互動場景每輪有 3–4 次上游呼叫，6 次退避會吃掉整輪預算 |
| 空回應 | 200 但無可用內容、`MAX_TOKENS`、無 `blockReason` | **3 次** | `UNUSABLE_ATTEMPTS = 3` |
| 內容攔截 | §5.5 的那組 reason | **1 次** | `CONTENT_BLOCK_ATTEMPTS = 1` |

分類邏輯放在一個 `IChatCompletionService` 的 decorator 裡，跟 connector 無關——§4.8 說 connector 還沒選，
而 Google connector 與 OpenAI 相容端點回「被擋」的形狀不同（前者 `promptFeedback.blockReason`，
後者大概是 `finish_reason: content_filter`）。這是 C# 版的 `_response_problem`。

**逾時跟著調**：單輪 60s → **120s**。一輪有 1 次 embed + 2–3 次 LLM 呼叫，每次最多重試 3 次加退避，60s 不夠。
退避等待吃同一個 `CancellationToken`：token 一到就停止重試、回滾，不會出現「逾時了還在退避」。

| 組態鍵 | 預設 |
| :--- | :--- |
| `Llm:TransportRetries` | 3 |
| `Llm:UnusableRetries` | 3 |
| `Llm:ContentBlockRetries` | 1 |
| `Orchestrator:TurnTimeoutSeconds` | 120 |

#### 手動重試：不加端點、不分原因

內部重試耗盡或被攔截 → 回滾 → 發事件。session 已經回到本輪開始前，**再送一次同一段文字跟第一次送完全等價**：
不需要 `/retry` 端點，也沒有「重試會不會重複計數」的問題——沒有東西可以被重複計。

事件不分 `error` 或 `blocked`，前端一律：失敗的訊息保留顯示並標記原因，附「重試」按鈕；按下把**原文填回輸入框**，
使用者可改可直接送。輸入什麼是使用者的決定——不給按鈕他也只是重打一次，區分只是做樣子。

這跟 §5.5「內容攔截只重試一次」不衝突：那條管的是**系統自動**重送的上限；使用者自己決定再送是他的判斷，
一次一則，每則都有 audit。

`error` 事件維持主規格 `{ code, message }`，不加 `retryable` 欄位。

#### 前端也要回滾

失敗前已串出去的 `tool_call` 卡片、`dimensions` 更新都是這一輪的半成品。主規格 §11.3 的 reducer 是純函式，
做法對稱：收到 `session` 事件（輪次開始）時 snapshot store，收到 `error` / `blocked` 時 restore。
後端回滾、前端回滾，兩邊一致。

#### Audit

一輪失敗寫**一筆** `Turn_Failed`，`payload` 記 `{ stage, errorClass, attempts }`。每次內部重試不各寫一筆，會淹掉有用的紀錄。
`Blocked_Upstream` 維持 §5.5 的定義，不併進來。

## 6. 對話記憶

### 6.1 `Session.PresetLedger`

[分維度檢索設計 §11](2026-09-22-dimension-scoped-retrieval-design.md#11-已知限制與後續) 已為跨維度去重要求一個 session ledger 累積 `presetId → [(dimension, dist, grounded)]`。直接把它加厚，不新增第二個帳本：

```text
LedgerEntry {
  Title, PromptSnippet, NegativeSnippet, FacetIds, ImageUrl
  Hits:      [(dimension, dist, grounded)]       // 既有：去重歸屬用
  OfferedAs: [(turnIndex, dimension?, label)]    // 新增：曾攤給使用者看過
}
```

- 每次 `SearchPresets` 回來就寫入（append，不覆蓋既有 `Hits`）。
- `AskUser` / `Discuss` 成功後，其 `options` 中 `presetId` 非 null 者寫入該 preset 的 `OfferedAs`。
- 只收 presets。`SearchSimilarPrompts` 撈的是 `shared_prompt_histories`，id 空間不同，而且「相似作品」是參考不是選項，不會被回頭引用；它維持主規格 §4.7 的壓縮規則，不進 ledger。

### 6.2 System prompt 注入

主規格 §4.9 的 system prompt 本來就每輪由 template + session 事實重組。多加一段：

```text
## 你先前提供過的選項（使用者可能回頭引用）
[T1] 風格 A. 寫實攝影風格 (preset 412) — photo realism, photorealistic
[T1] 風格 B. 日系動漫風格 (preset 88)  — anime, Anime art
[T3] 鏡頭 A. 低角度仰視   (preset 201) — low angle, from below
```

- 只帶 `OfferedAs` 非空的 preset，不帶全部檢索結果。使用者不會說「回到你第 17 個檢索結果」，只會說「回到你給我的那個厚塗油畫」。
- 按最近 offered 的 `turnIndex` 排序，取前 24 個 preset（3 維度 × 4 選項 × 2 次 `AskUser` 的最壞情況）。
- 一筆一行，token 成本可控。

### 6.3 Chat history 修剪與截斷（主規格 §4.7 改寫）

主規格 §4.7 只壓 tool result，沒有整體截斷。`Finalized` 之後 `Discuss` 不限次，history 就沒有上限了；而 `options` 改成結構化之後，call args 每輪都留在 history 裡，越積越肥。

兩條：

1. **call args 也壓**：該輪結束後，`AskUser.asks[].options` 與 `Discuss.options` 壓成 `[{label, presetId}]`，去掉 `tags`。完整內容 ledger 有。
2. **整體截斷**：保留 system message + 最近 10 輪（一輪 = 一則 user message 起到終止型 tool 止），更早的丟掉。`PresetLedger`、`FacetStates`、`LastFinal` 是 session 事實，不靠 history 記住，所以丟掉是安全的。

tool result 的壓縮規則不變。

### 6.4 借用驗證的範圍

單輪 demo 的 `validate_borrowed` 第一道檢查是「這個 preset 在**本次檢索結果**內嗎」。多輪之下「本次」沒有定義：第 2 輪撈到的 preset，第 6 輪定稿時算不算數？

**範圍 = 整個 `PresetLedger`，不是本輪。** 否則使用者聊了五輪之後定稿，前面撈到的東西全部失去借用資格，LLM 只能重撈或硬掰。跨維度去重歸屬（分維度檢索設計 §5.3）同樣在定稿時對整個 ledger 的 `Hits` 套用。

## 7. 一個典型對話

以「一個銀髮少女站在雨夜的霓虹街頭」為例，追蹤計數器。

| 輪 | 使用者 | LLM | `Ask` | `Streak` | `Status` | 畫面 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | 一個銀髮少女站在雨夜的霓虹街頭 | `SetProfile` → 六維度 `SearchPresets` → **`AskUser`**（風格／鏡頭／穿著三則） | 1 | 0 | Collecting | 追問卡三區、儀表板三維度高亮 |
| 2 | 寫實跟動漫差在哪？ | **`Discuss`**（解說 + 參考方向：寫實／動漫／厚塗油畫） | 1 | 1 | Collecting | 對話氣泡、儀表板不動 |
| 3 | 那就寫實。鏡頭我沒想法，你有建議嗎？ | `SetFacetStates(style.genre=covered)` → `SearchPresets(camera)` → **`Discuss`** | 1 | 2 | Collecting | 風格轉實心（由 `SetFacetStates` 驅動） |
| 4 | 低角度。穿著隨便 | `SetFacetStates(camera.angle=covered, outfit.* note=委託)` → **`FinalizePrompt`** | 1 | 0 | **Finalized** | 定稿卡；`AskUser` 永久消失、`Discuss` 解除 streak |
| 5 | negative 裡的 `blurry` 是幹嘛的？ | **`Discuss`** | 1 | – | Finalized | 對話氣泡，**沒有新定稿卡** |
| 6 | 把背景改成黃昏 | **`FinalizePrompt`** | 1 | – | Finalized | 新定稿卡 |
| 7 | 存起來 | **`RequestSaveConsent`** | 1 | – | Finalized | 確認卡片 → 前端按鈕打 `/save-to-shared` |

第 2 輪是舊設計的死路；第 5 輪在舊設計會吐一張新定稿卡。

護欄踩到的情況：使用者在 `Collecting` 一路聊不做決定，第 8 次 `Discuss` 之後 `ToolSetBuilder` 不再註冊 `Discuss`；`AskCount` 若也滿了，LLM 只剩 `FinalizePrompt`。交出去之後 `Status = Finalized`，想繼續聊從第 5 輪那條路走。

## 8. 測試

### 8.1 單元測試（主規格 §12.1 補列）— 純函式，不呼叫 LLM

`ToolSetBuilder.Build`：
- `Collecting` 且 `DiscussStreak < 8` → 有 `Discuss`；`= 8` → 無
- `Finalized` 且 `DiscussStreak = 8` → 仍有 `Discuss`
- `wantsAutoComplete` 命中 → 無 `AskUser` 也無 `Discuss`
- `AskCount = 2` 且 `DiscussStreak = 8` 且 `Collecting` → 只剩永遠註冊的那五個

Session 狀態機：
- `Discuss` 成功：`Collecting` 下 `DiscussStreak++`，`Finalized` 下不變；兩者 `AskCount` 皆不變
- `FinalizePrompt` 成功 → `DiscussStreak = 0`
- `AskUser` 成功 → `DiscussStreak` 不變
- `SetProfile` → `DiscussStreak` 不變
- `OutputSafetyFilter` 攔截 → 所有計數器不變

`AskUser.asks` 清洗（§4.3 表格每一列一個案例）：截斷、過濾非 missing、過濾跨維度、空則移除、全空回錯誤。

`options` 清洗：`presetId` 不在 ledger → null。

`Finalized` 下 `Discuss` 帶變更的 `facetStates` → 拒絕、不終止、計預算。

`Profile == null` 下 `Discuss` 的 `facetStates` → 忽略、不發 `dimensions`。

純文字補救：以 fake `IChatCompletionService` 回純文字，驗證包成 `Discuss` 而非 `AskUser`；`Discuss` 不可用時發 `error`。

`PresetLedger`：
- `OfferedAs` 注入取前 24、按最近 turn 排序
- `Hits` append 不覆蓋
- histories 不進 ledger

history：
- call args 壓縮後 `options` 無 `tags`
- 超過 10 輪時最舊的被丟，system message 保留

錯誤復原（§5.6）：
- 任一階段拋例外 → session 所有欄位等於 snapshot，`ChatHistory.Count` 回到輪次開始，`PresetLedger` 無本輪新增
- 終止型 tool 成功後再拋例外（例如 SSE 寫入失敗）→ 不回滾，狀態已提交
- decorator 分類：內容攔截 reason → 重試 1 次，仍被擋才拋 `UpstreamBlocked`，第二次通過則正常回傳；空回應 → 重試至 `UnusableRetries` 次；429／5xx → 重試至 `TransportRetries` 次；每一類用 fake `IChatCompletionService` 各一案例，內容攔截要有「第二次過」與「第二次仍擋」兩案
- 退避等待中 `CancellationToken` 取消 → 立即停止、回滾，不再打下一次
- 失敗後重送同一段文字 → 計數器、history 長度、ledger 與首次送出時完全相同
- 失敗一輪只寫一筆 `Turn_Failed`，`attempts` 等於實際嘗試次數

### 8.2 契約測試（主規格 §12.2）

`Discuss` 與改版 `AskUser` 的結構化輸出形狀：真打 Gemini，只斷言 schema。

### 8.3 人工 eval（主規格 §12.3 補列）

1. 中途提問（「寫實跟動漫差在哪？」）→ LLM 走 `Discuss`，`AskCount` 不變，前端是對話氣泡不是追問卡
2. 定稿後討論（「`blurry` 是幹嘛的？」）→ 沒有新定稿卡
3. `Collecting` 一路聊到 streak 踩滿 → 強制定稿 → 之後還能繼續聊
4. 回頭引用先前選項（「厚塗油畫那個具體會加哪些 tag？」）→ LLM 回答內容與 ledger 裡的 snippet 一致，沒有重撈也沒有編造
5. 「一個少女」六缺五 → 第一次 `AskUser` 問三個維度、第二次問剩下的
6. 「都你決定」→ 該輪直接定稿，沒有中間的 `Discuss`
7. 定稿後說「風格改成動漫」→ LLM 用 `FinalizePrompt` 不是 `Discuss`（或被 §4.4 拒絕後改用）
8. 以 fake connector 讓第二次 LLM 呼叫 500 兩次後成功 → 使用者無感，audit 無 `Turn_Failed`
9. 讓它連續失敗超過重試次數 → `error` 事件、儀表板回到輪次開始、按「重試」後正常完成、`AskCount` 只算一次
10. 送出會觸發上游攔截的描述 → `blocked` 事件、訊息保留、按「重試」原文回到輸入框、改寫後送出正常完成

## 9. 主規格改寫清單

實作計畫的第一項任務是把以下段落改進主規格，讓它成為單一事實來源；本文件之後降為「推導紀錄」。

| 主規格段落 | 改動 |
| :--- | :--- |
| §4.2 Plugins 與 Tools | `AskUser` 換成 §4.2 的簽名；新增 `Discuss` 列；`options` 形狀說明 |
| §4.3 工具清單組裝規則 | 換成 §3.3；`wantsAutoComplete` 命中同時移除 `Discuss` |
| §4.4 Session 狀態機 | 加 `DiscussStreak`、`PresetLedger`；狀態轉移表換成 §3.4；「`Finalized` 後使用者要求修改」那條補「純討論走 `Discuss`」 |
| §4.5 Filters | `TerminalToolFilter` 加 `Discuss` 與 §4.4 檢查；`OutputSafetyFilter` 範圍換成 §5.2 |
| §4.6 失敗模式處理 | 第一列換成 §5.3；新增「`Finalized` 下 `Discuss` 變更 facet」與「`asks` 清洗後為空」兩列；逾時那列的回滾一般化成所有失敗（§5.6）；新增 LLM 呼叫三層重試列；逾時 60s → 120s |
| §4.7 Chat history 修剪 | 換成 §6.3 |
| §7 資料模型 | `audit_logs.event_type` 加 `Turn_Failed`、`Blocked_Upstream` |
| §4.9 System prompt | 加 §6.2 的注入段；加兩條要求：使用者描述題材時不得 `Discuss` 要推進；`Finalized` 後 facet 有變必須 `FinalizePrompt` |
| §5.4 Facet 四態 | 「`AutoFill` 一旦為 true 保持」後補一句：使用者透過討論把某項變成 `covered` 或 `waived`，`AutoFill` 即管不到它 |
| §6.2 輸出側 | 範圍換成 §5.2；命中時的計數器規則 |
| §10.2 SSE 事件 | `final` 換成 §5.1；`error` / `blocked` 的前端反應改為「保留訊息 + 重試按鈕」（§5.6） |
| §11.1 版面 | 追問卡多維度版；`message` 氣泡與參考方向；chip 累積與前綴；失敗訊息的重試按鈕（§5.4） |
| §11.3 狀態管理 | reducer 加 turn snapshot / restore（§5.6） |
| §12.1 / §12.3 | 補 §8.1 / §8.3 |
| §14 子專案 2 驗收 | 「追問 → 回答 → 定稿」改成「追問 → 討論 → 回答 → 定稿 → 討論 → 修改」 |
| §15 決定紀錄 | 加 §10 的各條 |

## 10. 決定紀錄

| 項目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 使用者提問時重設或豁免 `AskCount` | **否決** | 重設把「系統的打斷額度」跟「使用者的參與度」綁在一起，兩者沒有因果關係；使用者要的是「能繼續對話」，不是「讓 LLM 多問我兩次」。豁免則要靠分類器判斷「這句是提問還是回答」，邊界模糊（「你覺得寫實比較好嗎？我選寫實」兩者皆是），把閘門建在分類器上等於把硬保證降級成猜測——跟主規格 §4.3 拒絕關鍵詞比對是同一個理由 |
| `Discuss` 帶不帶選項 | 帶「參考方向」，不帶 `missingFacetIds` | 純文字的討論體驗差（「再多給我幾個方向」只能收到散文）；界線靠「索取 vs 回應」的語意與 streak 護欄守住 |
| `Discuss` 帶不帶 `facetStates` | 帶，必填 | 終止型工具是該輪最後一次同步狀態的機會；不帶則討論期間儀表板變死的。這跟「不宣告需求」是兩回事 |
| `DiscussStreak` 上限 | 8 | 使用者指定。`Collecting` 期間最多 10 個未定稿回合 |
| `DiscussStreak` 誰歸零 | 只有 `FinalizePrompt` | 讓上限可以講成一個數字；`AskUser` 不代表進展 |
| `Finalized` 後 `Discuss` 限不限次 | 不限 | 護欄的目的是確保交出東西，交了就功成身退 |
| `AskUser` 多維度 | 一次最多 3 則 | 3 × 2 = 6 覆蓋最壞情況；一張卡 12 個 chip 分三區不至於糊掉 |
| `OutputSafetyFilter` 範圍 | 全檢（A 方案） | §6.2 原文的理由對 `options` 一字不差地成立；合規是對外賣點，出口不一致難講 |
| `presetId` 不在 ledger | 降級為 null，不移除 | `presetId` 本來就可為 null；輸出過濾兜住自由發明的內容 |
| `AutoFill` 可逆 | **不做** | 有了 `Discuss` 之後問題自己解掉：`Discuss` 不看 `AutoFill`，使用者透過討論把某項變成 `covered` 或 `waived`，那一項就不在 `AutoFill` 補齊的範圍內。`waived` 本來就是為「即使委託也不補」存在的 |
| 純文字補救的預設目標 | `Discuss` | LLM 吐散文時九成是想講話，不是追問 |
| ledger 只收 presets | 是 | histories 是參考不是選項，不會被回頭引用 |
| history 截斷 | 最近 10 輪 | session 事實都在 ledger / `FacetStates` / `LastFinal`，history 只需最近脈絡 |
| session 總輪次上限 | 不加 | 純成本護欄，既有設計就沒有，不在本次範圍 |
| 一輪失敗的處理 | 回滾至本輪開始前，所有失敗一律 | 一般化主規格 §4.6 的逾時回滾；「不全毀」靠原子性保證，不靠逐項列舉哪個欄位不動 |
| 手動重試 | 不加端點；不分 `error` / `blocked` 一律給重試按鈕，原文填回輸入框 | session 已回滾，重送等價首次送出；不給按鈕使用者也只是重打一次，區分是做樣子。§5.5 的「只重試一次」管的是系統自動重送的上限，不是使用者的決定 |
| 上游內容攔截的內部重試 | 1 次（原 0 次） | 使用者實測同一份 SFW 內容會被誤擋，重送一次常會過；送到上游的內容已先過我們自己的 `SafetyGuard`，容忍第二個分類器一次誤判合理。上限 1 是為了不變成「重送到過為止」——那才是規避安全判定 |
| 傳輸重試次數 | 3（Python 管線是 6） | 互動場景每輪 3–4 次上游呼叫，6 次退避會吃掉整輪預算 |
| 單輪逾時 | 120s（原 60s） | 給三層重試留空間；退避吃同一個 `CancellationToken`，不會逾時了還在等 |

## 11. 已知限制

- **`asks` 該先問哪三個維度靠 prompt 引導**，程式不排序。LLM 若先問穿著後問風格，2 次額度會浪費在低槓桿的維度上。
- **「使用者描述題材時不得 `Discuss`」只有 prompt 約束**。LLM 在第一輪就 `Discuss` 閒聊是可能的，由 streak 兜底，代價是多一輪。
- **`Discuss` 的 `options` 是 LLM 自己挑的**，程式只驗 `presetId` 存在，不驗 label 跟 preset 內容相符。跟單輪 demo 的「借用驗證是字面比對」同一類限制。
- **history 截斷 10 輪是拍腦袋的數字**，要看 eval 才知道對不對。
- **每個討論回合多一次分類呼叫**是 A 方案的固定成本，長對話的成本線性成長。
- **重試按鈕對確定性攔截無效**：輸入側 denylist 命中，原文重送必然再被擋。按鈕留著是為了行為一致，使用者按了會再看到同一則攔截訊息，然後自己改。
- **內容攔截重試一次會提高邊緣內容的通過率**。§5.5 實測表裡「少女＋熱褲」3/6 被擋，重試一次後被擋機率約降到 1/4。對誤擋的 SFW 內容這是修正，對真正該擋的邊緣內容這是漏網——兩者上游分不開，我們也分不開。守住的界線是「只一次」加上前面已過 `SafetyGuard`。
- **前端整頁重載後 session 狀態拿不回來**：session 在 `IMemoryCache` 還活著（2 小時），但主規格沒有 `GET /api/sessions/{id}` 讓前端重建畫面。不在本次範圍，但跟「不全毀」是同一類問題，子專案 3 要處理。 → 子專案 3 設計 §3.4 以 `GET /api/sessions/{id}` + 前端 `sessionStorage` 處理。
