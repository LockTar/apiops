using Bogus.DataSets;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask<ImmutableArray<CommitId>> WriteGitCommits(IEnumerable<ResourceModels> models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken);
internal delegate ValueTask WriteResourceModels(ResourceModels models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal static class FileSystemModule
{
    public static void ConfigureWriteGitCommits(IHostApplicationBuilder builder)
    {
        ConfigureWriteResourceModels(builder);
        builder.TryAddSingleton(ResolveWriteGitCommits);
    }

    private static WriteGitCommits ResolveWriteGitCommits(IServiceProvider provider)
    {
        var writeModels = provider.GetRequiredService<WriteResourceModels>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("write.git.commits");

            var authorName = "apiops";
            var authorEmail = "apiops@apiops.com";
            var directoryInfo = serviceDirectory.ToDirectoryInfo();

            var modelCount = 0;
            var commits = new List<CommitId>();

            await models.Select((models, index) => (models, index))
                        .Tap(_ => modelCount++)
                        .IterTask(async x =>
                        {
                            var (models, index) = x;

                            deleteNonGitArtifacts(directoryInfo, cancellationToken);
                            await writeModels(models, serviceDirectory, cancellationToken);

                            var commit = index == 0
                                            ? GitModule.InitializeRepository(directoryInfo, commitMessage: "Initial commit", authorName, authorEmail, DateTimeOffset.UtcNow)
                                            : GitModule.CommitChanges(directoryInfo, commitMessage: $"Commit {index}", authorName, authorEmail, DateTimeOffset.UtcNow);

                            var commitId = CommitId.From(commit.Id.Sha)
                                                   .IfErrorThrow();

                            commits.Add(commitId);
                        }, cancellationToken);

            if (modelCount != commits.Count)
            {
                throw new InvalidOperationException($"Expected {modelCount} commits, but got {commits.Count}.");
            }

            return [.. commits];
        };

        static void deleteNonGitArtifacts(DirectoryInfo directory, CancellationToken cancellationToken)
        {
            if (directory.Exists())
            {
                directory.EnumerateDirectories()
                         .Where(subDirectory => subDirectory.Name.Equals(".git") is false)
                         .Iter(subDirectory => subDirectory.DeleteIfExists(), cancellationToken);

                directory.EnumerateFiles()
                         .Where(file => file.Name.Equals(".gitignore") is false)
                         .Iter(file => file.Delete(), cancellationToken);
            }
        }
    }

    public static void ConfigureWriteResourceModels(IHostApplicationBuilder builder)
    {
        ServiceModule.ConfigureShouldSkipResource(builder);
        ResourceModule.ConfigureWriteInformationFile(builder);
        ResourceModule.ConfigureWriteApiSpecificationFile(builder);
        ResourceModule.ConfigureWritePolicyFile(builder);

        builder.TryAddSingleton(ResolveWriteResourceModels);
    }

    private static WriteResourceModels ResolveWriteResourceModels(IServiceProvider provider)
    {
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var writeInformationFile = provider.GetRequiredService<WriteInformationFile>();
        var writeApiSpecificationFile = provider.GetRequiredService<WriteApiSpecificationFile>();
        var writePolicyFile = provider.GetRequiredService<WritePolicyFile>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("write.resource.models");

            var tasks = new ConcurrentDictionary<ModelNode, Lazy<Task>>();

            await models.SelectMany(kvp => kvp.Value)
                        .IterTaskParallel(async node => await writeNode(node, models, tasks, serviceDirectory, cancellationToken),
                                          maxDegreeOfParallelism: Option.None,
                                          cancellationToken);
        };

        async ValueTask writeNode(ModelNode node, ResourceModels resourceModels, ConcurrentDictionary<ModelNode, Lazy<Task>> tasks, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await tasks.GetOrAdd(node, _ => new(async () =>
            {
                var resourceKey = new ResourceKey
                {
                    Name = node.Model.Name,
                    Parents = node.GetResourceParentChain(),
                    Resource = node.Model.AssociatedResource
                };

                if (await shouldSkipResource(resourceKey, cancellationToken))
                {
                    return;
                }

                // Write predecessors
                var predecessors = node.Predecessors;
                await predecessors.IterTaskParallel(async predecessor => await writeNode(predecessor, resourceModels, tasks, serviceDirectory, cancellationToken),
                                                    maxDegreeOfParallelism: Option.None,
                                                    cancellationToken);

                // Write model
                if (node.Model is IDtoTestModel dtoTestModel)
                {
                    var name = resourceKey.Name;
                    var dto = dtoTestModel.SerializeDto(predecessors);
                    var ancestors = resourceKey.Parents;

                    if (resourceKey.Resource is IResourceWithInformationFile resourceWithInformationFile)
                    {
                        await writeInformationFile(resourceWithInformationFile, name, dto, ancestors, cancellationToken);
                    }

                    if (resourceKey.Resource is IPolicyResource policyResource)
                    {
                        await writePolicyFile(policyResource, name, dto, ancestors, cancellationToken);
                    }
                }

                // Write API specification
                if (node.Model is ApiModel apiModel)
                {
                    var option = from specification in apiModel.Type switch
                    {
                        ApiType.Http => Option.Some<ApiSpecification>(new ApiSpecification.OpenApi
                        {
                            Format = OpenApiFormat.Yaml.Instance,
                            Version = OpenApiVersion.V3.Instance,
                        }),
                        ApiType.Soap => ApiSpecification.Wsdl.Instance,
                        ApiType.GraphQl => ApiSpecification.GraphQl.Instance,
                        _ => Option.None
                    }
                                 from contentsString in apiModel.Specification
                                 let contents = BinaryData.FromString(contentsString)
                                 select (specification, contents);

                    await option.IterTask(async tuple => await writeApiSpecificationFile(resourceKey, tuple.specification, tuple.contents, cancellationToken));
                }
            })).Value;
    }
}
