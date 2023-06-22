using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Octokit;
using Serilog;
using VRC.PackageManagement.Core.Types.Packages;
using ProductHeaderValue = Octokit.ProductHeaderValue;
using ListingSource = VRC.PackageManagement.Automation.Multi.ListingSource;

// ReSharper disable once CheckNamespace
namespace VRC.PackageManagement.Automation
{
    [GitHubActions(
        "GHTest",
        GitHubActionsImage.UbuntuLatest,
        On = new[] { GitHubActionsTrigger.WorkflowDispatch, GitHubActionsTrigger.Push },
        EnableGitHubToken = true,
        AutoGenerate = false,
        InvokedTargets = new[] { nameof(BuildRepoListing) })]
    class Build : NukeBuild
    {
        public static int Main() => Execute<Build>(x => x.BuildRepoListing);

        GitHubActions GitHubActions => GitHubActions.Instance;

        const string PackageManifestFilename = "package.json";
        // ReSharper disable once InconsistentNaming
        const string VRCAgent = "VCCBootstrap/1.0";
        const string PackageListingPublishFilename = "index.json";

        [Parameter("Directory to save index into")] 
        AbsolutePath ListPublishDirectory = RootDirectory / "docs";
        
        [Parameter("Filename of source json")]
        string PackageListingSourceFilename = "source.json";
        
        // assumes that "template-package-listings" repo is checked out in sibling dir for local testing, can be overriden
        [Parameter("Path to Target Listing Root")] 
        AbsolutePath PackageListingSourceFolder = IsServerBuild
            ? RootDirectory.Parent
            : RootDirectory.Parent / "package-index";

        [Parameter("Path to existing index.json file, typically https://{owner}.github.io/{repo}/index.json")]
        string CurrentListingUrl =>
            $"https://{GitHubActions.RepositoryOwner}.github.io/{GitHubActions.Repository.Split('/')[1]}/{PackageListingPublishFilename}";
        
        // assumes that "template-package" repo is checked out in sibling dir to this repo, can be overridden
        [Parameter("Path to Target Package")] 
        AbsolutePath LocalTestPackagesPath => RootDirectory.Parent / "template-package"  / "Packages";
        
        AbsolutePath PackageListingSourcePath => PackageListingSourceFolder / PackageListingSourceFilename;

        Target BuildRepoListing => _ => _
            .Executes(async () =>
            {
                if (!PackageListingSourcePath.FileExists())
                {
                    Log.Error("Could not find Listing Source at {PackageListingSourcePath}", PackageListingSourcePath);
                    throw new FileNotFoundException($"Could not find Listing Source at {PackageListingSourcePath}.", PackageListingSourcePath);
                }
                
                // Get listing source
                var listSourceString = File.ReadAllText(PackageListingSourcePath);
                var listSource = JsonConvert.DeserializeObject<ListingSource>(listSourceString, JsonReadOptions);

                if (listSource == null)
                {
                    Log.Error("Fail to get Listing Source");
                    throw new Exception("Fail to get Listing Source.");
                }

                if (string.IsNullOrWhiteSpace(listSource.id))
                {
                    Log.Error(
                        "You need a id for your list. Add a id on {PackageListingSourcePath}",
                        PackageListingSourcePath);
                    
                    throw new ArgumentNullException(nameof(listSource.id),
                        $"You need a id for your list. Add a id on {PackageListingSourcePath}.");
                }
                
                // Get existing RepoList URLs or create empty one, so we can skip existing packages
                var currentPackageUrls = new List<string>();
                var currentRepoListString = IsServerBuild ? await GetAuthenticatedString(CurrentListingUrl) : null;
                
                if (currentRepoListString != null &&
                    JsonConvert.DeserializeObject<VRCRepoList>(currentRepoListString, JsonReadOptions) is
                        { } originRepoList)
                {
                    currentPackageUrls = originRepoList
                        .GetAll()
                        .Select(package => package.Url).ToList();
                }

                // Make collection for constructed packages
                var packages = new List<VRCPackageManifest>();
                var possibleReleaseUrls = new List<string>();
                
                // Add packages from listing source if included
                if (listSource.packages != null)
                {
                    possibleReleaseUrls.AddRange(
                        listSource.packages?.SelectMany(info => info.releases) ?? Array.Empty<string>()
                    );
                }

                // Add GitHub repos if included
                if (listSource.githubRepos is { Count: > 0 })
                {
                    foreach (var ownerSlashName in listSource.githubRepos)
                    {
                        possibleReleaseUrls.AddRange(await GetReleaseZipUrlsFromGitHubRepo(ownerSlashName));
                    }
                }

                // Add each release url to the packages collection if it's not already in the listing, and its zip is valid
                foreach (var url in possibleReleaseUrls)
                {
                    Log.Information("Looking at {Url}", url);
                    if (currentPackageUrls.Contains(url))
                    {
                        Log.Information("Current listing already contains {Url}, skipping", url);
                        continue;
                    }
                    
                    var manifest = await HashZipAndReturnManifest(url);
                    if (manifest == null)
                    {
                        Log.Information("Could not find manifest in zip file {Url}, skipping", url);
                        continue;
                    }
                    
                    // Add package with updated manifest to collection
                    Log.Information("Found {ManifestId} ({ManifestName}) {ManifestVersion}, adding to listing", manifest.Id, manifest.name, manifest.Version);
                    packages.Add(manifest);
                }

                // Copy listing-source.json to new Json Object
                Log.Information("All packages prepared, generating Listing");
                var repoList = new VRCRepoList(packages)
                {
                    name = listSource.name,
                    id = listSource.id,
                    author = listSource.author.name,
                    url = listSource.url
                };

                // Server builds write into the source directory itself
                // So we dont need to clear it out
                if (!IsServerBuild) {
                    FileSystemTasks.EnsureCleanDirectory(ListPublishDirectory);
                }

                string savePath = ListPublishDirectory / PackageListingPublishFilename;
                repoList.Save(savePath);

                var listingInfo = new {
                    Name = listSource.name,
                    Url = listSource.url,
                    Description = listSource.description,
                    InfoLink = new {
                        Text = listSource.infoLink?.text,
                        Url = listSource.infoLink?.url,
                    },
                    Author = new {
                        Name = listSource.author.name,
                        Url = listSource.author.url,
                        Email = listSource.author.email
                    }
                };
                
                Log.Information("Made listingInfo {SerializeObject}", JsonConvert.SerializeObject(listingInfo, JsonWriteOptions));
                Log.Information("Saved Listing to {SavePath}", savePath);
            });

        GitHubClient _client;
        GitHubClient Client
        {
            get
            {
                if (_client != null) return _client;
                
                _client = new GitHubClient(new ProductHeaderValue("VRChat-Package-Manager-Automation"));
                if (IsServerBuild)
                {
                    _client.Credentials = new Credentials(GitHubActions.Token);
                }

                return _client;
            }
        }
        
        async Task<List<string>> GetReleaseZipUrlsFromGitHubRepo(string ownerSlashName)
        {
            // Split string into owner and repo, or skip if invalid.
            var parts = ownerSlashName.Split('/');
            if (parts.Length != 2)
            {
                Log.Fatal("Could not get owner and repository from included repo info {Parts}", parts);
                return null;
            }
            var owner = parts[0];
            var name = parts[1];

            var targetRepo = await Client.Repository.Get(owner, name);
            if (targetRepo == null)
            {
                Assert.Fail($"Could not get remote repo {owner}/{name}.");
                return null;
            }
            
            // Go through each release
            var releases = await Client.Repository.Release.GetAll(owner, name);
            if (releases.Count == 0)
            {
                Log.Information("Found no releases for {Owner}/{Name}", owner, name);
                return null;
            }

            var result = new List<string>();
            
            foreach (var release in releases)
            {
                result.AddRange(release.Assets.Where(asset => asset.Name.EndsWith(".zip")).Select(asset => asset.BrowserDownloadUrl));
            }

            return result;
        }

        // Keeping this for now to ensure existing listings are not broken
        Target BuildMultiPackageListing => _ => _
            .Triggers(BuildRepoListing);

        async Task<VRCPackageManifest> HashZipAndReturnManifest(string url)
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail($"Could not find valid zip file at {url}");
            }

            // Get manifest or return null
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var manifestBytes = GetFileFromZip(bytes, PackageManifestFilename);
            if (manifestBytes == null) return null;
                
            var manifestString = Encoding.UTF8.GetString(manifestBytes);
            var manifest = VRCPackageManifest.FromJson(manifestString);
            var hash = GetHashForBytes(bytes);
            manifest.zipSHA256 = hash; // putting the hash in here for now
            // Point manifest towards release
            manifest.url = url;
            return manifest;
        }
        
        static byte[] GetFileFromZip(byte[] bytes, string fileName)
        {
            using var stream = new MemoryStream(bytes);
            using var zipFile = new ZipFile(stream);
            var zipEntry = zipFile.GetEntry(fileName);

            if (zipEntry == null) return null;
            
            using var zipFileStream = zipFile.GetInputStream(zipEntry);
            
            var ret = new byte[zipEntry.Size];
            // ReSharper disable once MustUseReturnValue
            zipFileStream.Read(ret, 0, ret.Length);

            return ret;
        }

        static string GetHashForBytes(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash
                .ComputeHash(bytes)
                .Select(item => item.ToString("x2")));
        }

        async Task<HttpResponseMessage> GetAuthenticatedResponse(string url)
        {
            using var requestMessage =
                new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.Accept.ParseAdd("application/octet-stream");
            if (IsServerBuild)
            {
                requestMessage.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", GitHubActions.Token);
            }

            return await Http.SendAsync(requestMessage);
        }

        async Task<string> GetAuthenticatedString(string url)
        {
            var result = await GetAuthenticatedResponse(url);
            if (result.IsSuccessStatusCode)
            {
                return await result.Content.ReadAsStringAsync();
            }

            Log.Error("Could not download manifest from {Url}", url);
            return null;
        }

        static HttpClient _http;
        static HttpClient Http
        {
            get
            {
                if (_http != null)
                {
                    return _http;
                }

                _http = new HttpClient();
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(VRCAgent);
                _http.Timeout = TimeSpan.FromMinutes(5);
                return _http;
            }
        }
        
        // https://www.newtonsoft.com/json/help/html/T_Newtonsoft_Json_JsonSerializerSettings.htm
        static JsonSerializerSettings JsonWriteOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
        
        static JsonSerializerSettings JsonReadOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = new List<JsonConverter>()
            {
                new PackageConverter(),
                new VersionListConverter()
            },
        };
    }
}