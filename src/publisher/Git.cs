using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.IO;

namespace publisher;

internal delegate Option<FileOperations> GetCurrentCommitFileOperations();
internal delegate Option<FileOperations> GetPreviousCommitFileOperations();
internal delegate Option<CommitId> GetCurrentCommitId();
internal delegate Option<CommitId> GetPreviousCommitId();
internal delegate bool CommitIdWasPassed();
internal delegate Option<ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>> ListServiceDirectoryFilesModifiedByCurrentCommit();

internal static class GitModule
{
    public static void ConfigureGetCurrentCommitFileOperations(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetCurrentCommitFileOperations);
    }

    private static GetCurrentCommitFileOperations ResolveGetCurrentCommitFileOperations(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCurrentCommitId()
                .Map(commitId => new FileOperations
                {
                    ReadFile = async (file, cancellationToken) => await common.GitModule.ReadFile(file, commitId, cancellationToken),
                    GetSubDirectories = directory => common.GitModule.GetSubDirectories(commitId, directory),
                    EnumerateServiceDirectoryFiles = () => common.GitModule.GetCommitFiles(commitId, serviceDirectory.ToDirectoryInfo())
                });
    }

    private static void ConfigureGetCurrentCommitId(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveGetCurrentCommitId);

    private static GetCurrentCommitId ResolveGetCurrentCommitId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILogger>();

        // This will be called frequently and shouldn't change while the code is running,
        // so we use a Lazy to cache the result. We also want to run the logging operation once.
        var lazy = new Lazy<Option<CommitId>>(() =>
        {
            var commitIdOption = configuration.GetValue("COMMIT_ID")
                                              .Map(commitId => CommitId.From(commitId)
                                                                       .IfErrorThrow());

            commitIdOption.Match(commitId => logger.LogInformation("Using commit ID: {CommitId}", commitId),
                                 () => logger.LogInformation("No commit ID provided."));

            return commitIdOption;
        });

        return () => lazy.Value;
    }

    public static void ConfigureGetPreviousCommitFileOperations(IHostApplicationBuilder builder)
    {
        ConfigureGetPreviousCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetPreviousCommitFileOperations);
    }

    private static GetPreviousCommitFileOperations ResolveGetPreviousCommitFileOperations(IServiceProvider provider)
    {
        var getPreviousCommitId = provider.GetRequiredService<GetPreviousCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getPreviousCommitId()
                .Map(commitId => new FileOperations
                {
                    ReadFile = async (file, cancellationToken) => await common.GitModule.ReadFile(file, commitId, cancellationToken),
                    GetSubDirectories = directory => common.GitModule.GetSubDirectories(commitId, directory),
                    EnumerateServiceDirectoryFiles = () => common.GitModule.GetCommitFiles(commitId, serviceDirectory.ToDirectoryInfo())
                });
    }

    private static void ConfigureGetPreviousCommitId(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetPreviousCommitId);
    }

    private static GetPreviousCommitId ResolveGetPreviousCommitId(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCurrentCommitId()
                .Bind(commitId => common.GitModule.GetPreviousCommitId(commitId, serviceDirectory.ToDirectoryInfo()));
    }

    public static void ConfigureCommitIdWasPassed(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);

        builder.TryAddSingleton(ResolveCommitIdWasPassed);
    }

    private static CommitIdWasPassed ResolveCommitIdWasPassed(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();

        return () => getCurrentCommitId().IsSome;
    }

    // public static void ConfigureReadCurrentCommitFile(IHostApplicationBuilder builder)
    // {
    //     ManagementServiceModule.ConfigureServiceDirectory(builder);

    //     builder.TryAddSingleton(ResolveReadCurrentCommitFile);
    // }

    // private static ReadCurrentCommitFile ResolveReadCurrentCommitFile(IServiceProvider provider)
    // {
    //     var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();

    //     return async (file, cancellationToken) =>
    //         await getCommitId()
    //                 .BindTask(commitId => common.GitModule
    //                                             .ReadFile(file, commitId, cancellationToken));
    // }

    // public static void ConfigureReadPreviousCommitFile(IHostApplicationBuilder builder)
    // {
    //     ConfigureGetPreviousCommitId(builder);

    //     builder.TryAddSingleton(ResolveReadPreviousCommitFile);
    // }

    // private static ReadPreviousCommitFile ResolveReadPreviousCommitFile(IServiceProvider provider)
    // {
    //     var getCommitId = provider.GetRequiredService<GetPreviousCommitId>();

    //     return async (file, cancellationToken) =>
    //         await getCommitId()
    //                 .BindTask(commitId => common.GitModule
    //                                             .ReadFile(file, commitId, cancellationToken));
    // }

    // public static void ConfigureListCurrentCommitServiceDirectoryFiles(IHostApplicationBuilder builder)
    // {
    //     ConfigureGetCurrentCommitId(builder);
    //     ManagementServiceModule.ConfigureServiceDirectory(builder);

    //     builder.TryAddSingleton(ResolveListCurrentCommitServiceDirectoryFiles);
    // }

    // private static ListCurrentCommitServiceDirectoryFiles ResolveListCurrentCommitServiceDirectoryFiles(IServiceProvider provider)
    // {
    //     var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();
    //     var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

    //     var repositoryDirectory = serviceDirectory.ToDirectoryInfo();

    //     return () =>
    //         getCommitId()
    //             .Map(commitId => common.GitModule.GetCommitFiles(commitId, repositoryDirectory));
    // }

    public static void ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveListServiceDirectoryFilesModifiedByCurrentCommit);
    }

    private static ListServiceDirectoryFilesModifiedByCurrentCommit ResolveListServiceDirectoryFilesModifiedByCurrentCommit(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCommitId()
                .Map(commitId => common.GitModule.GetFilesModifiedByCommit(commitId, serviceDirectory.ToDirectoryInfo()));
    }

    // public static void ConfigureGetCurrentCommitSubDirectories(IHostApplicationBuilder builder)
    // {
    //     ConfigureGetCurrentCommitId(builder);

    //     builder.TryAddSingleton(ResolveGetCurrentCommitSubDirectories);
    // }

    // private static GetCurrentCommitSubDirectories ResolveGetCurrentCommitSubDirectories(IServiceProvider provider)
    // {
    //     var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();

    //     return directory =>
    //         getCurrentCommitId()
    //             .Bind(commitId => common.GitModule.GetSubDirectories(commitId, directory));
    // }

    // public static void ConfigureGetPreviousCommitSubDirectories(IHostApplicationBuilder builder)
    // {
    //     ConfigureGetPreviousCommitId(builder);

    //     builder.TryAddSingleton(ResolveGetPreviousCommitSubDirectories);
    // }

}
