using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FinanceCore.Api.Commands;
using FinanceCore.Api.Http;
using FinanceCore.Core.Commands;
using FinanceCore.Data;

namespace FinanceCore.Api.Tests;

/// <summary>
/// The API against its contract (A4): the OpenAPI document is generated from the commands and committed, the command names and permissions are the
/// ones of the specs, and the error codes of the source are the enumeration of the document, in both directions.
/// </summary>
public class ContractTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "06-command-contracts.md"))) return d.FullName;
        throw new DirectoryNotFoundException("The repository root with the specs was not found");
    }

    private static CommandCatalog Production()
    {
        var catalog = new CommandCatalog();
        new PostingModule().Register(catalog);
        new ConfigModule().Register(catalog);
        return catalog;
    }

    [Fact]
    public void EveryCommandOfTheApiHasItsPermissionForTheEndpointAndTheOpenApiDocument()
    {
        CommandCatalog catalog = Production();
        Assert.All(catalog.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Permission), $"{c.Name} has no permission for P0a"));
        JsonNode doc = OpenApiBuilder.Build(catalog);
        foreach (CommandRegistration c in catalog.All)
            Assert.Equal(c.Permission, doc["paths"]!["/v1/" + c.Name]!["post"]!["x-permission"]!.GetValue<string>());
    }

    private static string SpecText() =>
        string.Join('\n', Directory.GetFiles(Root, "*.md").Concat(Directory.GetFiles(Root, "*.md.txt")).Select(File.ReadAllText));

    private static IEnumerable<string> SourceFiles() =>
        new[] { "FinanceCore.Core", "FinanceCore.Data", "FinanceCore.Api" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(Root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "ErrorCatalog.cs");

    // ---------------------------------------------------------------------------------------------------------
    // The document
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void TheCommittedOpenApiDocumentIsTheOneTheCommandsGenerate()
    {
        string generated = OpenApiBuilder.ToText(OpenApiBuilder.Build(Production()));
        string path = Path.Combine(Root, "api", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
        }
        Assert.True(File.Exists(path), "api/openapi.json is missing: run the tests with UPDATE_OPENAPI=1");
        Assert.True(File.ReadAllText(path).ReplaceLineEndings("\n") == generated,
            "api/openapi.json is out of date: run the tests with UPDATE_OPENAPI=1 and commit the file");
    }

    [Fact]
    public void EveryOperationHasTheEnvelopeTheSecurityAndTheProblemResponses()
    {
        JsonObject doc = OpenApiBuilder.Build(Production());
        Assert.Equal("3.1.0", doc["openapi"]!.GetValue<string>());
        var paths = doc["paths"]!.AsObject();
        Assert.Equal(Production().All.Count, paths.Count);
        foreach (var (path, item) in paths)
        {
            Assert.Matches("^/v1/[a-z0-9-]+:[a-z0-9-]+$", path);
            var post = item!["post"]!.AsObject();
            var parameters = post["parameters"]!.AsArray().Select(p => p!["$ref"]!.GetValue<string>()).ToArray();
            Assert.Contains("#/components/parameters/IdempotencyKey", parameters);
            Assert.True(doc["components"]!["parameters"]!["IdempotencyKey"]!["required"]!.GetValue<bool>());
            Assert.NotNull(post["security"]);
            Assert.True(post["requestBody"]!["required"]!.GetValue<bool>());
            var responses = post["responses"]!.AsObject().Select(r => r.Key).Order().ToArray();
            Assert.Equal(new[] { "200", "400", "401", "403", "404", "409", "422", "500", "503" }, responses);
            Assert.Equal("OK", post["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["status"]!["const"]!.GetValue<string>());
        }
        Assert.Equal(paths.Count, paths.Select(p => p.Value!["post"]!["operationId"]!.GetValue<string>()).Distinct().Count());
    }

    [Fact]
    public void EveryReferenceResolvesAndEveryRequiredFieldIsAProperty()
    {
        JsonObject doc = OpenApiBuilder.Build(Production());
        var schemas = doc["components"]!["schemas"]!.AsObject();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["$ref"] is { } r)
                        Assert.True(schemas.ContainsKey(r.GetValue<string>().Replace("#/components/schemas/", "")) || doc["components"]!["parameters"]!.AsObject().ContainsKey(r.GetValue<string>().Split('/')[^1]), r.GetValue<string>());
                    if (o["required"] is JsonArray req && o["properties"] is JsonObject props)
                        foreach (var name in req) Assert.True(props.ContainsKey(name!.GetValue<string>()), name!.GetValue<string>());
                    foreach (var child in o) Walk(child.Value);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
        Walk(doc);
        Assert.Contains("PostJournalDocumentRequest", schemas.Select(s => s.Key));
    }

    [Fact]
    public void ARequestSchemaRequiresWhatIsNotNullableAndNothingElse()
    {
        var schemas = OpenApiBuilder.Build(Production())["components"]!["schemas"]!.AsObject();
        var post = schemas["PostJournalDocumentRequest"]!;
        var required = post["required"]!.AsArray().Select(x => x!.GetValue<string>()).Order().ToArray();
        Assert.Equal(new[] { "company_id", "document_date", "document_type", "lines", "posting_date", "transaction_currency" }, required);
        var properties = post["properties"]!.AsObject();
        Assert.Equal("uuid", properties["company_id"]!["format"]!.GetValue<string>());
        Assert.Equal("date", properties["posting_date"]!["format"]!.GetValue<string>());
        Assert.Contains("partner_id", properties.Select(p => p.Key));
        Assert.Equal(new[] { "string", "null" }, properties["header_text"]!["type"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.False(post["additionalProperties"]!.GetValue<bool>());

        var line = schemas["RequestLine"]!;
        Assert.Equal("string", line["properties"]!["amount_tc"]!["type"]!.GetValue<string>());   // an exact decimal is a string
        Assert.Equal(new[] { "account_id", "amount_tc", "line_ref" }, line["required"]!.AsArray().Select(x => x!.GetValue<string>()).Order().ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------
    // The specs
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void EveryCommandNameIsInTheSpecsOrItsResourceIs()
    {
        string specs = SpecText();
        var unnamed = Production().All.Select(c => c.Name).Where(n => !specs.Contains($"`{n}`", StringComparison.Ordinal)).ToList();
        // the generic verbs of 07, 11.1 and 06, 7.4 are written as {resource}:create, :update, :deactivate, :reactivate and :create-version
        var generic = new[] { ":create", ":update", ":deactivate", ":reactivate", ":create-version" };
        foreach (string name in unnamed.ToList())
        {
            string verb = name[name.IndexOf(':')..];
            string resource = name[..name.IndexOf(':')];
            if (generic.Contains(verb) && specs.Contains(resource, StringComparison.Ordinal)) unnamed.Remove(name);
        }
        Assert.Empty(unnamed);
    }

    [Fact]
    public void EveryPermissionOfACommandIsInThePermissionTable()
    {
        string specs = File.ReadAllText(Path.Combine(Root, "06-command-contracts.md"));
        string[] permissions =
        [
            JournalDocumentPoster.PostPermission, DocumentReverser.Permission, DocumentChanger.Permission, ClearingPoster.Permission, ClearingPoster.ResetPermission,
            FiscalYearOpener.Permission, PeriodStatusCommands.SoftClosePermission, PeriodStatusCommands.HardClosePermission, PeriodStatusCommands.ReopenPermission,
            CompanyActivator.ActivatePermission, CompanyActivator.ReadPermission, ChainVerification.Permission,
            FinanceCore.Core.Retention.RetentionRules.Permission, FinanceCore.Core.Masters.MasterRules.PartnerPermission, FinanceCore.Core.Masters.MasterRules.ConfigPermission
        ];
        foreach (string p in permissions) Assert.True(specs.Contains($"`{p}`", StringComparison.Ordinal), $"{p} is not in 06");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The error codes
    // ---------------------------------------------------------------------------------------------------------
    private static readonly Regex CodeLiteral = new("\"([A-Z][A-Z0-9_]*\\.[A-Z][A-Z0-9_]*)\"", RegexOptions.Compiled);

    [Fact]
    public void TheErrorCodesOfTheSourceAreTheEnumerationOfTheDocumentInBothDirections()
    {
        var used = SourceFiles().SelectMany(f => CodeLiteral.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        var catalogued = ErrorCatalog.All.Concat(ErrorCatalog.Platform).ToHashSet();
        Assert.Empty(used.Except(catalogued).Order());       // a code in a command that the document does not list
        Assert.Empty(catalogued.Except(used).Order());       // a code in the document that no command returns
        Assert.Equal(ErrorCatalog.All.Count, ErrorCatalog.All.Distinct().Count());
        Assert.Equal(ErrorCatalog.All.Order(StringComparer.Ordinal).ToArray(), ErrorCatalog.All.ToArray());
        Assert.Empty(ErrorCatalog.All.Intersect(ErrorCatalog.Platform));
    }

    /// <summary>The codes that the slice has and the specs do not name: placeholders (LIMIT), the names of the verification's breaks, and the
    /// codes of the API layer. Each is recorded in db/README.md, F-IMPL-7, F-IMPL-29 and F-IMPL-31.</summary>
    private static readonly string[] SliceOnly =
    [
        "API.COMMAND_NOT_FOUND", "AUTH.TENANT_MISMATCH", "AUTH.TENANT_MISSING", "AUTH.TOKEN_INVALID",
        "CHAIN.DOCUMENT_WITHOUT_LINES", "NUMBER.GAP", "SYSTEM.FISCAL_YEAR_MISSING",
        "EVENT.ACK_NOT_FORWARD", "EVENT.REWIND_NOT_BACKWARD"   // 15, 5.3 says "only forward" and "backward" without naming a code (F-IMPL-42)
    ];

    /// <summary>The index of 06 writes a code in full (`AREA.NAME`) or, inside the row of its area, as the bare name (`NAME`).</summary>
    private static bool InSpecs(string code, string[] lines)
    {
        string area = code[..code.IndexOf('.')], name = code[(code.IndexOf('.') + 1)..];
        return lines.Any(l => l.Contains(code, StringComparison.Ordinal) ||
                              l.Contains($"`{name}`", StringComparison.Ordinal) && Regex.IsMatch(l, $"(^|[^A-Z_]){area}([^A-Z_]|$)"));
    }

    [Fact]
    public void EveryErrorCodeIsInTheSpecsOrIsASliceCodeThatIsRecorded()
    {
        string[] lines = SpecText().Split('\n');
        var missing = ErrorCatalog.All
            .Where(c => !c.StartsWith("LIMIT.", StringComparison.Ordinal) && !SliceOnly.Contains(c) && !InSpecs(c, lines))
            .ToList();
        Assert.True(missing.Count == 0, string.Join(" ", missing));
    }
}
