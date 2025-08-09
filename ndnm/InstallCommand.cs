using Semver;
using Spectre.Console;
using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ndnm;

internal sealed class InstallCommand(HttpClient hc) : AsyncCommand<InstallCommand.Settings> {
    private static readonly Uri ReleasesIndexJson = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");

    private static readonly JsonWriterOptions IndentedWriterOptions = new() {
        Indented = true
    };

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) {
        var (inputVersion, sdkFiles, runtimeVersion)
            = settings.Version is not null ? await ResolveSdkAsync(settings.Version) : await ResolveFromGlobalJsonAsync();

        var jsonPath = Path.Combine( /*NdnmPath*/ AppContext.BaseDirectory, "instances.json");
        var rid = $"{OSName}-{OSArch}";

        if (File.Exists(jsonPath)) {
            await using FileStream fs = new(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

            if ((await JsonNode.ParseAsync(fs))!.AsObject()[rid]!.AsObject()[inputVersion.ToString()] is not null) {
                throw new InvalidOperationException($".NET SDK {inputVersion} is already installed.");
            }
        }

        var sdkFile = sdkFiles.Select(f => f.RuntimeIdentifier == rid && IsAppropriateFile(f) ? f : (DotnetFile?)null)
            .SingleOrDefault(f => f is not null) ?? throw new InvalidOperationException("Could not find a release matching the specified version.");

        AnsiConsole.Markup("[yellow]Fetching file information...[/]");

        // 파일 크기 정보 가져오기
        long totalSize;

        using (HttpRequestMessage message = new(HttpMethod.Head, sdkFile.Url))
        using (var headResponse = await hc.SendAsync(message)) {
            totalSize = headResponse.Content.Headers.ContentLength ?? 0;
        }

        if (totalSize == 0) {
            throw new InvalidOperationException("Something went wrong.");
        }

        AnsiConsole.MarkupLine(" [green]Done.[/]");
        AnsiConsole.MarkupLine($"[purple]Installing .NET SDK {inputVersion}...[/]");

        return await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
            .StartAsync(async ctx => {
                var downloadTask = ctx.AddTask("[green]Downloading .NET SDK[/]", maxValue: totalSize);
                var hashTask = ctx.AddTask("[blue]Calculating hash[/]", maxValue: totalSize);
                var extractTask = ctx.AddTask("[yellow]Extracting .NET SDK[/]");
                var fileExtension = GetFileExtension(sdkFile.Url);
                var fileName = Path.Combine(AppContext.BaseDirectory, $"dotnet.{fileExtension}");
                var tempDirPath = Path.Combine( /*NdnmPath*/ AppContext.BaseDirectory, "temp");
                DirectoryInfo tempDir = new(tempDirPath);

                File.Delete(fileName);

                try {
                    byte[] calculatedHash;

                    await using (FileStream fs = new(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) {
                        using var sha512 = SHA512.Create();

                        await using (var response = await hc.GetStreamAsync(sdkFile.Url)) {
                            var stopwatch = Stopwatch.StartNew();
                            var buffer = new byte[81920]; // 더 큰 버퍼로 성능 향상
                            int bytesRead;
                            long totalBytesRead = 0;

                            while ((bytesRead = await response.ReadAsync(buffer)) > 0) {
                                // 파일에 쓰기
                                await fs.WriteAsync(buffer.AsMemory(0, bytesRead));

                                // 해시 계산
                                sha512.TransformBlock(buffer, 0, bytesRead, null, 0);

                                totalBytesRead += bytesRead;
                                downloadTask.Value = totalBytesRead;
                                hashTask.Value = totalBytesRead;

                                // 다운로드 속도 표시
                                var elapsed = stopwatch.Elapsed.TotalSeconds;

                                if (elapsed > 0) {
                                    var speed = totalBytesRead / elapsed / 1024 / 1024; // MB/s
                                    downloadTask.Description = $"[green]Downloading .NET SDK[/] ({speed:F1} MB/s)";
                                }
                            }
                        }

                        // 해시 최종 계산
                        sha512.TransformFinalBlock([], 0, 0);

                        calculatedHash = sha512.Hash!;
                    }

                    downloadTask.Value = totalSize;
                    hashTask.Value = totalSize;

                    AnsiConsole.MarkupLine("[green]Download completed successfully![/]");

                    if (settings.ShowHash) {
                        AnsiConsole.WriteLine($"Original hash:   {sdkFile.Sha512Hash.ToUpperInvariant()}");
                        AnsiConsole.WriteLine($"Calculated hash: {ByteArrayToString(calculatedHash).ToUpperInvariant()}");
                    }

                    if (!calculatedHash.AsSpan().SequenceEqual(StringToByteArray(sdkFile.Sha512Hash))) {
                        AnsiConsole.MarkupLine("[red]Hash mismatch![/]");

                        return 1;
                    }

                    AnsiConsole.MarkupLine("[green]Hash verified successfully![/]");

                    if (tempDir.Exists) {
                        tempDir.Delete(true);
                    }

                    tempDir = Directory.CreateDirectory(tempDirPath);

                    await using (FileStream fs = new(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)) {
                        var fileSize = fs.Length;
                        extractTask.MaxValue = fileSize;

                        switch (fileExtension) {
                            case "tar.gz":
                                await using (GZipStream gzStream = new(fs, CompressionMode.Decompress))
                                await using (ReadProgressStream read = new(gzStream, progress => extractTask.Value = Math.Min(progress, fileSize))) {
                                    await TarFile.ExtractToDirectoryAsync(read, tempDir.FullName, true);
                                }

                                break;

                            default:
                                throw new InvalidOperationException("Something went wrong.");
                        }

                        extractTask.Value = fileSize;
                    }

                    const string dotnetCli = "dotnet-cli";

                    if (!File.Exists(jsonPath)) {
                        await using FileStream newJsonFile = new(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
                        await using Utf8JsonWriter writer = new(newJsonFile, IndentedWriterOptions);

                        writer.WriteStartObject();
                        writer.WriteNull(dotnetCli);
                        writer.WriteStartObject(rid);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }

                    bool inputIsGreater;

                    await using (FileStream jsonFile = new(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true)) {
                        var cliVersion = (await JsonNode.ParseAsync(jsonFile))!.AsObject()[dotnetCli];
                        inputIsGreater = cliVersion is null || SemVersion.Parse(cliVersion.GetValue<string>()).ComparePrecedenceTo(inputVersion) < 0;
                    }

                    var instanceDir = Directory.CreateDirectory(Path.Combine( /*NdnmPath*/ AppContext.BaseDirectory, rid));

                    foreach (var file in tempDir.EnumerateFiles("*", SearchOption.AllDirectories)) {
                        var dest = Path.Combine(instanceDir.FullName, Path.GetRelativePath(tempDir.FullName, file.FullName));

                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                        if (!inputIsGreater) {
                            if (!File.Exists(dest)) {
                                file.MoveTo(dest, false);
                            }
                        } else {
                            file.MoveTo(dest, true);
                        }
                    }

                    await using (FileStream jsonFile = new(jsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, true)) {
                        var rootObject = (await JsonNode.ParseAsync(jsonFile))!.AsObject();

                        if (inputIsGreater) {
                            rootObject[dotnetCli] = inputVersion.ToString();
                        }

                        rootObject[rid]!.AsObject()[inputVersion.ToString()] = runtimeVersion.ToString();
                        jsonFile.Position = 0;
                        await using Utf8JsonWriter writer = new(jsonFile, IndentedWriterOptions);

                        rootObject.WriteTo(writer);
                    }

                    AnsiConsole.MarkupLine("[green]Extraction completed successfully![/]");

                    return 0;

                    static string ByteArrayToString(byte[] arrInput) {
                        StringBuilder sOutput = new(arrInput.Length);

                        foreach (var t in arrInput) {
                            sOutput.Append(t.ToString("X2"));
                        }

                        return sOutput.ToString();
                    }

                    static ReadOnlySpan<byte> StringToByteArray(string strInput) {
                        var bytes = new byte[strInput.Length / 2];

                        for (var i = 0; i < strInput.Length; i += 2) {
                            bytes[i / 2] = Convert.ToByte(strInput.Substring(i, 2), 16);
                        }

                        return bytes;
                    }
                } finally {
                    if (tempDir.Exists) {
                        tempDir.Delete(true);
                    }

                    File.Delete(fileName);
                }

                static string GetFileExtension(Uri url) {
                    var extension = Path.GetExtension(url.LocalPath);

                    return extension switch {
                        ".gz" => "tar.gz",
                        _ => extension.TrimStart('.')
                    };
                }
            });

        static bool IsAppropriateFile(DotnetFile file) {
            var fileExtension = Path.GetExtension(file.Url.LocalPath);

            switch (OSName) {
                case "linux" when fileExtension == ".gz":
                case "win" when fileExtension == ".zip":
                    return true;

                default:
                    return false;
            }
        }

        async Task<(SemVersion, DotnetFile[], SemVersion)> ResolveSdkAsync(string versionPattern) {
            AnsiConsole.Markup("[yellow]Fetching release information...[/]");

            var dri = await hc.GetFromJsonAsync(ReleasesIndexJson, NdnmJsonSerializerContext.Default.DotnetReleasesIndex);
            var channels = await dri.Releases.ToAsyncEnumerable().Select(CollectionSelector).ToArrayAsync();
            var releases = channels.SelectMany(c => c.Releases).ToArray();

            // 모든 SDK 수집
            var allSdks = releases
                .Select(r => r.Sdk)
                .Concat(releases.SelectMany(r => r.Sdks ?? []))
                .Distinct()
                .OrderByDescending(s => SemVersion.Parse(s.Version), SemVersionComparer.Default)
                .ToArray();

            var resolvedSdk = ResolveSdkVersionPattern(versionPattern, channels, allSdks)
                ?? throw new InvalidOperationException("Could not find a release matching the specified version.");

            AnsiConsole.MarkupLine(" [green]Done.[/]");

            return (SemVersion.Parse(resolvedSdk.Version), resolvedSdk.Files, SemVersion.Parse(resolvedSdk.RuntimeVersion!));

            async ValueTask<DotnetChannel> CollectionSelector(DotnetChannelReleasesIndex releasesIndex, CancellationToken ct)
                => (await hc.GetFromJsonAsync(releasesIndex.ReleasesJson, NdnmJsonSerializerContext.Default.DotnetChannel, ct))!;

            static DotnetSdk? ResolveSdkVersionPattern(string pattern, DotnetChannel[] channels, DotnetSdk[] availableSdks) {
                // 이미 정확한 버전이면 그대로 반환
                if (SemVersion.TryParse(pattern, out var sv)) {
                    var value = availableSdks.Select(s => SemVersion.Parse(s.Version) == sv ? s : (DotnetSdk?)null).FirstOrDefault();

                    return value ?? availableSdks
                        .Select(s => s.DisplayVersion is not null && SemVersion.Parse(s.DisplayVersion) == sv ? s : (DotnetSdk?)null)
                        .FirstOrDefault();
                }

                // latest 패턴
                if (pattern.Equals("latest", StringComparison.OrdinalIgnoreCase)) {
                    var latestStableChannel = channels.First(c => c.SupportPhase == SupportPhase.Active);

                    return availableSdks.FirstOrDefault(s => latestStableChannel.LatestSdk == s.Version);
                }

                // lts 패턴 (6.0, 8.0이 LTS)
                if (pattern.Equals("lts", StringComparison.OrdinalIgnoreCase)) {
                    var latestLtsChannel = channels.First(c => c is { SupportPhase: SupportPhase.Active, ReleaseType: ReleaseType.Lts });

                    return availableSdks.FirstOrDefault(s => latestLtsChannel.LatestSdk == s.Version);
                }

                // 단일 숫자 (예: "9" -> 9.x.x 최신)
                if (int.TryParse(pattern, out var majorOnly)) {
                    return availableSdks.FirstOrDefault(s => {
                        var v = SemVersion.Parse(s.Version);

                        return v.Major == majorOnly;
                    });
                }

                // 9.0.x 패턴 처리
                if (pattern.EndsWith(".x")) {
                    var prefix = pattern[..^2]; // ".x" 제거
                    var parts = prefix.Split('.');

                    if (parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)) {
                        return availableSdks.FirstOrDefault(s => {
                            var v = SemVersion.Parse(s.Version);

                            return v.Major == major && v.Minor == minor;
                        }); // 이미 내림차순 정렬되어 있음
                    }
                }

                // 9.0.1xx 패턴 처리
                if (pattern.EndsWith("xx")) {
                    var prefix = pattern[..^2]; // "xx" 제거
                    var parts = prefix.Split('.');

                    if (parts.Length == 3 &&
                        int.TryParse(parts[0], out var major) &&
                        int.TryParse(parts[1], out var minor) &&
                        int.TryParse(parts[2], out var patchPrefix)) {
                        // 예: 9.0.1xx -> 100~199 범위에서 찾기
                        var minPatch = patchPrefix * 100;
                        var maxPatch = minPatch + 99;

                        return availableSdks.FirstOrDefault(s => {
                            var v = SemVersion.Parse(s.Version);

                            return v.Major == major && v.Minor == minor && v.Patch >= minPatch && v.Patch <= maxPatch;
                        });
                    }
                }

                return null;
            }
        }

        async Task<(SemVersion, DotnetFile[], SemVersion)> ResolveFromGlobalJsonAsync() {
            var globalJsonPath = FindGlobalJson() ?? throw new InvalidOperationException(
                "No version was specified and global.json was not found. Specify a version explicitly or run from the directory where global.json is located.");

            AnsiConsole.MarkupLine($"[cyan]Found global.json: {globalJsonPath}[/]");

            await using FileStream fs = new(globalJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var globalJson = await JsonNode.ParseAsync(fs);

            var sdkVersion = globalJson?["sdk"]?["version"]?.GetValue<string>()
                ?? throw new InvalidOperationException("SDK version not found in global.json.");

            return await ResolveSdkAsync(sdkVersion);

            static string? FindGlobalJson() {
                DirectoryInfo? currentDir = new(Environment.CurrentDirectory);

                while (currentDir != null) {
                    var globalJsonPath = Path.Combine(currentDir.FullName, "global.json");

                    if (File.Exists(globalJsonPath)) {
                        return globalJsonPath;
                    }

                    currentDir = currentDir.Parent;
                }

                return null;
            }
        }
    }

    public override ValidationResult Validate(CommandContext context, Settings settings) {
        if (SemVersion.TryParse(settings.Version, out var semver) && semver.Patch < 100) {
            return ValidationResult.Error("This program only supports downloading the .NET SDK, not the runtime.");
        }

        if (settings.Version is not null && !Regex.IsMatch(settings.Version, @"^([0-9]+(\.[0-9]+\.([0-9]{3,3}|[0-9]xx|x))?|lts|latest)$")) {
            return ValidationResult.Error("""
                                          Invalid version format. Please use the following format:
                                            - 9.0.100 (exact version)
                                            - 9.0.1xx (latest patch of major.minor.feature band)
                                            - 9.0.x (latest feature band and patch of major.minor)
                                            - 9 (latest version of major version)
                                            - lts (latest long-term support version)
                                            - latest (latest stable version)
                                          """);
        }

        return ValidationResult.Success();
    }

    internal sealed class Settings : CommandSettings {
        [CommandArgument(0, "[version]")]
        [Description("The version to install.")]
        public string? Version { get; init; }

        [CommandOption("--hash", IsHidden = true)]
        [Description("Show the hash of the downloaded file.")]
        public bool ShowHash { get; init; }
    }
}
