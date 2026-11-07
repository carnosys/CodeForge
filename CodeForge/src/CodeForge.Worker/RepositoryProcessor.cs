namespace CodeForge.Worker;


public class RepositoryProcessor
{
    private readonly ProcessRunner _processRunner;

    public RepositoryProcessor(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }


    public async Task ProcessRepoAsync(string repoUrl, CancellationToken stoppingToken)
    {
        string workspace = Path.Combine(
            Path.GetTempPath(), "codeforge",
            Guid.NewGuid().ToString()
        );

        Directory.CreateDirectory(workspace);

        string repoPath = Path.Combine(workspace, "repo");

        try
        {

            var clone_result = await _processRunner.RunAsync(
           "git",
           new[]
           {
             "clone",
               "--depth",
                "1",
                repoUrl,
                repoPath
           },
           workspace,
           TimeSpan.FromSeconds(20),
           stoppingToken
        );

            if (clone_result.TimeOut)
            {
                throw new RepositoryProcessingException(
                    "Repository clone timed out after 20 seconds. The repository may be too large or temporarily unavailable.");
            }

            if (clone_result.ExitCode != 0)
            {
                throw new RepositoryProcessingException(
                    DescribeCloneFailure(clone_result.StandardError));
            }


            string? projectFile = Directory
                .EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (projectFile == null)
            {
                throw new RepositoryProcessingException(
                    "No supported .NET project was found. The repository must contain at least one .csproj file.");

            }

            var build_result = await _processRunner.RunAsync(
                        "dotnet",
                        new[]
                        {
                        "build",
                        projectFile
                        },
                        repoPath,
                        TimeSpan.FromMinutes(5),
                        stoppingToken
                    );


            if (build_result.TimeOut)
            {
                throw new RepositoryProcessingException(
                    "The .NET build timed out after 5 minutes.");
            }

            if (build_result.ExitCode != 0)
            {
                throw new RepositoryProcessingException(
                    $"The .NET build failed with exit code {build_result.ExitCode}. Raw build output is kept out of the public job record because repository builds are untrusted.");
            }

            Console.WriteLine($"Build succeeded: {build_result.StandardOutput}");
        }

        finally
        {

            if (Directory.Exists(workspace))
            {
                Directory.Delete(
                    workspace,
                    recursive: true
                );

            }

        }
    }

    private static string DescribeCloneFailure(string? standardError)
    {
        if (standardError?.Contains("Repository not found", StringComparison.OrdinalIgnoreCase) == true ||
            standardError?.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Repository clone failed because the repository does not exist, is private, or the URL is incorrect.";
        }

        if (standardError?.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Repository clone failed because GitHub could not be reached. Check the worker's DNS and network connection.";
        }

        return "Repository clone failed. Verify that the URL is correct, the repository exists, and it is public.";
    }

}

public sealed class RepositoryProcessingException : Exception
{
    public RepositoryProcessingException(string message) : base(message)
    {
    }
}
