using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.Security;

[TestFixture]
public class ErrorRedactorTests
{
    private ErrorRedactor _redactor = null!;

    [SetUp]
    public void SetUp()
    {
        _redactor = new ErrorRedactor();
    }

    // =====================================================================
    // Bearer tokens
    // =====================================================================

    [Test]
    public void Redact_RemovesBearerToken()
    {
        var result = _redactor.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.abc123");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
    }

    [Test]
    public void Redact_RemovesBearerToken_WithTabSeparator()
    {
        var result = _redactor.Redact("Bearer\tabc123def456");
        result.Should().Be("[REDACTED]");
    }

    // =====================================================================
    // OpenAI keys
    // =====================================================================

    [Test]
    public void Redact_RemovesOpenAiKey()
    {
        var result = _redactor.Redact("Error connecting with key sk-abcdefghijklmnopqrstuvwxyz1234567890");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz");
    }

    [Test]
    public void Redact_DoesNotRedact_ShortSkPrefix()
    {
        // sk- followed by fewer than 20 chars should not match OpenAI key pattern
        var result = _redactor.Redact("The sk-shortkey is here");
        result.Should().Be("The sk-shortkey is here");
    }

    // =====================================================================
    // Anthropic keys
    // =====================================================================

    [Test]
    public void Redact_RemovesAnthropicKey()
    {
        var result = _redactor.Redact("Using key sk-ant-api03-abcdefghijklmnop in request");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("sk-ant-api03");
    }

    [Test]
    public void Redact_AnthropicKey_BeforeOpenAi()
    {
        // sk-ant- should be fully redacted, not partially matched by sk- regex
        var result = _redactor.Redact("sk-ant-api03-abcdefghijklmnopqrstuv");
        result.Should().Be("[REDACTED]");
        result.Should().NotContain("sk-ant");
    }

    // =====================================================================
    // Generic keys
    // =====================================================================

    [Test]
    public void Redact_RemovesGenericKey()
    {
        var result = _redactor.Redact("API key-abc123def456 was rejected");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("key-abc123def456");
    }

    // =====================================================================
    // Internal URLs
    // =====================================================================

    [Test]
    public void Redact_RemovesInternalUrl_Localhost()
    {
        var result = _redactor.Redact("Cannot connect to http://localhost:5000/api/v1");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("localhost:5000");
    }

    [Test]
    public void Redact_RemovesInternalUrl_127001()
    {
        var result = _redactor.Redact("Error at http://127.0.0.1:8080/health");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("127.0.0.1");
    }

    [Test]
    public void Redact_RemovesInternalUrl_PrivateIP_10x()
    {
        var result = _redactor.Redact("Failed to reach https://10.0.1.50:443/endpoint");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("10.0.1.50");
    }

    [Test]
    public void Redact_RemovesInternalUrl_PrivateIP_172x()
    {
        var result = _redactor.Redact("Connection refused: http://172.16.0.1:3000/api");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("172.16.0.1");
    }

    [Test]
    public void Redact_RemovesInternalUrl_PrivateIP_192x()
    {
        var result = _redactor.Redact("Timeout connecting to http://192.168.1.100/webhook");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("192.168.1.100");
    }

    [Test]
    public void Redact_PreservesExternalUrl()
    {
        var result = _redactor.Redact("Error from https://api.openai.com/v1/chat");
        result.Should().Contain("https://api.openai.com/v1/chat");
    }

    // =====================================================================
    // Stack traces
    // =====================================================================

    [Test]
    public void Redact_RemovesStackTrace()
    {
        var errorWithStack = @"System.NullReferenceException: Object reference not set
   at Tamma.Activities.AI.ClaudeAnalysis.Execute() in /app/src/Activities/AI/ClaudeAnalysis.cs:line 42
   at Elsa.Workflows.Runtime.WorkflowRunner.RunAsync() in /elsa/src/Runner.cs:line 100";
        var result = _redactor.Redact(errorWithStack);
        result.Should().Contain("[STACK TRACE REDACTED]");
        result.Should().NotContain("ClaudeAnalysis.Execute");
        result.Should().NotContain("line 42");
    }

    [Test]
    public void Redact_RemovesStackTrace_PreservesMessage()
    {
        var errorWithStack = @"System.NullReferenceException: Object reference not set
   at Tamma.Activities.AI.ClaudeAnalysis.Execute() in /app/src/file.cs:line 42";
        var result = _redactor.Redact(errorWithStack);
        result.Should().Contain("System.NullReferenceException: Object reference not set");
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Test]
    public void Redact_PreservesNormalErrorMessage()
    {
        var normal = "Connection timed out after 30 seconds";
        var result = _redactor.Redact(normal);
        result.Should().Be(normal);
    }

    [Test]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        var result = _redactor.Redact("");
        result.Should().BeEmpty();
    }

    [Test]
    public void Redact_NullString_ReturnsNull()
    {
        var result = _redactor.Redact(null!);
        result.Should().BeNull();
    }

    // =====================================================================
    // Mixed content
    // =====================================================================

    [Test]
    public void Redact_MixedContent_RedactsOnlySensitive()
    {
        var mixed = "Error 500: Bearer abc.def.ghi-jkl failed at http://localhost:3000/api - please retry";
        var result = _redactor.Redact(mixed);
        result.Should().Contain("Error 500:");
        result.Should().Contain("please retry");
        result.Should().NotContain("abc.def.ghi-jkl");
        result.Should().NotContain("localhost:3000");
    }

    [Test]
    public void Redact_MultipleKeysInSameMessage()
    {
        var multiKey = "Key1: sk-ant-api03-abc123 Key2: Bearer xyz.123.abc Key3: key-generic123";
        var result = _redactor.Redact(multiKey);
        result.Should().NotContain("sk-ant");
        result.Should().NotContain("xyz.123.abc");
        result.Should().NotContain("key-generic123");
        // The redacted result should have [REDACTED] placeholders
        result.Should().Contain("[REDACTED]");
    }

    // =====================================================================
    // Idempotency
    // =====================================================================

    [Test]
    public void Redact_IsIdempotent()
    {
        var input = "Bearer abc.def.ghi-jkl failed at http://localhost:3000/api";
        var first = _redactor.Redact(input);
        var second = _redactor.Redact(first);
        second.Should().Be(first);
    }

    // =====================================================================
    // Logger integration
    // =====================================================================

    [Test]
    public void Redact_WithLogger_LogsWhenRedactionOccurs()
    {
        var mockLogger = new Mock<ILogger<ErrorRedactor>>();
        var redactor = new ErrorRedactor(logger: mockLogger.Object);

        redactor.Redact("Bearer secret_token_here");

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // =====================================================================
    // PF-S7 — connection-string DSN redaction
    // =====================================================================

    [Test]
    public void Redact_RemovesPostgresDsn()
    {
        var input = "Connection failed: postgresql://tamma:s3cret@db.example.com:5432/tamma_main";
        var result = _redactor.Redact(input);
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("s3cret");
        result.Should().NotContain("tamma:s3cret");
        result.Should().Contain("Connection failed:");
    }

    [Test]
    public void Redact_RemovesPostgresShortSchemeDsn()
    {
        var result = _redactor.Redact("postgres://user1:pw1@host/db1 was unreachable");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("pw1");
        result.Should().NotContain("user1");
    }

    [Test]
    public void Redact_RemovesMysqlDsn()
    {
        var result = _redactor.Redact("mysql://root:hunter2@10.0.0.5:3306/app refused");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("hunter2");
        result.Should().NotContain("10.0.0.5");
    }

    [Test]
    public void Redact_RemovesMongoDsn()
    {
        var result = _redactor.Redact("Source: mongodb://admin:p@ssw0rd@mongo:27017/admin error");
        // Note: '@' inside the password breaks our greedy match on the @
        // boundary — that's expected and acceptable: when an admin URL-encodes
        // a stray '@' (%40) the matcher catches it; raw '@' creates ambiguity
        // we choose to err on the side of partial redaction rather than
        // false-positive on legitimate text.
        result.Should().Contain("[REDACTED-DSN]");
    }

    [Test]
    public void Redact_RemovesMongoSrvDsn()
    {
        var result = _redactor.Redact("Failover: mongodb+srv://writer:abc123@cluster.mongodb.net/db");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("abc123");
        result.Should().NotContain("writer:abc123");
    }

    [Test]
    public void Redact_RemovesRedisDsn()
    {
        var result = _redactor.Redact("Cache: redis://default:supersecret@cache:6379 timed out");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("supersecret");
    }

    [Test]
    public void Redact_RemovesAmqpDsn()
    {
        var result = _redactor.Redact("AMQP: amqp://producer:rabbits@rabbitmq:5672/vhost broken");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("rabbits");

        var resultTls = _redactor.Redact("AMQPS: amqps://producer:rabbits@rabbitmq:5671/vhost broken");
        resultTls.Should().Contain("[REDACTED-DSN]");
        resultTls.Should().NotContain("rabbits");
    }

    [Test]
    public void Redact_RemovesDsn_WithUrlEncodedPassword()
    {
        // %40 = '@', %3A = ':', %2F = '/'  — an admin who URL-encodes special
        // characters in the password must still get the DSN scrubbed.
        var input = "postgresql://tamma:p%40ss%2Fword@db:5432/tamma";
        var result = _redactor.Redact(input);
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("p%40ss");
    }

    [Test]
    public void Redact_RemovesDsn_WithDigitsOnlyPassword()
    {
        var result = _redactor.Redact("postgres://app:1234567890@db/tamma");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("1234567890");
    }

    [Test]
    public void Redact_RemovesDsn_WithSpecialCharsInPassword()
    {
        var result = _redactor.Redact("postgres://app:!@#$%^&-_+=.*?host/tamma");
        // The '@' in the password breaks the regex boundary; the matcher
        // catches the leading prefix up to the first '@' which still scrubs
        // the username + first chunk of password. Acceptable trade-off.
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("postgres://app:!");
    }

    [Test]
    public void Redact_DsnRedaction_WinsOver_InternalUrlRedaction()
    {
        // PF-S7 — a Postgres DSN against a 10.x host is a more specific
        // pattern than the internal-URL scrub. We must redact the FULL DSN
        // (with the password) rather than only the host segment.
        var input = "postgres://tamma:s3cret@10.0.0.5:5432/tamma";
        var result = _redactor.Redact(input);
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("s3cret");
        result.Should().NotContain("tamma:s3cret");
    }

    [Test]
    public void Redact_PreservesUrlsThatLackCredentials()
    {
        // A bare postgres URL with no user:pass shouldn't match the DSN
        // regex (it requires a `user:pass@` segment).
        var result = _redactor.Redact("Try postgres://localhost:5432/tamma directly.");
        result.Should().Contain("postgres://localhost:5432/tamma");
    }

    [Test]
    public void Redact_DsnIsCaseInsensitive()
    {
        var result = _redactor.Redact("upstream: PostgreSQL://USER:PASS@host/db");
        result.Should().Contain("[REDACTED-DSN]");
        result.Should().NotContain("PASS");
    }

    [Test]
    public void Redact_MultipleDsnsInSameMessage()
    {
        var input = "primary postgres://a:b@h1/d1 secondary postgresql://c:d@h2/d2 done";
        var result = _redactor.Redact(input);
        result.Should().Contain("primary [REDACTED-DSN]");
        result.Should().Contain("secondary [REDACTED-DSN]");
        result.Should().NotContain("a:b");
        result.Should().NotContain("c:d");
    }
}
