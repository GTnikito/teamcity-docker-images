﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IoC;
using TeamCity.Docker.Generic;
using TeamCity.Docker.Model;
// ReSharper disable ClassNeverInstantiated.Global

namespace TeamCity.Docker
{
    internal class ReadmeGenerator : IGenerator
    {
        private static readonly Dependency GenerateDependency = new Dependency(DependencyType.Generate);
        [NotNull] private readonly IGenerateOptions _options;
        [NotNull] private readonly IPathService _pathService;
        [NotNull] private readonly IBuildPathProvider _buildPathProvider;

        public ReadmeGenerator(
            [NotNull] IGenerateOptions options,
            [NotNull] IPathService pathService,
            [NotNull] IBuildPathProvider buildPathProvider)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
            _buildPathProvider = buildPathProvider ?? throw new ArgumentNullException(nameof(buildPathProvider));
        }

        public void Generate([NotNull] IGraph<IArtifact, Dependency> graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            var groups = (
                from node in graph.Nodes
                let image = node.Value as Image
                where image != null
                group node by image.File.ImageId
                into groupsByImageId
                from groupByImageId in
                    from groupByImageId in groupsByImageId
                    let image = groupByImageId.Value as Image
                    where image != null
                    orderby image.File
                    group groupByImageId by image.File
                group groupByImageId by groupsByImageId.Key)
                .ToList();

            foreach (var groupByImageId in groups)
            {
                var imageId = groupByImageId.Key;
                var lines = new List<string>();
                graph.TryAddNode(new FileArtifact(GetReadmeFile(imageId), lines), out var readmeNode);
                var groupByImage = groupByImageId.ToList();

                lines.Add($"## {imageId} tags");
                lines.Add(string.Empty);

                var otherImages = groups.Where(i => i.Key != imageId).ToList();
                if (otherImages.Any())
                {
                    lines.Add("Other tags");
                    lines.Add(string.Empty);
                    foreach (var image in otherImages)
                    {
                        lines.Add($"- [{image.Key}]({GetReadmeFilePath(image.Key)})");
                    }
                }

                lines.Add(string.Empty);

                // ReSharper disable once IdentifierTypo
                var mutliArch = (
                    from grp in (
                        from image in groupByImage
                        where image.Key.Repositories.Any()
                        from tag in image.Key.Tags.Skip(1)
                        orderby tag descending
                        group image by tag)
                    orderby $"{grp.Count()} {grp.Key}" descending
                    select grp)
                    .ToList();

                if (mutliArch.Any())
                {
                    lines.Add("#### multi-architecture");
                    lines.Add(string.Empty);
                    lines.Add("When running an image with multi-architecture support, docker will automatically select an image variant which matches your OS and architecture.");
                    lines.Add(string.Empty);
                    foreach (var dockerfileByMultiArchTag in mutliArch)
                    {
                        lines.Add($"- [{dockerfileByMultiArchTag.Key}](#{Normalize(dockerfileByMultiArchTag.Key)})");
                    }

                    lines.Add(string.Empty);
                }

                var dockerfileGroups =
                    from image in groupByImage
                    orderby image.Key.Platform
                    group image by image.Key.Platform
                    into grp1
                    from grp2 in (
                        from image in grp1
                        orderby image.Key.Description descending
                        group image by image.Key.Description)
                    group grp2 by grp1.Key;

                foreach (var dockerfileByPlatform in dockerfileGroups)
                {
                    lines.Add($"#### {dockerfileByPlatform.Key}");
                    lines.Add(string.Empty);

                    foreach (var dockerfileByDescription in dockerfileByPlatform)
                    {
                        lines.Add($"- {dockerfileByDescription.Key}");
                        foreach (var dockerfile in dockerfileByDescription)
                        {
                            lines.Add($"  - [{GetReadmeTagName(dockerfile.Key)}](#{GetTagLink(dockerfile.Key)})");
                        }
                    }

                    lines.Add(string.Empty);
                }

                lines.Add(string.Empty);

                foreach (var dockerfileByMultiArchTag in mutliArch)
                {
                    lines.Add($"### {dockerfileByMultiArchTag.Key}");
                    lines.Add(string.Empty);
                    var platforms = string.Join(", ", dockerfileByMultiArchTag.Select(i => $"{i.Key.Platform} {i.Key.Description}").Distinct().OrderBy(i => i));
                    lines.Add($"Supported platforms: {platforms}");
                    lines.Add(string.Empty);
                    lines.Add("#### Content");
                    lines.Add(string.Empty);
                    foreach (var dockerfile in dockerfileByMultiArchTag)
                    {
                        lines.Add($"- [{GetReadmeTagName(dockerfile.Key)}](#{GetTagLink(dockerfile.Key)})");
                    }

                    lines.Add(string.Empty);
                }

                lines.Add(string.Empty);

                foreach (var groupByFile in groupByImage)
                {
                    var dockerFile = groupByFile.Key;
                    lines.Add($"### {GetReadmeTagName(dockerFile)}");

                    lines.Add(string.Empty);
                    lines.Add($"[Dockerfile]({_pathService.Normalize(Path.Combine(dockerFile.Path, "Dockerfile"))})");

                    if (dockerFile.Comments.Any())
                    {
                        lines.Add(string.Empty);
                        foreach (var comment in dockerFile.Comments)
                        {
                            lines.Add(comment);
                        }
                    }

                    if (dockerFile.Repositories.Any(i => !string.IsNullOrWhiteSpace(i)))
                    {
                        lines.Add(string.Empty);
                        lines.Add("The docker image is available on:");
                        lines.Add(string.Empty);
                        foreach (var repo in dockerFile.Repositories.Where(i => !string.IsNullOrWhiteSpace(i)))
                        {
                            lines.Add($"- [{repo}{dockerFile.ImageId}]({repo}{dockerFile.ImageId})");
                        }
                    }
                    else
                    {
                        lines.Add("The docker image is not available and may be created manually.");
                    }

                    if (dockerFile.Components.Any())
                    {
                        lines.Add(string.Empty);
                        lines.Add("Installed components:");
                        lines.Add(string.Empty);
                        foreach (var component in dockerFile.Components)
                        {
                            lines.Add($"- {component}");
                        }
                    }

                    lines.Add(string.Empty);
                    lines.Add($"Container platform: {dockerFile.Platform}");

                    var publishRepo = dockerFile
                        .Repositories
                        .Select(i =>
                        {
                            try
                            {
                                return new Uri(i);
                            }
                            catch
                            {
                                return null;
                            }
                        })
                        .FirstOrDefault(i => i != null);
                    if (publishRepo != null)
                    {
                        lines.Add(string.Empty);
                        lines.Add("Docker pull command:");
                        lines.Add(string.Empty);

                        lines.Add("```");
                        lines.Add(GeneratePullCommand($"{publishRepo.Segments.Last()}/{dockerFile.ImageId}:{dockerFile.Tags.FirstOrDefault() ?? "latest"}"));
                        lines.Add("```");
                    }

                    foreach (var node in groupByFile)
                    {
                        var artifacts = _buildPathProvider.GetPath(graph, node).Select(i => i.Value).ToList();
                        var images = artifacts.OfType<Image>().ToList();
                        var weight = 0;

                        if (images.Any())
                        {
                            lines.Add(string.Empty);
                            lines.Add("Docker build commands:");
                            lines.Add(string.Empty);

                            lines.Add("```");
                            foreach (var image in images)
                            {
                                lines.Add(GenerateBuildCommand(image));
                                weight += image.Weight.Value;
                            }

                            lines.Add("```");
                        }

                        var references = artifacts.OfType<Reference>();
                        if (references.Any())
                        {
                            lines.Add(string.Empty);
                            lines.Add("Base images:");
                            lines.Add(string.Empty);

                            lines.Add("```");
                            foreach (var reference in artifacts.OfType<Reference>())
                            {
                                lines.Add(GeneratePullCommand(reference.RepoTag));
                                weight += reference.Weight.Value;
                            }

                            lines.Add("```");
                        }

                        if (weight > 0)
                        {
                            lines.Add(string.Empty);
                            lines.Add($"_The required free space to generate image(s) is about **{weight} GB**._");
                        }
                    }

                    foreach (var node in groupByFile)
                    {
                        graph.TryAddLink(node, GenerateDependency, readmeNode, out _);
                    }
                }
            }
        }

        private string GetReadmeFile(string imageId)
        {
            return _pathService.Normalize(Path.Combine(_options.TargetPath, GetReadmeFilePath(imageId)));
        }

        private static string GeneratePullCommand(string repoTag) => 
            $"docker pull {repoTag}";

        private string GenerateBuildCommand(Image image)
        {
            var dockerFilePath = _pathService.Normalize(Path.Combine(_options.TargetPath, image.File.Path, "Dockerfile"));
            return $"docker build -f \"{dockerFilePath}\" -t {image.File.ImageId}:{image.File.Tags.First()} \"{_options.ContextPath}\"";
        }

        private static string GetReadmeFilePath(string imageId) =>
            imageId + ".md";

        private string GetTagLink(Dockerfile dockerFile) =>
            Normalize(GetReadmeTagName(dockerFile));

        private string GetReadmeTagName(Dockerfile dockerFile) =>
            dockerFile.Tags.First();

        private static string Normalize(string text) =>
            text
                .Replace(",", string.Empty)
                .Replace(".", string.Empty)
                .Replace(" ", "-");
    }
}
