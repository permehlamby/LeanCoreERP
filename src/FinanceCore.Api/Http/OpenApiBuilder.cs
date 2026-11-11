using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceCore.Core.Commands;

namespace FinanceCore.Api.Http;

/// <summary>
/// The OpenAPI 3.1 document of the API (20, 7), generated from the command catalog: the request and result types are the records of the core library, so
/// the document cannot drift from the commands. A field that is not nullable is required, a nullable one is optional. Decimals are strings of digits,
/// ids are UUIDs, dates are plain dates, hashes are hex. The error codes are an enumeration.
/// </summary>
public static class OpenApiBuilder
{
    private static readonly string[] ProblemStatuses = ["400", "401", "403", "404", "409", "422", "500", "503"];

    public static JsonObject Build(CommandCatalog catalog, string version = "v1")
    {
        var schemas = new JsonObject();
        var names = new Dictionary<Type, string>();
        var paths = new JsonObject();

        foreach (CommandRegistration c in catalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            JsonObject request = Ref(c.RequestType, schemas, names);
            JsonObject result = Ref(c.ResultType, schemas, names);
            var responses = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "The command succeeded (06, 2.2). A replay of an earlier identical command has idempotent_replay true.",
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Envelope(result) } }
                }
            };
            foreach (string status in ProblemStatuses)
                responses[status] = new JsonObject
                {
                    ["description"] = Describe(status),
                    ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Problem" } } }
                };

            paths["/v1/" + c.Name] = new JsonObject
            {
                ["post"] = new JsonObject
                {
                    ["operationId"] = OperationId(c.Name),
                    ["summary"] = c.Name,
                    ["tags"] = new JsonArray(c.Name.Split(':')[0]),
                    ["x-permission"] = c.Permission,
                    ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
                    ["parameters"] = new JsonArray(
                        new JsonObject { ["$ref"] = "#/components/parameters/IdempotencyKey" },
                        new JsonObject { ["$ref"] = "#/components/parameters/CorrelationId" },
                        new JsonObject { ["$ref"] = "#/components/parameters/SourceSystem" },
                        new JsonObject { ["$ref"] = "#/components/parameters/TenantId" }),
                    ["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = request } }
                    },
                    ["responses"] = responses
                }
            };
        }

        schemas["Error"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("code", "path", "phase", "retryable", "severity"),
            ["properties"] = new JsonObject
            {
                ["code"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(ErrorCatalog.All.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) },
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The field of the request that the error is about, or empty" },
                ["params"] = new JsonObject { ["type"] = new JsonArray("object", "null"), ["additionalProperties"] = true },
                ["phase"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("P0", "P1", "P2", "P3", "P4", "P5", "P6", "P7") },
                ["retryable"] = new JsonObject { ["type"] = "boolean" },
                ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("ERROR", "WARNING") }
            }
        };
        schemas["Problem"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("type", "title", "status", "errors"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string" },
                ["title"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("REJECTED", "FAILED", "UNAUTHENTICATED", "FORBIDDEN", "NOT_FOUND") },
                ["status"] = new JsonObject { ["type"] = "integer" },
                ["correlation_id"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["errors"] = new JsonObject { ["type"] = "array", ["maxItems"] = 100, ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/Error" } }
            }
        };

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Finance Core API",
                ["version"] = version,
                ["description"] = "Commands as POST /v1/{resource}:{verb}. The tenant comes from the token. Every command needs an Idempotency-Key. Generated from the command catalog."
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = Sorted(schemas),
                ["parameters"] = new JsonObject
                {
                    ["IdempotencyKey"] = Header("Idempotency-Key", true, "At most 100 characters. Scope: the tenant (06, 4)", new JsonObject { ["type"] = "string", ["maxLength"] = 100 }),
                    ["CorrelationId"] = Header("X-Correlation-Id", false, "The caller's trace id, at most 100 characters. Generated if absent", new JsonObject { ["type"] = "string", ["maxLength"] = 100 }),
                    ["SourceSystem"] = Header("X-Source-System", false, "The calling system, at most 50 characters. A tracing field only", new JsonObject { ["type"] = "string", ["maxLength"] = 50 }),
                    ["TenantId"] = Header("X-Tenant-Id", false, "If present it must equal the tenant of the token (AUTH.TENANT_MISMATCH)", new JsonObject { ["type"] = "string", ["format"] = "uuid" })
                },
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "JWT", ["description"] = "OpenID Connect access token. Claims: tenant_id, sub, permissions or scope" }
                }
            }
        };
    }

    public static string ToText(JsonObject document) => document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    // ---------------------------------------------------------------------------------------------------------
    private static JsonObject Envelope(JsonObject result) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("status", "idempotent_replay", "command_log_id", "result", "warnings"),
        ["properties"] = new JsonObject
        {
            ["status"] = new JsonObject { ["const"] = "OK" },
            ["idempotent_replay"] = new JsonObject { ["type"] = "boolean" },
            ["command_log_id"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "uuid" },
            ["result"] = result.DeepClone(),
            ["warnings"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/Error" } }
        }
    };

    private static JsonObject Header(string name, bool required, string description, JsonObject schema) => new()
    {
        ["name"] = name, ["in"] = "header", ["required"] = required, ["description"] = description, ["schema"] = schema
    };

    private static string Describe(string status) => status switch
    {
        "400" => "The envelope or the payload does not fit the contract (P0, P1)",
        "401" => "No valid token (AUTH.TOKEN_INVALID)",
        "403" => "A permission or the tenant is missing (AUTH.*)",
        "404" => "The command or something it names does not exist",
        "409" => "An idempotency key was reused for another request, or a retryable conflict (SYSTEM.CONFLICT)",
        "422" => "The command refused the request: business rules (P2 to P6)",
        "500" => "The command failed (SYSTEM.*)",
        _ => "The service is unavailable (SYSTEM.UNAVAILABLE)"
    };

    private static string OperationId(string name)
    {
        string[] parts = name.Split(':', '-');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static JsonObject Sorted(JsonObject o)
    {
        var sorted = new JsonObject();
        foreach (var key in o.Select(p => p.Key).Order(StringComparer.Ordinal)) sorted[key] = o[key]!.DeepClone();
        return sorted;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Types
    // ---------------------------------------------------------------------------------------------------------
    private static JsonObject Ref(Type type, JsonObject schemas, Dictionary<Type, string> names) => Schema(type, false, schemas, names);

    private static JsonObject Schema(Type type, bool nullable, JsonObject schemas, Dictionary<Type, string> names)
    {
        Type? inner = Nullable.GetUnderlyingType(type);
        if (inner is not null) { type = inner; nullable = true; }
        JsonObject schema = Simple(type, schemas, names) ?? throw new NotSupportedException($"No schema for {type}");
        if (!nullable) return schema;
        // OpenAPI 3.1: a nullable value is a union with null. A $ref is wrapped, a plain type gets "null" added
        if (schema.ContainsKey("$ref")) return new JsonObject { ["oneOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
        if (schema["type"] is not { } t) return schema;   // an untyped value (an object of any kind) already admits null
        var union = new JsonArray();
        if (t is JsonArray types) foreach (JsonNode? x in types) union.Add(x!.GetValue<string>());
        else union.Add(t.GetValue<string>());
        union.Add("null");
        schema["type"] = union;
        return schema;
    }

    private static JsonObject? Simple(Type t, JsonObject schemas, Dictionary<Type, string> names)
    {
        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(int) || t == typeof(short)) return new JsonObject { ["type"] = "integer", ["format"] = "int32" };
        if (t == typeof(long)) return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        if (t == typeof(Guid)) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (t == typeof(DateOnly)) return new JsonObject { ["type"] = "string", ["format"] = "date" };
        if (t == typeof(DateTime)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(decimal))
            return new JsonObject { ["type"] = "string", ["pattern"] = "^-?[0-9]+(\\.[0-9]+)?$", ["description"] = "An exact decimal as a string of digits. A JSON number is accepted on input" };
        if (t == typeof(byte[])) return new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9a-f]*$", ["description"] = "Lower-case hex" };
        if (t == typeof(object)) return new JsonObject();
        if (t.IsGenericType && t.GetGenericTypeDefinition() is var g && (g == typeof(IReadOnlyDictionary<,>) || g == typeof(Dictionary<,>)))
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = true, ["description"] = "A set of named fields. The fields the command knows are typed by their kind (07, 11.1)" };
        if (t.IsArray || (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t)))
        {
            Type element = t.IsArray ? t.GetElementType()! : t.GetGenericArguments()[0];
            return new JsonObject { ["type"] = "array", ["items"] = Schema(element, false, schemas, names) };
        }
        if (t.IsClass && t.GetConstructors().Length > 0) return Record(t, schemas, names);
        return null;
    }

    private static JsonObject Record(Type t, JsonObject schemas, Dictionary<Type, string> names)
    {
        if (names.TryGetValue(t, out string? existing)) return new JsonObject { ["$ref"] = "#/components/schemas/" + existing };
        string name = t.Name;
        if (names.Values.Contains(name)) throw new InvalidOperationException($"Two types are called {name}");
        names[t] = name;

        ConstructorInfo ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var context = new NullabilityInfoContext();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (ParameterInfo p in ctor.GetParameters())
        {
            bool nullable = context.Create(p).WriteState == NullabilityState.Nullable || Nullable.GetUnderlyingType(p.ParameterType) is not null;
            string json = JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name!);
            properties[json] = Schema(p.ParameterType, nullable, schemas, names);
            if (!nullable) required.Add(json);
        }
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        schemas[name] = schema;
        return new JsonObject { ["$ref"] = "#/components/schemas/" + name };
    }
}
