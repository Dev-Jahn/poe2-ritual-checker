using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class ChoberTooltipTests
{
    private static Catalog Catalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        return JsonFiles.Read<Catalog>(Path.Combine(root!.FullName, "data", "catalog.json"));
    }

    private static string[] Lines(string name = "초버 자비", int level = 2) =>
        [
            name,
            "납빛 대망치",
            "양손 철퇴",
            "물리 피해: 117-188",
            "지능 요구사항 +100",
            "물리 피해 59~110 추가",
            "마나 최대치 +91",
            "정신력 +50",
            $"모든 소환수 스킬 레벨 +{level}",
            "소환수 피해의 증가 및 감소가 자신에게도 적용",
            "비용:",
            "공물 점수 xl,215",
        ];

    private static ItemObservation Selected() =>
        new(
            "2:4",
            new(362, 513, 140, 281),
            2,
            4,
            2,
            4,
            "Chober_Chaber",
            "초버 차버",
            "unique",
            1,
            .954,
            false,
            false,
            false,
            true,
            []
        );

    [Fact]
    public void SelectedIconAndStatsRecoverMisreadNameAndBindTooltip()
    {
        var catalog = Catalog();
        var item = Selected();
        var tip = TooltipParser.Parse(Lines(), catalog, selectedItem: item);
        Assert.NotNull(tip);
        Assert.Equal("Chober_Chaber", tip.CatalogId);
        Assert.True(tip.Complete);
        Assert.Equal(6, tip.Mods.Count);
        Assert.Equal(new double[] { 59, 110 }, tip.Mods["adds#to#physicaldamage"]);
        Assert.Equal(new double[] { 2 }, tip.Mods["#tolevelofallminionskills"]);
        Assert.Equal(1215, tip.PurchaseTribute);
        var analysis = new Analysis(
            1,
            "test",
            new(new(222, 233, 842, 702), 70.2, 1),
            [item],
            new(1039, 229, 951, 660),
            [],
            0
        );
        Assert.Equal(item.InstanceId, TooltipBinding.Resolve(analysis, tip, catalog)?.InstanceId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unselected")]
    [InlineData("low-score")]
    [InlineData("estimated")]
    [InlineData("wrong-size")]
    [InlineData("wrong-icon")]
    public void MisreadNameNeedsReliableSelection(string condition)
    {
        var item = condition switch
        {
            "missing" => null,
            "unselected" => Selected() with { Selected = false },
            "low-score" => Selected() with { Confidence = .89 },
            "estimated" => Selected() with { Estimated = true },
            "wrong-size" => Selected() with { Columns = 1 },
            _ => Selected() with { CatalogId = "Bristleboar" },
        };
        Assert.Null(TooltipParser.Parse(Lines(), Catalog(), selectedItem: item));
    }

    [Fact]
    public void IconCannotOverrideAmbiguousNameOrInsufficientStats()
    {
        var catalog = Catalog();
        var original = catalog.Items.Single(i => i.Id == "Chober_Chaber");
        var ambiguous = catalog with
        {
            Items = [.. catalog.Items, original with { Id = "other", NameKo = "초버 자부" }],
        };
        Assert.Null(TooltipParser.Parse(Lines(), ambiguous, selectedItem: Selected()));
        Assert.Null(
            TooltipParser.Parse(
                ["초버 자비", "지능 요구사항 +100", "정신력 +50"],
                catalog,
                selectedItem: Selected()
            )
        );
        Assert.Null(
            TooltipParser.Parse(Lines("알 수 없는 망치"), catalog, selectedItem: Selected())
        );
        Assert.Equal(
            "Bristleboar",
            TooltipParser.Parse(Lines("가시털멧돼지"), catalog, selectedItem: Selected())?.CatalogId
        );
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void CurrentSkillRollIsValidatedWithoutAcceptingUnknownValues(int level, bool complete)
    {
        var tip = TooltipParser.Parse(Lines("초버 차버", level), Catalog());
        Assert.NotNull(tip);
        Assert.Equal(complete, tip.Complete);
    }

    private sealed class TradeHandler : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Calls++;
            JsonObject body;
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/trade2/search/"))
            {
                var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Assert.Equal("Chober Chaber", query["query"]!["name"]!.GetValue<string>());
                Assert.Equal("online", query["query"]!["status"]!["option"]!.GetValue<string>());
                body = new()
                {
                    ["id"] = "search",
                    ["result"] = new JsonArray(
                        Enumerable.Range(0, 10).Select(i => JsonValue.Create("id" + i)).ToArray()
                    ),
                };
            }
            else
            {
                Assert.StartsWith("/api/trade2/fetch/", request.RequestUri.AbsolutePath);
                body = new()
                {
                    ["result"] = new JsonArray(
                        Enumerable
                            .Range(0, 10)
                            .Select(i =>
                                (JsonNode)
                                    new JsonObject
                                    {
                                        ["listing"] = new JsonObject
                                        {
                                            ["account"] = new JsonObject
                                            {
                                                ["name"] = "seller" + i,
                                                ["online"] = new JsonObject(),
                                            },
                                            ["price"] = new JsonObject
                                            {
                                                ["amount"] = i < 5 ? 100 + i : 1,
                                                ["currency"] = "exalted",
                                            },
                                        },
                                        ["item"] = new JsonObject
                                        {
                                            ["name"] = "Chober Chaber",
                                            ["typeLine"] = "Leaden Greathammer",
                                            ["corrupted"] = false,
                                            ["explicitMods"] = new JsonArray(
                                                "+100 Intelligence Requirement",
                                                "Adds 59 to 110 Physical Damage",
                                                "+91 to maximum Mana",
                                                "+50 to Spirit",
                                                $"+{(i < 5 ? 2 : 3)} to Level of all Minion Skills",
                                                "Increases and Reductions to Minion Damage also affect you"
                                            ),
                                        },
                                    }
                            )
                            .ToArray()
                    ),
                };
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task RecoveredTooltipQueriesTradeAndKeepsSkillRollsSeparate()
    {
        var catalog = Catalog();
        var tip = TooltipParser.Parse(Lines(), catalog, selectedItem: Selected());
        Assert.NotNull(tip);
        var handler = new TradeHandler();
        using var http = new MarketHttp("https://example.invalid", handler);
        using var trade = new TradeMarket(http);
        var result = await trade.QuoteAsync(
            catalog.Items.Single(i => i.Id == tip.CatalogId),
            "league",
            null,
            tip,
            default
        );
        Assert.Equal(2, handler.Calls);
        Assert.NotNull(result.Quote);
        Assert.Equal("유사 옵션 호가", result.Quote.Basis);
        Assert.Equal(5, result.Quote.Samples);
        Assert.Equal(102, result.Quote.UnitPrice);
    }
}
