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
                throw new Exception("Repository clone timed out");
            }

            if (clone_result.ExitCode != 0)
            {
                throw new Exception($"Clone result: {clone_result.StandardError}");
            }


            string? projectFile = Directory
                .EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (projectFile == null)
            {
                throw new Exception("No supported .NET project found");

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
                throw new Exception("Build timed out");
            }

            if (build_result.ExitCode != 0)
            {
                throw new Exception($"Build result: {build_result.StandardError}");
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

}
