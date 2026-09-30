# 推薦組法（看過延後、探索位、換一批）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 定稿卡每個維度一排改成「2 套相關位＋1 套探索位」，看過的組合加權延後、名次轉機率抽樣，每排可以「換一批」往右接；追問卡不動。

**Architecture:** SQL 只取候選（既有三條推薦 SQL 改 LIMIT，加一條依 id 取 facet 向量的查詢），挑選邏輯全放在新的純函式 `SlateSelector`（合併、tag 集合 key、有效名次、抽樣、探索位差異）。`RecommendationService` 對定稿卡走新路徑並把「看過」與批次記在 `Session`；新端點 `POST /api/sessions/{id}/recommendations/next` 產生下一批。audit 記每套的理由、名次、機率與批次，`adoption_report.py` 多一節分理由的採用率與取代率。

**Tech Stack:** .NET 10 / ASP.NET Core minimal API、Npgsql + Pgvector、xUnit、Nuxt 3 / Vue 3 / Pinia、Vitest、Python 3.12 + pytest、PostgreSQL 16 + pgvector。

**Spec:** `docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`（數字出自 `docs/experiments/2026-09-30-recommendation-slate.md`）

## Global Constraints

- 註解、文件以繁體中文為主，密度與風格照周圍程式（解釋「為什麼」、引用 spec 章節）。commit 標題英文，照 repo 慣例 `feat(api): …`／`test: …`／`docs: …`。
- 每個 commit 訊息結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`（執行者若不是 Opus，改成當時 harness 給的那一行）。
- **在主目錄開分支 `feat/recommendation-slate`，不要用 worktree**：worktree 路徑下的 Debug 建置與 `nuxt prepare` 會被 Windows 應用程式控制擋下（facet 向量案踩過）。
- 追問卡（`AskOutcome`）的推薦輸出逐欄不變；`RecommendationServiceTests` 既有的追問卡測試全部要過。
- 參數預設：`RecommendationPoolSize = 30`、`RecommendationSeenPenalty = 10`、`RecommendationTemperature = 5`；每批相關位 2 套、探索位最多 1 套。
- 權重 `exp(−(名次 + P×看過次數)/τ)`；相關位第 1 位取有效名次最小（同分取原名次小），`prob` 記 1；第 2 位與探索位用抽的。
- 種子 `SlateSelector.Seed(sessionId, LatestSlateTurn, dimension, batch)`，SHA-256 前 4 bytes；**不可用 `string.GetHashCode`**。
- 理由字串固定四個：`anchored`／`similar`／`query`／`explore`；前端文案：含你講的 X／接近你講的 X／最接近你描述的／換個搭法。
- SSE 用 `WhenWritingNull`：追問卡的新欄位是 null，不會出現在事件裡；audit 的巢狀物件用 `AgenticOrchestrator.Fields` 省略 null，既有 audit 字串斷言不能變。
- 測試指令：C# `dotnet test src/PromptCopilot.Api.Tests`（integration 測試要 `PC_INTEGRATION=1` 才跑，需要 docker 的 `prompt-copilot-db`）；前端在 `src/PromptCopilot.Frontend` 跑 `npm test`（Node 不在 shell PATH 上，先 `export PATH="$PATH:/c/Users/USER/AppData/Local/Microsoft/WinGet/Links"`）；Python 在 `scripts/` 跑 `./.venv/Scripts/python.exe -m pytest tests/...`。
- 文件跟程式同一個 commit：`docs/單輪流程說明.md`（Task 4）、主規格（Task 5、6）、Swagger 描述（Task 6）、`adoption_report.py` docstring（Task 9）。

## Review Focus

spec 沒逐條寫、但使用者一定會碰到的情況；每一條都在對應任務加了測試：

1. **同一個 preset 在兩批都出現**（看過的被權重輪回來）：前端的 `v-for` key 不能只用 `presetId`，否則 Vue 重用錯的節點、圖片錯位；採用要對到使用者按的那一批。→ Task 7 `appendBatch keeps a repeated preset as a separate set with its own batch`、Task 9 `test_adoption_with_batch_links_to_that_batch_even_if_the_preset_repeats`。
2. **定稿卡推薦做到一半失敗**（第 6 個維度的 SQL 丟例外）：前面維度不能被記成「看過」，否則下一張卡平白把使用者沒看到的往後推。→ Task 4 `Failed_final_card_records_nothing_as_seen`。
3. **定稿後又出了一張追問卡，舊定稿卡還開著「換一批」**：伺服器要拒絕（409），前端按鈕也不能出現。→ Task 4 `Ask_outcome_ends_the_slate`、Task 6 `Next_is_409_for_an_older_card_or_after_an_ask_card`。
4. **看過很多次，有效名次很大**：`exp` 直接算會全部下溢成 0、抽樣除以 0 得 NaN。→ Task 1 `Draw_survives_huge_effective_ranks`。
5. **舊版前端存下的對話與舊 audit 資料沒有 `reason`／`batch`**：畫面照舊顯示列層級文案；報表照 `anchored`／`similar` 推回理由、不進名次分桶。→ Task 7 `setReasonLabel returns null for sets without a reason`、Task 9 `test_old_rows_without_sets_infer_reason_from_row_flags`。

---

## 檔案結構

| 檔案 | 責任 |
| :--- | :--- |
| `src/PromptCopilot.Api/Orchestration/SlateSelector.cs`（新） | 理由常數、候選／挑選 record、合併、tag 集合 key、抽樣、探索位、種子 |
| `src/PromptCopilot.Api/Sessions/Session.cs` | `LatestSlateTurn`、`LastFinalTags`、看過次數、批次 |
| `src/PromptCopilot.Api/Data/PresetRepository.cs` | `FacetVectorsAsync` |
| `src/PromptCopilot.Api/Configuration/Options.cs` | 三個新設定 |
| `src/PromptCopilot.Api/Streaming/Recommendations.cs` | `RecommendedSet` 的 `Reason`／`AnchorTags`／`Rank`／`Prob`、`RecommendedDimension.Batch` |
| `src/PromptCopilot.Api/Orchestration/RecommendationService.cs` | 定稿卡走 `SlateAsync`、`NextAsync` |
| `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs` | audit 的 `batch`／`sets`、`adoption.batch` |
| `src/PromptCopilot.Api/Sessions/AdoptionComposer.cs`、`Adoption.cs` | `Batch` 透傳 |
| `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs` | 換一批端點、Swagger 描述 |
| `src/PromptCopilot.Frontend/types/api.ts`、`lib/copy.ts`、`lib/reducer.ts`、`lib/adopt.ts` | 型別、理由文案、批次合併、採用帶 batch |
| `src/PromptCopilot.Frontend/composables/useApi.ts`、`stores/session.ts`、`components/RecommendationStrip.vue`、`components/AdoptDialog.vue` | 呼叫端點、狀態、畫面 |
| `scripts/adoption_report.py` | 「定稿卡推薦組法」一節 |
| 文件 | `docs/單輪流程說明.md`、主規格、set-recommendations spec、`docs/eval-cases.md`、本案 spec §8 |

---

### Task 0: 開分支

- [ ] **Step 1: 從 master 開分支**

```bash
git checkout master && git pull --ff-only && git checkout -b feat/recommendation-slate
```

---

### Task 1: `SlateSelector` 純函式

**Files:**
- Create: `src/PromptCopilot.Api/Orchestration/SlateSelector.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SlateSelectorTests.cs`

**Interfaces:**
- Consumes: `PresetCandidate`（`Data/PresetRepository.cs`）、`TagAttribution.Normalize`。
- Produces:
  - `static class SlateReason { const string Anchored = "anchored", Similar = "similar", Query = "query", Explore = "explore"; }`
  - `record SlateCandidate(PresetCandidate Preset, string Reason, IReadOnlyList<string> AnchorTags, int Rank, string Key)`
  - `record SlatePick(SlateCandidate Candidate, int Rank, double Prob)`
  - `record SlateTier(string Reason, IReadOnlyList<PresetCandidate> Hits, Func<PresetCandidate, IReadOnlyList<string>> AnchorsOf)`
  - `SlateSelector.RelevantSlots = 2`
  - `SlateSelector.TagSetKey(IReadOnlyDictionary<string, IReadOnlyList<string>> facetTags, IReadOnlyList<string> dimensionFacets) → string`
  - `SlateSelector.Merge(IReadOnlyList<SlateTier> tiers, IReadOnlyList<string> dimensionFacets) → IReadOnlyList<SlateCandidate>`
  - `SlateSelector.PickRelevant(IReadOnlyList<SlateCandidate> candidates, IReadOnlyDictionary<string, int> seen, double penalty, double temperature, Random rng) → IReadOnlyList<SlatePick>`
  - `SlateSelector.PickExplore(IReadOnlyList<SlateCandidate> pool, IReadOnlyList<SlateCandidate> relevant, IReadOnlyList<string> compareFacets, IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors, IReadOnlyDictionary<string, int> seen, double penalty, double temperature, Random rng) → SlatePick?`
  - `SlateSelector.Draw(IReadOnlyList<double> effective, double temperature, Random rng) → (int index, double prob)`
  - `SlateSelector.Similarity(long a, long b, IReadOnlyList<string> facets, IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors) → double`
  - `SlateSelector.Cosine(float[] x, float[] y) → double`
  - `SlateSelector.Seed(string sessionId, int turnIndex, string dimension, int batch) → int`

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/SlateSelectorTests.cs`：

```csharp
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class SlateSelectorTests
{
    private static readonly string[] Clothing = { "clothing.upper", "clothing.lower", "clothing.footwear" };
    private static readonly IReadOnlyDictionary<string, int> NoneSeen = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<(long PresetId, string FacetId), float[]> NoVectors = new Dictionary<(long, string), float[]>();

    private static PresetCandidate C(long id, params (string facet, string tags)[] ft) =>
        new(id, $"#{id}", ft.Select(f => f.facet).ToList(),
            ft.ToDictionary(f => f.facet, f => (IReadOnlyList<string>)f.tags.Split(", ")), null, null, 0.2);

    private static SlateTier Tier(string reason, params PresetCandidate[] hits) => new(reason, hits, _ => Array.Empty<string>());
    private static float[] Unit(int hot) { var v = new float[8]; v[hot] = 1f; return v; }

    [Fact]
    public void Tag_set_key_normalizes_sorts_and_ignores_other_dimensions()
    {
        var a = C(1, ("clothing.upper", "White_Shirt, (skirt:1.2)"), ("scene.weather", "rain")).FacetTags;
        var b = C(2, ("clothing.upper", "skirt, white shirt")).FacetTags;
        Assert.Equal(SlateSelector.TagSetKey(a, Clothing), SlateSelector.TagSetKey(b, Clothing));
        Assert.Equal("clothing.upper=skirt|clothing.upper=white shirt", SlateSelector.TagSetKey(a, Clothing));
    }

    [Fact]
    public void Merge_keeps_tier_order_dedups_ids_and_collapses_identical_tag_sets()
    {
        var merged = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "shirt")), C(2, ("clothing.footwear", "sandals"), ("clothing.lower", "skirt"))),
            Tier(SlateReason.Similar, C(2, ("clothing.footwear", "sandals"), ("clothing.lower", "skirt")), C(3, ("clothing.upper", "Shirt"), ("clothing.footwear", "sandals"))),
            Tier(SlateReason.Query, C(4, ("clothing.upper", "hoodie"), ("clothing.lower", "jeans"))),
        }, Clothing);
        Assert.Equal(new long[] { 1, 2, 4 }, merged.Select(c => c.Preset.Id));          // 2 重複 id、3 跟 1 的 tag 集合相同
        Assert.Equal(new[] { 0, 1, 2 }, merged.Select(c => c.Rank));
        Assert.Equal(new[] { SlateReason.Anchored, SlateReason.Anchored, SlateReason.Query }, merged.Select(c => c.Reason));
    }

    [Fact]
    public void Merge_takes_anchor_tags_from_the_tier()
    {
        var tier = new SlateTier(SlateReason.Anchored, new[] { C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x")) }, _ => new[] { "sandals" });
        Assert.Equal(new[] { "sandals" }, Assert.Single(SlateSelector.Merge(new[] { tier }, Clothing)).AnchorTags);
    }

    private static IReadOnlyList<SlateCandidate> Four() => SlateSelector.Merge(new[]
    {
        Tier(SlateReason.Anchored, C(1, ("clothing.upper", "a"), ("clothing.lower", "a")), C(2, ("clothing.upper", "b"), ("clothing.lower", "b")),
             C(3, ("clothing.upper", "c"), ("clothing.lower", "c")), C(4, ("clothing.upper", "d"), ("clothing.lower", "d"))),
    }, Clothing);

    [Fact]
    public void First_relevant_slot_is_the_best_effective_rank_with_probability_one()
    {
        var picks = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(1));
        Assert.Equal(2, picks.Count);
        Assert.Equal(1, picks[0].Candidate.Preset.Id);
        Assert.Equal(1.0, picks[0].Prob);
        Assert.NotEqual(1, picks[1].Candidate.Preset.Id);
        Assert.InRange(picks[1].Prob, 0.0, 1.0);
    }

    [Fact]
    public void Seen_candidates_are_pushed_back_by_the_penalty()
    {
        var four = Four();
        var seen = new Dictionary<string, int> { [four[0].Key] = 1, [four[1].Key] = 1 };
        var picks = SlateSelector.PickRelevant(four, seen, 10, 0.01, new Random(1));   // τ 很小：第 2 位幾乎必取有效名次次小的
        Assert.Equal(new long[] { 3, 4 }, picks.Select(p => p.Candidate.Preset.Id));
        Assert.Equal(new[] { 2, 3 }, picks.Select(p => p.Rank));                         // Rank 記原名次，不是有效名次
    }

    [Fact]
    public void Same_seed_gives_the_same_picks()
    {
        var seed = SlateSelector.Seed("s1", 3, "clothing", 1);
        var a = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(seed)).Select(p => p.Candidate.Preset.Id);
        var b = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(seed)).Select(p => p.Candidate.Preset.Id);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Pick_relevant_handles_one_and_zero_candidates()
    {
        Assert.Empty(SlateSelector.PickRelevant(Array.Empty<SlateCandidate>(), NoneSeen, 10, 5, new Random(1)));
        Assert.Single(SlateSelector.PickRelevant(Four().Take(1).ToList(), NoneSeen, 10, 5, new Random(1)));
    }

    [Fact]
    public void Draw_frequency_matches_the_weights()
    {
        var rng = new Random(42);
        var counts = new int[3];
        for (var i = 0; i < 10_000; i++) counts[SlateSelector.Draw(new double[] { 0, 1, 2 }, 1, rng).index]++;
        var z = 1 + Math.Exp(-1) + Math.Exp(-2);
        Assert.InRange(counts[0] / 10_000.0, 1 / z - 0.02, 1 / z + 0.02);
        Assert.InRange(counts[2] / 10_000.0, Math.Exp(-2) / z - 0.02, Math.Exp(-2) / z + 0.02);
        var first = SlateSelector.Draw(new double[] { 0, 1, 2 }, 1, new Random(0));
        Assert.Equal(new[] { 1 / z, Math.Exp(-1) / z, Math.Exp(-2) / z }[first.index], first.prob, 6);   // 回報的機率就是它的權重占比
    }

    [Fact]
    public void Draw_survives_huge_effective_ranks()
    {
        // Review Focus 4
        var (index, prob) = SlateSelector.Draw(new double[] { 500, 510 }, 5, new Random(1));
        Assert.InRange(index, 0, 1);
        Assert.False(double.IsNaN(prob));
        Assert.InRange(prob, 0.0, 1.0);
    }

    [Fact]
    public void Explore_picks_the_candidate_most_different_on_the_compare_facets()
    {
        var relevant = SlateSelector.Merge(new[] { Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x"))) }, Clothing);
        var pool = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Explore, C(2, ("clothing.footwear", "flip flops"), ("clothing.upper", "y")), C(3, ("clothing.footwear", "boots"), ("clothing.lower", "long skirt"))),
        }, Clothing);
        var vectors = new Dictionary<(long, string), float[]>
        {
            [(1, "clothing.footwear")] = Unit(0), [(2, "clothing.footwear")] = Unit(0), [(3, "clothing.footwear")] = Unit(1),
        };
        var pick = SlateSelector.PickExplore(pool, relevant, new[] { "clothing.footwear" }, vectors, NoneSeen, 10, 0.01, new Random(1));
        Assert.Equal(3, pick!.Candidate.Preset.Id);                                    // flip flops 跟 sandals 同向量，差異 0
        Assert.Equal(0, pick.Rank);
        Assert.Equal(SlateReason.Explore, pick.Candidate.Reason);
    }

    [Fact]
    public void Explore_skips_candidates_without_tags_on_compare_facets_and_those_sharing_a_relevant_key()
    {
        var relevant = SlateSelector.Merge(new[] { Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x"))) }, Clothing);
        var pool = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Explore, C(5, ("clothing.footwear", "sandals"), ("clothing.upper", "x")), C(6, ("clothing.upper", "hoodie"), ("clothing.lower", "jeans"))),
        }, Clothing);
        Assert.Null(SlateSelector.PickExplore(pool, relevant, new[] { "clothing.footwear" }, NoVectors, NoneSeen, 10, 5, new Random(1)));
    }

    [Fact]
    public void Explore_without_relevant_falls_back_to_the_original_rank()
    {
        var pool = SlateSelector.Merge(new[] { Tier(SlateReason.Explore, C(7, ("clothing.upper", "a"), ("clothing.lower", "a")), C(8, ("clothing.upper", "b"), ("clothing.lower", "b"))) }, Clothing);
        var pick = SlateSelector.PickExplore(pool, Array.Empty<SlateCandidate>(), Clothing, NoVectors, NoneSeen, 10, 0.01, new Random(1));
        Assert.Equal(7, pick!.Candidate.Preset.Id);
    }

    [Fact]
    public void Similarity_averages_shared_facets_and_is_zero_without_any()
    {
        var v = new Dictionary<(long, string), float[]>
        {
            [(1, "a")] = Unit(0), [(2, "a")] = Unit(0), [(1, "b")] = Unit(0), [(2, "b")] = Unit(1), [(3, "c")] = Unit(0),
        };
        Assert.Equal(0.5, SlateSelector.Similarity(1, 2, new[] { "a", "b" }, v), 6);
        Assert.Equal(0, SlateSelector.Similarity(1, 3, new[] { "a", "b", "c" }, v));
    }

    [Fact]
    public void Seed_is_stable_and_changes_with_every_field()
    {
        var s = SlateSelector.Seed("s1", 3, "clothing", 1);
        Assert.Equal(s, SlateSelector.Seed("s1", 3, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s2", 3, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 4, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 3, "style", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 3, "clothing", 2));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SlateSelectorTests"`
Expected: 編譯失敗，`SlateSelector`／`SlateReason` 等不存在。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Orchestration/SlateSelector.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

/// <summary>定稿卡推薦組法的理由（推薦組法設計 §3.1、§5.1）。追問卡的組合不帶理由（null）。</summary>
public static class SlateReason
{
    public const string Anchored = "anchored";
    public const string Similar = "similar";
    public const string Query = "query";
    public const string Explore = "explore";
}

/// <summary>名單上的一筆。Rank：三層接成一條名單、合併後的原名次（0 起算）；Key：該維度的 tag 集合，看過次數用它記。</summary>
public sealed record SlateCandidate(PresetCandidate Preset, string Reason, IReadOnlyList<string> AnchorTags, int Rank, string Key);

/// <summary>挑中的一套。Rank：相關位是原名次，探索位是差異排名；Prob：被抽中的機率，第 1 位是取最高、不是抽的，記 1。</summary>
public sealed record SlatePick(SlateCandidate Candidate, int Rank, double Prob);

/// <summary>一層候選：理由、依距離排好的命中、每筆命中的錨。</summary>
public sealed record SlateTier(string Reason, IReadOnlyList<PresetCandidate> Hits, Func<PresetCandidate, IReadOnlyList<string>> AnchorsOf);

/// <summary>推薦組法的挑選（設計 §3.1、§4.1）。純函式：不碰資料庫與 session，隨機來源由呼叫端給（種子見 <see cref="Seed"/>）。</summary>
public static class SlateSelector
{
    public const int RelevantSlots = 2;

    /// <summary>該維度的 tag 集合：tag 正規化、帶 facet 前綴、去重、排序後串接。
    /// 知識庫很多片段在某維度的 tag 一模一樣；只認 presetId 的話，看過 A 之後一模一樣的 A' 權重仍是滿的（設計 §2）。</summary>
    public static string TagSetKey(IReadOnlyDictionary<string, IReadOnlyList<string>> facetTags, IReadOnlyList<string> dimensionFacets) =>
        string.Join("|", dimensionFacets
            .Where(facetTags.ContainsKey)
            .SelectMany(f => facetTags[f].Select(TagAttribution.Normalize).Where(t => t.Length > 0).Select(t => $"{f}={t}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>三層依序接成一條名單：presetId 重複的留第一次出現的，tag 集合相同的只留名次最前的一筆。</summary>
    public static IReadOnlyList<SlateCandidate> Merge(IReadOnlyList<SlateTier> tiers, IReadOnlyList<string> dimensionFacets)
    {
        var ids = new HashSet<long>();
        var keys = new HashSet<string>();
        var list = new List<SlateCandidate>();
        foreach (var tier in tiers)
            foreach (var h in tier.Hits)
            {
                if (!ids.Add(h.Id)) continue;
                var key = TagSetKey(h.FacetTags, dimensionFacets);
                if (!keys.Add(key)) continue;
                list.Add(new SlateCandidate(h, tier.Reason, tier.AnchorsOf(h), list.Count, key));
            }
        return list;
    }

    /// <summary>相關位（設計 §3.1）：第 1 位取有效名次最小（同分取原名次小），第 2 位依 exp(−有效名次/τ) 抽。有效名次＝原名次＋P×看過次數。</summary>
    public static IReadOnlyList<SlatePick> PickRelevant(IReadOnlyList<SlateCandidate> candidates, IReadOnlyDictionary<string, int> seen,
        double penalty, double temperature, Random rng)
    {
        var picks = new List<SlatePick>();
        if (candidates.Count == 0) return picks;
        double Effective(SlateCandidate c) => c.Rank + penalty * seen.GetValueOrDefault(c.Key);
        var first = candidates.OrderBy(Effective).ThenBy(c => c.Rank).First();
        picks.Add(new SlatePick(first, first.Rank, 1.0));
        var rest = candidates.Where(c => !ReferenceEquals(c, first)).ToList();
        while (picks.Count < RelevantSlots && rest.Count > 0)
        {
            var (i, prob) = Draw(rest.Select(Effective).ToList(), temperature, rng);
            picks.Add(new SlatePick(rest[i], rest[i].Rank, prob));
            rest.RemoveAt(i);
        }
        return picks;
    }

    /// <summary>探索位（設計 §3.1）：不看錨的純向量候選裡，去掉跟相關位同 key 的、在比較用 facet 上一個 tag 都沒有的（它取代不了任何東西），
    /// 依「跟相關位的差異」由大到小排名（同分照原名次），再用同一個權重與看過延後抽 1 套。相關位一套都沒有時差異全當 1。</summary>
    public static SlatePick? PickExplore(IReadOnlyList<SlateCandidate> pool, IReadOnlyList<SlateCandidate> relevant, IReadOnlyList<string> compareFacets,
        IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors, IReadOnlyDictionary<string, int> seen,
        double penalty, double temperature, Random rng)
    {
        var taken = relevant.Select(r => r.Key).ToHashSet();
        var eligible = pool.Where(c => !taken.Contains(c.Key)
            && compareFacets.Any(f => (c.Preset.FacetTags.GetValueOrDefault(f)?.Count ?? 0) > 0)).ToList();
        if (eligible.Count == 0) return null;
        double Difference(SlateCandidate c) =>
            relevant.Count == 0 ? 1 : 1 - relevant.Max(r => Similarity(c.Preset.Id, r.Preset.Id, compareFacets, vectors));
        var ranked = eligible.Select(c => (c, diff: Difference(c))).OrderByDescending(x => x.diff).ThenBy(x => x.c.Rank).Select(x => x.c).ToList();
        var (i, prob) = Draw(ranked.Select((c, rank) => rank + penalty * seen.GetValueOrDefault(c.Key)).ToList(), temperature, rng);
        return new SlatePick(ranked[i], i, prob);
    }

    /// <summary>依 exp(−有效名次/τ) 抽一個，回索引與它的機率。先減掉最小值再取 exp：看過很多次的有效名次很大，直接算會全部下溢成 0。</summary>
    public static (int index, double prob) Draw(IReadOnlyList<double> effective, double temperature, Random rng)
    {
        var min = effective.Min();
        var w = effective.Select(e => Math.Exp(-(e - min) / temperature)).ToArray();
        var total = w.Sum();
        var x = rng.NextDouble() * total;
        for (var i = 0; i < w.Length; i++)
        {
            x -= w[i];
            if (x < 0) return (i, w[i] / total);
        }
        return (w.Length - 1, w[^1] / total);
    }

    /// <summary>兩套在比較用 facet 上的相似度：兩者都有 facet 向量的 facet 各算 cos 再平均；一個共有的都沒有記 0（視為完全不同）。</summary>
    public static double Similarity(long a, long b, IReadOnlyList<string> facets, IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors)
    {
        var sims = facets.Where(f => vectors.ContainsKey((a, f)) && vectors.ContainsKey((b, f)))
            .Select(f => Cosine(vectors[(a, f)], vectors[(b, f)])).ToList();
        return sims.Count == 0 ? 0 : sims.Average();
    }

    public static double Cosine(float[] x, float[] y)
    {
        double dot = 0, nx = 0, ny = 0;
        for (var i = 0; i < x.Length; i++) { dot += x[i] * y[i]; nx += x[i] * x[i]; ny += y[i] * y[i]; }
        return nx == 0 || ny == 0 ? 0 : dot / Math.Sqrt(nx * ny);
    }

    /// <summary>(session, 定稿卡輪次, 維度, 批次) 的穩定種子：同樣狀態重播得到同一批（設計 §2）。
    /// 不用 string.GetHashCode：它每次程序啟動換隨機值，重啟後無法重現。</summary>
    public static int Seed(string sessionId, int turnIndex, string dimension, int batch) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{turnIndex}|{dimension}|{batch}")), 0);
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SlateSelectorTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/SlateSelector.cs src/PromptCopilot.Api.Tests/Orchestration/SlateSelectorTests.cs
git commit -m "feat(api): SlateSelector merges tiers, weights seen sets and samples relevant and explore slots"
```

---

### Task 2: Session 的推薦組法狀態

**Files:**
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`

**Interfaces:**
- Produces（`Session` 成員）：
  - `int? LatestSlateTurn { get; }`
  - `IReadOnlyList<string> LastFinalTags { get; }`
  - `void BeginSlate(int turnIndex, IReadOnlyList<string> finalTags)`：記最新定稿卡輪次與定稿 tag，**批次歸零、看過次數保留**
  - `void EndSlate()`：`LatestSlateTurn = null`
  - `IReadOnlyDictionary<string, int> SeenFor(string dimension)`
  - `int SlateBatch(string dimension)`：沒記過回 0
  - `void RecordSlate(string dimension, int batch, IEnumerable<string> keys)`：批次設為 `batch`、每個 key 看過次數 +1

- [ ] **Step 1: 寫失敗的測試**

加進 `SessionTests.cs`（沿用檔內既有的 `Catalog` 等欄位；沒有的話用 `new Session("s")` 即可，這些方法不需要 profile）：

```csharp
    [Fact]
    public void Slate_state_counts_seen_keys_per_dimension_and_resets_batches_on_a_new_card()
    {
        var s = new Session("s");
        Assert.Null(s.LatestSlateTurn);
        Assert.Empty(s.SeenFor("clothing"));
        Assert.Equal(0, s.SlateBatch("clothing"));

        s.BeginSlate(3, new[] { "sandals" });
        s.RecordSlate("clothing", 1, new[] { "k1", "k2" });
        s.RecordSlate("clothing", 2, new[] { "k1" });
        Assert.Equal(3, s.LatestSlateTurn);
        Assert.Equal(new[] { "sandals" }, s.LastFinalTags);
        Assert.Equal(2, s.SeenFor("clothing")["k1"]);
        Assert.Equal(1, s.SeenFor("clothing")["k2"]);
        Assert.Equal(2, s.SlateBatch("clothing"));
        Assert.Empty(s.SeenFor("style"));

        s.BeginSlate(5, Array.Empty<string>());                  // 新的定稿卡：批次歸零，看過保留（跨卡延後就靠它）
        Assert.Equal(0, s.SlateBatch("clothing"));
        Assert.Equal(2, s.SeenFor("clothing")["k1"]);
        Assert.Empty(s.LastFinalTags);

        s.EndSlate();
        Assert.Null(s.LatestSlateTurn);
    }

    [Fact]
    public void Restore_does_not_touch_slate_state()
    {
        var s = new Session("s");
        var snap = s.Snapshot();
        s.BeginSlate(2, Array.Empty<string>());
        s.RecordSlate("style", 1, new[] { "k" });
        s.Restore(snap);
        Assert.Equal(2, s.LatestSlateTurn);                       // 推薦在一輪成立後才產生，被攔截的輪走不到這裡（設計 §4.4）
        Assert.Equal(1, s.SeenFor("style")["k"]);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SessionTests"`
Expected: 編譯失敗，`BeginSlate` 等不存在。

- [ ] **Step 3: 實作**

在 `Session.cs` 的 `Lock` 屬性之後加：

```csharp
    /// <summary>推薦組法（2026-09-30 設計 §4.4）：最新一張定稿卡的輪次，換一批只接受它；出了新的追問卡就清掉。
    /// 下面這幾個都不進 Snapshot／Restore：推薦在一輪成立之後才產生，被攔截的輪走不到這裡。</summary>
    public int? LatestSlateTurn { get; private set; }
    /// <summary>最近一次定稿的 positive tag（已排除基礎詞），換一批時重算錨用。</summary>
    public IReadOnlyList<string> LastFinalTags { get; private set; } = Array.Empty<string>();
    private readonly Dictionary<string, Dictionary<string, int>> _seenSets = new();
    private readonly Dictionary<string, int> _slateBatches = new();
    private static readonly IReadOnlyDictionary<string, int> NoneSeen = new Dictionary<string, int>();
```

在 `RecordAdoption` 之後加：

```csharp
    /// <summary>新的定稿卡：批次歸零；看過次數保留，下一張卡才會把看過的往後延（設計 §3.1）。</summary>
    public void BeginSlate(int turnIndex, IReadOnlyList<string> finalTags)
    {
        LatestSlateTurn = turnIndex;
        LastFinalTags = finalTags.ToList();
        _slateBatches.Clear();
    }

    public void EndSlate() => LatestSlateTurn = null;

    /// <summary>該維度的「tag 集合 key → 看過次數」。</summary>
    public IReadOnlyDictionary<string, int> SeenFor(string dimension) => _seenSets.TryGetValue(dimension, out var d) ? d : NoneSeen;

    /// <summary>該維度目前是第幾批；這張卡還沒出過就是 0。</summary>
    public int SlateBatch(string dimension) => _slateBatches.GetValueOrDefault(dimension);

    public void RecordSlate(string dimension, int batch, IEnumerable<string> keys)
    {
        _slateBatches[dimension] = batch;
        if (!_seenSets.TryGetValue(dimension, out var d)) _seenSets[dimension] = d = new Dictionary<string, int>();
        foreach (var k in keys) d[k] = d.GetValueOrDefault(k) + 1;
    }
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SessionTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Sessions/Session.cs src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs
git commit -m "feat(api): session keeps the latest slate card, seen set counts and batch per dimension"
```

---

### Task 3: `PresetRepository.FacetVectorsAsync`

**Files:**
- Modify: `src/PromptCopilot.Api/Data/PresetRepository.cs`
- Test: `src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs`

**Interfaces:**
- Produces: `virtual Task<IReadOnlyDictionary<(long PresetId, string FacetId), float[]>> FacetVectorsAsync(IReadOnlyList<long> presetIds, IReadOnlyList<string> facetIds, CancellationToken ct)`；任一清單為空時直接回空字典、不查資料庫。

- [ ] **Step 1: 寫失敗的測試**

加進 `RepositoryIntegrationTests`（fixture 已插入：測試穿搭二（Ref3）與測試穿搭三（Ref4）的 `FxFootwear` 向量是 `Unit(1)`、測試單品（Ref5）是 `Unit(2)`、測試穿搭二的 `FxUpper` 是 `Unit(2)`）：

```csharp
    private async Task<long> IdOf(string sourceRef)
    {
        await using var cmd = _ds.CreateCommand("SELECT id FROM prompt_knowledge_presets WHERE source_ref = @r");
        cmd.Parameters.AddWithValue("r", sourceRef);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [IntegrationFact]
    public async Task Facet_vectors_returns_rows_for_the_requested_presets_and_facets_only()
    {
        var repo = new PresetRepository(_ds);
        long p3 = await IdOf(Ref3), p4 = await IdOf(Ref4), p5 = await IdOf(Ref5);
        var v = await repo.FacetVectorsAsync(new[] { p3, p4, p5 }, new[] { FxFootwear }, default);
        Assert.Equal(3, v.Count);
        Assert.Equal(1f, v[(p3, FxFootwear)][1]);
        Assert.Equal(1f, v[(p5, FxFootwear)][2]);
        Assert.False(v.ContainsKey((p3, FxUpper)));                                  // 沒要的 facet 不回
        Assert.Equal(1f, (await repo.FacetVectorsAsync(new[] { p3 }, new[] { FxUpper }, default))[(p3, FxUpper)][2]);
        Assert.Empty(await repo.FacetVectorsAsync(Array.Empty<long>(), new[] { FxFootwear }, default));
        Assert.Empty(await repo.FacetVectorsAsync(new[] { p3 }, Array.Empty<string>(), default));
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~Facet_vectors_returns"`
Expected: 編譯失敗，`FacetVectorsAsync` 不存在。

- [ ] **Step 3: 實作**

在 `PresetRepository` 的 `RecommendSimilarSql` 之後加常數：

```csharp
    // 推薦組法（2026-09-30 設計 §4.3）：探索位要比候選與相關位在比較用 facet 上的向量。子表主鍵就是 (preset_id, facet_id)，
    // 一次撈回約 60 個 preset × 幾個 facet；執行計畫與耗時見該設計 §8。
    private const string FacetVectorsSql = """
        SELECT preset_id, facet_id, embedding
        FROM preset_facet_embeddings
        WHERE preset_id = ANY(@ids) AND facet_id = ANY(@facets)
        """;
```

在 `RecommendSimilarAsync` 之後加方法：

```csharp
    public virtual async Task<IReadOnlyDictionary<(long PresetId, string FacetId), float[]>> FacetVectorsAsync(IReadOnlyList<long> presetIds,
        IReadOnlyList<string> facetIds, CancellationToken ct)
    {
        var map = new Dictionary<(long PresetId, string FacetId), float[]>();
        if (presetIds.Count == 0 || facetIds.Count == 0) return map;
        await using var cmd = ds.CreateCommand(FacetVectorsSql);
        cmd.Parameters.AddWithValue("ids", presetIds.ToArray());
        cmd.Parameters.AddWithValue("facets", facetIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[(r.GetInt64(0), r.GetString(1))] = r.GetFieldValue<Vector>(2).ToArray();
        return map;
    }
```

- [ ] **Step 4: 跑測試確認通過**

Run: `PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RepositoryIntegrationTests"`
Expected: 全部 PASS（需要 `prompt-copilot-db` 在跑）。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Data/PresetRepository.cs src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs
git commit -m "feat(api): PresetRepository.FacetVectorsAsync fetches facet vectors by preset and facet ids"
```

---

### Task 4: 定稿卡走推薦組法、`NextAsync`

**Files:**
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs`
- Modify: `src/PromptCopilot.Api/Streaming/Recommendations.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/RecommendationService.cs`
- Modify: `src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs`
- Modify: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`（兩個 stub 補 `NextAsync`）
- Modify: `docs/單輪流程說明.md`（C# 推薦那段）

**Interfaces:**
- Consumes: Task 1 全部、Task 2 的 `Session` 成員、Task 3 的 `FacetVectorsAsync`。
- Produces:
  - `OrchestratorOptions.RecommendationPoolSize`（int, 30）、`RecommendationSeenPenalty`（double, 10）、`RecommendationTemperature`（double, 5）
  - `RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets, string? Reason = null, IReadOnlyList<string>? AnchorTags = null, int? Rank = null, double? Prob = null)`
  - `RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets, bool Similar = false, int? Batch = null)`
  - `IRecommendationService.NextAsync(Session s, string dimension, CancellationToken ct) → Task<RecommendedDimension>`：沒有候選時回 `Sets` 為空、`Batch` = 目前批次 + 1 的一排，不記看過、不前進批次。呼叫端要拿著 session 鎖、先確認 `LatestSlateTurn`。

- [ ] **Step 1: 事件 record、設定、介面（先讓測試編得過）**

`Streaming/Recommendations.cs` 兩個 record 改成（保留既有 summary，補一句）：

```csharp
/// <summary>Reason／AnchorTags／Rank／Prob（2026-09-30 推薦組法設計 §5.1）：只有定稿卡有；追問卡為 null，SSE 的 WhenWritingNull 會省略。
/// Reason：anchored／similar／query／explore；Rank：相關位是原名次、探索位是差異排名；Prob：被抽中的機率，相關位第 1 位記 1。</summary>
public sealed record RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets,
    string? Reason = null, IReadOnlyList<string>? AnchorTags = null, int? Rank = null, double? Prob = null);
/// <summary>…（既有說明保留）… Batch（2026-09-30）：定稿卡的第幾批，定稿卡上那批是 1、換一批依序 2、3……；
/// 定稿卡這一排的 Anchored／Similar 固定 false、AnchorTags 為空，理由看每一套的 Reason。追問卡為 null。</summary>
public sealed record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets,
    bool Similar = false, int? Batch = null);
```

`Configuration/Options.cs` 的 `RecommendationSimilarMaxDist` 之後加：

```csharp
    /// <summary>推薦組法（2026-09-30 設計 §3.1）：定稿卡每層候選取幾筆。純向量那層走 HNSW，hnsw.ef_search 預設 40，
    /// 調到 40 以上要一起調 ef_search，否則回不滿（設計 §8）。</summary>
    public int RecommendationPoolSize { get; set; } = 30;
    /// <summary>看過一次，有效名次往後加幾名。P=10、τ=5 時看過一次權重剩 e^-2 ≈ 13.5%，兩次 1.8%。</summary>
    public double RecommendationSeenPenalty { get; set; } = 10;
    /// <summary>名次轉機率的溫度 τ：權重 exp(−有效名次/τ)。越大越往深處抽；使用者要的是看過的退得夠多，不是抽很深。</summary>
    public double RecommendationTemperature { get; set; } = 5;
```

`IRecommendationService` 加：

```csharp
    /// <summary>換一批（推薦組法設計 §4.5）：該維度的下一批。沒有候選時回 Sets 為空的一排（批次不前進、不記看過）。
    /// 呼叫端要拿著 session 鎖，並先確認 turnIndex 等於 LatestSlateTurn。</summary>
    Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct);
```

`AgenticOrchestratorTests.cs` 的 `StubRecommendations` 與 `ObservingRecommendations` 各加一行：

```csharp
        public Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct) => throw new NotSupportedException();
```

`RecommendationService` 先加一個會丟例外的 `NextAsync` 空殼讓專案編得過（Step 5 換成實作）：

```csharp
    public Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct) => throw new NotImplementedException();
```

- [ ] **Step 2: 寫失敗的測試**

`RecommendationServiceTests.cs` 的 `FakePresets` 加三個成員（放在既有成員之後）：

```csharp
        public string? ThrowOn { get; set; }                                                               // 維度第一個 facet 等於它就丟例外
        public Dictionary<(long, string), float[]> Vectors { get; } = new();
        public List<(IReadOnlyList<long> ids, IReadOnlyList<string> facets)> VectorCalls { get; } = new();
        public override Task<IReadOnlyDictionary<(long PresetId, string FacetId), float[]>> FacetVectorsAsync(IReadOnlyList<long> presetIds, IReadOnlyList<string> facetIds, CancellationToken ct)
        {
            VectorCalls.Add((presetIds, facetIds));
            IReadOnlyDictionary<(long PresetId, string FacetId), float[]> r = Vectors.Where(kv => presetIds.Contains(kv.Key.Item1) && facetIds.Contains(kv.Key.Item2))
                .ToDictionary(kv => (kv.Key.Item1, kv.Key.Item2), kv => kv.Value);
            return Task.FromResult(r);
        }
```

並把既有 `RecommendAsync` override 的第一行前面加上：

```csharp
            if (ThrowOn == dimensionFacets[0]) throw new InvalidOperationException("db down");
```

`Make` 改成可以帶設定（既有呼叫不用改）：

```csharp
    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) Make(params (string facet, string tags)[] covered) =>
        MakeWith(new OrchestratorOptions(), covered);

    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) MakeWith(OrchestratorOptions o, params (string facet, string tags)[] covered)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddSystemMessage("sys");
        s.ChatHistory.AddUserMessage("一個少女穿涼鞋");
        s.ApplyFacetStates(covered.ToDictionary(c => c.facet, _ => FacetState.Covered), Catalog, covered.ToDictionary(c => c.facet, c => c.tags));
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        return (new RecommendationService(Catalog, embed, presets, o), s, embed, presets);
    }

    /// <summary>τ 很小：第 2 位幾乎必取有效名次次小的，測試結果不受抽樣影響。</summary>
    private static OrchestratorOptions Greedy() => new() { RecommendationTemperature = 0.01 };

    private static readonly PresetCandidate[] FourSandals =
    {
        Set(1, "A", ("clothing.footwear", "sandals"), ("clothing.upper", "shirt")),
        Set(2, "B", ("clothing.footwear", "sandals"), ("clothing.lower", "skirt")),
        Set(3, "C", ("clothing.footwear", "sandals"), ("clothing.upper", "tank top")),
        Set(4, "D", ("clothing.footwear", "sandals"), ("clothing.lower", "shorts")),
    };
```

新測試：

```csharp
    [Fact]
    public async Task Final_card_row_has_two_relevant_sets_and_one_explore_set_with_reasons_and_batch_one()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals.Take(2).ToList();
        presets.Script[("clothing.head", false)] = new[]
        {
            Set(5, "靴子長裙", ("clothing.footwear", "boots"), ("clothing.lower", "long skirt")),
            Set(6, "帽T牛仔褲", ("clothing.upper", "hoodie"), ("clothing.lower", "jeans")),         // 沒有鞋履 tag：取代不了使用者講的，不能當探索位
        };
        var e = await svc.BuildAsync(s, Finalized("1girl, sandals"), 4, default);
        var d = e!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(1, d.Batch);
        Assert.False(d.Anchored); Assert.False(d.Similar); Assert.Empty(d.AnchorTags);
        Assert.Equal(new long[] { 1, 2, 5 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(new[] { "anchored", "anchored", "explore" }, d.Sets.Select(x => x.Reason));
        Assert.Equal(new[] { "sandals" }, d.Sets[0].AnchorTags);
        Assert.Equal(1.0, d.Sets[0].Prob); Assert.Equal(0, d.Sets[0].Rank);
        Assert.Equal(0, d.Sets[2].Rank);
        Assert.Equal(4, s.LatestSlateTurn);
        Assert.Equal(1, s.SlateBatch("clothing"));
        Assert.Equal(3, s.SeenFor("clothing").Count);
        Assert.All(presets.Calls, c => Assert.Equal(30, c.take));                                     // 每層取 PoolSize
        Assert.Equal(new[] { "clothing.footwear" }, presets.VectorCalls.Last().facets);                 // 比較用 facet = covered
    }

    [Fact]
    public async Task Final_card_queries_every_tier_even_when_the_literal_anchor_has_enough_hits()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.Script[("clothing.head", true)] = FourSandals;
        await svc.BuildAsync(s, Finalized("1girl"), 1, default);
        Assert.Equal(("clothing.footwear", 0.30, 30), Assert.Single(presets.SimilarCalls));
        Assert.Contains(presets.Calls, c => c.firstFacet == "clothing.head" && c.anchorTags.Count > 0);
        Assert.Contains(presets.Calls, c => c.firstFacet == "clothing.head" && c.anchorTags.Count == 0);
    }

    [Fact]
    public async Task Seen_sets_are_pushed_back_on_the_next_final_card()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals;
        var first = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        var second = (await svc.BuildAsync(s, Finalized("1girl"), 3, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new long[] { 1, 2 }, first.Sets.Select(x => x.PresetId));
        Assert.Equal(new long[] { 3, 4 }, second.Sets.Select(x => x.PresetId));
        Assert.Equal(1, second.Batch);
    }

    [Fact]
    public async Task Identical_tag_sets_collapse_into_one_candidate()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = new[] { FourSandals[0], Set(9, "A 的雙胞胎", ("clothing.upper", "Shirt"), ("clothing.footwear", "sandals")), FourSandals[1] };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new long[] { 1, 2 }, d.Sets.Select(x => x.PresetId));
    }

    [Fact]
    public async Task Explore_compares_every_dimension_facet_when_nothing_is_covered()
    {
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[]
        {
            Set(11, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")),
            Set(12, "動漫", ("style.genre", "anime"), ("style.palette", "vivid")),
            Set(13, "水彩", ("style.genre", "watercolor"), ("style.palette", "pastel")),
        };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "style");
        Assert.Equal(new[] { "query", "query", "explore" }, d.Sets.Select(x => x.Reason));
        Assert.Equal(13, d.Sets[2].PresetId);
        Assert.Equal(Catalog.FacetsOf("portrait", "style"), presets.VectorCalls.First(c => c.ids.Contains(13)).facets);
    }

    [Fact]
    public async Task Ask_outcome_ends_the_slate()
    {
        // Review Focus 3
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[] { Set(11, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")) };
        await svc.BuildAsync(s, Finalized("1girl"), 1, default);
        Assert.Equal(1, s.LatestSlateTurn);
        var ask = await svc.BuildAsync(s, Ask("style"), 2, default);
        Assert.Null(s.LatestSlateTurn);
        Assert.Null(ask!.Dimensions[0].Batch);                                                        // 追問卡不帶批次與理由
        Assert.Null(ask.Dimensions[0].Sets[0].Reason);
    }

    [Fact]
    public async Task Failed_final_card_records_nothing_as_seen()
    {
        // Review Focus 2
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[] { Set(11, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")) };
        presets.ThrowOn = "clothing.head";                                                            // 最後一個維度才炸
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildAsync(s, Finalized("1girl"), 1, default));
        Assert.Empty(s.SeenFor("style"));
    }

    [Fact]
    public async Task Next_returns_batch_two_avoids_the_first_batch_and_reuses_the_final_tags_as_anchors()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals;
        await svc.BuildAsync(s, Finalized("masterpiece, 1girl, sandals, white socks"), 1, default);
        var next = await svc.NextAsync(s, "clothing", default);
        Assert.Equal(2, next.Batch);
        Assert.Equal(new long[] { 3, 4 }, next.Sets.Select(x => x.PresetId));
        Assert.Equal(2, s.SlateBatch("clothing"));
        Assert.Contains("white socks", presets.Calls.Last(c => c.anchorTags.Count > 0).anchorTags);
        Assert.DoesNotContain("masterpiece", presets.Calls.Last(c => c.anchorTags.Count > 0).anchorTags);
    }

    [Fact]
    public async Task Next_without_candidates_returns_an_empty_row_and_does_not_advance()
    {
        var (svc, s, _, _) = MakeWith(Greedy());
        s.BeginSlate(1, Array.Empty<string>());
        var next = await svc.NextAsync(s, "style", default);
        Assert.Empty(next.Sets);
        Assert.Equal(1, next.Batch);
        Assert.Equal("風格", next.Label);
        Assert.Equal(0, s.SlateBatch("style"));
        Assert.Empty(s.SeenFor("style"));
    }
```


- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RecommendationServiceTests"`
Expected: 新測試 FAIL（定稿卡還是舊的 3 套、`NextAsync` 丟 `NotImplementedException`），既有的追問卡測試 PASS。

- [ ] **Step 4: 實作 `RecommendationService`**

整份 `BuildAsync` 換成下面這樣，並加入其餘方法（`JoinedUserText`、`QueryText`、`AnchorTags`、`MatchedAnchors`、`SimilarAnchorText` 保留原樣）。class 上方的 summary 補一句「定稿卡（2026-09-30 推薦組法設計）改成 2 相關＋1 探索、看過加權延後、可換一批；追問卡照舊」。

```csharp
    public async Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct)
    {
        if (s.Profile is null) return null;
        // 不管這輪出什麼卡，舊的定稿卡都不再是最新的：先收掉換一批，定稿卡再在下面重開（Review Focus 3）
        s.EndSlate();
        var profile = s.Profile;
        IReadOnlyList<string> dimensions = outcome switch
        {
            AskOutcome a => a.Asks.Select(x => x.Dimension).Distinct().Where(d => catalog.FacetsOf(profile, d).Count > 0).ToList(),
            FinalizedOutcome => catalog.DimensionsOf(profile),
            _ => Array.Empty<string>(),
        };
        if (dimensions.Count == 0) return null;
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return null;
        var (vec, anchorVec) = await EmbedAsync(s, dimensions, query, ct);
        // 基礎畫質詞不當錨：每次定稿都有，只會把推薦拉向剛好也寫了 masterpiece 的片段
        var finalTags = outcome is FinalizedOutcome f
            ? TagAttribution.Split(f.Final.Positive).Select(TagAttribution.Normalize).Where(t => t.Length > 0 && !TagAttribution.IsBase(t)).ToList()
            : new List<string>();

        var result = new List<RecommendedDimension>();
        if (outcome is FinalizedOutcome)
        {
            s.BeginSlate(turnIndex, finalTags);
            // 全部維度都成功才記看過：中途失敗時事件不會送出，使用者沒看到的不能被往後推（Review Focus 2）
            var seen = new List<(string dim, IReadOnlyList<string> keys)>();
            foreach (var dim in dimensions)
                if (await SlateAsync(s, dim, 1, vec, anchorVec, finalTags, ct) is { } built)
                {
                    result.Add(built.row);
                    seen.Add((dim, built.keys));
                }
            foreach (var (dim, keys) in seen) s.RecordSlate(dim, 1, keys);
        }
        else
            foreach (var dim in dimensions)
                if (await AskRowAsync(s, dim, vec, anchorVec, finalTags, ct) is { } row) result.Add(row);
        return result.Count == 0 ? null : new RecommendationsEvent(turnIndex, result);
    }

    public async Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct)
    {
        var profile = s.Profile ?? throw new InvalidOperationException("尚未判定題材");
        var batch = s.SlateBatch(dimension) + 1;
        var empty = new RecommendedDimension(dimension, catalog.DimensionLabel(dimension, profile), false, Array.Empty<string>(), Array.Empty<RecommendedSet>(), Batch: batch);
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return empty;
        var (vec, anchorVec) = await EmbedAsync(s, new[] { dimension }, query, ct);
        if (await SlateAsync(s, dimension, batch, vec, anchorVec, s.LastFinalTags, ct) is not { } built) return empty;
        s.RecordSlate(dimension, batch, built.keys);
        return built.row;
    }

    /// <summary>查詢向量與近似錨的向量一次 embed（設計 §6.2）：哪個維度走到近似錨都不會多一次呼叫；沒用到的向量丟掉無妨。</summary>
    private async Task<(float[] vec, Dictionary<(string dim, string facet), float[]> anchorVec)> EmbedAsync(Session s, IReadOnlyList<string> dimensions,
        string query, CancellationToken ct)
    {
        var anchorTexts = new List<(string dim, string facet, string text)>();
        foreach (var dim in dimensions)
            foreach (var facetId in catalog.FacetsOf(s.Profile!, dim))
                if (s.FacetStates.GetValueOrDefault(facetId, FacetState.Missing) == FacetState.Covered && SimilarAnchorText(s, facetId) is { } t)
                    anchorTexts.Add((dim, facetId, t));
        var vectors = await embed.EmbedAsync(new[] { query }.Concat(anchorTexts.Select(a => a.text)).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct);
        return (vectors[0], anchorTexts.Select((a, i) => (a, v: vectors[i + 1])).ToDictionary(x => (x.a.dim, x.a.facet), x => x.v));
    }

    /// <summary>追問卡的一排：原本的做法，一行不改（字面錨 → 近似錨 → 純向量，前一層不到 2 筆才退）。</summary>
    private async Task<RecommendedDimension?> AskRowAsync(Session s, string dim, float[] vec, IReadOnlyDictionary<(string, string), float[]> anchorVec,
        IReadOnlyList<string> finalTags, CancellationToken ct)
    {
        var facets = catalog.FacetsOf(s.Profile!, dim);
        // 預設給 Missing：FacetState 的 default 是 Covered，缺鍵時不能被當成已涵蓋
        var covered = facets.Where(x => s.FacetStates.GetValueOrDefault(x, FacetState.Missing) == FacetState.Covered).ToList();
        var anchors = AnchorTags(s, covered, finalTags);
        IReadOnlyList<PresetCandidate> hits = Array.Empty<PresetCandidate>();
        var anchored = false;
        if (anchors.Count > 0)
        {
            hits = await presets.RecommendAsync(vec, facets, covered, anchors, options.RecommendationTake, ct);
            anchored = hits.Count >= MinAnchoredHits;
        }
        var similar = false;
        IReadOnlyList<string> similarTags = Array.Empty<string>();
        if (!anchored)
        {
            (hits, similarTags) = await SimilarAsync(s, dim, covered, facets, anchorVec, ct);
            similar = hits.Count >= MinAnchoredHits;
            if (!similar) hits = Array.Empty<PresetCandidate>();
        }
        if (!anchored && !similar) hits = await presets.RecommendAsync(vec, facets, Array.Empty<string>(), Array.Empty<string>(), options.RecommendationTake, ct);
        if (hits.Count == 0) return null;
        var matched = anchored ? MatchedAnchors(hits, covered, anchors) : similar ? similarTags : Array.Empty<string>();
        return new RecommendedDimension(dim, catalog.DimensionLabel(dim, s.Profile!), anchored, matched, hits.Select(h => ToSet(s, facets, h)).ToList(), similar);
    }

    /// <summary>定稿卡的一排（推薦組法設計 §3.1）：三層（字面錨、近似錨、純向量）各取 PoolSize 筆、都查，接成一條名單並依 tag 集合合併；
    /// 2 套相關位＋最多 1 套探索位。回傳這一排與要記成看過的 key；由呼叫端決定何時記（整張卡成功才記）。沒有任何候選回 null。</summary>
    private async Task<(RecommendedDimension row, IReadOnlyList<string> keys)?> SlateAsync(Session s, string dim, int batch, float[] vec,
        IReadOnlyDictionary<(string, string), float[]> anchorVec, IReadOnlyList<string> finalTags, CancellationToken ct)
    {
        var facets = catalog.FacetsOf(s.Profile!, dim);
        var covered = facets.Where(x => s.FacetStates.GetValueOrDefault(x, FacetState.Missing) == FacetState.Covered).ToList();
        var anchors = AnchorTags(s, covered, finalTags);
        var pool = options.RecommendationPoolSize;
        var none = Array.Empty<string>();
        var literal = anchors.Count > 0 ? await presets.RecommendAsync(vec, facets, covered, anchors, pool, ct) : Array.Empty<PresetCandidate>();
        var similar = await SimilarHitsAsync(dim, covered, facets, anchorVec, pool, ct);
        var similarFacet = similar.ToDictionary(x => x.c.Id, x => x.facet);
        var plain = await presets.RecommendAsync(vec, facets, none, none, pool, ct);
        var candidates = SlateSelector.Merge(new SlateTier[]
        {
            new(SlateReason.Anchored, literal, h => MatchedAnchors(new[] { h }, covered, anchors)),
            new(SlateReason.Similar, similar.Select(x => x.c).ToList(), h => NormalizedFacetTags(s, similarFacet[h.Id])),
            new(SlateReason.Query, plain, _ => none),
        }, facets);
        if (candidates.Count == 0) return null;

        var seen = s.SeenFor(dim);
        var (penalty, temperature) = (options.RecommendationSeenPenalty, options.RecommendationTemperature);
        var rng = new Random(SlateSelector.Seed(s.Id, s.LatestSlateTurn ?? 0, dim, batch));
        var relevant = SlateSelector.PickRelevant(candidates, seen, penalty, temperature, rng);
        // 探索位只比使用者講過的 facet：比整個維度會挑到「只在沒講的地方不同」的，那不會發生取代（設計 §2）
        var compare = covered.Count > 0 ? covered : facets;
        var explorePool = SlateSelector.Merge(new[] { new SlateTier(SlateReason.Explore, plain, _ => none) }, facets);
        var relevantCandidates = relevant.Select(p => p.Candidate).ToList();
        IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors = explorePool.Count == 0
            ? new Dictionary<(long PresetId, string FacetId), float[]>()
            : await presets.FacetVectorsAsync(explorePool.Select(c => c.Preset.Id).Concat(relevantCandidates.Select(c => c.Preset.Id)).Distinct().ToList(), compare, ct);
        var explore = SlateSelector.PickExplore(explorePool, relevantCandidates, compare, vectors, seen, penalty, temperature, rng);
        var picks = explore is null ? relevant : relevant.Append(explore).ToList();

        var sets = picks.Select(p => ToSet(s, facets, p.Candidate.Preset) with
        {
            Reason = p.Candidate.Reason, AnchorTags = p.Candidate.AnchorTags, Rank = p.Rank, Prob = Math.Round(p.Prob, 4),
        }).ToList();
        var row = new RecommendedDimension(dim, catalog.DimensionLabel(dim, s.Profile!), false, Array.Empty<string>(), sets, Batch: batch);
        return (row, picks.Select(p => p.Candidate.Key).ToList());
    }

    private RecommendedSet ToSet(Session s, IReadOnlyList<string> facets, PresetCandidate h) =>
        new(h.Id, h.Title, h.ImageUrl, h.SourceRef, Math.Round(h.Dist, 3),
            facets.Select(x => new RecommendedFacet(x, catalog.Facets[x].Label,
                FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(x, FacetState.Missing)),
                h.FacetTags.GetValueOrDefault(x) ?? Array.Empty<string>())).ToList());

    private static IReadOnlyList<string> NormalizedFacetTags(Session s, string facetId) =>
        TagAttribution.Split(s.FacetTags.GetValueOrDefault(facetId)).Select(TagAttribution.Normalize).Where(t => t.Length > 0).Distinct().ToList();
```

`SimilarAsync` 拆出 `SimilarHitsAsync`，追問卡行為不變：

```csharp
    /// <summary>近似錨（設計 §6.1）：每個 covered 且有錨向量的 facet 各查一次，同一片段取最小距離，依距離取前 take；
    /// 回傳的 tags 是「有貢獻」的 facet 的 FacetTags（至少一筆進了前 take）。子表沒有這個 facet 的列就跳過它。</summary>
    private async Task<(IReadOnlyList<PresetCandidate> hits, IReadOnlyList<string> tags)> SimilarAsync(Session s, string dim, IReadOnlyList<string> covered,
        IReadOnlyList<string> facets, IReadOnlyDictionary<(string, string), float[]> anchorVec, CancellationToken ct)
    {
        var top = await SimilarHitsAsync(dim, covered, facets, anchorVec, options.RecommendationTake, ct);
        var contributing = covered.Where(f => top.Any(x => x.facet == f)).ToList();
        var tags = new List<string>();
        foreach (var f in contributing)
            foreach (var t in TagAttribution.Split(s.FacetTags.GetValueOrDefault(f)).Select(TagAttribution.Normalize))
                if (t.Length > 0 && !tags.Contains(t)) tags.Add(t);
        return (top.Select(x => x.c).ToList(), tags);
    }

    /// <summary>近似錨的命中與它來自哪個 facet，依距離排好、取前 take。</summary>
    private async Task<List<(PresetCandidate c, string facet)>> SimilarHitsAsync(string dim, IReadOnlyList<string> covered, IReadOnlyList<string> facets,
        IReadOnlyDictionary<(string, string), float[]> anchorVec, int take, CancellationToken ct)
    {
        var best = new Dictionary<long, (PresetCandidate c, string facet)>();
        foreach (var f in covered)
        {
            if (!anchorVec.TryGetValue((dim, f), out var av)) continue;
            if (await presets.FacetPoolSizeAsync(f, ct) == 0) continue;
            foreach (var h in await presets.RecommendSimilarAsync(av, f, facets, options.RecommendationSimilarMaxDist, take, ct))
                if (!best.TryGetValue(h.Id, out var cur) || h.Dist < cur.c.Dist) best[h.Id] = (h, f);
        }
        return best.Values.OrderBy(x => x.c.Dist).Take(take).ToList();
    }
```

刪掉 Step 1 放的 `NextAsync` 空殼。

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RecommendationServiceTests|FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 全部 PASS。既有的 `Finalized_outcome_queries_every_dimension_of_the_profile`（沒 covered → 每維度只查純向量一次，6 次）、`Finalized_adds_positive_tags_to_the_anchors…`、`Query_text_joins…` 應該不用改就過；若 `Finalized_outcome_queries_every_dimension_of_the_profile` 因為 `presets.Calls[5]` 的次序改變而失敗，確認維度順序仍是 profile 的順序後，只調整斷言，不改實作。

- [ ] **Step 6: 同步 `docs/單輪流程說明.md`**

找到「C# 端從 2026-09-25 起多一層**伺服器端的推薦**」那段（約第 436 行），在第 1 點之後插入一點（後面的編號順延）：

```markdown
2. **定稿卡的組法（2026-09-30）**：追問卡照上面的做法；定稿卡每個維度改成 2 套相關位＋1 套探索位。相關位把字面錨、近似錨、純向量三層各取 30 筆接成一條名單，同一維度 tag 集合相同的只留一筆；第 1 位取有效名次最前的，第 2 位依 `exp(−(名次＋10×看過次數)/5)` 抽，看過一次權重剩約 13.5%。探索位從不過濾錨的純向量前 30 名裡，挑在使用者講過的 facet 上跟相關位差最多的（facet 向量），卡片標「換個搭法」。每套標自己的理由（含你講的／接近你講的／最接近你描述的／換個搭法）。定稿卡每排可以「換一批」（`POST /api/sessions/{id}/recommendations/next`），不經過模型。設計見 `docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`。
```

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api/Configuration/Options.cs src/PromptCopilot.Api/Streaming/Recommendations.cs src/PromptCopilot.Api/Orchestration/RecommendationService.cs src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs docs/單輪流程說明.md
git commit -m "feat(api): final cards recommend two relevant sets and one explore set, pushing seen sets back"
```

---

### Task 5: audit 記理由／名次／機率／批次，採用帶 batch

**Files:**
- Modify: `src/PromptCopilot.Api/Sessions/AdoptionComposer.cs`（`AdoptRequest` 加 `Batch`）
- Modify: `src/PromptCopilot.Api/Sessions/Adoption.cs`（`Adoption` 加 `Batch`）
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（`Turn_Completed` payload）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`、`src/PromptCopilot.Api.Tests/Sessions/AdoptionComposerTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（audit `event_type` 清單）

**Interfaces:**
- Consumes: Task 4 的 `RecommendedSet.Reason/Rank/Prob`、`RecommendedDimension.Batch`。
- Produces: `AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string>? Take, int? Batch = null)`；`Adoption(..., IReadOnlyList<string> Replaced, int? Batch = null)`；audit `recommendations.dimensions[]` 多 `batch`、`sets: [{presetId, reason, rank, prob}]`（追問卡省略）；`adoption.batch`（沒送就省略）。

- [ ] **Step 1: 寫失敗的測試**

`AdoptionComposerTests.cs` 加：

```csharp
    [Fact]
    public void Batch_passes_through_to_the_adoption()
    {
        var s = Sess();
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }, Batch: 2), Preset(Tags), s, Catalog, 1);
        Assert.Equal(2, c.Adoption.Batch);
        Assert.Null(AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }), Preset(Tags), s, Catalog, 1).Adoption.Batch);
    }
```

`AgenticOrchestratorTests.cs` 加（放在 `Recommendations_event_follows_final_and_is_audited` 後面）：

```csharp
    [Fact]
    public async Task Slate_recommendations_audit_batch_and_each_set()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(new RecommendationsEvent(1, new[]
        {
            new RecommendedDimension("style", "風格", false, Array.Empty<string>(), new[]
            {
                new RecommendedSet(7, "油畫", null, null, 0.2, Array.Empty<RecommendedFacet>(), "anchored", new[] { "oil painting" }, 0, 1.0),
                new RecommendedSet(8, "水彩", null, null, 0.3, Array.Empty<RecommendedFacet>(), "explore", Array.Empty<string>(), 2, 0.25),
            }, Batch: 1),
        })));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.RunAsync("一個銀髮少女");
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""presetIds":[7,8],"batch":1,"sets":[{"presetId":7,"reason":"anchored","rank":0,"prob":1},{"presetId":8,"reason":"explore","rank":2,"prob":0.25}]""", completed.PayloadJson!);
    }

    [Fact]
    public async Task Adoption_audit_carries_the_batch_when_given()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
        {
            positivePrompt = "masterpiece, 1girl, purple kimono", negativePrompt = "lowres", tips = "t", intentSummary = "和服少女",
            facetStates = new[] { new { facetId = "clothing.upper", state = "covered", tags = "purple kimono" } },
        }) });
        var input = AdoptInput();
        await h.RunAsync(input with { Adoption = input.Adoption! with { Batch = 2 } });
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""replaced":[],"batch":2}""", completed.PayloadJson!);
    }
```

（`h.RunAsync` 若只接受 `string`，照 `Adoption_turn_records_adoption_marks_ledger_and_audits` 的寫法：它呼叫的是 `h.RunAsync(AdoptInput())`，所以已有 `TurnInput` 的多載。）

既有的 `Recommendations_event_follows_final_and_is_audited` 與 `Adoption_turn_records_adoption_marks_ledger_and_audits` 斷言的字串**不能改**：追問卡與沒帶 batch 的採用要跟以前一模一樣。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~Batch_passes_through|FullyQualifiedName~Slate_recommendations_audit|FullyQualifiedName~Adoption_audit_carries"`
Expected: 編譯失敗（`Batch` 不存在）。

- [ ] **Step 3: 實作**

`Adoption.cs`：record 最後加 `int? Batch = null`，summary 補「Batch（2026-09-30）：前端送的第幾批，只給量測用，伺服器不驗證」。

```csharp
public sealed record Adoption(int TurnIndex, long PresetId, string Title, string? SourceRef, string Dimension,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Taken, IReadOnlyList<string> Kept,
    IReadOnlyList<string> Filled, IReadOnlyList<string> Replaced, int? Batch = null);
```

`AdoptionComposer.cs`：

```csharp
/// <summary>POST /messages 的 adopt 欄位（設計 §6.1）。Take：照它的 facet；該維度其餘 facet 視為保留我的。
/// Batch（2026-09-30 推薦組法設計 §4.6）：這套來自定稿卡的第幾批，只寫進 audit，不驗證。</summary>
public sealed record AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string>? Take, int? Batch = null);
```

`Compose` 裡建 `Adoption` 那行改成：

```csharp
        var adoption = new Adoption(turnIndex, preset.Id, preset.Title, preset.SourceRef, req.Dimension, taken, kept, filled, replaced, req.Batch);
```

`AgenticOrchestrator.cs` 的 `Turn_Completed` payload 裡，`recommendations` 與 `adoption` 兩段換成用 `Fields` 省略 null（鍵的順序照舊，新欄位接在後面）：

```csharp
                    ("recommendations", recommended is null ? null : (object)new
                    {
                        dimensions = recommended.Dimensions.Select(d => Fields(
                            ("dimension", d.Dimension), ("anchored", d.Anchored), ("similar", d.Similar), ("presetIds", d.Sets.Select(x => x.PresetId).ToArray()),
                            // 推薦組法（2026-09-30 設計 §5.2）：定稿卡才有；追問卡省略，舊的查詢與報表照舊能讀
                            ("batch", d.Batch),
                            ("sets", d.Batch is null ? null : d.Sets.Select(x => new { presetId = x.PresetId, reason = x.Reason, rank = x.Rank, prob = x.Prob }).ToArray()))).ToArray(),
                    }),
                    ("adoption", input.Adoption is null ? null : (object)Fields(
                        ("presetId", input.Adoption.PresetId), ("dimension", input.Adoption.Dimension),
                        ("take", input.Adoption.Taken.Keys.ToArray()), ("filled", input.Adoption.Filled), ("replaced", input.Adoption.Replaced),
                        ("batch", input.Adoption.Batch)))),
```

`Fields` 的回傳型別是 `Dictionary<string, object?>`，`JsonSerializer` 照插入順序輸出鍵，所以既有字串斷言不變。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS（integration 測試 skip）。

- [ ] **Step 5: 同步主規格 audit 清單**

`docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` 的 audit `event_type` 那一大段（`Turn_Completed` 起那句）：
- `recommendations: { dimensions: [{ dimension, anchored, similar, presetIds }] }` 後面補：「；2026-09-30 起定稿卡多 `batch` 與 `sets: [{ presetId, reason, rank, prob }]`（追問卡省略），見 [推薦組法設計](2026-09-30-recommendation-slate-design.md) §5.2」
- `adoption: { presetId, dimension, take, filled, replaced }` 後面補「、`batch?`（2026-09-30，前端送的第幾批）」
- `Recommendation_Failed` 的 `stage` 補「；`next`（2026-09-30，換一批失敗）」
- 清單加一項：「`Recommendations_Next`（2026-09-30，換一批成功一次一筆；`turn_index` 是那張定稿卡的輪次，`payload` 記 `{ dimension, batch, sets: [{ presetId, reason, rank, prob }] }`，另記延遲）」

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Sessions/Adoption.cs src/PromptCopilot.Api/Sessions/AdoptionComposer.cs src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs src/PromptCopilot.Api.Tests/Sessions/AdoptionComposerTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): audit slate batch, reason, rank and prob per set; adoption carries its batch"
```

---

### Task 6: 換一批端點

**Files:**
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（SSE 事件表 `recommendations` 列、推薦段、端點）

**Interfaces:**
- Consumes: `IRecommendationService.NextAsync`、`Session.LatestSlateTurn`／`SlateBatch`、`IAuditSink`、`OrchestratorOptions.RecommendationTimeoutSeconds`。
- Produces: `POST /api/sessions/{id}/recommendations/next`，body `NextRecommendationsRequest(string? Dimension, int TurnIndex)`；200 回 `RecommendedDimension`（camelCase JSON）；404／409／400／503 回 `ErrorBody`。

- [ ] **Step 1: 寫失敗的測試**

`EndpointTests.cs`：在 `Factory.ConfigureServices` 加 `s.AddSingleton<IRecommendationService>(new StubRecommendations());`，並在 class 內加：

```csharp
    /// <summary>不打 DB：camera 維度模擬推薦失敗，其他回固定的第 2 批。</summary>
    public sealed class StubRecommendations : IRecommendationService
    {
        public Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct) => Task.FromResult<RecommendationsEvent?>(null);
        public Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct) => dimension == "camera"
            ? throw new InvalidOperationException("db down")
            : Task.FromResult(new RecommendedDimension(dimension, "風格", false, Array.Empty<string>(), new[]
            {
                new RecommendedSet(7, "油畫", null, null, 0.2, Array.Empty<RecommendedFacet>(), SlateReason.Query, Array.Empty<string>(), 3, 0.25),
            }, Batch: 2));
    }

    [Fact]
    public async Task Next_returns_the_batch_for_the_latest_final_card()
    {
        var s = PortraitSession();
        s.BeginSlate(3, Array.Empty<string>());
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "style", turnIndex = 3 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("batch").GetInt32());
        Assert.Equal("query", doc.RootElement.GetProperty("sets")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Next_is_409_for_an_older_card_or_after_an_ask_card()
    {
        // Review Focus 3
        var s = PortraitSession();
        s.BeginSlate(3, Array.Empty<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "style", turnIndex = 2 })).StatusCode);
        s.EndSlate();
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "style", turnIndex = 3 })).StatusCode);
    }

    [Fact]
    public async Task Next_is_409_while_a_turn_holds_the_session_lock()
    {
        var s = PortraitSession();
        s.BeginSlate(3, Array.Empty<string>());
        await s.Lock.WaitAsync();
        try { Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "style", turnIndex = 3 })).StatusCode); }
        finally { s.Lock.Release(); }
    }

    [Fact]
    public async Task Next_is_409_when_retrieval_is_off()
    {
        var s = PortraitSession(retrieval: false);
        s.BeginSlate(3, Array.Empty<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "style", turnIndex = 3 })).StatusCode);
    }

    [Fact]
    public async Task Next_is_400_for_an_unknown_or_empty_dimension_and_404_for_an_unknown_session()
    {
        var s = PortraitSession();
        s.BeginSlate(3, Array.Empty<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "nope", turnIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = (string?)null, turnIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/sessions/nope/recommendations/next", new { dimension = "style", turnIndex = 3 })).StatusCode);
    }

    [Fact]
    public async Task Next_is_503_when_the_recommendation_fails_and_the_lock_is_released()
    {
        var s = PortraitSession();
        s.BeginSlate(3, Array.Empty<string>());
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/recommendations/next", new { dimension = "camera", turnIndex = 3 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("再按一次", await r.Content.ReadAsStringAsync());
        Assert.True(await s.Lock.WaitAsync(0)); s.Lock.Release();
    }
```

`using` 補 `PromptCopilot.Api.Orchestration`（`SlateReason`、`IRecommendationService`、`TurnOutcome`）——檔案頂端已有，確認即可。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~EndpointTests"`
Expected: `Next_*` 測試 FAIL（404 路由不存在）。

- [ ] **Step 3: 實作**

`SessionEndpoints.cs` 檔頭 record 區加：

```csharp
/// <summary>換一批（2026-09-30 推薦組法設計 §4.5）。TurnIndex：前端那張定稿卡的輪次，必須是最新一張。</summary>
public sealed record NextRecommendationsRequest(string? Dimension, int TurnIndex);
```

`Map` 裡 `save-to-shared` 之前加端點（`using System.Diagnostics;` 補在檔頭）：

```csharp
        g.MapPost("/{id}/recommendations/next", async (string id, NextRecommendationsRequest req, SessionStore store, IRecommendationService recommendations,
            FacetCatalog catalog, IAuditSink audit, OrchestratorOptions options, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            // 會讀寫看過次數與批次；那一輪還在跑時不能動
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            try
            {
                if (!s.RetrievalEnabled) return Results.Conflict(new ErrorBody("這段對話沒有知識庫，沒有組合可以換"));
                if (s.LatestSlateTurn is null || s.LatestSlateTurn != req.TurnIndex) return Results.Conflict(new ErrorBody("只有最新一張定稿卡可以換一批"));
                if (string.IsNullOrWhiteSpace(req.Dimension) || s.Profile is null || !catalog.DimensionsOf(s.Profile).Contains(req.Dimension))
                    return Results.BadRequest(new ErrorBody("dimension 不屬於這段對話的題材"));
                var sw = Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(options.RecommendationTimeoutSeconds));
                RecommendedDimension row;
                try { row = await recommendations.NextAsync(s, req.Dimension, cts.Token); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    app.Logger.LogWarning(e, "next recommendations failed for session {SessionId} dimension {Dimension}", s.Id, req.Dimension);
                    await TryAuditAsync(audit, new AuditEntry(s.Id, req.TurnIndex, "Recommendation_Failed",
                        PayloadJson: JsonSerializer.Serialize(new { stage = "next", errorClass = e is OperationCanceledException ? "Timeout" : e.GetType().Name })), app.Logger);
                    return Results.Json(new ErrorBody("換一批失敗，再按一次"), statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (row.Sets.Count > 0)
                    await TryAuditAsync(audit, new AuditEntry(s.Id, req.TurnIndex, "Recommendations_Next",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            dimension = row.Dimension, batch = row.Batch,
                            sets = row.Sets.Select(x => new { presetId = x.PresetId, reason = x.Reason, rank = x.Rank, prob = x.Prob }),
                        }), LatencyMs: (int)sw.ElapsedMilliseconds), app.Logger);
                return Results.Ok(row);
            }
            finally { s.Lock.Release(); }
        })
        .WithSummary("定稿卡的某個維度換一批推薦")
        .WithDescription("""
            body：`{"dimension": "clothing", "turnIndex": 3}`。`turnIndex` 是那張定稿卡的輪次，必須是這段對話最新一張定稿卡（之後出過追問卡就不行）。不經過模型、不算一輪。

            回一個維度的推薦，形狀同 `recommendations` 事件裡的一排：`{ dimension, label, anchored: false, anchorTags: [], batch, sets: [{ presetId, title, imageUrl?, sourceRef?, dist, facets, reason, anchorTags, rank, prob }] }`。`batch` 是第幾批（定稿卡上的那批是 1）；`reason` 是 `anchored`（含你講的）／`similar`（接近你講的）／`query`（最接近你描述的）／`explore`（換個搭法）。看過的組合會被往後延，但不會被踢掉；`sets` 為空表示這個維度沒有更多了。挑法見 `docs/superpowers/specs/2026-09-30-recommendation-slate-design.md` §3。

            - `404`：session 不存在或已過期
            - `409`：這個 session 還有一輪在跑；`turnIndex` 不是最新一張定稿卡；這段對話 `retrieval: off`
            - `400`：`dimension` 空白或不屬於這段對話的題材
            - `503`：推薦失敗或逾時，可以再按一次
            """)
        .Produces<RecommendedDimension>(StatusCodes.Status200OK)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict)
        .Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
```

`SessionEndpoints` class 裡加共用的稽核旁路（跟 `save-to-shared` 的 try/catch 同一個原則）：

```csharp
    /// <summary>稽核是旁路：寫不進去不能讓已成立的回應變成 500。</summary>
    private static async Task TryAuditAsync(IAuditSink audit, AuditEntry entry, ILogger logger)
    {
        try { await audit.WriteAsync(entry, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "audit write failed: {EventType} session {SessionId}", entry.EventType, entry.SessionId);
        }
    }
```

`messages` 端點描述表裡 `recommendations` 那一列，接在「見 `2026-09-25-set-recommendations-design.md` §5」之後補：「。定稿卡（2026-09-30）：每排 2 套相關＋1 套探索，排多一個 `batch`，每套多 `reason`／`anchorTags`／`rank`／`prob`，排層級的 `anchored`／`similar` 固定 false；可用 `POST /api/sessions/{id}/recommendations/next` 換一批」。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS，包括 `Every_operation_has_a_summary_and_a_description`。

- [ ] **Step 5: 同步主規格**

`docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`：
- SSE 事件表 `recommendations` 列（約第 773 行）最後補同一句（定稿卡的 `batch`、`reason`／`anchorTags`／`rank`／`prob`，連到推薦組法設計 §5.1）。
- 推薦段（約第 736 行「卡片上有錨的顯示…」那段）最後補：「2026-09-30 起定稿卡改成 2 套相關＋1 套探索、看過加權延後、可換一批，追問卡不變，見 [推薦組法設計](2026-09-30-recommendation-slate-design.md)。」
- 端點清單（找 `save-to-shared` 出現的端點列表）加 `POST /api/sessions/{id}/recommendations/next` 一行，說明同 Swagger 描述第一段。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): POST recommendations/next returns the next batch for the latest final card"
```

---

### Task 7: 前端型別、理由文案、批次合併、採用帶 batch

**Files:**
- Modify: `src/PromptCopilot.Frontend/types/api.ts`
- Modify: `src/PromptCopilot.Frontend/lib/copy.ts`
- Modify: `src/PromptCopilot.Frontend/lib/reducer.ts`
- Modify: `src/PromptCopilot.Frontend/lib/adopt.ts`
- Test: `src/PromptCopilot.Frontend/tests/reducer.test.ts`、`tests/copy.test.ts`、`tests/adopt.test.ts`

**Interfaces:**
- Produces:
  - `type SetReason = 'anchored' | 'similar' | 'query' | 'explore'`
  - `RecommendedSet` 加 `reason?: SetReason | null; anchorTags?: string[] | null; rank?: number | null; prob?: number | null; batch?: number | null`
  - `RecommendedDimension` 加 `batch?: number | null`
  - `AdoptRequest` 加 `batch?: number`
  - `setReasonLabel(set: { reason?: SetReason | null; anchorTags?: string[] | null }): string | null`
  - `appendBatch(state: ChatState, turnIndex: number, row: RecommendedDimension): ChatState`
  - `adoptPayload(presetId: number, dimension: string, rows: AdoptRow[], batch?: number | null): AdoptRequest | null`

- [ ] **Step 1: 寫失敗的測試**

`tests/copy.test.ts` 加（import 補 `setReasonLabel`）：

```ts
describe('setReasonLabel', () => {
  it('四種理由的文案', () => {
    expect(setReasonLabel({ reason: 'anchored', anchorTags: ['sandals'] })).toBe('含你講的 sandals')
    expect(setReasonLabel({ reason: 'similar', anchorTags: ['slippers', 'beret'] })).toBe('接近你講的 slippers, beret')
    expect(setReasonLabel({ reason: 'query', anchorTags: [] })).toBe('最接近你描述的')
    expect(setReasonLabel({ reason: 'explore', anchorTags: [] })).toBe('換個搭法')
  })
  it('setReasonLabel returns null for sets without a reason', () => {
    // Review Focus 5：追問卡與舊 sessionStorage 的組合沒有 reason，畫面照舊顯示列層級文案
    expect(setReasonLabel({})).toBeNull()
    expect(setReasonLabel({ reason: null })).toBeNull()
  })
  it('有錨的理由沒有 anchorTags 時不留尾巴空白', () => {
    expect(setReasonLabel({ reason: 'anchored' })).toBe('含你講的')
  })
})
```

`tests/adopt.test.ts` 加：

```ts
describe('adoptPayload batch', () => {
  const rows = [{ facetId: 'style.genre', label: '流派', state: 'missing' as const, setTags: ['oil painting'], available: true, choice: 'set' as const }]
  it('有批次就帶上，沒有就不帶', () => {
    expect(adoptPayload(7, 'style', rows, 2)).toEqual({ presetId: 7, dimension: 'style', take: ['style.genre'], batch: 2 })
    expect(adoptPayload(7, 'style', rows)).toEqual({ presetId: 7, dimension: 'style', take: ['style.genre'] })
    expect(adoptPayload(7, 'style', rows, null)).toEqual({ presetId: 7, dimension: 'style', take: ['style.genre'] })
  })
})
```

`tests/reducer.test.ts` 加（import 補 `appendBatch`、`RecommendedDimension`）：

```ts
const slateRecs: AgentEvent = { type: 'recommendations', turnIndex: 1, dimensions: [{ dimension: 'style', label: '風格', anchored: false, anchorTags: [], batch: 1, sets: [
  { presetId: 7, title: '油畫', dist: 0.2, facets: [], reason: 'query', anchorTags: [], rank: 0, prob: 1 },
  { presetId: 8, title: '水彩', dist: 0.3, facets: [], reason: 'explore', anchorTags: [], rank: 1, prob: 0.4 },
] }] }
const batch2: RecommendedDimension = { dimension: 'style', label: '風格', anchored: false, anchorTags: [], batch: 2, sets: [
  { presetId: 9, title: '版畫', dist: 0.3, facets: [], reason: 'query', anchorTags: [], rank: 3, prob: 0.2 },
  { presetId: 7, title: '油畫', dist: 0.2, facets: [], reason: 'query', anchorTags: [], rank: 0, prob: 1 },
] }

describe('slate batches', () => {
  it('定稿卡的推薦事件把批次蓋到每一套上；追問卡的不動', () => {
    let s = applyEvent(started(), finalized)
    s = applyEvent(s, slateRecs)
    const sets = (s.transcript.at(-1) as any).recommendations.dimensions[0].sets
    expect(sets.map((x: any) => x.batch)).toEqual([1, 1])
    let a = applyEvent(started(), ask)
    a = applyEvent(a, recs)
    expect((a.transcript.at(-1) as any).recommendations.dimensions[0].sets[0].batch).toBeUndefined()
  })

  it('appendBatch keeps a repeated preset as a separate set with its own batch', () => {
    // Review Focus 1
    let s = applyEvent(applyEvent(started(), finalized), slateRecs)
    s = appendBatch(s, 1, batch2)
    const row = (s.transcript.at(-1) as any).recommendations.dimensions[0]
    expect(row.batch).toBe(2)
    expect(row.sets.map((x: any) => [x.presetId, x.batch])).toEqual([[7, 1], [8, 1], [9, 2], [7, 2]])
  })

  it('appendBatch ignores an unknown turn or dimension', () => {
    const s = applyEvent(applyEvent(started(), finalized), slateRecs)
    expect(appendBatch(s, 9, batch2)).toBe(s)
    expect(appendBatch(s, 1, { ...batch2, dimension: 'scene' })).toBe(s)
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && export PATH="$PATH:/c/Users/USER/AppData/Local/Microsoft/WinGet/Links" && npm test`
Expected: 新測試 FAIL（`setReasonLabel`／`appendBatch` 不存在、`batch` 沒蓋上）。

- [ ] **Step 3: 實作**

`types/api.ts`：

```ts
/** 推薦組法（2026-09-30）：定稿卡每套的理由；追問卡沒有 */
export type SetReason = 'anchored' | 'similar' | 'query' | 'explore'
/** reason／anchorTags／rank／prob（2026-09-30）：只有定稿卡有。batch 是前端蓋上去的：這套來自第幾批（換一批往右接） */
export interface RecommendedSet { presetId: number; title: string; imageUrl?: string | null; sourceRef?: string | null; dist: number; facets: RecommendedFacet[];
  reason?: SetReason | null; anchorTags?: string[] | null; rank?: number | null; prob?: number | null; batch?: number | null }
/** similar（2026-09-29）：…（既有說明保留）。batch（2026-09-30）：定稿卡目前最新是第幾批；追問卡沒有 */
export interface RecommendedDimension { dimension: string; label: string; anchored: boolean; similar?: boolean; anchorTags: string[]; sets: RecommendedSet[]; batch?: number | null }
```

`AdoptRequest` 加 `batch?: number`，註解補「batch（2026-09-30）：這套來自定稿卡第幾批，只給量測用」。

`lib/copy.ts`（`recommendationLead` 之後）：

```ts
/** 定稿卡每一套的理由（推薦組法設計 §6）。沒有 reason（追問卡、舊資料）回 null，畫面改顯示列層級的 recommendationLead。 */
export function setReasonLabel(set: { reason?: SetReason | null; anchorTags?: string[] | null }): string | null {
  const tags = (set.anchorTags ?? []).join(', ')
  switch (set.reason) {
    case 'anchored': return tags ? `含你講的 ${tags}` : '含你講的'
    case 'similar': return tags ? `接近你講的 ${tags}` : '接近你講的'
    case 'query': return '最接近你描述的'
    case 'explore': return '換個搭法'
    default: return null
  }
}
```

（import 補 `import type { SetReason } from '../types/api'`，若檔案已有 type import 就併進去。）

`lib/reducer.ts` 的 `case 'recommendations'` 改成：

```ts
    case 'recommendations': {
      const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.turnIndex === ev.turnIndex)
      if (idx < 0) return state
      const { type: _t, ...recs } = ev
      // 定稿卡（有 batch）把批次蓋到每一套上：換一批往右接之後，每套要知道自己是第幾批（畫分隔線、採用時帶上）
      const stamped: Recommendations = {
        ...(recs as Recommendations),
        dimensions: (recs as Recommendations).dimensions.map(d => (d.batch == null ? d : { ...d, sets: d.sets.map(x => ({ ...x, batch: d.batch })) })),
      }
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as FinalEntry), recommendations: stamped }
      return { ...state, transcript }
    }
```

同檔加 export（import 補 `RecommendedDimension`）：

```ts
/** 換一批（推薦組法設計 §6）：新的一批接在那一排右邊，每套蓋上自己的批次。同一個 preset 可能在兩批都出現（看過的會被權重輪回來），
 *  兩筆都留：批次不同就是不同的一套。找不到那張卡或那一排就原樣回傳。 */
export function appendBatch(state: ChatState, turnIndex: number, row: RecommendedDimension): ChatState {
  const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.turnIndex === turnIndex)
  if (idx < 0) return state
  const entry = state.transcript[idx] as FinalEntry
  const recs = entry.recommendations
  if (!recs || !recs.dimensions.some(d => d.dimension === row.dimension)) return state
  const dimensions = recs.dimensions.map(d => (d.dimension !== row.dimension ? d
    : { ...d, batch: row.batch, sets: [...d.sets, ...row.sets.map(x => ({ ...x, batch: row.batch }))] }))
  const transcript = state.transcript.slice()
  transcript[idx] = { ...entry, recommendations: { ...recs, dimensions } }
  return { ...state, transcript }
}
```

`lib/adopt.ts`：

```ts
export function adoptPayload(presetId: number, dimension: string, rows: AdoptRow[], batch?: number | null): AdoptRequest | null {
  const take = rows.filter(r => r.choice === 'set').map(r => r.facetId)
  if (!take.length) return null
  return typeof batch === 'number' ? { presetId, dimension, take, batch } : { presetId, dimension, take }
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/types/api.ts src/PromptCopilot.Frontend/lib/copy.ts src/PromptCopilot.Frontend/lib/reducer.ts src/PromptCopilot.Frontend/lib/adopt.ts src/PromptCopilot.Frontend/tests
git commit -m "feat(frontend): slate set reasons, batch stamping and appendBatch; adoption carries the batch"
```

---

### Task 8: 前端換一批（API、store、畫面）

**Files:**
- Modify: `src/PromptCopilot.Frontend/composables/useApi.ts`
- Modify: `src/PromptCopilot.Frontend/stores/session.ts`
- Modify: `src/PromptCopilot.Frontend/components/RecommendationStrip.vue`
- Modify: `src/PromptCopilot.Frontend/components/AdoptDialog.vue`

**Interfaces:**
- Consumes: Task 7 的 `appendBatch`、`setReasonLabel`、`adoptPayload(..., batch)`；Task 6 的端點。
- Produces:
  - `useApi().nextRecommendations(id: string, dimension: string, turnIndex: number): Promise<NextResult>`，`type NextResult = { ok: true; row: RecommendedDimension } | { ok: false; status: number; error: string }`
  - store：`batchState: Ref<Record<string, 'loading' | 'error' | 'exhausted'>>`（key `${turnIndex}:${dimension}`）、`nextBatch(turnIndex: number, dimension: string): Promise<void>`

- [ ] **Step 1: `useApi` 加呼叫**

```ts
export type NextResult = { ok: true; row: RecommendedDimension } | { ok: false; status: number; error: string }
```

（放在 `SaveResult` 旁，import 補 `RecommendedDimension`。）`saveToShared` 之後加：

```ts
  /** 換一批（推薦組法設計 §4.5）。失敗回狀態碼與後端的理由，由 store 決定怎麼顯示。 */
  async function nextRecommendations(id: string, dimension: string, turnIndex: number): Promise<NextResult> {
    const r = await fetch(`${base}/api/sessions/${encodeURIComponent(id)}/recommendations/next`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ dimension, turnIndex }),
    })
    if (r.ok) return { ok: true, row: await r.json() }
    let error = `HTTP ${r.status}`
    try { error = (await r.json()).error ?? error } catch { /* 沒 body 就用狀態碼 */ }
    return { ok: false, status: r.status, error }
  }
```

return 物件加 `nextRecommendations`。

- [ ] **Step 2: store 加狀態與動作**

`stores/session.ts`：import 從 `../lib/reducer` 多拿 `appendBatch`。`adoptTarget` 附近加：

```ts
  /** 換一批的狀態，key 是 `${turnIndex}:${dimension}`。不存：重載後回到可以再按。 */
  const batchState = ref<Record<string, 'loading' | 'error' | 'exhausted'>>({})

  /** 換一批：只有最新一張卡、沒在跑回合時能按（伺服器也會擋）。成功就接在那一排右邊並存檔；空批代表這個維度沒有更多了。 */
  async function nextBatch(turnIndex: number, dimension: string) {
    const key = `${turnIndex}:${dimension}`
    const id = state.value.sessionId
    if (!id || busy.value || turnIndex !== latestRecommendableTurn.value || batchState.value[key] === 'loading') return
    batchState.value = { ...batchState.value, [key]: 'loading' }
    try {
      const r = await api.nextRecommendations(id, dimension, turnIndex)
      if (!r.ok) { batchState.value = { ...batchState.value, [key]: 'error' }; return }
      if (r.row.sets.length === 0) { batchState.value = { ...batchState.value, [key]: 'exhausted' }; return }
      state.value = appendBatch(state.value, turnIndex, r.row)
      const { [key]: _done, ...rest } = batchState.value
      batchState.value = rest
      persist()
    } catch {
      batchState.value = { ...batchState.value, [key]: 'error' }
    }
  }
```

`newSession()` 裡 `adoptTarget.value = null` 旁加 `batchState.value = {}`。return 物件加 `batchState, nextBatch`。

- [ ] **Step 3: `RecommendationStrip.vue`**

模板換成下面這樣（script 補 import 與兩個 computed）：

```vue
<template>
  <section data-section="recommendations" class="mt-4 border-t border-rule pt-3">
    <h4 class="text-xs font-bold">參考組合</h4>
    <p class="mt-0.5 text-[11px] text-muted">知識庫裡真實存在、有圖的整套設定。圖片來自來源網站，著作權屬原作者，點圖看出處。</p>
    <div v-for="d in recs.dimensions" :key="d.dimension" class="mt-2.5" :data-dimension="d.dimension">
      <p class="flex flex-wrap items-baseline gap-x-2 text-xs">
        <span class="font-medium">{{ d.label }}</span>
        <!-- 定稿卡（有 batch）理由看每一套；追問卡照舊顯示列層級文案 -->
        <span v-if="d.batch == null" class="text-[11px] text-muted">{{ recommendationLead(d) }}</span>
      </p>
      <ul class="mt-1.5 flex gap-2 overflow-x-auto pb-1">
        <template v-for="(set, i) in d.sets" :key="`${set.batch ?? 1}-${set.presetId}`">
          <li v-if="i > 0 && set.batch != null && set.batch !== d.sets[i - 1].batch" class="flex shrink-0 flex-col items-center justify-start gap-1 pt-10" data-batch-divider>
            <span class="h-16 w-px bg-rule" />
            <span class="text-[10px] text-muted">第 {{ set.batch }} 批</span>
          </li>
          <li class="w-28 shrink-0" :data-reason="set.reason ?? undefined">
            <button type="button" class="block w-full text-left" :title="set.title" @click="s.openDrawer(set.presetId)">
              <span v-if="set.imageUrl && !broken.has(set.presetId)" class="relative block h-28 w-28 rounded-[3px]"
                    :class="set.reason === 'explore' ? 'outline-dashed outline-1 outline-offset-2 outline-ink/60' : ''">
                <img :src="set.imageUrl" :alt="set.title" class="h-28 w-28 rounded-[3px] object-cover" loading="lazy"
                     referrerpolicy="no-referrer" @error="broken.add(set.presetId)">
                <span v-if="sourceName(set.sourceRef)"
                      class="absolute bottom-0.5 right-0.5 rounded-[2px] bg-ink/70 px-1 text-[9px] leading-4 text-paper">{{ sourceName(set.sourceRef) }}</span>
              </span>
              <div v-else class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule text-xs text-muted">無圖</div>
              <span class="mt-1 block truncate text-[11px] text-ink">{{ set.title }}</span>
              <span v-if="setReasonLabel(set)" class="block truncate text-[10px] text-muted" :title="setReasonLabel(set)!">{{ setReasonLabel(set) }}</span>
            </button>
            <button type="button" :disabled="!adoptable" :title="adoptable ? '逐項選擇要照它的' : '已有新的結果，這張卡的推薦不能再採用'"
                    class="mt-1 w-full rounded-md border border-ink/80 px-2 py-1 text-[11px] font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                    @click="s.openAdopt(set, d.dimension, turnIndex)">採用</button>
          </li>
        </template>
        <li v-if="d.batch != null && adoptable" class="w-28 shrink-0">
          <p v-if="batchOf(d.dimension) === 'exhausted'" class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule p-2 text-center text-[11px] text-muted">這個維度沒有更多了</p>
          <button v-else type="button" data-action="next-batch" :disabled="batchOf(d.dimension) === 'loading'"
                  class="flex h-28 w-28 flex-col items-center justify-center gap-1 rounded-[3px] border border-ink/60 text-xs hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                  @click="s.nextBatch(turnIndex, d.dimension)">
            <span>{{ batchOf(d.dimension) === 'loading' ? '換一批中…' : '換一批' }}</span>
            <span v-if="batchOf(d.dimension) === 'error'" class="text-[10px] text-red-700">換一批失敗，再按一次</span>
          </button>
        </li>
      </ul>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { Recommendations } from '../types/api'
import { recommendationLead, setReasonLabel, sourceName } from '../lib/copy'
/** recs：該輪的 recommendations 事件；turnIndex：卡片的輪次。只有最新一張追問卡／定稿卡可以採用與換一批（舊卡的狀態已失效）。 */
const props = defineProps<{ recs: Recommendations; turnIndex: number }>()
const s = useSessionStore()
const broken = reactive(new Set<number>())
const adoptable = computed(() => s.latestRecommendableTurn === props.turnIndex && !s.busy)
function batchOf(dimension: string) { return s.batchState[`${props.turnIndex}:${dimension}`] }
</script>
```

（`text-red-700`：先看 `tailwind.config.ts` 與其他元件的錯誤色用什麼 token，例如 `FailureNotice.vue`；有專案自己的色名就改用它。）

- [ ] **Step 4: `AdoptDialog.vue` 帶上批次**

```ts
const payload = computed(() => (t.value ? adoptPayload(t.value.set.presetId, t.value.dimension, rows.value, t.value.set.batch) : null))
```

- [ ] **Step 5: 跑測試與型別檢查**

Run: `cd src/PromptCopilot.Frontend && npm test && npx nuxi typecheck`
Expected: 測試全 PASS、typecheck 沒有錯誤。（`nuxi typecheck` 若因環境缺套件跑不起來，改跑 `npm run build`，要成功。）

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Frontend/composables/useApi.ts src/PromptCopilot.Frontend/stores/session.ts src/PromptCopilot.Frontend/components/RecommendationStrip.vue src/PromptCopilot.Frontend/components/AdoptDialog.vue
git commit -m "feat(frontend): next-batch tile appends batches to final-card rows with per-set reasons"
```

---

### Task 9: `adoption_report.py` 的「定稿卡推薦組法」一節

**Files:**
- Modify: `scripts/adoption_report.py`
- Test: `scripts/tests/test_adoption_report.py`

**Interfaces:**
- Produces: `Turn(session_id, turn_index, payload, event_type="Turn_Completed")`；`build_slate_section(completed: list[Turn], nexts: list[Turn]) -> list[str]`；`build_report` 的輸出最後附上這一節（沒有定稿卡推薦時不附）。

- [ ] **Step 1: 寫失敗的測試**

`scripts/tests/test_adoption_report.py` 加：

```python
SLATE = {"dimensions": [{"dimension": "clothing", "anchored": False, "similar": False, "presetIds": [1, 2, 3], "batch": 1, "sets": [
    {"presetId": 1, "reason": "anchored", "rank": 0, "prob": 1},
    {"presetId": 2, "reason": "anchored", "rank": 4, "prob": 0.3},
    {"presetId": 3, "reason": "explore", "rank": 0, "prob": 0.5},
]}]}


def adopt(pid, replaced=(), batch=None):
    a = {"presetId": pid, "dimension": "clothing", "take": ["clothing.footwear"], "filled": [], "replaced": list(replaced)}
    if batch is not None:
        a["batch"] = batch
    return a


def nxt(session, turn, batch, sets):
    return Turn(session, turn, {"dimension": "clothing", "batch": batch, "sets": sets}, "Recommendations_Next")


def test_slate_section_counts_reasons_and_replace_rate():
    text = build_report([fin("a", 1, rec=SLATE), fin("a", 2, adoption=adopt(3, replaced=["clothing.footwear"]))])
    assert "## 定稿卡推薦組法" in text
    assert "| 換個搭法 | 1 | 1/1（100.0%） | 1/1（100.0%） | 1.0 |" in text
    assert "| 含你講的 | 2 | 0/2（0.0%） | 0/0（—） | — |" in text


def test_next_batch_sets_join_the_row_and_adoption_with_batch_links_there():
    turns = [fin("a", 1, rec=SLATE), nxt("a", 1, 2, [{"presetId": 9, "reason": "query", "rank": 7, "prob": 0.1}]),
             fin("a", 2, adoption=adopt(9, batch=2))]
    text = build_report(turns)
    assert "- 定稿排按過換一批：1/1（100.0%）；平均每排按 1.0 次" in text
    assert "- 採用來自第 2 批以後：1/1（100.0%）" in text
    assert "| 5–9 | 1 | 1 |" in text


def test_adoption_with_batch_links_to_that_batch_even_if_the_preset_repeats():
    # Review Focus 1：看過的會被權重輪回來，同一個 preset 可能在兩批都出現
    turns = [fin("a", 1, rec=SLATE), nxt("a", 1, 2, [{"presetId": 1, "reason": "anchored", "rank": 0, "prob": 1}]),
             fin("a", 2, adoption=adopt(1, batch=1))]
    text = build_report(turns)
    assert "- 採用來自第 2 批以後：0/1（0.0%）" in text


def test_old_rows_without_sets_infer_reason_from_row_flags():
    # Review Focus 5
    text = build_report([fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING)])
    assert "| 含你講的 | 3 | 1/3（33.3%） | 1/1（100.0%） | 1.0 |" in text
    assert "| 0 | 0 | 0 |" in text                                   # 舊資料沒有名次，不進分桶


def test_no_final_card_recommendations_means_no_slate_section():
    assert "定稿卡推薦組法" not in build_report([ask("a", 1, rec=REC_CLOTHING)])
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -v`
Expected: 新測試 FAIL（沒有「定稿卡推薦組法」、`Turn` 不收第 4 個參數）。

- [ ] **Step 3: 實作**

`Turn` 加欄位、SQL 撈兩種事件：

```python
SQL = """
SELECT session_id, turn_index, payload, event_type
FROM audit_logs
WHERE event_type IN ('Turn_Completed', 'Recommendations_Next') AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
ORDER BY session_id, turn_index, id
"""


@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict
    event_type: str = "Turn_Completed"


def fetch_turns(conn, since: date | None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2]), r[3]) for r in rows]
```

`build_report` 開頭改成只拿 `Turn_Completed` 算既有的數字，並在兩個 return 前附上新的一節：

```python
def build_report(turns: list[Turn]) -> str:
    completed = [t for t in turns if t.event_type == "Turn_Completed"]
    nexts = [t for t in turns if t.event_type == "Recommendations_Next"]
    if not completed:
        return "沒有 Turn_Completed 紀錄。"
    slate = build_slate_section(completed, nexts)
    ordered = sorted(completed, key=lambda t: (t.session_id, t.turn_index))
    # ……以下既有程式不變，但把用到 `turns` 的地方（tagOrigins 那行）改成 `completed`……
```

兩處 `return "\n".join(lines)` 改成 `return "\n".join(lines + slate)`。

新增（放在 `build_report` 之前）：

```python
REASONS = [("anchored", "含你講的"), ("similar", "接近你講的"), ("query", "最接近你描述的"), ("explore", "換個搭法")]
BUCKETS = [("0", 0, 0), ("1–4", 1, 4), ("5–9", 5, 9), ("10+", 10, None)]


def _bucket(rank) -> str | None:
    if rank is None:
        return None
    return next(name for name, lo, hi in BUCKETS if rank >= lo and (hi is None or rank <= hi))


def _row_sets(d: dict) -> list[dict]:
    """一排的每一套：新資料照 sets；舊資料照列層級的 anchored／similar 推回理由，批次當 1、名次缺值（推薦組法設計 §7）。"""
    if d.get("sets"):
        return [dict(s, batch=d.get("batch") or 1) for s in d["sets"]]
    reason = "anchored" if d.get("anchored") else "similar" if d.get("similar") else "query"
    return [{"presetId": p, "reason": reason, "rank": None, "prob": None, "batch": 1} for p in d.get("presetIds", [])]


def _match(sets: list[dict], adoption: dict) -> dict | None:
    """採用對回哪一套：有 batch 就找那一批；沒有就取最後出現的那一套（設計 §7）。"""
    pid, batch = adoption.get("presetId"), adoption.get("batch")
    hits = [s for s in sets if s["presetId"] == pid and (batch is None or s.get("batch") == batch)]
    return hits[-1] if hits else None


def build_slate_section(completed: list[Turn], nexts: list[Turn]) -> list[str]:
    """定稿卡推薦組法（設計 2026-09-30-recommendation-slate-design.md §7）：分理由的採用率與取代率、換一批、名次與採用。
    採用對回卡片的規則跟上面的採用率相同：同 session 在它之前最近的一張追問卡或定稿卡；這一節只算定稿卡。"""
    cards: dict[tuple[str, int], dict[str, list[dict]]] = {}
    for t in completed:
        rec = t.payload.get("recommendations")
        if t.payload.get("outcome") == FINAL and rec:
            cards[(t.session_id, t.turn_index)] = {d["dimension"]: _row_sets(d) for d in rec.get("dimensions", [])}
    if not cards:
        return []
    for n in nexts:
        rows = cards.get((n.session_id, n.turn_index))
        if rows is not None:
            rows.setdefault(n.payload.get("dimension"), []).extend(dict(s, batch=n.payload.get("batch")) for s in n.payload.get("sets", []))

    shown, bucket_shown = Counter(), Counter()
    for rows in cards.values():
        for sets in rows.values():
            for s in sets:
                shown[s["reason"]] += 1
                if (b := _bucket(s.get("rank"))) is not None:
                    bucket_shown[b] += 1

    adopted, adoptions, replaced, replaced_facets, bucket_adopted = Counter(), Counter(), Counter(), Counter(), Counter()
    credited: set = set()
    linked = later = 0
    session, card = None, None
    for t in sorted(completed, key=lambda t: (t.session_id, t.turn_index)):
        if t.session_id != session:
            session, card = t.session_id, None
        a = t.payload.get("adoption")
        if a and card in cards and (s := _match(cards[card].get(a.get("dimension"), []), a)) is not None:
            r = s["reason"]
            linked += 1
            adoptions[r] += 1
            replaced[r] += bool(a.get("replaced"))
            replaced_facets[r] += len(a.get("replaced", []))
            later += (s.get("batch") or 1) >= 2
            key = (card, a.get("dimension"), s["presetId"], s.get("batch"))
            if key not in credited:  # 同一套採用兩次只算一次，採用率才不會超過 100%
                credited.add(key)
                adopted[r] += 1
                if (b := _bucket(s.get("rank"))) is not None:
                    bucket_adopted[b] += 1
        if t.payload.get("outcome") in (ASK, FINAL):
            card = (t.session_id, t.turn_index)

    rows = [sets for r in cards.values() for sets in r.values()]
    batches = [max((s.get("batch") or 1) for s in sets) if sets else 1 for sets in rows]
    refreshed = sum(b >= 2 for b in batches)
    presses = sum(b - 1 for b in batches) / len(batches) if batches else 0

    lines = ["", "## 定稿卡推薦組法", "",
             "| 理由 | 出現 | 採用率 | 取代率 | 平均換掉 facet |", "| :--- | ---: | ---: | ---: | ---: |"]
    for reason, label in REASONS:
        avg = f"{replaced_facets[reason] / adoptions[reason]:.1f}" if adoptions[reason] else "—"
        lines.append(f"| {label} | {shown[reason]} | {_pct(adopted[reason], shown[reason])} | {_pct(replaced[reason], adoptions[reason])} | {avg} |")
    lines += ["", f"- 定稿排按過換一批：{_pct(refreshed, len(batches))}；平均每排按 {presses:.1f} 次",
              f"- 採用來自第 2 批以後：{_pct(later, linked)}", "",
              "| 原名次 | 出現 | 採用 |", "| :--- | ---: | ---: |"]
    lines += [f"| {name} | {bucket_shown[name]} | {bucket_adopted[name]} |" for name, _, _ in BUCKETS]
    return lines
```

檔頭 docstring 最後補一段：「定稿卡推薦組法（2026-09-30）：另讀 `Recommendations_Next`，分理由（含你講的／接近你講的／最接近你描述的／換個搭法）列採用率與取代率、換一批的比例、依原名次分桶的出現與採用。舊資料沒有每套的理由，照列層級的 anchored／similar 推回，不進名次分桶。」

表格欄位依序是：理由、出現、採用率（採用套數／出現套數）、取代率（有換掉 facet 的採用／該理由的採用次數）、平均換掉 facet；Step 1 的斷言照這個順序。

- [ ] **Step 4: 跑測試確認通過**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -v`
Expected: 全部 PASS（既有測試也要過）。

- [ ] **Step 5: 對開發庫跑一次，確認舊資料不會炸**

Run: `cd scripts && ./.venv/Scripts/python.exe adoption_report.py`
Expected: 印出原本的報表，最後多「定稿卡推薦組法」一節，理由全是由列層級推回的（還沒有新資料）。

- [ ] **Step 6: Commit**

```bash
git add scripts/adoption_report.py scripts/tests/test_adoption_report.py
git commit -m "feat(scripts): adoption report adds slate reasons, replace rate by reason, next-batch and rank buckets"
```

---

### Task 10: 效能量測寫進 spec §8

**Files:**
- Modify: `docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`（§8）

這一步由主持的 Claude 在開發庫跑，不需要程式變更。

- [ ] **Step 1: 準備參數**

在 `prompt-copilot-db` 裡挑一筆有 `clothing.footwear = sandals` 的片段，拿它的 `preset_embedding` 當查詢向量、它的 footwear facet 向量當近似錨向量：

```bash
docker exec -i prompt-copilot-db psql -U postgres -d prompt_copilot -At -c "
SELECT p.id FROM prompt_knowledge_presets p JOIN preset_facet_embeddings e ON e.preset_id = p.id
WHERE e.facet_id = 'clothing.footwear' AND e.tag_key = 'sandals' LIMIT 1;"
```

- [ ] **Step 2: EXPLAIN ANALYZE 四條查詢，各跑 5 次取中位數**

把 `PresetRepository` 裡的 SQL 原文貼進 psql，`@q`／`@a` 換成 `(SELECT preset_embedding FROM prompt_knowledge_presets WHERE id = <id>)`／`(SELECT embedding FROM preset_facet_embeddings WHERE preset_id = <id> AND facet_id = 'clothing.footwear')`，`@facets` 用 portrait 的 clothing 六個 facet，錨 `sandals`：

- `AnchoredRecommendSql`：`@take` 3 與 30 各一組
- `RecommendSql`：3 與 30
- `RecommendSimilarSql`：3 與 30，`@maxDist` 0.30
- `FacetVectorsSql`：`@ids` 取 `RecommendSql` LIMIT 30 的 id 再加 2 個，`@facets` = `{clothing.footwear}` 與全部 6 個 clothing facet 各一組

每條用 `EXPLAIN (ANALYZE, BUFFERS)`，記下：頂層節點、是否走 HNSW／主鍵、`Execution Time`。另外比對 `RecommendSql` LIMIT 30 的前 3 筆 id 是否等於 LIMIT 3 的結果（spec §9.2 移過來的檢查）。

- [ ] **Step 3: 換一批的延遲**

Task 11 的瀏覽器驗收跑完後，查 `Recommendations_Next` 的 `latency_ms`：

```bash
docker exec -i prompt-copilot-db psql -U postgres -d prompt_copilot -At -c "
SELECT count(*), percentile_cont(0.5) WITHIN GROUP (ORDER BY latency_ms), max(latency_ms)
FROM audit_logs WHERE event_type = 'Recommendations_Next';"
```

定稿卡整張的推薦延遲沒有獨立紀錄：用「6 個維度 × 單排延遲中位數」估，並註明是估計。

- [ ] **Step 4: 寫進 spec §8**

把 §8 開頭「實作後在開發庫量，結果寫回本節」改成實測結果：每條查詢一行（LIMIT 3 → 30 的中位數毫秒、關鍵節點）、`FacetVectorsSql` 走主鍵與否、HNSW 前 3 名比對結果、換一批延遲中位數與最大值、整張定稿卡的估計。保留 ef_search 那條提醒。

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-30-recommendation-slate-design.md
git commit -m "docs: record slate query plans and timings in the design"
```

（Step 3 依賴 Task 11；可以先 commit Step 2 的結果，Task 11 之後再補一個 commit。）

---

### Task 11: 瀏覽器驗收 G1–G5 與文件收尾

**Files:**
- Modify: `docs/eval-cases.md`
- Modify: `docs/superpowers/specs/2026-09-25-set-recommendations-design.md`（開頭加一行）

這一步由主持的 Claude 用 Playwright（headless Edge，照 2026-09-29 驗收的做法）跑；腳本放 scratchpad，不進 repo。

- [ ] **Step 1: 重建容器**

```bash
docker compose up -d --build api frontend
```

確認 `curl -s localhost:<api port>/health` 回 ok（port 看 `docker-compose.yml`）。

- [ ] **Step 2: 跑 G1–G5 並記錄**

每一條記下：輸入句、看到的結果、通過與否；需要時查 `audit_logs` 佐證。

- **G1**：輸入「一個穿涼鞋和白色洋裝的少女坐在海邊」，一路回答到定稿。定稿卡每排 3 張（相關 2＋探索 1，除非該維度候選不足），每張圖下有理由文字；探索位是虛線外框、標「換個搭法」；排頭沒有「含你講的…」列層級文案。
- **G2**：在穿著那排按「換一批」。右邊多一條分隔線、「第 2 批」、3 張新卡；第 2 批跟第 1 批沒有整批相同的 presetId；`audit_logs` 有一筆 `Recommendations_Next`，`batch` = 2。
- **G3**：從第 2 批採用一套（勾一個 facet）。`Turn_Completed.adoption.batch` = 2。
- **G4**：定稿後說「鞋子換成靴子」讓它重新定稿。新定稿卡的穿著排跟上一張定稿卡的第 1 批不是整排相同；舊定稿卡不再顯示「換一批」。
- **G5**：開新對話，第一句只講「一個女生」，讓它追問。追問卡的推薦跟改前一樣：排頭有列層級文案、沒有每套理由、沒有「換一批」。

- [ ] **Step 3: 寫進 `docs/eval-cases.md`**

最後加一節：

```markdown
## 2026-09-30 推薦組法（看過延後、探索位、換一批）

設計：`docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`。Claude 用 Playwright（headless Edge）跑。

| # | 情境 | 期望 | 結果 |
| :--- | :--- | :--- | :--- |
| G1 | 定稿卡 | 每排 2 相關＋1 探索、每套有理由、探索位虛線 | （填） |
| G2 | 換一批 | 接成第 2 批、不整批重複、audit 有 Recommendations_Next | （填） |
| G3 | 從第 2 批採用 | adoption.batch = 2 | （填） |
| G4 | 定稿後修改 | 新定稿卡不整排重複上一張；舊卡沒有換一批 | （填） |
| G5 | 追問卡 | 跟改前一樣 | （填） |
```

「（填）」換成 Step 2 的實際結果與觀察（例如探索位實際挑到什麼、哪個維度候選不足），不要留空。

- [ ] **Step 4: set-recommendations spec 加指向**

`docs/superpowers/specs/2026-09-25-set-recommendations-design.md` 開頭的日期／來源行之後加：

```markdown
後續：定稿卡的組法（2 相關＋1 探索、看過加權延後、換一批）見 `2026-09-30-recommendation-slate-design.md`；追問卡維持本文件的做法。
```

- [ ] **Step 5: Commit**

```bash
git add docs/eval-cases.md docs/superpowers/specs/2026-09-25-set-recommendations-design.md
git commit -m "docs: slate acceptance G1-G5 and pointer from the set recommendations design"
```

- [ ] **Step 6: 補 Task 10 Step 3 的延遲數字並 commit**（見 Task 10）

---

## 完成後

- 全部測試：`dotnet test src/PromptCopilot.Api.Tests`、`PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "Category=Integration"`、前端 `npm test`、`scripts` 的 pytest。
- 用 superpowers:finishing-a-development-branch 決定 merge 方式；merge 前更新記憶 `recommendation-slate-diversity-next`（完成狀態、commit hash）。
