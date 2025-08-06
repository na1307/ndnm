using Semver;
using Spectre.Console;
using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Ndnm;

internal sealed class InstallCommand(HttpClient hc) : AsyncCommand<InstallCommand.Settings> {
    private static readonly Uri ReleasesIndexJson = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) {
        var inputVersion = SemVersion.Parse(settings.Version);

        if (inputVersion.Patch < 100) {
            throw new NotSupportedException("This program only supports downloading the .NET SDK, not the runtime.");
        }

        AnsiConsole.Markup("[yellow]Fetching release information...[/]");

        var dri = await hc.GetFromJsonAsync(ReleasesIndexJson, NdnmJsonSerializerContext.Default.DotnetReleasesIndex);
        var releases = await dri.Releases.ToAsyncEnumerable().SelectMany(CollectionSelector).ToArrayAsync();
        var release = releases.SingleOrDefault(r => SemVersion.Parse(r.Sdk.Version) == inputVersion);
        DotnetFile[] sdkFiles;

        if (release != default) {
            sdkFiles = release.Sdk.Files;
        } else {
            var sdk = releases.SelectMany(r => r.Sdks ?? []).SingleOrDefault(s => SemVersion.Parse(s.Version) == inputVersion);

            if (sdk == default) {
                throw new InvalidOperationException("Could not find a release matching the specified version.");
            }

            sdkFiles = sdk.Files;
        }

        var rid = $"{Constants.OSName}-{Constants.OSArch}";
        var sdkFile = sdkFiles.SingleOrDefault(f => f.RuntimeIdentifier == rid);

        if (sdkFile == default) {
            throw new InvalidOperationException("Could not find a release matching the specified version.");
        }

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

        return await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
            .StartAsync(async ctx => {
                var downloadTask = ctx.AddTask("[green]Downloading .NET SDK[/]", maxValue: totalSize);
                var hashTask = ctx.AddTask("[blue]Calculating hash[/]", maxValue: totalSize);
                var extractTask = ctx.AddTask("[yellow]Extracting .NET SDK[/]");
                var fileExtension = GetFileExtension(sdkFile.Url);
                var fileName = Path.Combine(AppContext.BaseDirectory, $"dotnet.{fileExtension}");
                var tempDirPath = Path.Combine( /*Constants.NdnmPath*/ AppContext.BaseDirectory, "temp");
                DirectoryInfo tempDir = new(tempDirPath);

                File.Delete(fileName);

                try {
                    await using FileStream fs = new(fileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
                    byte[] calculatedHash;

                    using (var sha512 = SHA512.Create()) {
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
                    fs.Position = 0;
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
                    var instanceDir = Directory.CreateDirectory(Path.Combine( /*Constants.NdnmPath*/ AppContext.BaseDirectory, rid));

                    foreach (var file in tempDir.EnumerateFiles("*", SearchOption.AllDirectories)) {
                        var dest = Path.Combine(instanceDir.FullName, Path.GetRelativePath(tempDir.FullName, file.FullName));

                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        file.MoveTo(dest, true);
                    }

                    AnsiConsole.MarkupLine("[green]Extraction completed successfully![/]");

                    return 0;
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

                static string ByteArrayToString(byte[] arrInput) {
                    int i;
                    StringBuilder sOutput = new(arrInput.Length);

                    for (i = 0; i < arrInput.Length; i++) {
                        sOutput.Append(arrInput[i].ToString("X2"));
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
            });

        async ValueTask<IEnumerable<DotnetRelease>> CollectionSelector(DotnetChannelReleasesIndex releasesIndex, CancellationToken ct) {
            var channel = await hc.GetFromJsonAsync(releasesIndex.ReleasesJson, NdnmJsonSerializerContext.Default.DotnetChannel, ct);

            return channel!.Releases;
        }
    }

    internal sealed class Settings : CommandSettings {
        [CommandArgument(0, "<version>")]
        [Description("The version to install.")]
        public required string Version { get; init; }

        [CommandOption("--hash", IsHidden = true)]
        [Description("Show the hash of the downloaded file.")]
        public bool ShowHash { get; init; }
    }
}
