using System.Diagnostics;
using System.Text.Json;

namespace GroundKit.Mcp.Tests.Integration;

public sealed class McpStdioIntegrationTests
{
    [Fact(Explicit = true)]
    [Trait("Category", "PublishedPackage")]
    public async Task Should_Initialize_And_List_Tools_From_Published_Package_Via_Dnx()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var dataRoot = Path.Combine(Path.GetTempPath(), $"contextkit-dnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = dataRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "dnx", "ContextKit.Mcp", "--version", "1.0.0-preview-0015", "--yes",
            "--source", "https://api.nuget.org/v3/index.json",
        })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GROUNDKIT_HOME"] = dataRoot;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dnx.");
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"ContextKit.DnxTests\",\"version\":\"1.0\"}}}"
            );
            await process.StandardInput.FlushAsync(timeout.Token);
            using var initialize = await ReadDnxResponseAsync(process, timeout.Token);
            initialize.RootElement.GetProperty("id").GetInt32().ShouldBe(1);
            initialize.RootElement.GetProperty("result").GetProperty("protocolVersion")
                .GetString().ShouldBe("2025-03-26");
            initialize.RootElement.GetProperty("result").GetProperty("capabilities")
                .TryGetProperty("tools", out _).ShouldBeTrue();

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}"
            );
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}"
            );
            await process.StandardInput.FlushAsync(timeout.Token);
            using var tools = await ReadDnxResponseAsync(process, timeout.Token);
            tools.RootElement.GetProperty("id").GetInt32().ShouldBe(2);
            var toolNames = tools.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            toolNames.ShouldContain("resolve-source");
            toolNames.ShouldContain("query-docs");
            toolNames.ShouldContain("library_catalog");
        }
        catch (Exception exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"Published MCP package failed via dnx. stderr: {await standardError}", exception);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static async Task<JsonDocument> ReadDnxResponseAsync(
        Process process, CancellationToken cancellationToken)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new InvalidOperationException("dnx exited without an MCP response.");
        var response = JsonDocument.Parse(line);
        response.RootElement.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        response.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
        return response;
    }

    [Fact]
    public async Task Should_Initialize_And_List_GroundKit_Tools_Over_Stdio()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        var repositoryRoot = FindRepositoryRoot();
        var dataRoot = Path.Combine(Path.GetTempPath(), $"groundkit-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        Process? process = null;

        try
        {
            process = StartServer(repositoryRoot, dataRoot);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"GroundKit.Tests\",\"version\":\"1.0\"}}}"
            );
            await process.StandardInput.FlushAsync(cancellationToken);

            var initialize = await ReadResponseAsync(process, cancellationToken);
            initialize.RootElement.GetProperty("result").GetProperty("serverInfo");

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}"
            );
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}"
            );
            await process.StandardInput.FlushAsync(cancellationToken);

            var tools = await ReadResponseAsync(process, cancellationToken);
            var toolNames = tools
                .RootElement.GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToArray();

            toolNames.ShouldContain("resolve-source");
            toolNames.ShouldContain("query-docs");
            toolNames.ShouldContain("ask-docs");
            toolNames.ShouldContain("get_docs");
            toolNames.ShouldContain("find_libraries");
            toolNames.ShouldContain("search-docs");
            toolNames.ShouldContain("library_catalog");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            process?.Dispose();

            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static Process StartServer(string repositoryRoot, string dataRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/ContextKit.Mcp/ContextKit.Mcp.csproj");
        startInfo.ArgumentList.Add("--configuration");
#if DEBUG
        startInfo.ArgumentList.Add("Debug");
#else
        startInfo.ArgumentList.Add("Release");
#endif
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--");
        startInfo.Environment["GROUNDKIT_HOME"] = dataRoot;

        var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the GroundKit MCP stdio process."
            );
        return process;
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        Process process,
        CancellationToken cancellationToken
    )
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.TrimStart().StartsWith('{'))
            {
                return JsonDocument.Parse(line);
            }
        }

        throw new InvalidOperationException(
            "GroundKit MCP stdio process exited without a response."
        );
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextKit.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the GroundKit repository root.");
    }
}
