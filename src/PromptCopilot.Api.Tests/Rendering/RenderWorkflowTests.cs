using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderWorkflowTests
{
    private static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "Rendering", RenderWorkflow.FileName);

    /// <summary>從測試輸出目錄往上找 repo 根目錄（有 render/runpod/Dockerfile 的那層）。</summary>
    private static string RepoFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void Shipped_workflow_has_the_patched_nodes_and_its_checkpoint_is_baked_into_the_image()
    {
        var wf = RenderWorkflow.Load(ShippedPath);   // 節點對不上會丟
        var dockerfile = File.ReadAllText(RepoFile(Path.Combine("render", "runpod", "Dockerfile"))).Replace("\\\r\n", " ").Replace("\\\n", " ");
        var baked = Regex.Matches(dockerfile, @"--relative-path\s+models/checkpoints\b.*?--filename\s+(\S+)", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(wf.CheckpointName, baked);
    }

    [Fact]
    public void Build_fills_prompts_and_seed_without_touching_the_template()
    {
        var template = (JsonObject)JsonNode.Parse(File.ReadAllText(ShippedPath))!;
        var before = template.ToJsonString();
        var wf = new RenderWorkflow(template).Build("1girl, silver hair", "bad hands", 42);
        Assert.Equal("1girl, silver hair", wf["6"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.StartsWith("bad hands, nsfw, lowres", wf["7"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(42, wf["3"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal(before, template.ToJsonString());
    }

    [Fact]
    public void Template_whose_nodes_moved_is_rejected()
    {
        var template = (JsonObject)JsonNode.Parse(File.ReadAllText(ShippedPath))!;
        template["6"]!["class_type"] = "KSampler";
        var e = Assert.Throws<InvalidOperationException>(() => new RenderWorkflow(template));
        Assert.Contains("節點 6", e.Message);
    }

    [Fact]
    public void Base_negative_terms_are_appended_once_case_insensitively()
    {
        var neg = RenderWorkflow.WithBaseNegative("NSFW, lowres, extra arms");
        Assert.StartsWith("NSFW, lowres, extra arms, bad anatomy", neg);
        Assert.Single(neg.Split(", "), p => p.Equals("nsfw", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RenderWorkflow.BaseNegative, RenderWorkflow.WithBaseNegative(""));
    }
}
