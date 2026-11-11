using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PrAgent.Application.Sap;
using PrAgent.Infrastructure;
using PrAgent.Sap.Mock;
using PrAgent.Sap.S4Hana;

namespace PrAgent.IntegrationTests;

/// <summary>
/// Proves the plug: the SAP adapter is chosen by one configuration value, and nothing else in the
/// application changes. Today the S4Hana adapter is empty, so with it selected the app starts,
/// everything that does not need SAP works, and everything that does says clearly why it cannot.
/// </summary>
[Collection(ApiCollection.Name)]
public class SapSeamTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sap_mode_alone_selects_the_adapter()
    {
        await using var mock = await TestApp.StartAsync(postgres, "Mock");
        await using var real = await TestApp.StartAsync(postgres, "S4Hana");

        Assert.IsType<MockSapReadGateway>(mock.Services.GetRequiredService<ISapReadGateway>());
        Assert.IsType<S4HanaReadGateway>(real.Services.GetRequiredService<ISapReadGateway>());
        Assert.IsType<S4HanaPrWriter>(real.Services.GetRequiredService<ISapPrWriter>());
        Assert.Null(real.Services.GetService<MockSapLedger>());

        var mockMeta = await mock.As(DemoUsers.Rina).GetAsync("/api/meta").OkAsync();
        var realMeta = await real.As(DemoUsers.Rina).GetAsync("/api/meta").OkAsync();
        Assert.True(mockMeta["sapSimulated"]!.GetValue<bool>());
        Assert.False(realMeta["sapSimulated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task With_the_empty_s4hana_adapter_sap_calls_fail_loudly_and_nothing_is_faked()
    {
        await using var app = await TestApp.StartAsync(postgres, "S4Hana");
        var rina = app.As(DemoUsers.Rina);

        // What does not need SAP still works.
        Assert.NotEmpty((await rina.GetAsync("/api/sections").OkAsync()).AsArray());
        Assert.Equal(HttpStatusCode.OK, (await rina.GetAsync("/api/requests")).StatusCode);

        // What needs SAP answers 503 with a message that names the fix.
        foreach (var call in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => rina.GetAsync("/api/master-data/plants"),
                     () => rina.GetAsync("/api/budget?type=CostCenter&code=CC1101"),
                     () => rina.PostAsync("/api/rules/evaluate", Drafts.Request(Guid.NewGuid(), Drafts.Material(1))),
                 })
        {
            var response = await call();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.ReadAsync();
            Assert.Equal("SAP is not connected", problem["title"]!.GetValue<string>());
            Assert.Contains("docs/sap-hookup.md", problem["detail"]!.GetValue<string>());
        }
    }
}
