using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrackerApi.Tests;

// xUnit создает новый экземпляр класса для каждого тестового случая.
// У каждого экземпляра собственные сервер и TrackerStore: данные изолированы.
public class AccessTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    private HttpClient As(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", user);
        return client;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_Ok()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requester_List_Contains_Exactly_Own_Issues()
    {
        using var client = As("alice");
        var issues = await client.GetFromJsonAsync<List<IssueDto>>("/issues");
        Assert.NotNull(issues);
        Assert.Equal(new[] { 1, 3 }, issues.Select(i => i.Id).OrderBy(id => id).ToArray());
        Assert.All(issues, i => Assert.Equal("alice", i.Author));
    }

    [Theory]
    [InlineData("carol", "sales", 3)]
    [InlineData("mgr", "sales", 3)]
    [InlineData("dave", "logistics", 1)]
    public async Task Staff_List_Is_Limited_To_Department(string user, string department, int count)
    {
        using var client = As(user);
        var issues = await client.GetFromJsonAsync<List<IssueDto>>("/issues");
        Assert.NotNull(issues);
        Assert.Equal(count, issues.Count);
        Assert.All(issues, i => Assert.Equal(department, i.Department));
    }

    [Theory]
    [InlineData("alice", 3)]
    [InlineData("carol", 2)]
    [InlineData("mgr", 1)]
    [InlineData("dave", 4)]
    public async Task Authorized_Users_Can_Read_Issue(string user, int id)
    {
        using var client = As(user);
        var issue = await client.GetFromJsonAsync<IssueDto>($"/issues/{id}");
        Assert.NotNull(issue);
        Assert.Equal(id, issue.Id);
        Assert.False(string.IsNullOrWhiteSpace(issue.Body));
    }

    [Theory]
    [InlineData("carol", 1)]
    [InlineData("mgr", 2)]
    [InlineData("dave", 4)]
    public async Task Resolver_And_Manager_Can_Close_Department_Issue(string user, int id)
    {
        using var client = As(user);
        var before = await client.GetFromJsonAsync<IssueDto>($"/issues/{id}");
        Assert.NotNull(before);
        Assert.Equal("open", before.Status);
        var response = await client.PostAsync($"/issues/{id}/close", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await client.GetFromJsonAsync<IssueDto>($"/issues/{id}");
        Assert.NotNull(after);
        Assert.Equal("closed", after.Status);
    }

    [Theory]
    [InlineData("alice", 10, "CONFIDENTIAL payslip content for alice")]
    [InlineData("bob", 11, "vpn connection log for bob")]
    public async Task Owner_Can_Read_Own_Attachment(string user, int id, string expected)
    {
        using var client = As(user);
        var response = await client.GetAsync($"/attachments/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-user")]
    public async Task Missing_Or_Unknown_Identity_Is_Rejected(string? user)
    {
        using var client = _factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-User", user);
        foreach (var path in new[] { "/issues", "/issues/1", "/attachments/10", "/issues/1/exists" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/issues/1/close", null)).StatusCode);
    }

    // Д1: готовый пример. Skip снят: тест должен падать на исходном коде.
    [Fact]
    public async Task Requester_Cannot_Read_Foreign_Issue()
    {
        using var bob = As("bob");
        var own = await bob.GetFromJsonAsync<IssueDto>("/issues/2");
        Assert.NotNull(own);
        Assert.Equal("bob", own.Author);

        using var alice = As("alice");
        var response = await alice.GetAsync("/issues/2");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("cannot connect", await response.Content.ReadAsStringAsync());
    }

    // Д2: два независимых ограничения на закрытие - роль и подразделение.
    // Отказ не должен менять состояние, поэтому статус перечитывается после
    // каждой попытки.
    [Fact]
    public async Task Requester_Cannot_Close_Own_Issue()
    {
        using var client = As("alice");
        var before = await client.GetFromJsonAsync<IssueDto>("/issues/1");
        Assert.NotNull(before);
        Assert.Equal("open", before.Status);

        // Ограничение по роли: заявитель не закрывает даже свое обращение.
        // Обращение для него видимо, поэтому отказ - 403, а не 404.
        var response = await client.PostAsync("/issues/1/close", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var afterRequester = await client.GetFromJsonAsync<IssueDto>("/issues/1");
        Assert.NotNull(afterRequester);
        Assert.Equal("open", afterRequester.Status);

        // Ограничение по подразделению: исполнитель logistics не закрывает
        // обращение sales. Для него оно невидимо, поэтому 404.
        using var dave = As("dave");
        var foreign = await dave.PostAsync("/issues/1/close", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        // Состояние проверяет правомочный пользователь: после обеих попыток
        // обращение все еще открыто.
        using var carol = As("carol");
        var afterBoth = await carol.GetFromJsonAsync<IssueDto>("/issues/1");
        Assert.NotNull(afterBoth);
        Assert.Equal("open", afterBoth.Status);
    }

    // Д3: полностью самостоятельная проверка обхода через вложение.
    // Правило: доступ к вложению определяется правом чтения родительского
    // обращения. Вложение 10 принадлежит обращению 3 (alice, sales).
    [Fact]
    public async Task Requester_Cannot_Read_Foreign_Attachment()
    {
        const string secret = "CONFIDENTIAL payslip content for alice";

        // Данные существуют и доступны правомочным пользователям: автору и
        // исполнителю того же подразделения (у них есть право читать обращение 3).
        foreach (var allowed in new[] { "alice", "carol" })
        {
            using var permitted = As(allowed);
            var ok = await permitted.GetAsync("/attachments/10");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(secret, await ok.Content.ReadAsStringAsync());
        }

        // Один и тот же неправомочный пользователь (bob) проверяется на обоих путях:
        // обращение недоступно, значит недоступно и его вложение.
        using var bob = As("bob");
        var issue = await bob.GetAsync("/issues/3");
        Assert.Equal(HttpStatusCode.NotFound, issue.StatusCode);

        var attachment = await bob.GetAsync("/attachments/10");
        Assert.Equal(HttpStatusCode.NotFound, attachment.StatusCode);
        var body = await attachment.Content.ReadAsStringAsync();
        Assert.DoesNotContain("CONFIDENTIAL", body);
        Assert.DoesNotContain("payslip", body);

        // Исполнитель другого подразделения тоже не читает чужое вложение.
        using var dave = As("dave");
        var foreign = await dave.GetAsync("/attachments/11");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("vpn connection log", await foreign.Content.ReadAsStringAsync());
    }

    private record IssueDto(int Id, string Author, string Department, string Title, string Body, string Status);
}
